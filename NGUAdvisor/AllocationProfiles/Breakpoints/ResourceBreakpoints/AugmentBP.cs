using NGUAdvisor.Managers;
using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class AugmentBP : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.Energy;

        // D1 REVERSED (amendment 30): the advisor honours the No Augs challenge and refuses to fund
        // augments for its duration. The two refusals that used to sit here —
        // `!buttons.augmentation.interactable` and `!noAugsChallenge.inChallenge` — were the SAME
        // lock twice: the button is set false by exactly that challenge flag ([DECOMP]
        // ButtonShower.cs:199-203). They are now ONE call to AugmentMath.AugmentMenuUnlocked, which
        // is :199 in full. See its comment for why an unenforced rule is still obeyed.
        protected override bool Unlocked()
        {
            // Read once and hand it to both the predicate and the surfacing, so the gate and the
            // reason it reports can never disagree. Read directly, not through SurfaceNoAugs's
            // try/catch: a swallowed exception there would default to FUNDING through the challenge.
            bool inChallenge = _character.challenges.noAugsChallenge.inChallenge;

            // Before the gates, so the latch clears on the way out of the challenge no matter which
            // gate the lane goes on to fail, and the line is one per challenge entry.
            SurfaceNoAugs(inChallenge);

            if (!AugmentMath.AugmentMenuUnlocked(_character.bossID, inChallenge))
                return false;

            if (Index > 13)
                return false;

            var pair = _character.augmentsController.augments[Index / 2];
            return AugmentMath.AugmentIndexUnlocked(Index, _character.bossID, pair.augBossRequired, pair.upgradeBossRequired);
        }

        // The refusal is surfaced — a lane that goes quiet is indistinguishable from a lane that
        // broke (25 §4, at two hours' cost). Latched on the TRANSITION so it is one line per
        // challenge entry rather than one per tick, and the latch clears when the challenge ends so
        // a second No Augs run says it again. Static and protected — BestAug shares it, so the line
        // is emitted once for the augment SYSTEM, not once per lane that happens to be refused.
        private static bool _noAugsSurfaced;

        protected void SurfaceNoAugs(bool inChallenge)
        {
            try
            {
                var line = AugmentMath.NoAugsSurfacingLine(inChallenge, _noAugsSurfaced);
                if (line != null)
                    Main.Log(line);
                _noAugsSurfaced = inChallenge;
            }
            catch { }
        }

        protected override bool TargetMet()
        {
            var augs = _character.augments.augs[Index / 2];
            return Index % 2 == 0
                ? AugmentMath.AugmentTargetMet(Index, augs.augmentTarget, augs.augLevel)
                : AugmentMath.AugmentTargetMet(Index, augs.upgradeTarget, augs.upgradeLevel);
        }

        public override bool Allocate()
        {
            if (Main.Settings.MoneyPitRunMode && _character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier())
                return false;

            double gold = _character.realGold;
            var aug = _character.augmentsController.augments[Index / 2];

            long alloc = CalculateAugCap(Index, MaxAllocation);
            SetInput(alloc);

            float progress = Index % 2 == 0 ? aug.AugProgress() : aug.UpgradeProgress();

            // If aug has no progress check the cost before allocating energy
            if (progress == 0f)
            {
                double time = Index % 2 == 0 ? aug.AugTimeLeftEnergyMax(alloc) : aug.UpgradeTimeLeftEnergyMax(alloc);
                if (time < 0.01) { time = 0.01d; }
                // the cost is a rough estimate of running for 1 second, min of basecost, max of basecost*100;
                // does not consider the rising price of augments nor the gold that will be gained until the next energy allocation
                double cost = (double)Math.Max(1, 1d / time) * (Index % 2 == 0 ? (double)aug.getAugCost() : (double)aug.getUpgradeCost());

                if (cost > gold)
                    return false;
            }

            if (Index % 2 == 0)
                aug.addEnergyAug();
            else
                aug.addEnergyUpgrade();

            return true;
        }

        public long CalculateAugCap(int index, float allocation)
        {
            var calcA = CalculateAugCapCalc(500, index, allocation);
            if (calcA.PPT < 1)
            {
                var calcB = CalculateAugCapCalc(calcA.Offset, index, allocation);
                return calcB.Num;
            }

            return calcA.Num;
        }

        // Live reads only — the arithmetic is AugmentMath.AugCap, under characterisation test.
        // `index` is the FLAT half-index: even = augment half of pair index/2, odd = its upgrade half.
        public CapCalc CalculateAugCapCalc(int offset, int index, float allocation)
        {
            bool augHalf = index % 2 == 0;
            int augIndex = augHalf ? index / 2 : (index - 1) / 2;
            var ac = _character.augmentsController;
            var diff = _character.settings.rebirthDifficulty;
            var noAugs = _character.allChallenges.noAugsChallenge;

            // The difficulty branch is resolved here, not in the core: normal and evil scale their
            // divider by 50000.0 and sadistic does not, so the pair (scale, divider) carries the whole
            // branch. Any difficulty outside the three leaves num1 unscaled, exactly as before.
            double scale = 1.0, divider = 1.0;
            if (diff == difficulty.normal)
            {
                scale = 50000.0;
                divider = augHalf ? ac.normalAugSpeedDividers[augIndex] : ac.normalUpgradeSpeedDividers[augIndex];
            }
            else if (diff == difficulty.evil)
            {
                scale = 50000.0;
                divider = augHalf ? ac.evilAugSpeedDividers[augIndex] : ac.evilUpgradeSpeedDividers[augIndex];
            }
            else if (diff == difficulty.sadistic)
            {
                divider = augHalf ? ac.sadisticAugSpeedDividers[augIndex] : ac.sadisticUpgradeSpeedDividers[augIndex];
            }

            var r = AugmentMath.AugCap(new AugmentMath.AugCapInputs
            {
                Level = augHalf ? _character.augments.augs[augIndex].augLevel : _character.augments.augs[augIndex].upgradeLevel,
                Offset = offset,
                TotalEnergyPower = _character.totalEnergyPower(),
                SpeedDivider = divider,
                DividerScale = scale,
                AugsSpecBonus = _character.inventoryController.bonuses[specType.Augs],
                MacguffinBonus = _character.inventory.macguffinBonuses[12],
                HackAugSpeed = _character.hacksController.totalAugSpeedBonus(),
                ItopodAugSpeed = _character.adventureController.itopod.totalAugSpeedBonus(),
                CardAugSpeed = _character.cardsController.getBonus(cardBonus.augSpeed),
                NoAugsEvilCompletions = noAugs.evilCompletions(),
                NoAugsCompletedOnce = noAugs.completions() >= 1,
                NoAugsEvilMaxed = noAugs.evilCompletions() >= noAugs.maxCompletions,
                Sadistic = diff >= difficulty.sadistic,
                SadisticDivider = diff >= difficulty.sadistic ? ac.augments[augIndex].sadisticDivider() : 1.0,
                Allocation = allocation,
                IdleEnergy = _character.idleEnergy
            });

            return new CapCalc(r.PPT, r.Num);
        }
    }
}
