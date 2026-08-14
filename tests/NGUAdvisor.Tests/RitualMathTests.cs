using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // CHARACTERISATION tests for RitualMath (extraction step 4).
    //
    // BR is the magic layer's entire blood-facing surface (amendment 01: at the magic layer, the four
    // blood sinks are ONE consumer). M1 will be written against this decision, so it needed to come out
    // from behind the Character weld first. These tests pin what it does today — a duration and
    // feasibility filter, not a rate model.
    public class RitualMathTests
    {
        private static RitualMath.RitualState R(int id = 0, bool unlocked = true, double goldCost = 0,
            double progress = 0) =>
            new RitualMath.RitualState { Id = id, Unlocked = unlocked, GoldCost = goldCost, Progress = progress };

        // timeLeft is passed lazily to the real function; `probe` counts how often it is evaluated.
        private static RitualMath.RitualAction Decide(RitualMath.RitualState r, double gold, int secondsToRun,
            double nowSec, double rebirthDeadlineSec, double timeLeft, Action probe = null) =>
            RitualMath.RitualDecide(r, gold, secondsToRun, nowSec, rebirthDeadlineSec,
                                    () => { probe?.Invoke(); return timeLeft; });

        // ---------------- RitualDecide ----------------

        [Fact]
        public void RitualDecide_LockedRitualIsNotConsidered()
        {
            Assert.Equal(RitualMath.RitualAction.NotConsidered,
                Decide(R(unlocked: false), gold: 1e9, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 10));
        }

        [Fact]
        public void RitualDecide_AffordableAndFastEnough_IsFunded()
        {
            Assert.Equal(RitualMath.RitualAction.Fund,
                Decide(R(goldCost: 100), gold: 1e9, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 60));
        }

        [Fact]
        public void RitualDecide_UnaffordableAndUnstarted_IsSkippedAndDrained()
        {
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain,
                Decide(R(goldCost: 1e12, progress: 0), gold: 1, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 10));
        }

        [Fact]
        public void RitualDecide_UnaffordableButAlreadyStarted_StillGetsFunded()
        {
            // progress > 0 means the gold was already paid; the ritual is worth finishing.
            Assert.Equal(RitualMath.RitualAction.Fund,
                Decide(R(goldCost: 1e12, progress: 0.5), gold: 1, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 60));
        }

        [Fact]
        public void RitualDecide_WontFinishBeforeRebirth_IsSkipped()
        {
            // now 86000 + 1000s left = 87000 > the 86400 deadline.
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain,
                Decide(R(), gold: 1e9, secondsToRun: 0, nowSec: 86000, rebirthDeadlineSec: 86400, timeLeft: 1000));
        }

        [Fact]
        public void RitualDecide_NoScheduledRebirth_SkipsTheDeadlineTest()
        {
            Assert.Equal(RitualMath.RitualAction.Fund,
                Decide(R(), gold: 1e9, secondsToRun: 0, nowSec: 1e9, rebirthDeadlineSec: -1, timeLeft: 1000));
        }

        [Fact]
        public void RitualDecide_SecondsToRun_IsADurationFilter()
        {
            // BR-30 means "only rituals that complete within 30s".
            Assert.Equal(RitualMath.RitualAction.Fund,
                Decide(R(), gold: 1e9, secondsToRun: 30, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 29));
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain,
                Decide(R(), gold: 1e9, secondsToRun: 30, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 31));
        }

        [Fact]
        public void RitualDecide_WithoutAnExplicitDuration_TheDefaultBoundIsOneHour()
        {
            Assert.Equal(RitualMath.RitualAction.Fund,
                Decide(R(), gold: 1e9, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 3600));
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain,
                Decide(R(), gold: 1e9, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1, timeLeft: 3601));
        }

        // The time-left probe is LAZY and evaluated at most once. That matters: computing it runs the
        // whole ritual-rate stack against the game, and the original never reached it when the gold
        // gate had already refused the ritual.
        [Fact]
        public void RitualDecide_DoesNotEvaluateTimeLeftUntilTheGoldGateHasPassed()
        {
            int calls = 0;
            Decide(R(unlocked: false), 1e9, 0, 0, -1, 10, () => calls++);
            Assert.Equal(0, calls);

            Decide(R(goldCost: 1e12, progress: 0), gold: 1, secondsToRun: 0, nowSec: 0, rebirthDeadlineSec: -1,
                   timeLeft: 10, probe: () => calls++);
            Assert.Equal(0, calls);

            Decide(R(), 1e9, 0, 0, -1, 10, () => calls++);
            Assert.Equal(1, calls);
        }

        // [QUIRK] Every skip reason produces the SAME action, and that action strips magic already
        // parked in the ritual. A ritual skipped merely for being slow this pass is drained exactly like
        // one that cannot be afforded — so a ritual can be funded, drained, and re-funded across passes
        // as its time-left estimate crosses the bound. Characterised, NOT fixed.
        [Fact]
        public void QUIRK_RitualDecide_ATooSlowRitualIsDrainedJustLikeAnUnaffordableOne()
        {
            var tooSlow = Decide(R(), 1e9, 0, 0, -1, timeLeft: 5000);
            var tooPoor = Decide(R(goldCost: 1e12), 1, 0, 0, -1, timeLeft: 10);
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain, tooSlow);
            Assert.Equal(tooPoor, tooSlow);
        }

        // [QUIRK] The deadline test runs BEFORE the duration test, so a ritual that cannot finish before
        // rebirth is skipped even when the token asked for a short duration that it does satisfy.
        [Fact]
        public void QUIRK_RitualDecide_TheRebirthDeadlineOutranksTheTokenDuration()
        {
            // 20s left satisfies BR-30, but the rebirth is 10s away.
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain,
                Decide(R(), gold: 1e9, secondsToRun: 30, nowSec: 90, rebirthDeadlineSec: 100, timeLeft: 20));
        }

        // ---------------- ProgressPerTick / TimeLeft ----------------

        private static RitualMath.RitualRateInputs Rate(long remaining = 1000, double power = 1e6,
            double dividerScale = 50000.0, double divider = 1.0, bool sadistic = false,
            double sadDiv = 1.0, double speedBonus = 1.0) =>
            new RitualMath.RitualRateInputs
            {
                Remaining = remaining,
                TotalMagicPower = power,
                DividerScale = dividerScale,
                SpeedDivider = divider,
                Sadistic = sadistic,
                SadisticDivider = sadDiv,
                SpeedBonus = speedBonus
            };

        [Fact]
        public void ProgressPerTick_MatchesTheGameArithmetic()
        {
            double expected = 1000 * 1e6 / (50000.0 * 2.0) * 3.0;
            Assert.Equal((float)expected, RitualMath.ProgressPerTick(Rate(divider: 2.0, speedBonus: 3.0)));
        }

        [Fact]
        public void ProgressPerTick_SadisticAppliesTheExtraDivider()
        {
            float plain = RitualMath.ProgressPerTick(Rate(dividerScale: 1.0));
            float sad = RitualMath.ProgressPerTick(Rate(dividerScale: 1.0, sadistic: true, sadDiv: 4.0));
            Assert.Equal(plain / 4f, sad, 3);
        }

        [Fact]
        public void ProgressPerTick_ClampsToZeroAndToFloatMax()
        {
            Assert.Equal(0f, RitualMath.ProgressPerTick(Rate(remaining: 0)));
            Assert.Equal(float.MaxValue, RitualMath.ProgressPerTick(Rate(remaining: long.MaxValue, power: 1e300, dividerScale: 1, divider: 1)));
        }

        [Fact]
        public void TimeLeft_IsRemainingProgressOverRateAtFiftyTicksPerSecond()
        {
            Assert.Equal((float)((1.0 - 0.25) / 0.5 / 50.0), RitualMath.TimeLeft(0.25, 0.5f), 6);
        }

        // [QUIRK] With a zero rate this divides by zero and returns +Infinity rather than guarding. Every
        // caller then compares it against a finite bound, so the ritual skips — the behaviour happens to
        // be safe, but it is unguarded and depends on IEEE semantics. Characterised, NOT fixed.
        [Fact]
        public void QUIRK_TimeLeft_ZeroRateReturnsInfinityRatherThanGuarding()
        {
            Assert.True(float.IsPositiveInfinity(RitualMath.TimeLeft(0.0, 0f)));
            Assert.Equal(RitualMath.RitualAction.SkipAndDrain,
                Decide(R(), 1e9, 0, 0, -1, timeLeft: float.PositiveInfinity));
        }

        // ---------------- MaxAllocationFor ----------------

        [Fact]
        public void MaxAllocationFor_BudgetAboveTheCap_TakesExactlyTheCap()
        {
            Assert.Equal(500, RitualMath.MaxAllocationFor(capValue: 500, remaining: 1000));
        }

        [Fact]
        public void MaxAllocationFor_BudgetBelowTheCap_SnapsToAWholeNumberOfTicks()
        {
            // ceil(1000/300) = 4 ticks; 1000/4 = 250; +1.
            Assert.Equal(251, RitualMath.MaxAllocationFor(capValue: 1000, remaining: 300));
        }

        // [QUIRK] This is the ONE wrong variant among the eight inlined copies of the stair-snap formula
        // (report 02 §12.4): every other lane multiplies by the game-verbatim 1.00000202655792 epsilon,
        // and this one adds a bare +1 instead. The two are not equivalent — the epsilon scales with the
        // amount, the +1 does not. Characterised, NOT fixed; correcting it changes ritual allocation.
        [Fact]
        public void QUIRK_MaxAllocationFor_UsesAFlatPlusOneWhereEveryOtherLaneUsesTheGameEpsilon()
        {
            long cap = 1_000_000_000L, remaining = 300_000_000L;
            long actual = RitualMath.MaxAllocationFor(cap, remaining);

            double ticks = Math.Ceiling(cap / (double)remaining);
            long flatPlusOne = (long)(cap / ticks) + 1L;
            long epsilonStyle = (long)Math.Ceiling(cap / ticks * 1.00000202655792);

            Assert.Equal(flatPlusOne, actual);
            Assert.NotEqual(epsilonStyle, actual);
        }

        // ---------------- RitualBP ----------------

        [Fact]
        public void RitualIndexUnlocked_TreatsTheCountAsACountNotATopIndex()
        {
            // ritualsUnlocked() == 7 means ids 0..6. RIT-7 is NOT valid.
            Assert.True(RitualMath.RitualIndexUnlocked(6, 7));
            Assert.False(RitualMath.RitualIndexUnlocked(7, 7));
            // 8 once the Troll challenge has 6+ completions.
            Assert.True(RitualMath.RitualIndexUnlocked(7, 8));
        }

        [Fact]
        public void RitualGoldGateBlocks_OnlyWhenUnaffordableAndUnstarted()
        {
            Assert.True(RitualMath.RitualGoldGateBlocks(goldCost: 100, gold: 1, progress: 0));
            Assert.False(RitualMath.RitualGoldGateBlocks(goldCost: 100, gold: 1, progress: 0.01));
            Assert.False(RitualMath.RitualGoldGateBlocks(goldCost: 1, gold: 100, progress: 0));
        }

        private static RitualMath.RitualCapInputs Cap(double power = 1e6, double bonus = 1.0,
            double diffTerm = 50000.0, bool unknown = false, long maxAlloc = 1_000_000, long idle = long.MaxValue) =>
            new RitualMath.RitualCapInputs
            {
                TotalMagicPower = power,
                SpeedBonus = bonus,
                DifficultyTerm = diffTerm,
                UnknownDifficulty = unknown,
                MaxAllocation = maxAlloc,
                IdleMagic = idle
            };

        [Fact]
        public void RitualCap_MatchesTheGameArithmetic()
        {
            var a = Cap(power: 1e3, bonus: 2.0, diffTerm: 50000.0 * 3.0, maxAlloc: 1000);
            double num = Math.Ceiling(1 / (1e3 * 2.0) * (50000.0 * 3.0));
            if (num < 1.0) num = 1.0;
            double num1 = Math.Ceiling(num / Math.Ceiling(num / 1000L) * 1.00000202655792);
            Assert.Equal((long)num1, RitualMath.RitualCap(a));
        }

        [Fact]
        public void RitualCap_ClampsToIdleMagic()
        {
            Assert.Equal(77, RitualMath.RitualCap(Cap(power: 1e-6, diffTerm: 1e12, idle: 77)));
        }

        [Fact]
        public void RitualCap_FloorsTheDivisorAtOne()
        {
            Assert.Equal(2, RitualMath.RitualCap(Cap(power: 1e30, diffTerm: 1.0, maxAlloc: 100)));
        }

        // [QUIRK] An unrecognised difficulty returns a cap of ZERO, and the lane then calls
        // SetInput(min(0, budget)) and adds nothing — a silent no-op, not a fallback to the budget.
        // Unreachable with the game's three-valued enum, but it is the shape of the code.
        [Fact]
        public void QUIRK_RitualCap_UnknownDifficultyReturnsZeroRatherThanFallingBackToTheBudget()
        {
            Assert.Equal(0, RitualMath.RitualCap(Cap(unknown: true, maxAlloc: 1_000_000)));
        }
		
		[Theory]
        [InlineData(-1, 7, false)]   // malformed token sentinel — the defect
        [InlineData(0, 7, true)]
        [InlineData(6, 7, true)]
        [InlineData(7, 7, false)]    // COUNT not top index — RIT-7 on 7 rituals
        [InlineData(7, 8, true)]     // Troll 6+ unlocks the 8th
        public void RitualIndexUnlocked_RejectsSentinelAndRespectsCount(
            int index, int unlocked, bool expected)
            => Assert.Equal(expected, RitualMath.RitualIndexUnlocked(index, unlocked));
    }
}
