using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE GUIDE'S CH.5 HACK PHASE BOUNDARY, as data plus one predicate — Unity-free so the token
    // lists and the completion test can be pinned headlessly the way LaneTargets and HackMath are.
    //
    // The rows (audit/10 §A1.1, corrected by decision record amendment 09 §1 — sequencing, not
    // contradiction):
    //
    //   "Post-T7, run A/D Hack until completing CBlock 3"            → HACK-0        [GUIDE ch.5 §Hacks]
    //   "Post-CBlock3 ... Focus Adv Hack as your default to push"    → HACK-1        [GUIDE ch.5 §Hacks]
    //   "get the first milestone on Hacks 3-7 (TM-mNGU)"             → MILEHACK-2..6 [GUIDE ch.5 §Hacks]
    //
    // ⚠ THE INDEX TRAP, resolved once here (audit/10 §A1.0): the guide numbers hacks 1-BASED —
    // players count pods, the pod shows no index — so the guide's "Hacks 3-7" is Time Machine
    // through Magic NGU, decomp ids 2,3,4,5,6. The parenthetical "(TM-mNGU)" is what settles it.
    // Read 0-based it would be Drop Chance through Blood, which the parenthetical contradicts.
    //
    // WHY THE BOUNDED LANES COME FIRST. R3Breakpoints is an order-not-share waterfill whose head
    // lane self-limits only at saturation, and at this stage saturation exceeds the whole pool by
    // five orders of magnitude — the head lane takes everything. Put the unbounded default (HACK-1,
    // hitTarget never true with no target set) first and the milestone sweep behind it would never
    // receive a unit, forever. The sweep lanes are TERMINAL (MileHackBP reports done at the first
    // milestone), so leading with them costs a bounded, small detour — first milestones sit at
    // levels 20-50 minus reducers — after which every lane ahead of HACK-1 has dropped out and
    // Adventure takes the pool for the rest of the chapter. That is amendment 09's own reading:
    // "the hack is not demoted — it is completed, then deprioritised."
    //
    // ⚠ NEVER split the pool instead. prioCount stays 1 in the R3 lane by law: an even split can
    // push every share under the 2^-25 float stall floor, where a hack accumulates literally
    // nothing forever while the game's tooltip shows a finite countdown (HackMath.StallFloor).
    public static class HackPhase
    {
        // The campaign block whose completion is the guide's switch condition.
        public const string BlockId = "cblock3";

        // Until CBlock 3 completes: A/D only. ALLHACK funded hack 0 too — but by index accident,
        // and it dragged fourteen decoy lanes behind it. This is the same allocation, declared.
        public static readonly string[] PreCBlock3 = { "HACK-0" };

        // After: the first-milestone sweep (guide order, TM → mNGU), then Adventure as the
        // standing default. Ids 2-6 per the index note above.
        public static readonly string[] PostCBlock3 =
            { "MILEHACK-2", "MILEHACK-3", "MILEHACK-4", "MILEHACK-5", "MILEHACK-6", "HACK-1" };

        public static string[] R3Tokens(bool postCBlock3) => postCBlock3 ? PostCBlock3 : PreCBlock3;

        // Is every required chain ordinal of a leg already completed? `required` is
        // CampaignTables.LegRequirements (code -> highest required ordinal); `completions` is the
        // game's currentCompletions() per code — the CURRENT difficulty's counters, the only ones
        // the game exposes (CampaignTables.Status's own caveat). A code missing from `completions`
        // is unverifiable and reads as not done: the gate fails CLOSED, to the pre-CBlock3 list
        // that was also the status quo.
        public static bool ChainSatisfied(IDictionary<string, int> required,
                                          IDictionary<string, int> completions)
        {
            if (required == null || required.Count == 0) return false;   // nothing derivable — stay pre
            if (completions == null) return false;
            foreach (var kv in required)
            {
                if (!completions.TryGetValue(kv.Key, out var cur)) return false;
                if (cur < kv.Value) return false;
            }
            return true;
        }
    }
}
