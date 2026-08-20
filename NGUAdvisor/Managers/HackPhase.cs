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

        // ── THE STANDING DEFAULT IS NOW PRICED, NOT NAMED ────────────────────────────────────────
        //
        // PostCBlock3 ends in a bare HACK-1 because the guide says "Focus Adv Hack as your default
        // to push". That was a reasonable reading of a guide written for a fresh post-CBlock3
        // board, and it is wrong on a matured one: HACK-1 has no target, so hitTarget is never true
        // and the lane never retires — Adventure keeps the whole pool for the rest of the run no
        // matter what it is worth.
        //
        // Measured on the live board 2026-08-18 (L153, 100K pool), Adventure ranked ELEVENTH of
        // fifteen at 0.081% per hour. The top lane was hack 11 at 5.92%/h — 73x — and hack 7 at
        // 2.37%/h. Adventure carries the game's second-smallest baseEffectPerLevel (0.001) and by
        // L153 its 1.0078^L ladder has multiplied its price 3.3x, so it is deep in diminishing
        // returns while two untouched lanes sit at level 0 with no ladder at all.
        //
        // WHY RANKING AND NOT A LONGER STATIC LIST. The obvious repair — seat MILEHACK-11 and
        // MILEHACK-7 ahead of HACK-1 — overshoots badly, because MILEHACK stops at the first
        // MILESTONE and the milestone is far past the point where the lane stops being worth it.
        // Hack 11's density falls below Adventure's at L26; its first milestone is L40, another
        // ~512 hours of pool spent below the incumbent to buy a 4% step worth 0.008%/h. Hack 7
        // crosses at L23 against a milestone at L50. A ranking has no stop level to get wrong: the
        // lane leaves the head the moment something else is worth more, which is the same condition
        // a hand-picked stop is trying to approximate.
        //
        // ⚠ THE GUIDE SWEEP IS UNTOUCHED. MILEHACK-2..6 still lead, still terminal, still in guide
        // order — that gate was validated in game 2026-08-13 and shipped in public 2.4.0, and its
        // "bounded lanes go FIRST" reasoning is unaffected by what the tail does. This replaces the
        // TAIL only.
        //
        // STABILITY. Density moves only when a level moves, so the order is constant between level
        // gains and there is nothing to thrash; ties break on id so equal densities cannot reorder
        // between ticks. R3 already in a hack is never lost when the head changes — the game keeps
        // hacks[id].progress, and nothing here writes hacks[id].target (MileHackBP's standing rule:
        // the stop is READ, never written).
        // ⚠ THE CALLER MUST NOT ENUMERATE hacks.hacks.Count. HacksController.properties carries a
        // SIXTEENTH row — the garbage-named slot 15 (audit/11 §F2) — whose baseEffectPerLevel is 1,
        // twenty times the largest real coefficient in the game (hack 11's 0.05), and whose
        // milestoneEffect of 1 makes its staircase inert. Nothing consumes its bonus. Priced, it
        // ranks roughly 300x above the true top lane, takes the head, and holds it forever feeding
        // a decoy. The 0..14 bound in the reader is HackBP.Unlocked()'s own `Index <= 14` rule and
        // exists for exactly this reason — it is load-bearing, not a lazy constant.
        public struct Candidate
        {
            public int Id;
            public double Density;      // HackMath.MarginalDensity (+ MilestoneStep if the caller wants it)
            public bool Eligible;       // false = locked, hard-capped, or unpriceable; never seated
        }

        // The default the tail falls back to when nothing can be priced — today's behaviour exactly,
        // so a failed read can never do worse than the bare list it replaces.
        public static readonly string[] UnpricedTail = { "HACK-1" };

        public static string[] RankedTail(IList<Candidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return UnpricedTail;

            var live = new List<Candidate>();
            foreach (var c in candidates)
                if (c.Eligible && c.Density > 0 && !double.IsNaN(c.Density) && !double.IsInfinity(c.Density))
                    live.Add(c);
            if (live.Count == 0) return UnpricedTail;

            live.Sort((a, b) =>
            {
                int byDensity = b.Density.CompareTo(a.Density);   // descending
                return byDensity != 0 ? byDensity : a.Id.CompareTo(b.Id);
            });

            var toks = new string[live.Count];
            for (int i = 0; i < live.Count; i++) toks[i] = "HACK-" + live[i].Id;
            return toks;
        }

        // PostCBlock3 with its bare-HACK-1 tail replaced by the priced order. Pre-CBlock3 is
        // untouched: that phase is A/D only by guide rule, and one lane cannot be ranked.
        public static string[] R3Tokens(bool postCBlock3, IList<Candidate> candidates)
        {
            if (!postCBlock3) return PreCBlock3;

            var tail = RankedTail(candidates);

            // Drop the static default BY VALUE, never by position. `PostCBlock3.Length - 1` would
            // silently delete whatever happens to sit last the day someone reorders that array or
            // appends to it, and the deletion would be invisible — the list still parses, the run
            // just quietly loses a lane. UnpricedTail names the token so the two cannot drift.
            var list = new List<string>(PostCBlock3.Length + tail.Length);
            foreach (var t in PostCBlock3)
            {
                bool isTheDefault = false;
                foreach (var d in UnpricedTail) if (t == d) { isTheDefault = true; break; }
                if (!isTheDefault) list.Add(t);
            }
            foreach (var t in tail) if (!list.Contains(t)) list.Add(t);
            return list.ToArray();
        }

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
