using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class R3Breakpoints : BaseBreakpoints<ResourceBreakpoint[]>
    {
        public R3Breakpoints() : base() { }

        public R3Breakpoints(JSONNode bps) :
            base(bps, (bp) => ResourceBreakpoint.ParseBreakpointArray(bp["Priorities"], ResourceType.R3).ToArray()) { }

        // Walk the whole list, not just the head of it.
        //
        // This used to reclaim the pool and then fund valid.FirstOrDefault(), so one hack received all of it.
        // With HackBP now stopping at the allocation that saturates a hack — past which R3 buys nothing,
        // because updateAllHacks discards the overflow — that surplus had nowhere to go and was simply
        // destroyed. Everything after the first token in a fifteen-token hack-day list was decoration.
        //
        // prioCount stays 1 on purpose. The Energy lane divides the pool by the number of non-cap priorities
        // so its systems share; R3 must NOT, for two reasons. The list is an ORDER — the author put HACK-13
        // first because they want it fed first — and an even fifteen-way split of a modest pool can push
        // every share under the float stall floor (2^-25 progress per tick), where a hack accumulates
        // literally nothing forever. Giving each entry the full remaining idle and letting it self-limit
        // yields the same waterfill without either hazard: a hack takes what it can use, the rest flows down.
        //
        // Consequence worth stating: for the first time the lane can finish with R3 left over. The wish
        // spare pass (CustomAllocation, WishManager.Allocate(true)) takes all remaining idle, so surplus now
        // reaches wishes instead of sitting in a saturated hack. That is the right destination — wishes are
        // the other R3 sink — but it is a behaviour change, so HackDbg reports the leftover.
        protected override bool PerformSwap(Breakpoint bp)
        {
            // See EnergyBreakpoints.PerformSwap for why the null filter is here.
            var valid = bp.priorities.Where(x => x != null && x.IsValid()).ToList();
            // Challenge overlay: narrate dead-system filtering; inject fallback if the list is all-dead.
            valid = Managers.ChallengeOverlay.TransformPriorities(bp.priorities, valid, ResourceType.R3);
            if (valid.Count == 0)
                return false;

            RemoveR3();

            foreach (var prio in valid)
            {
                if (_character.res3.idleRes3 <= 0)
                    break;
                prio.UpdateMaxAllocation();
                prio.Allocate();
            }

            _character.hacksController.refreshMenu();
            ReportSurplusOnce();
            return false;
        }

        // Say it the first time it happens, once per session.
        //
        // Before the saturation clamp the lane always emptied the pool into one hack, so there was never any
        // idle R3 at this point and the wish spare pass had nothing to pick up. Now there can be, and
        // WishManager.Allocate(true) takes ALL of it regardless of the WishR3 share — which is the intended
        // destination, but it means a player who set WishR3 to 0 will see wishes progress anyway. Silent
        // would read as the advisor going behind their back.
        private static bool _surplusReported;
        private void ReportSurplusOnce()
        {
            if (_surplusReported) return;
            long idle = _character.res3.idleRes3;
            if (idle <= 0) return;
            _surplusReported = true;
            Main.Log($"Hacks: every hack in this list is at the most R3 it can use; "
                   + $"{Managers.NumberFormatter.Abbrev(idle)} left over goes to wishes.");
        }

        public override void Reset()
        {
            _surplusReported = false;
            base.Reset();
        }

        private void RemoveR3() => _character.hacksController.removeAllR3();
    }
}
