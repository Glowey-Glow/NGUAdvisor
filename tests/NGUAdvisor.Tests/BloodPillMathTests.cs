using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // CHARACTERISATION tests for BloodPillMath (extraction step 4).
    //
    // Report 04 §10 recorded BloodPlanner and BloodMagicManager at ZERO tests; amendment 01 §P3 repeats
    // it. These are their first. The Iron Pill ladder is the codebase's only existing temporal-banking
    // decision on the blood layer, which is what makes it the thing amendment 01's open item 10 is
    // about — so it is pinned in detail.
    public class BloodPillMathTests
    {
        private static BloodPillMath.PillInputs In(
            double blood = 1e6, double bps = 100, double runLeft = 3600, double trueRunLeft = 3600,
            double cdLeft = 0, double minBlood = 100, double advAttack = 10, double bonus = 1.0,
            bool autos = false, double capGrowth = 0, double cooldown = 1800, double availableFor = 99999) =>
            new BloodPillMath.PillInputs
            {
                Blood = blood,
                Bps = bps,
                RunLeftSec = runLeft,
                TrueRunLeftSec = trueRunLeft,
                CdLeftSec = cdLeft,
                MinBlood = minBlood,
                AdvBaseAttack = advAttack,
                IronPillBonus = bonus,
                AutosDraining = autos,
                CapGrowthPerSec = capGrowth,
                CooldownSec = cooldown,
                AvailableForSec = availableFor,
                PillWorthFraction = 0.10,
                PillMinAvailableSec = 1800.0,
                PillPoolingHorizonSec = 3600.0
            };

        // ---------------- RawPower / PoolOver / breakpoints ----------------

        [Fact]
        public void RawPower_IsTheFourthRootFloored()
        {
            Assert.Equal(3, BloodPillMath.RawPower(81, 1));
            Assert.Equal(3, BloodPillMath.RawPower(255, 1));
            Assert.Equal(4, BloodPillMath.RawPower(256, 1));
        }

        [Fact]
        public void RawPower_BelowTheCastMinimumIsZero()
        {
            Assert.Equal(0, BloodPillMath.RawPower(99, 100));
            Assert.Equal(3, BloodPillMath.RawPower(100, 100));
        }

        [Fact]
        public void NextBreakpointBlood_IsThePerfectFourthPower()
        {
            Assert.Equal(256.0, BloodPillMath.NextBreakpointBlood(3));
            Assert.Equal(625.0, BloodPillMath.NextBreakpointBlood(4));
        }

        [Fact]
        public void PoolOver_WithNoGrowth_IsJustRateTimesTime()
        {
            Assert.Equal(1000.0, BloodPillMath.PoolOver(bps: 10, capGrowthPerSec: 0, t0: 0, T: 100));
        }

        [Fact]
        public void PoolOver_WithGrowth_UsesTheWindowMidpoint()
        {
            // bps * T * (1 + g * (t0 + T/2))
            Assert.Equal(10 * 100 * (1.0 + 0.001 * (50 + 50)), BloodPillMath.PoolOver(10, 0.001, 50, 100), 9);
        }

        [Fact]
        public void PoolOver_NonPositiveWindowIsZero()
        {
            Assert.Equal(0, BloodPillMath.PoolOver(10, 0.001, 0, 0));
            Assert.Equal(0, BloodPillMath.PoolOver(10, 0.001, 0, -5));
        }

        [Fact]
        public void GrowthEma_SeedsOnTheFirstSampleThenBlendsSeventyThirty()
        {
            Assert.Equal(0.5, BloodPillMath.GrowthEma(previous: 0, sampleRate: 0.5));
            Assert.Equal(0.4 * 0.7 + 0.5 * 0.3, BloodPillMath.GrowthEma(0.4, 0.5), 12);
        }

        [Fact]
        public void RelativeGrowthPerSec_IsFlooredAtZero()
        {
            Assert.Equal(0, BloodPillMath.RelativeGrowthPerSec(capNow: 50, capThen: 100, dtSec: 60));
            Assert.Equal((200.0 / 100.0 - 1.0) / 60.0, BloodPillMath.RelativeGrowthPerSec(200, 100, 60), 12);
        }

        // ---------------- the pill ladder, in order ----------------

        [Fact]
        public void Decide_PillTooWeakToMatter_IsNotWorthwhile()
        {
            // best achievable ~ 31.6 vs 10% of a base adventure power of 1e9.
            var d = BloodPillMath.Decide(In(blood: 1e6, bps: 0, advAttack: 1e9));
            Assert.Equal(BloodPillMath.PillVerdict.NotWorthwhile, d.Verdict);
            Assert.False(d.CastNow);
        }

        [Fact]
        public void Decide_PowerNowIsPopulatedEvenOnTheNotWorthwhilePath()
        {
            // The display value is computed before the worth gate, so the panel still shows it.
            var d = BloodPillMath.Decide(In(blood: 256, bps: 0, advAttack: 1e9));
            Assert.Equal(BloodPillMath.PillVerdict.NotWorthwhile, d.Verdict);
            Assert.Equal(4, d.PowerNow);
        }

        [Fact]
        public void Decide_CooldownOutlastsTheRun_IsUnreachableThisRun()
        {
            var d = BloodPillMath.Decide(In(cdLeft: 7200, trueRunLeft: 3600));
            Assert.Equal(BloodPillMath.PillVerdict.UnreachableThisRun, d.Verdict);
        }

        [Fact]
        public void Decide_NoScheduledRebirth_IsNeverUnreachable()
        {
            // TrueRunLeftSec is MaxValue when nothing is scheduled: the pill will eventually be ready.
            var d = BloodPillMath.Decide(In(cdLeft: 7200, trueRunLeft: double.MaxValue));
            Assert.Equal(BloodPillMath.PillVerdict.Charging, d.Verdict);
        }

        [Fact]
        public void Decide_OnCooldownAndReachable_IsCharging()
        {
            var d = BloodPillMath.Decide(In(cdLeft: 600, trueRunLeft: 7200));
            Assert.Equal(BloodPillMath.PillVerdict.Charging, d.Verdict);
            Assert.False(d.PoolingTooFarOut);
        }

        [Fact]
        public void Decide_OnCooldownBeyondThePoolingHorizon_FlagsTooFarOut()
        {
            // The hard rule: never treat the pill as a live blood consumer more than an hour out.
            var d = BloodPillMath.Decide(In(cdLeft: 5400, trueRunLeft: 1e9));
            Assert.Equal(BloodPillMath.PillVerdict.Charging, d.Verdict);
            Assert.True(d.PoolingTooFarOut);
        }

        [Fact]
        public void Decide_ReadyButBelowTheCastMinimum_IsNoBloodYet()
        {
            var d = BloodPillMath.Decide(In(blood: 50, minBlood: 100, bps: 1e6, advAttack: 1));
            Assert.Equal(BloodPillMath.PillVerdict.NoBloodYet, d.Verdict);
        }

        [Fact]
        public void Decide_WithinTheFirstThirtyMinutesOfBeingReady_HoldsForAStrongerCast()
        {
            var d = BloodPillMath.Decide(In(blood: 1e8, bps: 1, advAttack: 1, availableFor: 60));
            Assert.Equal(BloodPillMath.PillVerdict.HoldingFailSafe, d.Verdict);
            Assert.False(d.CastNow);
        }

        [Fact]
        public void Decide_GainUnderTenPercentOfBaseAdventurePower_Holds()
        {
            // Worth-gate passes on the PROJECTED best (1e12/s of income makes a huge pill reachable),
            // but the cast available RIGHT NOW is 10, under 10% of a base adventure power of 200.
            var d = BloodPillMath.Decide(In(blood: 1e4, bps: 1e12, runLeft: 3600, advAttack: 200, availableFor: 99999));
            Assert.Equal(10, d.PowerNow);
            Assert.Equal(BloodPillMath.PillVerdict.HoldingFailSafe, d.Verdict);
        }

        [Fact]
        public void Decide_SecondPillBeatsHolding_CastsNow()
        {
            // Huge income and a run much longer than the cooldown: cast now, brew another.
            var d = BloodPillMath.Decide(In(blood: 1e8, bps: 1e6, runLeft: 86400, trueRunLeft: 86400,
                                            advAttack: 1, cooldown: 1800, availableFor: 99999));
            Assert.Equal(BloodPillMath.PillVerdict.CastNowSecondPill, d.Verdict);
            Assert.True(d.CastNow);
            Assert.True(d.PillSecond > 0);
            Assert.True(d.ENow + d.PillSecond > d.EEnd);
        }

        [Fact]
        public void Decide_NextBreakpointOutOfReach_CastsNow()
        {
            // Trickle income and a run too short to reach the next perfect fourth power.
            var d = BloodPillMath.Decide(In(blood: 1e8, bps: 1e-6, runLeft: 120, trueRunLeft: 120,
                                            advAttack: 1, cooldown: 1800, availableFor: 99999));
            Assert.Equal(BloodPillMath.PillVerdict.CastNowLastBreakpoint, d.Verdict);
            Assert.True(d.CastNow);
        }

        [Fact]
        public void Decide_EveryCastVerdictSetsCastNow()
        {
            foreach (var d in new[]
            {
                BloodPillMath.Decide(In(blood: 1e8, bps: 1e6, runLeft: 86400, trueRunLeft: 86400, advAttack: 1)),
                BloodPillMath.Decide(In(blood: 1e8, bps: 1e-6, runLeft: 120, trueRunLeft: 120, advAttack: 1)),
            })
                Assert.True(d.CastNow);
        }

        [Fact]
        public void Decide_NoCastVerdictSetsCastNow()
        {
            foreach (var d in new[]
            {
                BloodPillMath.Decide(In(blood: 1e6, bps: 0, advAttack: 1e9)),          // NotWorthwhile
                BloodPillMath.Decide(In(cdLeft: 7200, trueRunLeft: 3600)),             // UnreachableThisRun
                BloodPillMath.Decide(In(cdLeft: 600, trueRunLeft: 7200)),              // Charging
                BloodPillMath.Decide(In(blood: 50, minBlood: 100, bps: 1e6, advAttack: 1)),   // NoBloodYet
                BloodPillMath.Decide(In(blood: 1e8, bps: 1, advAttack: 1, availableFor: 60)), // HoldingFailSafe
            })
                Assert.False(d.CastNow);
        }

        [Fact]
        public void Decide_AutosDraining_ShrinksTheProjectedPoolingWindow()
        {
            var quiet = BloodPillMath.Decide(In(blood: 1e6, bps: 1000, cdLeft: 1800, trueRunLeft: 1e9, autos: false));
            var draining = BloodPillMath.Decide(In(blood: 1e6, bps: 1000, cdLeft: 1800, trueRunLeft: 1e9, autos: true));
            // With the autos on, blood only accumulates from 15m before the pill is ready.
            Assert.True(draining.BloodAtEnd < quiet.BloodAtEnd);
        }

        // [QUIRK] RunLeftSec comes from WandoosAdvisor.RunHorizonMinutes(), which CLAMPS to >= 10
        // minutes. TrueRunLeftSec exists precisely because that is too optimistic for pill timing — but
        // only the UnreachableThisRun test consults it. Every projection still runs on the clamped
        // value, so a run with 30 seconds left is projected as if it had ten minutes.
        [Fact]
        public void QUIRK_Decide_ProjectionsUseTheClampedRunHorizonNotTheTrueTimeLeft()
        {
            var d = BloodPillMath.Decide(In(blood: 1e6, bps: 1e5, runLeft: 600, trueRunLeft: 30, advAttack: 1));
            // trueRunLeft = 30s, but BloodAtEnd was projected over the clamped 600s window.
            Assert.Equal(1e6 + BloodPillMath.PoolOver(1e5, 0, 0, 600), d.BloodAtEnd, 6);
        }

        // [QUIRK] The fail-safe branch compares PowerNow — capped at 1e8 and multiplied by the Evil
        // ironPillBonus — against the worth bar, while every other rung of the ladder uses the raw
        // ENow. On Evil+ the two are different numbers.
        [Fact]
        public void QUIRK_Decide_FailSafeBranchUsesTheBonusedPowerWhileTheRestUsesTheRawOne()
        {
            var d = BloodPillMath.Decide(In(blood: 1e8, bps: 1e5, runLeft: 86400, trueRunLeft: 86400,
                                            advAttack: 1, bonus: 5.0, availableFor: 99999));
            Assert.Equal(BloodPillMath.RawPower(1e8, 100), d.ENow);
            Assert.Equal(BloodPillMath.RawPower(1e8, 100) * 5, d.PowerNow);
            Assert.NotEqual(d.ENow, d.PowerNow);
        }

        [Fact]
        public void Decide_PowerNowIsCappedAtOneHundredMillion()
        {
            var d = BloodPillMath.Decide(In(blood: 1e40, bps: 0, advAttack: 1, availableFor: 99999));
            Assert.Equal(BloodPillMath.PillPowerCap, d.PowerNow);
        }

        // ---------------- Counterfeit Gold / Spaghetti ----------------

        [Fact]
        public void GoldPercentNow_IsTheExactGameFormula()
        {
            // floor((log2(invested/min)+1)^2)
            Assert.Equal(Math.Floor(Math.Pow(Math.Log(1024.0 / 1.0, 2.0) + 1.0, 2.0)), BloodPillMath.GoldPercentNow(1024, 1));
        }

        [Fact]
        public void GoldPercentNow_BelowTheMinimumIsZero()
        {
            Assert.Equal(0, BloodPillMath.GoldPercentNow(50, 100));
            Assert.Equal(0, BloodPillMath.GoldPercentNow(100, 0));
        }

        [Fact]
        public void GoldNextInvestment_IsMinTimesTwoToTheSqrtMinusOne()
        {
            Assert.Equal(100 * Math.Pow(2.0, Math.Sqrt(5.0) - 1.0), BloodPillMath.GoldNextInvestment(100, 4), 9);
        }

        [Fact]
        public void GoldBelowKnee_EligibleWhenTheNextPercentIsWithinTwentyMinutes()
        {
            Assert.True(BloodPillMath.GoldBelowKnee(invested: 1000, minGold: 100, bps: 1e6, out _, out var fast));
            Assert.True(fast <= BloodPillMath.GoldKneeEtaSec);
        }

        [Fact]
        public void GoldBelowKnee_NotEligibleWhenTheNextPercentIsTooSlow()
        {
            Assert.False(BloodPillMath.GoldBelowKnee(invested: 1000, minGold: 100, bps: 1e-9, out _, out _));
        }

        [Fact]
        public void GoldBelowKnee_ZeroIncomeIsNeverEligible()
        {
            Assert.False(BloodPillMath.GoldBelowKnee(1000, 100, bps: 0, out _, out var eta));
            Assert.Equal(double.MaxValue, eta);
        }

        [Fact]
        public void GoldBelowKnee_ZeroMinimumIsNeverEligible()
        {
            Assert.False(BloodPillMath.GoldBelowKnee(1000, 0, 1e9, out _, out _));
        }

        [Fact]
        public void LootPercentNow_IsOnePercentPerDoubling()
        {
            Assert.Equal(0, BloodPillMath.LootPercentNow(100, 100));
            Assert.Equal(1, BloodPillMath.LootPercentNow(200, 100));
            Assert.Equal(3, BloodPillMath.LootPercentNow(800, 100));
        }

        [Fact]
        public void DcBelowRecommendation_IsAStrictComparison()
        {
            Assert.True(BloodPillMath.DcBelowRecommendation(49, 50));
            Assert.False(BloodPillMath.DcBelowRecommendation(50, 50));
        }

        [Fact]
        public void InvestmentWindow_OpenForTheFirstHalfOfTheRunAndClosedAfter()
        {
            Assert.True(BloodPillMath.InvestmentWindowOpen(rebirthTargetSec: 86400, nowSec: 0));
            Assert.False(BloodPillMath.InvestmentWindowOpen(86400, nowSec: 43200));
            Assert.False(BloodPillMath.InvestmentWindowOpen(86400, nowSec: 80000));
        }

        [Fact]
        public void InvestmentWindow_WithNoScheduledRebirthIsAlwaysOpen()
        {
            Assert.True(BloodPillMath.InvestmentWindowOpen(-1, 1e9));
        }

        // ---------------- the spell cast gate ----------------

        private static BloodPillMath.CastInputs Cast(bool enabled = true, bool unlocked = true, bool forced = false,
            double threshold = 5, double time = 9999, double cooldown = 1800, double blood = 1e6,
            double minBlood = 100, double effect = 10, bool failSafe = false,
            Action onBlood = null, Action onEffect = null, Action onFailSafe = null) =>
            new BloodPillMath.CastInputs
            {
                SpellsEnabled = enabled,
                Unlocked = unlocked,
                Forced = forced,
                Threshold = threshold,
                TimeSec = time,
                CooldownSec = cooldown,
                BloodPoints = () => { onBlood?.Invoke(); return blood; },
                MinBlood = minBlood,
                Effect = () => { onEffect?.Invoke(); return effect; },
                FailSafeHolds = _ => { onFailSafe?.Invoke(); return failSafe; }
            };

        [Fact]
        public void DecideCast_AllGatesPassed_Casts()
        {
            Assert.Equal(BloodPillMath.CastVerdict.Cast, BloodPillMath.DecideCast(Cast(), out var effect));
            Assert.Equal(10, effect);
        }

        // The lazy predicates are not a convenience — they are what keeps the extraction observationally
        // identical. The original ladder never called Effect() unless the blood gate passed, and Effect()
        // calls into the game (minMacguffin*Blood, totalBloodGuffbonus). Evaluating it eagerly would issue
        // game reads on a spell that was never going to cast.
        [Fact]
        public void DecideCast_DoesNotEvaluateEffectOrFailSafeUntilTheirGateIsReached()
        {
            int blood = 0, effectCalls = 0, failSafe = 0;

            BloodPillMath.DecideCast(Cast(unlocked: false,
                onBlood: () => blood++, onEffect: () => effectCalls++, onFailSafe: () => failSafe++), out _);
            Assert.Equal(0, blood);
            Assert.Equal(0, effectCalls);
            Assert.Equal(0, failSafe);

            // Blood gate fails -> blood read once, effect and fail-safe never.
            BloodPillMath.DecideCast(Cast(blood: 1,
                onBlood: () => blood++, onEffect: () => effectCalls++, onFailSafe: () => failSafe++), out _);
            Assert.Equal(1, blood);
            Assert.Equal(0, effectCalls);
            Assert.Equal(0, failSafe);

            // Threshold gate fails -> effect read once, fail-safe never.
            BloodPillMath.DecideCast(Cast(threshold: 1e9,
                onBlood: () => blood++, onEffect: () => effectCalls++, onFailSafe: () => failSafe++), out _);
            Assert.Equal(1, effectCalls);
            Assert.Equal(0, failSafe);
        }

        [Fact]
        public void DecideCast_ReportsZeroEffectWhenItNeverGotFarEnoughToComputeOne()
        {
            BloodPillMath.DecideCast(Cast(unlocked: false, effect: 999), out var effect);
            Assert.Equal(0, effect);
        }

        [Theory]
        [InlineData(false, true, 5.0, 9999.0, 1e6, 10.0, false, BloodPillMath.CastVerdict.SpellsDisabled)]
        [InlineData(true, false, 5.0, 9999.0, 1e6, 10.0, false, BloodPillMath.CastVerdict.NotUnlocked)]
        [InlineData(true, true, 0.0, 9999.0, 1e6, 10.0, false, BloodPillMath.CastVerdict.NoUserThreshold)]
        [InlineData(true, true, 5.0, 10.0, 1e6, 10.0, false, BloodPillMath.CastVerdict.OnCooldown)]
        [InlineData(true, true, 5.0, 9999.0, 1.0, 10.0, false, BloodPillMath.CastVerdict.BelowMinimumBlood)]
        [InlineData(true, true, 50.0, 9999.0, 1e6, 10.0, false, BloodPillMath.CastVerdict.BelowPowerThreshold)]
        [InlineData(true, true, 5.0, 9999.0, 1e6, 10.0, true, BloodPillMath.CastVerdict.HeldByFailSafe)]
        public void DecideCast_RejectsInTheGamesOwnOrder(bool enabled, bool unlocked, double threshold,
            double time, double blood, double effect, bool failSafe, BloodPillMath.CastVerdict expected)
        {
            Assert.Equal(expected, BloodPillMath.DecideCast(
                Cast(enabled: enabled, unlocked: unlocked, threshold: threshold, time: time, blood: blood,
                     effect: effect, failSafe: failSafe), out _));
        }

        // [QUIRK] A threshold below 1.0 stops an UNFORCED cast entirely — so a spell left at the default
        // threshold of 0 never fires outside a rebirth force-cast, no matter how much blood is pooled.
        // That is exactly why the planner-driven CastPlanned path has to skip the threshold.
        [Fact]
        public void QUIRK_DecideCast_AZeroThresholdMeansNeverCastRatherThanAlwaysCast()
        {
            Assert.Equal(BloodPillMath.CastVerdict.NoUserThreshold, BloodPillMath.DecideCast(Cast(threshold: 0), out _));
            Assert.Equal(BloodPillMath.CastVerdict.Cast, BloodPillMath.DecidePlannedCast(Cast(threshold: 0), out _));
        }

        [Fact]
        public void DecideCast_ForcedRebirthCastBypassesTheThresholdAndTheFailSafe()
        {
            Assert.Equal(BloodPillMath.CastVerdict.Cast, BloodPillMath.DecideCast(Cast(forced: true, threshold: 0), out _));
            Assert.Equal(BloodPillMath.CastVerdict.Cast, BloodPillMath.DecideCast(Cast(forced: true, threshold: 1e9, failSafe: true), out _));
        }

        [Fact]
        public void DecideCast_ForcedCastStillRespectsCooldownAndMinimumBlood()
        {
            Assert.Equal(BloodPillMath.CastVerdict.OnCooldown, BloodPillMath.DecideCast(Cast(forced: true, time: 10), out _));
            Assert.Equal(BloodPillMath.CastVerdict.BelowMinimumBlood, BloodPillMath.DecideCast(Cast(forced: true, blood: 1), out _));
        }

        [Fact]
        public void DecidePlannedCast_AppliesTheFailSafeEvenThoughNothingIsForced()
        {
            Assert.Equal(BloodPillMath.CastVerdict.HeldByFailSafe, BloodPillMath.DecidePlannedCast(Cast(failSafe: true), out _));
        }

        // ---------------- the pill fail-safe, shared by both files ----------------

        [Fact]
        public void PillFailSafe_HoldsForTheFirstThirtyMinutesOfAvailability()
        {
            Assert.Equal(BloodPillMath.PillHold.TooSoonAfterReady,
                BloodPillMath.PillFailSafe(availableForSec: 1799, effect: 1e9, baseAdvPower: 1, minAvailableSec: 1800, worthFraction: 0.1));
            Assert.Equal(BloodPillMath.PillHold.None,
                BloodPillMath.PillFailSafe(1800, 1e9, 1, 1800, 0.1));
        }

        [Fact]
        public void PillFailSafe_RefusesAGainUnderTenPercentOfBaseAdventurePower()
        {
            Assert.Equal(BloodPillMath.PillHold.GainTooSmall,
                BloodPillMath.PillFailSafe(9999, effect: 9, baseAdvPower: 100, minAvailableSec: 1800, worthFraction: 0.1));
            Assert.Equal(BloodPillMath.PillHold.None,
                BloodPillMath.PillFailSafe(9999, 10, 100, 1800, 0.1));
        }

        [Fact]
        public void PillFailSafe_FloorsBaseAdventurePowerAtOne()
        {
            Assert.Equal(BloodPillMath.PillHold.GainTooSmall,
                BloodPillMath.PillFailSafe(9999, effect: 0.05, baseAdvPower: 0, minAvailableSec: 1800, worthFraction: 0.1));
        }

        // ---------------- the three effect formulas ----------------

        [Fact]
        public void IronPillEffect_IsTheFlooredFourthRootTimesTheEvilBonus()
        {
            Assert.Equal(4.0, BloodPillMath.IronPillEffect(256, 1.0));
            Assert.Equal(8.0, BloodPillMath.IronPillEffect(256, 2.0));
        }

        [Fact]
        public void GuffAEffect_IsBaseTenLogTimesTheWishBonusThenFloored()
        {
            Assert.Equal(Math.Floor((Math.Log(1000.0 / 1.0, 10.0) + 1.0) * 2.0), BloodPillMath.GuffAEffect(1000, 1, 2.0));
        }

        [Fact]
        public void GuffBEffect_IsBaseTwentyLogFloored()
        {
            Assert.Equal(Math.Floor(Math.Log(400.0 / 1.0, 20.0) + 1.0), BloodPillMath.GuffBEffect(400, 1));
        }

        // The two MacGuffin spells differ ONLY in log base and in whether a bonus multiplies — pinned so
        // a later merge of the two classes stays honest about that.
        [Fact]
        public void GuffAAndGuffB_DifferOnlyByLogBaseAndTheBonus()
        {
            Assert.Equal(Math.Floor(Math.Log(1e6, 10.0) + 1.0), BloodPillMath.GuffAEffect(1e6, 1, 1.0));
            Assert.Equal(Math.Floor(Math.Log(1e6, 20.0) + 1.0), BloodPillMath.GuffBEffect(1e6, 1));
        }
    }
}
