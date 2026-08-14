using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // PARITY LOGGING's pure half: both allocations are computed, the NEW one is used, and every lane
    // where they differ is recorded with the pass that caused the difference and the old vs new
    // amount. Divergence is EXPECTED — the old allocator is wrong — so the point is not an alarm but
    // an inventory: the operator checks WHICH divergences appear against what the audit predicted
    // (capped BT lanes leaving the list, the divisor falling as infeasible lanes stop holding seats,
    // gold-stalled TM/augment lanes going to zero rather than a reduced share).
    //
    // THE THROTTLE: Analyze runs ~86,400x/day, and the amounts move every tick while the SET of
    // divergent lanes is what carries the signal. So emission is keyed on the divergence SIGNATURE —
    // lane + causing pass, amounts excluded: a signature CHANGE emits (subject to a small hard
    // floor), an unchanged signature re-emits only on the slow refresh interval. State (last
    // signature, last emit time) lives in the caller; every decision here is a pure function.
    public static class ConstraintParity
    {
        public struct Divergence
        {
            public string Label;
            public ConstraintLayer.PassId Pass;   // the pass that caused it; None = both seated,
                                                  // the amounts differ only by the share model
            public long OldAmount;
            public long NewAmount;
            public string Reason;                 // the elimination reason when a pass caused it
        }

        // Index-aligned by contract: the caller builds the legacy inputs, the plan and the takes
        // from the SAME membership list, so lane i is the same lane in all three.
        public static List<Divergence> Compare(ConstraintLayer.Plan plan,
            IList<long> newTakes, IList<LegacyShareModel.LaneShare> oldShares)
        {
            var diffs = new List<Divergence>();
            if (plan?.Lanes == null || newTakes == null || oldShares == null)
                return diffs;

            var n = Math.Min(plan.Lanes.Length, Math.Min(newTakes.Count, oldShares.Count));
            for (int i = 0; i < n; i++)
            {
                var d = plan.Lanes[i];
                var oldTake = oldShares[i].Take;
                var newTake = newTakes[i];
                if (oldTake == newTake)
                    continue;

                diffs.Add(new Divergence
                {
                    Label = d.Label,
                    Pass = d.EliminatedBy,
                    OldAmount = oldTake,
                    NewAmount = newTake,
                    Reason = d.Reason,
                });
            }
            return diffs;
        }

        // Amount-insensitive: WHICH lanes diverge and WHY, not by how much this tick.
        public static string Signature(IList<Divergence> diffs)
        {
            if (diffs == null || diffs.Count == 0)
                return "";
            var keys = new List<string>(diffs.Count);
            foreach (var d in diffs)
                keys.Add((d.Label ?? "?") + "@" + PassTag(d.Pass));
            keys.Sort(StringComparer.Ordinal);
            return string.Join("|", keys.ToArray());
        }

        public const double MinIntervalSeconds = 30;      // hard floor against flapping signatures
        public const double RefreshIntervalSeconds = 600; // unchanged-signature re-emit cadence

        public static bool ShouldEmit(string signature, string lastSignature, double secondsSinceLastEmit)
        {
            if (string.IsNullOrEmpty(signature))
                return false;                              // nothing diverges — nothing to say
            if (secondsSinceLastEmit < MinIntervalSeconds)
                return false;
            if (signature != (lastSignature ?? ""))
                return true;
            return secondsSinceLastEmit >= RefreshIntervalSeconds;
        }

        // One log block per emission: one line per divergent lane, old → new, tagged with the pass.
        public static string Format(string pool, IList<Divergence> diffs)
        {
            var sb = new StringBuilder();
            sb.Append("Constraint parity [").Append(pool).Append("]: ")
              .Append((diffs?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
              .Append(" lane(s) diverge from the prioCount model (expected — the old allocator is the one being replaced)");
            if (diffs != null)
            {
                foreach (var d in diffs)
                {
                    sb.Append("\n  ").Append(d.Label)
                      .Append(": old ").Append(Abbrev(d.OldAmount))
                      .Append(" -> new ").Append(Abbrev(d.NewAmount))
                      .Append(" [").Append(PassTag(d.Pass)).Append("]");
                    if (!string.IsNullOrEmpty(d.Reason))
                        sb.Append(" ").Append(d.Reason);
                }
            }
            return sb.ToString();
        }

        // Public so AllocTelemetry renders the SAME spelling: two copies of this switch would drift,
        // and the pass name is the key a reader greps for across the parity and [AllocDbg] channels.
        public static string PassTag(ConstraintLayer.PassId pass)
        {
            switch (pass)
            {
                case ConstraintLayer.PassId.Budget: return "pass 0 budget";
                case ConstraintLayer.PassId.Feasibility: return "pass 1 feasibility";
                case ConstraintLayer.PassId.Capacity: return "pass 2 capacity";
                case ConstraintLayer.PassId.Target: return "pass 3 target";
                default: return "share model";
            }
        }

        private static string Abbrev(long v) => NumberFormatter.Abbrev(v);
    }
}
