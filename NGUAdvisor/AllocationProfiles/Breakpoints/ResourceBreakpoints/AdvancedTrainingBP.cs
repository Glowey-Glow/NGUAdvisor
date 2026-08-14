using UnityEngine;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    // ---- THE ALLAT GROUP WATERFILL IS GONE (amendment 11 §4.4 REPLACE · amendment 28 · 31 §Q4a) ----
    // This class used to carry a `GroupSpread` flag, set on the 5 BPs yielded by ALLAT/CAPALLAT, and a
    // private GroupShare() that clamped each slot to an even waterfill share of idleEnergy over the
    // group members still wanting energy. It existed because a CAPALLAT slot's budget was
    // min(cur, idle) — everything left — so allocation order dumped the whole pool into Defense/Attack
    // and the wandoos ATs got nothing. It was a LOCAL equal-share divisor, the same category of
    // mechanism as the prioCount one, and amendment 01 put both on the REPLACE path.
    //
    // The constraint layer now performs that waterfill for the whole pool at once: every seated
    // destination — each AT slot is its own destination — is offered
    // min(capacity, remaining / destinations-not-yet-offered), with `remaining` recomputed after each
    // lane commits, so slack from a cheap slot flows forward to an expensive one exactly as the local
    // waterfill intended (ConstraintLayer.FillSession.Offer/Commit).
    //
    // Nested inside that fill, GroupShare was PROVABLY A NO-OP, not merely redundant. CalculateATCap
    // already clamps to MaxAllocation, which on this path IS the fill's offer, `remaining / D` over
    // the D destinations not yet offered. GroupShare's own waterlevel is at least
    // `idleEnergy / (1 + later hungry AT slots)`, and those later slots are a SUBSET of the later
    // destinations, so its divisor is never larger than D while its numerator is the same live pool:
    // its bound is always >= the offer the lane already had, and `Math.Min` therefore always returned
    // the unchanged amount. (Its `needs.Count <= 1` branch returned the last slot's full need
    // unclamped in any case — audit 30 §S6, LaneCapMathTests' QUIRK characterisation.)
    //
    // ⚠ NOT inert on the kill-switch path: with SavedSettings.ConstraintAllocatorDisabled set, the old
    // prioCount loop runs, and there the group's even spread now comes from that loop's own divisor
    // instead — the auto profile's ALLAT token is no longer CAPALLAT, so the five slots are non-CAP
    // lanes and are split by prioCount. A hand-written profile that still says CAPALLAT gets neither
    // and is back to the 2026-07-11 shape.
    public class AdvancedTrainingBP : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.Energy;

        protected override bool Unlocked() => UnlockedAt(Index);

        protected override bool TargetMet() => TargetMetAt(Index);

        // `<`, not `<=`: length is a COUNT (decomp AllAdvancedTraining.length == 5) and the slots are
        // 0-based, so `<=` let AT-5 report VALID. That is worse than the equivalent RitualBP off-by-one,
        // because there is no slot 5 to no-op against: ControllerFor(5) returns null and
        // levelTarget[5] is out of bounds, so TargetMetAt/Allocate throw, CustomAllocation.RunStep
        // swallows it, and the ENTIRE energy lane is dead until the next profile load — with one log line
        // per ten minutes. No shipped profile uses AT-5, but the profile editor accepts free text.
        private bool UnlockedAt(int index) =>
            Managers.LaneCapMath.AdvancedTrainingIndexUnlocked(index, _character.advancedTrainingController.length)
            && _character.buttons.advancedTraining.interactable;

        private bool TargetMetAt(int index) =>
            Managers.LaneTargets.AdvancedTrainingTargetMet(
                _character.advancedTraining.levelTarget[index],
                _character.advancedTraining.level[index]);

        private AdvancedTrainingController ControllerFor(int index)
        {
            var allController = _character.advancedTrainingController;

            switch (index)
            {
                case 0:
                    return allController.defense;
                case 1:
                    return allController.attack;
                case 2:
                    return allController.block;
                case 3:
                    return allController.wandoosEnergy;
                case 4:
                    return allController.wandoosMagic;
            }

            return null;
        }

        public override bool Allocate()
        {
            if (_character.wishes.wishes[190].level >= 1)
                return true;
            long amount = CalculateATCap();
            SetInput(amount);
            ControllerFor(Index).addEnergy();

            return true;
        }

        private double FormulaFor(int index, int offset) =>
            Managers.LaneCapMath.AdvancedTrainingFormula(
                GetDivisor(index, offset),
                Mathf.Sqrt(_character.totalEnergyPower()),
                _character.totalAdvancedTrainingSpeedBonus());

        private long CalculateATCap()
        {
            var calcA = CalculateATCap(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateATCap(calcA.Offset);
                return calcB.Num;
            }

            return calcA.Num;
        }

        private CapCalc CalculateATCap(int offset)
        {
            var r = Managers.LaneCapMath.AdvancedTrainingCap(FormulaFor(Index, offset), MaxAllocation, _character.idleEnergy);
            return new CapCalc(r.PPT, r.Num);
        }

        private float GetDivisor(int index, int offset) =>
            Managers.LaneCapMath.AdvancedTrainingDivisor(ControllerFor(index).baseTime, _character.advancedTraining.level[index], offset);
    }
}
