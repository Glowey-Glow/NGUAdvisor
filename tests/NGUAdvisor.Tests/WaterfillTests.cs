using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE WATERFILL (amendment 29) — the fix for the operator's 2026-08-07 report that ~85% of the
    // energy pool goes unallocated on EVERY energy swap.
    //
    // [OPERATOR]: "All of the systems that can take a resource should be in consideration for the
    // sink, not just wandoos. where they could take a fraction and it spread across multiple systems
    // for constant gain as well as wandoos."
    //
    // ⚠ THIS IS A REAL BEHAVIOUR CHANGE ON WELL-FORMED INPUT. Round 1 is the shipped single pass
    // byte for byte (ConstraintLayerTests.The_live_85_percent_remainder_is_reproduced_by_the_shipped_
    // fill still passes untouched); rounds 2+ re-offer what round 1 left to the lanes that proved
    // appetite in it. Three things are pinned here and nowhere else: that a lane at a CEILING is not
    // spammed, that the loop TERMINATES, and that the sweep can FAIL — a negative control with a case
    // the waterfill must not move and a case it must move entirely.
    public class WaterfillTests
    {
        private static BudgetPass.BudgetState NoBudgetPressure() =>
            new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 };

        private static ConstraintLayer.LaneSpec Lane(string name, long capacity, string label = null)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = capacity,
                WantsMore = true,
            };

        private static ConstraintLayer.LaneSpec SelfLimiting(string name, string label)
            => Lane(name, ConstraintLayer.SelfLimiting, label);

        private static ConstraintLayer.LaneSpec Sink()
            => new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP",
                Label = "CAPWandoos-0",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
                SurplusSink = true,
            };

        // ---- the game's stair arithmetic, as a take function ---------------------------------------

        // THE SHIPPED ARITHMETIC, not a model of it: AugmentMath.AugCap with every bonus neutralised
        // so its `num1` — the lane's ONE-LEVEL-PER-TICK cost — is exactly `cost`. That is the same
        // synthesis ConstraintLayer.RateChunk already performs on NguValueMath.NguCap (it sets
        // SpeedDivider = capacity so the game's num3 IS the capacity), and it is what lets a test
        // drive a SELF-LIMITING lane without a Unity scene.
        //
        //   num1 = 1 / (TotalEnergyPower / (Level + 1 + Offset)) x DividerScale x SpeedDivider
        //        = (cost - 1) + 1 + 0                                  with TEP = scale = div = 1
        // then every divisor below is 1 and every flag false, so num1 = ceil(cost) = cost.
        private static long StairTake(double cost, long offer, long idlePool)
        {
            if (offer <= 0) return 0;
            return AugmentMath.AugCap(new AugmentMath.AugCapInputs
            {
                Level = cost - 1.0,
                Offset = 0,
                TotalEnergyPower = 1.0,
                SpeedDivider = 1.0,
                DividerScale = 1.0,
                AugsSpecBonus = 0.0,
                MacguffinBonus = 1.0,
                HackAugSpeed = 1.0,
                ItopodAugSpeed = 1.0,
                CardAugSpeed = 1.0,
                NoAugsEvilCompletions = 0.0,
                NoAugsCompletedOnce = false,
                NoAugsEvilMaxed = false,
                Sadistic = false,
                SadisticDivider = 1.0,
                Allocation = offer,
                IdleEnergy = idlePool,
            }).Num;
        }

        // The executor loop, EXACTLY as ConstraintLayerBridge.PerformSwap drives it: Waterfill opens
        // a round, each live lane is offered its share, the lane's own arithmetic decides the take,
        // and the round closes on a LIVE pool read. `costs` supplies the self-limiting lanes' stair
        // cost by index; a known-capacity lane takes its whole offer, as Compose's fill does.
        private sealed class Replay
        {
            public long[] Total;
            public long[] Round1Offer;
            public long[] Round1Take;
            public long Idle;
            public long SinkTotal;
            public int Rounds;
        }

        // `takeFor[i]` is lane i's own Allocate(): (offer, idlePool) -> what it absorbed. A lane with
        // no entry takes its whole offer, which is what a KNOWN capacity means (Offer already clamped
        // it to min(residual capacity, share)).
        private static Replay Drive(long pool, IList<ConstraintLayer.LaneSpec> specs,
            IDictionary<int, Func<long, long, long>> takeFor)
        {
            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), specs);
            var n = plan.Lanes.Length;
            var r = new Replay
            {
                Total = new long[n],
                Round1Offer = new long[n],
                Round1Take = new long[n],
                Idle = pool,
            };

            var fill = new ConstraintLayer.Waterfill(pool, plan.Lanes, plan.SinkIndex);
            ConstraintLayer.FillSession session;
            while ((session = fill.BeginRound()) != null)
            {
                r.Rounds++;
                Assert.True(r.Rounds < 200, "the waterfill did not terminate");
                for (int i = 0; i < n; i++)
                {
                    if (i == plan.SinkIndex || !fill.IsLive(i))
                        continue;
                    string skip;
                    var lane = fill.LaneForRound(i);
                    var offer = session.Offer(lane, out skip);

                    Func<long, long, long> own;
                    long take = takeFor != null && takeFor.TryGetValue(i, out own)
                        ? own(Math.Min(offer, r.Idle), r.Idle)
                        : Math.Min(offer, r.Idle);

                    if (r.Rounds == 1)
                    {
                        r.Round1Offer[i] = offer;
                        r.Round1Take[i] = take;
                    }
                    r.Total[i] += take;
                    r.Idle -= take;
                    session.Commit(take);
                    fill.Record(i, offer, take);
                }
                fill.EndRound(r.Idle);
            }
            r.SinkTotal = fill.SinkTotal;
            return r;
        }

        // A lane that CHUNKS: the shipped stair arithmetic at a declared one-level-per-tick cost.
        private static Func<long, long, long> Chunker(double cost) =>
            (offer, idle) => StairTake(cost, offer, idle);

        // A lane at a CEILING: it absorbs at most `ceiling` however much it is offered. This is what
        // the six live NGU rows are — `cap=self offered=244861160667 took=81079664` — and modelling
        // them as self-limiting rather than as known-capacity is what makes the replay reproduce the
        // block's `offered=` column as well as its `took=` column.
        private static Func<long, long, long> Ceiling(long ceiling) =>
            (offer, idle) => Math.Min(ceiling, offer);

        // ---- THE LIVE BLOCK, [AllocDbg] 8/7/2026-10:47 PM (5379s) ----------------------------------

        private const long LivePool = 1_713_961_926_335L;
        private const long LiveIdle = 1_464_028_202_041L;      // 85.418% of the pool, EVERY swap
        private const long BestAugOffer = 244_851_703_762L;    // verbatim `offered=`
        private const long BestAugTake = 244_794_962_329L;     // verbatim `took=`

        // ⚠ THE LIVE LOG DOES NOT DETERMINE BestAug's ONE-LEVEL COST, and pretending otherwise would
        // be the whole finding. `took = ceil(cost / ceil(cost/offer) x F)`, so ONE (offer, take) pair
        // fixes only the CHUNK, `cost/k = 244,794,466,238` — and every k from 2 to 4277 reproduces
        // the live take to the unit through the shipped AugCap. (Verified by exhaustive search over
        // k; k > 4277 fails because `cost > (k-1) x offer` stops holding. The five 4749s-5379s samples
        // cannot narrow it either: the ratio take/offer equals cost/k/offer, which is independent of
        // k, and BestAug re-ranks all seven pairs every tick and splits across two halves, so "its"
        // cost is not one number across samples anyway.)
        //
        // So what the waterfill does with the idle 1.46 T is a BAND, and both ends are pinned below.
        private const double BestAugCostFloor = 489_588_932_476d;          // k = 2
        private const double BestAugCostCeiling = 1_046_985_932_099_926d;  // k = 4277

        // The live block with BestAug modelled as what it IS — self-limiting and chunking — instead of
        // as a lane whose capacity happens to equal its take. The six NGUs keep their live takes as
        // capacities: they ARE capacity-bound (81 M of a 244 B offer, 4.7 B of a 1.47 T offer), which
        // is what ConstraintLayerTests' replay of the same block establishes.
        private static readonly long[] LiveNguTakes =
            { 81_079_664L, 81_113_322L, 81_168_387L, 81_410_002L, 81_465_735L, 4_732_524_855L };

        private static List<ConstraintLayer.LaneSpec> LiveBlock()
        {
            var specs = new List<ConstraintLayer.LaneSpec> { SelfLimiting("BestAug", "BestAug-0") };
            foreach (var id in new[] { 0, 1, 3, 4, 5, 6 })
                specs.Add(SelfLimiting("NGUBP", "NGU-" + id));
            return specs;
        }

        // Lane 0 is BestAug; lanes 1-6 are the six NGUs, every one of them at a ceiling.
        private static Dictionary<int, Func<long, long, long>> LiveTakes(Func<long, long, long> bestAug)
        {
            var map = new Dictionary<int, Func<long, long, long>> { { 0, bestAug } };
            for (int i = 0; i < LiveNguTakes.Length; i++)
                map[i + 1] = Ceiling(LiveNguTakes[i]);
            return map;
        }

        // ROUND 1 IS THE BEFORE-PICTURE, AND IT IS UNTOUCHED. Every `offered=` and every `took=` in
        // the live block, to the unit, and the 1,464,028,202,041 that fell out as idle — reproduced
        // for BOTH ends of the cost band, because round 1 depends on the chunk and not on k.
        [Theory]
        [InlineData(BestAugCostFloor)]
        [InlineData(BestAugCostCeiling)]
        public void Round_one_still_reproduces_the_live_block_to_the_unit(double cost)
        {
            var r = Drive(LivePool, LiveBlock(), LiveTakes(Chunker(cost)));

            Assert.Equal(BestAugOffer, r.Round1Offer[0]);
            Assert.Equal(BestAugTake, r.Round1Take[0]);

            // The six NGU offers, verbatim from the block — every one of them, in order.
            Assert.Equal(244_861_160_667L, r.Round1Offer[1]);
            Assert.Equal(293_817_176_868L, r.Round1Offer[2]);
            Assert.Equal(367_251_192_755L, r.Round1Offer[3]);
            Assert.Equal(489_641_200_877L, r.Round1Offer[4]);
            Assert.Equal(734_421_096_315L, r.Round1Offer[5]);
            Assert.Equal(1_468_760_726_896L, r.Round1Offer[6]);

            Assert.Equal(81_079_664L, r.Round1Take[1]);
            Assert.Equal(81_113_322L, r.Round1Take[2]);
            Assert.Equal(81_168_387L, r.Round1Take[3]);
            Assert.Equal(81_410_002L, r.Round1Take[4]);
            Assert.Equal(81_465_735L, r.Round1Take[5]);
            Assert.Equal(4_732_524_855L, r.Round1Take[6]);

            // ...and the remainder the operator reported, to the unit.
            Assert.Equal(LiveIdle, LivePool - r.Round1Take.Sum());
        }

        // WHAT THE 1.46 T BECOMES AT THE CHEAPEST COST THE LOG ALLOWS (k = 2) — the WORST case, and
        // the FLOOR of the claim. BestAug's whole one-level price is 489,588,932,476 on this reading,
        // so it can absorb one more full level and no more; round 2 hands it exactly that and rule A
        // retires it, because 2 x 489.6 B does not exceed the 1.464 T it was offered.
        [Fact]
        public void The_idle_pool_at_the_cheapest_cost_the_log_allows_one_more_full_level()
        {
            var r = Drive(LivePool, LiveBlock(), LiveTakes(Chunker(BestAugCostFloor)));

            Assert.Equal(2, r.Rounds);
            Assert.Equal(BestAugTake, r.Round1Take[0]);
            Assert.Equal(734_384_886_986L, r.Total[0]);
            Assert.Equal(489_589_924_657L, r.Total[0] - r.Round1Take[0]);   // one more full level

            // NOT ONE NGU WAS OFFERED A SECOND UNIT: all six failed rule A in round 1.
            for (int i = 0; i < LiveNguTakes.Length; i++)
                Assert.Equal(LiveNguTakes[i], r.Total[i + 1]);

            // The idle pool falls from 85.418% to 56.85% — 489.6 B moved, 974.4 B still idle because
            // on this reading of the log nothing left in the set can use it.
            Assert.Equal(974_438_277_384L, r.Idle);
            Assert.Equal(r.Idle, r.SinkTotal);        // no sink in this set: SinkTotal IS the idle
            Assert.InRange(r.Idle * 100.0 / LivePool, 56.8, 56.9);
            Assert.True(r.Idle < LiveIdle * 2 / 3, "the idle pool must fall by at least a third");
            Assert.Equal(LivePool, r.Total.Sum() + r.Idle);
        }

        // WHAT IT BECOMES AT THE DEAREST COST THE LOG ALLOWS (k = 4277) — the CEILING of the claim,
        // and the one the log's own behaviour points at (a lane taking 99.977% of its offer five
        // samples running is deep in its chunking regime, not one level from the top of it).
        [Fact]
        public void The_idle_pool_at_the_dearest_cost_the_log_allows_is_gone_entirely()
        {
            var r = Drive(LivePool, LiveBlock(), LiveTakes(Chunker(BestAugCostCeiling)));

            Assert.Equal(3, r.Rounds);
            Assert.Equal(BestAugTake, r.Round1Take[0]);
            Assert.Equal(1_708_823_164_370L, r.Total[0]);

            // Same six NGUs, same single offer each — the ceiling lanes are untouched either way.
            for (int i = 0; i < LiveNguTakes.Length; i++)
                Assert.Equal(LiveNguTakes[i], r.Total[i + 1]);

            // 85.418% idle becomes ZERO. Three rounds, and no counter anywhere in the loop: the stair
            // arithmetic converges quadratically — a chunking lane offered R leaves about R^2/cost.
            Assert.Equal(0, r.Idle);
            Assert.Equal(LivePool, r.Total.Sum());
        }

        // ⚠ THE NEGATIVE CONTROL, and it is what makes the two above mean anything. A sweep that
        // cannot fail proves nothing.
        //
        // THE CASE THE WATERFILL MUST NOT MOVE: the same block with every lane KNOWN to be at its
        // ceiling — which is exactly ConstraintLayerTests' replay, BestAug's capacity being the
        // 244,794,962,329 it took. Residual capacity is zero for all seven after round 1, so the
        // second BeginRound finds no claimant and the loop is over. The remainder comes out at the
        // live 1,464,028,202,041 TO THE UNIT: the fill is bit-identical to the shipped one.
        //
        // THE CASE IT MUST MOVE ENTIRELY: the same block, the same driver, one number changed —
        // BestAug self-limiting at the dearest cost the log allows. Idle goes to zero.
        [Fact]
        public void Negative_control_a_block_of_ceilings_is_a_no_op_and_the_test_can_tell()
        {
            var ceilings = new List<ConstraintLayer.LaneSpec>
            {
                Lane("BestAug", BestAugTake, "BestAug-0"),
                Lane("NGUBP", LiveNguTakes[0], "NGU-0"),
                Lane("NGUBP", LiveNguTakes[1], "NGU-1"),
                Lane("NGUBP", LiveNguTakes[2], "NGU-3"),
                Lane("NGUBP", LiveNguTakes[3], "NGU-4"),
                Lane("NGUBP", LiveNguTakes[4], "NGU-5"),
                Lane("NGUBP", LiveNguTakes[5], "NGU-6"),
            };

            var noop = Drive(LivePool, ceilings, null);
            Assert.Equal(1, noop.Rounds);
            Assert.Equal(LiveIdle, noop.Idle);
            Assert.Equal(BestAugTake, noop.Total[0]);
            Assert.Equal(LivePool, noop.Total.Sum() + noop.Idle);

            var hungry = Drive(LivePool, LiveBlock(), LiveTakes(Chunker(BestAugCostCeiling)));
            Assert.Equal(0, hungry.Idle);

            // 1,464,028,202,041 against 0, from the same code on the same block. The sweep can fail.
            Assert.True(noop.Idle > 0 && hungry.Idle == 0,
                "the test must be able to tell a ceiling from an appetite");
        }

        // ⚠ THE THIRD CASE, AND THE UNCOMFORTABLE ONE: a lane whose ceiling sits ABOVE half its offer
        // passes rule A — the theorem only runs one way — so it is offered a second time and takes a
        // second full ceiling's worth before rule B retires it. Modelled here at exactly the live
        // shape: BestAug taking 99.977% of its offer because its ceiling IS 244,794,962,329.
        //
        // The overshoot is 244,794,962,329 out of a 1,464,028,202,041 pool that was previously 100%
        // idle, it happens ONCE, and the lane is gone from round 3. It cannot be avoided without
        // knowing the ceiling before offering, which nothing on this path does.
        [Fact]
        public void A_ceiling_above_half_the_offer_costs_exactly_one_extra_helping()
        {
            var r = Drive(LivePool, LiveBlock(), LiveTakes(Ceiling(BestAugTake)));

            Assert.Equal(2, r.Rounds);
            Assert.Equal(BestAugTake, r.Round1Take[0]);
            Assert.Equal(BestAugTake * 2, r.Total[0]);          // exactly one extra, never two
            Assert.Equal(489_589_924_658L, r.Total[0]);
            Assert.Equal(1_219_233_239_712L, r.Idle);
            Assert.True(r.Idle < LiveIdle, "even the overshoot case leaves less idle than before");
        }

        // ---- rule A and rule B, the two theorems -------------------------------------------------

        // RULE A: `2 x take <= offer` PROVES the lane's chunk divisor is 1, i.e. that it is at its
        // ceiling. Exact — the 2 is k_min, not a threshold.
        [Fact]
        public void AppetiteProven_rule_A_retires_a_lane_that_took_half_its_offer_or_less()
        {
            Assert.False(ConstraintLayer.AppetiteProven(offer: 100, take: 50, previousTake: 0, firstRound: true));
            Assert.False(ConstraintLayer.AppetiteProven(offer: 100, take: 49, previousTake: 0, firstRound: true));
            Assert.True(ConstraintLayer.AppetiteProven(offer: 100, take: 51, previousTake: 0, firstRound: true));

            // The live shapes: a cap-1 BasicTraining slot, an NGU past its per-tick price, BestAug.
            Assert.False(ConstraintLayer.AppetiteProven(100_796_380_579L, 1, 0, true));
            Assert.False(ConstraintLayer.AppetiteProven(244_861_160_667L, 81_079_664L, 0, true));
            Assert.True(ConstraintLayer.AppetiteProven(BestAugOffer, BestAugTake, 0, true));

            // Nothing offered, or nothing absorbed, is never an appetite.
            Assert.False(ConstraintLayer.AppetiteProven(0, 0, 0, true));
            Assert.False(ConstraintLayer.AppetiteProven(1000, 0, 0, true));

            // No overflow at the top of the range — the pool is a long and so is the arithmetic.
            Assert.True(ConstraintLayer.AppetiteProven(long.MaxValue, long.MaxValue, 0, true));
        }

        // RULE B: a lane at its ceiling returns THE SAME take whatever it is offered, so a take that
        // does not rise is the ceiling's signature. This is what stops the one case rule A misses —
        // a ceiling cost above half the offer.
        [Fact]
        public void AppetiteProven_rule_B_retires_a_lane_whose_take_stopped_rising()
        {
            // Round 1 has nothing to improve on, so previousTake is ignored.
            Assert.True(ConstraintLayer.AppetiteProven(100, 90, previousTake: 90, firstRound: true));
            // Round 2 onward it is not.
            Assert.False(ConstraintLayer.AppetiteProven(100, 90, previousTake: 90, firstRound: false));
            Assert.False(ConstraintLayer.AppetiteProven(100, 90, previousTake: 91, firstRound: false));
            Assert.True(ConstraintLayer.AppetiteProven(100, 90, previousTake: 89, firstRound: false));
        }

        // ---- termination -------------------------------------------------------------------------

        // THE PATHOLOGICAL CASE THE PROOF NAMES: every lane declines. One round, then BeginRound
        // finds no live lane and the loop is over. No counter, no floor constant — the lanes simply
        // stop being claimants.
        [Fact]
        public void Termination_when_every_lane_declines_the_loop_ends_after_one_round()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>();
            for (int i = 0; i < 12; i++)
                lanes.Add(SelfLimiting("BasicTrainingBP", "BasicTraining-" + i));

            // Every lane is a cap-1 BasicTraining slot: `min(slotCap, offer)` with slotCap == 1.
            var plan = ConstraintLayer.Compose(1_000_000_000_000L, NoBudgetPressure(), lanes);
            var fill = new ConstraintLayer.Waterfill(1_000_000_000_000L, plan.Lanes, plan.SinkIndex);

            ConstraintLayer.FillSession session;
            var rounds = 0;
            long idle = 1_000_000_000_000L;
            var totals = new long[plan.Lanes.Length];
            while ((session = fill.BeginRound()) != null)
            {
                rounds++;
                Assert.True(rounds < 100, "the waterfill did not terminate");
                for (int i = 0; i < plan.Lanes.Length; i++)
                {
                    if (!fill.IsLive(i)) continue;
                    string skip;
                    var offer = session.Offer(fill.LaneForRound(i), out skip);
                    var take = LaneCapMath.BasicTrainingAllocation(1, offer);
                    totals[i] += take;
                    idle -= take;
                    session.Commit(take);
                    fill.Record(i, offer, take);
                }
                fill.EndRound(idle);
            }

            Assert.Equal(1, rounds);
            // ⚠ NO REGRESSION AT A CEILING: exactly one unit each, not zero and not two.
            Assert.All(totals, t => Assert.Equal(1L, t));
            Assert.Equal(1_000_000_000_000L - 12, idle);
        }

        // THE OTHER HALF OF THE PROOF: when lanes DO keep taking, the pool at least halves every
        // round (rule A says a survivor took more than half of what it was offered, and the last live
        // lane of a round is offered everything that is left). So the round count is bounded by
        // log2(pool) even in a state built to be as slow as the rule permits.
        [Fact]
        public void Termination_a_pool_that_keeps_being_taken_halves_every_round()
        {
            const long pool = 1_000_000_000_000L;
            var lanes = new List<ConstraintLayer.LaneSpec> { SelfLimiting("AugmentBP", "Augment-0") };
            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), lanes);
            var fill = new ConstraintLayer.Waterfill(pool, plan.Lanes, plan.SinkIndex);

            ConstraintLayer.FillSession session;
            var rounds = 0;
            long idle = pool, previous = pool;
            while ((session = fill.BeginRound()) != null)
            {
                rounds++;
                Assert.True(rounds <= 41, "log2(1e12) is 40 — the halving bound must hold");
                string skip;
                var offer = session.Offer(fill.LaneForRound(0), out skip);
                // The slowest survivor rule A allows: exactly one unit past half the offer.
                var take = offer / 2 + 1;
                idle -= take;
                session.Commit(take);
                fill.Record(0, offer, take);
                fill.EndRound(idle);

                Assert.True(idle <= previous / 2, $"round {rounds} did not halve the pool: {previous} -> {idle}");
                previous = idle;
            }

            // It terminates in TWO, not in forty: rule B ends it as soon as the take stops rising,
            // which under a shrinking pool is immediately. The halving bound is the guarantee, not
            // the expectation — and the assertion inside the loop is what proves the guarantee holds
            // for a lane taking the least rule A permits.
            Assert.Equal(2, rounds);
            Assert.True(idle <= pool / 4);
        }

        // ---- no regression at a ceiling, no cap breached ------------------------------------------

        // A CAP LANE STILL STOPS AT ITS CAP however many rounds run, and the ten cap-1 BasicTraining
        // lanes from the operator's own block still take exactly 1 each while a hungry lane behind
        // them drinks the rest.
        [Fact]
        public void A_capped_lane_stops_at_its_cap_and_a_cap_one_lane_still_takes_exactly_one()
        {
            var specs = new List<ConstraintLayer.LaneSpec>();
            foreach (var idx in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11 })
                specs.Add(Lane("BasicTrainingBP", 1, "BasicTraining-" + idx));
            specs.Add(Lane("AdvancedTrainingBP", 29_038_864_600L, "AdvancedTraining-0"));
            specs.Add(SelfLimiting("BestAug", "BestAug-0"));

            var r = Drive(LivePool, specs,
                new Dictionary<int, Func<long, long, long>> { { 11, Chunker(BestAugCostCeiling) } });

            // ⚠ EXACTLY ONE UNIT EACH, in every round: not starved, not spammed. This is the shape
            // the operator reported as the defect (`offered=100796380579 took=1`, ten times over) and
            // it is CORRECT — Rebirth.resetTraining() floors the cap at 1 and past the cap the bar
            // completes every tick anyway ([DECOMP] Rebirth.cs:652-655, OffenseTraining.cs:99-101).
            for (int i = 0; i < 10; i++)
                Assert.Equal(1L, r.Total[i]);
            // The known-capacity lane is funded to its cap and NOT past it, across every round.
            Assert.Equal(29_038_864_600L, r.Total[10]);
            // And the hungry lane absorbs what the eleven declined — all of it.
            Assert.Equal(1_684_923_061_725L, r.Total[11]);
            Assert.Equal(0, r.Idle);
            Assert.Equal(LivePool, r.Total.Sum());
        }

        // THE SINK IS NOT STARVED — the one way this change could make things WORSE, measured. Before
        // the reserve went in, the 62963s sixteen-lane block took Wandoos from 30.9% of the pool to
        // under 0.1% because the waterfill kept re-dividing the residue the sink collects. It now
        // banks one round's share, out of the loop's reach, and lands on a PEER share of the lanes
        // that are still claiming.
        [Fact]
        public void The_surplus_sink_keeps_a_peer_share_and_is_never_squeezed_out()
        {
            const long pool = 926_504_309_183L;
            var specs = new List<ConstraintLayer.LaneSpec>
            {
                SelfLimiting("AugmentBP", "Augment-0"),
                SelfLimiting("AugmentBP", "Augment-1"),
                Lane("AdvancedTrainingBP", 29_038_864_600L, "AdvancedTraining-0"),
                Sink(),
            };
            var costs = new Dictionary<int, Func<long, long, long>>
            {
                { 0, Chunker(1_000_000_000_000_000d) },
                { 1, Chunker(1_000_000_000_000_000d) },
            };

            var r = Drive(pool, specs, costs);

            Assert.Equal(29_038_864_600L, r.Total[2]);          // saturated, funded exactly
            Assert.True(r.SinkTotal > 0, "spec §8: there is always a destination that accepts everything");

            // THE TEXTBOOK WATERFILL ANSWER: three claimants share what the saturated lane left, and
            // the sink is one of the three. 299,138,615,690 / 299,143,178,917 / 299,183,649,976 out
            // of 897,465,444,583 — a third each, spread 0.015%.
            long claimable = pool - 29_038_864_600L;
            // Goldens moved a hair on 2026-08-18 when AppetiteProven rule B gained its
            // "absorbed essentially all of its offer" tolerance: an offer-limited lane now
            // survives one more round. The PROPERTY this test is about — a third each, spread
            // ~0.015% — is unchanged; only the last few digits moved, and upward.
            Assert.Equal(299_139_914_177L, r.Total[0]);
            Assert.Equal(299_144_477_402L, r.Total[1]);
            Assert.Equal(299_181_053_004L, r.SinkTotal);
            foreach (var a in new[] { r.Total[0], r.Total[1], r.SinkTotal })
                Assert.InRange(a, claimable / 3 - claimable / 1000, claimable / 3 + claimable / 1000);
            Assert.Equal(pool, r.Total.Sum() + r.SinkTotal);
        }

        // ⚠ THE COST, PINNED RATHER THAN LEFT IMPLICIT. The game's stair math is stateless within a
        // tick: round r+1 recomputes the lane's cost from its LEVEL, not from what round r gave it.
        // A lane whose cost lands between round r's offer and round r+1's offer therefore receives a
        // full cost on top of round r's chunk, and the game converts one level either way. The
        // overshoot is AT MOST ONE EXTRA CHUNK — rule A or rule B retires the lane immediately after
        // — and it is spent on resource that was otherwise 100% idle.
        [Fact]
        public void The_stateless_stair_can_overshoot_a_cost_once_and_only_once()
        {
            // Cost 489,588,932,476 against a round-1 offer of 244.85 B: k = 2, so it chunks and
            // survives. Round 2 offers it 1.46 T, k drops to 1, and it takes the WHOLE cost again.
            var r = Drive(LivePool, LiveBlock(), LiveTakes(Chunker(BestAugCostFloor)));

            Assert.Equal(2, r.Rounds);                      // one overshoot, then retired
            Assert.Equal(BestAugTake, r.Round1Take[0]);
            var round2 = r.Total[0] - r.Round1Take[0];
            Assert.Equal(489_589_924_657L, round2);

            // Cumulative 1.5x the one-level cost, so a third of the increment buys nothing — against
            // 1.46 T that was buying nothing AT ALL before this commit.
            Assert.InRange(r.Total[0] / BestAugCostFloor, 1.49, 1.51);
        }
    }
}
