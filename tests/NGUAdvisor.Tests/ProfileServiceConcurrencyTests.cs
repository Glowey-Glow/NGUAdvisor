using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NGUAdvisor.Managers;   // ProfileModel, for building the large fixture directly
using NGUAdvisorCompanion;   // the companion's real ProfileService (linked source)
using SimpleJSON;
using Xunit;
using Xunit.Abstractions;

namespace NGUAdvisor.Tests
{
    // Counter-audit finding C10: "concurrent local profile writes race and silently lose one".
    //
    // MainForm.TryHandleLocal dispatches EVERY profile mutation into its own bare Task.Run
    // (MainForm.cs:218/231/244/257/300). Each one independently does
    //     File.ReadAllText -> ProfileModel.Load -> mutate -> validate -> File.WriteAllText -> re-read
    // so two commands in flight against the same profile file interleave with nothing to stop them.
    //
    // These tests drive the REAL ProfileService against a real temp profile - no game, no injector, no UI -
    // and assert the only invariant that belongs at this layer: every mutation that reports success must be
    // on disk. They deliberately do NOT assert WHICH row a delete removes; that is a different finding
    // (C3, stale index), and conflating the two hides both.
    //
    // Seed timeline is five energy breakpoints at 0/60/120/180/240s, so the surviving times identify
    // exactly which operation won:
    //     delete idx1 only        -> [0,120,180,240]
    //     delete idx3 only        -> [0,60,120,240]
    //     both, in either order   -> 3 rows
    //
    // Measured on the unfixed ProfileService (before the _fileGate lock), this machine:
    //     two simultaneous deletes  x40  -> correct=0,  one delete lost every time (loser raised a Windows
    //                                       sharing violation, so that variant is loud, not silent)
    //     delete vs setChallenges   x40  -> correct=0,  one command lost every time, same mechanism
    //     staggered delete          x6   -> correct=1,  4 SILENT losses (both calls returned a fresh
    //                                       timelines message; the row was simply not on disk) + 1 violation
    // After the fix all three are correct on every iteration.
    public class ProfileServiceConcurrencyTests : IDisposable
    {
        private const string Name = "RaceProfile";
        private static readonly int[] SeedTimes = { 0, 60, 120, 180, 240 };

        // Minimal valid profile; the five energy breakpoints are added through ProfileService itself so the
        // on-disk shape is byte-for-byte what the companion actually writes.
        private const string Empty = @"{
  ""Breakpoints"": {
    ""Energy"":  [ ],
    ""Rebirth"": [ { ""Type"": ""Time"", ""Time"": { ""h"": 24 } } ]
  }
}";

        private readonly ITestOutputHelper _out;
        private readonly string _dir;
        private readonly string _path;
        private readonly string _seed;

        public ProfileServiceConcurrencyTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "NGUAdvisorRace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, Name + ".json");

            File.WriteAllText(_path, Empty);
            foreach (var t in SeedTimes)
                ProfileService.AddBreakpoint(_dir, Name, "energy", t, "NGU", "", null);
            _seed = File.ReadAllText(_path);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Reset() => File.WriteAllText(_path, _seed);

        /// <summary>The energy breakpoint times surviving on disk, or null if the file is unreadable.</summary>
        private static List<int> EnergyTimes(string dir, string name)
        {
            try
            {
                var rows = JSON.Parse(ProfileService.BuildTimelinesJson(dir, name))["systems"]["energy"].AsArray;
                var list = new List<int>();
                for (int i = 0; i < rows.Count; i++) list.Add(rows[i]["sec"].AsInt);
                return list;
            }
            catch { return null; }
        }

        private List<int> EnergyTimes() => EnergyTimes(_dir, Name);

        private int ChallengeCount()
        {
            try { return JSON.Parse(ProfileService.BuildTimelinesJson(_dir, Name))["challenges"].AsArray.Count; }
            catch { return -1; }
        }

        private static string Show(List<int> t) => t == null ? "<unreadable>" : "[" + string.Join(",", t) + "]";

        private static string Short(Exception ex) =>
            ex == null ? "-" : ex.GetType().Name + ": " + ex.Message.Replace("\r", " ").Replace("\n", " ");

        /// <summary>
        /// Run two operations as concurrently as the scheduler allows: both threads park on a gate, the gate
        /// opens, both proceed. results[0]/[1] are whatever each threw (null = the call reported success).
        /// </summary>
        private static Exception[] Race(Action a, Action b)
        {
            var results = new Exception[2];
            using (var gate = new ManualResetEventSlim(false))
            using (var ready = new CountdownEvent(2))
            {
                var ta = Task.Run(() => { ready.Signal(); gate.Wait(); try { a(); } catch (Exception ex) { results[0] = ex; } });
                var tb = Task.Run(() => { ready.Signal(); gate.Wait(); try { b(); } catch (Exception ex) { results[1] = ex; } });
                ready.Wait();          // both threads are parked on the gate before it opens
                gate.Set();
                Task.WaitAll(ta, tb);
            }
            return results;
        }

        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void Two_concurrent_deletes_both_survive()
        {
            const int iterations = 40;
            int ok = 0, silentLoss = 0, lossWithError = 0, otherFailure = 0;
            var log = new List<string>();

            for (int i = 0; i < iterations; i++)
            {
                Reset();
                var errs = Race(
                    () => ProfileService.DeleteBreakpoint(_dir, Name, "energy", 1),
                    () => ProfileService.DeleteBreakpoint(_dir, Name, "energy", 3));
                var times = EnergyTimes();
                int n = times == null ? -1 : times.Count;

                if (errs[0] == null && errs[1] == null && n == SeedTimes.Length - 2) { ok++; continue; }

                // Which delete is missing from disk? 4 rows means exactly one landed.
                string missing = null;
                if (n == SeedTimes.Length - 1)
                    missing = times.Contains(60) ? "A(idx1)" : "B(idx3)";
                bool missingOneReportedSuccess =
                    (missing == "A(idx1)" && errs[0] == null) || (missing == "B(idx3)" && errs[1] == null);

                string line = "  iter " + i + ": disk=" + Show(times) +
                              "  A=" + Short(errs[0]) + "  B=" + Short(errs[1]);
                if (missing != null && missingOneReportedSuccess) { silentLoss++; log.Add("  SILENT LOSS of " + missing + line.Substring(2)); }
                else if (missing != null) { lossWithError++; log.Add("  LOSS+ERROR  of " + missing + line.Substring(2)); }
                else { otherFailure++; log.Add("  OTHER      " + line); }
            }

            var summary = "two concurrent DeleteBreakpoint calls x" + iterations +
                          ": correct=" + ok + "  silently-lost=" + silentLoss +
                          "  lost-with-an-error-reply=" + lossWithError + "  other=" + otherFailure;
            _out.WriteLine(summary);
            foreach (var l in log) _out.WriteLine(l);

            Assert.True(ok == iterations, "C10: " + summary + "\n" + string.Join("\n", log));
        }

        [Fact]
        public void Delete_racing_SetChallenges_both_survive()
        {
            const int iterations = 40;
            int ok = 0, silentLoss = 0, lossWithError = 0, otherFailure = 0;
            var log = new List<string>();

            for (int i = 0; i < iterations; i++)
            {
                Reset();
                var errs = Race(
                    () => ProfileService.DeleteBreakpoint(_dir, Name, "energy", 2),
                    () => ProfileService.SetChallenges(_dir, Name, new[] { "24HR-3" }));
                var times = EnergyTimes();
                int n = times == null ? -1 : times.Count;
                int chal = ChallengeCount();

                bool deleteLanded = n == SeedTimes.Length - 1;
                bool challengesLanded = chal == 1;
                if (errs[0] == null && errs[1] == null && deleteLanded && challengesLanded) { ok++; continue; }

                string missing = !deleteLanded ? "delete" : !challengesLanded ? "setChallenges" : null;
                bool missingOneReportedSuccess =
                    (missing == "delete" && errs[0] == null) || (missing == "setChallenges" && errs[1] == null);

                string line = " disk=" + Show(times) + " challenges=" + chal +
                              "  delete=" + Short(errs[0]) + "  setChallenges=" + Short(errs[1]);
                if (missing != null && missingOneReportedSuccess) { silentLoss++; log.Add("  SILENT LOSS of " + missing + line); }
                else if (missing != null) { lossWithError++; log.Add("  LOSS+ERROR  of " + missing + line); }
                else { otherFailure++; log.Add("  OTHER     " + line); }
            }

            var summary = "DeleteBreakpoint racing SetChallenges x" + iterations +
                          ": correct=" + ok + "  silently-lost=" + silentLoss +
                          "  lost-with-an-error-reply=" + lossWithError + "  other=" + otherFailure;
            _out.WriteLine(summary);
            foreach (var l in log) _out.WriteLine(l);

            Assert.True(ok == iterations, "C10: " + summary + "\n" + string.Join("\n", log));
        }

        /// <summary>
        /// The deterministic variant. Free-running threads only overlap by luck; here the second delete is
        /// started a calibrated gap after the first - long enough that the first has certainly finished its
        /// READ, short enough that it has certainly not yet WRITTEN. That is the exact interleave the
        /// counter-audit describes, and it is a pure SILENT loss: both calls return a fresh timelines
        /// message, no exception is raised, and one of the two deletes is simply not on disk.
        ///
        ///   A: load [t0..tN] -> remove idx 1 -> write (A's result)
        ///   B: load [t0..tN] (stale, read before A wrote) -> remove idx 3 -> write (clobbers A)
        ///
        /// The window is widened by using a large profile (the read-modify-write is then tens of ms rather
        /// than tens of us) and the gap is calibrated from a measured single-threaded run, so the test does
        /// not depend on this machine's speed.
        /// </summary>
        [Fact]
        public void Staggered_delete_does_not_clobber_the_earlier_write()
        {
            const int rows = 4000;
            const int iterations = 6;

            var dir = Path.Combine(Path.GetTempPath(), "NGUAdvisorRaceBig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, Name + ".json");
                var model = ProfileModel.Load(Empty);
                for (int i = 0; i < rows; i++) model.AddBreakpoint("energy", i * 10);
                File.WriteAllText(path, model.ToJson());
                var seed = File.ReadAllText(path);

                // Calibrate: how long does one full load-mutate-validate-save-reload take on this box?
                var sw = Stopwatch.StartNew();
                ProfileService.DeleteBreakpoint(dir, Name, "energy", 1);
                sw.Stop();
                int opMs = (int)sw.ElapsedMilliseconds;
                int gapMs = Math.Max(2, Math.Min(150, opMs / 3));
                _out.WriteLine("fixture: " + rows + " breakpoints, " + new FileInfo(path).Length / 1024 +
                               " KB; one op = " + opMs + " ms; stagger gap = " + gapMs + " ms");

                int ok = 0, silentLoss = 0, other = 0;
                var log = new List<string>();

                for (int i = 0; i < iterations; i++)
                {
                    File.WriteAllText(path, seed);
                    Exception ea = null, eb = null;
                    var ta = Task.Run(() => { try { ProfileService.DeleteBreakpoint(dir, Name, "energy", 1); } catch (Exception ex) { ea = ex; } });
                    Thread.Sleep(gapMs);   // A has read; A has not written
                    var tb = Task.Run(() => { try { ProfileService.DeleteBreakpoint(dir, Name, "energy", 3); } catch (Exception ex) { eb = ex; } });
                    Task.WaitAll(ta, tb);

                    var times = EnergyTimes(dir, Name);
                    int n = times == null ? -1 : times.Count;
                    if (ea == null && eb == null && n == rows - 2) { ok++; continue; }

                    string line = "  iter " + i + ": " + n + " of " + rows + " rows survive (expected " +
                                  (rows - 2) + ")  A=" + Short(ea) + "  B=" + Short(eb);
                    if (n == rows - 1 && ea == null && eb == null) { silentLoss++; log.Add("  SILENT LOSS" + line.Substring(2)); }
                    else { other++; log.Add(line); }
                }

                var summary = "staggered delete x" + iterations + ": correct=" + ok +
                              "  silently-lost=" + silentLoss + "  other=" + other;
                _out.WriteLine(summary);
                foreach (var l in log) _out.WriteLine(l);

                Assert.True(ok == iterations, "C10 (deterministic): " + summary + "\n" + string.Join("\n", log));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
