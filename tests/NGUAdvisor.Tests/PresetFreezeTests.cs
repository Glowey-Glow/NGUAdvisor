using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE PRESET FREEZE (audit/42 §4, follow-on).
    //
    // PresetInstaller decides whether the operator hand-edited a shipped preset by hashing it. The
    // companion's editor is not the identity function, so OPENING A SHIPPED PRESET AND SAVING IT WITHOUT
    // CHANGING ANYTHING used to produce a file matching neither its shipped text nor its manifest record —
    // and Decide then correctly, permanently and silently refused to ever update that preset again. The
    // operator would only find out when a fix never arrived.
    //
    // Measured 2026-08-07 before the fix, load-then-save through the editor's real write path
    // (ProfileModel.Load(text).ToJson(), which is literally what ProfileService.SaveAndReload writes):
    //
    //   byte-identical round trip .... 0 of 30
    //   manifest-hash identical ...... 0 of 30      (Hash() folds CRLF, and it did not help)
    //   canonical-JSON identical ..... 0 of 30      (so this is NOT only whitespace)
    //
    // THREE transformations, not the two that are obvious:
    //   1. documentation keys dropped by name (ProfileModel:322-335). 21 of 30 carry at least one.
    //   2. inline arrays reflowed to one element per line; "Key": becomes "Key" : . All 30.
    //   3. ABSENT keys ADDED, because the writer always emits its payload key — Gear[n].ID and
    //      Rebirth[n].Time. This is why the 9 presets carrying NO documentation key still diverged, and it
    //      is the one a reader of the other two would not predict.
    //
    // These tests are built from the REAL shipped files, not fixtures, because the defect was a property of
    // those files meeting that writer.
    public class PresetFreezeTests
    {
        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string PresetRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "Presets");
        private static string[] Presets() => Directory.GetFiles(PresetRoot(), "*.json").OrderBy(f => f).ToArray();

        // Exactly what NGUAdvisorCompanion.ProfileService.SaveAndReload writes to disk (:194-198).
        private static string EditorSave(string text) => ProfileModel.Load(text).ToJson();

        [Fact]
        public void The_defect_still_reproduces_on_the_raw_comparison()
        {
            // Guard on the PREMISE. If this ever goes green the editor stopped transforming, and the
            // normalisation below is dead weight that should be reconsidered rather than silently kept.
            var files = Presets();
            Assert.Equal(30, files.Length);

            var unchanged = files.Count(f =>
            {
                var text = File.ReadAllText(f);
                return string.Equals(PresetInstallPlan.Hash(text),
                                     PresetInstallPlan.Hash(EditorSave(text)), StringComparison.OrdinalIgnoreCase);
            });
            Assert.Equal(0, unchanged);
        }

        [Fact]
        public void No_op_save_of_every_shipped_preset_stays_AlreadyCurrent()
        {
            // THE REGRESSION TEST FOR THE DEFECT. Install the preset, save it in the editor without
            // changing anything, run the installer's decision again: it must still be AlreadyCurrent.
            foreach (var f in Presets())
            {
                var name = Path.GetFileName(f);
                var shipped = File.ReadAllText(f);
                var shippedNorm = PresetInstallPlan.HashNormalized(shipped);

                // What the installer recorded when it wrote the file.
                var recorded = shippedNorm;

                // The operator opens it in the companion and saves. Nothing else.
                var onDisk = EditorSave(shipped);

                var verdict = PresetInstallPlan.Decide(true,
                    PresetInstallPlan.Hash(onDisk), PresetInstallPlan.HashNormalized(onDisk),
                    shippedNorm, recorded);

                Assert.True(verdict == PresetInstallPlan.Action.AlreadyCurrent,
                    name + " froze after a no-op save: " + verdict);
            }
        }

        [Fact]
        public void Saving_repeatedly_never_drifts()
        {
            // Normalisation has to be a FIXED POINT, or each save would step further away and the fix
            // would only survive one round trip. Three passes over every shipped preset.
            foreach (var f in Presets())
            {
                var name = Path.GetFileName(f);
                var shipped = File.ReadAllText(f);
                var once = EditorSave(shipped);
                var twice = EditorSave(once);
                var thrice = EditorSave(twice);

                Assert.True(string.Equals(once, twice, StringComparison.Ordinal), name + " is not idempotent at pass 2");
                Assert.True(string.Equals(twice, thrice, StringComparison.Ordinal), name + " is not idempotent at pass 3");
                Assert.Equal(PresetInstallPlan.Normalize(shipped), PresetInstallPlan.Normalize(thrice));
            }
        }

        [Fact]
        public void A_genuine_field_edit_is_still_detected_even_under_the_cosmetic_noise()
        {
            // W3. The five verdicts must still work. A REAL change to a REAL field, on a file that ALSO
            // carries every cosmetic difference the editor introduces, must come back PreserveUserEdit —
            // otherwise normalisation has been made blind to allocation content.
            foreach (var f in Presets())
            {
                var name = Path.GetFileName(f);
                var shipped = File.ReadAllText(f);
                var shippedNorm = PresetInstallPlan.HashNormalized(shipped);

                // Cosmetic noise first (this is what used to be enough to freeze it), then a genuine edit
                // on top: change the FIRST breakpoint time of whichever system this preset has.
                var reformatted = EditorSave(shipped);
                var model = ProfileModel.Load(reformatted);
                if (!MutateFirstTime(model)) continue;   // preset with no time-bearing breakpoint
                var edited = model.ToJson();

                Assert.NotEqual(PresetInstallPlan.HashNormalized(edited), shippedNorm);

                var verdict = PresetInstallPlan.Decide(true,
                    PresetInstallPlan.Hash(edited), PresetInstallPlan.HashNormalized(edited),
                    shippedNorm, shippedNorm);

                Assert.True(verdict == PresetInstallPlan.Action.PreserveUserEdit,
                    name + " lost a genuine field edit: " + verdict);
            }
        }

        private static bool MutateFirstTime(ProfileModel m)
        {
            if (m.Energy.Count > 0) { m.Energy[0].TimeSeconds += 137; return true; }
            if (m.Magic.Count > 0) { m.Magic[0].TimeSeconds += 137; return true; }
            if (m.Diggers.Count > 0) { m.Diggers[0].TimeSeconds += 137; return true; }
            if (m.Beards.Count > 0) { m.Beards[0].TimeSeconds += 137; return true; }
            if (m.Gear.Count > 0) { m.Gear[0].TimeSeconds += 137; return true; }
            return false;
        }

        [Fact]
        public void The_five_verdicts_still_hold_on_the_normalised_overload()
        {
            var shipped = File.ReadAllText(Path.Combine(PresetRoot(), "Goal-Adventure.json"));
            var sNorm = PresetInstallPlan.HashNormalized(shipped);
            var sRaw = PresetInstallPlan.Hash(shipped);

            // missing
            Assert.Equal(PresetInstallPlan.Action.Install,
                PresetInstallPlan.Decide(false, null, null, sNorm, null));
            // byte-identical to shipped
            Assert.Equal(PresetInstallPlan.Action.AlreadyCurrent,
                PresetInstallPlan.Decide(true, sRaw, sNorm, sNorm, null));
            // differs, and we have never written it: one-time migration, with a backup
            var other = PresetInstallPlan.Hash("{\"Breakpoints\":{\"Energy\":[{\"Time\":9,\"Priorities\":[\"TM\"]}]}}");
            var otherNorm = PresetInstallPlan.HashNormalized("{\"Breakpoints\":{\"Energy\":[{\"Time\":9,\"Priorities\":[\"TM\"]}]}}");
            Assert.Equal(PresetInstallPlan.Action.BackupThenInstall,
                PresetInstallPlan.Decide(true, other, otherNorm, sNorm, null));
            // our file, untouched, shipped has moved
            Assert.Equal(PresetInstallPlan.Action.UpdateInPlace,
                PresetInstallPlan.Decide(true, other, otherNorm, sNorm, otherNorm));
            // the user's file now
            Assert.Equal(PresetInstallPlan.Action.PreserveUserEdit,
                PresetInstallPlan.Decide(true, other, otherNorm, sNorm, sNorm));
        }

        [Fact]
        public void A_manifest_written_before_the_fix_still_reads_as_ours()
        {
            // MIGRATION. Manifests already on disk hold RAW hashes. Without the raw fallback in Decide,
            // every preset would read as hand-edited on the first launch after this change — the exact
            // freeze the fix exists to remove, delivered by the fix itself.
            var shipped = File.ReadAllText(Path.Combine(PresetRoot(), "CBlock5.json"));
            var moved = File.ReadAllText(Path.Combine(PresetRoot(), "CBlock4.json"));   // stands in for "shipped changed"

            var oldStyleRecord = PresetInstallPlan.Hash(shipped);        // what an old run wrote
            var verdict = PresetInstallPlan.Decide(true,
                PresetInstallPlan.Hash(shipped), PresetInstallPlan.HashNormalized(shipped),
                PresetInstallPlan.HashNormalized(moved), oldStyleRecord);

            Assert.Equal(PresetInstallPlan.Action.UpdateInPlace, verdict);
        }

        [Fact]
        public void Normalize_refuses_anything_that_is_not_a_profile()
        {
            // The degenerate case is the dangerous one: two DIFFERENT non-profiles must not normalise to
            // the same empty Breakpoints object and hash equal, which would take AlreadyCurrent and
            // overwrite one with the other. Normalize returns the input unchanged instead.
            foreach (var junk in new[] { "", "   ", "not json at all", "{}", "[1,2,3]",
                                         "{\"Breakpoints\":{}}", "{\"something\":\"else\"}" })
            {
                Assert.Equal(junk, PresetInstallPlan.Normalize(junk));
            }
            Assert.Null(PresetInstallPlan.Normalize(null));

            // and two different non-profiles stay different
            Assert.NotEqual(PresetInstallPlan.HashNormalized("{\"a\":1}"),
                            PresetInstallPlan.HashNormalized("{\"a\":2}"));
        }

        [Fact]
        public void Normalization_folds_documentation_keys_and_layout_but_not_content()
        {
            // The trade, pinned. These two must be indistinguishable...
            var bare = "{\"Breakpoints\":{\"Energy\":[{\"Time\":0,\"Priorities\":[\"TM\"]}]}}";
            var commented = "{\"Breakpoints\":{\"Energy\":[{\"Time\":0,\n \"Priorities\":[ \"TM\" ],\n" +
                            " \"Comment\":\"anything at all\",\"Note\":\"and this\"}]}}";
            Assert.Equal(PresetInstallPlan.HashNormalized(bare), PresetInstallPlan.HashNormalized(commented));

            // ...and these two must NOT be.
            var different = "{\"Breakpoints\":{\"Energy\":[{\"Time\":0,\"Priorities\":[\"BR\"]}]}}";
            Assert.NotEqual(PresetInstallPlan.HashNormalized(bare), PresetInstallPlan.HashNormalized(different));

            // A named alternate priority set is USER DATA, not a comment (ProfileModel:315-317). It must
            // survive normalisation, or an operator's stored loadout would become invisible to the check.
            var withSet = "{\"Breakpoints\":{\"Energy\":[{\"Time\":0,\"Priorities\":[\"TM\"]," +
                          "\"AdvDC\":[\"NGU-4\",\"NGU-6\"]}]}}";
            Assert.NotEqual(PresetInstallPlan.HashNormalized(bare), PresetInstallPlan.HashNormalized(withSet));
        }
    }
}
