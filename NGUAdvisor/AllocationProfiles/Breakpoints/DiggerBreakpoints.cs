using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using NGUAdvisor.Managers;
using SimpleJSON;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class DiggerBreakpoints : BaseBreakpoints<int[]>
    {
        public DiggerBreakpoints() : base() { }

        public DiggerBreakpoints(JSONNode bps) :
            base(bps, (bp) => bp["List"].AsArray.Children.Select(x => x.AsInt).Where(x => x <= 11).ToArray()) { }

        protected override bool PerformSwap(Breakpoint bp)
        {
            if (!LockManager.CanSwap())
                return false;

            // Advisor auto-apply (Phase B): while enabled (and not in a challenge), the advisor's
            // goal-aware set replaces the profile's list at every swap.
            var target = bp.priorities;
            if (Main.Settings != null && Main.Settings.AdvisorDiggers)
                target = OptimizationAdvisor.CurrentDiggerSet() ?? target;

            if (DiggerManager.EquipDiggers(target))
            {
                _lastFailKey = null;
                Main.Log($"Equipping Diggers: {string.Join(", ", target)}");
                current = bp;
                return true;
            }

            // Retrying is right (gold income may simply not exist yet after rebirth), but log the
            // failure once per distinct set — the old per-pass line spammed every 10s all run.
            var key = string.Join(",", target);
            if (key != _lastFailKey)
            {
                _lastFailKey = key;
                Main.Log($"Diggers {key} can't run yet (no gold income or nothing usable) — retrying quietly.");
            }

            return false;
        }

        private static string _lastFailKey;
    }
}
