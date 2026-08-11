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
    // OPower is the adventure attack at which a zone becomes one-shottable, and it gates every boost/gear
    // farm route (BoostFarmAdvisor, GearFarmAdvisor, ZoneStatHelper.ZoneFightType/GetBestZone).
    //
    // THE RULE THESE TESTS PIN, derived from the game and not fitted to the table:
    //
    //     OPower(zone) = ceil_4sf( max over enemies e in zone ( e.maxHP / 1.2 + e.defense / 2 ) )
    //
    // from PlayerController.cs:236-239 (baseDamage = attack - enemyDefense/2), :287-290 (regularAttack
    // multiplies by regAttackMulti and by Random.Range(0.8f, 1.2f)), Adventure.cs:388
    // (regAttackMulti = 1.5f), so surviving the worst 0.8 roll needs attack >= hp/(0.8*1.5) + def/2.
    // Rounding is UP because EnemyAI.cs:387 FLOORS the damage — rounding down fails OPEN.
    //
    // THIS TABLE HAS BEEN WRONG THREE DIFFERENT WAYS, each invisible on review because an OPower sitting
    // near IPower still reads as "a number":
    //   1. Zones 31-41 were, for seven of nine, EXACTLY 2 x IToughness — the wrong column, doubled —
    //      leaving them 10-18x too LOW. Fixed 2026-08-01.
    //   2. That 2026-08-01 pass regenerated ten rows as maxHP/1.2 with NO def/2 term, leaving all ten
    //      0.6-1.3% low — still failing OPEN. Fixed 2026-08-03.
    //   3. Zones 0-9 assumed no attack multiplier at all, running ~50% HIGH. Fixed 2026-08-03.
    // Zone 43 (7 Aethereal Seas) had NO ROW AT ALL — 31 rows for 32 non-titan zones — so the gate read
    // it as one-shottable at any attack in both farm advisors. Added 2026-08-03.
    //
    // WHY THIS STILL PARSES SOURCE AS TEXT rather than linking ZoneStatHelper: ZoneStatHelper.cs reaches
    // Main.Character (:96, :107, :116), Main.Log (:53, :69-74), ZoneHelpers (:119) and CombatHelpers
    // (:122). The static-Character weld is NOT broken as of this branch, so the file cannot be compiled
    // into this headless net9.0 project. ShippedPresetTests reads its inputs off disk for the same
    // reason. The GATE, by contrast, is now a Unity-free file (ZoneGate.cs) and IS linked and tested
    // directly below — that is the part where behaviour lives.
    //
    // WHY THE DECOMP FIGURES ARE CAPTURED HERE rather than re-read: reference/decomp-full/ lives OUTSIDE
    // this git repository (it is a sibling of the repo root, not a tracked directory), so a test that
    // read it would pass or fail based on a path no clone controls. Each row below carries the
    // AdventureController.cs line it came from, matching the per-row comments in ZoneStatHelper.cs, so
    // the capture is diffable against the decomp by hand or by script.
    public class ZoneOPowerTests
    {
        // The 32 non-titan zones and, for each, the enemy that BINDS max(hp/1.2 + def/2) — which is not
        // always the highest-HP enemy: in zone 37 JIGSAW ties on HP and wins on defense.
        // Columns: zone, binding enemy maxHP, its defense, its AdventureController.cs line, its name.
        [Theory]
        [InlineData(0, 100, 9, 1950, "A SMALL MOUSE (BOSS)")]
        [InlineData(1, 150, 13, 1956, "BROWN SLIME (BOSS)")]
        [InlineData(2, 900, 17, 1963, "Zombie")]
        [InlineData(3, 3000, 122, 1985, "CHAD (BOSS)")]
        [InlineData(4, 9000, 340, 1997, "BIRD PERSON (BOSS)")]
        [InlineData(5, 12000, 440, 2009, "SPIKY HAIRED GUY (BOSS)")]
        [InlineData(7, 85000, 1720, 2022, "SUNDAE (BOSS)")]
        [InlineData(9, 133333, 3133, 2036, "SUPER HEXAGON (BOSS)")]
        [InlineData(10, 335000, 7600, 2045, "GHOST DAD (BOSS)")]
        [InlineData(12, 1e6, 18300, 2059, "VIC (BOSS)")]
        [InlineData(13, 4.2e6, 63300, 2070, "DOCTOR WAHWEE (BOSS)")]
        [InlineData(15, 5.5e7, 750000, 2084, "A CLOGGED SHOWER DRAIN (BOSS)")]
        [InlineData(17, 1.06e9, 1.15e7, 2101, "EVIL BADLY DRAWN KITTY")]
        [InlineData(18, 8.6e9, 9e7, 2110, "An Army of Annoying Penguins")]
        [InlineData(20, 3.25e12, 3.05e10, 2132, "MELTED CHOCOLATE BLOB (BOSS)")]
        [InlineData(21, 5.25e14, 5.05e12, 2144, "EVIL SPIKY HAIRED GUY (BOSS)")]
        [InlineData(22, 2.7e15, 2.55e13, 2155, "TINKLES (BOSS)")]
        [InlineData(24, 1.25e18, 1.15e16, 2172, "THE DRAGON OF DILDO (BOSS)")]
        [InlineData(25, 1.25e19, 1.15e17, 2182, "THE LIFE OF THE PARTY (BOSS)")]
        [InlineData(27, 8.25e21, 8.15e19, 2199, "ELDER TYPO GOD, ELXU (BOSS)")]
        [InlineData(28, 4.25e22, 4.15e20, 2209, "DEMONIC FLURBIE (BOSS)")]
        [InlineData(29, 2.25e23, 2.15e21, 2219, "TRUE FINAL (BOSS)")]
        [InlineData(31, 2.25e26, 2.15e24, 2238, "RADIOACTIVE MACGUFFIN (BOSS)")]
        [InlineData(32, 3.25e28, 3.15e26, 2248, "BELDING (BOSS)")]
        [InlineData(33, 1.65e29, 1.65e27, 2258, "THE SHERIFF (BOSS)")]
        [InlineData(35, 6e30, 1.3e29, 2275, "A DAY-OLD BAGUETTE (BOSS)")]
        [InlineData(36, 2.06e31, 4.1e29, 2285, "THE 'FRO (BOSS)")]
        [InlineData(37, 6.5e31, 1.3e30, 2295, "JIGSAW (BOSS)")]
        [InlineData(39, 2.1e33, 4.3e31, 2311, "THE CRANE (BOSS)")]
        [InlineData(40, 5.06e33, 1.1e32, 2321, "A SINGLE GRAPE (BOSS)")]
        [InlineData(41, 1.3e34, 2.68e32, 2330, "THE GRAND DUTCH DUCHY")]
        [InlineData(43, 8.35e35, 1.34e34, 2355, "RAMSHACKLE SEA INN (BOSS)")]
        public void OPower_is_the_derived_one_shot_threshold(int zone, double hp, double def, int decompLine, string enemy)
        {
            var row = Table().SingleOrDefault(r => r.Zone == zone);
            Assert.True(row != null, $"zone {zone} ({enemy}) has no row in ZoneStatHelper.Defaults");

            double expected = Ceil4Sf(hp / OneShotDivisor + def / 2.0);

            // Relative, not exact: the literal parsed out of the source is not bit-identical to
            // Math.Ceiling(v/s)*s. The values carry four significant figures, so any REAL error is at
            // least 1 part in 1e4; 1e-9 is far below that and far above double round-off.
            double drift = Math.Abs(row.OPower - expected) / expected;
            Assert.True(drift < 1e-9,
                $"zone {zone} ({row.Name}): OPower is {row.OPower:e8}, derived is {expected:e8} " +
                $"({drift:e2} relative) = ceil_4sf({hp:e3}/1.2 + {def:e3}/2) from \"{enemy}\" " +
                $"at AdventureController.cs:{decompLine}");

            // The row must carry its provenance. A value without a cite is how this table went wrong twice.
            Assert.True(row.Cite == decompLine,
                $"zone {zone} ({row.Name}): row cites AdventureController.cs:{row.Cite}, expected :{decompLine}");
        }

        // regAttackMulti 1.5 (Adventure.cs:388) x the worst damage roll 0.8 (PlayerController.cs:287).
        private const double OneShotDivisor = 1.2;

        // Round UP at four significant figures: EnemyAI.cs:387 floors the damage, so a threshold rounded
        // down sits below the real requirement and fails OPEN.
        private static double Ceil4Sf(double v)
        {
            if (v <= 0) return 0;
            double e = Math.Floor(Math.Log10(v));
            double s = Math.Pow(10, e - 3);
            return Math.Ceiling(v / s) * s;
        }

        // ---- structural facts about the table as a whole -------------------------------------------

        [Fact]
        public void The_table_covers_every_non_titan_zone_and_no_titan_zone()
        {
            // Zone ids 0..45. Titans are 6,8,11,14,16,19,23,26,30,34,38,42 (bigBoss/enemyType in
            // AdventureController.createEnemyTable); 44 and 45 are TIPPI and THE TRAITOR, the two
            // final-boss zones. Everything else is a farmable adventure zone.
            var titansAndFinals = new HashSet<int> { 6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42, 44, 45 };
            var expected = Enumerable.Range(0, 46).Where(z => !titansAndFinals.Contains(z)).ToArray();

            var actual = Table().Select(r => r.Zone).OrderBy(z => z).ToArray();

            Assert.Equal(32, expected.Length);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Zone_43_has_a_row_because_its_absence_failed_the_gate_open()
        {
            var row = Table().SingleOrDefault(r => r.Zone == 43);
            Assert.True(row != null,
                "zone 43 (7 Aethereal Seas) has no row — a missing row used to read as 'one-shottable " +
                "at any attack' in both farm advisors. ZoneGate now fails closed, but the row must exist.");
            Assert.Equal("7 Aethereal Seas", row.Name);
        }

        [Fact]
        public void Every_row_cites_a_decomp_line()
        {
            var uncited = Table().Where(r => r.Cite <= 0).Select(r => r.Zone).ToArray();
            Assert.True(uncited.Length == 0,
                "rows with no [DECOMP] AdventureController.cs cite: " + string.Join(",", uncited));
        }

        // The fingerprint of the 2026-08-01 defect: OPower filled from twice the IToughness column.
        [Fact]
        public void No_zone_has_OPower_equal_to_twice_its_IToughness()
        {
            foreach (var r in Table())
            {
                double twice = 2.0 * r.IToughness;
                if (twice <= 0) continue;
                Assert.True(Math.Abs(r.OPower - twice) / twice > 0.01,
                    $"zone {r.Zone} ({r.Name}): OPower {r.OPower:e3} == 2 x IToughness — that is the " +
                    "wrong-column bug, not a one-shot threshold");
            }
        }

        // The fingerprint of the defect fixed 2026-08-03: the def/2 term dropped. Every row must sit
        // strictly ABOVE the bare maxHP/1.2, because every enemy in the game has non-zero defense.
        [Fact]
        public void No_zone_drops_the_enemy_defense_term()
        {
            foreach (var r in Table())
            {
                double withoutDef = Ceil4Sf(MaxHpOf(r.Zone) / OneShotDivisor);
                Assert.True(r.OPower > withoutDef,
                    $"zone {r.Zone} ({r.Name}): OPower {r.OPower:e4} does not exceed maxHP/1.2 " +
                    $"({withoutDef:e4}) — the enemy-defense term has been dropped again, which fails OPEN");
            }
        }

        // A one-shot gate sitting at roughly the idle-clearable power is not a gate: it would admit a zone
        // at the same attack as the idle path while dropping the defense requirement entirely.
        [Fact]
        public void OPower_is_far_above_IPower_so_the_one_shot_gate_means_something()
        {
            foreach (var r in Table())
            {
                if (r.IPower <= 0) continue;
                double ratio = r.OPower / r.IPower;
                Assert.True(ratio > 5.0, $"zone {r.Zone} ({r.Name}): OPower is only {ratio:0.0}x IPower");
            }
        }

        // ---- the guide cross-check (player consensus, NOT a source) ---------------------------------

        // audit/22 §Q4.2 and audit/23 §Q4: the community guide independently states a one-hit power for
        // exactly four zones. The decomp wins on any disagreement — this test records the size of the
        // disagreement so a future edit that widens it is visible. Ranges are the current measured
        // agreement, not a tolerance anyone chose.
        [Theory]
        [InlineData(18, 7.2e9, 1, "BAE 1-shot power 7.2b [GUIDE ch.4 §Post-v2]")]
        [InlineData(20, 1.3e12, 2, "Choco 2-hit at 1.3t [GUIDE ch.4 §Choco World]")]
        [InlineData(21, 440e12, 1, "Evilverse 1-hit 440 Trillion [GUIDE ch.5 §Evilverse]")]
        [InlineData(22, 2.27e15, 1, "PPPL 1-hit 2.27 Quadrillion [GUIDE ch.5 §PPPL]")]
        public void Guide_one_hit_powers_stay_within_five_percent_of_the_derived_value(
            int zone, double guideValue, int hits, string cite)
        {
            var row = Table().Single(r => r.Zone == zone);
            double guideAsOneShot = guideValue * hits;
            double ratio = row.OPower / guideAsOneShot;

            Assert.True(ratio > 0.95 && ratio < 1.05,
                $"zone {zone} ({row.Name}): derived OPower {row.OPower:e4} is {ratio:0.000}x the guide's " +
                $"{guideAsOneShot:e4} ({cite}). The decomp wins, but a divergence this large is a finding.");
        }

        // ---- the source parser ----------------------------------------------------------------------

        private sealed class Row
        {
            public int Zone;
            public string Name;
            public double OPower, IPower, IToughness;
            public int Cite;              // the AdventureController.cs line the row's comment names
        }

        private static List<Row> Table()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", "ZoneStatHelper.cs"));

            // Only the Defaults dictionary; RecommendedDcPercent above it has no ZoneStats entries.
            int start = src.IndexOf("Defaults = new Dictionary<int, ZoneStats>", StringComparison.Ordinal);
            Assert.True(start > 0, "could not find the Defaults table in ZoneStatHelper.cs");
            src = src.Substring(start);

            var rows = new List<Row>();
            foreach (Match m in Regex.Matches(src, @"(?<zone>\d+),\s*new ZoneStats\s*\{(?<body>[^}]*)\}"))
            {
                var body = m.Groups["body"].Value;
                var cite = Regex.Match(body, @"\[DECOMP\]\s*AdventureController\.cs:(?<l>\d+)");
                rows.Add(new Row
                {
                    Zone = int.Parse(m.Groups["zone"].Value, CultureInfo.InvariantCulture),
                    Name = Field(body, "Name"),
                    OPower = Num(body, "OPower"),
                    IPower = Num(body, "IPower"),
                    IToughness = Num(body, "IToughness"),
                    Cite = cite.Success ? int.Parse(cite.Groups["l"].Value, CultureInfo.InvariantCulture) : 0,
                });
            }
            Assert.True(rows.Count >= 30, $"parsed only {rows.Count} zone rows");
            return rows;
        }

        // Binding-enemy maxHP per zone, mirroring the InlineData above. Used only by the
        // dropped-defense-term test, which needs maxHP without the defense contribution.
        private static double MaxHpOf(int zone)
        {
            switch (zone)
            {
                case 0: return 100;        case 1: return 150;        case 2: return 900;
                case 3: return 3000;       case 4: return 9000;       case 5: return 12000;
                case 7: return 85000;      case 9: return 133333;     case 10: return 335000;
                case 12: return 1e6;       case 13: return 4.2e6;     case 15: return 5.5e7;
                case 17: return 1.06e9;    case 18: return 8.6e9;     case 20: return 3.25e12;
                case 21: return 5.25e14;   case 22: return 2.7e15;    case 24: return 1.25e18;
                case 25: return 1.25e19;   case 27: return 8.25e21;   case 28: return 4.25e22;
                case 29: return 2.25e23;   case 31: return 2.25e26;   case 32: return 3.25e28;
                case 33: return 1.65e29;   case 35: return 6e30;      case 36: return 2.06e31;
                case 37: return 6.5e31;    case 39: return 2.1e33;    case 40: return 5.06e33;
                case 41: return 1.3e34;    case 43: return 8.35e35;
                default: throw new ArgumentOutOfRangeException(nameof(zone), zone, "not a non-titan zone");
            }
        }

        private static double Num(string body, string key)
        {
            // Anchored so "OPower" cannot match inside a comment line, and so IPower does not match
            // the "OPower" substring.
            var m = Regex.Match(body, @"(?m)^\s*" + key + @"\s*=\s*(?<v>-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
            return m.Success ? double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) : 0.0;
        }

        private static string Field(string body, string key)
        {
            var m = Regex.Match(body, @"(?m)^\s*" + key + @"\s*=\s*""(?<v>[^""]*)""");
            return m.Success ? m.Groups["v"].Value : "";
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
