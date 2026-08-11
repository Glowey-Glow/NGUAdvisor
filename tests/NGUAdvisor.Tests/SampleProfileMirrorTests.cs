using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace NGUAdvisor.Tests
{
    // build\deploy-sampleprofiles.ps1 — the mirror that makes NGU\sampleprofiles\ equal
    // NGUAdvisor\SampleProfiles\.
    //
    // WHY THESE RUN THE REAL SCRIPT. Every other test in this project links a Unity-free C# file and calls
    // it. There is no C# here to call: the artifact IS a PowerShell script, and re-implementing its
    // decision table in C# so it could be unit-tested would test a copy while the copy and the original
    // drifted. So these start the actual script, in a scratch tree, and read the filesystem back. The
    // decision table is PresetInstallPlan's (install / adopt / update / back-up-then-replace / preserve)
    // plus one verdict that class has no equivalent of — back-up-then-REMOVE — and that verdict is the
    // whole reason the mirror exists rather than a copy.
    //
    // ⚠ NOTHING HERE MAY POINT AT NGU\sampleprofiles\. That folder is the operator's hand-maintained
    // reference tree; the mirror deletes from its target. Every test below builds BOTH sides in
    // Path.GetTempPath() and passes them with -Source/-Target. There is no default-path test on purpose.
    //
    // WHAT WAS BROKEN (audit/42 §5, ranked #1 of 10 by detection latency). SampleProfiles is in no .csproj.
    // The deploy was a human with a mouse and it last happened 2026-07-02. Measured 2026-08-06 against the
    // repo's 49 files, the deployed folder held 57: 18 current, 30 stale, 1 never copied, and 9 files the
    // repo had DELETED — including cblock4.json, which CampaignTables.cs:357 names as broken ("Challenges
    // nested inside Breakpoints.Rebirth, loads as zero … Delete it rather than copying it in") and which
    // package-release.sh:36-41 records shipping in public releases through 2.0.1 for exactly this reason.
    // A copy that only ADDS leaves that file sitting there, which is why the delete side is under test
    // first and by name.
    public class SampleProfileMirrorTests : IDisposable
    {
        private readonly string _root;
        private readonly string _src;
        private readonly string _dst;

        public SampleProfileMirrorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "NGUAdvisorMirror_" + Guid.NewGuid().ToString("N"));
            // SEPARATE PARENTS, as in the real layout (<repo>\NGUAdvisor\SampleProfiles ->
            // <NGU>\sampleprofiles). Hanging both off _root as "SampleProfiles" and "sampleprofiles" is
            // ONE directory on a case-insensitive filesystem, which silently makes every test compare the
            // tree to itself and pass. That mistake was made here first; the script now refuses it
            // outright (see A_source_that_is_also_the_target_fails_loudly).
            _src  = Path.Combine(_root, "repo", "SampleProfiles");
            _dst  = Path.Combine(_root, "runtime", "sampleprofiles");
            Directory.CreateDirectory(_src);
            Directory.CreateDirectory(_dst);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // ------------------------------------------------------------------ harness

        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !File.Exists(Path.Combine(dir, "build", "deploy-sampleprofiles.ps1")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string ScriptPath() =>
            Path.Combine(RepoRoot(), "build", "deploy-sampleprofiles.ps1");

        // pwsh where it exists, Windows PowerShell 5.1 otherwise. The script is written to parse under
        // both (that is what its ASCII-only header is about), so whichever is present is a valid host.
        private static string _shell;
        private static string Shell()
        {
            if (_shell != null) return _shell;
            foreach (var candidate in new[] { "pwsh", "powershell" })
            {
                try
                {
                    var psi = new ProcessStartInfo(candidate) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add("exit 0");
                    using (var p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                        p.WaitForExit(30000);
                    }
                    _shell = candidate;
                    return _shell;
                }
                catch { }
            }
            throw new InvalidOperationException("no PowerShell host found (tried pwsh and powershell)");
        }

        private sealed class Run
        {
            public int ExitCode;
            public string Out;
        }

        private Run Mirror(params string[] extraArgs) => MirrorTo(_dst, extraArgs);

        private Run MirrorTo(string target, params string[] extraArgs)
        {
            var psi = new ProcessStartInfo(Shell())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(ScriptPath());
            psi.ArgumentList.Add("-Source");
            psi.ArgumentList.Add(_src);
            psi.ArgumentList.Add("-Target");
            psi.ArgumentList.Add(target);
            foreach (var a in extraArgs) psi.ArgumentList.Add(a);

            using (var p = Process.Start(psi))
            {
                // Both streams read asynchronously: reading one to the end while the other fills its pipe
                // buffer is the classic redirect deadlock.
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                Assert.True(p.WaitForExit(120000), "the mirror script did not finish within 120s");
                return new Run { ExitCode = p.ExitCode, Out = o.Result + e.Result };
            }
        }

        // ------------------------------------------------------------------ tree helpers

        private static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        private void WriteSrc(string rel, string text) => WriteUnder(_src, rel, text);
        private void WriteDst(string rel, string text) => WriteUnder(_dst, rel, text);

        private static void WriteUnder(string root, string rel, string text)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, text, NoBom);
        }

        private string DstPath(string rel) => Path.Combine(_dst, rel.Replace('/', Path.DirectorySeparatorChar));
        private bool DstHas(string rel) => File.Exists(DstPath(rel));
        private string DstText(string rel) => File.ReadAllText(DstPath(rel));
        private string BackupRoot() => Path.Combine(_dst, "_backup");

        // Backups are named "<stem>.<yyyyMMdd-HHmmss>.json" in the file's own subdirectory.
        private string[] BackupsOf(string rel)
        {
            var relWin = rel.Replace('/', Path.DirectorySeparatorChar);
            var dir  = Path.Combine(BackupRoot(), Path.GetDirectoryName(relWin) ?? "");
            var stem = Path.GetFileNameWithoutExtension(relWin);
            if (!Directory.Exists(dir)) return new string[0];
            return Directory.GetFiles(dir, stem + ".*.json", SearchOption.TopDirectoryOnly);
        }

        private const string ProfileA = "{\n  \"Breakpoints\" : {\n    \"Rebirth\" : []\n  }\n}\n";
        private const string ProfileB = "{\n  \"Breakpoints\" : {\n    \"Rebirth\" : [ 1 ]\n  }\n}\n";

        // ------------------------------------------------------------------ the delete side

        // THE cblock4 CASE, by name. This is the file the codebase already knows is broken and the reason a
        // merge is not good enough: it is not in the repo, so an add-only copy can never remove it, and it
        // has sat in the operator's reference folder since long before the repo deleted it.
        [Fact]
        public void Mirror_removes_a_file_the_repo_deleted()
        {
            WriteSrc("Evil/CBlock4.json", ProfileA);
            WriteDst("Evil/CBlock4.json", ProfileA);
            WriteDst("cblock4.json", "{ \"broken\" : true }\n");

            var run = Mirror();

            Assert.Equal(0, run.ExitCode);
            Assert.False(DstHas("cblock4.json"), "the superseded cblock4.json is still in the target");
            Assert.True(DstHas("Evil/CBlock4.json"), "the good CBlock4.json was removed");
            Assert.Contains("cblock4.json", run.Out);
            Assert.Contains("REMOVED", run.Out);
        }

        // The removal is a DELETE of the operator's file. It is allowed only because a copy is taken first,
        // so the copy is the thing under test, not the delete.
        [Fact]
        public void Mirror_copies_a_file_to_backup_before_it_removes_it()
        {
            const string doomed = "{ \"keep-me\" : \"this text must survive\" }\n";
            WriteSrc("Evil/CBlock4.json", ProfileA);
            WriteDst("cblock4.json", doomed);

            Mirror();

            var backups = BackupsOf("cblock4.json");
            Assert.Single(backups);
            Assert.Equal(doomed, File.ReadAllText(backups[0]));
        }

        // The backup keeps the file's subdirectory. Flattening it would be the exact collision
        // CampaignTables.cs:340-346 documents: a flat `cblock4.json` and `Evil\CBlock4.json` are ONE name
        // once the folders are dropped on a case-insensitive filesystem, so a flattened backup of the file
        // being deleted would overwrite the backup of the file being kept.
        [Fact]
        public void Backups_keep_the_subdirectory_so_the_two_CBlock4s_cannot_collide()
        {
            WriteSrc("Evil/CBlock4.json", ProfileB);
            WriteDst("Evil/CBlock4.json", "{ \"which\" : \"the good one, stale\" }\n");
            WriteDst("cblock4.json", "{ \"which\" : \"the broken one\" }\n");

            Mirror();

            var flat   = BackupsOf("cblock4.json");
            var nested = BackupsOf("Evil/CBlock4.json");
            Assert.Single(flat);
            Assert.Single(nested);
            Assert.NotEqual(Path.GetFullPath(flat[0]), Path.GetFullPath(nested[0]));
            Assert.Contains("the broken one", File.ReadAllText(flat[0]));
            Assert.Contains("the good one, stale", File.ReadAllText(nested[0]));
        }

        // _backup lives INSIDE the target, so without an exclusion the mirror finds its own backups as
        // extras and deletes them on the very next run — losing exactly what the backups exist to keep.
        [Fact]
        public void Mirror_never_deletes_its_own_backups()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);
            WriteDst("cblock3-evil.json", "{ \"legacy\" : true }\n");

            Mirror();
            var afterFirst = Directory.GetFiles(BackupRoot(), "*", SearchOption.AllDirectories).Length;
            Assert.Equal(1, afterFirst);

            Mirror();
            Assert.Equal(afterFirst, Directory.GetFiles(BackupRoot(), "*", SearchOption.AllDirectories).Length);
        }

        // ------------------------------------------------------------------ the write side

        [Fact]
        public void Mirror_installs_a_file_the_target_does_not_have()
        {
            // The real one: Normal\C-Microblock1-Basics.json was in the repo and had never been copied.
            WriteSrc("Normal/C-Microblock1-Basics.json", ProfileA);

            var run = Mirror();

            Assert.Equal(0, run.ExitCode);
            Assert.Equal(ProfileA, DstText("Normal/C-Microblock1-Basics.json"));
            // An install replaces nothing, so it must not manufacture a backup.
            Assert.False(Directory.Exists(BackupRoot()));
        }

        // The first run has no manifest, so every file that differs is "might be the operator's" and takes
        // the one-time BackupThenInstall migration. On the real folder that is 30 files.
        [Fact]
        public void First_run_backs_up_before_it_overwrites_a_file_it_did_not_write()
        {
            WriteSrc("Normal/CBlock1.json", ProfileB);
            WriteDst("Normal/CBlock1.json", ProfileA);

            Mirror();

            Assert.Equal(ProfileB, DstText("Normal/CBlock1.json"));
            var backups = BackupsOf("Normal/CBlock1.json");
            Assert.Single(backups);
            Assert.Equal(ProfileA, File.ReadAllText(backups[0]));
        }

        // Once the manifest says "this is the file we wrote, untouched", a repo change is delivered with no
        // backup at all. Backing up our own unmodified copy on every deploy would bury the ones that matter.
        [Fact]
        public void A_later_run_updates_our_own_untouched_copy_in_place()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);
            Mirror();                                   // installs; records the hash
            Assert.False(Directory.Exists(BackupRoot()));

            WriteSrc("Normal/CBlock1.json", ProfileB);  // the repo moves
            var run = Mirror();

            Assert.Equal(0, run.ExitCode);
            Assert.Equal(ProfileB, DstText("Normal/CBlock1.json"));
            Assert.Empty(BackupsOf("Normal/CBlock1.json"));
        }

        // THE ONE THAT PROTECTS THE OPERATOR. A file this tool wrote, then a human changed, then the repo
        // moved: the human wins, permanently, and is told so by name. Same policy as PresetInstallPlan's
        // PreserveUserEdit, for the same reason — the alternative silently destroys hand-edited work.
        [Fact]
        public void A_hand_edited_file_is_kept_and_said_out_loud()
        {
            const string handEdited = "{ \"mine\" : \"do not touch\" }\n";
            WriteSrc("Evil/HackDay.json", ProfileA);
            Mirror();                                   // installs; records the hash

            WriteDst("Evil/HackDay.json", handEdited);  // the operator edits it
            WriteSrc("Evil/HackDay.json", ProfileB);    // and the repo moves under them
            var run = Mirror();

            Assert.Equal(0, run.ExitCode);
            Assert.Equal(handEdited, DstText("Evil/HackDay.json"));
            Assert.Empty(BackupsOf("Evil/HackDay.json"));   // not replaced, so nothing to back up
            Assert.Contains("KEPT YOURS", run.Out);
            Assert.Contains("HackDay.json", run.Out);
        }

        // git checks this tree out with `* text=auto`, so on the operator's machine every one of the 48
        // shared files differs at the byte level and none of them differs as text. A byte comparison would
        // call the whole folder stale on every run and back up 48 files each time.
        [Fact]
        public void A_line_ending_difference_alone_is_not_a_change()
        {
            WriteSrc("Normal/CBlock1.json", "{\n  \"a\" : 1\n}\n");
            WriteDst("Normal/CBlock1.json", "{\r\n  \"a\" : 1\r\n}\r\n");

            var run = Mirror();

            Assert.Equal(0, run.ExitCode);
            Assert.Equal("{\r\n  \"a\" : 1\r\n}\r\n", DstText("Normal/CBlock1.json")); // untouched
            Assert.False(Directory.Exists(BackupRoot()));
        }

        [Fact]
        public void A_second_run_over_a_mirrored_tree_changes_nothing()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);
            WriteSrc("Evil/CBlock4.json", ProfileB);
            WriteDst("cblock4.json", "{ \"broken\" : true }\n");
            Mirror();

            var before = Directory.GetFiles(_dst, "*", SearchOption.AllDirectories)
                                  .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                  .Select(p => p + "|" + File.ReadAllText(p)).ToArray();
            var run = Mirror();
            var after = Directory.GetFiles(_dst, "*", SearchOption.AllDirectories)
                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                 .Select(p => p + "|" + File.ReadAllText(p)).ToArray();

            Assert.Equal(0, run.ExitCode);
            Assert.Equal(before, after);
        }

        // ------------------------------------------------------------------ the detector

        // -CheckOnly is the answer to audit/42's "no test, no gate, no log line, no build step". It has to
        // be worth trusting on both counts: it must not write, and it must not report clean when it is not.
        [Fact]
        public void CheckOnly_writes_nothing_and_exits_1_when_the_trees_disagree()
        {
            WriteSrc("Normal/CBlock1.json", ProfileB);
            WriteDst("Normal/CBlock1.json", ProfileA);
            WriteDst("cblock4.json", "{ \"broken\" : true }\n");

            var run = Mirror("-CheckOnly");

            Assert.Equal(1, run.ExitCode);
            Assert.Equal(ProfileA, DstText("Normal/CBlock1.json"));   // not updated
            Assert.True(DstHas("cblock4.json"));                      // not removed
            Assert.False(Directory.Exists(BackupRoot()));             // nothing backed up
            Assert.False(File.Exists(Path.Combine(_dst, "_deployed-samples.manifest")));
            Assert.Contains("DRIFTED", run.Out);
        }

        [Fact]
        public void CheckOnly_exits_0_when_the_trees_agree()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);
            WriteSrc("Evil/CBlock4.json", ProfileB);
            Mirror();

            var run = Mirror("-CheckOnly");

            Assert.Equal(0, run.ExitCode);
            Assert.Contains("IN SYNC", run.Out);
        }

        // The manifest sits in a folder whose *.json files are profiles. The runtime lists profiles with
        // Directory.GetFiles(dir, "*.json") (UiBridge.cs:2063) — naming this file *.json would have put the
        // mirror's bookkeeping in the operator's profile picker. Same trap PresetInstallPlan named.
        [Fact]
        public void The_manifest_cannot_appear_in_a_profile_list()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);
            Mirror();

            var manifest = Path.Combine(_dst, "_deployed-samples.manifest");
            Assert.True(File.Exists(manifest));
            Assert.DoesNotContain(Directory.GetFiles(_dst, "*.json", SearchOption.AllDirectories),
                                  p => string.Equals(Path.GetFileName(p), "_deployed-samples.manifest",
                                                     StringComparison.OrdinalIgnoreCase));
        }

        // A folder the delete side emptied is a phantom in a reference tree — the operator opens
        // sampleprofiles\cblock2\ and finds nothing, which reads as a broken deploy rather than a removed
        // set. (On the real folder this is cblock2\, six files.)
        [Fact]
        public void Mirror_prunes_a_folder_the_removals_emptied()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);
            WriteDst("cblock2/3_minute.json", "{ }\n");
            WriteDst("cblock2/notm.json", "{ }\n");

            Mirror();

            Assert.False(Directory.Exists(Path.Combine(_dst, "cblock2")));
            Assert.Equal(2, Directory.GetFiles(BackupRoot(), "*", SearchOption.AllDirectories).Length);
            Assert.True(Directory.Exists(Path.Combine(BackupRoot(), "cblock2")));
        }

        // A source tree that is not there is a hard failure, not a quiet success. The three csproj deploy
        // targets are all `Condition="… And Exists(…)"`, which means a build on a machine without the
        // runtime folder ships nothing and still reports success (audit/42 §1) — the shape of defect this
        // whole document is about. The mirror does not repeat it.
        [Fact]
        public void A_missing_source_tree_fails_loudly()
        {
            Directory.Delete(_src, true);

            var run = Mirror();

            Assert.Equal(2, run.ExitCode);
            Assert.Contains("FAILED", run.Out);
        }

        // One folder cannot be both sides of a mirror, and this is the failure mode that hides itself: every
        // file compares equal to itself, so the run reports a healthy "in sync" having measured nothing. It
        // is reachable by a plausible typo, because the repo tree is `SampleProfiles` and the runtime tree
        // is `sampleprofiles` — the same name on a case-insensitive filesystem, which is the identical
        // collision CampaignTables.cs:340-346 documents for cblock4. It cost a debugging round here.
        [Fact]
        public void A_source_that_is_also_the_target_fails_loudly()
        {
            WriteSrc("Normal/CBlock1.json", ProfileA);

            var run = MirrorTo(_src);

            Assert.Equal(2, run.ExitCode);
            Assert.Contains("FAILED", run.Out);
            // and it refused before doing anything
            Assert.False(Directory.Exists(Path.Combine(_src, "_backup")));
        }
    }
}
