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

        protected override bool PerformSwap(Breakpoint bp)
        {
            // NULL FILTER: ParseBreakpointArray ends in `yield return null` for any token it does not
            // recognise, and the profile constructor above does not strip those (ChallengeOverlay's own
            // parser does — that asymmetry is the bug). Without this, ONE typo'd or unsupported token in an
            // R3 list NREs the lane on every tick before it can reallocate, and CustomAllocation.RunStep
            // swallows it and throttles the log to one line per ten minutes.
            var valid = bp.priorities.Where(x => x != null && x.IsValid()).ToList();
            // Challenge overlay: narrate dead-system filtering; inject fallback if the list is all-dead.
            valid = Managers.ChallengeOverlay.TransformPriorities(bp.priorities, valid, ResourceType.R3);
            var prio = valid.FirstOrDefault();
            if (prio != null)
            {
                RemoveR3();

                prio.UpdateMaxAllocation();
                prio.Allocate();

                _character.hacksController.refreshMenu();
            }

            return false;
        }

        private void RemoveR3() => _character.hacksController.removeAllR3();
    }
}
