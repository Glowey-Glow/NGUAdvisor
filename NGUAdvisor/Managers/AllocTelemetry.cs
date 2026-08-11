using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // THE [AllocDbg] TWIN FOR THE CONSTRAINT LAYER — the new path's per-lane allocation dump.
    //
    // WHY THIS EXISTS. AllocDiagnostic (AllocationProfiles/Breakpoints/AllocDiagnostic.cs) is the
    // instrument that caught the founding defect: one line reading CAPBestAug-0 taking
    // 732,735,009,221 of 732,767,363,921 idle energy — 99.996% of the pool into a single lane. It is
    // reached from EnergyBreakpoints.cs:36 / MagicBreakpoints.cs:29, which are BELOW the branch to
    // ConstraintLayerBridge.PerformSwap at :22-23 / :19-20. So on the new path AllocDiagnostic is
    // never even constructed, and the constraint layer has had NO equivalent:
    //
    //   * ConstraintParity emits DIVERGENCES only, only in the new>old direction that the Need
    //     approximation can express (amendment 24 §C2.1), and only while ConstraintParityLog is on.
    //   * Surface() emits three STATE lines — budget pressure, sink refused, rate lanes skipped.
    //   * LastEnergyPlan / LastMagicPlan hold the whole plan and nothing reads them (verified by
    //     exhaustive search: the two properties have no consumer in the repo).
    //
    // A lane funded CORRECTLY therefore emitted nothing, anywhere — and so did a lane funded
    // catastrophically, as long as the catastrophe was a legal seat. That is the same failure class
    // as the two-hour Evil-NGU zero (audit/25 §4): the layer works and nothing can see it working,
    // so nothing can see it working WRONGLY either.
    //
    // NOT A PORT OF AllocDiagnostic. Its two headline numbers have no meaning here:
    //   prioCount — the equal-share divisor. There is no divisor on this path. SeatRoster exposes a
    //     SeatCount, but a seat count is not a share denominator: the fill is min(capacity, want) in
    //     list order, so lane N's amount does not depend on how many lanes exist.
    //   passes    — the retry loop's iteration count. ⚠ AMENDMENT 36 MADE THIS HALF-TRUE AGAIN: the
    //     constraint fill now runs ROUNDS (ConstraintLayer.Waterfill), so there IS a re-run with the
    //     survivors. It is still not AllocDiagnostic's `passes`, which counted retries triggered by
    //     `Allocate()` returning false, with the whole list reclaimed and re-offered from scratch; a
    //     waterfill round re-offers only what the previous round LEFT, to only the lanes that proved
    //     appetite in it. The block below reports each lane's CUMULATIVE offered and taken across
    //     the rounds, which is the figure the operator reads either way.
    // What replaces them is per-lane state that the plan actually has: the pass that eliminated the
    // lane, its Pass 2 capacity, what the fill OFFERED it, what it actually TOOK, and that take as a
    // share of the pool — the ratio is the figure that made the founding defect legible.
    //
    // RAW LONGS, NOT NumberFormatter.Abbrev. Abbrev rounds to ~3 significant figures, which renders
    // both sides of the founding line as "732.7G" and destroys exactly the discrimination that named
    // the defect. The amounts print in full and the share is computed to three decimals.
    //
    // EVERY ZERO CARRIES ITS REASON (spec §10). The plan supplies a reason for eliminated lanes and
    // for fill-skipped rate lanes; it does NOT supply one for the three ways a SEATED lane can still
    // end a tick at zero — a no-allocation beard seat, a lane whose turn came after the pool ran dry,
    // and a lane that was offered resource and whose own Allocate() absorbed none of it. ZeroReason
    // synthesises those three, so no zero in the block is ever bare.
    //
    // Unity-free on purpose (the ConstraintParity pattern): pure string production plus a pure
    // throttle decision, with the per-pool latch state held by the caller. That is what lets the
    // net9.0 test project link and exercise it without the game build.
    public static class AllocTelemetry
    {
        // ---- the throttle ------------------------------------------------------------------------
        //
        // PER RESOURCE, signature-latched, with a periodic heartbeat. Neither of the two house
        // precedents is right on its own:
        //
        //   AllocDiagnostic's flat 60s-per-resource keeps the heartbeat — a steady state still
        //   proves the layer is alive — but at the allocator's 10s cadence it shows one tick in six,
        //   so a lane that seats and refuses inside a window is invisible.
        //
        //   ZoneGate's per-key latch is first-occurrence-only, forever. That is the wrong shape for
        //   a recurring dump twice over: it fragments one tick's allocation into N independently
        //   gated lines (the founding defect was only legible as a RATIO ACROSS lanes within ONE
        //   tick), and it goes permanently silent after the first emission — precisely when a slow
        //   drift would start.
        //
        // So: the whole plan for one pool is the unit, the heartbeat is AllocDiagnostic's 60s
        // verbatim, and a change in the DISPOSITION signature (amounts excluded — ConstraintParity
        // .Signature's principle, already the precedent in this file's neighbour) overrides it
        // behind a one-tick floor. The heartbeat is the load-bearing half: the failure this exists to
        // prevent is a STEADY wrong state, which a change-only trigger is blind to by construction.
        public const double RefreshIntervalSeconds = 60;   // AllocDiagnostic's cadence, unchanged
        public const double MinIntervalSeconds = 10;       // one allocator tick — the flap ceiling

        // Pure; the caller holds (lastSignature, lastEmitUtc) per pool, as ConstraintParity does.
        public static bool ShouldEmit(string signature, string lastSignature, double secondsSinceLastEmit)
        {
            if (secondsSinceLastEmit >= RefreshIntervalSeconds)
                return true;                                     // heartbeat: steady states stay visible
            if (secondsSinceLastEmit < MinIntervalSeconds)
                return false;                                    // never faster than the allocator itself
            return (signature ?? "") != (lastSignature ?? "");    // a real state change, next tick
        }

        // WHICH lanes are in WHICH disposition, and whether each ended at zero — amounts excluded,
        // because the pool grows every tick and an amount-sensitive signature would change forever.
        // The funded/zero bit is IN the signature deliberately: a lane that stays seated while
        // falling to zero is the founding failure mode, and it must read as a change.
        public static string Signature(ConstraintLayer.Plan plan, IList<long> offers)
        {
            if (plan?.Lanes == null)
                return "";
            var keys = new List<string>(plan.Lanes.Length);
            for (int i = 0; i < plan.Lanes.Length; i++)
            {
                var d = plan.Lanes[i];
                string state;
                if (!d.Seated)
                    state = ConstraintParity.PassTag(d.EliminatedBy);
                else if (d.Allocation > 0)
                    state = "funded";
                else
                    state = "seated-zero";
                keys.Add((d.Label ?? d.Name ?? "?") + "@" + state);
            }
            keys.Sort(StringComparer.Ordinal);
            var sig = string.Join("|", keys.ToArray());
            // The sink's own disposition is not in Lanes' seated bit alone — an absent sink and a
            // refused sink are different states with the same (empty) lane contribution.
            return sig + "#sink=" + (plan.SinkIndex < 0 ? "absent" : plan.SinkSeated ? "seated" : "refused");
        }

        // ---- the per-lane zero reason -------------------------------------------------------------

        // Non-null exactly when this lane ended the tick at zero. The plan carries the reason for an
        // eliminated lane (ConstraintLayer.Eliminate) and for a fill-skipped rate lane
        // (FillSession.Offer's skipReason); the remaining three cases are synthesised here.
        public static string ZeroReason(in ConstraintLayer.LaneDecision d, long offered)
        {
            if (d.Allocation > 0)
                return null;

            if (!d.Seated)
                return d.Reason ??
                    ("refused at " + ConstraintParity.PassTag(d.EliminatedBy) + " with NO recorded reason " +
                     "— that omission is itself the defect spec §10 forbids");

            if (d.NoAllocation)
                return "no allocation cost: seats for the campaign ranking, never offered a unit " +
                       "(spec §6, the beard shape)";

            if (d.Reason != null)
                return d.Reason;   // the fill's own skip reason — the rate-lane chunk

            if (offered <= 0)
                return d.SurplusSink
                    ? "nothing remained for the surplus sink: the seated lanes above it absorbed the whole pool"
                    : "offered nothing: the pool was exhausted by the lanes ahead of it in list order — " +
                      "what they left does not reach one whole unit for each destination still waiting";

            return d.SurplusSink
                ? "the surplus sink absorbed none of its offer: Wandoos is at its per-tick capAmount " +
                  "(CapacityPass.Table) — the residue is left for the wish spare pass, on purpose"
                : "offered resource and its own Allocate() took none of it: the stair-snap self-limited " +
                  "below one whole unit, or a gate inside Allocate refused (a self-limiting lane's " +
                  "capacity is only discovered at fill time)";
        }

        // ---- the block ----------------------------------------------------------------------------

        // One emission = one multi-line block, exactly like ConstraintParity.Format. Returns null
        // when there is nothing to render, so the caller pays nothing.
        //
        // offers: index-aligned with plan.Lanes — what FillSession.Offer returned for each lane
        //   (and, at SinkIndex, the remainder handed to the sink). May be null, in which case the
        //   offer column reads "?" rather than lying: on the Compose-side fill offer and take are
        //   the same number, but on the live path they are NOT, and the gap between them is the
        //   whole point of the instrument.
        public static string Render(string pool, ConstraintLayer.Plan plan, IList<long> offers)
        {
            if (plan?.Lanes == null)
                return null;

            var inv = CultureInfo.InvariantCulture;
            int seated = 0;
            int width = 1;
            for (int i = 0; i < plan.Lanes.Length; i++)
            {
                if (plan.Lanes[i].Seated) seated++;
                var lbl = plan.Lanes[i].Label ?? plan.Lanes[i].Name ?? "?";
                if (lbl.Length > width) width = lbl.Length;
            }

            var sb = new StringBuilder();
            sb.Append("[AllocDbg] ").Append(pool ?? "?")
              .Append(" pool=").Append(plan.Pool.ToString(inv))
              .Append(" lanes=").Append(plan.Lanes.Length.ToString(inv))
              .Append(" seated=").Append(seated.ToString(inv))
              .Append(" (constraint layer)");

            for (int i = 0; i < plan.Lanes.Length; i++)
            {
                var d = plan.Lanes[i];
                long offered = offers != null && i < offers.Count ? offers[i] : long.MinValue;

                sb.Append("\n  ").Append((d.Label ?? d.Name ?? "?").PadRight(width)).Append("  ");

                // Padded to the widest disposition ("refused [pass 1 feasibility]") so the seated /
                // refused split reads as a column. The founding defect was found by EYE, scanning
                // one tick's lanes for the one holding the pool; ragged columns cost that.
                var disposition = !d.Seated
                    ? "refused [" + ConstraintParity.PassTag(d.EliminatedBy) + "]"
                    : "seated " + (d.SurplusSink ? "[sink]" : d.RateLane ? "[rate]" : "      ");
                sb.Append(disposition.PadRight(DispositionWidth));

                sb.Append(" cap=").Append(CapText(d))
                  .Append(" offered=").Append(offered == long.MinValue ? "?" : offered.ToString(inv))
                  .Append(" took=").Append(d.Allocation.ToString(inv));

                if (d.Allocation > 0)
                    sb.Append(" (").Append(Share(d.Allocation, plan.Pool)).Append(" of pool)");

                var why = ZeroReason(d, offered == long.MinValue ? 0 : offered);
                if (why != null)
                    sb.Append(" — ").Append(why);
            }

            // The remainder, ALWAYS — including when it is zero. An unstated remainder is the one
            // number a reader would otherwise have to derive by subtracting nine others.
            sb.Append("\n  remainder=").Append(plan.Unallocated.ToString(inv));
            if (plan.Unallocated > 0)
                sb.Append(" (").Append(Share(plan.Unallocated, plan.Pool)).Append(" of pool)");
            if (plan.SinkIndex >= 0)
                sb.Append(" sink=").Append(plan.Lanes[plan.SinkIndex].Label ?? "?")
                  .Append(plan.SinkSeated ? " seated took=" + plan.SinkAllocation.ToString(inv) : " REFUSED");
            else
                sb.Append(" sink=absent");
            if (!string.IsNullOrEmpty(plan.UnallocatedReason))
                sb.Append(" — ").Append(plan.UnallocatedReason);

            // The two aggregate states the plan already computes. They have their own state-change
            // lines on the operator channel; repeating them here keeps one block self-contained, so
            // a reader never has to correlate two log files by timestamp.
            if (plan.BudgetMessage != null)
                sb.Append("\n  budget: ").Append(plan.BudgetMessage);
            if (plan.RateLanesSkipped > 0)
                sb.Append("\n  rate skips: ").Append(plan.RateLanesSkipped.ToString(inv))
                  .Append(" lane(s), pool ").Append(plan.RateSkipPool.ToString(inv))
                  .Append(" < cheapest capacity ").Append(plan.RateSkipCheapest.ToString(inv));

            return sb.ToString();
        }

        // "refused [pass 1 feasibility]" — the longest disposition ConstraintParity.PassTag produces.
        private const int DispositionWidth = 28;

        private static string CapText(in ConstraintLayer.LaneDecision d)
        {
            if (d.NoAllocation)
                return "none";                                   // the beard shape: seats, never fills
            if (d.Capacity == ConstraintLayer.SelfLimiting)
                return "self";                                   // discovered at fill time
            return d.Capacity.ToString(CultureInfo.InvariantCulture);
        }

        // Three decimals, because 99.996% and 100% are the difference between the founding defect
        // and a healthy tick.
        private static string Share(long amount, long pool)
        {
            if (pool <= 0)
                return "n/a";
            return (100.0 * amount / pool).ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }
    }
}
