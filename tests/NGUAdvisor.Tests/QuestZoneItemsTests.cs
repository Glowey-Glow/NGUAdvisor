using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace NGUAdvisor.Tests
{
    // QuestManager.ZoneItems drives the capstone hold: a finished major quest is held while any of the
    // zone's gear is still un-maxed, so the free forced-farming time keeps producing merges.
    //
    // It broke the same way ZoneStatHelper.OPower did — a hand-transcribed decomp table with no
    // detector under it — and in two directions at once:
    //   1. The extraction captured makeLevelledLoot(...) and MISSED EVERY makeLoot(...) gear id.
    //      Five of the ten reachable rows were incomplete; zone 9 listed one id where the game drops
    //      eight. The hold ended early or never started. (audit/12 §Q-1)
    //   2. It included part.Misc ids, which can never be maxed (the advisor never merges or boosts
    //      them, so level stays 0 and markItemAsMaxxed needs >= 100). Because the consumer breaks on
    //      the FIRST un-maxed id, the three cooking ids pinned a 3-hour hold forever. (audit/12 §Q-2)
    //
    // Parsed from SOURCE as text for the same reason ZoneOPowerTests is: QuestManager.cs reaches
    // Main.Character (:9-10) and cannot be linked into this headless project.
    //
    // The decomp figures below are CAPTURED rather than re-read because reference/decomp-full/ lives
    // outside this git repository. Each expectation names its LootDrop.cs line.
    public class QuestZoneItemsTests
    {
        // curQuestZone()'s switch returns exactly these, or -100.
        // [DECOMP] BeastQuestController.cs:997-1013
        private static readonly int[] ReachableZones = { 1, 2, 5, 9, 12, 13, 15, 20, 21, 22 };

        // Every makeLoot / makeLevelledLoot id in LootDrop.zone<N>Drop whose type[id] is an equipment
        // part (Head/Chest/Legs/Boots/Weapon/Accessory per Equipment.cs:570-577).
        public static IEnumerable<object[]> Expected => new[]
        {
            new object[] { 1,  158,  new[] { 40,41,42,43,44,45,46,77 } },
            new object[] { 2,  252,  new[] { 47,48,49,50,51,52,53,135,432 } },
            new object[] { 5,  614,  new[] { 53,68,69,70,71,72,73,74,435 } },
            new object[] { 9,  1114, new[] { 95,96,97,98,99,100,101,437 } },
            new object[] { 12, 1492, new[] { 122,123,124,125,126,127,439 } },
            new object[] { 13, 1602, new[] { 76,130,131,132,133,134,440 } },
            new object[] { 15, 1780, new[] { 76,143,144,145,146,147,148,441 } },
            new object[] { 20, 2467, new[] { 142,221,222,223,224,225,226,227,444 } },
            new object[] { 21, 2624, new[] { 142,213,214,215,216,217,218,219,220,445 } },
            new object[] { 22, 2771, new[] { 142,231,232,233,234,235,236,446 } },
        };

        [Theory]
        [MemberData(nameof(Expected))]
        public void Zone_row_matches_the_decomp_equipment_drops(int zone, int lootDropLine, int[] expected)
        {
            var table = Table();
            Assert.True(table.ContainsKey(zone),
                $"zone {zone} has no ZoneItems row, but curQuestZone() can return it");

            Assert.Equal(expected.OrderBy(i => i).ToArray(), table[zone].OrderBy(i => i).ToArray());

            // The row must carry its provenance; that is what makes a future drift detectable.
            Assert.True(CiteOf(zone) == lootDropLine,
                $"zone {zone} cites LootDrop.cs:{CiteOf(zone)}, expected :{lootDropLine}");
        }

        [Fact]
        public void The_table_holds_exactly_the_zones_curQuestZone_can_return()
        {
            var actual = Table().Keys.OrderBy(z => z).ToArray();
            Assert.Equal(ReachableZones.OrderBy(z => z).ToArray(), actual);
        }

        // The Q-1 fingerprint. Every one of these is a plain makeLoot(...) gear id that the old
        // extraction dropped on the floor. If a future edit reverts to the levelled-only rule, the
        // row shrinks back to its levelled ids and this fails.
        [Theory]
        [InlineData(2, 53)]
        [InlineData(5, 68)] [InlineData(5, 74)]
        [InlineData(9, 95)] [InlineData(9, 101)]
        [InlineData(12, 122)] [InlineData(12, 126)]
        [InlineData(13, 76)] [InlineData(13, 134)]
        public void Plain_makeLoot_gear_ids_are_present(int zone, int id)
        {
            Assert.Contains(id, Table()[zone]);
        }

        // The Q-2 fingerprint. part.Misc ids can never reach itemMaxxed, and the consumer breaks on
        // the first un-maxed id, so a single one of these pins the hold open for its whole 180-minute
        // budget. 367/369/370 are the cooking items that actually did it.
        [Theory]
        [InlineData(66)]  [InlineData(163)] [InlineData(339)] [InlineData(367)]
        [InlineData(368)] [InlineData(369)] [InlineData(370)] [InlineData(371)]
        public void No_row_contains_a_non_equipment_id(int miscId)
        {
            foreach (var kv in Table())
                Assert.False(kv.Value.Contains(miscId),
                    $"zone {kv.Key} lists id {miscId}, which is part.Misc — it can never be maxed, so " +
                    "the capstone hold would never release");
        }

        [Fact]
        public void Every_row_cites_a_decomp_line()
        {
            foreach (var zone in Table().Keys)
                Assert.True(CiteOf(zone) > 0, $"zone {zone} has no [DECOMP] LootDrop.cs cite");
        }

        // ---- the source parser ----------------------------------------------------------------------

        private static Dictionary<int, int[]> Table()
        {
            var rows = new Dictionary<int, int[]>();
            foreach (Match m in Regex.Matches(Body(), @"\{\s*(?<zone>\d+),\s*new\[\]\s*\{(?<ids>[^}]*)\}"))
            {
                var ids = m.Groups["ids"].Value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
                    .ToArray();
                rows[int.Parse(m.Groups["zone"].Value, CultureInfo.InvariantCulture)] = ids;
            }
            Assert.True(rows.Count > 0, "parsed no ZoneItems rows out of QuestManager.cs");
            return rows;
        }

        private static int CiteOf(int zone)
        {
            // The [DECOMP] comment immediately preceding the row for this zone.
            var m = Regex.Match(Body(),
                @"\[DECOMP\]\s*LootDrop\.cs:(?<l>\d+)[^\n]*\n\s*\{\s*" + zone + @"\s*,\s*new\[\]");
            return m.Success ? int.Parse(m.Groups["l"].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string Body()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", "QuestManager.cs"));
            int start = src.IndexOf("ZoneItems = new Dictionary<int, int[]>", StringComparison.Ordinal);
            Assert.True(start > 0, "could not find the ZoneItems table in QuestManager.cs");
            int end = src.IndexOf("};", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of the ZoneItems table");
            return src.Substring(start, end - start);
        }

        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }
    }
}
