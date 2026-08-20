using System.Collections.Generic;
using System.Linq;
using Xunit;
using NGUAdvisor.Managers;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// The sink bank clamp, and the reason it is GATED on WishManager.Unlocked().
    ///
    /// ── THE MECHANISM ─────────────────────────────────────────────────────────────────────────────
    /// Waterfill.BeginRound reserves `_remaining / roster.Count` for the sink every round; EndRound
    /// removes the whole reserve from `_remaining`. The reserve is a SHARE, the sink's appetite is a
    /// CEILING, and what the sink then declines becomes the REMAINDER.
    ///
    /// ── WHY THE GATE ──────────────────────────────────────────────────────────────────────────────
    /// The bank's consumer set is {sink, wish pass} — the remainder is the wish funding channel. So the
    /// bank may only be clamped to the sink's ceiling when the wish pass cannot run, i.e. pre-T8.
    ///
    /// BOTH BOARD SHAPES ARE IN THIS FIXTURE ON PURPOSE. The unconditional version of this clamp shipped
    /// on 2026-08-18 and was reverted the same day, and the fixture that let it through seated one
    /// bounded NGU against TimeMachine with no wish pass — so "freed resource reaches the lanes" was
    /// true and was the wrong question. A fixture that does not contain the shape that breaks is not
    /// evidence. See test 2.
    /// </summary>
    public class SinkBankClampTests
    {
        // ⚠ WHAT THIS FIXTURE CANNOT REACH, STATED SO NOBODY READS MORE INTO IT THAN IT PROVES.
        //
        // 1. THE GATE IS NOT UNDER TEST. Whether the clamp runs at all is decided by
        //    `wishesProvenLocked` in ConstraintLayerBridge.BuildSpec, which reads c.wishes.wishesOn
        //    and is Unity-bound. Every test here drives Compose directly and chooses the sink
        //    capacity by hand, so the one thing that could be inverted - a positive read of wishesOn
        //    versus `!WishManager.Unlocked()`, which fails the WRONG way on an unreadable board -
        //    has zero coverage. Test 2 below shows what the clamp WOULD do post-T8; it does not
        //    show that the gate stops it.
        //
        // 2. THE LANE SHAPE IS A MODEL, NOT THE BOARD. Compose only simulates its own fill when
        //    every seated non-sink lane has a known capacity (CapacitiesKnown, :265-272), so these
        //    fixtures give each lane a finite one. On the real pre-T8 board TimeMachine, BestAug and
        //    the AT slots are cap=self, CapacitiesKnown is FALSE, and the fill is driven by the
        //    BRIDGE calling each lane's own Allocate() with AppetiteProven retirement instead. So
        //    "the freed bank lands on TimeMachine and BestAug" is a property of THIS MODEL; the live
        //    claim rests on the measured log lines quoted in the header, not on this test.
        //
        // Both limits are recorded rather than papered over because the defect that started this -
        // an unconditional clamp shipping green - was exactly a fixture that did not contain the
        // shape that breaks.

        // Compose only simulates the fill when every seated non-sink lane carries a known capacity
        // (CapacitiesKnown). Live, `cap=self` lanes discover their take by calling the game's own
        // Allocate(), which is Unity-bound — so a Compose-only harness models a hungry self-limiting
        // lane as a rate lane with a capacity far above its share.
        private static ConstraintLayer.LaneSpec Lane(string label, long capacity, bool sink = false)
        {
            return new ConstraintLayer.LaneSpec
            {
                Name = sink ? "WandoosBP" : "NGUBP",
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = capacity,
                RateLane = !sink,
                SurplusSink = sink,
                WantsMore = true,
            };
        }

        private static long LaneTake(ConstraintLayer.Plan p) =>
            p.Lanes.Where((l, i) => i != p.SinkIndex).Sum(l => l.Allocation);

        // ── 1. THE PRE-T8 BOARD — the live save, 2026-08-18, energy pool 5.319e12 ─────────────────
        // Real lane set and real ceilings. Wandoos was offered 1,533,867,963,723 and took
        // 142,907,739,583; the declined 1,390,960,224,140 equalled the remainder EXACTLY, and the log
        // carried zero [WishDbg] lines. Every NGU lane is rate-bound at its ceiling; the only hungry
        // lanes are TimeMachine and BestAug, both taking >99.99% of every offer.
        private static List<ConstraintLayer.LaneSpec> PreT8Board(long sinkCap)
        {
            return new List<ConstraintLayer.LaneSpec>
            {
                Lane("Wandoos-0", sinkCap, sink: true),
                Lane("TimeMachine-0", 4_000_000_000_000L),   // cap=self, hungry
                Lane("BestAug-0", 4_000_000_000_000L),       // cap=self, hungry
                Lane("AdvancedTraining-0", 75_548_408_563L), // self-limits at this take
                Lane("AdvancedTraining-1", 75_548_408_563L),
                Lane("AdvancedTraining-3", 151_096_817_126L),
                Lane("AdvancedTraining-4", 151_096_817_126L),
                Lane("NGU-0", 10_500_106L),
                Lane("NGU-1", 10_503_139L),
                Lane("NGU-2", 9_318_315L),
                Lane("NGU-3", 10_507_955L),
                Lane("NGU-4", 10_529_509L),
                Lane("NGU-5", 10_534_528L),
                Lane("NGU-6", 746_613_267L),
                Lane("NGU-7", 10_784_308_845L),
                Lane("NGU-8", 172_933_978_642L),
            };
        }

        [Fact]
        public void Pre_T8_the_clamp_routes_the_dead_bank_to_the_two_hungry_lanes()
        {
            const long pool = 5_318_993_225_663L;
            const long sinkCap = 142_907_739_583L;

            var before = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), PreT8Board(ConstraintLayer.SelfLimiting));
            var after = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), PreT8Board(sinkCap));

            Assert.True(LaneTake(after) > LaneTake(before),
                "the clamp must free the dead bank: before=" + LaneTake(before) + " after=" + LaneTake(after));

            // It must land on TimeMachine and BestAug — the lanes that are actually hungry — and not be
            // absorbed by an NGU, every one of which is at its ceiling on this board.
            System.Func<ConstraintLayer.Plan, long> hungry = p => p.Lanes
                .Where(l => l.Label == "TimeMachine-0" || l.Label == "BestAug-0").Sum(l => l.Allocation);
            Assert.True(hungry(after) > hungry(before));

            foreach (var l in after.Lanes.Where(x => x.Label.StartsWith("NGU-")))
                Assert.True(l.Allocation <= l.Capacity, l.Label + " exceeded its ceiling");

            Assert.Equal(pool, after.Lanes.Sum(l => l.Allocation) + after.Unallocated);
        }

        // ── 2. THE POST-T8 BOARD — the bench, Melody's save, energy pool 9e18 ─────────────────────
        // THIS IS THE SHAPE THAT BROKE. NGU-7 and NGU-8 have ceilings of 3.55e18 and 9e18, at or above
        // the pool, so they are unbounded in practice. Clamping here does not feed TimeMachine — it
        // collapses the remainder to zero and the wish pass, whose only funding this is, gets nothing.
        private static List<ConstraintLayer.LaneSpec> PostT8Board(long sinkCap)
        {
            return new List<ConstraintLayer.LaneSpec>
            {
                Lane("Wandoos-0", sinkCap, sink: true),
                Lane("TimeMachine-0", 4_000_000_000_000_000_000L),
                Lane("NGU-6", 452_854_318_093_550_336L),
                Lane("NGU-7", 3_554_964_027_868_360_704L),
                Lane("NGU-8", 9_000_000_000_000_000_000L),   // >= pool: unbounded in practice
            };
        }

        [Fact]
        public void Post_T8_the_clamp_would_collapse_the_remainder_which_is_why_it_is_gated_off()
        {
            const long pool = 8_999_999_999_999_999_988L;
            const long sinkCap = 153_018_752_951_633L;

            var gatedOff = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), PostT8Board(ConstraintLayer.SelfLimiting));
            var ifItRan = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), PostT8Board(sinkCap));

            // Compose MODELS the sink as absorbing whatever it is handed — the real take is discovered
            // at execute time, when the bridge calls the lane's own Allocate(). So the planning-time
            // observable is SinkAllocation (what is handed over), and the remainder the wish pass will
            // actually see is what that exceeds the sink's ceiling by.
            System.Func<ConstraintLayer.Plan, long> impliedRemainder = p =>
                p.SinkAllocation > sinkCap ? p.SinkAllocation - sinkCap : 0L;

            // Gated off — today's behaviour — a large remainder survives to fund the wish pass.
            Assert.True(impliedRemainder(gatedOff) > pool / 10,
                "post-T8 the remainder must survive the fill as wish funding, got " + impliedRemainder(gatedOff));

            // If it ran, the remainder is destroyed. This is the regression, pinned.
            Assert.True(impliedRemainder(ifItRan) < impliedRemainder(gatedOff) / 100,
                "clamping on an unbounded-tail board collapses the wish channel: " + impliedRemainder(ifItRan));

            // ⚠ NOT ASSERTED HERE: live, TimeMachine LOST 8.4 points on this board (20.894% -> 12.484%)
            // while NGU-7/NGU-8 absorbed the surplus. This harness gives TM MORE, because live TM is
            // cap=self and is modelled here with a finite ceiling, which changes how the equal-share
            // offer splits against a 9e18 NGU tail. That divergence is left visible on purpose: the
            // measurement belongs in the log and the comment at ConstraintLayerBridge, not in an
            // assertion this model cannot honestly carry. Encoding live claims a fixture cannot
            // reproduce is what let the unconditional clamp ship in the first place.
        }

        // ── 3. INVARIANTS ────────────────────────────────────────────────────────────────────────
        [Fact]
        public void A_self_limiting_sink_is_completely_unaffected()
        {
            const long pool = 1_000_000L;
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("Wandoos-0", ConstraintLayer.SelfLimiting, sink: true),
                Lane("NGU-0", 10_553_946L),
                Lane("NGU-1", 10_556_999L),
            };
            var plan = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), lanes);
            Assert.Equal(pool, plan.Lanes.Sum(l => l.Allocation) + plan.Unallocated);
            Assert.True(plan.SinkSeated);
        }

        /// <summary>
        /// The clamp may only make the bank SMALLER, so it cannot reintroduce the sink starvation the
        /// per-round bank was added to fix (amendment 36 §7: Wandoos fell from 30.9% of the pool to
        /// under 0.1%). Where the SHARE, not the ceiling, is the binding term, the sink still gets it.
        /// </summary>
        [Fact]
        public void A_small_pool_still_gives_the_sink_its_share()
        {
            const long pool = 900L;
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("Wandoos-0", 1_000_000L, sink: true),
                Lane("NGU-0", 10_553_946L),
                Lane("NGU-1", 10_556_999L),
            };
            var plan = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), lanes);
            Assert.True(plan.SinkAllocation > 0,
                "the sink must not be starved when its share, not its ceiling, is the binding term");
        }

        [Fact]
        public void A_zero_capacity_sink_banks_nothing_and_closure_holds()
        {
            const long pool = 1_000_000L;
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("Wandoos-0", 0L, sink: true),
                Lane("NGU-0", 10_553_946L),
            };
            var plan = ConstraintLayer.Compose(pool, default(BudgetPass.BudgetState), lanes);
            Assert.Equal(pool, plan.Lanes.Sum(l => l.Allocation) + plan.Unallocated);
        }
    }
}
