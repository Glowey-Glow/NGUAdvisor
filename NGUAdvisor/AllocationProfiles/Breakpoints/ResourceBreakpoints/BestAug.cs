using NGUAdvisor.Managers;
using System;
using System.Collections.Generic;

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
    // THE DECISION NOW LIVES IN Managers/AugmentMath (audit 01 §3.4, extraction E1). This class is the
    // live-state half: it reads the game, hands the core plain data, and applies what the core picked.
    // The projection, the split, the gold gate and the ranking are all in AugmentMath, under
    // characterisation test in tests/NGUAdvisor.Tests/AugmentMathTests.cs.
    public class BestAug : AugmentBP
    {
        // Boss 37 is where the upgrade halves become fundable. Was a field assigned at the top of
        // Allocate(); TargetMet() runs BEFORE Allocate() (ResourceBreakpoint.IsValid evaluates all
        // three terms eagerly), so it has to be readable without having allocated first. bossID does
        // not move inside a pass, so a property is exactly the old value with no ordering to get wrong.
        private bool _useUpgrades => _character.bossID >= 37;

        // D1 REVERSED (amendment 30), same as AugmentBP: the advisor honours the No Augs challenge.
        // Both refusals that used to be on this line were one lock written twice ([DECOMP]
        // ButtonShower.cs:199-203), and they are now the single AugmentMath.AugmentMenuUnlocked call
        // — :199 in full, both terms. SurfaceNoAugs (inherited from AugmentBP, one shared latch)
        // reports the refusal once for the augment system.
        protected override bool Unlocked()
        {
            bool inChallenge = _character.challenges.noAugsChallenge.inChallenge;
            SurfaceNoAugs(inChallenge);

            return AugmentMath.AugmentMenuUnlocked(_character.bossID, inChallenge);
        }

        // WIRED to the game's own terminator (decision record amendment 16 §7; audit 20 §2.7). The game
        // answers "is this half done?" itself — hitAugmentTarget() / hitUpgradeTarget(), [DECOMP]
        // AugmentController.cs:171-186 — and on hit its own tick cascades the allocation to the next
        // unmet pair (moveToNextAug / moveToNextUpgrade, AllAugsController.cs:147-199). BestAug used to
        // discard that signal and hardcode `false`, so the lane could never report DONE, could never
        // release its priority seat, and kept a share of idle energy reserved for 7 pairs that were all
        // either boss-locked or already at target.
        //
        // The whole-lane question is NOT AugmentBP's per-half one: BestAug ranks all seven pairs, so it
        // is done only when no half of any pair is still live. Arithmetic in AugmentMath.BestAugTargetMet.
        protected override bool TargetMet() =>
            AugmentMath.BestAugTargetMet(PairTargetStates(), _useUpgrades);

        // The 7 pairs as four game predicates each. Same two reads LiveHalves() makes, in the same
        // order — HalfLive() is the shared expression, so "live enough to rank" and "not done" are one
        // definition rather than two that can drift.
        private List<AugmentMath.AugPairTargetState> PairTargetStates()
        {
            var pairs = new List<AugmentMath.AugPairTargetState>(7);
            for (var i = 0; i < 7; i++)
            {
                var aug = _character.augmentsController.augments[i];
                pairs.Add(new AugmentMath.AugPairTargetState
                {
                    AugLocked = aug.augLocked(),
                    AugHitTarget = aug.hitAugmentTarget(),
                    UpgradeLocked = aug.upgradeLocked(),
                    UpgradeHitTarget = aug.hitUpgradeTarget()
                });
            }
            return pairs;
        }

        public override bool Allocate()
        {
            if (Main.Settings.MoneyPitRunMode && _character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier())
                return false;

            return AllocatePairs() > 0;
        }

        // The run's planned end, read from the LIVE profile (Main.Profile, as BloodPlanner and
        // WandoosAdvisor do): the breakpoint parser never populated a RebirthTime on BESTAUG, so the
        // property this used to read was always 0 and its guard — the one the rewrite claimed replaced
        // the old `time > 300` cutoff — could never fire. Cf. BR.cs, whose copy is unwired the same way.
        // The horizon arithmetic itself is AugmentMath.Horizon.
        private double Horizon(out bool toRebirth)
        {
            double target = -1;
            try { target = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1; } catch { }
            return AugmentMath.Horizon(Main.Settings.AutoRebirth, target, _character.rebirthTime.totalseconds, out toRebirth);
        }

        // Which halves of the pair can still take energy. An aug is a candidate if EITHER half is live:
        // the old test skipped the whole aug the moment one target was met (and `_useUpgrades &&
        // upgradeLocked() || hitUpgradeTarget()` bound as `(_useUpgrades && upgradeLocked()) ||
        // hitUpgradeTarget()`, so a met UPGRADE target starved the aug half too, even pre-boss-37).
        private void LiveHalves(AugmentController aug, out bool augLive, out bool upgLive)
        {
            augLive = AugmentMath.HalfLive(aug.augLocked(), aug.hitAugmentTarget());
            upgLive = _useUpgrades && AugmentMath.HalfLive(aug.upgradeLocked(), aug.hitUpgradeTarget());
        }

        // Read every live fact the ranking needs into plain data, then let AugmentMath rank it. The
        // Split-before-Share-before-TimeLeftEnergyMax ordering is load-bearing and preserved: a half's
        // seconds-per-level is priced AT THE SHARE that half would actually receive, so the split has to
        // be known before the rate can be read.
        private float AllocatePairs()
        {
            double horizon = Horizon(out bool toRebirth);
            double gold = _character.realGold;

            var pairs = new List<AugmentMath.AugPairState>(7);
            for (var i = 0; i < 7; i++)
            {
                var aug = _character.augmentsController.augments[i];
                LiveHalves(aug, out bool augLive, out bool upgLive);
                if (!augLive && !upgLive)
                    continue;

                double tier = aug.augTierBonus();
                AugmentMath.Split(tier, augLive, upgLive, out float augRatio, out float upgRatio);

                // Full-level cost. TimeLeftEnergy is just TimeLeftEnergyMax x (1 - progress), so the
                // core derives the level-in-flight remainder instead of paying the game's rate call a
                // second time per half.
                pairs.Add(new AugmentMath.AugPairState
                {
                    Index = i,
                    AugLive = augLive,
                    UpgLive = upgLive,
                    Tier = tier,
                    BaseBoost = aug.baseBoost,
                    AugLevel = _character.augments.augs[aug.id].augLevel,
                    UpgradeLevel = _character.augments.augs[aug.id].upgradeLevel,
                    AugProgress = aug.AugProgress(),
                    UpgradeProgress = aug.UpgradeProgress(),
                    AugSecPerLevel = augLive ? Math.Max(0.01, aug.AugTimeLeftEnergyMax(AugmentMath.Share(MaxAllocation, augRatio))) : 0,
                    UpgSecPerLevel = upgLive ? Math.Max(0.01, aug.UpgradeTimeLeftEnergyMax(AugmentMath.Share(MaxAllocation, upgRatio))) : 0,
                    AugCost = aug.getAugCost(),
                    UpgradeCost = aug.getUpgradeCost(),
                    TotalStatBoostNow = aug.getTotalStatBoost()
                });
            }

            var pick = AugmentMath.PickBest(pairs, gold, horizon, toRebirth);
            if (!pick.Found)
                return 0;

            var best = _character.augmentsController.augments[pick.Index];
            var totalAllocated = 0f;
            var index = pick.Index * 2;
            if (pick.AugLive)
            {
                long alloc = CalculateAugCap(index, AugmentMath.Share(MaxAllocation, pick.AugRatio));
                SetInput(alloc);
                best.addEnergyAug();
                totalAllocated += alloc;
            }
            if (pick.UpgLive)
            {
                long alloc = CalculateAugCap(index + 1, AugmentMath.Share(MaxAllocation, pick.UpgRatio));
                SetInput(alloc);
                best.addEnergyUpgrade();
                totalAllocated += alloc;
            }
            return totalAllocated;
        }
    }
}
