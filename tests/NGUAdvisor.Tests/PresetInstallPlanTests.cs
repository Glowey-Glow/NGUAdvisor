using System;
using System.Collections.Generic;
using NGUAdvisor.Managers;
using Xunit;
using Action = NGUAdvisor.Managers.PresetInstallPlan.Action;

namespace NGUAdvisor.Tests
{
    // THE OVERWRITE POLICY.
    //
    // PresetInstaller was `if (File.Exists(dest)) continue;` — install once, never again — and that one line
    // is why audit 01 finding #11 is still live on the operator's machine: `Presets\24hr-EarlyEvil.json` was
    // repaired in the repo, the installed copy still lists ALLNGU twice, and no release could ever replace it.
    // "Never overwrite" is not a safe default, it is an undeliverable-fix default.
    //
    // The replacement has to hold two things at once: deliver our fixes, and never silently destroy a profile
    // the user edited (the companion's Timeline editor writes these files in place). The separator is a
    // manifest of the hash of the text THIS INSTALLER LAST WROTE — the only fact that distinguishes "our file,
    // untouched" from "the user's file now".
    public class PresetInstallPlanTests
    {
        private const string Shipped = "shipped-hash";
        private const string Old = "old-shipped-hash";
        private const string Edited = "user-edited-hash";

        [Fact]
        public void A_missing_preset_is_installed()
        {
            Assert.Equal(Action.Install, PresetInstallPlan.Decide(false, null, Shipped, null));
            // …and a stale manifest entry for a file the user deleted does not stop the reinstall.
            Assert.Equal(Action.Install, PresetInstallPlan.Decide(false, null, Shipped, Old));
        }

        [Fact]
        public void A_preset_that_already_matches_the_shipped_text_is_left_alone()
        {
            Assert.Equal(Action.AlreadyCurrent, PresetInstallPlan.Decide(true, Shipped, Shipped, null));
            Assert.Equal(Action.AlreadyCurrent, PresetInstallPlan.Decide(true, Shipped, Shipped, Shipped));
            Assert.False(PresetInstallPlan.Writes(Action.AlreadyCurrent));
        }

        // THE FIX-DELIVERY CASE. We wrote it, the user has not touched it, the repo has moved on.
        [Fact]
        public void Our_own_untouched_copy_is_refreshed_to_the_shipped_version()
        {
            Assert.Equal(Action.UpdateInPlace, PresetInstallPlan.Decide(true, Old, Shipped, Old));
            Assert.True(PresetInstallPlan.Writes(Action.UpdateInPlace));
        }

        // THE DO-NOT-DESTROY CASE. The manifest says we wrote X; the file on disk is not X and not the shipped
        // text either. That is a deliberate edit and it wins — permanently, not until the next release.
        [Fact]
        public void A_hand_edited_preset_is_never_overwritten()
        {
            Assert.Equal(Action.PreserveUserEdit, PresetInstallPlan.Decide(true, Edited, Shipped, Old));
            Assert.Equal(Action.PreserveUserEdit, PresetInstallPlan.Decide(true, Edited, Shipped, Shipped));
            Assert.False(PresetInstallPlan.Writes(Action.PreserveUserEdit),
                "PreserveUserEdit must never write — this is the assertion that stops a later refactor " +
                "turning the policy back into a plain overwrite");
        }

        // THE ONE-TIME MIGRATION. Every existing install predates the manifest, so provenance is unknown for
        // exactly one run per file. That is the run that has to deliver 24hr-EarlyEvil's fix, and it is the
        // only branch that can move a file the user might have authored — so it takes a copy first.
        [Fact]
        public void An_untracked_divergent_preset_is_backed_up_before_it_is_replaced()
        {
            Assert.Equal(Action.BackupThenInstall, PresetInstallPlan.Decide(true, Edited, Shipped, null));
            Assert.True(PresetInstallPlan.Writes(Action.BackupThenInstall));
            // The migration is once-only: after it runs the record equals the shipped hash, so the same file
            // next launch is AlreadyCurrent, and a later user edit lands on PreserveUserEdit, not on another
            // backup-and-replace.
            Assert.Equal(Action.AlreadyCurrent, PresetInstallPlan.Decide(true, Shipped, Shipped, Shipped));
            Assert.Equal(Action.PreserveUserEdit, PresetInstallPlan.Decide(true, Edited, "next-shipped", Shipped));
        }

        // An untracked file that happens to match the shipped text is adopted, not backed up — which is 22 of
        // the operator's 30 installed presets and keeps the migration's blast radius to the four that differ.
        [Fact]
        public void An_untracked_preset_that_matches_shipped_is_adopted_without_a_backup()
        {
            Assert.Equal(Action.AlreadyCurrent, PresetInstallPlan.Decide(true, Shipped, Shipped, null));
        }

        // ------------------------------------------------------------------ the hash

        [Fact]
        public void Hash_ignores_line_endings_but_nothing_else()
        {
            var lf = "{\n  \"a\": 1\n}\n";
            Assert.Equal(PresetInstallPlan.Hash(lf), PresetInstallPlan.Hash(lf.Replace("\n", "\r\n")));
            Assert.NotEqual(PresetInstallPlan.Hash(lf), PresetInstallPlan.Hash("{\n  \"a\": 2\n}\n"));
            // A trailing-space edit is still an edit; only the CRLF/LF distinction is folded.
            Assert.NotEqual(PresetInstallPlan.Hash(lf), PresetInstallPlan.Hash(lf + " "));
            Assert.Null(PresetInstallPlan.Hash(null));
        }

        [Fact]
        public void Hash_is_stable_across_cultures()
        {
            var text = "{ \"Time\": 0 }";
            string invariant;
            using (new CultureScope("en-US")) invariant = PresetInstallPlan.Hash(text);
            using (new CultureScope("de-DE")) Assert.Equal(invariant, PresetInstallPlan.Hash(text));
            using (new CultureScope("tr-TR")) Assert.Equal(invariant, PresetInstallPlan.Hash(text));
        }

        // ------------------------------------------------------------------ the manifest

        [Fact]
        public void Manifest_round_trips()
        {
            var records = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "24hr-EarlyEvil.json", PresetInstallPlan.Hash("a") },
                { "CBlock1.json", PresetInstallPlan.Hash("b") },
            };
            var back = PresetInstallPlan.ParseManifest(PresetInstallPlan.SerializeManifest(records));
            Assert.Equal(2, back.Count);
            Assert.Equal(records["24hr-EarlyEvil.json"], back["24hr-EarlyEvil.json"]);
            Assert.Equal(records["CBlock1.json"], back["CBlock1.json"]);
        }

        // A corrupt manifest must degrade to "no record", which costs one backup and never costs a user edit.
        // It must not throw: this runs inside Main's startup path, before anything else reads the profiles.
        [Fact]
        public void A_corrupt_manifest_degrades_to_no_records_rather_than_throwing()
        {
            Assert.Empty(PresetInstallPlan.ParseManifest(null));
            Assert.Empty(PresetInstallPlan.ParseManifest(""));
            Assert.Empty(PresetInstallPlan.ParseManifest("# only comments\n\n"));
            Assert.Empty(PresetInstallPlan.ParseManifest("no tab on this line\nanother\t\n\tleading"));
            var mixed = PresetInstallPlan.ParseManifest("garbage\nCBlock1.json\tabc123\n");
            Assert.Single(mixed);
            Assert.Equal("abc123", mixed["CBlock1.json"]);
        }

        // The manifest and the backups must be invisible to the profile picker, which is
        // Directory.GetFiles(dir, "*.json") with no AllDirectories. A *.json manifest would have shown up in
        // the dropdown as a profile that fails to load.
        [Fact]
        public void The_manifest_is_not_a_json_file_and_backups_live_in_a_subfolder()
        {
            Assert.False(PresetInstallPlan.ManifestFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrEmpty(PresetInstallPlan.BackupFolderName));
            Assert.DoesNotContain(".", PresetInstallPlan.BackupFolderName);
        }

        [Fact]
        public void Backup_names_are_timestamped_and_keep_the_json_extension()
        {
            var when = new DateTime(2026, 8, 4, 13, 5, 9, DateTimeKind.Local);
            Assert.Equal("24hr-EarlyEvil.20260804-130509.json",
                         PresetInstallPlan.BackupName("24hr-EarlyEvil.json", when));
            // Two replacements in the same second are the only collision, and File.Copy(overwrite: true)
            // makes that a no-op rather than a throw; different seconds never collide.
            Assert.NotEqual(PresetInstallPlan.BackupName("CBlock1.json", when),
                            PresetInstallPlan.BackupName("CBlock1.json", when.AddSeconds(1)));
        }

        [Fact]
        public void Backup_names_are_culture_invariant()
        {
            var when = new DateTime(2026, 8, 4, 13, 5, 9, DateTimeKind.Local);
            string invariant;
            using (new CultureScope("en-US")) invariant = PresetInstallPlan.BackupName("CBlock1.json", when);
            using (new CultureScope("ar-SA")) Assert.Equal(invariant, PresetInstallPlan.BackupName("CBlock1.json", when));
        }
    }
}
