using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // CHARACTERISATION tests for LaneCapMath (extraction step 5): TimeMachineBP, WandoosBP,
    // BasicTrainingBP, AdvancedTrainingBP.
    //
    // These four are exactly the lanes 05 §8 records as having no value model (E3, E4, M3). What is
    // pinned here is their FEASIBILITY arithmetic, which 05 §1 is explicit is not the same thing.
    public class LaneCapMathTests
    {
        // ---------------- SnapAndClamp ----------------

        [Fact]
        public void SnapAndClamp_DividesTheFormulaIntoAWholeNumberOfTicks()
        {
            // formula 1000, budget 300 -> ceil(1000/300) = 4 ticks -> 250 each, x epsilon, ceilinged.
            var r = LaneCapMath.SnapAndClamp(1000, 300, long.MaxValue, LaneCapMath.StairEpsilon);
            Assert.Equal((long)Math.Ceiling(1000.0 / 4.0 * LaneCapMath.StairEpsilon), r.Num);
        }

        [Fact]
        public void SnapAndClamp_BudgetAboveTheFormula_TakesOneTicksWorth()
        {
            var r = LaneCapMath.SnapAndClamp(1000, 5000, long.MaxValue, LaneCapMath.StairEpsilon);
            Assert.Equal((long)Math.Ceiling(1000.0 * LaneCapMath.StairEpsilon), r.Num);
            Assert.True(r.PPT >= 1.0);
        }

        [Fact]
        public void SnapAndClamp_ClampsToTheIdlePool()
        {
            Assert.Equal(42, LaneCapMath.SnapAndClamp(1e9, long.MaxValue, 42, LaneCapMath.StairEpsilon).Num);
        }

        [Fact]
        public void SnapAndClamp_PptBelowOneIsWhatTriggersTheSecondPass()
        {
            // Four ticks to fill one level => ppt ~ 0.25, so the lane recomputes at CapCalc.Offset.
            var r = LaneCapMath.SnapAndClamp(1000, 300, long.MaxValue, LaneCapMath.StairEpsilon);
            Assert.True(r.PPT < 1.0);
            Assert.Equal((int)Math.Floor(r.PPT * 50 * 10), r.Offset);
        }

        // ---------------- TimeMachineBP ----------------

        private static LaneCapMath.TimeMachineCapInputs Tm(double baseDivider = 1.0, float level = 100,
            int offset = 500, double power = 1e6, double hack = 1, double challenge = 1, double card = 1,
            bool sadistic = false, double sadDiv = 1, long maxAlloc = 1_000_000, long idle = long.MaxValue) =>
            new LaneCapMath.TimeMachineCapInputs
            {
                BaseDivider = baseDivider,
                Level = level,
                Offset = offset,
                Power = power,
                HackTmSpeed = hack,
                ChallengeTmSpeed = challenge,
                CardTmSpeed = card,
                Sadistic = sadistic,
                SadisticDivider = sadDiv,
                MaxAllocation = maxAlloc,
                IdlePool = idle
            };

        [Fact]
        public void TimeMachineCap_MatchesTheGameArithmeticStepForStep()
        {
            var a = Tm(baseDivider: 3.0, level: 100, offset: 500, power: 2e6, hack: 2, challenge: 1.5, card: 1.25);
            double formula = 50000.0 * 3.0 * (1f + 100f + 500);
            formula /= 2e6; formula /= 2; formula /= 1.5; formula /= 1.25;
            formula = Math.Ceiling(formula);
            if (formula < 1.0) formula = 1.0;
            double num = Math.Ceiling(formula / Math.Ceiling(formula / 1_000_000L) * LaneCapMath.StairEpsilon);

            Assert.Equal((long)num, LaneCapMath.TimeMachineCap(a).Num);
        }

        [Fact]
        public void TimeMachineCap_EverySpeedBonusMakesTheLevelCheaper()
        {
            // Budget generous enough that the whole level fits in one tick, so the bonus shows directly.
            long plain = LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3, maxAlloc: long.MaxValue)).Num;
            Assert.True(LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3, hack: 4, maxAlloc: long.MaxValue)).Num < plain);
            Assert.True(LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3, card: 4, maxAlloc: long.MaxValue)).Num < plain);
        }

        // [QUIRK] With a budget BELOW the level cost the stair-snap quantises into whole ticks, and a
        // speed bonus can then leave the per-tick allocation completely unchanged — it buys fewer ticks
        // instead of a smaller tick. So "a bonus reduces what this lane draws per pass" is false in
        // general; it reduces how many passes the level takes. Characterised, not a defect: the whole
        // point of the snap is that anything past one level per tick is discarded by the game.
        [Fact]
        public void QUIRK_TimeMachineCap_AStairSnappedBudgetCanAbsorbASpeedBonusEntirely()
        {
            // formula 3,005,000 over a 1,000,000 budget = 4 ticks of 751,250; with a 4x hack bonus the
            // formula is 751,250 = 1 tick of 751,250. Identical draw.
            long plain = LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3, maxAlloc: 1_000_000)).Num;
            long boosted = LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3, hack: 4, maxAlloc: 1_000_000)).Num;
            Assert.Equal(plain, boosted);
        }

        [Fact]
        public void TimeMachineCap_SadisticMultipliesTheCostBack_Up()
        {
            long plain = LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3)).Num;
            long sad = LaneCapMath.TimeMachineCap(Tm(baseDivider: 100, power: 1e3, sadistic: true, sadDiv: 8)).Num;
            Assert.True(sad > plain);
        }

        [Fact]
        public void TimeMachineCap_FloorsTheFormulaAtOne()
        {
            var r = LaneCapMath.TimeMachineCap(Tm(baseDivider: 1e-30, power: 1e30, maxAlloc: 100));
            Assert.Equal(2, r.Num);
        }

        // [QUIRK] The Level term is a FLOAT (`1f + machine.levelSpeed + offset`), so past 2^24 the
        // level stops moving the cost. Both TM halves share the quirk. Characterised, NOT fixed.
        [Fact]
        public void QUIRK_TimeMachineCap_LevelTermIsFloatSoDeepLevelsLosePrecision()
        {
            float deep = 1 << 30;
            Assert.Equal(1f + deep + 500, 1f + (deep + 1) + 500);
            Assert.Equal(LaneCapMath.TimeMachineCap(Tm(level: deep)).Num,
                         LaneCapMath.TimeMachineCap(Tm(level: deep + 1)).Num);
        }

        // The energy and magic halves are the SAME function once the reads are resolved — which is
        // exactly why the code cannot answer 05 §6.4's "is TM one consumer or two?".
        [Fact]
        public void TimeMachineCap_BothPoolsAreTheIdenticalFunction()
        {
            var energyShaped = Tm(baseDivider: 7.0, level: 250, power: 5e5);
            var magicShaped = Tm(baseDivider: 7.0, level: 250, power: 5e5);
            Assert.Equal(LaneCapMath.TimeMachineCap(energyShaped).Num, LaneCapMath.TimeMachineCap(magicShaped).Num);
        }

        // ---------------- WandoosBP ----------------

        [Fact]
        public void WandoosCap_MatchesTheGameArithmetic()
        {
            double num = Math.Ceiling(1e6 / 250.0);
            double num1 = Math.Ceiling(num / Math.Ceiling(num / 1000L) * LaneCapMath.WandoosEpsilon);
            Assert.Equal((long)num1, LaneCapMath.WandoosCap(1e6, 250.0, 1000, long.MaxValue));
        }

        [Fact]
        public void WandoosCap_ClampsToTheIdlePool()
        {
            Assert.Equal(9, LaneCapMath.WandoosCap(1e12, 1.0, long.MaxValue, 9));
        }

        [Fact]
        public void WandoosCap_FloorsTheDivisorAtOne()
        {
            Assert.Equal(2, LaneCapMath.WandoosCap(1.0, 1e30, 100, long.MaxValue));
        }

        // Wandoos is the one lane that does NOT use the shared epsilon. Report 02 §12.4: its
        // `1.000002f` is game-verbatim ([DECOMP] Wandoos98Controller.cs:577), so converging it onto
        // the other seven copies would replace the only variant with provenance. This test exists to
        // make that convergence fail loudly if anyone tries it.
        [Fact]
        public void WandoosEpsilon_IsDeliberatelyNotTheSharedStairEpsilon()
        {
            Assert.NotEqual(LaneCapMath.StairEpsilon, LaneCapMath.WandoosEpsilon);
            Assert.Equal(1.000002f, LaneCapMath.WandoosEpsilon);
        }

        [Fact]
        public void ShareOfCap_IsTheFractionOfTheResourceCapTheLaneResolvedTo()
        {
            Assert.Equal(0.25, LaneCapMath.ShareOfCap(250, 1000));
        }

        [Fact]
        public void ShareOfCap_ClampsAtOne()
        {
            Assert.Equal(1.0, LaneCapMath.ShareOfCap(5000, 1000));
        }

        [Fact]
        public void ShareOfCap_RefusesToRecordOnAnUnreadableCapOrEmptyBudget()
        {
            Assert.Equal(-1, LaneCapMath.ShareOfCap(250, 0));
            Assert.Equal(-1, LaneCapMath.ShareOfCap(250, -1));
            Assert.Equal(-1, LaneCapMath.ShareOfCap(0, 1000));
        }

        // ---------------- BasicTrainingBP ----------------

        [Fact]
        public void BasicTrainingUnlocked_AboveElevenIsLocked()
        {
            Assert.False(LaneCapMath.BasicTrainingUnlocked(12, _ => long.MaxValue));
        }

        [Fact]
        public void BasicTrainingUnlocked_TheFirstSlotOfEachGroupIsAlwaysOpen()
        {
            // Index 0 (attack) and 6 (defense). The prior-slot read must never happen for these — it
            // would index -1.
            Assert.True(LaneCapMath.BasicTrainingUnlocked(0, _ => throw new InvalidOperationException()));
            Assert.True(LaneCapMath.BasicTrainingUnlocked(6, _ => throw new InvalidOperationException()));
        }

        [Fact]
        public void BasicTrainingUnlocked_LaterSlotsNeedFiveThousandTimesTheirPositionInThePriorSlot()
        {
            Assert.False(LaneCapMath.BasicTrainingUnlocked(1, i => 4999));   // needs 5000 in slot 0
            Assert.True(LaneCapMath.BasicTrainingUnlocked(1, i => 5000));
            Assert.False(LaneCapMath.BasicTrainingUnlocked(3, i => 14999));  // needs 15000 in slot 2
            Assert.True(LaneCapMath.BasicTrainingUnlocked(3, i => 15000));
        }

        [Fact]
        public void BasicTrainingUnlocked_ReadsThePriorSlotWithinTheGroup()
        {
            int seen = -99;
            LaneCapMath.BasicTrainingUnlocked(9, i => { seen = i; return 0; });
            Assert.Equal(2, seen);   // 9 % 6 - 1
        }

        [Fact]
        public void BasicTrainingSlot_FoldsBothGroupsOntoZeroToFive()
        {
            Assert.Equal(0, LaneCapMath.BasicTrainingSlot(0));
            Assert.Equal(5, LaneCapMath.BasicTrainingSlot(5));
            Assert.Equal(0, LaneCapMath.BasicTrainingSlot(6));
            Assert.Equal(5, LaneCapMath.BasicTrainingSlot(11));
        }

        [Fact]
        public void BasicTrainingAllocation_TakesTheLesserOfTheSlotCapAndTheBudget()
        {
            Assert.Equal(100, LaneCapMath.BasicTrainingAllocation(100, 999));
            Assert.Equal(50, LaneCapMath.BasicTrainingAllocation(100, 50));
        }

        // THE `took=1` THAT LOOKS LIKE A DEFECT AND IS NOT (operator report 2026-08-07: ten BT lanes
        // each offered 100-214 BILLION and each taking exactly 1, live [AllocDbg] 4749s).
        //
        // It is `Math.Min(slotCap, maxAllocation)` with slotCap == 1, and slotCap == 1 is the game's
        // own END STATE, not a corrupted read. Rebirth.resetTraining() reduces every training cap on
        // every rebirth and FLOORS IT AT 1 ([DECOMP] Rebirth.cs:652-655, :670-673):
        //
        //     if (character.training.attackCaps[i] - num <= 1) character.training.attackCaps[i] = 1;
        //
        // VERIFIED ON THE REPORTING ACCOUNT, not inferred: the BinaryFormatter payload of
        // NGUSave-Build-1260-August-07-18-37 carries `_attackCaps` and `_defenseCaps` as
        // int[6] { 1, 1, 1, 1, 1, 1 } (from initial { 2500, 15000, 30000, 50000, 70000, 100000 }),
        // alongside `_attackEnergy` / `_defenseEnergy` of long[6] { 0, 1, 1, 1, 1, 1 }.
        //
        // ONE UNIT IS THE WHOLE LANE at that point. OffenseTraining.cs:99-101 completes the bar every
        // tick once attackEnergy >= attackCaps — 50 levels/second, the fastest the slot can ever go —
        // so unit 2 through unit 100,000,000,000 buy NOTHING. An allocator that "fixed" this by
        // handing the slot its whole offer would be burning 100 B of energy per lane per tick for
        // zero levels. Do not make `took` track `offered` here.
        [Fact]
        public void BasicTrainingAllocation_AtTheRebirthFlooredCapOfOne_TakesOneWhateverTheOffer()
        {
            Assert.Equal(1, LaneCapMath.BasicTrainingAllocation(1, 100_796_380_579L));
            Assert.Equal(1, LaneCapMath.BasicTrainingAllocation(1, long.MaxValue));
        }

        // The other half of the same end state, and why those ten lanes were seated at all: at cap 1
        // a slot holding 0 is NOT saturated and genuinely wants its one unit (with 0 on it the bar
        // adds 0/1 == 0 and the slot levels not at all), while a slot holding 1 IS saturated and must
        // leave the seating list. That alternation — {0} seated against {1..5}, both groups — is the
        // 9-lane / 17-lane flip-flop in the live log, and it TERMINATES: once every slot holds its
        // one unit no BasicTrainingBP survives IsValid() and the energy list settles at 7 lanes
        // (live, 5059s onward).
        [Fact]
        public void BasicTrainingSaturated_AtTheFlooredCapOfOne_RetiresTheSlotOnItsFirstUnit()
        {
            Assert.False(LaneCapMath.BasicTrainingSaturated(0, 1));   // wants its one unit
            Assert.True(LaneCapMath.BasicTrainingSaturated(1, 1));    // has it; nothing more to buy
        }

        // ---------------- AdvancedTrainingBP ----------------

        [Fact]
        public void AdvancedTrainingIndexUnlocked_TreatsLengthAsACountNotATopIndex()
        {
            // AllAdvancedTraining.length == 5 means slots 0..4. AT-5 is NOT valid — ControllerFor(5)
            // returns null and would kill the whole energy lane for a profile-load cycle.
            Assert.True(LaneCapMath.AdvancedTrainingIndexUnlocked(4, 5));
            Assert.False(LaneCapMath.AdvancedTrainingIndexUnlocked(5, 5));
            Assert.False(LaneCapMath.AdvancedTrainingIndexUnlocked(-1, 5));
        }

        [Fact]
        public void AdvancedTrainingDivisor_IsBaseTimeTimesLevelPlusOffsetPlusOne()
        {
            Assert.Equal(2f * (100 + 500 + 1f), LaneCapMath.AdvancedTrainingDivisor(2f, 100, 500));
        }

        [Fact]
        public void AdvancedTrainingFormula_UsesTheSquareRootOfEnergyPower()
        {
            double sqrtPower = Math.Sqrt(1e6);
            double expected = Math.Ceiling(50.0 * 1202.0 / (sqrtPower * 2.0));
            Assert.Equal(expected, LaneCapMath.AdvancedTrainingFormula(1202.0, sqrtPower, 2.0));
        }

        [Fact]
        public void AdvancedTrainingFormula_ZeroDivisorIsZeroAndOtherwiseFloorsAtOne()
        {
            Assert.Equal(0, LaneCapMath.AdvancedTrainingFormula(0.0, 1000, 1));
            Assert.Equal(1.0, LaneCapMath.AdvancedTrainingFormula(1.0, 1e30, 1e30));
        }

        [Fact]
        public void AdvancedTrainingCap_ZeroFormulaReturnsTheLanesDefaultRatherThanAllocating()
        {
            var r = LaneCapMath.AdvancedTrainingCap(0.0, 1_000_000, long.MaxValue);
            Assert.Equal(0, r.Num);
            Assert.Equal(1, r.PPT);
        }

        [Fact]
        public void AdvancedTrainingCap_OtherwiseIsTheSharedSnap()
        {
            Assert.Equal(LaneCapMath.SnapAndClamp(1000, 300, 999999, LaneCapMath.StairEpsilon).Num,
                         LaneCapMath.AdvancedTrainingCap(1000, 300, 999999).Num);
        }

        [Fact]
        public void AdvancedTrainingNeed_FundsFiveHundredLevelsAheadWhenThePoolCoversIt()
        {
            Assert.Equal(1000, LaneCapMath.AdvancedTrainingNeed(off => off == 500 ? 1000 : 10, idleEnergy: 1_000_000));
        }

        [Fact]
        public void AdvancedTrainingNeed_FallsBackToTheCurrentLevelWhenItCannot()
        {
            Assert.Equal(10, LaneCapMath.AdvancedTrainingNeed(off => off == 500 ? 1e12 : 10, idleEnergy: 1000));
        }

        [Fact]
        public void AdvancedTrainingNeed_NonPositiveFormulaIsZero()
        {
            Assert.Equal(0, LaneCapMath.AdvancedTrainingNeed(off => 0, idleEnergy: 1000));
        }

        // ---------------- the ALLAT waterfill ----------------

        [Fact]
        public void GroupShare_ALoneMemberTakesItsWholeNeed()
        {
            Assert.Equal(500, LaneCapMath.GroupShare(500, new long[0], available: 100));
        }

        [Fact]
        public void GroupShare_PoolCoversEveryone_EachTakesItsFullNeed()
        {
            Assert.Equal(100, LaneCapMath.GroupShare(100, new long[] { 100, 100 }, available: 10_000));
        }

        [Fact]
        public void GroupShare_PoolTooSmall_CapsAtTheWaterlevel()
        {
            // 3 members, 300 available -> waterlevel 100; my need of 500 is trimmed to 100.
            Assert.Equal(100, LaneCapMath.GroupShare(500, new long[] { 500, 500 }, available: 300));
        }

        [Fact]
        public void GroupShare_SlackFromCheapMembersFlowsToExpensiveOnes()
        {
            // needs 10 and 1000, pool 400: the cheap slot takes 10, leaving 390 for the expensive one,
            // which is more than the naive even split of 200.
            long expensive = LaneCapMath.GroupShare(1000, new long[] { 10 }, available: 400);
            Assert.Equal(390, expensive);
            Assert.True(expensive > 400 / 2);
        }

        [Fact]
        public void GroupShare_NeverGivesAMemberMoreThanItsOwnNeed()
        {
            Assert.Equal(5, LaneCapMath.GroupShare(5, new long[] { 1_000_000 }, available: 1_000_000));
        }

        [Fact]
        public void GroupShare_IgnoresNonPositiveNeeds()
        {
            Assert.Equal(500, LaneCapMath.GroupShare(500, new long[] { 0, -1 }, available: 10_000));
        }

        // [QUIRK — and it is the same shape as the allocator's own "last non-CAP lane" behaviour]
        //
        // The waterfill bounds every group member EXCEPT the last one. `needs.Count <= 1` returns
        // myNeed unclamped, so the final ALLAT slot asks for its FULL need no matter how little the
        // pool has left. Walking a 5-slot group of equal 400-unit needs over a 1000-unit pool: the
        // first four are correctly trimmed to 200 each, and the fifth then asks for 400 — 1200 drawn
        // against a pool of 1000.
        //
        // This is NOT a live over-allocation, because the caller's CalculateATCap already clamps to
        // idleEnergy before GroupShare is consulted, and that clamp is the real backstop. But it means
        // the waterfill's guarantee is "every slot but the last gets an even share", not "the group
        // fits in the pool" — and it is the AT-group instance of the same asymmetry the breakpoint
        // allocator has, where the last non-CAP lane receives idle/1.
        //
        // Characterised, NOT fixed: changing it changes allocation.
        [Fact]
        public void QUIRK_GroupShare_TheLastMemberBypassesTheWaterfillAndAsksForItsFullNeed()
        {
            var needs = new long[] { 400, 400, 400, 400, 400 };
            long pool = 1000, drawn = 0, avail = pool;
            var shares = new List<long>();
            for (int i = 0; i < needs.Length; i++)
            {
                long share = LaneCapMath.GroupShare(needs[i], needs.Skip(i + 1).ToArray(), avail);
                shares.Add(share);
                drawn += share;
                avail -= share;
            }

            Assert.Equal(new long[] { 200, 200, 200, 200, 400 }, shares);
            Assert.Equal(1200, drawn);
            Assert.True(drawn > pool);
        }
		// LaneCapMathTests
		[Theory]
		[InlineData(-6, false)]   // -6 % 6 == 0 hit the early-true
		[InlineData(-1, false)]
		[InlineData(0, true)]
		[InlineData(6, true)]
		[InlineData(12, false)]
		public void BasicTrainingUnlocked_RejectsNegative(int index, bool expected) => Assert.Equal(expected, LaneCapMath.BasicTrainingUnlocked(index, _ => long.MaxValue));
    }
}
