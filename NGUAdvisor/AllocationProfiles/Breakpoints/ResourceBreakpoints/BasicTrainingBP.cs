using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    // NOTE on the "Insta Training Cap" AP purchase (full decompile, Rebirth.instaTrain()): its ONLY
    // effect is a one-time seed at rebirth — +12 energy, 6 into the first attack and defense
    // trainings. It does NOT insta-complete bars; training speed is energy/cap per tick. So ALLBT's
    // full-cap allocation is optimal with or without the purchase, and the earlier "seed 6" special
    // case (built on a wrong model of the purchase) was reverted — it throttled training to a crawl
    // and visibly dripped 6 energy into the newest training every cycle.
    public class BasicTrainingBP : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.Energy;

        // The predicate is Managers.LaneCapMath.BasicTrainingUnlocked; the prior-slot read is passed as
        // a callback so the `Index % 6 == 0` early-true still short-circuits it (that index would be -1).
        protected override bool Unlocked() =>
            Managers.LaneCapMath.BasicTrainingUnlocked(Index, slot =>
                (Index <= 5 ? _character.training.attackTraining : _character.training.defenseTraining)[slot]);

        // WIRED to the game's own ceiling (decision record amendment 16 §7; audit 20 §2.2). A slot at
        // or above attackCaps/defenseCaps levels as fast as the game permits and cannot use another
        // unit; the advisor used to hardcode `false` here and re-fund the slot on every pass, piling
        // energy into it permanently. Arithmetic and the full citation are in
        // Managers.LaneCapMath.BasicTrainingSaturated.
        //
        // The index bound is NOT redundant with Unlocked(). ResourceBreakpoint.IsValid() is a method
        // call, so all three terms are evaluated eagerly — an out-of-range Index reaches this body even
        // when Unlocked() has already answered false, and the arrays are 6 long. Same bound as
        // LaneCapMath.BasicTrainingUnlocked, and false = "not done" keeps the pre-P1 answer.
        protected override bool TargetMet()
        {
            if (Index < 0 || Index > 11)
                return false;

            int slot = Managers.LaneCapMath.BasicTrainingSlot(Index);
            return Index <= 5
                ? Managers.LaneCapMath.BasicTrainingSaturated(_character.training.attackEnergy[slot], _character.training.attackCaps[slot])
                : Managers.LaneCapMath.BasicTrainingSaturated(_character.training.defenseEnergy[slot], _character.training.defenseCaps[slot]);
        }

        public override bool Allocate()
        {
            int slot = Managers.LaneCapMath.BasicTrainingSlot(Index);
            if (Index <= 5)
            {
                SetInput(Managers.LaneCapMath.BasicTrainingAllocation(_character.training.attackCaps[slot], MaxAllocation));
                _character.allOffenseController.trains[slot].addEnergy();
            }
            else
            {
                SetInput(Managers.LaneCapMath.BasicTrainingAllocation(_character.training.defenseCaps[slot], MaxAllocation));
                _character.allDefenseController.trains[slot].addEnergy();
            }

            return true;
        }
    }
}
