using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // CHARACTERISATION tests for NguValueMath (extraction E2).
    //
    // Report 03 recorded this model's "Testability today: zero". These are its first tests. They pin
    // current behaviour, INCLUDING the three known defects (Q1 linear pricing, Q2 the share model, Q3
    // Rating-vs-Ratio) — each has a [QUIRK] test that asserts the wrong answer on purpose so that a
    // later fix cannot land silently.
    public class NguValueMathTests
    {
        private static NguValueMath.NguCandidate C(int id = 0, long level = 100, double divider = 1.0,
            double factor = 0.01, bool respawn = false, bool normalTrack = true) =>
            new NguValueMath.NguCandidate
            {
                Id = id,
                Level = level,
                Divider = divider,
                Factor = factor,
                IsRespawn = respawn,
                NormalTrack = normalTrack
            };

        private static Dictionary<int, NguValueMath.NguCandidate> ById(params NguValueMath.NguCandidate[] cs) =>
            cs.ToDictionary(c => c.Id);

        // ---------------- ValueRatio ----------------

        [Fact]
        public void ValueRatio_NoGrowth_IsUnity()
        {
            Assert.Equal(1.0, NguValueMath.ValueRatio(C(), 100, 0));
            Assert.Equal(1.0, NguValueMath.ValueRatio(C(), 100, -5));
        }

        [Fact]
        public void ValueRatio_WithABoostFactor_IsTheLinearBonusRatio()
        {
            var n = C(factor: 0.02);
            double expected = (1.0 + 0.02 * (100 + 50)) / (1.0 + 0.02 * 100);
            Assert.Equal(expected, NguValueMath.ValueRatio(n, 100, 50), 12);
        }

        [Fact]
        public void ValueRatio_UnreadableFactor_FallsBackToTheLevelRatio()
        {
            var n = C(factor: 0);
            Assert.Equal((100 + 50 + 1.0) / (100 + 1.0), NguValueMath.ValueRatio(n, 100, 50), 12);
        }

        [Fact]
        public void ValueRatio_MoreLevelsIsAlwaysWorthMore()
        {
            var n = C(factor: 0.01);
            Assert.True(NguValueMath.ValueRatio(n, 100, 100) > NguValueMath.ValueRatio(n, 100, 10));
        }

        // [QUIRK Q1] The game prices an NGU's bonus on a POWER curve above a per-NGU break level; this
        // model is linear in the level everywhere. The test states the current answer exactly, so the
        // day the power curve lands, this assertion has to be rewritten deliberately.
        [Fact]
        public void QUIRK_Q1_ValueRatio_IsLinearInLevelWithNoBreakPoint()
        {
            var n = C(factor: 0.001);
            // A linear model has no knee: the ratio for a fixed dL is a smooth function of L with no
            // regime change at any level, including levels far past any plausible in-game break.
            double atLow = NguValueMath.ValueRatio(n, 1_000, 1_000);
            double atHigh = NguValueMath.ValueRatio(n, 1_000_000_000, 1_000);
            Assert.Equal((1.0 + 0.001 * 2000) / (1.0 + 0.001 * 1000), atLow, 12);
            Assert.Equal((1.0 + 0.001 * 1_000_001_000) / (1.0 + 0.001 * 1_000_000_000), atHigh, 12);
            // and the deep NGU is priced as essentially worthless, which is the linear model's shape.
            Assert.True(atHigh < 1.000_002);
        }

        // ---------------- RespawnRatio ----------------

        [Fact]
        public void RespawnRatio_NormalTrackBelowFourHundred_UsesTheLinearBranch()
        {
            var n = C(id: 2, respawn: true, factor: 0.0005, normalTrack: true);
            double now = Math.Max(0.8, 1.0 - 0.0005 * 100);
            double after = Math.Max(0.8, 1.0 - 0.0005 * 200);
            Assert.Equal(now / after, NguValueMath.RespawnRatio(n, 100, 100), 12);
        }

        [Fact]
        public void RespawnRatio_AtTheNormalFloor_IsExactlyOne()
        {
            // Both ends clamp to 0.8, so now == after and a capped Respawn earns nothing.
            var n = C(id: 2, respawn: true, factor: 0.5, normalTrack: true);
            Assert.Equal(1.0, NguValueMath.RespawnRatio(n, 100, 100));
        }

        [Fact]
        public void RespawnRatio_EvilTrackHasItsOwnFloorsAndAsymptote()
        {
            var n = C(id: 2, respawn: true, factor: 1e-6, normalTrack: false);
            // below 10000 -> linear branch floored at 0.925
            Assert.True(NguValueMath.RespawnRatio(n, 100, 100) > 1.0);
            // far above -> asymptotic branch floored at 0.9
            var deep = NguValueMath.RespawnRatio(n, 5_000_000, 1_000);
            Assert.True(deep >= 1.0);
        }

        [Fact]
        public void ValueRatio_RoutesRespawnToItsOwnCurve()
        {
            var respawn = C(id: 2, respawn: true, factor: 0.5, normalTrack: true);
            var plain = C(id: 2, respawn: false, factor: 0.5, normalTrack: true);
            Assert.Equal(1.0, NguValueMath.ValueRatio(respawn, 100, 100));
            Assert.NotEqual(1.0, NguValueMath.ValueRatio(plain, 100, 100));
        }

        // ---------------- LevelsPerHourPerUnit ----------------

        [Fact]
        public void LevelsPerHourPerUnit_IsProgressPerTickTimesFiftyTimesThirtySixHundred()
        {
            double expected = 1e6 / 4.0 * 2.0 / (99 + 1) * 50.0 * 3600.0;
            Assert.Equal(expected, NguValueMath.LevelsPerHourPerUnit(1e6, 4.0, 2.0, 99), 6);
        }

        [Fact]
        public void LevelsPerHourPerUnit_NonPositiveDividerIsZero()
        {
            Assert.Equal(0, NguValueMath.LevelsPerHourPerUnit(1e6, 0, 1, 10));
            Assert.Equal(0, NguValueMath.LevelsPerHourPerUnit(1e6, -1, 1, 10));
        }

        // ---------------- Build ----------------

        [Fact]
        public void Build_SkipsOutOfRangeIdsAndUnreadableDividers()
        {
            var list = NguValueMath.Build(new[] { C(id: -1), C(id: 99), C(id: 3, divider: 0) },
                                          magic: false, power: 1e6, mult: 1, pool: 1e6);
            Assert.Empty(list);
        }

        [Fact]
        public void Build_NamesEnergyAndMagicNgusFromTheirOwnTables()
        {
            var e = NguValueMath.Build(new[] { C(id: 2) }, magic: false, power: 1e6, mult: 1, pool: 1e6);
            var m = NguValueMath.Build(new[] { C(id: 2) }, magic: true, power: 1e6, mult: 1, pool: 1e6);
            Assert.Equal("Respawn", e[0].Name);
            Assert.Equal("Power-β", m[0].Name);
        }

        [Fact]
        public void Build_MagicHasSevenNgusAndEnergyNine()
        {
            Assert.Equal(9, NguValueMath.ENames.Length);
            Assert.Equal(7, NguValueMath.MNames.Length);
            // id 7 and 8 exist on energy but not on magic — the ALLNGU parser tops match these.
            Assert.Single(NguValueMath.Build(new[] { C(id: 8) }, false, 1e6, 1, 1e6));
            Assert.Empty(NguValueMath.Build(new[] { C(id: 8) }, true, 1e6, 1, 1e6));
        }

        [Fact]
        public void Build_SortsByRatingDescending()
        {
            var list = NguValueMath.Build(
                new[] { C(id: 0, level: 1_000_000, divider: 1), C(id: 1, level: 1, divider: 1) },
                magic: false, power: 1e6, mult: 1, pool: 1e6);
            Assert.Equal(2, list.Count);
            Assert.True(list[0].Rating >= list[1].Rating);
            Assert.Equal(1, list[0].Id);   // the shallow NGU is worth more
        }

        // [QUIRK Q3] Rating is scored at the FULL pool; Ratio is only refined to the share inside Pick.
        // Build leaves them equal, and the final ordering is by Rating — so the ranking is decided by a
        // number computed at a budget no lane ever actually receives.
        [Fact]
        public void QUIRK_Q3_BuildScoresRatingAtTheFullPoolNotAtAnyShare()
        {
            var list = NguValueMath.Build(new[] { C(id: 0, level: 100, divider: 1, factor: 1e-9) },
                                          magic: false, power: 1e3, mult: 1, pool: 1e9);
            var e = list[0];
            Assert.Equal(e.Rating, e.Ratio);                        // not yet refined
            Assert.Equal(NguValueMath.ValueRatio(C(id: 0, level: 100, factor: 1e-9), 100, e.LphPerUnit * 1e9),
                         e.Rating, 12);
        }

        // ---------------- Pick ----------------

        [Fact]
        public void Pick_EmptyListPicksNothing()
        {
            Assert.Empty(NguValueMath.Pick(new List<NguValueMath.Entry>(), ById(), 1e6));
        }

        [Fact]
        public void Pick_EverythingHot_KeepsTheWholeSet()
        {
            // Big factor + big pool => every lane clears 1.05 at its share on the first pass.
            var cands = new[] { C(id: 0, divider: 1, factor: 1), C(id: 1, divider: 1, factor: 1) };
            var list = NguValueMath.Build(cands, false, 1e6, 1, 1e9);
            var picked = NguValueMath.Pick(list, ById(cands), 1e9);
            Assert.Equal(2, picked.Length);
        }

        [Fact]
        public void Pick_NothingHot_FallsBackToTheTopTwoByRating()
        {
            // Tiny factor => nothing clears the 1.05 bar at any share, so the fallback branch runs.
            var cands = Enumerable.Range(0, 5).Select(i => C(id: i, level: 1_000_000 * (i + 1), divider: 1, factor: 1e-12)).ToArray();
            var list = NguValueMath.Build(cands, false, 1.0, 1, 1e3);
            var picked = NguValueMath.Pick(list, ById(cands), 1e3);
            Assert.Equal(NguValueMath.NothingHotFallbackCount, picked.Length);
        }

        [Fact]
        public void Pick_ReturnsIdsOrderedByRatingDescending()
        {
            var cands = new[] { C(id: 0, level: 10, divider: 1, factor: 1e-3), C(id: 1, level: 1_000_000, divider: 1, factor: 1e-3) };
            var list = NguValueMath.Build(cands, false, 1e6, 1, 1e6);
            var picked = NguValueMath.Pick(list, ById(cands), 1e6);
            Assert.Equal(0, picked[0]);   // shallower NGU rates higher under the linear model
        }

        // [QUIRK Q2] Pick's budget model is `pool / keep.Count`, an equal split over the candidates it
        // happens to be looking at. The real allocator divides by prioCount — the seat count over the
        // WHOLE token list — and CAP tokens bypass the split entirely. This test states the model Pick
        // actually uses, so a fix that swaps in the real budget must edit it.
        [Fact]
        public void QUIRK_Q2_PickSplitsThePoolEvenlyOverItsOwnCandidatesNotOverTheTokenList()
        {
            var cands = new[]
            {
                C(id: 0, divider: 1, factor: 1e-6), C(id: 1, divider: 1, factor: 1e-6),
                C(id: 2, divider: 1, factor: 1e-6, respawn: false), C(id: 3, divider: 1, factor: 1e-6)
            };
            var list = NguValueMath.Build(cands, false, 1.0, 1.0, 1e6);
            NguValueMath.Pick(list, ById(cands), 1e6);
            // Every surviving entry's Lph is LphPerUnit x (pool / count-at-that-pass), never x a budget
            // derived from prioCount. With 4 candidates the first pass share is pool/4.
            var e = list.First(x => x.Id == 0);
            Assert.Equal(e.LphPerUnit * (1e6 / 4.0), e.Lph, 6);
        }

        [Fact]
        public void Pick_IsMonotoneAndTerminates()
        {
            // The prune only ever removes, so it cannot oscillate; 12 iterations is an upper bound it
            // never needs. Assert termination on a set engineered to prune one lane per pass.
            var cands = Enumerable.Range(0, 9)
                .Select(i => C(id: i, level: (long)Math.Pow(10, i), divider: 1, factor: 1e-4)).ToArray();
            var list = NguValueMath.Build(cands, false, 1e4, 1, 1e6);
            var picked = NguValueMath.Pick(list, ById(cands), 1e6);
            Assert.True(picked.Length >= 1 && picked.Length <= 9);
        }

        // ---------------- Surplus ----------------

        [Fact]
        public void Surplus_ExcludesTargetsAndAnythingAtOrBelowTheFloor()
        {
            var list = new List<NguValueMath.Entry>
            {
                new NguValueMath.Entry { Id = 0, Rating = 2.0 },
                new NguValueMath.Entry { Id = 1, Rating = 1.5 },
                new NguValueMath.Entry { Id = 2, Rating = 1.0 },        // a capped Respawn
                new NguValueMath.Entry { Id = 3, Rating = 1.00005 },    // below the 1.0001 floor
            };
            var s = NguValueMath.Surplus(list, new[] { 0 });
            Assert.Equal(new[] { 1 }, s);
        }

        [Fact]
        public void Surplus_IsOrderedByRatingDescending()
        {
            var list = new List<NguValueMath.Entry>
            {
                new NguValueMath.Entry { Id = 5, Rating = 1.2 },
                new NguValueMath.Entry { Id = 6, Rating = 3.4 },
            };
            Assert.Equal(new[] { 6, 5 }, NguValueMath.Surplus(list, new int[0]));
        }

        // ---------------- Stabilize ----------------

        private static List<NguValueMath.Entry> Rated(params (int id, double rating)[] rows) =>
            rows.Select(r => new NguValueMath.Entry { Id = r.id, Rating = r.rating }).ToList();

        [Fact]
        public void Stabilize_NoIncumbent_TakesTheFreshPick()
        {
            var all = Rated((0, 1.2), (1, 1.1));
            Assert.Equal(new[] { 0 }, NguValueMath.Stabilize(all, new[] { 0 }, new int[0]));
            Assert.Equal(new[] { 0 }, NguValueMath.Stabilize(all, new[] { 0 }, null));
        }

        [Fact]
        public void Stabilize_DifferentSetSize_TakesTheFreshPick()
        {
            var all = Rated((0, 1.2), (1, 1.1));
            Assert.Equal(new[] { 0, 1 }, NguValueMath.Stabilize(all, new[] { 0, 1 }, new[] { 0 }));
        }

        [Fact]
        public void Stabilize_IncumbentNoLongerACandidate_TakesTheFreshPick()
        {
            var all = Rated((0, 1.2), (1, 1.1));
            Assert.Equal(new[] { 0 }, NguValueMath.Stabilize(all, new[] { 0 }, new[] { 77 }));
        }

        [Fact]
        public void Stabilize_WithinTolerance_KeepsTheIncumbent()
        {
            // 0.1% apart — inside the 0.5% tie tolerance, so the churn is suppressed.
            var all = Rated((0, 1.1730), (1, 1.1718));
            Assert.Equal(new[] { 1 }, NguValueMath.Stabilize(all, new[] { 0 }, new[] { 1 }));
        }

        [Fact]
        public void Stabilize_FreshPickClearlyAhead_TakesOver()
        {
            var all = Rated((0, 2.0), (1, 1.0));
            Assert.Equal(new[] { 0 }, NguValueMath.Stabilize(all, new[] { 0 }, new[] { 1 }));
        }

        // ---------------- NGUBP predicates ----------------

        [Fact]
        public void NguTargetMet_NegativeTargetIsTheNeverFundMarker()
        {
            Assert.True(NguValueMath.NguTargetMet(-1, 0));
            Assert.True(NguValueMath.NguTargetMet(-1, long.MaxValue));
        }

        [Fact]
        public void NguTargetMet_ZeroTargetNeverReportsDone()
        {
            Assert.False(NguValueMath.NguTargetMet(0, long.MaxValue));
        }

        [Fact]
        public void NguTargetMet_MetAtOrAboveAPositiveTarget()
        {
            Assert.False(NguValueMath.NguTargetMet(500, 499));
            Assert.True(NguValueMath.NguTargetMet(500, 500));
        }

        [Fact]
        public void NguIndexInRange_MagicTopsAtSixEnergyAtEight()
        {
            Assert.True(NguValueMath.NguIndexInRange(true, 6));
            Assert.False(NguValueMath.NguIndexInRange(true, 7));
            Assert.True(NguValueMath.NguIndexInRange(false, 8));
            Assert.False(NguValueMath.NguIndexInRange(false, 9));
        }

        // ---------------- NguCap ----------------

        [Fact]
        public void NguCap_MatchesTheGameArithmeticStepForStep()
        {
            var a = new NguValueMath.NguCapInputs
            {
                LevelPlusOnePlusOffset = 100 + 1 + 500,
                Num2 = 1e6,
                SpeedDivider = 250.0,
                MaxAllocation = 1_000_000,
                IdlePool = long.MaxValue
            };
            double num3 = Math.Ceiling(250.0 * 601.0 / 1e6);
            if (num3 < 1.0) num3 = 1.0;
            double num4 = Math.Ceiling(num3 / Math.Ceiling(num3 / 1_000_000L) * 1.00000202655792);

            var r = NguValueMath.NguCap(a);
            Assert.Equal((long)num4, r.Num);
            Assert.Equal(num4 / num3, r.PPT, 12);
        }

        [Fact]
        public void NguCap_ClampsToTheIdlePool()
        {
            var a = new NguValueMath.NguCapInputs
            {
                LevelPlusOnePlusOffset = 1e9f,
                Num2 = 1.0,
                SpeedDivider = 1.0,
                MaxAllocation = long.MaxValue,
                IdlePool = 4242
            };
            Assert.Equal(4242, NguValueMath.NguCap(a).Num);
        }

        [Fact]
        public void NguCap_FloorsTheDivisorAtOne()
        {
            var a = new NguValueMath.NguCapInputs
            {
                LevelPlusOnePlusOffset = 1f,
                Num2 = 1e30,
                SpeedDivider = 1.0,
                MaxAllocation = 100,
                IdlePool = long.MaxValue
            };
            Assert.Equal(2, NguValueMath.NguCap(a).Num);
        }

        [Fact]
        public void NguCap_Offset_IsTheOneWindowStairTarget()
        {
            Assert.Equal(250, new NguValueMath.NguCapResult { PPT = 0.5 }.Offset);
        }

        // [QUIRK] `num1` is a FLOAT in the game's own code and this extraction keeps it one. Past 2^24
        // the level+1+offset term loses integer precision, so two NGUs a few levels apart resolve to the
        // identical cap. Characterised, NOT fixed.
        [Fact]
        public void QUIRK_NguCap_LevelTermIsFloatSoDeepLevelsLosePrecision()
        {
            long deep = 1L << 30;
            Assert.Equal((float)(deep + 1 + 500), (float)(deep + 2 + 500));

            NguValueMath.NguCapInputs In(long level) => new NguValueMath.NguCapInputs
            {
                LevelPlusOnePlusOffset = level + 1L + 500,
                Num2 = 1e3,
                SpeedDivider = 1.0,
                MaxAllocation = 1_000_000,
                IdlePool = long.MaxValue
            };
            Assert.Equal(NguValueMath.NguCap(In(deep)).Num, NguValueMath.NguCap(In(deep + 1)).Num);
        }
		[Theory]
        [InlineData(false, -1, false)]  // sentinel — energy
        [InlineData(true,  -1, false)]  // sentinel — magic
        [InlineData(false,  0, true)]
        [InlineData(false,  8, true)]
        [InlineData(false,  9, false)]
        [InlineData(true,   6, true)]
        [InlineData(true,   7, false)]
        public void NguIndexInRange_RejectsSentinelAndRespectsPoolBounds(bool magic, int index, bool expected) => Assert.Equal(expected, NguValueMath.NguIndexInRange(magic, index));
		
		// NguValueMathTests
		[Theory]
		[InlineData(false, -1, false)]
		[InlineData(true,  -1, false)]
		[InlineData(false,  0, true)]
		[InlineData(false,  8, true)]
		[InlineData(false,  9, false)]
		[InlineData(true,   6, true)]
		[InlineData(true,   7, false)]
		public void NguIndexInRange_RejectsSentinel(bool magic, int index, bool expected) => Assert.Equal(expected, NguValueMath.NguIndexInRange(magic, index));
    }
}
