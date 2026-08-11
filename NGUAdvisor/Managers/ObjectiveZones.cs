using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE OBJECTIVE LAYER'S ZONE TABLE — audit/23-objective-layer.md §3, transcribed. 33 zones across
    // three bands: 16 Normal, 8 Evil, 9 Sadistic. Primary source [GUIDE lists/zone-list]; unlock gates
    // from [GUIDE lists/boss-list] cross-checked against the zone-list's Boss # column; phase rules and
    // techniques from the chapters, CHAPTER-KEYED; ids from ZoneHelpers.ZoneList.
    //
    // ⚠ NOTHING CONSUMES THIS. Data plus a reader (ObjectiveReader); no live path calls either. In
    // particular this table does NOT feed the one-shot gate — ZoneGate/ZoneStatHelper.Defaults own
    // that, and they are decomp-derived. See OneHitPowerGuide below.
    //
    // ⚠ ONE-HIT POWER IS A CROSS-CHECK COLUMN, NOT A SOURCE. ZoneStatHelper.OPower derives all 32
    // non-titan thresholds from the decomp (maxEnemyHP/1.2 + def/2, ZoneStatHelper.cs:152-170). The
    // guide states a one-hit power for FOUR zones in total — 2 of 16 Normal (BAE, Choco), 2 of 8 Evil
    // (EV, PPPL), 0 of 9 Sadistic — and all four agree with the derived value. Those four are carried
    // here so a future edit that widens the gap is visible; they are NEVER the source. The existing
    // assertion lives at ZoneOPowerTests.Guide_one_hit_powers_stay_within_five_percent_of_the_derived
    // _value; ObjectiveZonesTests binds THIS table's copy of the four to the same parsed source.
    //
    // ⚠ TRACK IS THE ZONE'S BAND, AND THE UNLOCK GATE BINDS ONLY ON A RUN OF THAT SAME DIFFICULTY.
    // [GUIDE mechanics/general-info §Difficulties]: on EVIL, "All Normal adventure zones are unlocked
    // when adventure is unlocked"; on SADISTIC, "All Normal and Evil adventure zones are unlocked when
    // adventure is unlocked." So a Sadistic run has every Normal and Evil zone open from the start,
    // GATE-FREE. A consumer that reads Boss for a lower band on a higher-difficulty run reads a gate
    // that does not apply. See BandUnlocksFreelyOn.
    //
    // ⚠ BOTH SOURCES ARE ESTIMATES, so a disagreement with ZoneStatHelper is a disagreement, not an
    // error in either. The zone-list's own header says "All stats are CONSERVATIVE ESTIMATES. You can
    // always try to defeat enemies with lower P/T." 23 §3.5 records 16 field-by-field disagreements
    // with ZoneStatHelper.Defaults — nine of them Sadistic, three of those flagged by the guide itself
    // as its own worse estimate. That comparison is NOT duplicated here (23 §3.5 holds it, and
    // ZoneOPowerTests owns the OPower half); this table carries only what the guide states.
    //
    // Notation key, from [GUIDE lists/zone-list]: Trillion 1e12 · Quadrillion 1e15 · Quintillion 1e18 ·
    // Sextillion 1e21 · Septillion 1e24 · Octillion 1e27 · Nonillion 1e30 · Decillion 1e33.
    public static class ObjectiveZones
    {
        // ⟨BM⟩ is published for Evil and Sadistic only; the Normal band has ONE inline BM figure
        // (Choco). 0 in a BM field therefore means NOT PUBLISHED, not "no bonus" — the same
        // silence-is-not-a-zero rule the lane table runs on. Guard on HasBeastMode.
        public struct ZoneRow
        {
            public int Chapter;              // ObjectiveTable.ChapterAny when no chapter names it
            public int Id;                   // ZoneHelpers.ZoneList id
            public string Name;
            public TargetPass.Track Band;    // the zone's BAND, not the run's difficulty

            public string UnlockGate;        // the guide's own words
            public int UnlockBoss;           // the Boss # from the gate, 0 if none

            public bool HasStats;            // false for the Safe Zone, which has no P/T row at all
            public double ManualPower, ManualToughness;
            public double IdlePower, IdleToughness;
            public bool HasBeastMode;
            public double BeastModePower, BeastModeToughness;

            // The guide's stated one-hit power, and how many hits it is stated FOR (Choco is
            // published as a TWO-hit figure). 0 == the guide states none, which is 29 of 33 zones.
            public double OneHitPowerGuide;
            public int OneHitHits;

            public string SnipeStats;        // stated for 6 of 8 Evil zones and 1 Sadistic
            public string PhaseRule;
            public string Technique;
            public string Cite;

            // ⚠ THE GUIDE FLAGS ITS OWN NUMBER AS THE WORSE OF TWO SOURCES IT HOLDS. [GUIDE
            // lists/zone-list] marks Breadverse / 70's / Halloweenies IDLE (and ⟨BM⟩) with an
            // asterisk and says "See the following charts for BETTER CROWD-SOURCED ESTIMATES for
            // Idle". Those charts are PNGs and were NOT transcribed (23 §8).
            public bool IdleFlaggedUnreliableByGuide;

            // ⚠ ZONE 43 ONLY. Its four M/I figures are a LABELLED COMMUNITY ESTIMATE, never derived.
            // ZoneStatHelper.cs:584-590 carries the same four under the same label.
            public bool StatsAreCommunityEstimate;

            public bool HasOneHitPower { get { return OneHitPowerGuide > 0; } }

            // The one-hit figure normalised to a single hit, for comparison against a derived OPower.
            // Choco's 1.3e12 is published as a 2-HIT figure, so the one-shot equivalent is 2.6e12.
            public double OneHitAsOneShot
            {
                get { return OneHitPowerGuide * (OneHitHits <= 0 ? 1 : OneHitHits); }
            }
        }

        // On a run of this difficulty, every zone in the named band is open from the start and its
        // UnlockBoss does NOT apply ([GUIDE mechanics/general-info §Difficulties]).
        public static bool BandUnlocksFreelyOn(TargetPass.Track band, TargetPass.Track runDifficulty)
        {
            if (band == TargetPass.Track.Normal)
                return runDifficulty == TargetPass.Track.Evil ||
                       runDifficulty == TargetPass.Track.Sadistic;
            if (band == TargetPass.Track.Evil)
                return runDifficulty == TargetPass.Track.Sadistic;
            return false;
        }

        // =========================================================================================
        // §3.1 NORMAL band — 16 zones
        // =========================================================================================
        // ⚠ CH.1 AND CH.2 GIVE OPPOSITE PHASE RULES AND NEITHER RETRACTS THE OTHER. Ch.1 is "stay
        // until maxed", five times. Ch.2 is "snipe and move on, come back later." Under amendment 09
        // §5 this is SEQUENCING against chapter objectives, not a reversal — ch.2 states its reason
        // outright: "because progress is bottlenecked by adventure stats, it can be much faster to
        // obtain better gear and come back to max the other sets when you have better drop chance and
        // ENOUGH POWER/TOUGHNESS TO KILL ENEMIES IN ONE HIT." Ch.2's rule IS the one-hit deferral
        // rule, stated once, generally, then applied per-zone for the rest of the game. It is the
        // strongest phase rule in the guide because it OVERRIDES the per-zone rules of ch.1 and ch.3.
        public const string Ch2GlobalPhaseRule =
            "CH.2 GLOBAL (overrides the per-zone rule): \"the optimal strategy for this chapter is to " +
            "SNIPE ADVENTURE ZONES WHENEVER YOU CAN, THEN LEVEL/BOOST THAT GEAR SET JUST ENOUGH TO " +
            "SNIPE THE NEXT ADVENTURE ZONE... You can trash old gear sets for the inventory space, " +
            "you'll farm them all faster later\" [GUIDE ch.2 §Adventure Zones]. Opt-out: \"If you " +
            "prefer not to snipe, you can always choose to idle/max each set as you go... the " +
            "difference shouldn't be more than a few days.\" One ordering constraint: \"It's " +
            "recommended to at least DEFEAT T3 (JAKE) BEFORE GOING BACK TO FARM OLD ZONES\", as the " +
            "feature unlocked by T3 provides a strong drop chance modifier.";

        public static readonly ZoneRow[] Zones =
        {
            // ---- NORMAL ----------------------------------------------------------------------
            new ZoneRow { Chapter = 1, Id = -1, Name = "Safe Zone: Awakening Site",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 4", UnlockBoss = 4,
                HasStats = false,
                Technique = "no drops",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1]" },

            new ZoneRow { Chapter = 1, Id = 0, Name = "Tutorial Zone", Band = TargetPass.Track.Normal,
                UnlockGate = "Boss 4", UnlockBoss = 4, HasStats = true,
                ManualPower = 10, ManualToughness = 10, IdlePower = 13, IdleToughness = 13,
                PhaseRule = "\"STAY UNTIL YOU HAVE MAXED ALL 5 ITEMS IN THE TRAINING SET\"",
                Technique = "items drop at L10, so 10 of each maxes",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1 §Adventure Zones]" },

            new ZoneRow { Chapter = 1, Id = 1, Name = "Sewers", Band = TargetPass.Track.Normal,
                UnlockGate = "Boss 7", UnlockBoss = 7, HasStats = true,
                ManualPower = 12, ManualToughness = 12, IdlePower = 21, IdleToughness = 21,
                PhaseRule = "\"STAY UNTIL YOU'VE MAXED THE SEWERS SET AND THE TUTORIAL CUBE\"",
                Technique = "Cube -> Infinity Cube. Also the ch.7 BEUC unlock site (ctrl+click Nuts)",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1 §Adventure Zones]" },

            new ZoneRow { Chapter = 1, Id = 2, Name = "Forest", Band = TargetPass.Track.Normal,
                UnlockGate = "Boss 17", UnlockBoss = 17, HasStats = true,
                ManualPower = 35, ManualToughness = 35, IdlePower = 53, IdleToughness = 53,
                PhaseRule = "\"STAY UNTIL YOU'VE MAXED EVERYTHING BUT THE FOREST PENDANT\"",
                Technique = "FP drops with no stats; also drops in the next 3 zones. First Normal " +
                            "Bonus Accessory (Tuba of Time, item slot 432)",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1 §Adventure Zones]" },

            new ZoneRow { Chapter = 1, Id = 3, Name = "Cave of Many Things",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 37", UnlockBoss = 37,
                HasStats = true,
                ManualPower = 150, ManualToughness = 150, IdlePower = 200, IdleToughness = 200,
                PhaseRule = "\"STAY UNTIL YOU'VE MAXED THE CAVE SET\"",
                Technique = "drops Cheese Grater + Forest Pendant",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1 §Adventure Zones]" },

            new ZoneRow { Chapter = 1, Id = 4, Name = "The Sky", Band = TargetPass.Track.Normal,
                UnlockGate = "Boss 48", UnlockBoss = 48, HasStats = true,
                ManualPower = 600, ManualToughness = 400, IdlePower = 750, IdleToughness = 650,
                PhaseRule = "NO SET — \"Once you have enough stats to kill enemies quickly, The Sky " +
                            "is the BEST ADVENTURE ZONE TO FARM BOOSTS EARLY\"",
                Technique = "GUARANTEED PISSED OFF KEY on the first Sky boss (unlocks the ITOPOD). " +
                            "Also drops Wandoos 98, Looty McLootFace",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1 §Adventure Zones]" },

            new ZoneRow { Chapter = 1, Id = 5, Name = "High Security Base",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 58", UnlockBoss = 58,
                HasStats = true,
                ManualPower = 700, ManualToughness = 500, IdlePower = 750, IdleToughness = 750,
                PhaseRule = "\"You should MAX HSB, but you should be able to fight T1 with 10-20 " +
                            "levels of HSB gear\"",
                Technique = "SWITCH TO 1 H REBIRTHS HERE",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.1 §Adventure Zones]" },

            new ZoneRow { Chapter = 2, Id = 7, Name = "Clock Dimension",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 66", UnlockBoss = 66,
                HasStats = true,
                ManualPower = 3250, ManualToughness = 2250, IdlePower = 4500, IdleToughness = 3000,
                PhaseRule = Ch2GlobalPhaseRule,
                Technique = "magic-focused set; \"Clock weapon is very strong\"",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.2 §Adventure Zones]" },

            new ZoneRow { Chapter = 2, Id = 9, Name = "The 2D Universe",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 74", UnlockBoss = 74,
                HasStats = true,
                ManualPower = 4500, ManualToughness = 3500, IdlePower = 8000, IdleToughness = 6000,
                PhaseRule = Ch2GlobalPhaseRule,
                Technique = "\"the NEXT BEST BOOST ZONE AFTER SKY\"; \"focus all your boosts into " +
                            "the Infinity Cube until you reach Tier 1\"",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.2 §Adventure Zones]" },

            new ZoneRow { Chapter = 2, Id = 10, Name = "Ancient Battlefield",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 82", UnlockBoss = 82,
                HasStats = true,
                ManualPower = 12000, ManualToughness = 10000, IdlePower = 17000, IdleToughness = 16000,
                PhaseRule = Ch2GlobalPhaseRule,
                Technique = "SPOOPY SET IS THE AVSP SNIPE TOOL — \"it's faster to farm AB than wait " +
                            "2 hours per Jake spawn\"",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.2 §Adventure Zones]" },

            new ZoneRow { Chapter = 2, Id = 12, Name = "A Very Strange Place",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 90", UnlockBoss = 90,
                HasStats = true,
                ManualPower = 28000, ManualToughness = 18000, IdlePower = 48000, IdleToughness = 38000,
                PhaseRule = Ch2GlobalPhaseRule,
                Technique = "AUGMENT SWITCH POINT: Scissors -> Milk",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.2 §Adventure Zones]" },

            new ZoneRow { Chapter = 2, Id = 13, Name = "Mega Lands", Band = TargetPass.Track.Normal,
                UnlockGate = "Boss 100", UnlockBoss = 100, HasStats = true,
                ManualPower = 125000, ManualToughness = 60000,
                IdlePower = 265000, IdleToughness = 145000,
                PhaseRule = Ch2GlobalPhaseRule,
                Technique = "one boss, DOUBLE APPEARANCE CHANCE; \"specials are worse than AVSP/Jake " +
                            "gear\"",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.2 §Adventure Zones]" },

            new ZoneRow { Chapter = 3, Id = 15, Name = "The Beardverse",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 108", UnlockBoss = 108,
                HasStats = true,
                ManualPower = 1.3e6, ManualToughness = 550000, IdlePower = 2.5e6, IdleToughness = 1.8e6,
                PhaseRule = "\"push AT until ~2.5m Power/1.8m Toughness\" (⚠ the guide typos this as " +
                            "\"BREADVERSE idle stats\" — Breadverse is a SADISTIC zone; " +
                            "[GUIDE lists/zone-list §Normal] gives Beardverse idle as exactly " +
                            "2.5M/1.8M, so the intent is unambiguous — 23 §0.6, a live name-matching " +
                            "hazard); \"THE DROP CHANCE SUCKS. Beardverse base DC drops to 1%. Run " +
                            "NGU DC if needed\"",
                Technique = "\"You may require higher stats if you have not maxed Mysterious Red " +
                            "Liquid\"; USE AVSP/MEGA GEAR FOR NGU SPEED, Beardverse P/T only to " +
                            "kill T5; second digger here",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.3 §Beardverse]" },

            new ZoneRow { Chapter = 3, Id = 17, Name = "Badly Drawn World",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 116", UnlockBoss = 116,
                HasStats = true,
                ManualPower = 18e6, ManualToughness = 11e6, IdlePower = 45e6, IdleToughness = 35e6,
                PhaseRule = "\"SNIPE BDW -> IDLE BDW -> SNIPE BAE -> FIGHT T6\"; \"Once you are able " +
                            "to idle BDW, you can begin the LRB to T6\"",
                Technique = "terrible drop rates — \"if you have any LUCKY CHARMS, farming BDW/BAE " +
                            "is the best time to use them\"; WEAR THE BDW HELMET WHILE LEVELLING AT",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.3 §BDW/BAE]" },

            new ZoneRow { Chapter = 3, Id = 18, Name = "Boring-Ass Earth",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 124", UnlockBoss = 124,
                HasStats = true,
                ManualPower = 180e6, ManualToughness = 90e6, IdlePower = 360e6, IdleToughness = 270e6,
                OneHitPowerGuide = 7.2e9, OneHitHits = 1,
                PhaseRule = "ch.3: \"Farm as many levels of BDW gear as needed to reach BAE SNIPE " +
                            "STATS (~180m power)\", \"aim for 1-2% DC to snipe\"; ch.4: \"MAX BAE " +
                            "AT 1-SHOT POWER (7.2b) AND ~100k% DC, farm for 1 stealthiest\"",
                Technique = "STEALTHIEST ONLY DROPS AFTER THE BAE SET IS MAXED; \"Max Stealthiest " +
                            "at MAX DC (500k%) — can also use a Lucky Charm at 250k%\"; ~650-700m " +
                            "power to attempt T6",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.3 §BDW/BAE]; " +
                       "[GUIDE ch.4 §Post-v2]" },

            new ZoneRow { Chapter = 4, Id = 20, Name = "Chocolate World",
                Band = TargetPass.Track.Normal, UnlockGate = "Boss 137", UnlockBoss = 137,
                HasStats = true,
                ManualPower = 70e9, ManualToughness = 50e9, IdlePower = 150e9, IdleToughness = 90e9,
                HasBeastMode = true, BeastModePower = 400e9, BeastModeToughness = 170e9,
                OneHitPowerGuide = 1.3e12, OneHitHits = 2,
                PhaseRule = "\"Choco at Level 0 is WORSE THAN SLIME SET, so WAIT TO FARM UNTIL YOU " +
                            "CAN FARM QUICKLY\"; ch.4 order: \"Post-v3: Idle Choco when you can for " +
                            "the accessory\"",
                Technique = "FROM CHOCO ONWARD DROP CHANCE IS CUBE-ROOTED; maxing CW unlocks E/M " +
                            "Bar Bar items (\"max passively\") + a new quest item",
                Cite = "[GUIDE lists/zone-list §Normal]; [GUIDE ch.4 §Adventure Zones]" },

            // ---- EVIL --------------------------------------------------------------------------
            // ⭐ The Evilverse row carries the ONLY technique in the guide with a keystroke sequence.
            new ZoneRow { Chapter = 5, Id = 21, Name = "The Evilverse", Band = TargetPass.Track.Evil,
                UnlockGate = "Boss 58 in Evil", UnlockBoss = 58, HasStats = true,
                ManualPower = 10e12, ManualToughness = 5e12,
                IdlePower = 20e12, IdleToughness = 15e12,
                HasBeastMode = true, BeastModePower = 53e12, BeastModeToughness = 24e12,
                OneHitPowerGuide = 440e12, OneHitHits = 1,
                PhaseRule = "\"IDLE EV FOR ONE EACH OF THE ACCESSORIES, THEN WAIT UNTIL 1-HIT TO " +
                            "FARM EV GEAR FOR SET BONUS\"; \"Once you have one of each, you can " +
                            "IGNORE EV UNTIL POST-T7\"",
                Technique = "⭐ THE EXPLODER SNIPE: \"snipe the exploder before the other enemies to " +
                            "raise your TM GPS... Exploders explode on a timer, so WITH ENOUGH " +
                            "BLOCK AT YOU CAN SURVIVE. Wearing MAX MOVE CD GEAR, start the fight " +
                            "with OFFENSIVE BUFF, use buff timer to block. BLOCK WHEN OFFENSIVE " +
                            "BUFF TIMER HITS 16s -> 3s -> 24s -> 11s, refresh buff ASAP.\" Terminal " +
                            "for the AT lane: \"snipe EV exploder (5-7T pow)\"",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.5 §Early Evil]" },

            new ZoneRow { Chapter = 5, Id = 22, Name = "Pretty Pink Princess Land",
                Band = TargetPass.Track.Evil, UnlockGate = "Boss 100 in Evil", UnlockBoss = 100,
                HasStats = true,
                ManualPower = 54e12, ManualToughness = 24e12,
                IdlePower = 130e12, IdleToughness = 80e12,
                HasBeastMode = true, BeastModePower = 270e12, BeastModeToughness = 130e12,
                OneHitPowerGuide = 2.27e15, OneHitHits = 1,
                PhaseRule = "\"SIMILARLY IDLE PPPL FOR ACCESSORIES, THEN 1-HIT FARM FOR GEAR/SET " +
                            "BONUS\"",
                Technique = "\"PPPL ALSO HAS AN EXPLODER that can be sniped for an earlier GPS " +
                            "increase\". ⚠ ch.5 spells it \"idle PPL\" once",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.5 §Early Evil]" },

            new ZoneRow { Chapter = 5, Id = 24, Name = "Meta Land", Band = TargetPass.Track.Evil,
                UnlockGate = "Boss 158 in Evil", UnlockBoss = 158, HasStats = true,
                ManualPower = 26e15, ManualToughness = 12e15,
                IdlePower = 45e15, IdleToughness = 31e15,
                HasBeastMode = true, BeastModePower = 115e15, BeastModeToughness = 55e15,
                SnipeStats = "26e15/12e15",
                PhaseRule = "\"Can SNIPE FOR A FEW ACCESSORIES, MAINLY THE EXPONENTIAL, which has " +
                            "good DC and E/M\"; T8 LRB day 1: \"04:00-21:00: FARM META with full " +
                            "NGU gear\"; day 2+: \"IDLE META -> SNIPE IDP -> IDLE IDP -> 1-SHOT MAX " +
                            "META -> IDLE IDP UNTIL T8 KILL\"",
                Technique = "LRB start gate: \"around Boss 166 and Meta Idle / Manual 7v3 stats " +
                            "(90-100e15 power)\"",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.5 §LRB to T8]" },

            new ZoneRow { Chapter = 5, Id = 25, Name = "Interdimensional Party",
                Band = TargetPass.Track.Evil, UnlockGate = "Boss 166 in Evil", UnlockBoss = 166,
                HasStats = true,
                ManualPower = 250e15, ManualToughness = 110e15,
                IdlePower = 480e15, IdleToughness = 310e15,
                HasBeastMode = true, BeastModePower = 1.45e18, BeastModeToughness = 550e15,
                SnipeStats = "250e15/110e15",
                PhaseRule = "\"Gear is decent outside of accs, but will quickly get outclassed by " +
                            "T8 gear. MOSTLY USED FOR P/T TO SNIPE T8\"",
                Technique = "\"Wear D20, Tutu, Voodoo Doll while farming to help with eBeard growth\"",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.5 §LRB to T8]" },

            new ZoneRow { Chapter = 6, Id = 27, Name = "Typo Zonw", Band = TargetPass.Track.Evil,
                UnlockGate = "Boss 174 in Evil", UnlockBoss = 174, HasStats = true,
                ManualPower = 150e18, ManualToughness = 68e18,
                IdlePower = 270e18, IdleToughness = 240e18,
                HasBeastMode = true, BeastModePower = 880e18, BeastModeToughness = 400e18,
                SnipeStats = "150e18/68e18",
                PhaseRule = "\"Your goal is to SNIPE UNTIL YOU HAVE AT LEAST 1 OF EACH ITEM... Every " +
                            "item except the Chest is useful for CBlock/HD\"; then \"CHILL IN " +
                            "ITOPOD UNTIL YOU CAN 2-3 SHOT TYPO, THEN MAX TYPO\"",
                Technique = "Ordering reason: \"Why should I snypo before CBlock4? Typo gear is " +
                            "useful for CBlock4... STARTING CHALLENGES WILL ALSO RESET YOUR AT / " +
                            "TEMP BEARDS / BANKS, meaning you'd have to climb back up\". T8v1 -> " +
                            "Typo is ~1 MONTH OF 24 H REBIRTHS — the longest wall in the game",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.6 §Typo Wall]" },

            new ZoneRow { Chapter = 6, Id = 28, Name = "The Fad-lands", Band = TargetPass.Track.Evil,
                UnlockGate = "Boss 182 in Evil", UnlockBoss = 182, HasStats = true,
                ManualPower = 700e18, ManualToughness = 400e18,
                IdlePower = 1.6e21, IdleToughness = 1.1e21,
                HasBeastMode = true, BeastModePower = 4e21, BeastModeToughness = 2e21,
                SnipeStats = "700e18/400e18",
                PhaseRule = "\"After sniping Fad-lands gear, CHILL IN ITOPOD UNTIL YOU CAN 2-3 SHOT " +
                            "TYPO, THEN MAX TYPO\"",
                Technique = "Weapon \"used until Early Sad\"",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.6 §The Fad-lands]" },

            new ZoneRow { Chapter = 6, Id = 29, Name = "JRPGVille", Band = TargetPass.Track.Evil,
                UnlockGate = "Boss 190 in Evil", UnlockBoss = 190, HasStats = true,
                ManualPower = 4e21, ManualToughness = 2e21, IdlePower = 8e21, IdleToughness = 6e21,
                HasBeastMode = true, BeastModePower = 20e21, BeastModeToughness = 14e21,
                SnipeStats = "4e21/2e21",
                PhaseRule = "\"After sniping one of each JRPG gear, MAX FAD GEAR, then start HD2 / " +
                            "LRB to T9\"",
                Technique = "\"With max Fad gear, you can SKIP SNIPING THE LEGS/BOOTS/WEAPON from " +
                            "JRPG if desired\"",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.6 §T9 LRB]" },

            // ⭐ 23 §3.2 calls this the zone table's most useful single finding.
            new ZoneRow { Chapter = 7, Id = 31, Name = "The Rad-Lands", Band = TargetPass.Track.Evil,
                UnlockGate = "Boss 200 in Evil", UnlockBoss = 200, HasStats = true,
                ManualPower = 3.2e24, ManualToughness = 1.4e24,
                IdlePower = 9.1e24, IdleToughness = 5.6e24,
                HasBeastMode = true, BeastModePower = 24e24, BeastModeToughness = 12e24,
                PhaseRule = "⛔ EXPLICIT ANTI-SNIPE: \"RAD DC IS VERY LOW, MAKING SNIPING NOT WORTH " +
                            "THE EFFORT, SO LRB GETS TO IDLE STATS FASTER... STAY IN LRB UNTIL RAD " +
                            "MAX, then once gear is boosted, start RadDay\". The ONLY zone in the " +
                            "guide with an explicit instruction NOT to snipe, and the reason is a " +
                            "DROP CHANCE property, not a stat threshold — any advisor that routes " +
                            "on P/T alone will send a player to snipe Rad and they will get nothing",
                Technique = "\"If you do decide to snipe, put the MIXTAPE IN DAYCARE to get the " +
                            "Evil Acc set bonus\"",
                Cite = "[GUIDE lists/zone-list §Evil]; [GUIDE ch.7 §Adventure Zones · Rad LRB]" },

            // ---- SADISTIC ----------------------------------------------------------------------
            // ⚠ Ch.8's zone section is organised BY TITAN, not by zone ("Adapted from Deceptive
            // Thinker's SAD strategy") — which is why zone 43 falls out of the chapters entirely.
            new ZoneRow { Chapter = 8, Id = 32, Name = "Back To School",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 125 in Sad", UnlockBoss = 125,
                HasStats = true,
                ManualPower = 500e24, ManualToughness = 250e24,
                IdlePower = 1.6e27, IdleToughness = 940e24,
                HasBeastMode = true, BeastModePower = 4.4e27, BeastModeToughness = 1.4e27,
                PhaseRule = "\"SNIPE BTS FOR GOLD, BUT SKIP FARMING BTS GEAR UNTIL T10 LRB\"; " +
                            "\"After maxing BTS gear and obtaining Corgi, DO A SNUGDAY\"",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 33, Name = "The West World",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 150 in Sad", UnlockBoss = 150,
                HasStats = true,
                ManualPower = 2.65e27, ManualToughness = 830e24,
                IdlePower = 8e27, IdleToughness = 3.5e27,
                HasBeastMode = true, BeastModePower = 19e27, BeastModeToughness = 9e27,
                SnipeStats = "2.65e27/830e24",
                PhaseRule = "\"LRB TO T10 STARTING FROM WW SNIPE STATS (2.65e27/830e24). START WITH " +
                            "A HACKDAY... Can extend the LRB to max WW and getting a single secret " +
                            "drop to throw into daycare\"",
                Technique = "\"23h muff rbs from entry cblock to West World snipe\"",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 35, Name = "The Breadverse",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 208 in Sad", UnlockBoss = 208,
                HasStats = true,
                ManualPower = 140e27, ManualToughness = 24e27,
                IdlePower = 430e27, IdleToughness = 240e27,
                HasBeastMode = true, BeastModePower = 1.2e30, BeastModeToughness = 270e27,
                IdleFlaggedUnreliableByGuide = true,
                PhaseRule = "\"SNIPE BREAD/70s MOSTLY FOR GOLD, sets aren't worth using until you " +
                            "can idle with BM\"",
                Technique = "⚠ the guide asterisks its own IDLE and ⟨BM⟩ figures here and defers to " +
                            "crowd-sourced PNG charts it holds but which were NOT transcribed (23 §8)",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 36, Name = "That 70's Zone",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 216 in Sad", UnlockBoss = 216,
                HasStats = true,
                ManualPower = 510e27, ManualToughness = 75e27,
                IdlePower = 1.5e30, IdleToughness = 650e27,
                HasBeastMode = true, BeastModePower = 4e30, BeastModeToughness = 1e30,
                IdleFlaggedUnreliableByGuide = true,
                PhaseRule = "as Breadverse — \"Snipe Bread/70s mostly for gold\"",
                Technique = "\"2K ADVENTURE GUFF is a good point to start T11 LRB (~halfway between " +
                            "bread and 70s)\". ⚠ idle and ⟨BM⟩ asterisked by the guide",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 37, Name = "The Halloweenies",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 224 in Sad", UnlockBoss = 224,
                HasStats = true,
                ManualPower = 1.5e30, ManualToughness = 400e27,
                IdlePower = 3.4e30, IdleToughness = 2.6e30,
                HasBeastMode = true, BeastModePower = 13e30, BeastModeToughness = 2e30,
                IdleFlaggedUnreliableByGuide = true,
                PhaseRule = "\"DON'T BOTHER WITH HALLOWEENIES UNTIL POST-T11, EXTEND LRB TO MAX THE " +
                            "SET. Set bonus is ridiculously good, worth taking a few days to knock " +
                            "out\"",
                Technique = "supplies a PAPER ITEM for the T11 fight. ⚠ idle and ⟨BM⟩ asterisked by " +
                            "the guide",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 39, Name = "Construction Zone",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 232 in Sad", UnlockBoss = 232,
                HasStats = true,
                ManualPower = 52e30, ManualToughness = 20e30,
                IdlePower = 285e30, IdleToughness = 75e30,
                HasBeastMode = true, BeastModePower = 625e30, BeastModeToughness = 90e30,
                PhaseRule = "\"Construction gear will be quickly outclassed by Duck / Nether, but " +
                            "useful for now. STILL NOT WORTH SNIPING, WAIT UNTIL IDLE\"",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 40, Name = "DUCK DUCK ZONE",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 240 in Sad", UnlockBoss = 240,
                HasStats = true,
                ManualPower = 128e30, ManualToughness = 32e30,
                IdlePower = 670e30, IdleToughness = 180e30,
                HasBeastMode = true, BeastModePower = 1.2e33, BeastModeToughness = 220e30,
                PhaseRule = "\"DUCK / NETHER ARE THE REAL REASONS FOR THE T12 LRB, SNIPE AS SOON AS " +
                            "POSSIBLE. Gear will be used through the end of the game\"",
                Technique = "\"T12 gear is trash, accessories are good\"",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            new ZoneRow { Chapter = 8, Id = 41, Name = "The Nether Regions",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 248 in Sad", UnlockBoss = 248,
                HasStats = true,
                ManualPower = 315e30, ManualToughness = 84e30,
                IdlePower = 1.7e33, IdleToughness = 440e30,
                HasBeastMode = true, BeastModePower = 2.9e33, BeastModeToughness = 500e30,
                PhaseRule = "\"EXTEND LRB TO MAX NETHER AFTER T12\"",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE ch.8]" },

            // ⚠ ZONE 43. NO PHASE RULE ANYWHERE — the zone exists only in
            // [GUIDE lists/zone-list §Sadistic] and ch.8 NEVER NAMES IT. Its four M/I figures are a
            // LABELLED COMMUNITY ESTIMATE, never derived: the decomp supplies no manual/idle
            // threshold for any zone (they are measured "can I clear this" values that emerge from
            // the whole combat loop — ZoneStatHelper.cs:197-203 checked and found no rule to
            // extract). This is the row whose ABSENCE from ZoneStatHelper.Defaults made both farm
            // advisors read zone 43 as one-shottable at any attack (01 line 253, a fail-OPEN);
            // ZoneStatHelper.cs:582-593 now carries it under the same estimate label, with a
            // DERIVED OPower. ZoneHelpers.cs:57 names the id "7 Aethereal Seas".
            new ZoneRow { Chapter = ObjectiveTable.ChapterAny, Id = 43, Name = "The Aethereal Sea",
                Band = TargetPass.Track.Sadistic, UnlockGate = "Boss 269 in Sad", UnlockBoss = 269,
                HasStats = true,
                ManualPower = 17e33, ManualToughness = 6e33,
                IdlePower = 47e33, IdleToughness = 34e33,
                HasBeastMode = true, BeastModePower = 122e33, BeastModeToughness = 54e33,
                StatsAreCommunityEstimate = true,
                PhaseRule = "⚠ NO PHASE RULE ANYWHERE. The zone exists only in " +
                            "[GUIDE lists/zone-list §Sadistic]; ch.8 never names it",
                Technique = "⚠ PROVENANCE: PLAYER CONSENSUS, not a decomp extraction. The guide " +
                            "supplies four of the five fields the missing ZoneStatHelper row needed " +
                            "and states NO one-hit power for this or ANY Sadistic zone. " +
                            "ZoneHelpers.cs:57 names it \"7 Aethereal Seas\"; Pirate Set",
                Cite = "[GUIDE lists/zone-list §Sadistic]; [GUIDE lists/boss-list §Sadistic]; " +
                       "23 §3.6" },
        };

        // ---- queries -----------------------------------------------------------------------------

        public static IList<ZoneRow> ZonesFor(int chapter, TargetPass.Track band)
        {
            var hits = new List<ZoneRow>();
            for (int i = 0; i < Zones.Length; i++)
            {
                var z = Zones[i];
                if (!ObjectiveTable.ChapterMatches(z.Chapter, chapter))
                    continue;
                if (z.Band != band)
                    continue;
                hits.Add(z);
            }
            return hits;
        }

        public static bool TryGetZone(int id, out ZoneRow row)
        {
            for (int i = 0; i < Zones.Length; i++)
            {
                if (Zones[i].Id == id)
                {
                    row = Zones[i];
                    return true;
                }
            }
            row = default(ZoneRow);
            return false;
        }

        // The four zones for which the guide independently states a one-hit power. A cross-check
        // list, not a source — see the header.
        public static IList<ZoneRow> ZonesWithGuideOneHitPower()
        {
            var hits = new List<ZoneRow>();
            for (int i = 0; i < Zones.Length; i++)
            {
                if (Zones[i].HasOneHitPower)
                    hits.Add(Zones[i]);
            }
            return hits;
        }

        // =========================================================================================
        // §3.7 THE ITOPOD — rules, but NO P/T row, so it is deliberately not a ZoneRow.
        // =========================================================================================
        // ⚠ THE OPTIMAL-FLOOR BUG IS AN ADVISOR-LEVEL HAZARD, NOT A TRIVIA ITEM. An advisor computing
        // "can I one-shot this floor" and an advisor reading the game's Optimal Floor are NOT
        // computing the same thing: the game's figure assumes the Spoopy (Ancient Battlefield) set
        // bonus at max and is LOW BY UP TO 4 FLOORS before that set exists. The guide's one computable
        // stop-farming condition in the whole corpus ("chill in ITOPOD until you can 2-3 shot Typo")
        // is precisely the kind of calculation this bug attacks.
        public struct ItopodRule
        {
            public string Field;
            public string Rule;
            public string Cite;
        }

        public static readonly ItopodRule[] Itopod =
        {
            new ItopodRule { Field = "Unlock",
                Rule = "consume the PISSED OFF KEY (guaranteed on the first Sky boss killed)",
                Cite = "[GUIDE mechanics/adventure §ITOPOD]" },
            new ItopodRule { Field = "Size",
                Rule = "1600 floors; 10 CONSECUTIVE KILLS unlocks the next; each floor 1.05x the " +
                       "previous",
                Cite = "[GUIDE mechanics/adventure §ITOPOD]" },
            new ItopodRule { Field = "Start/End floor",
                Rule = "\"Your Start Floor cannot be higher than one lower than your highest reached " +
                       "floor\"; End <= 1600",
                Cite = "[GUIDE mechanics/adventure §ITOPOD]" },
            new ItopodRule { Field = "PP per kill",
                Rule = "(200 + floor) Normal -> (700 + floor) Evil -> (2000 + floor) Sadistic; " +
                       "1,000,000 progress points = 1 PP",
                Cite = "[GUIDE mechanics/adventure §ITOPOD Rewards]" },
            new ItopodRule { Field = "Milestones",
                Rule = "floors 10...90 = 1 PP, 100 = 10 PP; 110...190 = 2 PP, 200 = 20 PP; \"then " +
                       "increases by 1 for next\"",
                Cite = "[GUIDE mechanics/adventure §ITOPOD Rewards]" },
            new ItopodRule { Field = "Phase rule — ch.1/2",
                Rule = "\"In the early game (pre-T4/Chapter 3), it's NOT ADVISED to spend time " +
                       "farming ITOPOD — without significant PP modifiers, respawn, and power, your " +
                       "PP gain is negligible.\" CLIMB ONLY (\"set start floor to 0, end floor to " +
                       "100\")",
                Cite = "[GUIDE ch.1 §ITOPOD]" },
            new ItopodRule { Field = "Phase rule — ch.3+",
                Rule = "⚠ THE RULE FLIPS SIGN HERE. \"Later, it's typical to farm ITOPOD AT OPTIMAL " +
                       "FLOOR BETWEEN ADVENTURE ZONES\"; \"If you don't have gear to farm, farm PP " +
                       "at your optimal floor\"",
                Cite = "[GUIDE ch.3 §Adventure Zones]" },
            new ItopodRule { Field = "Gear rule",
                Rule = "\"while farming pod, the ONLY TWO STATS that affect PP output are POWER AND " +
                       "RESPAWN\"; \"From Evil onwards, a general rule of thumb is 1/3 OF TOTAL ACC " +
                       "SLOTS = RESPAWN\"; RoG always, + Stapler at 7 acc slots, + Green Heart at 9",
                Cite = "[GUIDE mechanics/adventure §Farming ITOPOD]" },
            new ItopodRule { Field = "Boosts",
                Rule = "\"Boosts drop at a flat 14% DC, NOT AFFECTED BY DC MODIFIERS/GEAR\" — \"Can " +
                       "max out boosts in ITOPOD since you only need half the boost drops\"",
                Cite = "[GUIDE mechanics/adventure §Farming ITOPOD]" },
            new ItopodRule { Field = "⚠ KNOWN BUG",
                Rule = "\"OPTIMAL FLOOR CALCULATION ASSUMES SPOOPY SET MAX BONUS, so before getting " +
                       "that set, sometimes it will place you 4 FLOORS LOWER than your highest " +
                       "1-shot floor\"",
                Cite = "[GUIDE mechanics/adventure §Farming ITOPOD]" },
            new ItopodRule { Field = "Lazy ITOPOD Shifter",
                Rule = "AP purchase, recomputes optimal floor after every kill — \"IF TURNED ON, " +
                       "THIS WILL PREVENT YOU FROM CLIMBING FLOORS. Must be turned off to climb\"",
                Cite = "[GUIDE mechanics/general-info §Settings]" },
        };
    }
}
