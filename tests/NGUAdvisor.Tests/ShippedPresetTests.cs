using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The AUTO-INSTALLED preset set (NGUAdvisor\Presets\*.json).
    //
    // These files are different from every other profile in the repo in one way that matters: the csproj glob
    // embeds them into the DLL and PresetInstaller writes them into the player's profiles dir on startup,
    // flat and without asking. A broken one is not a file somebody chose to open — it is a file that appears
    // in everybody's dropdown. So the bar is higher than "SimpleJSON managed to read it":
    //
    //   * STRICT JSON, checked with System.Text.Json rather than SimpleJSON. SimpleJSON is deliberately
    //     lenient (it will swallow things a conforming parser rejects), so validating a shipped file with it
    //     proves only that our own reader copes. It must also be free of duplicate keys — legal-ish JSON that
    //     silently discards whichever copy loses.
    //   * FLAT NAMES MUST NOT COLLIDE. The install target is Path.Combine(profilesDir, fileName) with no
    //     difficulty folder, on a case-insensitive filesystem, and profile resolution is by bare name. Two
    //     presets differing only in case would be one file at runtime.
    //   * THE DESCRIPTION KEYS MUST SURVIVE A ROUND-TRIP. _Preset/_Injector are top-level keys inside
    //     Breakpoints, so ProfileModel routes them through _passthrough and re-emits them verbatim. Move
    //     either of them INSIDE a breakpoint and IsCommentKey would drop it on the first save from the
    //     companion's editor — the file would quietly lose its own documentation. This pins the placement.
    public class ShippedPresetTests
    {
        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string PresetRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "Presets");

        private static string[] Presets() => Directory.GetFiles(PresetRoot(), "*.json");

        // Names the campaign table binds to a block — the subset that must also carry the manual-setup line.
        private static HashSet<string> CampaignNames() =>
            new HashSet<string>(CampaignTables.AllProfiles(false).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void Every_preset_is_strict_json_with_no_duplicate_keys()
        {
            var files = Presets();
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                // Default JsonDocumentOptions: no comments, no trailing commas. If it parses here it is
                // conforming JSON, not just SimpleJSON-shaped text.
                using (var doc = JsonDocument.Parse(text))
                    AssertNoDuplicateKeys(doc.RootElement, Path.GetFileName(f), "");
            }
        }

        private static void AssertNoDuplicateKeys(JsonElement el, string file, string path)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject())
                {
                    Assert.True(seen.Add(p.Name), file + ": duplicate key " + path + "/" + p.Name +
                                                  " — one copy is silently discarded on load");
                    AssertNoDuplicateKeys(p.Value, file, path + "/" + p.Name);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var v in el.EnumerateArray()) AssertNoDuplicateKeys(v, file, path + "/" + i++);
            }
        }

        // R-DUP — A `Priorities` ARRAY MAY NOT CONTAIN THE SAME TOKEN TWICE.
        //
        // audit 01 §R-DUP named THIS FILE as the gap, correctly: the duplicate-key test above walks INTO
        // arrays but only ever compares OBJECT KEYS, so two identical ELEMENTS of one array are invisible to
        // it. The failure mode is arithmetic, not style — a token listed twice expands twice, both copies are
        // counted in prioCount, and every other lane in that breakpoint is divided down to match.
        //
        // ⚠ MEASURED 2026-08-07 AGAINST e12de0d, AND THE COUNT IS ZERO: no duplicate element in any array, in
        // any of the 30 shipped presets, the 49 SampleProfiles, or the operator's 31 installed profiles. This
        // test PINS that zero. It did not fix anything, because there was nothing to fix.
        //
        // ⚠ THE FINDING BEHIND THIS RULE WAS REAL, AND IT WAS NEVER IN THIS REPO. Worth stating precisely,
        // because the shorthand "24hr-EarlyEvil listed ALLNGU twice" has been repeated into two source
        // comments and reads as if a shipped preset was broken:
        //
        //   * audit 01 #11 / audit 36 §3a measured the operator's INSTALLED copy under %LOCALAPPDATA%Low,
        //     which really did carry `[ "ALLNGU", "CAPWAN", "ALLAT", "ALLNGU" ]` at Energy[3] t=h:20 and
        //     `[ "ALLNGU", "BR:20", "WAN", "ALLNGU" ]` at Magic[3]. Both duplicate seats are non-CAP, so
        //     prioCount really did count nine NGU lanes as eighteen. 36 §3a says outright that 01's line
        //     numbers "resolve against this live file — not against Presets/".
        //   * `Presets\24hr-EarlyEvil.json` never had it. Energy h22 and Magic h22 hold one bare `ALLNGU`
        //     each — two pools, one token apiece — in every revision back to 679e1d8.
        //   * The installed copy no longer has it either: it was hand-edited on 2026-08-04, ~20h after 36
        //     measured it, and now matches the repo's h22 shape. That hand-edit is also why it is the one
        //     file on that machine taking PreserveUserEdit forever.
        //
        // So the rule is sound and the instance was genuine; what was never true is that a SHIPPED preset
        // carried it. This test is what makes that checkable instead of re-litigated.
        //
        // Scoped to the keys the RUNTIME reads — Breakpoints.<section>[].Priorities. BaseBreakpoints reads
        // Time / Priorities / Challenge and nothing else (ProfileTokenCorpusTests:66-69), so a repeat inside
        // PrioritiesDefault / Before30 / AdvDC / a gear list is inert documentation and is not failed on here.
        private static readonly string[] TokenSections = { "Energy", "Magic", "R3" };

        // Returns one line per offending array. Shared with the negative control below so the sweep is
        // demonstrably capable of failing rather than being a test that can only ever pass.
        internal static List<string> DuplicatePriorityTokens(string json, string label)
        {
            var bad = new List<string>();
            var root = SimpleJSON.JSON.Parse(json)["Breakpoints"];
            if (root == null) return bad;

            foreach (var section in TokenSections)
            {
                var arr = root[section];
                if (arr == null || !arr.IsArray) continue;
                for (var b = 0; b < arr.Count; b++)
                {
                    var prios = arr[b]["Priorities"];
                    if (prios == null || !prios.IsArray) continue;

                    var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var t = 0; t < prios.Count; t++)
                    {
                        var token = prios[t].Value;
                        if (token == null) continue;
                        int n;
                        seen.TryGetValue(token, out n);
                        seen[token] = n + 1;
                    }

                    foreach (var kv in seen)
                    {
                        if (kv.Value < 2) continue;
                        bad.Add(label + " " + section + "[" + b + "] lists \"" + kv.Key + "\" " + kv.Value +
                                " times — it expands " + kv.Value + "x into prioCount and divides every other " +
                                "lane in that breakpoint down to match");
                    }
                }
            }
            return bad;
        }

        [Fact]
        public void No_shipped_preset_lists_the_same_priority_token_twice_in_one_array()
        {
            var bad = new List<string>();
            var files = 0;
            foreach (var f in Presets())
            {
                files++;
                bad.AddRange(DuplicatePriorityTokens(File.ReadAllText(f), Path.GetFileName(f)));
            }
            Assert.True(files >= 30, "the preset walk found only " + files + " files — the root is wrong");
            Assert.True(bad.Count == 0, "duplicate priority tokens:\n  " + string.Join("\n  ", bad));
        }

        // The negative control. Without this the sweep above is unfalsifiable, which is the exact way audit
        // 01's own R-DUP finding survived unmeasured for five days.
        [Fact]
        public void The_duplicate_token_check_actually_catches_a_duplicate()
        {
            const string doubled =
                "{\"Breakpoints\":{\"Energy\":[{\"Time\":0,\"Priorities\":[\"ALLNGU\",\"WAN\",\"ALLNGU\"]}]}}";
            var hits = DuplicatePriorityTokens(doubled, "synthetic.json");
            Assert.Single(hits);
            Assert.Contains("ALLNGU", hits[0]);
            Assert.Contains("2 times", hits[0]);

            // …and the shape that is NOT a duplicate: the same token once in each of two pools, which is what
            // 24hr-EarlyEvil actually carries and what finding #11 misread.
            const string twoPools =
                "{\"Breakpoints\":{\"Energy\":[{\"Time\":0,\"Priorities\":[\"ALLNGU\"]}]," +
                "\"Magic\":[{\"Time\":0,\"Priorities\":[\"ALLNGU\"]}]}}";
            Assert.Empty(DuplicatePriorityTokens(twoPools, "synthetic.json"));
        }

        [Fact]
        public void Preset_filenames_do_not_collide_when_installed_flat()
        {
            // PresetInstaller strips the "NGUAdvisor.Presets." resource prefix and writes the remainder into
            // profilesDir directly, so the basename IS the installed name.
            var names = Presets().Select(Path.GetFileName).ToList();
            var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(distinct.Count, names.Count);
        }

        [Fact]
        public void Every_preset_describes_itself_in_the_dropdown()
        {
            foreach (var f in Presets())
            {
                var bps = ProfileModel.Load(File.ReadAllText(f));
                var raw = SimpleJSON.JSON.Parse(File.ReadAllText(f))["Breakpoints"];
                Assert.NotNull(bps);
                var preset = raw["_Preset"] != null ? raw["_Preset"].Value : null;
                Assert.False(string.IsNullOrWhiteSpace(preset), Path.GetFileName(f) + " has no _Preset text");
                Assert.Contains("auto-installed", preset);
            }
        }

        [Fact]
        public void Every_campaign_preset_carries_the_manual_setup_it_cannot_do_itself()
        {
            var campaign = CampaignNames();
            var checkedAny = false;
            foreach (var f in Presets())
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!campaign.Contains(name)) continue;   // Goal-* / steady-state presets need no _Injector
                checkedAny = true;
                var raw = SimpleJSON.JSON.Parse(File.ReadAllText(f))["Breakpoints"];
                var injector = raw["_Injector"] != null ? raw["_Injector"].Value : null;
                Assert.False(string.IsNullOrWhiteSpace(injector), name + " is a campaign block but has no _Injector text");
            }
            Assert.True(checkedAny, "no campaign profile resolved into Presets\\ at all — the promotion is missing");
        }

        // The placement contract. _Preset/_Injector live at the top of Breakpoints, which is passthrough;
        // a per-breakpoint Comment would be dropped by IsCommentKey instead.
        [Fact]
        public void Preset_and_injector_text_survives_a_ProfileModel_round_trip()
        {
            foreach (var f in Presets())
            {
                var original = File.ReadAllText(f);
                var before = SimpleJSON.JSON.Parse(original)["Breakpoints"];

                var saved = ProfileModel.Load(original).ToJson();
                var after = SimpleJSON.JSON.Parse(saved)["Breakpoints"];

                foreach (var key in new[] { "_Preset", "_Injector" })
                {
                    if (before[key] == null) continue;
                    Assert.False(after[key] == null, Path.GetFileName(f) + " lost " + key + " on save");
                    Assert.Equal(before[key].Value, after[key].Value);
                }

                // ...and stays stable, so an editor save does not churn the file every time.
                Assert.Equal(saved, ProfileModel.Load(saved).ToJson());
            }
        }

        // A block preset with no rotation is the C-Miniblock2.3-NoRB failure mode: it installs, appears in the
        // dropdown, runs nothing, and takes the rest of its challenge line down with it. (That file now runs
        // NORB-1; the rule it broke is what this keeps.)
        //
        // ONE profile is allowed an empty rotation, and only because it is not a challenge queue at all.
        // CBlock2.0-LSC is the allocation profile for Laser Sword runs — LscAdvisor owns LSC and redirects an
        // already-scheduled rebirth into it, so the file supplies gear, energy and a Number 100000 plan and
        // schedules nothing. Named here rather than inferred, and checked in BOTH directions so the licence
        // cannot go stale the way a NotAutoInstalled entry can.
        private static readonly Dictionary<string, string> RotationlessByDesign =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CBlock2.0-LSC",
              "Allocation profile, not a challenge queue: LscAdvisor owns Laser Sword and the guide names LSC " +
              "in no block roster. Its old LSC-3..20 queue was a redundant second mechanism that could never " +
              "fire (3 can never match with 1 and 2 unsupplied) and was removed; the allocation, gear and " +
              "Number 100000 rebirth plan are the part worth keeping." },
        };

        [Fact]
        public void Every_campaign_preset_actually_carries_a_challenge_rotation()
        {
            var campaign = CampaignNames();
            var sawExempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Presets())
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!campaign.Contains(name)) continue;
                var m = ProfileModel.Load(File.ReadAllText(f));
                bool empty = m.Challenges == null || m.Challenges.Count == 0;

                if (RotationlessByDesign.ContainsKey(name))
                {
                    // A stale licence is the same defect wearing the other hat: if this file grows a rotation
                    // again, the exemption has to be deleted rather than quietly excusing a real one.
                    Assert.True(empty, name + " is listed as rotationless by design but now ships a rotation — " +
                                       "delete the exemption instead of leaving it to excuse the next one");
                    sawExempt.Add(name);
                    continue;
                }

                Assert.False(empty, name + " is a campaign block profile but ships an empty Challenges array");
            }
            Assert.Equal(RotationlessByDesign.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                         sawExempt.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        // The promoted files are copies. Pin the copy against the source so a later edit to one side is
        // visible rather than silent: whatever the sample runs, the installed preset must run identically.
        //
        // Driven off the PRESET FOLDER, not off CampaignTables, on purpose — a promoted file whose block the
        // table does not name yet (C-Microblock1-Basics is exactly that today) still has to match its source.
        [Fact]
        public void Promoted_presets_run_the_same_rotation_as_the_sample_they_came_from()
        {
            var samples = Path.Combine(RepoRoot(), "NGUAdvisor", "SampleProfiles");
            int compared = 0;
            foreach (var preset in Presets())
            {
                var name = Path.GetFileNameWithoutExtension(preset);
                var twin = new[] { CampaignTables.Normal, CampaignTables.Evil, CampaignTables.Sadistic }
                    .Select(d => Path.Combine(samples, d, name + ".json"))
                    .FirstOrDefault(File.Exists);
                if (twin == null) continue;   // Goal-* / Normal-24hr / CBlock2-Normal have no sample twin
                compared++;
                Assert.Equal(ProfileModel.Load(File.ReadAllText(twin)).Challenges,
                             ProfileModel.Load(File.ReadAllText(preset)).Challenges);
            }
            // 16, plus C-Miniblock2.3-NoRB promoted once it was written, plus the five Evil DAILY DRIVERS
            // (24hr-EarlyEvil/-Evil/-MidEvil/-EndEvil, LRB-Evil). Those five existed in SampleProfiles/Evil
            // from the start but were never promoted, so PresetInstaller could not deliver them and
            // ProgressionAnalyzer had nothing to recommend on Evil but the Normal-track Goal-NGU.
            Assert.Equal(22, compared);
        }
    }
}
