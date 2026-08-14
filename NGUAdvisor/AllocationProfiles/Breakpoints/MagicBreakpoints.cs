using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class MagicBreakpoints : BaseBreakpoints<ResourceBreakpoint[]>
    {
        public MagicBreakpoints() : base() { }

        public MagicBreakpoints(JSONNode bps) :
            base(bps, (bp) => ResourceBreakpoint.ParseBreakpointArray(bp["Priorities"], ResourceType.Magic).ToArray()) { }

        protected override bool PerformSwap(Breakpoint bp)
        {
            // KILL SWITCH — see EnergyBreakpoints.PerformSwap: flag ON routes to the constraint
            // layer; flag OFF runs the original prioCount loop below, byte-unchanged.
            if (Managers.ConstraintLayerBridge.NewPathEnabled)
                return Managers.ConstraintLayerBridge.PerformSwap(bp.priorities, ResourceType.Magic);

            // See EnergyBreakpoints.PerformSwap for why the null filter is here.
            var temp = bp.priorities.Where(x => x != null && x.IsValid()).ToList();
            // Challenge overlay: narrate dead-system filtering; inject fallback if the list is all-dead.
            temp = Managers.ChallengeOverlay.TransformPriorities(bp.priorities, temp, ResourceType.Magic);
            if (temp.Count == 0)
                return false;

            var dbg = AllocDiagnostic.Begin(ResourceType.Magic);
            long curMagic = _character.magic.curMagic;

            var shouldRetry = true;
            while (shouldRetry)
            {
                var successList = new List<ResourceBreakpoint>();
                shouldRetry = false;

                var prioCount = temp.Count(x => !x.IsCap);
                if (dbg != null) dbg.Pass(prioCount);

                RemoveMagic();

                foreach (var prio in temp)
                {
                    prio.UpdateMaxAllocation(prioCount);
                    long before = _character.magic.idleMagic;
                    var ok = prio.Allocate();
                    if (dbg != null) dbg.Lane(prio, before - _character.magic.idleMagic);
                    if (ok)
                        successList.Add(prio);
                    else
                        shouldRetry = true;

                    if (!prio.IsCap)
                        prioCount--;
                }
                temp = successList;
                shouldRetry &= temp.Count > 0;
            }

            if (dbg != null) dbg.Emit(curMagic, _character.magic.idleMagic);

            _character.timeMachineController.updateMenu();
            _character.bloodMagicController.updateMenu();
            _character.NGUController.refreshMenu();
            _character.wandoos98Controller.refreshMenu();

            return false;
        }

        private void RemoveMagic()
        {
            _character.wandoos98Controller.removeAllMagic();
            _character.timeMachineController.removeAllMagic();
            _character.bloodMagicController.removeAllMagic();
            _character.NGUController.removeAllMagic();
        }
    }
}
