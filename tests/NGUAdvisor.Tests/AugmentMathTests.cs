using System;
using System.Collections.Generic;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // CHARACTERISATION tests for AugmentMath (extraction E1).
    //
    // These pin the behaviour BestAug/AugmentBP had before the Unity-free core was lifted out of them.
    // They are deliberately not "is this right?" tests — several assertions below pin behaviour that is
    // arguably wrong (see the [QUIRK] tests at the bottom). Their job is to make the extraction provably
    // behaviour-preserving and to make any later fix show up as a deliberate, visible test edit.
    public class AugmentMathTests
    {
        private static AugmentMath.AugPairState Pair(
            int index = 0, bool augLive = true, bool upgLive = true,
            double tier = 1.0, double baseBoost = 100, double augLevel = 10, double upgLevel = 5,
            float augProgress = 0f, float upgProgress = 0f,
            double augSec = 10, double upgSec = 10,
            double augCost = 0, double upgradeCost = 0, double statBoostNow = 0) =>
            new AugmentMath.AugPairState
            {
                Index = index,
                AugLive = augLive,
                UpgLive = upgLive,
                Tier = tier,
                BaseBoost = baseBoost,
                AugLevel = augLevel,
                UpgradeLevel = upgLevel,
                AugProgress = augProgress,
                UpgradeProgress = upgProgress,
                AugSecPerLevel = augLive ? augSec : 0,
                UpgSecPerLevel = upgLive ? upgSec : 0,
                AugCost = augCost,
                UpgradeCost = upgradeCost,
                TotalStatBoostNow = statBoostNow
            };

        // ---------------- Horizon ----------------

        [Fact]
        public void Horizon_WithoutAutoRebirth_IsAlwaysTheFullHour()
        {
            double h = AugmentMath.Horizon(autoRebirth: false, rebirthTargetSec: 60, nowSec: 0, out bool toRebirth);
            Assert.Equal(AugmentMath.MaxHorizon, h);
            Assert.False(toRebirth);
        }

        [Fact]
        public void Horizon_WithNoScheduledRebirth_IsTheFullHour()
        {
            // NextRebirthTargetSeconds() returns -1 when rebirth is disabled/unset (NORB, RebirthTime -1).
            Assert.Equal(AugmentMath.MaxHorizon, AugmentMath.Horizon(true, -1, 100, out bool a));
            Assert.False(a);
            Assert.Equal(AugmentMath.MaxHorizon, AugmentMath.Horizon(true, 0, 100, out bool b));
            Assert.False(b);
        }

        [Fact]
        public void Horizon_InsideTheHour_EndsAtTheRebirthAndSetsToRebirth()
        {
            double h = AugmentMath.Horizon(true, rebirthTargetSec: 86400, nowSec: 86400 - 900, out bool toRebirth);
            Assert.Equal(900, h);
            Assert.True(toRebirth);
        }

        [Fact]
        public void Horizon_PastTheDeadline_ReopensToTheFullHourRatherThanGoingDark()
        {
            // The rebirth can be blocked (NUMBER/BOSSNUM floors, locks, NORB) so the run continues.
            double h = AugmentMath.Horizon(true, rebirthTargetSec: 86400, nowSec: 90000, out bool toRebirth);
            Assert.Equal(AugmentMath.MaxHorizon, h);
            Assert.False(toRebirth);
        }

        [Fact]
        public void Horizon_ExactlyOneHourOut_StaysOnTheFullHorizonAndNotToRebirth()
        {
            // `left >= MaxHorizon` is inclusive, so 3600s left is NOT treated as a rebirth-bounded horizon.
            double h = AugmentMath.Horizon(true, rebirthTargetSec: 3600, nowSec: 0, out bool toRebirth);
            Assert.Equal(AugmentMath.MaxHorizon, h);
            Assert.False(toRebirth);
        }

        // ---------------- LevelsInHorizon ----------------

        [Fact]
        public void LevelsInHorizon_ZeroRateOrZeroHorizon_IsZero()
        {
            Assert.Equal(0, AugmentMath.LevelsInHorizon(0, 1, 10, 3600, false));
            Assert.Equal(0, AugmentMath.LevelsInHorizon(10, 1, 10, 0, false));
        }

        [Fact]
        public void LevelsInHorizon_InsideTheLevelInFlight_IsTheLinearFraction()
        {
            // horizon <= secLeft: still inside the level already in progress.
            Assert.Equal(0.5, AugmentMath.LevelsInHorizon(secPerLevel: 100, secLeft: 40, level: 10, horizon: 20, completedOnly: false));
        }

        [Fact]
        public void LevelsInHorizon_BeyondTheLevelInFlight_UsesTheQuadraticInverse()
        {
            // c = secPerLevel/(level+1) = 10/11; b = 2*11+1 = 23; t = 3600-10 = 3590.
            double c = 10.0 / 11.0, b = 23.0, t = 3590.0;
            double expected = 1.0 + (-b + Math.Sqrt(b * b + 8.0 * t / c)) / 2.0;
            Assert.Equal(expected, AugmentMath.LevelsInHorizon(10, 10, 10, 3600, false), 10);
        }

        [Fact]
        public void LevelsInHorizon_CompletedOnly_FloorsTheResult()
        {
            double raw = AugmentMath.LevelsInHorizon(100, 100, 10, 350, completedOnly: false);
            double floored = AugmentMath.LevelsInHorizon(100, 100, 10, 350, completedOnly: true);
            Assert.True(raw > floored);
            Assert.Equal(Math.Floor(raw), floored);
        }

        [Fact]
        public void LevelsInHorizon_CompletedOnly_CanReturnZeroWhenNoLevelLands()
        {
            // At the rebirth a level still in flight is wiped and worth nothing.
            Assert.Equal(0, AugmentMath.LevelsInHorizon(secPerLevel: 100, secLeft: 100, level: 10, horizon: 50, completedOnly: true));
        }

        [Theory]
        [InlineData(0)]      // no progress data
        [InlineData(-5)]     // negative
        [InlineData(999)]    // larger than a full level
        public void LevelsInHorizon_OddSecLeft_FallsBackToAFullLevel(double secLeft)
        {
            double expected = AugmentMath.LevelsInHorizon(100, 100, 10, 250, false);
            Assert.Equal(expected, AugmentMath.LevelsInHorizon(100, secLeft, 10, 250, false));
        }

        // ---------------- Split / Share ----------------

        [Fact]
        public void Split_BothHalvesLive_SplitsByTheBoostExponents()
        {
            AugmentMath.Split(tier: 2.0, augLive: true, upgLive: true, out float a, out float u);
            Assert.Equal(0.5f, a, 6);        // tier/(2+tier)
            Assert.Equal(0.5f, u, 6);        // 2/(2+tier)
            Assert.Equal(1.0f, a + u, 6);
        }

        [Fact]
        public void Split_OneHalfDead_YieldsTheWholeShareToTheLiveOne()
        {
            AugmentMath.Split(3.0, augLive: true, upgLive: false, out float a1, out float u1);
            Assert.Equal(1f, a1);
            Assert.Equal(0f, u1);

            AugmentMath.Split(3.0, augLive: false, upgLive: true, out float a2, out float u2);
            Assert.Equal(0f, a2);
            Assert.Equal(1f, u2);
        }

        [Fact]
        public void Split_BothDead_IsZeroZero()
        {
            AugmentMath.Split(3.0, false, false, out float a, out float u);
            Assert.Equal(0f, a);
            Assert.Equal(0f, u);
        }

        [Fact]
        public void Share_ALiveHalfAlwaysGetsAtLeastOneUnit()
        {
            // (long)(10 * 0.01f) == 0, but a live half is floored at 1.
            Assert.Equal(1, AugmentMath.Share(10, 0.01f));
        }

        [Fact]
        public void Share_ADeadHalfGetsNothing()
        {
            Assert.Equal(0, AugmentMath.Share(1_000_000, 0f));
        }

        [Fact]
        public void Share_TruncatesRatherThanRounds()
        {
            Assert.Equal(333, AugmentMath.Share(1000, 0.3334f));
        }

        // ---------------- ProjectedGain ----------------

        [Fact]
        public void ProjectedGain_IsTheGameBoostFormulaDeltaOverTheHorizon()
        {
            // Halves so slow that no COMPLETED level lands inside the horizon, so with toRebirth the
            // projection collapses to the current levels and the formula is exact.
            var p = Pair(tier: 2.0, baseBoost: 10, augLevel: 4, upgLevel: 3, augSec: 1e9, upgSec: 1e9, statBoostNow: 100);
            double expected = 10 * (Math.Pow(3, 2) + 1) * Math.Pow(4, 2) - 100;
            Assert.Equal(expected, AugmentMath.ProjectedGain(p, 3600, toRebirth: true), 6);
        }

        [Fact]
        public void ProjectedGain_MidRun_PricesTheFractionOfALevelInFlight()
        {
            // The same pair mid-run is worth slightly MORE than at the rebirth: banked progress counts.
            var p = Pair(tier: 2.0, baseBoost: 10, augLevel: 4, upgLevel: 3, augSec: 1e9, upgSec: 1e9, statBoostNow: 100);
            double atRebirth = AugmentMath.ProjectedGain(p, 3600, toRebirth: true);
            double midRun = AugmentMath.ProjectedGain(p, 3600, toRebirth: false);
            Assert.True(midRun > atRebirth);
        }

        [Fact]
        public void ProjectedGain_DeadHalvesDoNotGrow()
        {
            var live = Pair(augLive: true, upgLive: true, tier: 1, baseBoost: 1, augLevel: 10, upgLevel: 10, augSec: 1, upgSec: 1);
            var augOnly = Pair(augLive: true, upgLive: false, tier: 1, baseBoost: 1, augLevel: 10, upgLevel: 10, augSec: 1, upgSec: 1);
            Assert.True(AugmentMath.ProjectedGain(live, 3600, false) > AugmentMath.ProjectedGain(augOnly, 3600, false));
        }

        [Fact]
        public void ProjectedGain_ToRebirth_NeverExceedsTheMidRunValue()
        {
            var p = Pair(tier: 1.5, baseBoost: 5, augLevel: 7, upgLevel: 2, augSec: 90, upgSec: 140);
            double midRun = AugmentMath.ProjectedGain(p, 900, toRebirth: false);
            double atRebirth = AugmentMath.ProjectedGain(p, 900, toRebirth: true);
            Assert.True(atRebirth <= midRun);
        }

        // ---------------- GoldGateBlocks ----------------

        [Fact]
        public void GoldGate_AffordablePair_IsNotBlocked()
        {
            var p = Pair(augCost: 1, upgradeCost: 1, augSec: 10, upgSec: 10);
            Assert.False(AugmentMath.GoldGateBlocks(p, gold: 1_000_000));
        }

        [Fact]
        public void GoldGate_ColdAndUnaffordable_IsBlocked()
        {
            var p = Pair(augCost: 1e12, upgradeCost: 1e12, augSec: 10, upgSec: 10, augProgress: 0f, upgProgress: 0f);
            Assert.True(AugmentMath.GoldGateBlocks(p, gold: 1));
        }

        [Fact]
        public void GoldGate_LevelAlreadyInProgressAndNotAboutToLand_IsNotBlocked()
        {
            // progress != 0 and timeRemaining >= 10 -> worth waiting on even if we cannot pay.
            var p = Pair(augCost: 1e12, upgradeCost: 1e12, augSec: 100, upgSec: 100, augProgress: 0.5f, upgProgress: 0.5f);
            Assert.False(AugmentMath.GoldGateBlocks(p, gold: 1));
        }

        [Fact]
        public void GoldGate_LevelAboutToLand_IsBlockedEvenWithProgress()
        {
            // timeRemaining < 10 -> the `progress == 0 || timeRemaining < 10` disjunction fires.
            var p = Pair(augCost: 1e12, upgradeCost: 1e12, augSec: 10, upgSec: 10, augProgress: 0.99f, upgProgress: 0.99f);
            Assert.True(AugmentMath.GoldGateBlocks(p, gold: 1));
        }

        // ---------------- PickBest ----------------

        [Fact]
        public void PickBest_NoCandidates_FindsNothing()
        {
            Assert.False(AugmentMath.PickBest(new List<AugmentMath.AugPairState>(), 1e9, 3600, false).Found);
            Assert.False(AugmentMath.PickBest(null, 1e9, 3600, false).Found);
        }

        [Fact]
        public void PickBest_SkipsPairsWithNoLiveHalf()
        {
            var pairs = new List<AugmentMath.AugPairState> { Pair(index: 3, augLive: false, upgLive: false) };
            Assert.False(AugmentMath.PickBest(pairs, 1e9, 3600, false).Found);
        }

        [Fact]
        public void PickBest_ChoosesTheHighestProjectedGain()
        {
            var small = Pair(index: 0, baseBoost: 1, tier: 1, augLevel: 5, upgLevel: 1, augSec: 100, upgSec: 100);
            var big = Pair(index: 5, baseBoost: 1000, tier: 1, augLevel: 5, upgLevel: 1, augSec: 100, upgSec: 100);
            var pick = AugmentMath.PickBest(new List<AugmentMath.AugPairState> { small, big }, 1e9, 3600, false);
            Assert.True(pick.Found);
            Assert.Equal(5, pick.Index);
        }

        [Fact]
        public void PickBest_ReportsTheWinnersElasticitySplit()
        {
            var p = Pair(index: 2, tier: 2.0, baseBoost: 10, augLevel: 5, upgLevel: 5, augSec: 50, upgSec: 50);
            var pick = AugmentMath.PickBest(new List<AugmentMath.AugPairState> { p }, 1e9, 3600, false);
            Assert.True(pick.Found);
            Assert.True(pick.AugLive);
            Assert.True(pick.UpgLive);
            Assert.Equal(0.5f, pick.AugRatio, 6);
            Assert.Equal(0.5f, pick.UpgRatio, 6);
        }

        [Fact]
        public void PickBest_GoldBlockedPairCanNeverWin()
        {
            var rich = Pair(index: 0, baseBoost: 1e9, tier: 2, augLevel: 5, upgLevel: 5, augSec: 10, upgSec: 10,
                            augCost: 1e18, upgradeCost: 1e18);
            var modest = Pair(index: 1, baseBoost: 10, tier: 1, augLevel: 5, upgLevel: 1, augSec: 100, upgSec: 100);
            var pick = AugmentMath.PickBest(new List<AugmentMath.AugPairState> { rich, modest }, gold: 1, horizon: 3600, toRebirth: false);
            Assert.True(pick.Found);
            Assert.Equal(1, pick.Index);
        }

        [Fact]
        public void PickBest_TiesGoToTheFirstIndex()
        {
            // bestValue uses strict `>`, so an equal-valued later pair never displaces an earlier one.
            var a = Pair(index: 1, baseBoost: 10, tier: 1, augLevel: 5, upgLevel: 2, augSec: 100, upgSec: 100);
            var b = Pair(index: 6, baseBoost: 10, tier: 1, augLevel: 5, upgLevel: 2, augSec: 100, upgSec: 100);
            var pick = AugmentMath.PickBest(new List<AugmentMath.AugPairState> { a, b }, 1e9, 3600, false);
            Assert.Equal(1, pick.Index);
        }

        [Fact]
        public void PickBest_NonPositiveGainNeverWins()
        {
            // bestValue starts at 0.0, so a pair whose projection is at or below its current boost is
            // never selected — the lane allocates nothing rather than funding a zero-value pair.
            var stale = Pair(index: 0, baseBoost: 1, tier: 1, augLevel: 5, upgLevel: 1, augSec: 1e12, upgSec: 1e12,
                             statBoostNow: 1000);
            var pick = AugmentMath.PickBest(new List<AugmentMath.AugPairState> { stale }, 1e9, 3600, false);
            Assert.False(pick.Found);
        }

        // ---------------- AugmentBP predicates ----------------

        [Fact]
        public void AugmentIndexUnlocked_AboveThirteen_IsLocked()
        {
            Assert.False(AugmentMath.AugmentIndexUnlocked(14, bossID: 999, augBossRequired: 0, upgradeBossRequired: 0));
        }

        [Fact]
        public void AugmentIndexUnlocked_EvenIndexReadsTheAugBossRequirement_OddReadsTheUpgradeOne()
        {
            Assert.True(AugmentMath.AugmentIndexUnlocked(4, bossID: 10, augBossRequired: 5, upgradeBossRequired: 100));
            Assert.False(AugmentMath.AugmentIndexUnlocked(5, bossID: 10, augBossRequired: 5, upgradeBossRequired: 100));
        }

        [Fact]
        public void AugmentIndexUnlocked_UsesStrictGreaterThan()
        {
            // bossID must EXCEED the requirement, not merely meet it.
            Assert.False(AugmentMath.AugmentIndexUnlocked(0, bossID: 5, augBossRequired: 5, upgradeBossRequired: 0));
            Assert.True(AugmentMath.AugmentIndexUnlocked(0, bossID: 6, augBossRequired: 5, upgradeBossRequired: 0));
        }

        [Fact]
        public void AugmentTargetMet_ZeroTargetMeansNoTarget()
        {
            Assert.False(AugmentMath.AugmentTargetMet(0, target: 0, level: long.MaxValue));
        }

        [Fact]
        public void AugmentTargetMet_MetAtOrAboveTheTarget()
        {
            Assert.False(AugmentMath.AugmentTargetMet(0, 100, 99));
            Assert.True(AugmentMath.AugmentTargetMet(0, 100, 100));
            Assert.True(AugmentMath.AugmentTargetMet(0, 100, 101));
        }

        // NGUBP and AdvancedTrainingBP spell "never fund this" out as an explicit `target < 0 => true`
        // branch. AugmentBP has no such branch — but it reaches the same answer INCIDENTALLY, because
        // `level >= target` is trivially true for every non-negative level once the target is negative.
        // Same outcome, different mechanism; recorded here so a later tidy-up of the three TargetMet()
        // bodies into one shared shape does not read this as a behaviour change.
        [Fact]
        public void AugmentTargetMet_NegativeTargetMeansNeverFund_ButOnlyIncidentally()
        {
            Assert.True(AugmentMath.AugmentTargetMet(0, target: -1, level: 0));
            Assert.True(AugmentMath.AugmentTargetMet(0, target: -1, level: long.MaxValue));
        }

        // ---------------- AugCap ----------------

        private static AugmentMath.AugCapInputs Cap(double level = 0, int offset = 500, double power = 1e6,
            double speedDivider = 1.0, double dividerScale = 50000.0, float allocation = 1e9f, long idle = long.MaxValue) =>
            new AugmentMath.AugCapInputs
            {
                Level = level,
                Offset = offset,
                TotalEnergyPower = power,
                SpeedDivider = speedDivider,
                DividerScale = dividerScale,
                AugsSpecBonus = 0,
                MacguffinBonus = 1,
                HackAugSpeed = 1,
                ItopodAugSpeed = 1,
                CardAugSpeed = 1,
                NoAugsEvilCompletions = 0,
                NoAugsCompletedOnce = false,
                NoAugsEvilMaxed = false,
                Sadistic = false,
                SadisticDivider = 1,
                Allocation = allocation,
                IdleEnergy = idle
            };

        [Fact]
        public void AugCap_MatchesTheGameArithmeticStepForStep()
        {
            var a = Cap(level: 100, offset: 500, power: 2e6, speedDivider: 3.0, allocation: 1e9f);
            double num1 = 1 / (2e6 / (100 + 1.0 + 500)) * 50000.0 * 3.0;
            num1 = Math.Ceiling(num1);
            double num = Math.Ceiling(num1 / Math.Ceiling(num1 / 1e9f) * 1.00000202655792);

            var r = AugmentMath.AugCap(a);
            Assert.Equal((long)num, r.Num);
            Assert.Equal(num / num1, r.PPT, 12);
        }

        [Fact]
        public void AugCap_ClampsToIdleEnergy()
        {
            var a = Cap(level: 1_000_000, power: 1, speedDivider: 1e6, idle: 12345);
            Assert.Equal(12345, AugmentMath.AugCap(a).Num);
        }

        [Fact]
        public void AugCap_FloorsTheDivisorAtOne()
        {
            // A huge power drives num1 below 1; the game floors it so the ceiling division stays finite.
            var a = Cap(level: 0, power: 1e30, speedDivider: 1.0, allocation: 100f);
            var r = AugmentMath.AugCap(a);
            Assert.Equal(2, r.Num);          // ceil(1/ceil(1/100) * 1.00000202655792) == 2
            Assert.Equal(2.0, r.PPT, 9);
        }

        [Fact]
        public void AugCap_EveryBonusDividesTheCostDown()
        {
            var plain = AugmentMath.AugCap(Cap(level: 500, power: 1e3, speedDivider: 10));
            var boosted = Cap(level: 500, power: 1e3, speedDivider: 10);
            boosted.HackAugSpeed = 4;
            Assert.True(AugmentMath.AugCap(boosted).Num < plain.Num);
        }

        [Fact]
        public void AugCap_NoAugsChallengeCompletionsApplyTheirExactGameLiterals()
        {
            var b = Cap(level: 500, power: 1e3, speedDivider: 10);
            long plain = AugmentMath.AugCap(b).Num;

            var once = b; once.NoAugsCompletedOnce = true;
            var maxed = b; maxed.NoAugsEvilMaxed = true;

            Assert.True(AugmentMath.AugCap(once).Num < plain);      // /1.1000000238418579
            Assert.True(AugmentMath.AugCap(maxed).Num < plain);     // /1.25
        }

        [Fact]
        public void AugCap_Offset_IsTheOneWindowStairTarget()
        {
            var r = new AugmentMath.AugCapResult { Num = 0, PPT = 0.5 };
            Assert.Equal(250, r.Offset);     // floor(PPT * 50 ticks/s * 10s)
        }

        // WHY THE AUGMENT LANE IS THE ONE THAT WOULD SPEND THE IDLE POOL (operator report 2026-08-07,
        // "over 80% of energy idle"). The other seated energy lanes on that account are all past their
        // stair-snap: an NGU offered 1.47 T absorbs 4.7 B and DISCARDS the rest, a training slot at the
        // rebirth-floored cap of 1 absorbs 1. This lane is the opposite shape — when the level cost
        // num1 is far above the budget, the stair chunk is `num1 / ceil(num1 / allocation)`, which sits
        // just under the WHOLE allocation and RISES WITH IT.
        //
        // That signature is what identifies BestAug-0 in the live [AllocDbg] blocks: five samples,
        // 4749s-5379s, every one taking 99.966-99.977% of an offer that itself moved by 60 M. A lane
        // bounded by its own capacity would take a number unrelated to the offer; only a deeply
        // share-bound lane tracks it that closely.
        [Fact]
        public void AugCap_WhenTheLevelCostFarExceedsTheBudget_TheTakeIsTheWholeBudgetAndScalesWithIt()
        {
            // num1 = 1/(power/(level+1+offset)) * dividerScale * speedDivider, deliberately enormous
            // against the budgets below. Allocation is a float in the game's own signature, so the
            // budgets are compared against their FLOAT values (see the QUIRK test below).
            float share = 2.4e11f;          // BestAug-0's live share of a 1.71 T pool
            float wholePool = 1.7e12f;      // what the pool could offer if the surplus were re-offered
            var small = Cap(level: 1e11, offset: 0, power: 1.0, speedDivider: 1.0, allocation: share);
            var large = Cap(level: 1e11, offset: 0, power: 1.0, speedDivider: 1.0, allocation: wholePool);

            var rSmall = AugmentMath.AugCap(small);
            var rLarge = AugmentMath.AugCap(large);

            // Just under the offer, never over it — the share is the binding constraint, not capacity.
            Assert.InRange(rSmall.Num / (double)share, 0.999, 1.0);
            Assert.InRange(rLarge.Num / (double)wholePool, 0.999, 1.0);

            // And seven times the budget buys seven times the allocation: the surplus every other
            // lane declines has a destination that converts ALL of it.
            Assert.True(rLarge.Num > rSmall.Num * 6.9);
        }

        // [QUIRK] Allocation is a FLOAT in the game's own signature and this extraction keeps it one.
        // Past ~2^24 a long allocation loses precision on the way in, so two different budgets can
        // resolve to the identical cap. Characterised, NOT fixed.
        [Fact]
        public void QUIRK_AugCap_AllocationIsFloatSoLargeBudgetsLosePrecision()
        {
            long a = 100_000_001L, b = 100_000_002L;
            Assert.Equal((float)a, (float)b);
            var ca = Cap(level: 10, power: 1, speedDivider: 1, allocation: a);
            var cb = Cap(level: 10, power: 1, speedDivider: 1, allocation: b);
            Assert.Equal(AugmentMath.AugCap(ca).Num, AugmentMath.AugCap(cb).Num);
        }

		// AugmentMathTests
		[Theory]
		[InlineData(-1, false)]   // -1 % 2 == -1, took the upgrade branch
		[InlineData(-2, false)]
		[InlineData(0, true)]
		[InlineData(13, true)]
		[InlineData(14, false)]
		public void AugmentIndexUnlocked_RejectsNegative(int index, bool expected) => Assert.Equal(expected, AugmentMath.AugmentIndexUnlocked(index, 999, 0, 0));

        // -----------------------------------------------------------------------------------------
        // D1 REVERSED (amendment 30) — the advisor HONOURS the No Augs challenge and refuses to fund
        // augments for its duration. These are NOT characterisation tests: they pin a DELIBERATE LIVE
        // BEHAVIOUR DECISION. They replace the D1 assertions, which pinned the opposite.
        //
        // The mechanical finding those assertions rested on (21 §C2 — the lock is a non-interactable
        // menu button and nothing else) is NOT overturned and is not re-tested here, because it is
        // not a property of this code. What is pinned here is the decision: the game does not enforce
        // the rule, and the advisor obeys it anyway.
        // -----------------------------------------------------------------------------------------

        // The menu gate is [DECOMP] ButtonShower.cs:199 IN FULL —
        // `bossID < 17 || noAugsChallenge.inChallenge` -> interactable = false — so the predicate is
        // that condition negated. Both terms, no exceptions.
        [Theory]
        [InlineData(0, false, false)]
        [InlineData(16, false, false)]   // the game's own boundary: `bossID < 17` is locked
        [InlineData(17, false, true)]    // ...so 17 is the first unlocked boss
        [InlineData(58, false, true)]    // the No Augs target boss, outside the challenge
        [InlineData(999, false, true)]
        [InlineData(0, true, false)]     // and the challenge term alone locks it at every boss
        [InlineData(16, true, false)]
        [InlineData(17, true, false)]
        [InlineData(58, true, false)]
        [InlineData(999, true, false)]
        public void AugmentMenuUnlocked_IsButtonShower199Negated(long bossId, bool inChallenge, bool expected) =>
            Assert.Equal(expected, AugmentMath.AugmentMenuUnlocked(bossId, inChallenge));

        // THE DECISION, stated as a test: no bossID makes an augment lane seat inside the challenge.
        // This is the assertion that fails if the challenge term is dropped from the shared predicate
        // again — including by "simplifying" it back to the boss half.
        [Fact]
        public void AugmentsRefuseDuringNoAugsChallenge_AtEveryBoss()
        {
            // 58 = the No Augs target boss (21 §C1), and 999 stands in for "arbitrarily far past it".
            // If the term were dropped, every one of these would flip to true.
            foreach (var bossId in new long[] { 0, 16, 17, 36, 37, 58, 999 })
                Assert.False(AugmentMath.AugmentMenuUnlocked(bossId, noAugsInChallenge: true));
        }

        // BOTH LANES. AugmentBP and BestAug route through the one predicate — that consolidation is
        // D1's implementation and it survives the reversal — so a single refusal shuts both. The
        // per-lane arithmetic beneath the gate is deliberately UNCHANGED by the challenge: it is the
        // gate that refuses, not seven scattered copies of it.
        [Fact]
        public void BothLanesRefuse_ThroughTheOneSharedPredicate()
        {
            Assert.False(AugmentMath.AugmentMenuUnlocked(58, noAugsInChallenge: true));

            // Underneath it, the index gate and BestAug's whole-lane DONE question are untouched by
            // the challenge — they never saw it and still do not. The refusal has exactly one site.
            Assert.True(AugmentMath.AugmentIndexUnlocked(0, 58, 16, 36));
            Assert.True(AugmentMath.AugmentIndexUnlocked(1, 58, 16, 36));
            var pairs = new List<AugmentMath.AugPairTargetState>
            {
                new AugmentMath.AugPairTargetState { AugLocked = false, AugHitTarget = false }
            };
            Assert.False(AugmentMath.BestAugTargetMet(pairs, useUpgrades: false));
        }

        // OUTSIDE the challenge, nothing moved. `bossID >= 17` is exactly `!(bossID < 17)`, which is
        // exactly what `buttons.augmentation.interactable` reported before either change — so this is
        // byte-for-byte the pre-D1 behaviour, and the boss boundary still sits between 16 and 17.
        [Fact]
        public void OutsideTheChallenge_BehaviourIsUnchanged_AndTheBossGateStillHolds()
        {
            for (long bossId = 0; bossId <= 40; bossId++)
                Assert.Equal(bossId >= 17, AugmentMath.AugmentMenuUnlocked(bossId, noAugsInChallenge: false));

            // The boundary itself, spelled out: 16 locked, 17 open.
            Assert.False(AugmentMath.AugmentMenuUnlocked(16, noAugsInChallenge: false));
            Assert.True(AugmentMath.AugmentMenuUnlocked(17, noAugsInChallenge: false));
        }

        // The refusal is SURFACED — a lane going quiet is indistinguishable from a lane that broke
        // (25 §4). One line per challenge entry, nothing said outside a challenge, nothing repeated
        // while the latch is held.
        [Theory]
        [InlineData(false, false)]   // not in the challenge, never surfaced -> silence
        [InlineData(false, true)]    // not in the challenge, latch still set -> silence
        [InlineData(true, true)]     // in the challenge, already said -> silence
        public void NoAugsSurfacingLine_SaysNothing(bool inChallenge, bool alreadySurfaced) =>
            Assert.Null(AugmentMath.NoAugsSurfacingLine(inChallenge, alreadySurfaced));

        [Fact]
        public void NoAugsSurfacingLine_FiresOnceOnEntryAndReportsTheREFUSAL()
        {
            var line = AugmentMath.NoAugsSurfacingLine(inChallenge: true, alreadySurfaced: false);
            Assert.NotNull(line);
            // What is happening, and on whose authority. The wording is the reversal's, not D1's:
            // the lane is NOT FUNDED, and it must not read as "funding anyway" ever again.
            Assert.Contains("No Augs", line, StringComparison.Ordinal);
            Assert.Contains("not funded", line, StringComparison.Ordinal);
            Assert.Contains("ButtonShower.cs:199", line, StringComparison.Ordinal);
            Assert.Contains("amendment 30", line, StringComparison.Ordinal);
            Assert.DoesNotContain("funding augments anyway", line, StringComparison.Ordinal);
        }
    }
}
