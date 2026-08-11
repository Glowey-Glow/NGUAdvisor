using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Two things are under test here.
    //
    // (1) THE TABLE. CampaignTables is hand-transcribed reference data (the community guide's block spine),
    //     and the same class of typo that TitanTablesTests guards against applies: a duplicate id, a hole in
    //     the order, or a profile filename that does not exist would silently produce a campaign view that
    //     points at nothing.
    //
    // (2) THE CHAIN-HEALTH DERIVATION. Breaks must fall OUT of the union of what the profiles supply, not be
    //     asserted as constants — otherwise fixing a preset would leave the node reporting a break that no
    //     longer exists. That is not hypothetical: this file used to pin four documented breaks, all four were
    //     repaired in the profiles, and the derivation reported the change with no edit to itself. So the
    //     tests build synthetic campaigns and check the derivation's behaviour, then run it against the real
    //     shipped files — where the answer is now "none". An empty result is easy to reach by going blind, so
    //     the tests that assert it also mutate the real files and require the break to come back.
    public class CampaignTablesTests
    {
        // ------------------------------------------------------------------ repo layout

        private static string RepoRoot([CallerFilePath] string here = null)
        {
            // <repo>\tests\NGUAdvisor.Tests\CampaignTablesTests.cs
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "SampleProfiles")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        // The shipped sample tree. This is the SOURCE of what the readme tells the player to copy into the
        // profiles dir, and what package-release.sh:42 zips into a release, so it is the right thing to pin.
        //
        // This comment used to say NGU\sampleprofiles\ "is a deployed copy of" it. That described a deploy
        // that did not exist: SampleProfiles appears in no .csproj, and the copy was a human with a mouse
        // who last made it 2026-07-02 (audit/42 §5). Measured a month later it was 30 files stale, missing
        // one, and still holding nine the repo had deleted -- including the cblock4.json that CampaignTables
        // names as broken. It is a real mirror NOW: build\deploy.ps1 runs build\deploy-sampleprofiles.ps1 as
        // its last step, and SampleProfileMirrorTests covers that script. It remains the OPERATOR'S runtime
        // tree, not an input to anything here, and no test in this project reads it.
        private static string SampleRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "SampleProfiles");

        // The EMBEDDED set. Everything here is compiled into the DLL by the csproj glob and written flat into
        // the player's profiles dir by PresetInstaller on startup, so this — not the sample tree — is what a
        // fresh install actually has.
        private static string PresetRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "Presets");

        // ------------------------------------------------------------------ table shape

        [Fact]
        public void Order_is_a_total_order_over_the_whole_spine()
        {
            var orders = CampaignTables.Blocks.Select(b => b.Order).ToList();
            Assert.Equal(orders.Count, orders.Distinct().Count());
            Assert.Equal(Enumerable.Range(1, orders.Count).ToList(), orders.OrderBy(x => x).ToList());
            // The array itself is stored in spine order, which the UI renders directly.
            Assert.Equal(orders.OrderBy(x => x).ToList(), orders);
        }

        [Fact]
        public void Ids_and_profile_names_are_unique()
        {
            var ids = CampaignTables.Blocks.Select(b => b.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Case-INSENSITIVE, because profile resolution is by bare name on a case-insensitive filesystem:
            // two campaign entries whose names differ only in case would resolve to the same file. This is
            // what keeps the superseded `cblock4` out of the table (it folds onto the shipped `CBlock4`).
            var profiles = CampaignTables.AllProfiles(true).Select(p => p.Name).ToList();
            Assert.Equal(profiles.Count, profiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Chapters_never_go_backwards_along_the_spine()
        {
            int last = 0;
            foreach (var b in CampaignTables.Blocks)
            {
                Assert.True(b.Chapter >= last, b.Name + " is chapter " + b.Chapter + " after chapter " + last);
                last = b.Chapter;
            }
        }

        [Fact]
        public void Every_leg_declares_a_known_difficulty_and_every_block_a_known_kind()
        {
            var kinds = new[] { CampaignTables.KindBlock, CampaignTables.KindBasic, CampaignTables.KindMopUp };
            foreach (var b in CampaignTables.Blocks)
            {
                Assert.Contains(b.Kind, kinds);
                Assert.NotEmpty(b.Legs);
                Assert.False(string.IsNullOrWhiteSpace(b.EntryGate), b.Name + " needs an entry gate");
                Assert.False(string.IsNullOrWhiteSpace(b.HandsBackTo), b.Name + " needs a handoff");
                foreach (var l in b.Legs)
                    Assert.True(CampaignTables.DifficultyRank(l.Difficulty) >= 0, b.Name + ": " + l.Difficulty);
            }
        }

        // The structural fact the whole view rests on: this is one spine, and exactly one block spans two
        // difficulties. If a future edit splits CBlock 3 into two blocks, or flattens it onto one difficulty,
        // the Chapter-5 Normal return trip is lost and CBlock3.0-N reads as a Chapter 4 artifact again.
        [Fact]
        public void CBlock3_is_one_block_spanning_Evil_and_Normal_and_is_the_only_one()
        {
            var cb3 = Assert.Single(CampaignTables.Blocks, b => b.Legs.Length > 1);
            Assert.Equal("cblock3", cb3.Id);
            Assert.Equal(5, cb3.Chapter);
            Assert.Equal(new[] { CampaignTables.Evil, CampaignTables.Normal }, cb3.Legs.Select(l => l.Difficulty).ToArray());
            Assert.Equal("Evil + Normal", cb3.DifficultyLabel);
            // All three files, split across two difficulty folders, are Chapter 5.
            Assert.Contains("CBlock3.0-N", cb3.Legs.Single(l => l.Difficulty == CampaignTables.Normal).Profiles);
            Assert.Contains("CBlock3.1-E100LC", cb3.Legs.Single(l => l.Difficulty == CampaignTables.Evil).Profiles);
            Assert.Contains("CBlock3.2-E", cb3.Legs.Single(l => l.Difficulty == CampaignTables.Evil).Profiles);
        }

        // The Evil-entry Basic is sequenced by the guide but is not a block. It must be in the spine (between
        // CBlock2 and CBlock 3) and must be distinguishable from a block, or the view either loses it or
        // claims the guide numbers a tenth block.
        [Fact]
        public void Evil_entry_Basic_is_sequenced_but_not_numbered_as_a_block()
        {
            var basic = CampaignTables.Blocks.Single(b => b.Kind == CampaignTables.KindBasic);
            Assert.Equal("evil-entry-basic", basic.Id);
            Assert.Equal(5, basic.Chapter);
            Assert.Equal(0, basic.GuideNumber);              // not one of the guide's nine
            Assert.Equal(new[] { "BASIC-1" }, basic.Legs.Single().Ordinals);
            Assert.Equal(new[] { "EvilStart" }, basic.Legs.Single().Profiles);
            Assert.Equal(30, basic.CadenceMinutes);          // "Do 30 minute rebirths at the start" (Ch5)

            var cblock2 = CampaignTables.Blocks.Single(b => b.Id == "cblock2");
            var cblock3 = CampaignTables.Blocks.Single(b => b.Id == "cblock3");
            Assert.True(cblock2.Order < basic.Order && basic.Order < cblock3.Order);
        }

        [Fact]
        public void The_guide_numbers_exactly_nine_blocks_in_order_and_every_one_of_them_ships()
        {
            var numbered = CampaignTables.Blocks.Where(b => b.GuideNumber > 0).ToList();
            Assert.Equal(9, numbered.Count);
            Assert.Equal(Enumerable.Range(1, 9).ToList(), numbered.Select(b => b.GuideNumber).ToList());
            // PostEND is shipped but is not a guide block.
            Assert.Single(CampaignTables.Blocks, b => b.Kind == CampaignTables.KindMopUp);

            // EVERY block now names at least one profile. The Micro-CBlock was the last that did not: the
            // table predated C-Microblock1-Basics.json and left its leg as an empty string[], which made
            // shipped=false and stranded the whole Normal BASIC/24HR line. An empty Profiles array is the
            // exact shape that regresses, so name the offenders rather than just counting them.
            var unshipped = CampaignTables.Blocks.Where(b => !b.Shipped).Select(b => b.Id).ToArray();
            Assert.Equal(new string[0], unshipped);
            Assert.Contains("C-Microblock1-Basics",
                            CampaignTables.Blocks.Single(b => b.Id == "micro").Legs.Single().Profiles);

            // ...and Shipped is still the property that would catch it, which the line above can no longer
            // demonstrate now that nothing in the table is unshipped.
            var empty = new CampaignTables.Block("x", "X", 1, 1, 0, CampaignTables.KindBlock,
                new[] { new CampaignTables.Leg(CampaignTables.Normal, new string[0], new[] { "BASIC-1" }) },
                "entry", "handoff");
            Assert.False(empty.Shipped);
        }

        [Fact]
        public void Every_referenced_profile_exists_on_disk_somewhere()
        {
            var samples = SampleRoot();
            var presets = PresetRoot();
            Assert.True(Directory.Exists(samples), "sample profile tree not found at " + samples);
            Assert.True(Directory.Exists(presets), "preset folder not found at " + presets);
            foreach (var p in CampaignTables.AllProfiles(false))   // legacy files are reported, not required
            {
                // Either location counts here: the sample tree is what the readme tells a player to copy in,
                // the preset folder is what auto-installs. A name in NEITHER is a leg pointing at nothing.
                // Which of the two a name lives in is pinned separately, below.
                var sample = Path.Combine(samples, p.Difficulty, p.Name + ".json");
                var preset = Path.Combine(presets, p.Name + ".json");
                Assert.True(File.Exists(sample) || File.Exists(preset),
                    p.Name + " is referenced by block " + p.BlockId + " but exists neither at " + sample +
                    " nor at " + preset);
            }
        }

        // THE MISMATCH THIS CLASS OF BUG KEEPS PRODUCING. The table was transcribed from SampleProfiles, which
        // ships to nobody — it is repo-tree content linked from the readme. PresetInstaller only writes
        // Presets\*.json, flat, so a leg naming a profile that no preset installs points a fresh player's
        // campaign view at a file they do not have, silently. CBlock2 was exactly that: the shipped file is
        // named CBlock2-Normal and the table only knew "CBlock2".
        //
        // Both directions are checked, because a stale exemption is the same defect wearing the other hat: a
        // name marked not-shipped that later starts shipping would keep excusing itself forever.
        [Fact]
        public void Every_referenced_profile_auto_installs_or_says_why_it_does_not()
        {
            var root = PresetRoot();
            foreach (var p in CampaignTables.AllProfiles(false))
            {
                bool installs = File.Exists(Path.Combine(root, p.Name + ".json"));
                string why;
                bool exempt = CampaignTables.NotAutoInstalled.TryGetValue(p.Name, out why);

                Assert.True(installs || exempt,
                    p.Name + " is named by block " + p.BlockId + " but no Presets\\" + p.Name + ".json " +
                    "installs it and CampaignTables.NotAutoInstalled does not say why. Either promote the " +
                    "profile into Presets\\ or record the reason it is deliberately not shipped.");
                Assert.False(installs && exempt,
                    p.Name + " installs from Presets\\ but is still listed in NotAutoInstalled — stale exemption.");
                if (exempt) Assert.False(string.IsNullOrWhiteSpace(why), p.Name + " needs a reason, not an empty string");

                Assert.Equal(installs, CampaignTables.IsAutoInstalled(p.Name));
            }

            // Pin the exemptions themselves so removing one is a deliberate act, not a side effect.
            // C-Miniblock2.3-NoRB was the second: an empty stub held back rather than shipped broken. It runs
            // NORB-1 now and auto-installs, so leaving the exemption behind would have been the stale-exemption
            // defect the two Asserts above exist to catch.
            Assert.Equal(new[] { "CBlock2" },
                         CampaignTables.NotAutoInstalled.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.True(File.Exists(Path.Combine(root, "C-Miniblock2.3-NoRB.json")),
                        "C-Miniblock2.3-NoRB was promoted out of NotAutoInstalled, so it must now install");
        }

        // On a fresh install the presets resolve and the one deliberate exception does not, so the missing-files
        // list stops being "you have no profiles" and becomes exactly that one deliberate hole. It must say it
        // is deliberate, or the player goes looking for a file they were never meant to have.
        [Fact]
        public void A_deliberately_unshipped_profile_reports_its_reason_not_a_bare_filename()
        {
            var superseded = CampaignTables.ReadSupply("CBlock2", CampaignTables.Normal, "cblock2", false, null);
            var mine = CampaignTables.ReadSupply("MyOwnProfile", CampaignTables.Normal, "mini", false, null);

            var report = CampaignTables.Health(new[] { superseded, mine });
            Assert.Contains(report.MissingFiles, m => m.StartsWith("CBlock2")
                                                   && m.Contains("not auto-installed by design")
                                                   && m.Contains("CBlock2-Normal"));
            Assert.Contains("MyOwnProfile", report.MissingFiles);   // anything else stays a bare name

            // The repaired stub is NOT one of these any more: it installs, so a fresh player has it and it
            // would only appear here if they deleted it — a bare name, not an excuse.
            var norb = CampaignTables.ReadSupply("C-Miniblock2.3-NoRB", CampaignTables.Normal, "mini", false, null);
            Assert.Contains("C-Miniblock2.3-NoRB", CampaignTables.Health(new[] { norb }).MissingFiles);
        }

        // A folder-preserved copy of the sample tree is NOT a supply: the profile list is
        // Directory.GetFiles with no AllDirectories and the loader opens the bare name in the flat dir, so
        // <profiles>\Normal\CBlock2.json can never be selected or run. Counting it as a supplier invented a
        // duplicate for every ordinal it shared with the flat file that really does supply them -- 26 of
        // them on a real save, against CBlock2-Normal. The report must name the folder instead, because the
        // fix is to move the file up one level and the player cannot guess that from a bare filename.
        [Fact]
        public void A_nested_copy_is_reported_by_location_and_supplies_nothing()
        {
            const string rotation = "{\"Breakpoints\":{\"Challenges\":[\"NORB-5\",\"NORB-6\"]}}";

            var flat = CampaignTables.ReadSupply("CBlock2-Normal", CampaignTables.Normal, "cblock2", false, rotation);
            var shadowed = CampaignTables.ReadSupply("CBlock2", CampaignTables.Normal, "cblock2", false, null);
            shadowed.NestedIn = CampaignTables.Normal;

            var report = CampaignTables.Health(new[] { flat, shadowed });

            // The whole point: the shadowed copy carries the same ordinals but must not collide with them.
            Assert.Empty(report.Duplicates);
            Assert.Contains(report.MissingFiles, m => m.StartsWith("CBlock2 —")
                                                   && m.Contains(CampaignTables.Normal + "\\")
                                                   && m.Contains("move it up one folder"));

            // Without a nested copy the same absence keeps its own explanation rather than borrowing this one.
            var plain = CampaignTables.ReadSupply("CBlock2", CampaignTables.Normal, "cblock2", false, null);
            Assert.Contains(CampaignTables.Health(new[] { plain }).MissingFiles,
                            m => m.Contains("not auto-installed by design"));
        }

        [Fact]
        public void Every_table_ordinal_parses_and_is_within_the_game_cap()
        {
            foreach (var b in CampaignTables.Blocks)
                foreach (var l in b.Legs)
                    foreach (var raw in l.Ordinals.Concat(l.Optional))
                    {
                        string code; int idx;
                        Assert.True(CampaignTables.TryParseOrdinal(raw, out code, out idx), b.Name + ": " + raw);
                        int cap = CampaignTables.CapOf(code);
                        Assert.True(cap > 0, b.Name + ": unknown challenge code in " + raw);
                        Assert.InRange(idx, 1, cap);
                    }
        }

        [Fact]
        public void Advisor_owned_codes_are_never_prescribed_by_a_block()
        {
            // LSC is run by LscAdvisor, and the guide names it in no block's challenge list. If it ever
            // appears in a block's Ordinals the absent-check would start demanding LSC-1..2 from a preset,
            // which is precisely the false positive this category exists to prevent.
            foreach (var b in CampaignTables.Blocks)
                foreach (var l in b.Legs)
                    foreach (var raw in l.Ordinals.Concat(l.Optional))
                    {
                        string code; int idx;
                        CampaignTables.TryParseOrdinal(raw, out code, out idx);
                        Assert.False(CampaignTables.IsAdvisorOwned(code), b.Name + " prescribes " + raw);
                    }
        }

        // ------------------------------------------------------------------ synthetic campaigns

        // A campaign in which every (difficulty, code) the table prescribes is supplied contiguously from 1,
        // by one synthetic profile per difficulty.
        //
        // It ALSO marks every campaign profile as present-but-empty. That is not padding: the absent-check in
        // Health only judges a leg whose files it could all read, so without this no leg is readable and the
        // BreakAbsent path cannot be reached at all. It used to be reached by accident — the Micro-CBlock named
        // zero profiles, so its leg was trivially "fully read" and its BASIC/24HR ordinals were the only ones
        // ever judged absent. Wiring C-Microblock1-Basics in removed that accident and took the coverage with
        // it, which is what this does deliberately instead.
        private static List<CampaignTables.Supply> HealthyCampaign()
        {
            // difficulty -> code -> highest prescribed ordinal
            var top = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in CampaignTables.Blocks)
                foreach (var l in b.Legs)
                    foreach (var raw in l.Ordinals.Concat(l.Optional))
                    {
                        string code; int idx;
                        if (!CampaignTables.TryParseOrdinal(raw, out code, out idx)) continue;
                        Dictionary<string, int> byCode;
                        if (!top.TryGetValue(l.Difficulty, out byCode)) { byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); top[l.Difficulty] = byCode; }
                        int cur;
                        byCode[code] = byCode.TryGetValue(code, out cur) ? Math.Max(cur, idx) : idx;
                    }

            var supplies = new List<CampaignTables.Supply>();
            foreach (var p in CampaignTables.AllProfiles(false))
                supplies.Add(new CampaignTables.Supply
                { Profile = p.Name, Difficulty = p.Difficulty, BlockId = p.BlockId, Found = true });

            foreach (var kv in top)
            {
                var s = new CampaignTables.Supply { Profile = "synthetic-" + kv.Key, Difficulty = kv.Key, Found = true };
                foreach (var c in kv.Value)
                    for (int i = 1; i <= c.Value; i++) s.Ordinals.Add(c.Key + "-" + i);
                supplies.Add(s);
            }
            return supplies;
        }

        /// <summary>The one supply in a HealthyCampaign that actually carries that difficulty's ordinals.</summary>
        private static CampaignTables.Supply Synthetic(List<CampaignTables.Supply> campaign, string difficulty) =>
            campaign.Single(s => s.Profile == "synthetic-" + difficulty);

        // The scaffolding above has to actually reach the absent path, or the three tests that lean on it
        // would pass by never running it. Removing a whole code from the union must produce an `absent` break.
        [Fact]
        public void The_synthetic_campaign_makes_every_leg_readable_so_the_absent_path_is_reachable()
        {
            var readable = new HashSet<string>(
                HealthyCampaign().Where(s => s.Found).Select(s => s.Profile), StringComparer.OrdinalIgnoreCase);
            foreach (var b in CampaignTables.Blocks)
                foreach (var l in b.Legs)
                    foreach (var p in l.Profiles)
                        Assert.True(readable.Contains(p), b.Id + " leg names " + p + ", which nothing supplies");
        }

        [Fact]
        public void A_complete_campaign_reports_no_breaks()
        {
            var report = CampaignTables.Health(HealthyCampaign());
            Assert.Empty(report.Breaks);
            Assert.Empty(report.Dropped);
            Assert.Empty(report.Advisor);   // nothing supplied an advisor-owned code
        }

        [Fact]
        public void Removing_one_ordinal_strands_exactly_the_ones_above_it()
        {
            var campaign = HealthyCampaign();
            var normal = Synthetic(campaign, CampaignTables.Normal);
            Assert.True(normal.Ordinals.Remove("NORB-1"));

            var report = CampaignTables.Health(campaign);
            var br = Assert.Single(report.Breaks);
            Assert.Equal(CampaignTables.Normal, br.Difficulty);
            Assert.Equal("NORB", br.Code);
            Assert.Equal(CampaignTables.BreakStranded, br.Kind);
            Assert.Equal(1, br.Missing);
            Assert.Equal(0, br.Reach);
            Assert.Equal(2, br.StrandedFrom);
            Assert.Equal(10, br.StrandedTo);
            Assert.Equal(9, br.StrandedCount);   // 2..10 — everything above the hole, nothing else
        }

        [Fact]
        public void A_hole_in_the_middle_strands_only_what_is_above_it()
        {
            var campaign = HealthyCampaign();
            var evil = Synthetic(campaign, CampaignTables.Evil);
            Assert.True(evil.Ordinals.Remove("24HR-8"));

            var br = Assert.Single(CampaignTables.Health(campaign).Breaks);
            Assert.Equal("24HR", br.Code);
            Assert.Equal(8, br.Missing);
            Assert.Equal(7, br.Reach);          // 1..7 still fire
            Assert.Equal(9, br.StrandedFrom);
            Assert.Equal(2, br.StrandedCount);  // 9 and 10 only
        }

        [Fact]
        public void A_code_no_profile_supplies_at_all_is_absent_not_stranded()
        {
            var campaign = HealthyCampaign();
            var normal = Synthetic(campaign, CampaignTables.Normal);
            normal.Ordinals.RemoveAll(o => o.StartsWith("BASIC-", StringComparison.Ordinal));

            var br = Assert.Single(CampaignTables.Health(campaign).Breaks);
            Assert.Equal(CampaignTables.BreakAbsent, br.Kind);
            Assert.Equal("BASIC", br.Code);
            Assert.Equal(0, br.StrandedCount);   // nothing to strand: there are no entries
            Assert.Equal(1, br.Missing);
        }

        // The correction that matters most: LSC is advisor-owned, so a gap in it is NOT a chain break.
        [Fact]
        public void Advisor_owned_gaps_are_categorised_not_reported_as_breaks()
        {
            var campaign = HealthyCampaign();
            var normal = Synthetic(campaign, CampaignTables.Normal);
            normal.Profile = "CBlock2.0-LSC";
            for (int i = 3; i <= 20; i++) normal.Ordinals.Add("LSC-" + i);   // exactly how it ships: opens at 3

            var report = CampaignTables.Health(campaign);
            Assert.Empty(report.Breaks);                                     // NOT a stranded chain
            var note = Assert.Single(report.Advisor);
            Assert.Equal(CampaignTables.Normal, note.Difficulty);
            Assert.Equal("LSC", note.Code);
            Assert.Equal("LscAdvisor", note.Owner);
            Assert.Equal(18, note.Entries);
            Assert.False(note.EntriesReachable);                             // 3 can never match with 1-2 absent
            Assert.Contains("CBlock2.0-LSC", note.Profiles);
        }

        [Fact]
        public void Unknown_codes_and_over_cap_ordinals_are_dropped_the_way_the_runtime_drops_them()
        {
            var campaign = HealthyCampaign();
            var normal = Synthetic(campaign, CampaignTables.Normal);
            normal.Ordinals.AddRange(new[] { "NOTAREALCODE-1", "TC-8", "TC-0", "garbage" });

            var report = CampaignTables.Health(campaign);
            Assert.Equal(4, report.Dropped.Count);   // TC caps at 7; TC-0 could never equal cur+1
            Assert.Contains(report.Dropped, d => d.Contains("NOTAREALCODE-1") && d.Contains("unknown"));
            Assert.Contains(report.Dropped, d => d.Contains("TC-8") && d.Contains("1..7"));
            Assert.Contains(report.Dropped, d => d.Contains("garbage") && d.Contains("not CODE-n"));
            // Dropping them must not disturb the rest: the campaign is otherwise healthy.
            Assert.Empty(report.Breaks);
        }

        [Fact]
        public void Duplicate_ordinals_on_one_difficulty_are_named_but_are_not_breaks()
        {
            // Evil BASIC-1 really is double-shipped: EvilStart fires it at entry and CBlock3.2-E lists it too.
            var a = new CampaignTables.Supply { Profile = "EvilStart", Difficulty = CampaignTables.Evil, Found = true };
            a.Ordinals.Add("BASIC-1");
            var b = new CampaignTables.Supply { Profile = "CBlock3.2-E", Difficulty = CampaignTables.Evil, Found = true };
            b.Ordinals.AddRange(new[] { "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5" });

            var report = CampaignTables.Health(new[] { a, b });
            Assert.DoesNotContain(report.Breaks, x => x.Code == "BASIC" && x.Difficulty == CampaignTables.Evil);
            Assert.Contains(report.Duplicates, d => d.Contains("BASIC-1") && d.Contains("EvilStart") && d.Contains("CBlock3.2-E"));
        }

        // ------------------------------------------------------------------ malformed vs empty

        private const string LegacyNested = @"{
  ""Breakpoints"": {
    ""Energy"": [ { ""Time"": 0, ""Priorities"": [ ""NGU"" ] } ],
    ""Rebirth"": {
      ""Type"": ""Bosses"",
      ""Target"": 5,
      ""Challenges"": [ ""NOTM-1"", ""NOAUG-1"", ""NOAUG-2"" ]
    }
  }
}";

        [Fact]
        public void Legacy_nested_challenges_are_reported_as_malformed_not_empty()
        {
            var sup = CampaignTables.ReadSupply("cblock3-evil", CampaignTables.Evil, "cblock3", true, LegacyNested);
            Assert.True(sup.Found);
            Assert.Empty(sup.Ordinals);                                  // the schema really does load zero
            Assert.Equal(CampaignTables.MalformedNested, sup.Malformed);
            Assert.Equal(3, sup.HiddenCount);

            // Dropped into an otherwise healthy campaign it must report itself and change nothing else:
            // the ordinals hiding in Breakpoints.Rebirth are counted by neither the union nor a block.
            var campaign = HealthyCampaign();
            campaign.Add(sup);
            var report = CampaignTables.Health(campaign);
            Assert.Contains(report.Malformed, m => m.Contains("cblock3-evil") && m.Contains("malformed, not empty"));
            Assert.Empty(report.Breaks);
        }

        [Fact]
        public void A_genuinely_empty_challenge_array_is_not_called_malformed()
        {
            var sup = CampaignTables.ReadSupply("C-Miniblock2.3-NoRB", CampaignTables.Normal, "mini", false,
                @"{ ""Breakpoints"": { ""RebirthTime"": -1, ""Challenges"": [ ] } }");
            Assert.True(sup.Found);
            Assert.Empty(sup.Ordinals);
            Assert.Null(sup.Malformed);
        }

        // A fresh install where the sample tree was never copied in must not read as "the campaign is broken
        // in thirty places". We cannot know what a file we never read would have supplied.
        [Fact]
        public void No_profiles_on_disk_reports_missing_files_not_a_wall_of_breaks()
        {
            var none = CampaignTables.AllProfiles(false)
                .Select(p => CampaignTables.ReadSupply(p.Name, p.Difficulty, p.BlockId, false, null))
                .ToList();

            var report = CampaignTables.Health(none);
            Assert.Equal(none.Count, report.MissingFiles.Count);

            // NOT ONE BREAK. Every leg now names at least one profile, so with nothing on disk there is no leg
            // whose contents we can claim to know — and we do not guess. This used to report two absent breaks
            // (Normal BASIC and 24HR) purely because the Micro-CBlock's leg named zero files and so counted as
            // "fully read"; that was an artifact of the missing profile, not a fact about a fresh install.
            Assert.Empty(report.Breaks);

            // And the blocks say so rather than showing an empty 0/0.
            var st = CampaignTables.Status(none, CampaignTables.Normal, Completions(), null);
            Assert.Equal(4, st.Single(x => x.Block.Id == "mini").FilesMissing);
            Assert.Equal(1, st.Single(x => x.Block.Id == "micro").FilesMissing);
            Assert.Equal(none.Count, st.Sum(x => x.FilesMissing));   // every named profile is accounted for

            // The judgement is per-leg, so putting ONE leg back on disk brings exactly that leg back into
            // scope and leaves the rest silent. This is the property the emptiness above rests on.
            var justMicro = none.Select(s => s.Profile == "C-Microblock1-Basics"
                ? CampaignTables.ReadSupply(s.Profile, s.Difficulty, s.BlockId, false,
                                            @"{ ""Breakpoints"": { ""Challenges"": [ ""BASIC-2"", ""24HR-1"" ] } }")
                : s).ToList();
            var partial = CampaignTables.Health(justMicro);
            var br = Assert.Single(partial.Breaks);
            Assert.Equal(CampaignTables.Normal, br.Difficulty);
            Assert.Equal("BASIC", br.Code);            // BASIC-2 with no BASIC-1 anywhere
            Assert.Equal(CampaignTables.BreakStranded, br.Kind);
        }

        [Fact]
        public void Unparseable_and_missing_files_are_told_apart()
        {
            var bad = CampaignTables.ReadSupply("X", CampaignTables.Normal, "b", false, "this is not json");
            Assert.True(bad.Found);
            Assert.Equal(CampaignTables.MalformedParse, bad.Malformed);

            var gone = CampaignTables.ReadSupply("Y", CampaignTables.Normal, "b", false, null);
            Assert.False(gone.Found);
            Assert.Null(gone.Malformed);
            Assert.Contains("Y", CampaignTables.Health(new[] { gone }).MissingFiles);
        }

        // ------------------------------------------------------------------ against the real shipped presets

        private static List<CampaignTables.Supply> ShippedCampaign()
        {
            var root = SampleRoot();
            var list = new List<CampaignTables.Supply>();
            foreach (var p in CampaignTables.AllProfiles(true))
            {
                var path = Path.Combine(root, p.Difficulty, p.Name + ".json");
                string json = File.Exists(path) ? File.ReadAllText(path) : null;
                list.Add(CampaignTables.ReadSupply(p.Name, p.Difficulty, p.BlockId, p.IsLegacy, json));
            }
            return list;
        }

        // THE HEADLINE, DERIVED. Nothing here is a constant lifted from a write-up: the test reads the real
        // files, runs the same union arithmetic the snapshot node runs, and pins the result. It used to pin
        // four breaks:
        //
        //   Normal BASIC absent + Normal 24HR stranded — the Micro-CBlock named no profile, so nothing
        //     supplied the Normal Basics or 24HR-1, and without 24HR-1 the whole Normal 24H line
        //     (Mini -> CBlock1 -> CBlock2 -> CBlock3.0-N) was dead;
        //   Normal NORB stranded — C-Miniblock2.3-NoRB was an empty stub, so NORB-1 was never supplied;
        //   Evil 24HR stranded — CBlock5 stopped at 24HR-7 and FinalEvil24hCBlock opened at 9.
        //
        // All four were closed by editing profiles and one Leg. The derivation itself was not touched, which
        // is the whole point of deriving it. What it pins now is that the union is clean.
        [Fact]
        public void Shipped_presets_derive_a_chain_with_no_breaks_left()
        {
            var report = CampaignTables.Health(ShippedCampaign());

            Assert.Equal(new string[0], Summarise(report));

            // ...and the assertion above is still capable of failing. Each repair is reverted in turn against
            // the real files and the break it closed must come back, or "no breaks" would only mean the
            // derivation had gone blind.
            Assert.Equal(new[] { "Normal 24HR stranded missing=1 stranded=9",
                                 "Normal BASIC stranded missing=1 stranded=4" },
                         Summarise(WithoutOrdinals("C-Microblock1-Basics", "BASIC-1", "24HR-1")));

            Assert.Equal(new[] { "Normal NORB stranded missing=1 stranded=9" },
                         Summarise(WithoutOrdinals("C-Miniblock2.3-NoRB", "NORB-1")));

            Assert.Equal(new[] { "Evil 24HR stranded missing=8 stranded=2" },
                         Summarise(WithoutOrdinals("FinalEvil24hCBlock", "24HR-8")));
        }

        private static string[] Summarise(CampaignTables.HealthReport report) =>
            report.Breaks
                .Select(b => b.Difficulty + " " + b.Code + " " + b.Kind + " missing=" + b.Missing + " stranded=" + b.StrandedCount)
                .ToArray();

        private static string[] Summarise(List<CampaignTables.Supply> supplies) =>
            Summarise(CampaignTables.Health(supplies));

        /// <summary>The real shipped campaign with specific ordinals struck out of one profile.</summary>
        private static List<CampaignTables.Supply> WithoutOrdinals(string profile, params string[] ordinals)
        {
            var campaign = ShippedCampaign();
            var target = campaign.Single(s => s.Profile == profile && !s.IsLegacy);
            foreach (var o in ordinals)
                Assert.True(target.Ordinals.Remove(o), profile + " no longer supplies " + o);
            return campaign;
        }

        // LSC is advisor-owned, so it is categorised rather than reported as a chain break. The Normal
        // (CBlock2.0-LSC) and Evil (CBlock5) LSC-3..20 queues have been removed — they were a redundant second
        // mechanism that could not fire at all — leaving PostEND-Challenges, the only file anywhere that
        // starts LSC at 1, as the single row.
        [Fact]
        public void Shipped_LSC_is_reported_as_advisor_owned_never_as_a_break()
        {
            var report = CampaignTables.Health(ShippedCampaign());
            Assert.DoesNotContain(report.Breaks, b => b.Code == "LSC");

            var sad = Assert.Single(report.Advisor);
            Assert.Equal(CampaignTables.Sadistic, sad.Difficulty);
            Assert.Equal("LSC", sad.Code);
            Assert.Equal("LscAdvisor", sad.Owner);
            Assert.Equal(20, sad.Entries);
            Assert.True(sad.EntriesReachable);                                // chained from 1
            Assert.Equal(new[] { "PostEND-Challenges" }, sad.Profiles.ToArray());

            // No profile queues LSC on Normal or Evil any more, and the category still has to hold if one
            // ever does again: an opens-at-3 queue is an advisor NOTE, never a break.
            Assert.DoesNotContain(report.Advisor, n => n.Difficulty != CampaignTables.Sadistic);
            var reintroduced = ShippedCampaign();
            var cb5 = reintroduced.Single(s => s.Profile == "CBlock5");
            for (int i = 3; i <= 20; i++) cb5.Ordinals.Add("LSC-" + i);
            var after = CampaignTables.Health(reintroduced);
            Assert.DoesNotContain(after.Breaks, b => b.Code == "LSC");
            var evil = after.Advisor.Single(n => n.Difficulty == CampaignTables.Evil);
            Assert.Equal(18, evil.Entries);
            Assert.False(evil.EntriesReachable);
        }

        // Was "Sadistic is the only difficulty with no gaps" — all three are gapless now. Checked here against
        // the union directly rather than through Health, so a bug that made Health under-report would not also
        // silence this.
        [Fact]
        public void Every_difficultys_supplied_union_runs_contiguously_from_one()
        {
            // (difficulty, code) -> the ordinals any campaign profile supplies on that difficulty
            var union = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ShippedCampaign())
            {
                if (s.IsLegacy || !s.Found) continue;
                foreach (var raw in s.Ordinals)
                {
                    if (!CampaignTables.TryParseOrdinal(raw, out var code, out var idx)) continue;
                    if (CampaignTables.IsAdvisorOwned(code)) continue;   // opportunistic, not a chain
                    var key = s.Difficulty + "|" + code;
                    if (!union.TryGetValue(key, out var set)) union[key] = set = new SortedSet<int>();
                    set.Add(idx);
                }
            }

            Assert.NotEmpty(union);
            foreach (var kv in union)
                Assert.Equal(Enumerable.Range(1, kv.Value.Count).ToArray(), kv.Value.ToArray());

            // Every difficulty on the spine is represented, so "contiguous" is not passing by vacuity on one.
            foreach (var d in new[] { CampaignTables.Normal, CampaignTables.Evil, CampaignTables.Sadistic })
                Assert.Contains(union.Keys, k => k.StartsWith(d + "|", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Shipped_presets_have_no_unparseable_files_and_no_dropped_entries()
        {
            var report = CampaignTables.Health(ShippedCampaign());
            Assert.Empty(report.Malformed);   // several files are loose JSON, but SimpleJSON reads them all
            Assert.Empty(report.Dropped);     // every ordinal parses and is within its cap
        }

        // ------------------------------------------------------------------ status join

        private static Dictionary<string, int> Completions(params string[] pairs)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs) { var s = p.Split('='); d[s[0]] = int.Parse(s[1]); }
            return d;
        }

        [Fact]
        public void The_active_profiles_block_is_the_active_block_and_a_custom_profile_is_not_in_the_campaign()
        {
            var st = CampaignTables.Status(ShippedCampaign(), CampaignTables.Evil, Completions(), "CBlock4");
            Assert.Equal(CampaignTables.StateActive, st.Single(x => x.Block.Id == "cblock4").State);
            Assert.DoesNotContain(st, x => x.State == CampaignTables.StateActive && x.Block.Id != "cblock4");

            Assert.Null(CampaignTables.BlockOfProfile("24hr-EarlyEvil"));
            Assert.Null(CampaignTables.BlockOfProfile("MyOwnProfile"));
            Assert.Equal("cblock4", CampaignTables.BlockOfProfile("CBlock4").Id);
            Assert.Equal(CampaignTables.Evil, CampaignTables.LegOfProfile("CBlock4").Difficulty);
            // The Normal third of CBlock 3 resolves to the Normal leg even though the block is Evil-first.
            Assert.Equal(CampaignTables.Normal, CampaignTables.LegOfProfile("CBlock3.0-N").Difficulty);
            Assert.Equal("cblock3", CampaignTables.BlockOfProfile("CBlock3.0-N").Id);
        }

        [Fact]
        public void Off_difficulty_blocks_report_uncounted_rather_than_a_made_up_number()
        {
            // Playing Evil with no completions anywhere.
            var st = CampaignTables.Status(ShippedCampaign(), CampaignTables.Evil, Completions(), null);

            var sad = st.Single(x => x.Block.Id == "sad-entry");
            Assert.False(sad.Counted);
            Assert.Equal(CampaignTables.StateUpcoming, sad.State);

            // CBlock 3 spans both, so only the Evil leg can be verified.
            var cb3 = st.Single(x => x.Block.Id == "cblock3");
            Assert.False(cb3.Counted);
            Assert.Equal(2, cb3.Legs.Count);
            Assert.True(cb3.Legs.Single(l => l.Difficulty == CampaignTables.Evil).Counted);
            Assert.False(cb3.Legs.Single(l => l.Difficulty == CampaignTables.Normal).Counted);
        }

        [Fact]
        public void Done_counts_only_ordinals_the_live_counter_has_actually_spent()
        {
            var st = CampaignTables.Status(ShippedCampaign(), CampaignTables.Evil,
                                           Completions("BASIC=5", "100LC=3"), null);

            var basic = st.Single(x => x.Block.Id == "evil-entry-basic");
            Assert.True(basic.Counted);
            Assert.Equal(1, basic.Required);
            Assert.Equal(1, basic.Done);
            Assert.Equal(CampaignTables.StateComplete, basic.State);

            // CBlock 3's Evil leg: BASIC-1..5 spent, 100LC-1..3 spent, the rest not.
            var evilLeg = st.Single(x => x.Block.Id == "cblock3").Legs.Single(l => l.Difficulty == CampaignTables.Evil);
            Assert.Equal(8, evilLeg.Done);
        }

        [Fact]
        public void Advisor_owned_entries_are_kept_out_of_a_blocks_required_count()
        {
            var st = CampaignTables.Status(ShippedCampaign(), CampaignTables.Sadistic, Completions(), null);
            // PostEND-Challenges is now the only shipped profile mixing the two: 23 chain entries
            // (NoNGU 1-10, NoTM 1-10, 24H 8-10) alongside an LSC 1-20 run the advisor owns. The LSC entries
            // must not be able to hold the block at 23/43 forever.
            var postend = st.Single(x => x.Block.Id == "postend");
            Assert.Equal(23, postend.Required);
            Assert.Equal(20, postend.Opportunistic);

            // CBlock2 was the other case and is no longer one: CBlock2.0-LSC's LSC-3..20 queue is gone, so the
            // block is a plain 26 with nothing opportunistic in it.
            var cb2 = st.Single(x => x.Block.Id == "cblock2");
            Assert.Equal(26, cb2.Required);
            Assert.Equal(0, cb2.Opportunistic);
        }

        // Was "an unshipped block never becomes the next step". Every block ships now — the Micro-CBlock was
        // the last that did not, and while it was unshipped `next` skipped it and landed on the Mini-CBlock,
        // which told the player to start at chapter 3. With Micro wired in, `next` on a fresh Normal save is
        // the block the guide actually opens with.
        [Fact]
        public void The_next_step_is_the_first_block_with_work_left_and_it_ships()
        {
            var st = CampaignTables.Status(ShippedCampaign(), CampaignTables.Normal, Completions(), null);

            var next = Assert.Single(st, x => x.State == CampaignTables.StateNext);
            Assert.True(next.Block.Shipped);
            Assert.Equal("micro", next.Block.Id);
            Assert.Equal(6, next.Required);              // BASIC-1..5 + 24HR-1
            Assert.Equal(0, next.Done);

            // Spend the Micro-CBlock and `next` moves on rather than sticking.
            var after = CampaignTables.Status(ShippedCampaign(), CampaignTables.Normal,
                                              Completions("BASIC=5", "24HR=1"), null);
            Assert.Equal(CampaignTables.StateComplete, after.Single(x => x.Block.Id == "micro").State);
            Assert.Equal("mini", Assert.Single(after, x => x.State == CampaignTables.StateNext).Block.Id);

            // The guard that skipped it is still there and still keyed on Shipped, which nothing in the table
            // can demonstrate now: a block naming no profile would run nothing, so it must never be `next`.
            Assert.All(st, x => Assert.True(x.Block.Shipped));
        }

        // ------------------------------------------------------------------ the published node

        // Reproduces the owner's live save: Evil, one Basic done, a hand-written 24h profile selected,
        // AdvisorChallenges off. This is the exact JSON UiBridge publishes — UiBridge only supplies the
        // live reads — so both the shape the companion consumes and its size are pinned here.
        private static SimpleJSON.JSONObject LiveLikeNode()
        {
            return CampaignTables.ToJson(
                ShippedCampaign(),
                CampaignTables.Evil,
                Completions("BASIC=1", "NOAUG=0", "24HR=0", "100LC=0", "NOEC=0", "TC=0",
                            "NORB=0", "LSC=0", "BLIND=0", "NONGU=0", "NOTM=0"),
                "24hr-EarlyEvil", false, false);
        }

        [Fact]
        public void The_published_node_has_the_shape_the_companion_consumes()
        {
            var node = LiveLikeNode();
            Assert.True(node["known"].AsBool);
            Assert.Equal("Evil", node["difficulty"].Value);
            Assert.Equal(CampaignTables.Blocks.Length, node["blocks"].AsArray.Count);

            // Every block carries the keys the view needs, on every row.
            foreach (SimpleJSON.JSONNode b in node["blocks"].AsArray)
                foreach (var key in new[] { "id", "name", "chapter", "order", "kind", "difficulty",
                                            "state", "done", "required", "counted", "shipped",
                                            "entry", "handsBackTo", "profiles" })
                    Assert.False(b[key] == null, key + " missing from block " + b["id"].Value);

            // A hand-written profile is reported as outside the campaign, not guessed onto a block.
            Assert.False(node["active"]["inCampaign"].AsBool);
            Assert.Contains("not a campaign profile", node["active"]["note"].Value);

            // `breaks` is emitted unconditionally (the UI draws a "no chain breaks" state off an empty array),
            // and after the profile repairs it is empty. `advisor` is the single Sadistic LSC row.
            Assert.Equal(0, node["health"]["breaks"].AsArray.Count);
            Assert.Equal(1, node["health"]["advisor"].AsArray.Count);
            // Nothing to report => the key is absent, so the UI never draws an empty section.
            Assert.True(node["health"]["malformed"] == null);
            Assert.True(node["health"]["dropped"] == null);
            // ...but the two that DO have something to say are present: Evil BASIC-1 is deliberately supplied
            // twice, and CBlock2-Normal is a preset with no sample twin, which is what this fixture reads.
            Assert.Equal(1, node["health"]["duplicates"].AsArray.Count);
            Assert.Equal(1, node["health"]["missingFiles"].AsArray.Count);

            // The empty `breaks` is derived, not a constant: strike one ordinal out of the fixture and the
            // node publishes the break, with the text the companion renders.
            var broken = CampaignTables.ToJson(WithoutOrdinals("C-Miniblock2.3-NoRB", "NORB-1"),
                                               CampaignTables.Evil, Completions(), "24hr-EarlyEvil", false, false);
            var br = Assert.Single(broken["health"]["breaks"].AsArray.Children);
            Assert.Equal("NORB", br["code"].Value);
            Assert.Equal(CampaignTables.BreakStranded, br["kind"].Value);
            Assert.Equal(1, br["missing"].AsInt);
        }

        [Fact]
        public void The_advisor_row_says_the_switch_is_off_when_it_is_off()
        {
            var off = LiveLikeNode()["health"];
            foreach (SimpleJSON.JSONNode n in off["advisor"].AsArray) Assert.False(n["enabled"].AsBool);
            Assert.Contains("AdvisorChallenges is OFF", off["advisorNote"].Value);
            Assert.Contains("NOT chain breaks", off["advisorNote"].Value);

            var on = CampaignTables.ToJson(ShippedCampaign(), CampaignTables.Evil, Completions(),
                                           "CBlock4", false, true)["health"];
            foreach (SimpleJSON.JSONNode n in on["advisor"].AsArray) Assert.True(n["enabled"].AsBool);
            Assert.DoesNotContain("AdvisorChallenges is OFF", on["advisorNote"].Value);
        }

        // The node rides a ~34 KB snapshot line published once a second over a local named pipe, and it is
        // rebuilt on the ~5 s cadence and republished from cache in between. Measured at ~8.4 KB
        // (blocks ~5.6 KB / health ~2.5 KB / active ~0.1 KB). This pins the wire cost so a future edit that
        // starts emitting, say, every ordinal or the per-block guide list can't quietly double the snapshot.
        [Fact]
        public void The_published_node_stays_within_its_byte_budget()
        {
            int bytes = System.Text.Encoding.UTF8.GetByteCount(LiveLikeNode().ToString());
            Assert.InRange(bytes, 6000, 10000);
        }

        [Fact]
        public void Blocks_on_earlier_difficulties_read_complete_but_unverified()
        {
            var st = CampaignTables.Status(ShippedCampaign(), CampaignTables.Sadistic, Completions(), null);
            foreach (var id in new[] { "mini", "cblock1", "cblock2", "evil-entry-basic", "cblock4", "beucblock" })
            {
                var b = st.Single(x => x.Block.Id == id);
                Assert.Equal(CampaignTables.StateComplete, b.State);
                Assert.False(b.Counted);   // we never claim to have verified another difficulty's counters
            }
        }
    }
}
