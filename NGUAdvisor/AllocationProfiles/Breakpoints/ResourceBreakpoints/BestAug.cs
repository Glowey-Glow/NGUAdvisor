using NGUAdvisor.Managers;
using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    // Picks the augment pair worth funding right now. Ranking is a HORIZON PROJECTION: for each aug,
    // how much stat boost would this energy share actually buy over the next `Horizon()` seconds?
    // That is a true gain-per-second (the horizon is shared, so the division is a no-op) and it lets
    // an expensive-but-steep aug beat a cheap shallow one on its merits.
    //
    // It replaces the old `if (time > 300) continue;` cutoff, which abandoned any aug whose NEXT LEVEL
    // cost more than five minutes — a pure cost test with no reference to what the level was worth.
    // In practice that dropped the laser sword at ~lv 8 every run regardless of value, because aug 6
    // has both the largest baseBoost and the largest augTierBonus exponent and therefore also the
    // steepest cost curve. Cost still matters, but now only through how many levels the horizon buys.
    public class BestAug : AugmentBP
    {
        public int RebirthTime { get; set; }
        private bool _useUpgrades;

        // How far ahead the projection looks. An hour is long enough that a slow, steep aug can show
        // its value and short enough that the linear cost model below stays honest.
        private const double MaxHorizon = 3600.0;

        protected override bool Unlocked() => _character.buttons.augmentation.interactable && !_character.challenges.noAugsChallenge.inChallenge;

        protected override bool TargetMet() => false;

        public override bool Allocate()
        {
            if (Main.Settings.MoneyPitRunMode && _character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier())
                return false;

            _useUpgrades = _character.bossID >= 37;
            return AllocatePairs() > 0;
        }

        // Seconds until the rebirth, capped at MaxHorizon. This subsumes the old "will it finish before
        // rebirth" guard, which compared the next level's cost against the run's ELAPSED time
        // (`rebirthTime.totalseconds - time < 0`) rather than its REMAINING time — backwards, and dead
        // after the first 300s of any run because the old cutoff already bounded `time`. Levels that
        // cannot land before the rebirth now simply project no gain. Cf. BR.cs, which had it right.
        private double Horizon()
        {
            double h = MaxHorizon;
            if (RebirthTime > 0 && Main.Settings.AutoRebirth)
            {
                double left = RebirthTime - _character.rebirthTime.totalseconds;
                if (left < h) h = left;
            }
            return h > 0 ? h : 0;
        }

        // Levels gained in `horizon` seconds. Cost per level is linear in the level
        // (getAugProgressPerTick divides by level+1), so with c = secPerLevel / (level+1) the time to
        // gain n levels is c * (n*level + n(n+1)/2); invert for n.
        private static double LevelsInHorizon(double secPerLevel, double level, double horizon)
        {
            if (secPerLevel <= 0 || horizon <= 0) return 0;
            double c = secPerLevel / (level + 1.0);
            double b = 2.0 * level + 1.0;
            double n = (-b + Math.Sqrt(b * b + 8.0 * horizon / c)) / 2.0;
            return n > 0 ? n : 0;
        }

        // Which halves of the pair can still take energy. An aug is a candidate if EITHER half is live:
        // the old test skipped the whole aug the moment one target was met (and `_useUpgrades &&
        // upgradeLocked() || hitUpgradeTarget()` bound as `(_useUpgrades && upgradeLocked()) ||
        // hitUpgradeTarget()`, so a met UPGRADE target starved the aug half too, even pre-boss-37).
        private void LiveHalves(AugmentController aug, out bool augLive, out bool upgLive)
        {
            augLive = !aug.augLocked() && !aug.hitAugmentTarget();
            upgLive = _useUpgrades && !aug.upgradeLocked() && !aug.hitUpgradeTarget();
        }

        // Energy split by elasticity: boost goes as augLevel^tier x upgradeLevel^2, so the exponents
        // tier and 2 are the shares. A dead half yields its share to the live one.
        private static void Split(double tier, bool augLive, bool upgLive, out float augRatio, out float upgRatio)
        {
            if (augLive && upgLive)
            {
                augRatio = (float)(tier / (2.0 + tier));
                upgRatio = (float)(2.0 / (2.0 + tier));
            }
            else
            {
                augRatio = augLive ? 1f : 0f;
                upgRatio = upgLive ? 1f : 0f;
            }
        }

        private long Share(float ratio) => ratio <= 0 ? 0 : Math.Max(1, (long)(MaxAllocation * ratio));

        private float AllocatePairs()
        {
            double horizon = Horizon();
            if (horizon <= 0) return 0;

            double gold = _character.realGold;
            var bestAugment = -1;
            var bestValue = 0.0;

            for (var i = 0; i < 7; i++)
            {
                var aug = _character.augmentsController.augments[i];
                LiveHalves(aug, out bool augLive, out bool upgLive);
                if (!augLive && !upgLive)
                    continue;

                double tier = aug.augTierBonus();
                Split(tier, augLive, upgLive, out float augRatio, out float upgRatio);

                double augSec = augLive ? Math.Max(0.01, aug.AugTimeLeftEnergyMax(Share(augRatio))) : 0;
                double upgSec = upgLive ? Math.Max(0.01, aug.UpgradeTimeLeftEnergyMax(Share(upgRatio))) : 0;

                // Gold gate on the half we would actually start. A level already in progress, or one
                // about to land, is worth waiting on; a cold one we cannot pay for is not.
                double time = Math.Max(augSec, upgSec);
                double cost = Math.Max(1, 1.0 / time) * (upgLive ? (double)aug.getUpgradeCost() : (double)aug.getAugCost());
                float progress = upgLive ? aug.UpgradeProgress() : aug.AugProgress();
                double timeRemaining = upgLive
                    ? aug.UpgradeTimeLeftEnergy(Share(upgRatio))
                    : aug.AugTimeLeftEnergy(Share(augRatio));
                if (cost > gold && (progress == 0f || timeRemaining < 10))
                    continue;

                double value = ProjectedGain(aug, augLive, upgLive, augSec, upgSec, tier, horizon);
                if (value > bestValue)
                {
                    bestAugment = i;
                    bestValue = value;
                }
            }

            if (bestAugment == -1)
                return 0;

            var best = _character.augmentsController.augments[bestAugment];
            LiveHalves(best, out bool bestAugLive, out bool bestUpgLive);
            Split(best.augTierBonus(), bestAugLive, bestUpgLive, out float bestAugRatio, out float bestUpgRatio);

            var totalAllocated = 0f;
            var index = bestAugment * 2;
            if (bestAugLive)
            {
                long alloc = CalculateAugCap(index, Share(bestAugRatio));
                SetInput(alloc);
                best.addEnergyAug();
                totalAllocated += alloc;
            }
            if (bestUpgLive)
            {
                long alloc = CalculateAugCap(index + 1, Share(bestUpgRatio));
                SetInput(alloc);
                best.addEnergyUpgrade();
                totalAllocated += alloc;
            }
            return totalAllocated;
        }

        // Stat boost this pair would hold at the end of the horizon, minus what it holds now. The boost
        // formula is the game's own (AugmentController.getTotalStatBoost):
        //     baseBoost x (upgradeLevel^2 + 1) x augLevel^augTierBonus
        private double ProjectedGain(AugmentController aug, bool augLive, bool upgLive, double augSec, double upgSec, double tier, double horizon)
        {
            double augLv = _character.augments.augs[aug.id].augLevel;
            double upgLv = _character.augments.augs[aug.id].upgradeLevel;

            double newAug = augLive ? augLv + LevelsInHorizon(augSec, augLv, horizon) : augLv;
            double newUpg = upgLive ? upgLv + LevelsInHorizon(upgSec, upgLv, horizon) : upgLv;

            double projected = (double)aug.baseBoost * (Math.Pow(newUpg, 2.0) + 1.0) * Math.Pow(newAug, tier);
            return projected - aug.getTotalStatBoost();
        }
    }
}
