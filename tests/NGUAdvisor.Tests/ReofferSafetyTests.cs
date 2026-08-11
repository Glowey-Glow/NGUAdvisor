using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // RE-OFFER SAFETY (amendment 29 §2) — the defect that rolled the waterfill back out of the
    // operator's live game.
    //
    // The waterfill (3546670) worked on energy exactly as designed: BestAug-0 went from 14.285% to
    // 99.707% of a 1.71 T pool and the remainder went to zero. IT BROKE MAGIC. A second offer to BR
    // does not waste — IT UNDOES, because BR.CastRituals re-walks every unlocked ritual against the
    // NEW, smaller budget, prices every one of them as unable to finish, and takes the SkipAndDrain
    // branch, which calls [DECOMP] BloodMagicController.removeAllMagic() on each.
    //
    // ⚠ THE PREMISE THE WATERFILL WAS WRITTEN ON — "the algorithm discovers appetite by offering and
    // measuring take" — IS FALSE FOR BR. AppetiteProven's rules A and B are sound theorems about the
    // game's STAIR ARITHMETIC, and rule A passed BR correctly: it took 99.99996% of its offer. What
    // no arithmetic on (offer, take) can see is that calling that lane's Allocate() a SECOND TIME
    // RUNS A DIFFERENT PROGRAM. Appetite is not permission.
    //
    // Everything below is driven by the SHIPPED cores — RitualMath.RitualDecide, .ProgressPerTick,
    // .TimeLeft, .MaxAllocationFor — never by a model of them, and against the operator's own
    // [AllocDbg] numbers.
    public class ReofferSafetyTests
    {
        private static BudgetPass.BudgetState NoBudgetPressure() =>
            new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 };

        private static ConstraintLayer.LaneSpec SelfLimiting(string name, string label)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
            };

        private static ConstraintLayer.LaneSpec Sink(string name = "WandoosBP")
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = "CAPWandoos-0",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
                SurplusSink = true,
            };

        // ---- the executor, with a pool a lane can PUSH BACK INTO -----------------------------------

        // WaterfillTests' Drive cannot express this defect: it computes `take` from a pure function
        // and subtracts it, so a lane can only ever consume. The live executor does not work that
        // way — it reads the pool, calls Allocate(), and reads the pool AGAIN
        // (ConstraintLayerBridge.cs:197-201) — so a lane that hands resource back moves the second
        // read UP. This driver is that loop byte for byte, negative-take clamp included.
        private sealed class Pool { public long Idle; }

        // A lane's live Allocate(): it is handed the budget the fill offered and the live pool, and
        // it mutates the pool however it likes — including upward.
        private delegate void LaneAllocate(long budget, Pool pool);

        private sealed class Replay
        {
            public long[] Offered;      // CUMULATIVE, as the bridge's `offers[i] += offer` is
            public long[] Took;         // CUMULATIVE, as `takes[i] += take` is
            public long Remainder;      // the executor's end-of-fill `long remainder = Idle(c, type)`
            public int Rounds;
        }

        private static Replay Drive(long pool, IList<ConstraintLayer.LaneSpec> specs,
            IDictionary<int, LaneAllocate> allocate)
        {
            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), specs);
            var n = plan.Lanes.Length;
            var r = new Replay { Offered = new long[n], Took = new long[n] };
            var p = new Pool { Idle = pool };

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
                    long offer = session.Offer(fill.LaneForRound(i), out skip);
                    if (offer <= 0)
                    {
                        fill.Record(i, 0, 0);
                        continue;
                    }

                    long before = p.Idle;
                    LaneAllocate own;
                    if (allocate != null && allocate.TryGetValue(i, out own))
                        own(Math.Min(offer, before), p);
                    else
                        p.Idle -= Math.Min(offer, before);

                    long take = before - p.Idle;
                    if (take < 0) take = 0;          // ConstraintLayerBridge.cs:201
                    r.Offered[i] += offer;
                    r.Took[i] += take;
                    session.Commit(take);
                    fill.Record(i, offer, take);
                }
                fill.EndRound(p.Idle);               // the GAME is the authority on what is left
            }

            r.Remainder = p.Idle;
            return r;
        }

        // A lane at a CEILING — absorbs at most `ceiling` however much it is offered, and never
        // returns anything. This is what the five magic NGU rows are in the block below.
        private static LaneAllocate Ceiling(long ceiling) =>
            (budget, pool) =>
            {
                var take = Math.Min(ceiling, Math.Min(budget, pool.Idle));
                if (take > 0) pool.Idle -= take;
            };

        // ---- BR, as BR.CastRituals actually behaves -------------------------------------------------

        // ONE unlocked ritual, walked through BR.CastRituals' verbatim ladder. The gold gate and the
        // rebirth deadline are held open so the ONLY thing deciding Fund-vs-SkipAndDrain is the
        // duration test — which is the whole point: `RitualTimeLeft(id, allocationLeft)` prices the
        // ritual AT THE BUDGET IT WAS OFFERED, and RitualMath.ProgressPerTick's rate is linear in
        // that budget, so tLeft scales as 1/budget. A round-2 residue budget therefore makes ANY
        // ritual too slow, and BR.cs:92-98 drains it.
        private sealed class BloodLane
        {
            private const double DividerScale = 50000.0;   // normal/evil, RitualMath's own constant
            private readonly double _speedDivider;
            private readonly long _capValue;
            private readonly int _secondsToRun;
            private long _placed;        // bloodMagic.ritual[i].magic, exactly

            public BloodLane(long capValue, int secondsToRun, double speedDivider)
            {
                _capValue = capValue;
                _secondsToRun = secondsToRun;
                _speedDivider = speedDivider;
            }

            public long Placed => _placed;

            public void Allocate(long budget, Pool pool)
            {
                long allocationLeft = budget;
                if (allocationLeft <= 0) return;       // BR.cs:69
                if (pool.Idle == 0) return;            // BR.cs:71

                var state = new RitualMath.RitualState
                {
                    Id = 0,
                    Unlocked = true,
                    GoldCost = 0.0,                    // gold gate held open
                    Progress = 0.0,
                };

                long left = allocationLeft;
                var action = RitualMath.RitualDecide(state, gold: double.MaxValue,
                    secondsToRun: _secondsToRun, nowSec: 0.0, rebirthDeadlineSec: -1,
                    timeLeftSec: () => TimeLeft(left));

                if (action != RitualMath.RitualAction.Fund)
                {
                    // BR.cs:94-95 -> [DECOMP] BloodMagicController.removeAllMagic() :230-236,
                    // `idleMagic += magic; ritual[id].magic -= magic`.
                    if (_placed > 0)
                    {
                        pool.Idle += _placed;
                        _placed = 0;
                    }
                    return;
                }

                long cap = RitualMath.MaxAllocationFor(_capValue, allocationLeft);
                // [DECOMP] BloodMagicController.add() -> addMagic(:130-153): clamped to idle, and
                // `ritual[id].magic += num` — ACCUMULATING, never a resize.
                if (cap > pool.Idle) cap = pool.Idle;
                if (cap <= 0) return;
                _placed += cap;
                pool.Idle -= cap;
            }

            private double TimeLeft(long remaining) =>
                RitualMath.TimeLeft(0.0, RitualMath.ProgressPerTick(new RitualMath.RitualRateInputs
                {
                    Remaining = remaining,
                    TotalMagicPower = 1.0,
                    DividerScale = DividerScale,
                    SpeedDivider = _speedDivider,
                    Sadistic = false,
                    SadisticDivider = 1.0,
                    SpeedBonus = 1.0,
                }));
        }

        // ---- THE OPERATOR'S TWO MAGIC BLOCKS, VERBATIM ---------------------------------------------
        //
        // [AllocDbg] 8/7/2026, the SAME six-lane AUGMENTATION magic membership 150 seconds apart —
        // NGU-0,1,2,3,4 then BR-30, no surplus sink. The first is the shipped single pass, the second
        // is the waterfill. Identical shape, indistinguishable pool, and the take barely moved:
        //
        //   (9120s) pool=1026305260498  BR-30 offered=1009899134963 took=1009899134562  remainder=401
        //   (9270s) pool=1026345432999  BR-30 offered=1009942040007 took=1009942039231  remainder=1009942039619
        //
        // ⚠ 1,009,942,039,619 = 1,009,942,039,231 + 388. The remainder IS the take, plus the 388 the
        // round left over. Nothing was allocated somewhere else; the magic came back.

        private const long Pool9270 = 1_026_345_432_999L;
        private const long Pool9120 = 1_026_305_260_498L;

        // `took=` for NGU-0,1,2,3,4 at each sample. Every one of them fails rule A by three orders of
        // magnitude, so they are retired in round 1 either way and only BR reaches a second round.
        private static readonly long[] Ngu9270 =
            { 112_788_829L, 298_379_831L, 869_700_216L, 2_157_289_021L, 12_965_235_483L };
        private static readonly long[] Ngu9120 =
            { 112_818_992L, 298_455_708L, 869_907_334L, 2_157_744_892L, 12_967_198_609L };

        // What BR's `took=` fixes: MaxAllocationFor returns capValue outright whenever the budget
        // exceeds it (RitualMath.cs:132-138's first branch), so ONE ritual at this capValue
        // reproduces the live take exactly, and the 388 / 401 residue with it.
        private const long BrCap9270 = 1_009_942_039_231L;
        private const long BrCap9120 = 1_009_899_134_562L;

        // Any divider in [1.2e-2 .. 3.0e10] separates the two budgets; 1e6 is a plausible ritual
        // speed divider and sits in the middle of it. tLeft = 1000 x divider / budget, so:
        //   budget 1,009,942,039,619 -> 9.9e-4 s   (<= 30, FUND)
        //   budget             388   -> 2.58e6 s   (>  30, SKIP AND DRAIN)
        private const double BrDivider = 1_000_000.0;
        private const int BrSecondsToRun = 30;

        private static List<ConstraintLayer.LaneSpec> MagicBlock()
        {
            var specs = new List<ConstraintLayer.LaneSpec>();
            foreach (var id in new[] { 0, 1, 2, 3, 4 })
                specs.Add(SelfLimiting("NGUBP", "NGU-" + id));
            specs.Add(SelfLimiting("BR", "BR-30"));
            return specs;
        }

        private static Dictionary<int, LaneAllocate> MagicTakes(long[] nguTakes, BloodLane br)
        {
            var map = new Dictionary<int, LaneAllocate>();
            for (int i = 0; i < nguTakes.Length; i++)
                map[i] = Ceiling(nguTakes[i]);
            map[nguTakes.Length] = br.Allocate;
            return map;
        }

        // ============================================================================================
        // THE DEFECT, AND THE FIX
        // ============================================================================================

        // ⚠ THIS IS THE REGRESSION TEST, AND IT FAILED BEFORE THE RE-OFFER GATE WENT IN. Without the
        // gate this block runs TWO rounds and comes out at remainder=1,009,942,039,619 — the operator's
        // measured number, to the unit. With it, BR is offered ONCE and the remainder is the 388 the
        // round left behind.
        [Fact]
        public void A_second_offer_to_BR_no_longer_withdraws_the_magic_the_first_one_placed()
        {
            var br = new BloodLane(BrCap9270, BrSecondsToRun, BrDivider);
            var r = Drive(Pool9270, MagicBlock(), MagicTakes(Ngu9270, br));

            // ROUND 1 IS UNCHANGED — every `offered=` and every `took=` in the live block, verbatim.
            Assert.Equal(171_057_572_166L, r.Offered[0]);
            Assert.Equal(205_246_528_834L, r.Offered[1]);
            Assert.Equal(256_483_566_084L, r.Offered[2]);
            Assert.Equal(341_688_188_041L, r.Offered[3]);
            Assert.Equal(511_453_637_551L, r.Offered[4]);
            for (int i = 0; i < Ngu9270.Length; i++)
                Assert.Equal(Ngu9270[i], r.Took[i]);
            Assert.Equal(1_009_942_039_231L, r.Took[5]);

            // ⚠ AND THE LANE IS OFFERED EXACTLY ONCE. Before the gate this read 1,009,942,040,007 —
            // round 1's 1,009,942,039,619 PLUS a second offer of 388, which is the live block's own
            // `offered=` and the fingerprint of the extra round.
            Assert.Equal(1_009_942_039_619L, r.Offered[5]);
            Assert.Equal(1, r.Rounds);

            // THE WHOLE POINT: the magic stayed where round 1 put it.
            Assert.Equal(1_009_942_039_231L, br.Placed);
            Assert.Equal(388L, r.Remainder);
            Assert.Equal(Pool9270, r.Took.Sum() + r.Remainder);

            // 98.402% of the pool idle becomes 0.0000000378% of it.
            Assert.True(r.Remainder * 1000L < Pool9270 / 1_000_000L,
                "the withdrawn 1,009,942,039,619 must not come back as a remainder");
        }

        // ⚠ THE NEGATIVE CONTROL, and without it the test above proves nothing. Same block, same
        // driver, ONE thing changed — BR declared re-offerable — and the operator's defect reappears
        // to the unit: two rounds, cumulative offered 1,009,942,040,007, remainder 1,009,942,039,619.
        //
        // This is what the shipped code did on 2026-08-07 and it is why the branch was rolled back.
        [Fact]
        public void Negative_control_a_reofferable_BR_reproduces_the_live_withdrawal_to_the_unit()
        {
            var specs = MagicBlock();
            var unsafeBr = specs[5];
            unsafeBr.Reofferable = true;           // the pre-fix behaviour, and nothing else
            specs[5] = unsafeBr;

            var br = new BloodLane(BrCap9270, BrSecondsToRun, BrDivider);
            var r = Drive(Pool9270, specs, MagicTakes(Ngu9270, br));

            Assert.Equal(2, r.Rounds);
            // The live block's own three numbers, all of them.
            Assert.Equal(1_009_942_040_007L, r.Offered[5]);   // 1,009,942,039,619 + a second 388
            Assert.Equal(1_009_942_039_231L, r.Took[5]);      // the clamp hides the negative take
            Assert.Equal(1_009_942_039_619L, r.Remainder);    // the take, plus the 388

            // The ritual is EMPTY — that is the difference between a wasted offer and a withdrawn one.
            Assert.Equal(0L, br.Placed);
            Assert.True(r.Remainder > r.Took[5], "the pool ended up holding more than the lane reports");
        }

        // THE FIX RESTORES THE SINGLE PASS EXACTLY, on the operator's OTHER sample — the one taken
        // 150 seconds earlier under the shipped allocator, which reported remainder=401. Same
        // membership, same driver, the pre-waterfill pool: BR is offered once and the residue is the
        // 401 the game itself left.
        [Fact]
        public void The_pre_waterfill_sample_is_reproduced_exactly_offer_once_residue_401()
        {
            var br = new BloodLane(BrCap9120, BrSecondsToRun, BrDivider);
            var r = Drive(Pool9120, MagicBlock(), MagicTakes(Ngu9120, br));

            Assert.Equal(1, r.Rounds);
            Assert.Equal(171_050_876_749L, r.Offered[0]);
            Assert.Equal(1_009_899_134_963L, r.Offered[5]);   // verbatim `offered=`
            Assert.Equal(1_009_899_134_562L, r.Took[5]);      // verbatim `took=`
            Assert.Equal(401L, r.Remainder);                  // verbatim `remainder=401`
            Assert.Equal(Pool9120, r.Took.Sum() + r.Remainder);
        }

        // A NON-RE-OFFERABLE LANE IS RETIRED ON ITS APPETITE, NOT INSTEAD OF THE FILL. The gate must
        // not stop the ROUND — lanes behind BR in the list, and lanes that ARE re-offerable, keep
        // every offer they had. Here BR sits FIRST and a hungry augment sits behind it.
        [Fact]
        public void The_gate_retires_only_the_unsafe_lane_and_the_waterfill_carries_on_without_it()
        {
            const long pool = 1_000_000_000_000L;
            var specs = new List<ConstraintLayer.LaneSpec>
            {
                SelfLimiting("BR", "BR-30"),
                SelfLimiting("BestAug", "BestAug-0"),
            };

            // BR takes its whole first offer and would happily be re-offered; BestAug is a ceiling
            // just above half its offer, so rule A keeps IT alive for a second helping.
            var br = new BloodLane(pool / 2, BrSecondsToRun, BrDivider);
            var r = Drive(pool, specs, new Dictionary<int, LaneAllocate>
            {
                { 0, br.Allocate },
                { 1, Ceiling(300_000_000_000L) },
            });

            // BR: one offer of pool/2, one take, and never seen again. ⚠ THE TAKE IS ONE UNIT OVER
            // THE CAP, and that is faithful: with budget == capValue exactly, MaxAllocationFor's
            // first branch (`remaining > capValue`) does not fire and the fall-through returns
            // `capValue / ceil(capValue/remaining) + 1` — the bare `+ 1L` that report 02 §12.4
            // identifies as the one wrong variant among the eight stair-snap copies and that
            // RitualMath preserves deliberately.
            Assert.Equal(pool / 2, r.Offered[0]);
            Assert.Equal(pool / 2 + 1, r.Took[0]);
            Assert.Equal(pool / 2 + 1, br.Placed);

            // BestAug: MORE than one offer — the waterfill still runs, it just runs without BR.
            // Round 1 offers it the 499,999,999,999 BR left and it takes its 300 B ceiling; round 2
            // re-offers the rest and it takes ALL of it, then rule B retires it because the take fell.
            Assert.Equal(2, r.Rounds);
            Assert.True(r.Offered[1] > pool / 2, "the re-offerable lane keeps its later rounds");
            Assert.Equal(499_999_999_999L, r.Took[1]);
            Assert.Equal(0L, r.Remainder);                 // nothing left idle, and nothing withdrawn
            Assert.Equal(pool, r.Took.Sum() + r.Remainder);
        }

        // ============================================================================================
        // THE CLASSIFICATION
        // ============================================================================================

        // DEFAULT CLOSED, and it is the whole safety property. A lane the table cannot name is not
        // re-offerable — a new lane class, a synthetic name, a typo.
        [Fact]
        public void An_unclassified_lane_is_never_reofferable()
        {
            Assert.False(ConstraintLayer.ReofferableLane(null));
            Assert.False(ConstraintLayer.ReofferableLane(""));
            Assert.False(ConstraintLayer.ReofferableLane("SomeLaneAddedLater"));
            Assert.False(ConstraintLayer.ReofferableLane("br"));       // Ordinal, not case-insensitive
            Assert.False(ConstraintLayer.ReofferableLane("BestAugment"));
        }

        // THE VERDICTS, one per lane, each carrying the proof recorded in ConstraintLayer.ReofferTable.
        [Theory]
        [InlineData("BR", false)]                   // MEASURED destructive — the defect above
        [InlineData("RitualBP", false)]             // same removeAllMagic, reachability only CONTINGENT
        [InlineData("BestAug", true)]
        [InlineData("NGUBP", true)]
        [InlineData("BasicTrainingBP", true)]
        [InlineData("TimeMachineBP", true)]
        [InlineData("AugmentBP", true)]
        [InlineData("AdvancedTrainingBP", true)]
        [InlineData("WandoosBP", true)]             // moot: the sink is never handed to Offer
        [InlineData("HackBP", true)]                // not routed through this layer at all
        public void Every_lane_type_carries_a_verdict(string lane, bool reofferable)
        {
            Assert.Equal(reofferable, ConstraintLayer.ReofferableLane(lane));

            var row = ConstraintLayer.ReofferTable.Single(x => x.Lane == lane);
            Assert.False(string.IsNullOrWhiteSpace(row.AdvisorShape));
            Assert.False(string.IsNullOrWhiteSpace(row.GameCall));
            Assert.False(string.IsNullOrWhiteSpace(row.Proof));
        }

        // ⚠ THE DRIFT DETECTOR. LaneTargets.Table is the advisor's lane-class inventory; a class that
        // exists there and not here would silently inherit the default — which is the SAFE direction,
        // but it would inherit it without anyone having read its Allocate(). Failing the build is the
        // point: the classification is a proof obligation, not a default.
        [Fact]
        public void The_reoffer_table_is_total_over_the_advisors_lane_classes()
        {
            var classes = LaneTargets.Table.Select(x => x.Lane).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            var classified = ConstraintLayer.ReofferTable.Select(x => x.Lane).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(classes, classified);

            // No lane classified twice — a second row would make the answer depend on table order.
            Assert.Equal(ConstraintLayer.ReofferTable.Length,
                         ConstraintLayer.ReofferTable.Select(x => x.Lane).Distinct().Count());
        }

        // The verdict reaches the plan, so the executor and the surfacing read ONE answer.
        [Fact]
        public void Compose_resolves_the_verdict_onto_every_lane_and_an_override_wins()
        {
            var specs = new List<ConstraintLayer.LaneSpec>
            {
                SelfLimiting("BR", "BR-30"),
                SelfLimiting("NGUBP", "NGU-0"),
                Sink(),
            };
            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), specs);

            Assert.False(plan.Lanes[0].Reofferable);
            Assert.True(plan.Lanes[1].Reofferable);
            Assert.True(plan.Lanes[2].Reofferable);

            // An explicit value overrides the table in BOTH directions — the negative control above
            // depends on this, and so would a caller with a lane shape the table cannot name.
            var forced = SelfLimiting("BR", "BR-30");
            forced.Reofferable = true;
            var closed = SelfLimiting("NGUBP", "NGU-0");
            closed.Reofferable = false;
            var plan2 = ConstraintLayer.Compose(1000, NoBudgetPressure(),
                new List<ConstraintLayer.LaneSpec> { forced, closed });

            Assert.True(plan2.Lanes[0].Reofferable);
            Assert.False(plan2.Lanes[1].Reofferable);
        }

        // ⚠ NO REGRESSION TO THE WATERFILL'S OWN HEADLINE CASE. The energy block that motivated
        // amendment 29 contains no unsafe lane, so the gate must be invisible to it: BestAug still
        // re-converges the 1.46 T the six NGUs decline. Pinned here as well as in WaterfillTests
        // because THIS is the property the gate could most plausibly have broken.
        [Fact]
        public void The_energy_block_the_waterfill_was_built_for_is_untouched_by_the_gate()
        {
            const long pool = 1_713_961_926_335L;
            var specs = new List<ConstraintLayer.LaneSpec> { SelfLimiting("BestAug", "BestAug-0") };
            foreach (var id in new[] { 0, 1, 3, 4, 5, 6 })
                specs.Add(SelfLimiting("NGUBP", "NGU-" + id));

            var takes = new Dictionary<int, LaneAllocate>
            {
                // BestAug at the live shape: a ceiling of 244,794,962,329, which sits above half its
                // 244,851,703,762 offer and therefore passes rule A.
                { 0, Ceiling(244_794_962_329L) },
            };
            var ngu = new[] { 81_079_664L, 81_113_322L, 81_168_387L, 81_410_002L, 81_465_735L, 4_732_524_855L };
            for (int i = 0; i < ngu.Length; i++)
                takes[i + 1] = Ceiling(ngu[i]);

            var r = Drive(pool, specs, takes);

            Assert.Equal(2, r.Rounds);
            Assert.Equal(244_794_962_329L * 2, r.Took[0]);     // the second helping still happens
            for (int i = 0; i < ngu.Length; i++)
                Assert.Equal(ngu[i], r.Took[i + 1]);           // and the ceilings are still retired
            Assert.True(r.Remainder < 1_464_028_202_041L,
                "the gate must not put the 85% idle pool back");
        }
    }
}
