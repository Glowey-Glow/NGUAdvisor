using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimpleJSON;

namespace NGUAdvisor.Managers
{
    public class ZoneStatHelper
    {
        public static Dictionary<int, ZoneStats> UserOverrides;

        public static void CreateOverrides(string dir)
        {
            UserOverrides = Defaults.ToDictionary(entry => entry.Key, entry => entry.Value);
            var overridePath = Path.Combine(dir, "zoneOverride.json");
            if (!File.Exists(overridePath))
            {
                var emptyZones = @"{
    ""zones"": {
    ""0"": {
      ""MPower"": 10,
      ""MToughness"": 10,
      ""IPower"": 13,
      ""IToughness"": 13,
      ""OPower"": 129.5,
      ""Name"": ""Tutorial Zone""
    }
}
}
        ";

                using (var writer = new StreamWriter(File.Open(overridePath, FileMode.CreateNew)))
                {
                    writer.WriteLine(emptyZones);
                    writer.Flush();
                }
            }

            var overrides = new List<string>();

            try
            {
                var text = File.ReadAllText(overridePath);
                var parsed = JSON.Parse(text);
                var zones = parsed["zones"];

                foreach (var key in zones.Keys)
                {
                    var success = int.TryParse(key.Value, out var index);
                    if (!success)
                        continue;
                    Main.Log($"Key: {index}");
                    var stat = new ZoneStats
                    {
                        IPower = zones[key.Value]["IPower"].AsDouble,
                        IToughness = zones[key.Value]["IToughness"].AsDouble,
                        MPower = zones[key.Value]["MPower"].AsDouble,
                        MToughness = zones[key.Value]["MToughness"].AsDouble,
                        OPower = zones[key.Value]["OPower"].AsDouble,
                        Name = zones[key.Value]["Name"]
                    };
                    UserOverrides[index] = stat;
                    overrides.Add(stat.Name);
                }
            }
            catch (Exception e)
            {
                Main.Log(e.Message);
                Main.Log(e.StackTrace);
            }

            if (overrides.Count > 0)
                Main.Log($"Loaded Zone Overrides: {string.Join(", ", overrides.ToArray())}");
        }

        // "Farm-ready" total drop chance per zone, in percent (multiplier x100): the DC at which the
        // zone's REGULAR drops are all capped = 1 / smallest regular roll. Extracted from the game's
        // LootDrop.zone{N}Drop tables (rolls within 20x of the zone's most common roll; ultra-rare
        // specials like the 0.8% Ring of Apathy are excluded — capping those takes far more and is a
        // choice, not a baseline). Zones absent here (titan/safe/tutorial) have no advice.
        public static readonly Dictionary<int, double> RecommendedDcPercent = new Dictionary<int, double>
        {
            { 2, 1250 }, { 3, 834 }, { 5, 1667 }, { 7, 3334 }, { 9, 2000 }, { 10, 3334 },
            { 12, 10000 }, { 13, 20000 }, { 15, 50000 }, { 17, 2000000 }, { 18, 3333334 },
            { 20, 1250000 }, { 21, 5555556 }, { 22, 8333334 }, { 24, 16666667 }, { 25, 10000000 },
            { 27, 25000000 }, { 28, 55555556 }, { 29, 50000000 }, { 31, 1.25e9 }, { 32, 666666667 },
            { 33, 1.666666667e9 }, { 35, 2.5e9 }, { 36, 4e9 }, { 37, 6.25e9 }, { 39, 1e10 },
            { 40, 1.25e10 }, { 41, 1.6666666667e10 },
        };

        // Adventure attack measured WITHOUT the beast-mode multiplier — the conservative baseline
        // power that every zone one-shot / fight-type gate must agree on.
        public static float EffectiveAdvAttack()
        {
            var c = Main.Character;
            return c.totalAdvAttack() / Math.Max(1f, c.adventureController.beastModeBonus());
        }

        // FightType of one specific zone at current stats.
        //
        // ⚠ FAIL-CLOSED SINCE 2026-08-06 (audit/40 §4). This used to return 2 — the BEST fight type
        // — for a zone with no row, commented "so unknown zones never block progress". That is the
        // exact shape ZoneGate was built to close (ZoneGate.cs:7-23): a missing row read as
        // clearable at any attack, silently. Zone 43 hit it for real and won the Sadistic ranking
        // the moment its boss gate opened. The two rules now live in one file and agree.
        //
        // WHAT CHANGES. Both callers are UpdateFurthestZone (Main.cs), testing `== 0` to mean "the
        // ratcheted zone is not fightable at all, let it go". _furthestZone is only ever assigned
        // from GetBestZone(), which reads UserOverrides, so a MISSING ROW cannot be reached from
        // there; the reachable case is UserOverrides == null (the table has not loaded, or a
        // zoneOverride.json reload is mid-flight). Before: the stale ratchet was KEPT and adventured
        // on no data. Now it is released and routing falls through to normal ITOPOD routing until
        // the table answers. Fail-closed in the same direction as ZoneGate.
        public static int ZoneFightType(int zone)
        {
            bool tableLoaded = UserOverrides != null;
            ZoneStats st = null;
            bool rowFound = tableLoaded && UserOverrides.TryGetValue(zone, out st);

            int rowType = 0;
            if (rowFound)
                rowType = st.FightType(EffectiveAdvAttack(), Main.Character.totalAdvDefense());

            var d = ZoneGate.EvaluateFightType(tableLoaded, rowFound, rowType);
            if (!d.Known && ZoneGate.ShouldAnnounce("ZoneFightType", zone))
                Main.Log($"Zone {zone}: {d.Reason} — treating it as not clearable (fail-closed).");
            return d.FightType;
        }

        public static ZoneTarget GetBestZone()
        {
            if (UserOverrides == null)
                return null;

            float power = EffectiveAdvAttack();
            float toughness = Main.Character.totalAdvDefense();

            // Compute the reachable-zone ceiling once instead of once per zone inside the LINQ predicate
            int maxReachable = ZoneHelpers.GetMaxReachableZone(false);

            var fightType = 2;
            if (CombatHelpers.UltimateAttackUnlocked() && CombatHelpers.UltimateBuffUnlocked())
                fightType = 1;

            // Single pass: pick the highest-id zone that is both reachable and clearable at the required fight type
            int bestZoneId = int.MinValue;
            ZoneStats bestStats = null;
            foreach (var kvp in UserOverrides)
            {
                if (kvp.Key > maxReachable)
                    continue;
                if (kvp.Value.FightType(power, toughness) < fightType)
                    continue;
                if (kvp.Key > bestZoneId)
                {
                    bestZoneId = kvp.Key;
                    bestStats = kvp.Value;
                }
            }

            if (bestStats == null)
                return null;

            return new ZoneTarget
            {
                FightType = bestStats.FightType(power, toughness),
                Zone = bestZoneId
            };
        }


        // OPOWER = the adventure attack at which a zone becomes one-shottable. It gates every boost/gear
        // farm route (BoostFarmAdvisor, GearFarmAdvisor, ZoneFightType, GetBestZone).
        //
        // THE RULE, derived from the game. Every term is cited; nothing here is fitted to the old table.
        //   PlayerController.cs:231-234  minDamage() = 0, so Max(minDamage, baseDamage) IS baseDamage
        //   PlayerController.cs:236-239  baseDamage() = totalAdvAttack() - currentEnemy.defense / 2
        //   PlayerController.cs:287-290  regularAttack: dmg = baseDamage * regAttackMulti * Random(0.8,1.2)
        //   Adventure.cs:388             regAttackMulti = 1.5f  (idleAttackMulti = 1.2f, Adventure.cs:387)
        //   EnemyAI.cs:28,387            takeDamage floors the damage; defenseFactor = 1f by default
        //   AdventureController.cs:2386+ spawnEnemy returns enemyList[zone][n] UNSCALED. powerUp() is
        //                                ITOPOD-only (:2441), so the HP cited per row IS the live HP.
        // A guaranteed one-shot must survive the worst 0.8 roll, so for EVERY enemy e in the zone:
        //     (attack - e.defense/2) * 0.8 * 1.5 >= e.maxHP
        //     attack >= e.maxHP / 1.2 + e.defense / 2
        // and the zone's threshold is the MAX of that over the zone's whole enemy list:
        //
        //     OPower = ceil_4sf( max over e ( e.maxHP / 1.2 + e.defense / 2 ) )
        //
        // Rounding is UP at four significant figures because EnemyAI.takeDamage FLOORS the damage:
        // rounding down would park the gate a hair below the real requirement, i.e. fail OPEN.
        // Each row cites the enemy that BINDS the max, with its decomp line. That is not always the
        // highest-HP enemy: in zone 37 JIGSAW ties on HP and wins on defense.
        //
        // ⚠ THE /1.2 FORM IS THE *REGULAR ATTACK*. regAttackMulti applies unconditionally
        // (PlayerController.cs:289). The IDLE path uses Character.idleAttackPower()
        // (Character.cs:1663-1673) = regAttackMulti WITH the Ghost set complete, idleAttackMulti (1.2)
        // without — so a player idling without Ghost actually needs maxHP/0.96 + def/2, i.e. 25% MORE
        // than this table states. Wiring that live is the real fix and is deliberately NOT done here:
        // OPower is a field users override in zoneOverride.json, and changing its meaning would
        // silently reinterpret every override file already on disk.
        //
        // WHAT CHANGED 2026-08-03, and what the two previous passes each missed:
        //   - Zones 10-28 were maxHP/1.2 + def/2 at 3sf — the rule, correctly applied. Re-derived and
        //     kept, now at 4sf. Only zone 13 (+0.91%) and zone 22 (-0.31%) move at all.
        //   - Zones 29-41 were regenerated 2026-08-01 as maxHP/1.2 with NO def/2 term. That fixed the
        //     older "OPower = 2 x IToughness" wrong-column bug, but left all ten rows 0.6-1.3% LOW —
        //     still failing OPEN, just by less. The defense term is restored here.
        //   - Zones 0-9 assumed NO attack multiplier at all (maxHP/0.8 + def/2), making them ~50% too
        //     HIGH, i.e. failing CLOSED and hiding early zones. No attack path in the game has
        //     multiplier 1.0 except the parry riposte (PlayerController.cs:205-216), which is a
        //     reaction, not a farm cadence. These eight rows drop ~33% onto the one rule.
        //   - Zone 43 (7 Aethereal Seas) HAD NO ROW — 31 rows for 32 non-titan zones. Its absence made
        //     both farm advisors read it as one-shottable at ANY attack. It is DERIVED here from
        //     enemyList[43] (AdventureController.cs:2340-2360), not taken from the guide estimate.
        //
        // THE M/I COLUMNS ARE NOT DERIVABLE AND ARE NOT TOUCHED HERE. MPower/MToughness/IPower/
        // IToughness are measured "can I clear this zone" thresholds that emerge from the whole combat
        // loop (attack rate, enemy regen, your HP/regen/block/dodge) — not a closed form over any enemy
        // stat. Checked before concluding: IPower / maxEnemyDefense scatters from 1.44x to 5.56x across
        // the 31 known zones, and IToughness / maxEnemyAttack from 1.44x to 3.80x. No rule is present to
        // extract. They come from the wiki's measured table; audit/09 §7 records them as drifting.
        // ABSENT from the decomp — do not "derive" them. Zone 43's four are a labelled guide estimate.
        //
        // Titan zones (6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42) and the two final-boss zones
        // (44, 45) are correctly absent: their max HP is a titan, not a farm target.
        public static Dictionary<int, ZoneStats> Defaults = new Dictionary<int, ZoneStats>
        {
            {
                0, new ZoneStats
                {
                    MPower = 10,
                    MToughness = 10,
                    IPower = 13,
                    IToughness = 13,
                    // [DECOMP] AdventureController.cs:1950 "A SMALL MOUSE (BOSS)" hp=100 def=9
                    OPower = 87.84,
                    Name = "Tutorial Zone"
                }
            },
            {
                1, new ZoneStats
                {
                    MPower = 12,
                    MToughness = 12,
                    IPower = 21,
                    IToughness = 21,
                    // [DECOMP] AdventureController.cs:1956 "BROWN SLIME (BOSS)" hp=150 def=13
                    OPower = 131.5,
                    Name = "Sewers"
                }
            },
            {
                2, new ZoneStats
                {
                    MPower = 35,
                    MToughness = 35,
                    IPower = 53,
                    IToughness = 53,
                    // [DECOMP] AdventureController.cs:1963 "Zombie" hp=900 def=17
                    OPower = 758.5,
                    Name = "Forest"
                }
            },
            {
                3, new ZoneStats
                {
                    MPower = 150,
                    MToughness = 150,
                    IPower = 200,
                    IToughness = 200,
                    // [DECOMP] AdventureController.cs:1985 "CHAD (BOSS)" hp=3000 def=122
                    OPower = 2561,
                    Name = "Cave of Many Things"
                }
            },
            {
                4, new ZoneStats
                {
                    MPower = 600,
                    MToughness = 400,
                    IPower = 750,
                    IToughness = 650,
                    // [DECOMP] AdventureController.cs:1997 "BIRD PERSON (BOSS)" hp=9000 def=340
                    OPower = 7670,
                    Name = "The Sky"
                }
            },
            {
                5, new ZoneStats
                {
                    MPower = 700,
                    MToughness = 500,
                    IPower = 750,
                    IToughness = 750,
                    // [DECOMP] AdventureController.cs:2009 "SPIKY HAIRED GUY (BOSS)" hp=12000 def=440
                    OPower = 10220,
                    Name = "High Security Base"
                }
            },
            {
                7, new ZoneStats
                {
                    MPower = 3250,
                    MToughness = 2250,
                    IPower = 4500,
                    IToughness = 3000,
                    // [DECOMP] AdventureController.cs:2022 "SUNDAE (BOSS)" hp=85000 def=1720
                    OPower = 71700,
                    Name = "Clock Dimension"
                }
            },
            {
                9, new ZoneStats
                {
                    MPower = 4500,
                    MToughness = 3500,
                    IPower = 8000,
                    IToughness = 6000,
                    // [DECOMP] AdventureController.cs:2036 "SUPER HEXAGON (BOSS)" hp=133333 def=3133
                    OPower = 112700,
                    Name = "2D Universe"
                }
            },
            {
                10, new ZoneStats
                {
                    MPower = 12000,
                    MToughness = 10000,
                    IPower = 17000,
                    IToughness = 16000,
                    // [DECOMP] AdventureController.cs:2045 "GHOST DAD (BOSS)" hp=335000 def=7600
                    OPower = 283000,
                    Name = "Ancient Battlefield"
                }
            },
            {
                12, new ZoneStats
                {
                    MPower = 28000,
                    MToughness = 18000,
                    IPower = 48000,
                    IToughness = 38000,
                    // [DECOMP] AdventureController.cs:2059 "VIC (BOSS)" hp=1e6 def=18300
                    OPower = 842500,
                    Name = "A Very Strange Place"
                }
            },
            {
                13, new ZoneStats
                {
                    MPower = 125000,
                    MToughness = 60000,
                    IPower = 265000,
                    IToughness = 145000,
                    // [DECOMP] AdventureController.cs:2070 "DOCTOR WAHWEE (BOSS)" hp=4.2e6 def=63300
                    OPower = 3.532e6,
                    Name = "Mega Lands"
                }
            },
            {
                15, new ZoneStats
                {
                    MPower = 1300000,
                    MToughness = 550000,
                    IPower = 3000000,
                    IToughness = 2200000,
                    // [DECOMP] AdventureController.cs:2084 "A CLOGGED SHOWER DRAIN (BOSS)" hp=5.5e7 def=750000
                    OPower = 4.621e7,
                    Name = "Beardverse"
                }
            },
            {
                17, new ZoneStats
                {
                    MPower = 25000000,
                    MToughness = 15000000,
                    IPower = 45000000,
                    IToughness = 35000000,
                    // [DECOMP] AdventureController.cs:2101 "EVIL BADLY DRAWN KITTY" hp=1.06e9 def=1.15e7
                    OPower = 8.891e8,
                    Name = "Badly Drawn World"
                }
            },
            {
                18, new ZoneStats
                {
                    MPower = 180000000,
                    MToughness = 90000000,
                    IPower = 360000000,
                    IToughness = 270000000,
                    // [DECOMP] AdventureController.cs:2110 "An Army of Annoying Penguins" hp=8.6e9 def=9e7
                    OPower = 7.212e9,
                    Name = "Boring-Ass Earth"
                }
            },
            {
                20, new ZoneStats
                {
                    MPower = 7e10,
                    MToughness = 5e10,
                    IPower = 1.5e11,
                    IToughness = 9e10,
                    // [DECOMP] AdventureController.cs:2132 "MELTED CHOCOLATE BLOB (BOSS)" hp=3.25e12 def=3.05e10
                    OPower = 2.724e12,
                    Name = "Chocolate World"
                }
            },
            {
                21, new ZoneStats
                {
                    MPower = 1e13,
                    MToughness = 4.7e12,
                    IPower = 2.4e13,
                    IToughness = 1.6e13,
                    // [DECOMP] AdventureController.cs:2144 "EVIL SPIKY HAIRED GUY (BOSS)" hp=5.25e14 def=5.05e12
                    OPower = 4.401e14,
                    Name = "Evilverse"
                }
            },
            {
                22, new ZoneStats
                {
                    MPower = 5.4e13,
                    MToughness = 2.4e13,
                    IPower = 1.3e14,
                    IToughness = 9.7e13,
                    // [DECOMP] AdventureController.cs:2155 "TINKLES (BOSS)" hp=2.7e15 def=2.55e13
                    OPower = 2.263e15,
                    Name = "Pretty Pink Princess Land"
                }
            },
            {
                24, new ZoneStats
                {
                    MPower = 2.6e16,
                    MToughness = 1.2e16,
                    IPower = 4.5e16,
                    IToughness = 3.1e16,
                    // [DECOMP] AdventureController.cs:2172 "THE DRAGON OF DILDO (BOSS)" hp=1.25e18 def=1.15e16
                    OPower = 1.048e18,
                    Name = "Meta Land"
                }
            },
            {
                25, new ZoneStats
                {
                    MPower = 2.5e17,
                    MToughness = 1.1e17,
                    IPower = 4.8e17,
                    IToughness = 3.1e17,
                    // [DECOMP] AdventureController.cs:2182 "THE LIFE OF THE PARTY (BOSS)" hp=1.25e19 def=1.15e17
                    OPower = 1.048e19,
                    Name = "Interdimensional Party"
                }
            },
            {
                27, new ZoneStats
                {
                    MPower = 1.5e20,
                    MToughness = 6.8e19,
                    IPower = 2.7e20,
                    IToughness = 2.4e20,
                    // [DECOMP] AdventureController.cs:2199 "ELDER TYPO GOD, ELXU (BOSS)" hp=8.25e21 def=8.15e19
                    OPower = 6.916e21,
                    Name = "Typo Zonw"
                }
            },
            {
                28, new ZoneStats
                {
                    MPower = 7e20,
                    MToughness = 4e20,
                    IPower = 1.5e21,
                    IToughness = 1.1e21,
                    // [DECOMP] AdventureController.cs:2209 "DEMONIC FLURBIE (BOSS)" hp=4.25e22 def=4.15e20
                    OPower = 3.563e22,
                    Name = "The Fad-Lands"
                }
            },
            {
                29, new ZoneStats
                {
                    MPower = 4e21,
                    MToughness = 2e21,
                    IPower = 9e21,
                    IToughness = 6e21,
                    // [DECOMP] AdventureController.cs:2219 "TRUE FINAL (BOSS)" hp=2.25e23 def=2.15e21
                    OPower = 1.886e23,
                    Name = "JRPGVille"
                }
            },
            {
                31, new ZoneStats
                {
                    MPower = 3.2e24,
                    MToughness = 1.4e24,
                    IPower = 7.8e24,
                    IToughness = 5.2e24,
                    // [DECOMP] AdventureController.cs:2238 "RADIOACTIVE MACGUFFIN (BOSS)" hp=2.25e26 def=2.15e24
                    OPower = 1.886e26,
                    Name = "The Rad-Lands"
                }
            },
            {
                32, new ZoneStats
                {
                    MPower = 5e26,
                    MToughness = 2.5e26,
                    IPower = 1.75e27,
                    IToughness = 8.8e26,
                    // [DECOMP] AdventureController.cs:2248 "BELDING (BOSS)" hp=3.25e28 def=3.15e26
                    OPower = 2.725e28,
                    Name = "Back To School"
                }
            },
            {
                33, new ZoneStats
                {
                    MPower = 2.65e27,
                    MToughness = 8.26e26,
                    IPower = 8.85e27,
                    IToughness = 4.6e27,
                    // [DECOMP] AdventureController.cs:2258 "THE SHERIFF (BOSS)" hp=1.65e29 def=1.65e27
                    OPower = 1.384e29,
                    Name = "The West World"
                }
            },
            {
                35, new ZoneStats
                {
                    MPower = 1.79e29,
                    MToughness = 6.41e28,
                    IPower = 4.31e29,
                    IToughness = 2.44e29,
                    // [DECOMP] AdventureController.cs:2275 "A DAY-OLD BAGUETTE (BOSS)" hp=6e30 def=1.3e29
                    OPower = 5.065e30,
                    Name = "The Breadverse"
                }
            },
            {
                36, new ZoneStats
                {
                    MPower = 5.77e29,
                    MToughness = 1.17e29,
                    IPower = 1.07e30,
                    IToughness = 7.59e29,
                    // [DECOMP] AdventureController.cs:2285 "THE 'FRO (BOSS)" hp=2.06e31 def=4.1e29
                    OPower = 1.738e31,
                    Name = "That 70's Zone"
                }
            },
            {
                37, new ZoneStats
                {
                    MPower = 1.55e30,
                    MToughness = 5.51e29,
                    IPower = 3.84e30,
                    IToughness = 2.33e30,
                    // [DECOMP] AdventureController.cs:2295 "JIGSAW (BOSS)" hp=6.5e31 def=1.3e30
                    OPower = 5.482e31,
                    Name = "The Halloweenies"
                }
            },
            {
                39, new ZoneStats
                {
                    MPower = 5.24e31,
                    MToughness = 2.01e31,
                    IPower = 1.45e32,
                    IToughness = 8e31,
                    // [DECOMP] AdventureController.cs:2311 "THE CRANE (BOSS)" hp=2.1e33 def=4.3e31
                    OPower = 1.772e33,
                    Name = "Construction Zone"
                }
            },
            {
                40, new ZoneStats
                {
                    MPower = 1.28e32,
                    MToughness = 3.2e31,
                    IPower = 3.5e32,
                    IToughness = 2.7e32,
                    // [DECOMP] AdventureController.cs:2321 "A SINGLE GRAPE (BOSS)" hp=5.06e33 def=1.1e32
                    OPower = 4.272e33,
                    Name = "Duck Duck Zone"
                }
            },
            {
                41, new ZoneStats
                {
                    MPower = 3.15e32,
                    MToughness = 8.42e31,
                    IPower = 8.94e32,
                    IToughness = 6.03e32,
                    // [DECOMP] AdventureController.cs:2330 "THE GRAND DUTCH DUCHY" hp=1.3e34 def=2.68e32
                    OPower = 1.097e34,
                    Name = "The Nether Regions"
                }
            },
            {
                43, new ZoneStats
                {
                    // M/I: GUIDE ESTIMATE, not derived — [GUIDE lists/zone-list §Sadistic] via audit/22 §Q4.3:
                    // manual 17e33/6e33, idle 47e33/34e33. The decomp supplies no manual/idle threshold
                    // for any zone (see the M/I note above). OPower below IS derived.
                    MPower = 1.7e34,
                    MToughness = 6e33,
                    IPower = 4.7e34,
                    IToughness = 3.4e34,
                    // [DECOMP] AdventureController.cs:2355 "RAMSHACKLE SEA INN (BOSS)" hp=8.35e35 def=1.34e34
                    OPower = 7.026e35,
                    Name = "7 Aethereal Seas"
                }
            }
        };

    }

    public class ZoneTarget
    {
        public int Zone { get; set; }

        public int FightType { get; set; }
    }

    public class ZoneStats
    {
        public double MPower { get; set; }

        public double MToughness { get; set; }

        public double IPower { get; set; }

        public double IToughness { get; set; }

        public double OPower { get; set; }

        public string Name { get; set; }

        public int FightType(float attack, float def)
        {
            // 2 Means we can use fast combat
            // 1 means we need to precast buffs
            // 0 Means we cant do the zone
            if (attack > OPower)
                return 2;
            if (attack >= IPower && def >= IToughness)
                return 2;
            if (attack >= MPower && def >= MToughness)
                return 1;

            return 0;
        }
    }
}
