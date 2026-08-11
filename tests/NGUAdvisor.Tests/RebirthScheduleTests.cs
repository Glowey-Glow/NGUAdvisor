using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NGUAdvisor.Managers;
using SimpleJSON;
using Xunit;

namespace NGUAdvisor.Tests
{
    // "IS A REBIRTH COMING?" — the gate BloodPlanner uses to decide whether a NUMBER multi has anything
    // to be cashed into.
    //
    // WHAT WAS BROKEN. The gate was `Main.Profile.NextRebirthTargetSeconds() > 0`. That function returns
    // a SECONDS figure and only has one to return when the rebirth target is expressed in seconds. A
    // NUMBER or BOSSES rebirth entry is normally written with no "Time" key at all — its trigger is the
    // attack multi — and BreakpointWrapper parses a missing "Time" to 0, which the `> 0` test discards.
    // Eleven shipped profiles are shaped exactly that way, and on every one of them blood pooled uncast
    // behind "blood idle — no rebirth scheduled to bank NUMBER for" while the profile was in fact
    // scheduled to rebirth the moment rebirth power reached its target. [OPERATOR] hit it live in
    // CBlock3.2-E on 2026-08-08, on a profile whose rebirth trigger IS rebirth power at 1000x.
    //
    // WHY THE NEGATIVE HALF IS THE LOAD-BEARING HALF. "The gate is stuck off" has a trivial wrong fix —
    // make it true — and that fix passes every positive assertion anyone would think to write. So the
    // three cases where blood must STILL idle are pinned first and pinned against the real corpus:
    // NORB, Auto Rebirth off, and a profile that genuinely disarmed rebirth (RebirthTime -1, which is
    // seventeen shipped profiles — every LRB/HackDay/RADDay one, plus PostEND).
    public class RebirthScheduleTests
    {
        // ---------------------------------------------------------------- the per-entry arm rule

        [Fact]
        public void Zero_is_ARMED_because_it_means_no_time_floor_not_no_rebirth()
        {
            // THE WHOLE DEFECT IN ONE ASSERTION. A Number/Bosses entry with no "Time" key parses to 0.
            Assert.True(RebirthSchedule.EntryArmed(0.0));
            // ...and the old gate's test says the opposite, which is why it must never come back.
            Assert.False(0.0 > 0);
        }

        [Fact]
        public void Minus_one_is_the_disarm_sentinel()
        {
            Assert.False(RebirthSchedule.EntryArmed(-1.0));
            Assert.False(RebirthSchedule.EntryArmed(-0.5));
        }

        [Fact]
        public void Positive_time_targets_are_armed()
        {
            Assert.True(RebirthSchedule.EntryArmed(86400));   // a 24h Time entry
            Assert.True(RebirthSchedule.EntryArmed(1920));    // a 32m Time backstop
            Assert.True(RebirthSchedule.EntryArmed(double.Epsilon));
        }

        // ---------------------------------------------------------------- NEGATIVE CONTROLS

        [Fact]
        public void NEGATIVE_CONTROL_NORB_still_idles_even_with_everything_else_on()
        {
            // Everything that could arm a rebirth is on. NORB must still win: the run cannot rebirth,
            // so a banked multi is never cashed and the auto-spells stay off.
            var o = RebirthSchedule.Current(autoRebirth: true, inNoRebirthChallenge: true,
                                            profileHasArmedEntry: true, moneyPitRunMode: true);
            Assert.Equal(RebirthSchedule.Outlook.NoRebirthChallenge, o);
            Assert.False(RebirthSchedule.IsComing(true, true, true, true));
        }

        [Fact]
        public void NEGATIVE_CONTROL_no_rebirth_configured_still_idles()
        {
            // A disarmed profile (every entry RebirthTime -1) with money-pit mode off. This is the LRB
            // shape, and it must keep blood magic off the marathon's magic cap.
            var o = RebirthSchedule.Current(autoRebirth: true, inNoRebirthChallenge: false,
                                            profileHasArmedEntry: false, moneyPitRunMode: false);
            Assert.Equal(RebirthSchedule.Outlook.NothingScheduled, o);
            Assert.False(RebirthSchedule.IsComing(true, false, false, false));
        }

        [Fact]
        public void NEGATIVE_CONTROL_auto_rebirth_off_still_idles_on_a_fully_armed_profile()
        {
            // Main.cs:1229 will not call the profile's DoRebirth without AutoRebirth, and the money-pit
            // fallback routes through BaseRebirth.RebirthAvailable, which refuses on the same flag. So
            // an armed profile with the master switch off is not going to rebirth.
            var o = RebirthSchedule.Current(autoRebirth: false, inNoRebirthChallenge: false,
                                            profileHasArmedEntry: true, moneyPitRunMode: true);
            Assert.Equal(RebirthSchedule.Outlook.AutoRebirthOff, o);
            Assert.False(RebirthSchedule.IsComing(false, false, true, true));
        }

        [Fact]
        public void The_gate_was_not_simply_turned_on()
        {
            // Of the sixteen combinations, exactly the four with AutoRebirth on, NORB off and at least
            // one of (armed profile, money-pit mode) may come back Coming. Enumerated rather than
            // spot-checked so a later "simplification" to `return true` cannot survive.
            var coming = new List<string>();
            foreach (var ar in new[] { false, true })
                foreach (var norb in new[] { false, true })
                    foreach (var armed in new[] { false, true })
                        foreach (var mp in new[] { false, true })
                            if (RebirthSchedule.IsComing(ar, norb, armed, mp))
                                coming.Add($"auto={ar} norb={norb} armed={armed} pit={mp}");

            // Three: (armed, pit) may be (F,T), (T,F) or (T,T) — never (F,F).
            Assert.Equal(3, coming.Count);
            Assert.All(coming, s => Assert.Contains("auto=True", s));
            Assert.All(coming, s => Assert.Contains("norb=False", s));
            Assert.DoesNotContain("auto=True norb=False armed=False pit=False", coming);
        }

        [Fact]
        public void Money_pit_run_mode_arms_the_fifth_trigger_type_which_is_not_in_the_profile()
        {
            // MoneyPitRunRebirth is a static class with no entry in the rebirth array and no
            // RebirthTime; Main.cs:1231 invokes it as the fallback when the profile declines. A
            // money-pit run on a disarmed profile IS going to rebirth.
            Assert.True(RebirthSchedule.IsComing(autoRebirth: true, inNoRebirthChallenge: false,
                                                 profileHasArmedEntry: false, moneyPitRunMode: true));
        }

        // ---------------------------------------------------------------- the shipped corpus

        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "SampleProfiles")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static IEnumerable<string> CorpusFiles()
        {
            var root = RepoRoot();
            foreach (var sub in new[] { "SampleProfiles", "Presets" })
            {
                var dir = Path.Combine(root, "NGUAdvisor", sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                    yield return f;
            }
        }

        // A FAITHFUL RE-HOST of the rebirth half of BreakpointWrapper's ctor
        // (CustomAllocation.cs:455-491) plus CustomAllocation.ParseTime and the two RebirthTime
        // initialisers in RebirthStuff\. Copied for the same reason BreakpointParseMirror is: the real
        // parser constructs TimeRebirth subclasses whose `_character` reads Main.Character, and
        // MuffinRebirth's ctor builds a ConsumablesManager.Muffin. Neither links here.
        //
        // Pinned below by Mirror_matches_the_parser_source, which reads the three source files and
        // requires the lines this depends on to still be there.
        private static List<double> RebirthTimes(JSONNode parsed)
        {
            var bps = parsed["Breakpoints"] != null ? parsed["Breakpoints"] : parsed;
            var rb = bps["Rebirth"];
            var rbtime = bps["RebirthTime"];
            var times = new List<double>();

            if (rb == null)
            {
                // Legacy single-key form: CreateRebirth(ParseTime(rbtime), 0.0, "time").
                if (rbtime != null) times.Add(ParseTime(rbtime));
                return times;
            }

            foreach (var bp in rb.Children)
            {
                if (bp["Type"] == null) continue;
                var type = bp["Type"].Value.ToUpper();
                if (type != "TIME" && bp["Target"] == null) continue;

                // ⚠ THE LINE THE WHOLE FIX RESTS ON: the default is 0, not -1 and not "absent".
                var time = 0.0;
                if (bp["Time"] != null) time = ParseTime(bp["Time"]);

                if (type == "TIME") times.Add(time);
                else if (type.Contains("MUFFIN")) times.Add(60 * 60 * 24);   // MuffinRebirth ctor
                else if (type == "NUMBER" || type == "BOSSES") times.Add(time);
                // anything else: CreateRebirth returns null and the entry is dropped.
            }
            return times;
        }

        private static double ParseTime(JSONNode node)
        {
            var time = 0;
            if (node.IsObject)
            {
                foreach (var n in node)
                {
                    if (!n.Value.IsNumber) continue;
                    switch (n.Key.ToLower())
                    {
                        case "h": time += 60 * 60 * n.Value.AsInt; break;
                        case "m": time += 60 * n.Value.AsInt; break;
                        default: time += n.Value.AsInt; break;
                    }
                }
            }
            if (node.IsNumber) time = node.AsInt;
            return time;
        }

        private static bool ProfileArmed(string path)
        {
            var parsed = JSON.Parse(File.ReadAllText(path));
            return RebirthTimes(parsed).Any(RebirthSchedule.EntryArmed);
        }

        [Fact]
        public void The_operators_live_profile_is_ARMED_and_the_old_gate_said_it_was_not()
        {
            // CBlock3.2-E rebirths when rebirth power reaches 1000x — the very quantity the NUMBER
            // blood spell banks. Its Number entry carries no "Time" key, so it parses to RebirthTime 0,
            // and that zero is what the old gate read as "no rebirth scheduled".
            //
            // ⚠ TIMES ARE NOW { 0, 1920 }, NOT { 0 } — a 32-minute Time backstop was added
            // 2026-08-08 after [OPERATOR] found a Troll run stalled for 4h28m (see
            // The_number_target_alone_cannot_bound_a_run below). THAT DOES NOT SOFTEN THIS TEST: the
            // Number entry still parses to 0, EntryArmed(0) is still what makes the profile armed, and
            // the defect would still be live for any profile without a backstop. The backstop is a
            // ceiling on run LENGTH; it is not what tells the gate a rebirth is coming.
            foreach (var f in CorpusFiles().Where(p => Path.GetFileName(p) == "CBlock3.2-E.json"))
            {
                var times = RebirthTimes(JSON.Parse(File.ReadAllText(f)));
                Assert.Equal(new[] { 0.0, 1920.0 }, times);            // the parse, stated outright
                Assert.Contains(0.0, times);                           // the Number entry, still zero
                Assert.Contains(times, RebirthSchedule.EntryArmed);    // new gate: a rebirth is coming
                // The ORIGINAL defect, preserved: the zero entry alone would have failed the old gate.
                Assert.False(times[0] > 0);
            }
            Assert.Contains(CorpusFiles(), p => Path.GetFileName(p) == "CBlock3.2-E.json");
        }

        [Fact]
        public void Every_shipped_profile_is_armed_or_disarmed_and_the_split_is_the_documented_one()
        {
            // Named rather than counted: a count moves whenever a profile is added, and the point is
            // WHICH profiles changed answer.
            //
            // ⚠ THIS SET WAS THE WHOLE CHALLENGE-BLOCK SPINE — TWELVE NAMES — AND IS NOW ONE.
            // On 2026-08-08 a 32-minute Time backstop was added to every challenge preset that lacked
            // one, in BOTH Presets\ and SampleProfiles\, after [OPERATOR] found a Troll run stalled
            // 4h28m against a compounding Number target. Those profiles now carry a positive time, so
            // they are armed the OLD gate's way too and no longer belong here.
            //
            // ⚠ THIS IS NOT THE DEFECT BEING FIXED, AND MUST NOT BE READ AS FIXING IT. A backstop
            // bounds run LENGTH; it says nothing about whether a rebirth is coming. Any profile whose
            // only entry is Number-with-no-Time still parses to 0 and still needs EntryArmed(0) to be
            // seen at all — which is exactly what CBlock2.0-LSC still proves below. Delete the
            // backstops and this set grows back; the gate is what stops the bug returning.
            var armedAtZero = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // The one challenge preset deliberately left without a backstop: it has an EMPTY
                // challenge queue and a Number target of 100,000 — two orders above the blocks' 1000 —
                // so the 32m ceiling calibrated for them does not transfer. Left as the live witness
                // that a Number-only profile still parses to zero.
                "CBlock2.0-LSC.json"
            };

            var seenArmedAtZero = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disarmed = new List<string>();

            foreach (var f in CorpusFiles())
            {
                var name = Path.GetFileName(f);
                var times = RebirthTimes(JSON.Parse(File.ReadAllText(f)));
                bool armed = times.Any(RebirthSchedule.EntryArmed);
                bool hadPositive = times.Any(t => t > 0);

                if (!armed)
                {
                    disarmed.Add(name);
                    // A disarmed profile must be disarmed under BOTH readings — the fix must not have
                    // dragged any LRB profile into "a rebirth is coming".
                    Assert.False(hadPositive);
                    continue;
                }

                if (!hadPositive)
                {
                    // Armed, but with nothing positive: this is exactly the set the old gate got wrong.
                    seenArmedAtZero.Add(name);
                    Assert.Contains(name, armedAtZero);
                }
            }

            Assert.Equal(armedAtZero.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                         seenArmedAtZero.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            // The disarmed side is real and non-empty — the negative control on live data.
            Assert.NotEmpty(disarmed);
            Assert.Contains(disarmed, n => n.StartsWith("LRB", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(disarmed, n => n.IndexOf("HackDay", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains("PostEND.json", disarmed);
        }

        [Fact]
        public void A_profile_that_pairs_a_number_trigger_with_a_time_backstop_was_never_affected()
        {
            // CBlock4/5, FinalEvil24hCBlock, FinalCBlock, SADCBlock1, PostEND-Challenges and
            // C-Microblock1-Basics all carry a Number entry AND a Time entry. The Time entry is a real
            // seconds target, so NextRebirthTargetSeconds already returned it and these never idled.
            // Pinned so the fix cannot be mistaken for having changed them.
            foreach (var f in CorpusFiles().Where(p => Path.GetFileName(p) == "CBlock4.json"))
            {
                var times = RebirthTimes(JSON.Parse(File.ReadAllText(f)));
                Assert.Contains(times, t => t == 0.0);      // the Number trigger
                Assert.Contains(times, t => t > 0);         // the Time backstop
                Assert.Contains(times, RebirthSchedule.EntryArmed);
            }
        }

        // ---------------------------------------------------------------- mirror + convention pins

        // Source text with whole-line `//` comments removed. The tests below grep for CODE, and the
        // code they grep for is described in prose two lines above itself — without this, the comment
        // explaining the defect satisfies the assertion that the defect is gone.
        private static string CodeOnly(string src) =>
            string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        [Fact]
        public void Mirror_matches_the_parser_source()
        {
            var root = RepoRoot();
            var custom = File.ReadAllText(Path.Combine(root, "NGUAdvisor", "AllocationProfiles", "CustomAllocation.cs"));

            // The default that makes a timeless Number entry ARMED rather than absent. If this ever
            // becomes `var time = -1.0;` the mirror above is wrong and so is EntryArmed's premise.
            Assert.Contains("var time = 0.0;", custom);
            Assert.Contains("if (bp[\"Time\"] != null)", custom);

            // The legacy single-key path still routes through ParseTime as type "time".
            Assert.Contains("CustomAllocation.ParseTime(rbtime), 0.0, \"time\"", custom);

            // ParseTime's h/m/default arithmetic, which the mirror reproduces.
            Assert.Contains("time += 60 * 60 * N.Value.AsInt;", custom);
            Assert.Contains("time += 60 * N.Value.AsInt;", custom);
        }

        [Fact]
        public void EntryArmed_is_the_same_test_the_rebirth_path_already_runs_on()
        {
            // EntryArmed is not a new convention. It is `>= 0`, which is what DoRebirth filters on and
            // the inverse of what TimeRebirth.RebirthAvailable refuses on. If either of those moves,
            // this test fails rather than the two quietly disagreeing about what -1 means.
            var root = RepoRoot();
            var custom = File.ReadAllText(Path.Combine(root, "NGUAdvisor", "AllocationProfiles", "CustomAllocation.cs"));
            var timeRb = File.ReadAllText(Path.Combine(root, "NGUAdvisor", "AllocationProfiles", "RebirthStuff", "TimeRebirth.cs"));

            Assert.Contains("_wrapper.rebirth.Where(x => x.RebirthTime >= 0.0)", custom);
            Assert.Contains("if (RebirthTime < 0.0)", timeRb);

            // And RebirthIsArmed delegates to EntryArmed rather than re-spelling the comparison.
            Assert.Contains("RebirthSchedule.EntryArmed(rb.RebirthTime)", custom);
        }

        [Fact]
        public void BloodPlanner_no_longer_gates_the_NUMBER_sink_on_a_seconds_figure()
        {
            // The regression guard proper. The gate must not be spelled as a comparison against
            // NextRebirthTargetSeconds again — that is the exact line that shipped the defect.
            var planner = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", "BloodPlanner.cs")));

            Assert.DoesNotMatch(new Regex(@"rebirthOn\s*=.*NextRebirthTargetSeconds"), planner);
            Assert.Contains("var outlook = RebirthOutlook(norb);", planner);
            Assert.Contains("bool rebirthOn = outlook == RebirthSchedule.Outlook.Coming;", planner);

            // NextRebirthTargetSeconds is still read TWICE in this file — RunLeftSeconds and
            // InvestmentWindowOpen — and both are honest: they want a duration, and -1 correctly means
            // "no deadline known". Pinned so a later sweep does not "fix" them too.
            Assert.Equal(2, Regex.Matches(planner, @"NextRebirthTargetSeconds\(\)").Count);
        }

        [Fact]
        public void The_idle_message_names_the_actual_cause()
        {
            // The old single sentence asserted "no rebirth scheduled" against profiles that HAD
            // scheduled one, which is what made the defect read as correct behaviour for so long.
            var planner = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", "BloodPlanner.cs")));

            Assert.DoesNotContain("no rebirth scheduled to bank NUMBER for", planner);
            Assert.Contains("NORB: no rebirth to cash a NUMBER multi into", planner);
            Assert.Contains("Auto Rebirth is off", planner);
            Assert.Contains("the profile schedules no rebirth to bank NUMBER for", planner);
        }
    }
}
