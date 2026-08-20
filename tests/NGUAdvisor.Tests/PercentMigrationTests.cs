using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// THE GUARD THE `:percent` MIGRATION DID NOT HAVE.
    ///
    /// `:percent` now means MANUAL MODE — the named system leaves the optimiser and takes its
    /// authored share off the top. So a percentage left in a SHIPPED file is not a cosmetic
    /// leftover: it silently opts a preset's user out of the advisor on that system.
    ///
    /// The migration is a SPLIT, and both halves have to hold:
    ///   Energy/Magic — percentages were INERT under the constraint allocator, so removing them
    ///                  changed nothing and they must now be ABSENT.
    ///   R3           — percentages were always HONOURED (R3 is not routed through the constraint
    ///                  allocator), so manual mode is what those lanes already did and they must be
    ///                  PRESERVED. Stripping them would make CAPHACK-1 unbounded, which is worse.
    ///
    /// ⚠ WHY THIS FILE EXISTS. The first migration pass ran against `NGU/sampleprofiles/` — the
    /// operator's untracked, deployed copy — and missed `NGUAdvisor/SampleProfiles/`, which is the
    /// tracked tree `package-release.sh` actually ships (`PROFILES="$ROOT/NGUAdvisor/SampleProfiles"`,
    /// copied to `sampleprofiles/` in the zip). 49 files and 219 live tokens would have gone out in
    /// a release whose own presets told users percentages mean manual mode. Nothing in the suite
    /// could see it, because nothing asserted the token doctrine over either tree.
    /// </summary>
    public class PercentMigrationTests
    {
        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string PresetRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "Presets");
        private static string SampleRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "SampleProfiles");

        // Both trees. The presets are what a user loads from the UI; the samples are what ships in
        // the zip. Missing either one is how this went wrong the first time.
        private static IEnumerable<string> Shipped() =>
            Directory.GetFiles(PresetRoot(), "*.json")
                .Concat(Directory.GetFiles(SampleRoot(), "*.json", SearchOption.AllDirectories))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        // ⚠ CASE-INSENSITIVE, BECAUSE THE PARSER IS. ResourceBreakpoint.ParseBreakpointArray
        // upper-cases every token before matching (`x.Value.ToUpper()`), so "capwan:40" in a shipped
        // file is a LIVE manual lane. A case-sensitive guard would not see it.
        private static readonly Regex Percent =
            new Regex(@"^[A-Za-z0-9][A-Za-z0-9-]*:\s*\+?\d+\s*$", RegexOptions.IgnoreCase);

        // ⚠ EVERY `Priorities*` KEY, NOT JUST `Priorities`.
        //
        // The runtime reads only `Priorities`, so the siblings — PrioritiesDefault, PrioritiesLSC,
        // PrioritiesForNOTM/NoTM/NORB, PrioritiesBak, Priorities_bak — are inert where they sit.
        // They are NOT inert in effect: they exist to be PASTED into Priorities, and a preset that
        // tells a user "for No-TM, copy this" hands them a manual-mode opt-in the moment they do.
        //
        // This mattered immediately: the fix commit's own preset edits were entirely inside those
        // sibling arrays, i.e. the first version of this guard could not see the very change it was
        // written alongside.
        private static IEnumerable<string> Tokens(string file, params string[] sections)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("Breakpoints", out var bps)) yield break;
            foreach (var section in sections)
            {
                if (!bps.TryGetProperty(section, out var rows) || rows.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in row.EnumerateObject())
                    {
                        if (!prop.Name.StartsWith("Priorities", StringComparison.OrdinalIgnoreCase)) continue;
                        if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (var t in prop.Value.EnumerateArray())
                            if (t.ValueKind == JsonValueKind.String) yield return t.GetString();
                    }
                }
            }
        }

        [Fact]
        public void No_shipped_file_authors_an_energy_or_magic_percent()
        {
            var bad = new List<string>();
            var files = 0;
            foreach (var f in Shipped())
            {
                files++;
                foreach (var t in Tokens(f, "Energy", "Magic"))
                    if (Percent.IsMatch(t))
                        bad.Add(Path.GetFileName(f) + " -> " + t);
            }

            Assert.True(files >= 70, "the shipped walk found only " + files + " files — a root is wrong");
            Assert.True(bad.Count == 0,
                "these would silently put a preset user into MANUAL MODE:\n  " + string.Join("\n  ", bad));
        }

        [Fact]
        public void R3_percents_are_preserved_because_they_were_always_honoured()
        {
            // The negative control. Without it the test above is satisfiable by stripping
            // EVERYTHING, which would change real R3 behaviour — an unbounded CAPHACK-1.
            var found = new List<string>();
            foreach (var f in Shipped())
                foreach (var t in Tokens(f, "R3"))
                    if (Percent.IsMatch(t)) found.Add(t);

            Assert.NotEmpty(found);
            Assert.Contains("CAPHACK-1:10", found);
        }

        [Fact]
        public void Stripping_left_no_duplicate_and_gutted_no_breakpoint()
        {
            // Removing a suffix can COLLAPSE two lanes into one: the old share model deliberately
            // listed a lane twice, once capped-with-percent and once bare to mop up (BESTAUG:50 …
            // BESTAUG). A duplicate expands into prioCount and divides every other lane in that
            // breakpoint down to match.
            var bad = new List<string>();
            foreach (var f in Shipped())
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (!doc.RootElement.TryGetProperty("Breakpoints", out var bps)) continue;
                foreach (var section in new[] { "Energy", "Magic", "R3" })
                {
                    if (!bps.TryGetProperty(section, out var rows) || rows.ValueKind != JsonValueKind.Array)
                        continue;
                    int i = 0;
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (row.ValueKind == JsonValueKind.Object
                            && row.TryGetProperty("Priorities", out var prio)
                            && prio.ValueKind == JsonValueKind.Array)
                        {
                            var toks = prio.EnumerateArray()
                                .Where(t => t.ValueKind == JsonValueKind.String)
                                .Select(t => t.GetString()).ToList();
                            var dup = toks.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                            if (dup.Count > 0)
                                bad.Add(Path.GetFileName(f) + " " + section + "[" + i + "] duplicates " + string.Join(", ", dup));
                        }
                        i++;
                    }
                }
            }
            Assert.True(bad.Count == 0, "duplicate priority tokens:\n  " + string.Join("\n  ", bad));
        }

        // THE NEGATIVE CONTROL. Without it the sweep above is unfalsifiable — a `Breakpoints` or
        // `Energy` key rename would leave 79 files counted and ZERO tokens inspected, and every
        // assertion would pass vacuously. ShippedPresetTests ships one for its duplicate sweep for
        // exactly this reason.
        [Fact]
        public void The_sweep_actually_inspects_tokens()
        {
            int em = 0, r3 = 0;
            foreach (var f in Shipped())
            {
                em += Tokens(f, "Energy", "Magic").Count();
                r3 += Tokens(f, "R3").Count();
            }
            Assert.True(em > 1000, "the Energy/Magic walk found only " + em + " tokens — a key name changed");
            Assert.True(r3 > 100, "the R3 walk found only " + r3 + " tokens — a key name changed");
        }

        [Fact]
        public void The_percent_pattern_matches_what_the_parser_would()
        {
            // Positive: the shapes that reach CapPercent.
            Assert.Matches(Percent, "CAPTM:10");
            Assert.Matches(Percent, "capwan:40");        // parser upper-cases; the guard must too
            Assert.Matches(Percent, "CAPBR-300:10");
            Assert.Matches(Percent, "CAPALLNGU:5");
            // Negative: a bare token, and a target index, are not percents.
            Assert.DoesNotMatch(Percent, "CAPTM");
            Assert.DoesNotMatch(Percent, "NGU-4");
            Assert.DoesNotMatch(Percent, "CAPTM:ten");
        }

        [Fact]
        public void Every_shipped_file_is_still_strict_json()
        {
            foreach (var f in Shipped())
            {
                var ex = Record.Exception(() => JsonDocument.Parse(File.ReadAllText(f)).Dispose());
                Assert.True(ex == null, Path.GetFileName(f) + " no longer parses: " + ex?.Message);
            }
        }
    }
}
