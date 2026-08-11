using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class TimeMachineBP : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.Energy || Type == ResourceType.Magic;

        protected override bool Unlocked() => _character.buttons.brokenTimeMachine.interactable && !_character.challenges.timeMachineChallenge.inChallenge;

        // ONE class, TWO targets on two different pools — energy funds levelSpeed toward speedTarget,
        // magic funds levelGoldMulti toward multiTarget. 05 §6.4 flags this as an open structural
        // question (is TM one consumer or two?); the extraction records it, it does not resolve it.
        protected override bool TargetMet()
        {
            long target = Type == ResourceType.Energy ? _character.machine.speedTarget : _character.machine.multiTarget;
            long level = Type == ResourceType.Energy ? _character.machine.levelSpeed : _character.machine.levelGoldMulti;

            return Managers.LaneTargets.TimeMachineTargetMet(target, level);
        }

        public override bool Allocate()
        {
            if (Type == ResourceType.Energy)
                AllocateEnergy();
            else
                AllocateMagic();
            return true;
        }

        private void AllocateEnergy()
        {
            var toAllocate = CalculateTMEnergyCap();
            SetInput(toAllocate);
            _character.timeMachineController.addEnergy();
        }

        private void AllocateMagic()
        {
            var toAllocate = CalculateTMMagicCap();
            SetInput(toAllocate);
            _character.timeMachineController.addMagic();
        }

        private long CalculateTMMagicCap()
        {
            var calcA = CalculateMagicTM(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateMagicTM(calcA.Offset);
                return calcB.Num;
            }

            return calcA.Num;
        }

        private long CalculateTMEnergyCap()
        {
            var calcA = CalculateEnergyTM(500);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateEnergyTM(calcA.Offset);
                return calcB.Num;
            }

            return calcA.Num;
        }

        #region Hidden
        // Live reads only — the arithmetic is Managers.LaneCapMath.TimeMachineCap, under
        // characterisation test. The two halves resolve to the SAME function; only the divider, the
        // level, the power and the pool differ.
        private CapCalc CalculateEnergyTM(int offset) => TmCap(
            _character.timeMachineController.baseSpeedDivider(),
            _character.machine.levelSpeed,
            _character.totalEnergyPower(),
            _character.idleEnergy,
            offset);

        private CapCalc CalculateMagicTM(int offset) => TmCap(
            _character.timeMachineController.baseGoldMultiDivider(),
            _character.machine.levelGoldMulti,
            _character.totalMagicPower(),
            _character.magic.idleMagic,
            offset);

        private CapCalc TmCap(double baseDivider, float level, double power, long idlePool, int offset)
        {
            bool sadistic = _character.settings.rebirthDifficulty >= difficulty.sadistic;
            var r = Managers.LaneCapMath.TimeMachineCap(new Managers.LaneCapMath.TimeMachineCapInputs
            {
                BaseDivider = baseDivider,
                Level = level,
                Offset = offset,
                Power = power,
                HackTmSpeed = _character.hacksController.totalTMSpeedBonus(),
                ChallengeTmSpeed = _character.allChallenges.timeMachineChallenge.TMSpeedBonus(),
                CardTmSpeed = _character.cardsController.getBonus(cardBonus.TMSpeed),
                Sadistic = sadistic,
                SadisticDivider = sadistic ? _character.timeMachineController.sadisticDivider() : 1.0,
                MaxAllocation = MaxAllocation,
                IdlePool = idlePool
            });
            return new CapCalc(r.PPT, r.Num);
        }
        #endregion

    }
}
