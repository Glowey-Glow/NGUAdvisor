using NGUAdvisor.Managers;
using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class BestAug : AugmentBP
    {
        public int RebirthTime { get; set; }
        private bool _useUpgrades;

        protected override bool Unlocked() => _character.buttons.augmentation.interactable && !_character.challenges.noAugsChallenge.inChallenge;

        protected override bool TargetMet() => false;

        public override bool Allocate()
        {
            if (Main.Settings.MoneyPitRunMode && _character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier())
                return false;

            _useUpgrades = _character.bossID >= 37;
            return AllocatePairs() > 0;
        }

        private float AllocatePairs()
        {
            var totalAllocated = 0f;
            double gold = _character.realGold;
            var bestAugment = -1;
            var bestAugmentValue = 0.0;
            for (var i = 0; i < 7; i++)
            {
                var aug = _character.augmentsController.augments[i];
                if (aug.augLocked() || aug.hitAugmentTarget())
                    continue;

                if (_useUpgrades && aug.upgradeLocked() || aug.hitUpgradeTarget())
                    continue;

                var augTierBonus = (float)aug.augTierBonus();
                float augRatio;
                float upgRatio;

                double time;
                double timeRemaining;
                double cost;
                float progress;
                if (_useUpgrades)
                {
                    augRatio = augTierBonus / (2 + augTierBonus);
                    upgRatio = 2 / (2 + augTierBonus);
                    time = Math.Max(aug.UpgradeTimeLeftEnergyMax((long)(MaxAllocation * upgRatio)), aug.AugTimeLeftEnergyMax((long)(MaxAllocation * augRatio)));
                    if (time < 0.01) { time = 0.01; }
                    timeRemaining = aug.UpgradeTimeLeftEnergy((long)(MaxAllocation * upgRatio));
                    cost = (double)Math.Max(1, 1.0 / time) * (double)aug.getUpgradeCost();
                    progress = aug.UpgradeProgress();
                }
                else
                {
                    augRatio = 1f;
                    upgRatio = 0f;
                    time = aug.AugTimeLeftEnergyMax((long)MaxAllocation);
                    if (time < 0.01) { time = 0.01; }
                    timeRemaining = aug.AugTimeLeftEnergy((long)MaxAllocation);
                    cost = (double)Math.Max(1, 1.0 / time) * (double)aug.getAugCost();
                    progress = aug.AugProgress();
                }

                if (cost > gold && (progress == 0f || timeRemaining < 10))
                    continue;

                if (time > 300)
                    continue;

                if (RebirthTime > 0 && Main.Settings.AutoRebirth)
                {
                    if (_character.rebirthTime.totalseconds - time < 0)
                        continue;
                }

                var value = AugmentValue(i);

                if (value / time > bestAugmentValue)
                {
                    bestAugment = i;
                    bestAugmentValue = value / time;
                }

            }
            if (bestAugment != -1)
            {
                var aug = _character.augmentsController.augments[bestAugment];
                var augTierBonus = (float)aug.augTierBonus();
                float augRatio = augTierBonus / (2 + augTierBonus);
                float upgRatio = 2 / (2 + augTierBonus);
                float maxAllocation = _useUpgrades ? MaxAllocation * augRatio : MaxAllocation;
                float maxAllocationUpgrade = _useUpgrades ? MaxAllocation * upgRatio : MaxAllocation;
                var index = bestAugment * 2;
                long alloc = CalculateAugCap(index, maxAllocation);
                long alloc2 = CalculateAugCap(index + 1, maxAllocationUpgrade);
                SetInput(alloc);
                _character.augmentsController.augments[bestAugment].addEnergyAug();
                totalAllocated += alloc;
                if (_useUpgrades)
                {
                    SetInput(alloc2);
                    _character.augmentsController.augments[bestAugment].addEnergyUpgrade();
                    totalAllocated += alloc2;
                }
            }
            return totalAllocated;
        }

        private double AugmentValue(int id)
        {
            var aug = _character.augmentsController.augments[id];
            double nextValue = aug.baseBoost * Math.Max(Math.Pow(_character.augments.augs[aug.id].upgradeLevel + 500f, 2f) + 1f, 1f);
            if (_useUpgrades)
                nextValue *= Math.Pow(_character.augments.augs[aug.id].augLevel + 500f, aug.augTierBonus());
            else
                nextValue *= aug.getUpgradeBoost();

            return nextValue - aug.getTotalStatBoost();
        }
    }
}
