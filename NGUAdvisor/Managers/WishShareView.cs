namespace NGUAdvisor.Managers
{
    // WHAT THE WISH PASS TOOK OF ENERGY AND MAGIC — the missing lane on the "where the pool went" board.
    // Unity-free, linked into the test project.
    //
    // ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
    // The board answers "where did the pool go" for Energy and Magic by drawing the constraint layer's
    // lanes. The wish pass is not one of them: WishManager.Allocate runs as its OWN step AFTER the
    // E/M/R3 swaps (CustomAllocation.cs "Wishes (share of remaining idle)"), outside the layer
    // entirely. So the single largest consumer of energy on a mature save was drawn as nothing at all —
    // it appeared only inside the residue, as an OFFER.
    //
    // That gap is not cosmetic. Measured on a 686-hour end-game save, 2026-08-18: wishes were holding
    // ~8.1e18 energy, about 90% of the game's 9e18 cap, while the board showed the pool draining with
    // no destination for it. The operator's own words on seeing the deployed board: "I don't see wishes
    // in the E or M chips, but there is allocation for it."
    //
    // ── OFFER IS NOT TAKE, AND ONLY THE TAKE ANSWERS THE QUESTION ─────────────────────────────────
    // ConstraintLayer.Plan.Unallocated is computed DURING the swap, before this pass runs, so it is
    // what wishes were OFFERED. A board built on it cannot tell a 100% take from a 0% take — both draw
    // an identical bar. The take is only knowable inside Allocate(), by differencing the running
    // remainder, and it cannot be reconstructed afterwards: the pool regenerates continuously and the
    // next swap's reclaim moves it again.
    //
    // R3's equivalent lives in R3PoolView because R3 composes its WHOLE pool there (hacks + wishes +
    // idle, with no constraint-layer plan to attach to). Energy and magic already have a plan; they
    // need one lane appended to it, not a pool composed. Hence two small classes rather than one that
    // does both jobs badly.
    internal static class WishShareView
    {
        public struct Share
        {
            /// <summary>What the sliders made available to wishes this pass.</summary>
            public long Offered;
            /// <summary>What the wish slots actually consumed of it.</summary>
            public long Taken;
            /// <summary>
            /// The whole idle pool at the moment the wish pass ran — AFTER it released last tick's
            /// holdings back into idle. This is the denominator the take is a share of.
            /// </summary>
            /// <remarks>
            /// ⚠ THIS IS NOT ConstraintLayer.Plan.Pool, AND THE GAP IS ENORMOUS. The plan's pool is
            /// measured during the swap, while the wish slots are still HOLDING last tick's resources —
            /// so those holdings are not idle and not in it. Allocate() then calls removeAllResources()
            /// and they land back in idle before the take is measured.
            ///
            /// Drawing the take as a share of the plan's pool therefore reports well over 100%: observed
            /// ~300% on an end-game save where the lanes were working from ~1e16 while the wish slots
            /// were sitting on ~8e18. The board must widen its denominator to the whole resource in
            /// play, not scale the bar down to fit.
            /// </remarks>
            public long IdleAtPass;
            /// <summary>True once a pass has recorded; distinguishes "took nothing" from "never ran".</summary>
            public bool Recorded;

            /// <summary>Offered but not consumed — genuinely idle, and NOT reclaimed by the next swap.</summary>
            public long Untaken { get { return Offered > Taken ? Offered - Taken : 0L; } }

            /// <summary>
            /// What the board should use as the pool: everything the lanes committed, plus everything
            /// that was idle when the wish pass ran. Closes exactly against lanes + wishes + idle.
            /// </summary>
            /// <param name="planPool">
            /// The plan's own pool. The result is never smaller than this, which is what makes the
            /// board safe: every constraint lane's take is a share of the plan pool by construction,
            /// so a denominator below it can render a lane above 100%.
            /// </param>
            /// <remarks>
            /// ⚠ THE TWO SUMMANDS ARE READ AT DIFFERENT MOMENTS AND CAN DISAGREE. `laneAllocated` comes
            /// from the swap; `IdleAtPass` from the wish pass at the end of the same tick — and the
            /// snapshot that pairs them runs on its own timer, so a swap landing between the two reads
            /// pairs a fresh plan with a stale wish record. When that happened the sum came out BELOW a
            /// lane's own allocation and the board drew NGU lanes at ~150% (observed live, right after
            /// wish sink mode changed the pool's shape).
            ///
            /// Taking the max of the two candidate denominators makes the display safe by construction
            /// rather than by hoping the two reads agree: the plan pool bounds every constraint lane,
            /// the sum bounds the wish lane, and the larger bounds both. The cost is that the bars can
            /// under-fill slightly on a mismatched tick, which is the right direction to be wrong in —
            /// an under-full bar is visibly odd, a 150% bar is a number nobody can act on.
            /// </remarks>
            public long BoardPool(long laneAllocated, long planPool)
            {
                if (laneAllocated < 0) laneAllocated = 0;
                if (planPool < 0) planPool = 0;
                long total = laneAllocated + IdleAtPass;
                if (total < 0) total = long.MaxValue;      // saturate rather than wrap
                return total > planPool ? total : planPool;
            }
        }

        public static Share Energy { get; private set; }
        public static Share Magic { get; private set; }

        /// <summary>
        /// Record one wish pass. Call from a `finally` — <see cref="WishManager"/>'s loop has early
        /// returns, and a pass that exits through one of them still took whatever it took.
        /// </summary>
        public static void Record(long offeredEnergy, long takenEnergy, long idleEnergyAtPass,
                                  long offeredMagic, long takenMagic, long idleMagicAtPass)
        {
            Energy = Make(offeredEnergy, takenEnergy, idleEnergyAtPass);
            Magic = Make(offeredMagic, takenMagic, idleMagicAtPass);
        }

        private static Share Make(long offered, long taken, long idleAtPass)
        {
            if (offered < 0) offered = 0;
            if (taken < 0) taken = 0;
            if (idleAtPass < 0) idleAtPass = 0;
            // The offer is a slider percentage OF the idle pool, so it can never exceed it. If it does,
            // the two were read at different instants and the pool is the one to trust.
            if (offered > idleAtPass) idleAtPass = offered;
            // Clamp rather than trust: AllocateToWish rounds each slot up
            // (`remaining/slots + Sign(remaining%slots)`), so the sum of the slots can legitimately
            // exceed the offer by a few units. A lane wider than the residue it came from would read
            // as a board bug, and the few units are noise at every scale this runs at.
            if (taken > offered) taken = offered;
            return new Share { Offered = offered, Taken = taken, IdleAtPass = idleAtPass, Recorded = true };
        }

        /// <summary>
        /// How much of an idle pool the wish pass may claim this tick.
        /// </summary>
        /// <param name="sink">
        /// SINK: everything still idle. The lanes have already allocated from a pool that included last
        /// tick's wish holdings (released before the swaps), so a capped lane has taken its fill and the
        /// remainder is genuinely spare — this is what "keep the NGUs capped, wishes get the rest" means.
        /// PRIORITY: a slider share, the historical behaviour.
        /// </param>
        /// <param name="idle">The idle pool at the wish pass.</param>
        /// <param name="sliderPercent">0-100. Ignored entirely in sink mode.</param>
        /// <remarks>
        /// Ceiling, not rounding, and then clamped — matching what WishManager did before this was
        /// extracted. Above 2^53 the double product loses exactness and Ceiling can land one unit past
        /// the pool, and pools legitimately exceed 1e18 under potions (audit/15 §A1).
        /// </remarks>
        public static long Offer(bool sink, long idle, double sliderPercent)
        {
            if (idle <= 0) return 0;
            if (sink) return idle;
            if (sliderPercent <= 0) return 0;

            long take = (long)System.Math.Ceiling(idle * sliderPercent / 100.0);
            return take > idle || take < 0 ? idle : take;
        }

        /// <summary>Forget both records — a profile load or a rebirth makes the last pass meaningless.</summary>
        public static void Reset()
        {
            Energy = default(Share);
            Magic = default(Share);
        }

        /// <summary>
        /// The lane sentence. States that the take is HELD, because the board used to say the opposite.
        /// </summary>
        /// <remarks>
        /// ConstraintLayerBridge's residue reason claimed the remainder was "reclaimed next swap".
        /// For the portion the wish pass keeps that is FALSE: Reclaim() releases wandoos, augments, TM,
        /// AT, NGU and BT and never touches wishesController, so wish holdings survive every swap and
        /// are only released when the game itself calls removeAllEnergyAndMagic() — which in practice
        /// means a gear change. That single wrong word is why a compounding claim on the pool looked
        /// like normal operation for as long as it did.
        /// </remarks>
        public static string LaneWhy(bool tookAnything)
        {
            return tookAnything
                ? "held by the wish slots — NOT reclaimed by the next swap; released only when the game "
                  + "clears resources (a gear change does it). The Wish % slider sets this share."
                : "the wish pass ran and took nothing — no slot could use it (a wish needs energy, magic "
                  + "AND R3 non-zero), or the Wish % slider is 0.";
        }
    }
}
