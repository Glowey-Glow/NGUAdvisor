using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // AUGMENTATION'S CONDITIONAL SURPLUS SINK (amendment 29 §3.1) — the item c828f06 recorded as
    // still open and deliberately did not fix.
    //
    // THE STATE: `ChallengeOverlay.AutoTokens`, case "AUGMENTATION", is the only auto-profile energy
    // segment that emits no Wandoos token, so its ticks log `sink=absent`. While its ANCHOR is
    // seated that is harmless — the anchor takes the whole pool and the remainder is 0 — but if the
    // anchor drops out (BestAug refused by the No Augs challenge or by all seven pairs at target;
    // BR-30 dropped because BloodPlanner.BloodMatters() went false) every remaining lane is a
    // per-tick CEILING and ~99% of the pool has nowhere to go.
    //
    // ⚠ THE HYPOTHESIS THIS BRANCH WAS OPENED ON — "the sink may be OUTSIDE the fill's divisor, so
    // seating Wandoos might cost the augment nothing" — IS FALSE, and the first two tests below are
    // the disproof. The sink is counted as a seated DESTINATION by FillSession's constructor and
    // merely never handed to Offer (amendment 28 §2: "its slot is never spent, and that is what
    // leaves a remainder for it"), and Waterfill.BeginRound additionally BANKS it one round-share
    // per round. An unconditional WAN therefore costs the augment 2.02x, which is c828f06's measured
    // refusal reproduced here to five significant figures. Hence a CONDITIONAL sink.
    public class AugmentationSinkTests
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
                Label = "Wandoos-0",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
                SurplusSink = true,
            };

        // The shipped stair arithmetic as a take function — AugmentMath.AugCap with every bonus
        // neutralised so its `num1` IS `cost`. Verbatim from WaterfillTests; see that file's header
        // for the derivation.
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

        private sealed class Replay
        {
            public long[] Total;
            public long[] Round1Offer;
            public long Idle;          // what nothing in the lane list absorbed
            public long SinkOffer;     // what the sink is handed at the end (0 when there is none)
            public int Rounds;
        }

        // The executor loop, exactly as ConstraintLayerBridge.PerformSwap drives it. Same shape as
        // WaterfillTests.Drive; `takeFor[i]` is lane i's own Allocate().
        private static Replay Drive(long pool, IList<ConstraintLayer.LaneSpec> specs,
            IDictionary<int, Func<long, long, long>> takeFor)
        {
            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), specs);
            var n = plan.Lanes.Length;
            var r = new Replay { Total = new long[n], Round1Offer = new long[n], Idle = pool };

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
                    var offer = session.Offer(fill.LaneForRound(i), out skip);

                    Func<long, long, long> own;
                    long take = takeFor != null && takeFor.TryGetValue(i, out own)
                        ? own(Math.Min(offer, r.Idle), r.Idle)
                        : Math.Min(offer, r.Idle);

                    if (r.Rounds == 1)
                        r.Round1Offer[i] = offer;
                    r.Total[i] += take;
                    r.Idle -= take;
                    session.Commit(take);
                    fill.Record(i, offer, take);
                }
                fill.EndRound(r.Idle);
            }
            r.SinkOffer = plan.SinkSeated ? fill.SinkTotal : 0;
            return r;
        }

        private static Func<long, long, long> Chunker(double cost) =>
            (offer, idle) => StairTake(cost, offer, idle);

        private static Func<long, long, long> Ceiling(long ceiling) =>
            (offer, idle) => Math.Min(ceiling, offer);

        // ---- THE OPERATOR'S REAL AUGMENTATION BLOCKS, [AllocDbg] 8/7/2026-11:52 PM (9270s) --------

        private const long AugEnergyPool = 1_728_134_347_235L;
        private const long BestAugTake = 1_723_065_677_713L;      // 99.707% of the pool, verbatim

        // The six NGU lanes' `took=`, verbatim and in list order (NGU-0/1/3/4/5/6). Every one is a
        // per-tick CEILING — 79.9 M against a 246 B offer — which is what makes losing the anchor
        // catastrophic rather than merely suboptimal.
        private static readonly long[] AugNguTakes =
            { 79_927_785L, 79_960_929L, 80_015_172L, 80_253_137L, 80_308_030L, 4_668_204_469L };

        // ...and their `offered=`, verbatim, which the replay below reproduces to the unit.
        private static readonly long[] AugNguOffers =
        {
            246_882_124_202L, 296_242_563_485L, 370_283_214_124L,
            493_684_280_442L, 740_486_294_095L, 1_480_892_280_160L,
        };

        // ⚠ BestAug's ONE-LEVEL COST, FITTED TO THE BLOCK RATHER THAN ASSUMED. One (offer, take) pair
        // fixes only the CHUNK cost/k, exactly as WaterfillTests records — but this block pins the
        // whole SHAPE, because six ceiling lanes downstream turn BestAug's round-1 take into six
        // published `offered=` numbers. Sweeping the chunk with k = 82 lands on a cost that
        // reproduces BestAug's total AND all six NGU offers AND `remainder=0` with zero error, which
        // is seven independent constraints on one parameter. That is the model this file measures on.
        private const double BestAugCost = 20_240_970_346_288d;   // k = 82, chunk 246,841,101,784

        private const long AugMagicPool = 1_026_345_432_999L;
        private static readonly long[] AugMagicNguTakes =
            { 112_788_829L, 298_379_831L, 869_700_216L, 2_157_289_021L, 12_965_235_483L };

        // Wandoos' per-tick absorptive capacity, MEASURED on the same account 45 minutes later
        // ([AllocDbg] 8/8/2026-12:37 AM, 11959s): offered 931,713,984,650, took 798,091,953,267 with
        // the surfaced reason "beyond the sink's per-tick absorptive capacity". Magic the same tick:
        // offered 428,349,410,856, took 391,237,321,191.
        private const long WandoosEnergyCapacity = 798_091_953_267L;
        private const long WandoosMagicCapacity = 391_237_321_191L;

        // The AUGMENTATION energy membership. `sink` is the ONLY difference between the before- and
        // after-pictures: the token list, the lane order and every lane's arithmetic are identical.
        private static List<ConstraintLayer.LaneSpec> EnergyBlock(bool anchor, bool sink)
        {
            var specs = new List<ConstraintLayer.LaneSpec>();
            if (anchor) specs.Add(SelfLimiting("BestAug", "BestAug-0"));
            foreach (var id in new[] { 0, 1, 3, 4, 5, 6 })
                specs.Add(SelfLimiting("NGUBP", "NGU-" + id));
            if (sink) specs.Add(Sink());
            return specs;
        }

        private static Dictionary<int, Func<long, long, long>> EnergyTakes(bool anchor)
        {
            var map = new Dictionary<int, Func<long, long, long>>();
            int off = 0;
            if (anchor) { map[0] = Chunker(BestAugCost); off = 1; }
            for (int i = 0; i < AugNguTakes.Length; i++)
                map[i + off] = Ceiling(AugNguTakes[i]);
            return map;
        }

        private static List<ConstraintLayer.LaneSpec> MagicBlock(bool anchor, bool sink)
        {
            var specs = new List<ConstraintLayer.LaneSpec>();
            foreach (var id in new[] { 0, 1, 2, 3, 4 })
                specs.Add(SelfLimiting("NGUBP", "NGU-" + id));
            if (anchor) specs.Add(SelfLimiting("BR", "BR-30"));
            if (sink) specs.Add(Sink());
            return specs;
        }

        private static Dictionary<int, Func<long, long, long>> MagicTakes()
        {
            var map = new Dictionary<int, Func<long, long, long>>();
            for (int i = 0; i < AugMagicNguTakes.Length; i++)
                map[i] = Ceiling(AugMagicNguTakes[i]);
            return map;
        }

        // ---- (1) IS THE SINK INSIDE THE DIVISOR? SETTLED, FROM THE OPERATOR'S OWN BLOCK ------------

        // ⚠ THE DECISIVE QUESTION, AND THE ANSWER IS *INSIDE*. If the sink were excluded from the
        // divisor and handed only the leftover, seating it would be free and this whole file would
        // be one unconditional token. It is not.
        //
        // [AllocDbg] 8/8/2026-12:37 AM (11959s), the NGU+AT block: `Energy pool=1758030099891
        // lanes=15 seated=15`, Wandoos-0 logged `seated [sink]`, and the FIRST lane in the list —
        // TimeMachine-0 — logged `offered=117202006659`.
        //
        //     1,758,030,099,891 / 15 = 117,202,006,659   ← the sink IS one of the fifteen
        //     1,758,030,099,891 / 14 = 125,573,578,563   ← what the log would say if it were not
        //
        // FillSession's constructor counts every seated non-NoAllocation lane and does not exclude
        // SurplusSink; Offer is what skips it, which spends no slot. The two are different things and
        // the log settles which one is happening.
        [Fact]
        public void The_surplus_sink_is_counted_inside_the_fill_divisor()
        {
            const long pool = 1_758_030_099_891L;

            // Fifteen lanes, the second of which is the sink, in the live block's order.
            var specs = new List<ConstraintLayer.LaneSpec> { SelfLimiting("TimeMachineBP", "TimeMachine-0"), Sink() };
            for (int i = 0; i < 13; i++)
                specs.Add(SelfLimiting("NGUBP", "NGU-" + i));

            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), specs);
            Assert.Equal(15, plan.Lanes.Length);
            Assert.True(plan.SinkSeated);
            Assert.Equal(1, plan.SinkIndex);

            var session = new ConstraintLayer.FillSession(pool, plan.Lanes);
            Assert.Equal(15, session.LanesLeft);          // the sink is one of them

            string skip;
            var first = session.Offer(plan.Lanes[0], out skip);
            Assert.Equal(117_202_006_659L, first);        // the log's `offered=`, to the unit
            Assert.NotEqual(125_573_578_563L, first);     // what an excluded sink would have given

            // And the same set with the sink REMOVED offers the first lane the /14 number — so the
            // divisor really is what moved, not some other term.
            var noSink = new List<ConstraintLayer.LaneSpec>(specs);
            noSink.RemoveAt(1);
            var bare = ConstraintLayer.Compose(pool, NoBudgetPressure(), noSink);
            Assert.Equal(-1, bare.SinkIndex);
            var bareSession = new ConstraintLayer.FillSession(pool, bare.Lanes);
            Assert.Equal(14, bareSession.LanesLeft);
            Assert.Equal(125_573_578_563L, bareSession.Offer(bare.Lanes[0], out skip));
        }

        // ---- (2) THE REPLAY, AND WHY (a) WAS REFUSED -----------------------------------------------

        // THE BEFORE-PICTURE, reproduced to the unit: BestAug's `took=`, all six NGU `offered=`, and
        // `remainder=0`. This is what the model has to earn before any counterfactual it produces
        // means anything.
        [Fact]
        public void The_live_augmentation_block_is_reproduced_to_the_unit()
        {
            var r = Drive(AugEnergyPool, EnergyBlock(anchor: true, sink: false), EnergyTakes(anchor: true));

            Assert.Equal(BestAugTake, r.Total[0]);
            for (int i = 0; i < AugNguOffers.Length; i++)
                Assert.Equal(AugNguOffers[i], r.Round1Offer[i + 1]);
            for (int i = 0; i < AugNguTakes.Length; i++)
                Assert.Equal(AugNguTakes[i], r.Total[i + 1]);

            Assert.Equal(0, r.Idle);                       // the block's `remainder=0`
            Assert.Equal(AugEnergyPool, r.Total.Sum());
            Assert.InRange(r.Total[0] * 100.0 / AugEnergyPool, 99.70, 99.71);
        }

        // ⚠ OPTION (a) — "seat WAN unconditionally, it is free" — MEASURED AND REFUSED, on the same
        // block, with the ONLY difference being the sink lane. c828f06 reported 49.853% for this
        // counterfactual under an infinite-appetite BestAug; the fitted model says 49.448%. The
        // refusal is reproduced, not taken on trust.
        [Fact]
        public void Seating_the_sink_unconditionally_halves_the_augment()
        {
            var before = Drive(AugEnergyPool, EnergyBlock(anchor: true, sink: false), EnergyTakes(anchor: true));
            var after = Drive(AugEnergyPool, EnergyBlock(anchor: true, sink: true), EnergyTakes(anchor: true));

            Assert.Equal(1_723_065_677_713L, before.Total[0]);
            Assert.Equal(854_524_124_048L, after.Total[0]);

            // A 2.02x fall in the lane this segment exists to fund, and it is the DIVISOR plus the
            // per-round bank that did it — the sink is offered nothing until the loop is over.
            Assert.True(after.Total[0] < before.Total[0] / 2,
                "an unconditional sink costs the augment more than half its take");
            Assert.InRange(before.Total[0] * 1.0 / after.Total[0], 2.01, 2.03);
            Assert.InRange(after.Total[0] * 100.0 / AugEnergyPool, 49.4, 49.5);

            // And it makes the block IDLE where it idled nothing: Wandoos cannot absorb the whole
            // 868 B it is handed (WandoosEnergyCapacity, measured), so the excess is left for the
            // wish share pass exactly as ConstraintLayerBridge documents.
            Assert.Equal(868_541_553_665L, after.SinkOffer);
            Assert.Equal(0, before.Idle);
            Assert.True(after.SinkOffer - WandoosEnergyCapacity > 70_000_000_000L,
                "the unconditional sink strands resource this block did not strand before");
        }

        // ---- (3) BEFORE vs AFTER UNDER THE CHANGE THAT SHIPPED -------------------------------------

        // ⚠ THE HEADLINE, AND IT IS AN EQUALITY: on the operator's real block the decision says NO,
        // the membership is unchanged, and BestAug's take is IDENTICAL — not "close", not "within
        // tolerance". That is a structural guarantee rather than a measured one, because the rule's
        // second clause is the anchor's presence and BestAug is present in every tick of every
        // 2026-08-07/08 log.
        [Fact]
        public void BestAug_take_is_unchanged_on_the_real_block_because_the_rule_declines_to_seat()
        {
            var decision = ConstraintLayer.AnchorAbsentSink("AUGMENTATION", energy: true,
                anchorSeated: true, sinkSeated: false);
            Assert.False(decision.Seat);
            Assert.Null(decision.Reason);

            var before = Drive(AugEnergyPool, EnergyBlock(anchor: true, sink: false), EnergyTakes(anchor: true));
            var after = Drive(AugEnergyPool, EnergyBlock(anchor: true, sink: decision.Seat), EnergyTakes(anchor: true));

            Assert.Equal(before.Total[0], after.Total[0]);
            Assert.Equal(BestAugTake, after.Total[0]);
            Assert.Equal(before.Idle, after.Idle);
            Assert.Equal(before.Rounds, after.Rounds);
            Assert.Equal(before.Total.Length, after.Total.Length);
            for (int i = 0; i < before.Total.Length; i++)
                Assert.Equal(before.Total[i], after.Total[i]);
        }

        // Same for magic while the ritual is funding: BR-30 is the anchor, it is seated, no sink.
        [Fact]
        public void The_magic_block_is_unchanged_while_the_ritual_is_funding()
        {
            var decision = ConstraintLayer.AnchorAbsentSink("AUGMENTATION", energy: false,
                anchorSeated: true, sinkSeated: false);
            Assert.False(decision.Seat);

            var before = Drive(AugMagicPool, MagicBlock(anchor: true, sink: false), MagicTakes());
            var after = Drive(AugMagicPool, MagicBlock(anchor: true, sink: decision.Seat), MagicTakes());
            for (int i = 0; i < before.Total.Length; i++)
                Assert.Equal(before.Total[i], after.Total[i]);
        }

        // ---- (4) THE ANCHOR-DROPS-OUT CASE, WHICH IS WHAT THIS BUYS --------------------------------

        // ENERGY: BestAug refused — the No Augs challenge, or all seven pairs at target — with
        // ALLBT's twelve slots at cap. Six NGU ceilings absorb 5,068,669,522 of a 1,728,134,347,235
        // pool and the rest has nowhere to go.
        [Fact]
        public void Energy_anchor_drops_out_the_pool_goes_from_99_percent_idle_to_a_funded_sink()
        {
            var decision = ConstraintLayer.AnchorAbsentSink("AUGMENTATION", energy: true,
                anchorSeated: false, sinkSeated: false);
            Assert.True(decision.Seat);
            Assert.Contains("BestAug", decision.Reason);
            Assert.Contains("energy", decision.Reason);

            var before = Drive(AugEnergyPool, EnergyBlock(anchor: false, sink: false), EnergyTakes(anchor: false));
            var after = Drive(AugEnergyPool, EnergyBlock(anchor: false, sink: decision.Seat), EnergyTakes(anchor: false));

            // BEFORE: 99.707% idle with `sink=absent`, and it is the SAME 1,723,065,677,713 BestAug
            // used to take — the anchor's whole take becomes waste.
            Assert.Equal(5_068_669_522L, before.Total.Sum());
            Assert.Equal(1_723_065_677_713L, before.Idle);
            Assert.Equal(0, before.SinkOffer);
            Assert.InRange(before.Idle * 100.0 / AugEnergyPool, 99.70, 99.71);

            // AFTER: the ceiling lanes are untouched to the unit — this does not take from them —
            // and the entire remainder now has a destination.
            for (int i = 0; i < AugNguTakes.Length; i++)
                Assert.Equal(before.Total[i], after.Total[i]);
            Assert.Equal(1_723_065_677_713L, after.SinkOffer);
            Assert.Equal(0, after.Idle - after.SinkOffer);

            // ⚠ WHAT ACTUALLY LANDS, and it is bounded by the GAME, not by this rule. Wandoos absorbs
            // at most its per-tick capacity; ConstraintLayerBridge leaves the excess idle ON PURPOSE
            // for the wish share pass. At the capacity measured on this account that is 798.09 B
            // converted out of a pool that was converting NOTHING — idle 99.707% -> 53.53%.
            long landed = Math.Min(WandoosEnergyCapacity, after.SinkOffer);
            long stranded = after.SinkOffer - landed;
            Assert.Equal(798_091_953_267L, landed);
            Assert.Equal(924_973_724_446L, stranded);
            Assert.InRange(stranded * 100.0 / AugEnergyPool, 53.5, 53.6);
            Assert.True(stranded < before.Idle / 2 + before.Idle / 10,
                "the idle pool must fall by well over a third");

            // With an unbounded sink — the shape the rule itself produces, before the game's own
            // per-tick cap applies — idle is ZERO.
            Assert.Equal(AugEnergyPool, after.Total.Sum() + after.SinkOffer);
        }

        // MAGIC: BloodPlanner.BloodMatters() went false, so no BR-30 token was emitted at all. Five
        // NGU ceilings absorb 16,403,393,380 of a 1,026,345,432,999 pool.
        [Fact]
        public void Magic_anchor_drops_out_the_pool_goes_from_98_percent_idle_to_a_funded_sink()
        {
            var decision = ConstraintLayer.AnchorAbsentSink("AUGMENTATION", energy: false,
                anchorSeated: false, sinkSeated: false);
            Assert.True(decision.Seat);
            Assert.Contains("BR", decision.Reason);
            Assert.Contains("magic", decision.Reason);

            var before = Drive(AugMagicPool, MagicBlock(anchor: false, sink: false), MagicTakes());
            var after = Drive(AugMagicPool, MagicBlock(anchor: false, sink: decision.Seat), MagicTakes());

            Assert.Equal(16_403_393_380L, before.Total.Sum());
            Assert.Equal(1_009_942_039_619L, before.Idle);
            Assert.Equal(0, before.SinkOffer);
            Assert.InRange(before.Idle * 100.0 / AugMagicPool, 98.40, 98.41);

            for (int i = 0; i < AugMagicNguTakes.Length; i++)
                Assert.Equal(before.Total[i], after.Total[i]);
            Assert.Equal(1_009_942_039_619L, after.SinkOffer);

            long landed = Math.Min(WandoosMagicCapacity, after.SinkOffer);
            Assert.Equal(391_237_321_191L, landed);
            Assert.Equal(618_704_718_428L, after.SinkOffer - landed);
            Assert.Equal(AugMagicPool, after.Total.Sum() + after.SinkOffer);
        }

        // ---- (5) THE NEGATIVE CONTROL --------------------------------------------------------------

        // ⚠ A SWEEP THAT CANNOT FAIL PROVES NOTHING. Two cases the rule must NOT move, driven through
        // the same code as the two it must, with the discriminating assertion written out.
        //
        //   · THE SEGMENT IS NOT ON THE ALLOWLIST. NGU+AT has the identical shape — anchor absent,
        //     no sink in the specs handed to the predicate — and gets NOTHING, because it already
        //     emits its own WAN token and generalising the table was refused.
        //   · A SINK IS ALREADY SEATED. AUGMENTATION with a Wandoos already in the membership does
        //     not get a second one; a duplicate would be refused by Compose anyway
        //     ("duplicate surplus sink"), and a refused lane still inflates nothing only because it
        //     never seats — this stops the question being asked at all.
        [Fact]
        public void Negative_control_the_rule_declines_where_it_must_and_the_test_can_tell()
        {
            // MUST NOT MOVE — segment off the allowlist, even with the anchor absent.
            Assert.Null(ConstraintLayer.SinkAnchorFor("NGU+AT", energy: true));
            Assert.False(ConstraintLayer.AnchorAbsentSink("NGU+AT", true, anchorSeated: false, sinkSeated: false).Seat);
            Assert.False(ConstraintLayer.AnchorAbsentSink("EVIL NGU", true, false, false).Seat);
            Assert.False(ConstraintLayer.AnchorAbsentSink("NGU MARATHON", false, false, false).Seat);
            Assert.False(ConstraintLayer.AnchorAbsentSink("TM HOUR", true, false, false).Seat);
            Assert.False(ConstraintLayer.AnchorAbsentSink(null, true, false, false).Seat);
            Assert.False(ConstraintLayer.AnchorAbsentSink("", true, false, false).Seat);

            // MUST NOT MOVE — a sink is already there.
            Assert.False(ConstraintLayer.AnchorAbsentSink("AUGMENTATION", true, anchorSeated: false, sinkSeated: true).Seat);

            // MUST MOVE — the one state the rule exists for.
            Assert.True(ConstraintLayer.AnchorAbsentSink("AUGMENTATION", true, anchorSeated: false, sinkSeated: false).Seat);
            Assert.True(ConstraintLayer.AnchorAbsentSink("AUGMENTATION", false, anchorSeated: false, sinkSeated: false).Seat);

            // ...and the allocation consequence of that difference, from the same driver on the same
            // pool: a declined seat leaves 1,723,065,677,713 with nowhere to go, a granted one gives
            // all of it a destination. If the rule silently stopped firing, this is the assertion
            // that would go red.
            var declined = Drive(AugEnergyPool, EnergyBlock(anchor: false, sink: false), EnergyTakes(anchor: false));
            var granted = Drive(AugEnergyPool, EnergyBlock(anchor: false, sink: true), EnergyTakes(anchor: false));
            Assert.Equal(0, declined.SinkOffer);
            Assert.Equal(1_723_065_677_713L, granted.SinkOffer);
            Assert.True(declined.SinkOffer == 0 && granted.SinkOffer > 0,
                "the test must be able to tell a seated sink from an absent one");
        }

        // ---- (6) STABILITY: THE CONDITION CANNOT FLAP ----------------------------------------------

        // ⚠ THE OPERATOR HAS BEEN BITTEN BY A SEGMENT FLIP-FLOP BEFORE (audit 31: membership decided
        // before a global reclaim, so the allocation moved the input to its own decision). THE PROOF
        // HERE IS STRUCTURAL: `AnchorAbsentSink` is a pure function of a segment name and two
        // MEMBERSHIP facts, and neither fact is written by the fill.
        //
        // This test pins the two halves of that:
        //   (a) the function is total and deterministic over its whole input space — same inputs,
        //       same answer, every time, so nothing inside it can oscillate;
        //   (b) a run of ticks in which only the SINK'S OWN take varies never changes the answer,
        //       because the sink's take is not an input. Under a feedback loop this is where the
        //       alternation would appear.
        [Fact]
        public void The_seating_rule_is_a_pure_function_of_membership_and_cannot_oscillate()
        {
            // (a) totality + determinism over the full 2x2x2 space, twice.
            foreach (var seg in new[] { "AUGMENTATION", "NGU+AT", "TM HOUR", "unknown", "" })
                foreach (var energy in new[] { true, false })
                    foreach (var anchor in new[] { true, false })
                        foreach (var sink in new[] { true, false })
                        {
                            var a = ConstraintLayer.AnchorAbsentSink(seg, energy, anchor, sink);
                            var b = ConstraintLayer.AnchorAbsentSink(seg, energy, anchor, sink);
                            Assert.Equal(a.Seat, b.Seat);
                            Assert.Equal(a.Reason, b.Reason);
                            // Seat implies a surfaced reason, and only AUGMENTATION can seat.
                            Assert.Equal(a.Seat, a.Reason != null);
                            if (a.Seat)
                            {
                                Assert.Equal("AUGMENTATION", seg);
                                Assert.False(anchor);
                                Assert.False(sink);
                            }
                        }

            // (b) forty consecutive ticks in the anchor-absent state. The sink is seated on tick 1
            // and absorbs a different amount every tick (the pool moves); the decision is asked
            // again each time with the membership facts the tick actually has — anchor still absent,
            // and the sink present ONLY because this rule put it there, which is the shape a
            // feedback loop would need. It never flips.
            bool sinkPresentLastTick = false;
            var answers = new List<bool>();
            for (int tick = 0; tick < 40; tick++)
            {
                // The membership is rebuilt from tokens every tick (spec §4.5 forbids caching), so
                // the rule always sees `sinkSeated: false` from the token list — the previous tick's
                // seat is not an input. That is the no-feedback property, written as code.
                var d = ConstraintLayer.AnchorAbsentSink("AUGMENTATION", energy: true,
                    anchorSeated: false, sinkSeated: false);
                answers.Add(d.Seat);

                var r = Drive(AugEnergyPool - tick * 1_000_000_000L,
                    EnergyBlock(anchor: false, sink: d.Seat), EnergyTakes(anchor: false));
                Assert.True(r.SinkOffer > 0);          // it absorbed, and the amount differs per tick
                sinkPresentLastTick = d.Seat;
            }
            Assert.True(sinkPresentLastTick);
            Assert.All(answers, a => Assert.True(a));
            Assert.Single(answers.Distinct());          // ONE value across forty ticks — no alternation

            // And the mirror: forty ticks with the anchor back. Also one value, also no alternation.
            var back = new List<bool>();
            for (int tick = 0; tick < 40; tick++)
                back.Add(ConstraintLayer.AnchorAbsentSink("AUGMENTATION", true, anchorSeated: true, sinkSeated: false).Seat);
            Assert.Single(back.Distinct());
            Assert.All(back, a => Assert.False(a));
        }

        // The table itself, pinned: the anchors are the two lanes AutoTokens' AUGMENTATION case
        // actually emits, named exactly as ConstraintLayerBridge.BuildSpec keys them
        // (`bp.GetType().Name`). A rename on either side that did not update the other would leave
        // the anchor permanently "absent" and seat the sink in every tick — the unconditional case
        // this file exists to refuse — so the names are asserted rather than assumed.
        [Fact]
        public void The_anchor_table_names_the_lanes_the_segment_actually_emits()
        {
            Assert.Single(ConstraintLayer.AnchorAbsentSinkTable);
            var row = ConstraintLayer.AnchorAbsentSinkTable[0];
            Assert.Equal("AUGMENTATION", row.Segment);
            Assert.Equal("BestAug", row.EnergyAnchor);
            Assert.Equal("BR", row.MagicAnchor);
            Assert.False(string.IsNullOrEmpty(row.Why));

            Assert.Equal("BestAug", ConstraintLayer.SinkAnchorFor("AUGMENTATION", energy: true));
            Assert.Equal("BR", ConstraintLayer.SinkAnchorFor("AUGMENTATION", energy: false));

            // Both names are lanes the layer already knows: BestAug carries a re-offer row, and BR
            // carries the NOT-re-offerable row that rolled the waterfill back out of the live game.
            Assert.True(ConstraintLayer.ReofferableLane("BestAug"));
            Assert.False(ConstraintLayer.ReofferableLane("BR"));
            Assert.Contains(ConstraintLayer.ReofferTable, r => r.Lane == "BestAug");
            Assert.Contains(ConstraintLayer.ReofferTable, r => r.Lane == "BR");

            // The sink the rule seats is Wandoos, and Wandoos is the ONLY lane this layer will ever
            // treat as one (spec §8) — ConstraintLayerBridge sets SurplusSink = `bp is WandoosBP`.
            Assert.Contains(ConstraintLayer.ReofferTable, r => r.Lane == "WandoosBP");
        }
    }
}
