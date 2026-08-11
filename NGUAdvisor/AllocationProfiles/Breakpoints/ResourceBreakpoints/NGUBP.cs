using NGUAdvisor.Managers;
using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class NGUBP : ResourceBreakpoint
    {
        protected override bool Unlocked()
        {
            switch (Type)
            {
                case ResourceType.Magic when Index <= 6:
                case ResourceType.Energy when Index <= 8:
                    return _character.buttons.ngu.interactable;
            }

            return false;
        }

        protected override bool TargetMet()
        {
            var track = _character.settings.nguLevelTrack;
            var ngus = Type == ResourceType.Energy ? _character.NGU.skills : _character.NGU.magicSkills;
            long target;
            long level;
            switch (track)
            {
                case difficulty.normal:
                    target = ngus[Index].target;
                    level = ngus[Index].level;
                    break;
                case difficulty.evil:
                    target = ngus[Index].evilTarget;
                    level = ngus[Index].evilLevel;
                    break;
                default:
                    target = ngus[Index].sadisticTarget;
                    level = ngus[Index].sadisticLevel;
                    break;
            }

            return NguValueMath.NguTargetMet(target, level);
        }

        public override bool Allocate()
        {
            if (Type == ResourceType.Energy)
                AllocateEnergy();
            else
                AllocateMagic();

            return true;
        }

        private void AllocateMagic()
        {
            var alloc = CalculateNGUMagicCap();
            SetInput(alloc);
            _character.NGUController.NGUMagic[Index].add();
        }

        private void AllocateEnergy()
        {
            var alloc = CalculateNGUEnergyCap();
            SetInput(alloc);
            _character.NGUController.NGU[Index].add();
        }

        protected override bool CorrectResourceType() => Type == ResourceType.Energy || Type == ResourceType.Magic;

        private long CalculateNGUEnergyCap()
        {
            var calcA = GetNGUEnergyCapCalc(500);
            if (calcA.PPT < 1)
            {
                var calcB = GetNGUEnergyCapCalc(calcA.Offset);
                return calcB.Num;
            }

            return calcA.Num;
        }

        private long CalculateNGUMagicCap()
        {
            var calcA = GetNGUMagicCapCalc(500);
            if (calcA.PPT < 1)
            {
                var calcB = GetNGUMagicCapCalc(calcA.Offset);
                return calcB.Num;
            }

            return calcA.Num;
        }

        // Live reads only. The stair-snap arithmetic is NguValueMath.NguCap, under characterisation
        // test. num2 is assembled HERE, in the game's own left-to-right order, and handed over as one
        // number: floating-point multiplication is not associative and this value feeds two
        // Math.Ceiling calls, where a 1-ulp shift is a whole allocation unit.
        private CapCalc GetNGUEnergyCapCalc(int offset)
        {
            var num1 = 0.0f;
            if (_character.settings.nguLevelTrack == difficulty.normal)
                num1 = _character.NGU.skills[Index].level + 1L + offset;
            else if (_character.settings.nguLevelTrack == difficulty.evil)
                num1 = _character.NGU.skills[Index].evilLevel + 1L + offset;
            else if (_character.settings.nguLevelTrack == difficulty.sadistic)
                num1 = _character.NGU.skills[Index].sadisticLevel + 1L + offset;

            var num2 = _character.totalEnergyPower() * (double)_character.totalNGUSpeedBonus();
            num2 *= _character.adventureController.itopod.totalEnergyNGUBonus() * _character.inventory.macguffinBonuses[4];
            num2 *= _character.NGUController.energyNGUBonus() * _character.allDiggers.totalEnergyNGUBonus();
            num2 *= _character.hacksController.totalEnergyNGUBonus() * _character.beastQuestPerkController.totalEnergyNGUSpeed();
            num2 *= _character.wishesController.totalEnergyNGUSpeed() * _character.cardsController.getBonus(cardBonus.energyNGUSpeed);
            if (_character.allChallenges.trollChallenge.sadisticCompletions() >= 1)
                num2 *= 3.0;
            if (_character.settings.nguLevelTrack >= difficulty.sadistic)
                num2 /= _character.NGUController.NGU[0].sadisticDivider();

            var r = NguValueMath.NguCap(new NguValueMath.NguCapInputs
            {
                LevelPlusOnePlusOffset = num1,
                Num2 = num2,
                SpeedDivider = _character.NGUController.energySpeedDivider(Index),
                MaxAllocation = MaxAllocation,
                IdlePool = _character.idleEnergy
            });
            return new CapCalc(r.PPT, r.Num);
        }

        private CapCalc GetNGUMagicCapCalc(int offset)
        {
            var num1 = 0.0f;
            if (_character.settings.nguLevelTrack == difficulty.normal)
                num1 = _character.NGU.magicSkills[Index].level + 1L + offset;
            else if (_character.settings.nguLevelTrack == difficulty.evil)
                num1 = _character.NGU.magicSkills[Index].evilLevel + 1L + offset;
            else if (_character.settings.nguLevelTrack == difficulty.sadistic)
                num1 = _character.NGU.magicSkills[Index].sadisticLevel + 1L + offset;

            var num2 = _character.totalMagicPower() * (double)_character.totalNGUSpeedBonus();
            num2 *= _character.adventureController.itopod.totalMagicNGUBonus() * _character.inventory.macguffinBonuses[5];
            num2 *= _character.NGUController.magicNGUBonus() * _character.allDiggers.totalMagicNGUBonus();
            num2 *= _character.hacksController.totalMagicNGUBonus() * _character.beastQuestPerkController.totalMagicNGUSpeed();
            num2 *= _character.wishesController.totalMagicNGUSpeed() * _character.cardsController.getBonus(cardBonus.magicNGUSpeed);
            if (_character.allChallenges.trollChallenge.completions() >= 1)
                num2 *= 3.0;
            if (_character.settings.nguLevelTrack >= difficulty.sadistic)
                num2 /= _character.NGUController.NGUMagic[0].sadisticDivider();

            var r = NguValueMath.NguCap(new NguValueMath.NguCapInputs
            {
                LevelPlusOnePlusOffset = num1,
                Num2 = num2,
                SpeedDivider = _character.NGUController.magicSpeedDivider(Index),
                MaxAllocation = MaxAllocation,
                IdlePool = _character.magic.idleMagic
            });
            return new CapCalc(r.PPT, r.Num);
        }
    }
}
