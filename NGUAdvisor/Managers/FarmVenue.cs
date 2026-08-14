namespace NGUAdvisor.Managers
{
    // THE DC/PP VENUE LAW, as a decision. Unity-free, linked into the test project.
    //
    // Diggers 0 "Drops" and 8 "PP" are a near-interchangeable utility pair picked by VENUE
    // (OptimizationAdvisor's DIGGER LAWS). The law was implemented as:
    //
    //     if (titanWindow || hunting)  { DC in, PP out }
    //     else if (itopod)             { PP in, DC out }
    //     // no else
    //
    // where `hunting` is the MANUAL Gear Hunt toggle. The advisor's own gear/rare farm was not a
    // case, so a farm it chose itself fell into the gap and reordered nothing.
    //
    // ⚠ THE GAP WAS NOT HARMLESS, AND IT INVERTED. `itopod` reads Settings.SnipeZone >= 1000, so
    // while the advisor sat in the ITOPOD the law ran its PP branch and pushed digger 0 to the TAIL —
    // where the Take(slots) cut drops it. Observed live: the equipped set was 3,1,2,8,4,5,6,7,11,10,9
    // — every digger except 0. Then the rare farm moved routing to zone 20, `itopod` went false,
    // `hunting` was false, and NEITHER branch fired: digger 0 stayed cut, benched by a decision made
    // for a venue the character had already left, while the advisor committed to a ~54h drop farm.
    //
    // ── WHY PP IS THE ONE TO BENCH [OPERATOR 2026-08-05] ──────────────────────────────────────────
    // "since we'd be moving the user off the ITOPOD the PP digger can get dropped for it since there
    // is no PP awarded in adventure zones." Verified, with one exception the operator did not claim
    // and which is handled elsewhere:
    //   [DECOMP] AdventureController.cs:2919-2932 — the ENTIRE perk-point block is inside
    //     `if (zone == 1000)`. Ordinary adventure-zone kills award no PP at all. The rule holds.
    //   [DECOMP] LootDrop.cs:2374, 2943, 3384, 3964, 4519, 5041, 5563 — TITAN kills DO award PP
    //     (boss6PP..boss12PP), and titans live in adventure zones. So "no PP outside the ITOPOD" is
    //     not universally true. It does not need to be: titanWindow is already its own case above
    //     this one and wins, and SwapTitanDiggers owns the swap around the kill itself.
    //   [DECOMP] FruitController.cs:653 — Yggdrasil fruit also awards PP, on its own schedule.
    //
    // So benching PP is free while farming a NON-TITAN adventure zone, which is exactly when a drop
    // farm routes. It is not free in general, and this file does not claim otherwise.
    public static class FarmVenue
    {
        public const int DropChanceDigger = 0;
        public const int PerkPointDigger = 8;

        public enum Pays
        {
            // No venue signal: leave the pair where the mode ranking put them. This is the honest
            // answer for "we don't know", and it is NOT the same as the ITOPOD case.
            Unknown = 0,
            DropChance,
            PerkPoints
        }

        // A zone number the reader could not produce. NOT the ITOPOD and not a farm — Unknown, which
        // is the honest answer and is deliberately not the same as the ITOPOD case.
        public const int UnknownZone = int.MinValue;

        // The ITOPOD is a ZONE NUMBER, and one owner of it (ZonePhase.ItopodZone). Every ITOPOD floor
        // is >= 1000; ordinary zones and the Safe Zone (-1) are below it.
        public static bool AtItopod(int zone) => zone >= ZonePhase.ItopodZone;

        // titanWindow : a titan spawns within the swap window.
        // gearHunt    : the MANUAL Gear Hunt toggle owns routing.
        // dropFarm    : the advisor's OWN gear/rare farm is routing a zone for its drops.
        // currentZone : THE ZONE THE CHARACTER IS ACTUALLY IN (Character.adventure.zone), or
        //               UnknownZone when it could not be read.
        //
        // ⚠ A ZONE NUMBER, NOT A TOGGLE — audit/40 §3 item 7. This parameter used to be
        // `bool itopod`, and OptimizationAdvisor filled it from
        // `!hunting && (AdventureTargetITOPOD || SnipeZone >= 1000)` — two layer-1 INTENT fields plus
        // a hand copy of R10's gear-hunt row. audit/40 §0: layer 1 only WRITES Settings.SnipeZone,
        // layer 2 (Main.SnipeZone) decides what is adventured, and "most of what is written in layer
        // 1 is discarded, silently, by layer 2" — R3-R8 and R11-R13 all route somewhere the toggles
        // never named. This law is a claim about WHERE KILLS HAPPEN ([DECOMP]
        // AdventureController.cs:2919-2932 puts the entire perk-point block inside `if (zone ==
        // 1000)`), so it must be answered by the zone the character stands in, not by what something
        // wrote ten minutes ago. Taking the zone rather than a flag is what makes that unstatable in
        // the other direction: there is no bool-to-int conversion in C#, so the old expression cannot
        // be handed back to this method by accident.
        //
        // WHO WINS IS UNCHANGED. The four rows below are in the same order they have always been;
        // only the source of the last one moved.
        public static Pays Decide(bool titanWindow, bool gearHunt, bool dropFarm, int currentZone)
        {
            if (titanWindow || gearHunt) return Pays.DropChance;
            if (dropFarm) return Pays.DropChance;
            if (AtItopod(currentZone)) return Pays.PerkPoints;
            return Pays.Unknown;
        }

        // Which digger this venue wants promoted, and which benched. -1 for neither.
        public static int Promote(Pays p)
            => p == Pays.DropChance ? DropChanceDigger
             : p == Pays.PerkPoints ? PerkPointDigger
             : -1;

        public static int Bench(Pays p)
            => p == Pays.DropChance ? PerkPointDigger
             : p == Pays.PerkPoints ? DropChanceDigger
             : -1;

        // ── THE LIVE DEMAND ───────────────────────────────────────────────────────────────────────
        // Set by AdvisorApply when the gear or rare farm takes routing, read by the digger pass.
        //
        // A PLAIN FLAG, NOT A LATCH WITH A TIMEOUT: the farm re-decides on its own 10-minute throttle
        // and writes this every pass, so a stale `true` can only survive as long as the farm keeps
        // choosing to farm. It is cleared on the same paths that hand routing back.
        //
        // ⚠ ONE TICK OF LAG, BY CONSTRUCTION. ApplyDiggers runs BEFORE ApplyZones in the same tick
        // (AdvisorApply: diggers ~:256, zones ~:795), so a demand raised this tick is honoured on the
        // next digger pass. The digger pass is 30s and the zone pass is 10 minutes, so the lag is at
        // most one digger tick against a farm measured in hours. Not worth reordering the tick for.
        public static bool DropFarmActive;
    }
}
