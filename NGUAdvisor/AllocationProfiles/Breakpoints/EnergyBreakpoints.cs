using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class EnergyBreakpoints : BaseBreakpoints<ResourceBreakpoint[]>
    {
        public EnergyBreakpoints() : base() { }

        public EnergyBreakpoints(JSONNode bps) :
            base(bps, (bp) => ResourceBreakpoint.ParseBreakpointArray(bp["Priorities"], ResourceType.Energy).ToArray()) { }

        protected override bool PerformSwap(Breakpoint bp)
        {
            // KILL SWITCH (SavedSettings.ConstraintAllocator, default ON): the four-pass constraint
            // layer is the allocator — ConstraintLayerBridge builds fresh per-lane verdicts, composes
            // the plan and fills through each lane's own Allocate(). Flag OFF runs the ORIGINAL
            // prioCount share loop below, byte-unchanged (decision-G1-D3-V9 put it on REPLACE;
            // removing it is a later job, not this commit's).
            if (Managers.ConstraintLayerBridge.NewPathEnabled)
                return Managers.ConstraintLayerBridge.PerformSwap(bp.priorities, ResourceType.Energy);

            // NULL FILTER: ParseBreakpointArray ends in `yield return null` for any token it does not
            // recognise, and the profile constructor above does not strip those (ChallengeOverlay's own
            // parser does — that asymmetry is the bug). Without this, ONE typo'd or unsupported token in a
            // profile NREs the whole lane on every tick, and CustomAllocation.RunStep swallows it and
            // throttles the log to one line per ten minutes — i.e. silent, total loss of the resource.
            var temp = bp.priorities.Where(x => x != null && x.IsValid()).ToList();
            // Challenge overlay: narrate dead-system filtering; inject fallback if the list is all-dead.
            temp = Managers.ChallengeOverlay.TransformPriorities(bp.priorities, temp, ResourceType.Energy);
            if (temp.Count == 0)
                return false;

            var dbg = AllocDiagnostic.Begin(ResourceType.Energy);
            long curEnergy = _character.curEnergy;

            var shouldRetry = true;
            while (shouldRetry)
            {
                var successList = new List<ResourceBreakpoint>();
                shouldRetry = false;
                var prioCount = temp.Count(x => !x.IsCap);
                if (dbg != null) dbg.Pass(prioCount);

                RemoveEnergy(temp.Exists(x => x is BasicTrainingBP));

                foreach (var prio in temp)
                {
                    prio.UpdateMaxAllocation(prioCount);
                    long before = _character.idleEnergy;
                    var ok = prio.Allocate();
                    if (dbg != null) dbg.Lane(prio, before - _character.idleEnergy);
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

            if (dbg != null) dbg.Emit(curEnergy, _character.idleEnergy);

            _character.NGUController.refreshMenu();
            _character.wandoos98Controller.refreshMenu();
            _character.advancedTrainingController.refresh();
            _character.timeMachineController.updateMenu();
            _character.allOffenseController.refresh();
            _character.allDefenseController.refresh();
            _character.augmentsController.updateMenu();

            return false;
        }

        private void RemoveEnergy(bool removeBT)
        {
            _character.wandoos98Controller.removeAllEnergy();
            _character.augmentsController.removeAllEnergy();
            _character.timeMachineController.removeAllEnergy();
            _character.advancedTrainingController.removeAllEnergy();
            _character.NGUController.removeAllEnergy();
            if (removeBT)
            {
                _character.allOffenseController.removeAllEnergy();
                _character.allDefenseController.removeAllEnergy();
            }
        }
    }
}
