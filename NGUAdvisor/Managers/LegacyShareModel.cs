using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE OLD ALLOCATOR, AS A PURE COUNTERFACTUAL — a faithful replay of the arithmetic of
    // ResourceBreakpoint.UpdateMaxAllocation and the Energy/MagicBreakpoints.PerformSwap walk it
    // lives in, kept ONLY so parity logging can say what the prioCount share model WOULD have done
    // while the constraint layer is the path actually allocating.
    //
    // This is decision-record material, not a model to build on: decision-G1-D3-V9.md:57-64 put
    // UpdateMaxAllocation and the prioCount share model on REPLACE ("any fix that leaves the
    // seat-count divisor in place is treating the symptom"), and amendment 11 §4.4 lists the four
    // divisor workarounds that accreted on top of it. The REAL old path still exists, byte-unchanged,
    // behind the kill switch in Energy/MagicBreakpoints.PerformSwap — flag off runs THAT code, not
    // this simulation. This file exists so the parity log can run both models against one tick's
    // state without allocating twice.
    //
    // Replayed exactly (per ResourceBreakpoint.cs:71-79 and EnergyBreakpoints.cs:31-56):
    //   prioCount = count of non-cap lanes
    //   per lane, in list order:
    //     capMax = IsCap ? cur : idle              (GetMaxResourceAmount — idle is LIVE, post-takes)
    //     percent?      capMax = ceil(capMax * pct)     (percent SKIPS the divisor — even non-cap)
    //     else non-cap? capMax = capMax / prioCount + Math.Sign(capMax % prioCount)
    //     offer = min(capMax, idle)
    //     take  = min(need, offer)
    //     idle -= take;  non-cap? prioCount--
    //
    // Two knowing approximations, both stated in the parity log's contract:
    //   1. ONE steady pass — the old loop's retry-on-false redistribution is not replayed.
    //   2. `Need` is the lane's self-limit. Live, the bridge feeds the constraint path's observed
    //      take, which equals the true need whenever the lane self-limited below both offers and is
    //      a lower bound when the new offer was the binding constraint.
    public static class LegacyShareModel
    {
        public struct LaneInput
        {
            public string Label;
            public bool IsCap;
            public bool HasPercent;
            public double Percent;   // already /100, as ResourceBreakpoint stores CapPercent
            public long Need;        // what Allocate() would take given an unbounded offer
        }

        public struct LaneShare
        {
            public string Label;
            public long Offer;       // MaxAllocation as UpdateMaxAllocation would have computed it
            public long Take;        // min(Need, Offer)
        }

        public static LaneShare[] Simulate(long idlePool, long curPool, IList<LaneInput> lanes)
        {
            var count = lanes?.Count ?? 0;
            var result = new LaneShare[count];
            long idle = idlePool > 0 ? idlePool : 0;

            int prioCount = 0;
            for (int i = 0; i < count; i++)
                if (!lanes[i].IsCap)
                    prioCount++;

            for (int i = 0; i < count; i++)
            {
                var lane = lanes[i];
                long capMax = lane.IsCap ? curPool : idle;
                if (lane.HasPercent)
                    capMax = (long)Math.Ceiling(capMax * lane.Percent);
                else if (!lane.IsCap && prioCount > 0)
                    capMax = capMax / prioCount + Math.Sign(capMax % prioCount);

                long offer = Math.Min(capMax, idle);
                long take = Math.Min(lane.Need > 0 ? lane.Need : 0, offer);
                if (take < 0)
                    take = 0;

                result[i] = new LaneShare { Label = lane.Label, Offer = offer, Take = take };

                idle -= take;
                if (!lane.IsCap)
                    prioCount--;
            }

            return result;
        }
    }
}
