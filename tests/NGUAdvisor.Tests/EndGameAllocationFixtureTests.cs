using System.Collections.Generic;
using System.Linq;
using Xunit;
using NGUAdvisor.Managers;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// THE END-GAME FIXTURE. Real numbers, captured from a user's 686-hour Sadistic-track save running
    /// live in the bench on 2026-08-18, driven through the REAL allocator (ConstraintLayer.Compose is
    /// Unity-free and already linked here).
    ///
    /// ── WHY THIS FILE EXISTS ──────────────────────────────────────────────────────────────────────
    /// In one session that save surfaced four defects that a full green suite had never touched:
    ///
    ///   1. the digger budget was zero because DiggerCap defaulted to 0    (gross 1.39e87)
    ///   2. the wish pass compounded and collapsed the energy pool 9e18 -> 1.2e16
    ///   3. the board's denominator disagreed with the lanes it divided     (300%, then 150%)
    ///   4. NGU lanes never received a capacity off the Evil track, and plateaued near 40% of cap
    ///
    /// Every one of them needed END-GAME MAGNITUDES to exist at all. At 1e6 they are arithmetically
    /// invisible: a pool that never approaches the 9e18 hard cap cannot expose a lane that fails to
    /// stop at its cap, and a wish claim that compounds looks like rounding until the pool is the cap.
    /// The suite was 2281 tests green while all four were live.
    ///
    /// So the point of this file is NOT to re-test the allocator's happy path — the rest of the suite
    /// does that. It is to hold the allocator to invariants AT THE MAGNITUDES WHERE IT ACTUALLY RUNS,
    /// using a state nobody has to reach in-game to reproduce.
    ///
    /// ⚠ WHAT THIS CANNOT DO. Compose is pure, so this covers the layer's arithmetic and nothing else.
    /// It cannot see UiBridge's snapshot wiring, LoadoutManager.ChangeGear, or whether a capacity was
    /// ASKED FOR — defect 4 above lived in ConstraintLayerBridge deciding not to supply one, which is
    /// Unity-bound and still uncovered. Read EveryNguLaneMustCarryACapacity below for the seam.
    /// </summary>
    public class EndGameAllocationFixtureTests
    {
        // ── THE CAPTURE ───────────────────────────────────────────────────────────────────────────
        // From [AllocDbg] Energy, 2026-08-18 1:27 PM, wish sink mode on and the pool holding at cap:
        //   pool=8999999999999999978 lanes=11 seated=11 (constraint layer)
        // The game hard-caps energy at 9e18 (decomp Character.cs:918-921 hardCap()), so this is the
        // largest pool the allocator will ever be handed — the arithmetic has nowhere worse to go.
        private const long EndGamePool = 8_999_999_999_999_999_978L;

        // The eleven real lanes, with the capacities the game's own helpers report for them. Wandoos
        // is the surplus sink; the ten others are rate lanes carrying a genuine cap, which is what the
        // Evil-only gate used to withhold on this very save.
        private static List<ConstraintLayer.LaneSpec> EndGameLanes()
        {
            var caps = new (string Label, long Capacity)[]
            {
                ("CAPTimeMachine-0", 1_477_390_078_831_506_306L),
                ("CAPNGU-0",           820_682_473_533_495_575L),
                ("CAPNGU-1",           911_836_216_361_349_933L),
                ("CAPNGU-2",         1_025_778_395_905_603_622L),
                ("CAPNGU-3",         1_172_275_488_650_261_577L),
                ("CAPNGU-4",         1_367_604_991_074_614_191L),
                ("CAPNGU-5",         1_640_533_120_882_422_729L),
                ("CAPNGU-6",         2_044_498_515_933_722_568L),
                ("CAPNGU-7",         3_310_171_341_441_957_396L),
                ("CAPNGU-8",         3_733_748_487_514_524_383L),
            };

            var lanes = caps.Select(c => new ConstraintLayer.LaneSpec
            {
                Name = "NGUBP",
                Label = c.Label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = c.Capacity,
                RateLane = true,
                WantsMore = true,
            }).ToList();

            lanes.Insert(0, new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP",
                Label = "Wandoos-0",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                SurplusSink = true,
            });

            return lanes;
        }

        private static ConstraintLayer.Plan ComposeEndGame()
        {
            return ConstraintLayer.Compose(EndGamePool, default(BudgetPass.BudgetState), EndGameLanes());
        }

        // ── INVARIANTS ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// CLOSURE. Everything allocated plus everything unallocated is the pool — exactly, in longs,
        /// at 9e18. This is the invariant the board violated twice: it drew lanes against a
        /// denominator that did not account for them, and reported 300% and then 150%. The layer
        /// itself must never leave a unit unaccounted for, because every display downstream divides
        /// by some number derived from these.
        /// </summary>
        [Fact]
        public void The_plan_accounts_for_every_unit_of_an_end_game_pool()
        {
            var plan = ComposeEndGame();

            long allocated = plan.Lanes.Sum(l => l.Allocation);
            Assert.Equal(EndGamePool, allocated + plan.Unallocated);
        }

        /// <summary>
        /// No lane may take more than the pool. Trivially true at small numbers and the exact thing
        /// that stops being trivial near long.MaxValue, where a double round-trip in a share
        /// calculation can overshoot.
        /// </summary>
        [Fact]
        public void No_lane_can_out_take_the_pool()
        {
            var plan = ComposeEndGame();

            foreach (var lane in plan.Lanes)
            {
                Assert.True(lane.Allocation >= 0, lane.Label + " allocated a negative amount");
                Assert.True(lane.Allocation <= EndGamePool, lane.Label + " took more than the whole pool");
            }
        }

        /// <summary>
        /// A RATE LANE STOPS AT ITS CAP. Over-allocating past the cap is pure waste — the game discards
        /// it — and it is taken from a lane that could have used it.
        /// </summary>
        [Fact]
        public void A_rate_lane_never_exceeds_the_capacity_the_game_reported()
        {
            var lanes = EndGameLanes();
            var plan = ConstraintLayer.Compose(EndGamePool, default(BudgetPass.BudgetState), lanes);

            for (var i = 0; i < lanes.Count; i++)
            {
                if (!lanes[i].RateLane) continue;
                Assert.True(plan.Lanes[i].Allocation <= lanes[i].Capacity,
                    lanes[i].Label + " took " + plan.Lanes[i].Allocation +
                    " against a reported capacity of " + lanes[i].Capacity);
            }
        }

        /// <summary>
        /// THE SEAM DEFECT 4 CAME THROUGH, stated as an assertion about the FIXTURE rather than the
        /// code that builds it.
        ///
        /// The NGU lanes above all carry a real capacity because that is what the game's helpers
        /// return — energyNGUCapAmount/magicNGUCapAmount select the track themselves
        /// (normal -> level+1, evil -> evilLevel+1, sadistic -> sadisticLevel+1). Until 2026-08-18 the
        /// bridge only asked for that number on the Evil track, so on this save every NGU lane arrived
        /// as SelfLimiting and the waterfill guessed; the lanes plateaued near 40% of their caps with a
        /// full pool in front of them.
        ///
        /// This test cannot reach ConstraintLayerBridge.BuildSpec — it is Unity-bound. What it CAN do
        /// is refuse to let the fixture drift back into describing NGU lanes as self-limiting, which is
        /// what would silently make every other assertion in this file weaker than it looks.
        /// </summary>
        [Fact]
        public void Every_ngu_lane_in_the_fixture_carries_a_real_capacity()
        {
            var ngu = EndGameLanes().Where(l => l.Label.StartsWith("CAPNGU")).ToList();

            Assert.Equal(9, ngu.Count);
            foreach (var lane in ngu)
            {
                Assert.True(lane.RateLane, lane.Label + " must be a rate lane on every track");
                Assert.NotEqual(ConstraintLayer.SelfLimiting, lane.Capacity);
                Assert.True(lane.Capacity > 0, lane.Label + " must carry the game's reported cap");
            }
        }

        /// <summary>
        /// The sink absorbs the remainder and does not starve the rate lanes to do it. On this save
        /// Wandoos absorbs ~1.6e14 against a 9e18 pool — 0.002% — which is why "the surplus has exactly
        /// one destination" is true about termination and false about absorptive capacity.
        /// </summary>
        [Fact]
        public void The_sink_does_not_pre_empt_the_rate_lanes()
        {
            var lanes = EndGameLanes();
            var plan = ConstraintLayer.Compose(EndGamePool, default(BudgetPass.BudgetState), lanes);

            var sinkIndex = lanes.FindIndex(l => l.SurplusSink);
            long sinkTook = plan.Lanes[sinkIndex].Allocation;
            long rateTook = plan.Lanes.Where((l, i) => lanes[i].RateLane).Sum(l => l.Allocation);

            Assert.True(rateTook > sinkTook,
                "the sink took " + sinkTook + " while every rate lane together took " + rateTook);
        }

        /// <summary>
        /// Precision guard. The same eleven lanes at the hard cap versus one unit below it must not
        /// change the shape of the answer — if a double creeps into the share arithmetic, this is
        /// where it shows up first.
        /// </summary>
        [Fact]
        public void One_unit_below_the_hard_cap_changes_nothing_structural()
        {
            var atCap = ConstraintLayer.Compose(EndGamePool, default(BudgetPass.BudgetState), EndGameLanes());
            var below = ConstraintLayer.Compose(EndGamePool - 1, default(BudgetPass.BudgetState), EndGameLanes());

            Assert.Equal(atCap.Lanes.Length, below.Lanes.Length);
            long a = atCap.Lanes.Sum(l => l.Allocation) + atCap.Unallocated;
            long b = below.Lanes.Sum(l => l.Allocation) + below.Unallocated;
            Assert.Equal(EndGamePool, a);
            Assert.Equal(EndGamePool - 1, b);
        }
        /// <summary>
        /// A LANE THAT ABSORBS ITS WHOLE OFFER MUST KEEP ITS SEAT. Rule B used to retire it, because
        /// offers shrink every round (remaining / roster.Count) so a fully-absorbing lane's take MUST
        /// fall — it was retired for the offer shrinking, which it does by construction.
        ///
        /// Measured live on a mid-Evil save: TimeMachine-0 offered 1014366448125, took 1014356535925
        /// (99.999%), retired, and 66.127% of the energy pool then sat idle every tick with nowhere to
        /// go. This asserts the predicate directly, since the waterfill that consumes it is internal.
        /// </summary>
        [Fact]
        public void A_fully_absorbing_lane_is_not_retired_for_a_shrinking_offer()
        {
            // Round 1: offered 1000, took all of it. Round 2: offered less, took all of that too.
            Assert.True(ConstraintLayer.AppetiteProven(offer: 400, take: 400, previousTake: 1000, firstRound: false),
                "a lane that absorbed its entire offer has not shown a ceiling");

            // THE REAL NUMBERS. A stair lane can only take whole ticks, so it always leaves a sliver —
            // the first attempt at this fix tested for exact equality and changed nothing on the live
            // save. TimeMachine-0, 2026-08-18, one round: 99.9988% absorbed, 12,118,559 short.
            Assert.True(ConstraintLayer.AppetiteProven(
                    offer: 1_014_377_423_037L, take: 1_014_365_304_478L,
                    previousTake: 2_327_118_522_867L, firstRound: false),
                "a lane one stair short of its offer is offer-limited, not ceiling-limited");
        }

        [Fact]
        public void A_lane_that_leaves_something_on_the_table_and_declines_is_still_retired()
        {
            // The behaviour Rule B was written for is preserved: this lane could have taken more and
            // did not, AND it took less than last round — that is a real ceiling.
            Assert.False(ConstraintLayer.AppetiteProven(offer: 400, take: 300, previousTake: 1000, firstRound: false));

            // A genuinely ceiling-limited lane: its take is a FIXED number while the offer moves, so
            // once the offer exceeds the ceiling it leaves tens of percent behind and must still retire.
            Assert.False(ConstraintLayer.AppetiteProven(
                offer: 1_000_000_000L, take: 600_000_000L, previousTake: 600_000_000L, firstRound: false));
        }

        [Fact]
        public void A_lane_taking_nothing_is_retired_however_it_is_offered()
        {
            Assert.False(ConstraintLayer.AppetiteProven(offer: 400, take: 0, previousTake: 0, firstRound: false));
            Assert.False(ConstraintLayer.AppetiteProven(offer: 0, take: 0, previousTake: 0, firstRound: true));
        }

        [Fact]
        public void Rule_A_still_retires_a_lane_that_took_less_than_half_its_offer()
        {
            // Unchanged: taking under half the offer is a ceiling regardless of history.
            Assert.False(ConstraintLayer.AppetiteProven(offer: 1000, take: 400, previousTake: 0, firstRound: true));
            Assert.True(ConstraintLayer.AppetiteProven(offer: 1000, take: 501, previousTake: 0, firstRound: true));
        }
    }
}
