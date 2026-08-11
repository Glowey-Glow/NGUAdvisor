using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE COMPOSITION (constraint-layer-spec §2): four passes in order, every tick, then fill seated
    // lanes to min(capacity, want) in list order, remainder to the surplus sink. These tests assert
    // the full four-pass SEQUENCE — not each pass in isolation (the per-pass files do that) — plus
    // the three properties the composition must not lose: the seat rule survives composition, the
    // surplus always lands, and a focus cannot strand the pool.
    public class ConstraintLayerTests
    {
        private static BudgetPass.BudgetState NoBudgetPressure() =>
            new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 };

        private static BudgetPass.BudgetState Exhausted() =>
            new BudgetPass.BudgetState { InLevelChallenge = true, RebirthLevels = 100 };

        private static ConstraintLayer.LaneSpec Lane(string name, long capacity,
            bool wantsMore = true, string label = null)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = capacity,
                WantsMore = wantsMore,
            };

        private static ConstraintLayer.LaneSpec Refused(string name, string why)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Feasibility = FeasibilityPass.Verdict.Refuse(why),
                Capacity = 1000,
                WantsMore = true,
            };

        private static ConstraintLayer.LaneSpec Sink()
            => new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
                SurplusSink = true,
            };

        private static ConstraintLayer.LaneSpec RateLane(string name, long capacity, string label = null)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = capacity,
                RateLane = true,
                // Deliberately hostile default: a rate lane must be funded even when a stray
                // "target met" arrives — Pass 3 never sees these lanes (amendment 18 §1.2).
                WantsMore = false,
                WantReason = "stray target-met signal that must be ignored",
            };

        // ---- the end-to-end composition (a pool, a lane set, a target table -> an allocation) ----

        [Fact]
        public void Full_four_pass_sequence_produces_the_allocation_and_a_reason_for_every_zero()
        {
            // Pass 3 driven by the REAL target table machinery: the sole standing terminal
            // evaluated through TargetPass for a lane already past it. ⚠ THAT ROW IS NOW AT BLOCK
            // AT 100,000, NOT RESPAWN 401 — [OPERATOR] removed Respawn 2026-08-07 and promoted
            // Block AT to a hard-cap Terminal, so this drives the same machinery through the row
            // that is actually standing. RowsFor hands over BOTH at-2 rows (the ch.3 Normal 5,000
            // precondition and this one) and Evaluate selects by the lane's ACTIVE TRACK.
            var blockRows = TargetPass.GuideRows
                .Where(r => r.System == TargetPass.SysAt && r.Index == 2).ToList();
            var blockLane = new TargetPass.LaneState
            {
                System = TargetPass.SysAt,
                Index = 2,
                ActiveTrack = TargetPass.Track.Evil,
                LevelOnTrack = 150_000,
            };
            var answer = TargetPass.Evaluate(blockLane, blockRows, FeasibilityPass.Verdict.Seat());
            Assert.Equal(TargetPass.Disposition.WriteTarget, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.Satisfied, answer.Satisfaction);

            string wantReason;
            var wants = ConstraintLayer.WantFromAnswer(answer, out wantReason);
            Assert.False(wants);

            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Refused("TimeMachineBP", "gold stall: bar unstarted and realGold 0 < cost 5"),
                Lane("AugmentBP", capacity: 300),
                new ConstraintLayer.LaneSpec        // AT Block, satisfied via the target table
                {
                    Name = "AdvancedTrainingBP", Label = "AT-2",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 400,
                    WantsMore = wants, WantReason = wantReason,
                },
                Lane("NGUBP", capacity: 200, label: "NGU-4"),
                Lane("BasicTrainingBP", capacity: 0),          // saturated — Pass 2
                new ConstraintLayer.LaneSpec                    // the beard shape: seats, never fills
                {
                    Name = "Beards",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    NoAllocation = true,
                },
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);

            Assert.True(plan.CapacitiesKnown);

            // Pass 1: gold-stalled TM goes to ZERO, not a reduced share.
            Assert.Equal(ConstraintLayer.PassId.Feasibility, plan.Lanes[0].EliminatedBy);
            Assert.Equal(0, plan.Lanes[0].Allocation);
            Assert.Contains("gold stall", plan.Lanes[0].Reason);

            // Seated lanes fill to min(capacity, want) in list order.
            Assert.Equal(300, plan.Lanes[1].Allocation);

            // Pass 3: the satisfied terminal is eliminated with the table's reason.
            Assert.Equal(ConstraintLayer.PassId.Target, plan.Lanes[2].EliminatedBy);
            Assert.Equal(0, plan.Lanes[2].Allocation);
            Assert.Contains("target met", plan.Lanes[2].Reason);

            Assert.Equal(200, plan.Lanes[3].Allocation);

            // Pass 2: a saturated lane holds no fill slot.
            Assert.Equal(ConstraintLayer.PassId.Capacity, plan.Lanes[4].EliminatedBy);

            // The beard seat: seated, zero allocation, no elimination.
            Assert.True(plan.Lanes[5].Seated);
            Assert.Equal(0, plan.Lanes[5].Allocation);

            // Remainder to the sink; nothing stranded.
            Assert.Equal(500, plan.SinkAllocation);
            Assert.Equal(0, plan.Unallocated);
            Assert.Equal(plan.Pool, plan.Lanes.Sum(l => l.Allocation));

            // Every eliminated lane carries a surfaceable reason (spec §10).
            foreach (var d in plan.Lanes.Where(l => !l.Seated))
                Assert.False(string.IsNullOrEmpty(d.Reason));
        }

        [Fact]
        public void Pass_order_is_budget_then_feasibility_a_lane_failing_both_reports_budget()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Refused("AugmentBP", "gold stall"),   // counting lane, ALSO infeasible
                Lane("NGUBP", capacity: 100),          // exempt by omission
                Sink(),
            };

            var plan = ConstraintLayer.Compose(500, Exhausted(), lanes);

            // Budget runs FIRST: the earlier pass owns the elimination and its reason.
            Assert.Equal(ConstraintLayer.PassId.Budget, plan.Lanes[0].EliminatedBy);
            Assert.Contains("budget exhausted", plan.Lanes[0].Reason);

            // The exempt lane is untouched and becomes a productive destination.
            Assert.True(plan.Lanes[1].Seated);
            Assert.Equal(100, plan.Lanes[1].Allocation);
        }

        [Fact]
        public void Budget_exhaustion_idles_the_counting_sink_and_surfaces_the_unallocated_remainder()
        {
            // WandoosBP holds two counting sites (BudgetPass.Allowlist) — at 100/100 the sink itself
            // is refused at Pass 0, and the remainder must be SURFACED, not silently dropped
            // (spec §3.4: a lane going to zero is indistinguishable from a bug).
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("AugmentBP", capacity: 300),     // counting — refused
                Lane("NGUBP", capacity: 150),          // exempt — funds
                Sink(),                                // WandoosBP — counting — refused
            };

            var plan = ConstraintLayer.Compose(1000, Exhausted(), lanes);

            Assert.Equal(ConstraintLayer.PassId.Budget, plan.Lanes[0].EliminatedBy);
            Assert.Equal(ConstraintLayer.PassId.Budget, plan.Lanes[2].EliminatedBy);
            Assert.False(plan.SinkSeated);
            Assert.Equal(150, plan.Lanes[1].Allocation);
            Assert.Equal(850, plan.Unallocated);
            Assert.Contains("surplus sink refused", plan.UnallocatedReason);
            Assert.NotNull(plan.BudgetMessage);
            Assert.Contains("2 lanes idle", plan.BudgetMessage);
        }

        // ---- the seat rule survives composition ---------------------------------------------------

        // The founding defect class, in composition form: RIT-7 seated infeasible and inflated the
        // equal-share divisor for every magic lane. Here there IS no divisor — adding a refused lane
        // at ANY pass must change no other lane's allocation, and the refusal lands in the roster's
        // refusal list, never its seat count.
        [Fact]
        public void A_lane_refused_at_any_pass_changes_no_other_lanes_allocation()
        {
            var baseline = new List<ConstraintLayer.LaneSpec>
            {
                Lane("NGUBP", capacity: 200, label: "NGU-0"),
                Lane("AugmentBP", capacity: 300),
                Sink(),
            };
            var basePlan = ConstraintLayer.Compose(1000, NoBudgetPressure(), baseline);

            // One refused lane per pass, inserted mid-list.
            var intruders = new[]
            {
                new ConstraintLayer.LaneSpec        // Pass 0: counting lane under exhaustion — see
                {                                    // the dedicated budget test; here use passes 1-3
                    Name = "RitualBP", Label = "RIT-7",
                    Feasibility = FeasibilityPass.Verdict.Refuse("boss gate: bossID 10 has not passed required 58"),
                    Capacity = 999999, WantsMore = true,
                },
                Lane("BasicTrainingBP", capacity: 0),                       // Pass 2: saturated
                Lane("TimeMachineBP", capacity: 500, wantsMore: false),     // Pass 3: target met
            };

            foreach (var intruder in intruders)
            {
                var withIntruder = new List<ConstraintLayer.LaneSpec>(baseline);
                withIntruder.Insert(1, intruder);

                var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), withIntruder);

                Assert.Equal(basePlan.Lanes[0].Allocation, plan.Lanes[0].Allocation);
                Assert.Equal(basePlan.Lanes[1].Allocation, plan.Lanes[2].Allocation);
                Assert.Equal(basePlan.SinkAllocation, plan.SinkAllocation);

                Assert.False(plan.Lanes[1].Seated);
                Assert.Equal(0, plan.Lanes[1].Allocation);
                Assert.False(string.IsNullOrEmpty(plan.Lanes[1].Reason));

                // The roster files the two cases into different lists; the refusal cannot enter
                // any count.
                Assert.Equal(basePlan.Roster.SeatCount, plan.Roster.SeatCount);
                Assert.Contains(plan.Roster.Refusals,
                    r => r.Lane == (intruder.Label ?? intruder.Name));
            }
        }

        // ---- the surplus always lands -------------------------------------------------------------

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(499L)]
        [InlineData(500L)]
        [InlineData(1_000_000L)]
        public void No_input_strands_the_pool_sum_of_allocations_always_equals_it(long pool)
        {
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("NGUBP", capacity: 200),
                Refused("TimeMachineBP", "gold stall"),
                Lane("AugmentBP", capacity: 300),
                Lane("BasicTrainingBP", capacity: 0),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), lanes);

            Assert.Equal(plan.Pool, plan.Lanes.Sum(l => l.Allocation));
            Assert.Equal(0, plan.Unallocated);
        }

        [Fact]
        public void Every_lane_refused_the_sink_takes_the_whole_pool()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Refused("AugmentBP", "boss gate"),
                Refused("TimeMachineBP", "gold stall"),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(777, NoBudgetPressure(), lanes);

            Assert.Equal(777, plan.SinkAllocation);
            Assert.Equal(0, plan.Unallocated);
        }

        [Fact]
        public void A_lane_set_without_a_sink_surfaces_the_remainder_instead_of_dropping_it()
        {
            var lanes = new List<ConstraintLayer.LaneSpec> { Lane("NGUBP", capacity: 100) };

            var plan = ConstraintLayer.Compose(500, NoBudgetPressure(), lanes);

            Assert.Equal(400, plan.Unallocated);
            Assert.Contains("no surplus sink", plan.UnallocatedReason);
        }

        [Fact]
        public void A_second_sink_is_refused_the_remainder_has_one_destination()
        {
            var lanes = new List<ConstraintLayer.LaneSpec> { Sink(), Sink() };

            var plan = ConstraintLayer.Compose(100, NoBudgetPressure(), lanes);

            Assert.True(plan.Lanes[0].Seated);
            Assert.False(plan.Lanes[1].Seated);
            Assert.Contains("duplicate surplus sink", plan.Lanes[1].Reason);
            Assert.Equal(100, plan.SinkAllocation);
        }

        // ---- vacuity (spec §5.2) ------------------------------------------------------------------

        [Fact]
        public void Sigma_capacity_below_pool_fills_everything_and_passes_the_remainder()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("NGUBP", capacity: 100, label: "NGU-0"),
                Lane("NGUBP", capacity: 250, label: "NGU-1"),
                Lane("AugmentBP", capacity: 50),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(10_000, NoBudgetPressure(), lanes);

            Assert.True(plan.Vacuity.Vacuous);
            Assert.Equal(400, plan.Vacuity.TotalCapacity);
            Assert.Equal(100, plan.Lanes[0].Allocation);
            Assert.Equal(250, plan.Lanes[1].Allocation);
            Assert.Equal(50, plan.Lanes[2].Allocation);
            Assert.Equal(9_600, plan.SinkAllocation);
        }

        // ---- Evil NGUs — rate lanes (amendment 18 §1) ---------------------------------------------

        [Fact]
        public void Rate_lanes_fund_to_their_share_down_the_list_never_past_it()
        {
            // ⚠ UPDATED BY AMENDMENT 28. This used to read 400/400/0/200: the first two lanes were
            // blanked at full capacity and the third was refused with "partial funding refused",
            // because regime A funded down the list out of the WHOLE pool. Four destinations, so
            // each lane's capacity of 400 is now measured against a share of ~250 and it chunks.
            //
            // ⚠ UPDATED AGAIN BY AMENDMENT 29 (the waterfill): 201/201/201/397 -> 237/237/237/289.
            // The chunk arithmetic left 397 of 1000 standing after one pass; the sink used to sweep
            // ALL of it simply by being what the walk ended on. It now keeps ONE ROUND'S SHARE and
            // the rest is re-offered to the lanes that proved appetite. THE EVEN SPLIT — the
            // property this test exists for — is untouched: three identical lanes, three identical
            // amounts, and the sink within 22% of a peer rather than 2x one.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGUBP", 400, "NGU-0"),
                RateLane("NGUBP", 400, "NGU-1"),
                RateLane("NGUBP", 400, "NGU-2"),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);

            Assert.Equal(237, plan.Lanes[0].Allocation);
            Assert.Equal(237, plan.Lanes[1].Allocation);
            // No lane is refused for the benefit of one ahead of it: nothing was stolen, because
            // a reserved share cannot be stolen from.
            Assert.Equal(237, plan.Lanes[2].Allocation);
            Assert.True(plan.Lanes[2].Seated);
            Assert.Null(plan.Lanes[2].Reason);
            Assert.Equal(289, plan.SinkAllocation);
            // NEVER PAST ITS CAPACITY, which is the other half of the test's name and the thing the
            // extra rounds could most easily have broken.
            foreach (var d in plan.Lanes)
                if (d.RateLane)
                    Assert.True(d.Allocation <= d.Capacity, d.Label + " overran its capacity");
            Assert.Equal(1000, plan.Lanes.Sum(l => l.Allocation));
        }

        [Fact]
        public void A_lane_whose_capacity_fits_its_share_is_still_blanked_at_exactly_capacity()
        {
            // Amendment 18 §1.2's full blank is NOT lost with the regimes: when capacity fits the
            // share, NguCap's chunk divisor is 1 and the lane funds at exactly its capacity —
            // clamped there, because the x1.00000202655792 fudge overshoots by design.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGUBP", 200, "NGU-0"),
                RateLane("NGUBP", 200, "NGU-1"),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);

            Assert.Equal(200, plan.Lanes[0].Allocation);
            Assert.Equal(200, plan.Lanes[1].Allocation);
            Assert.Equal(600, plan.SinkAllocation);
        }

        [Fact]
        public void A_short_pool_reaches_every_lane_the_dear_one_no_longer_costs_the_walk_a_slot()
        {
            // ⚠ UPDATED BY AMENDMENT 28. This used to read 400/0/300/100 — "working down the list
            // as far as the pool reaches", where NGU-1 at 900 was unaffordable and got nothing
            // while NGU-0 and NGU-2 were blanked whole. Every lane now takes its share of what
            // reaches it, and the dear lane makes progress instead of waiting.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGUBP", 400, "NGU-0"),
                RateLane("NGUBP", 900, "NGU-1"),
                RateLane("NGUBP", 300, "NGU-2"),
                Sink(),
            };

            // ⚠ UPDATED AGAIN BY AMENDMENT 29 (the waterfill): 201/181/151/267 -> 217/198/168/217.
            // The DEAR lane is the point of this test and it is still funded — more than before, not
            // less — and the sink has come down from 33.4% of the pool to a peer's 27.1%.
            var plan = ConstraintLayer.Compose(800, NoBudgetPressure(), lanes);

            Assert.Equal(217, plan.Lanes[0].Allocation);
            Assert.Equal(198, plan.Lanes[1].Allocation);
            Assert.Equal(168, plan.Lanes[2].Allocation);
            Assert.Equal(217, plan.SinkAllocation);
            Assert.Equal(0, plan.RateLanesSkipped);
            // NGU-1 is the dear one — capacity 900 against a pool of 800 — and it makes progress.
            Assert.True(plan.Lanes[1].Allocation > 0);
            Assert.Equal(800, plan.Lanes.Sum(l => l.Allocation));
        }

        [Fact]
        public void Pass_3_never_sees_a_rate_lane_a_stray_target_signal_is_ignored()
        {
            // RateLane() builds its specs with WantsMore=false — if Pass 3 consulted them they
            // would all be eliminated. They must fund anyway.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGUBP", 250, "NGU-0"),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);

            Assert.True(plan.Lanes[0].Seated);
            Assert.Equal(250, plan.Lanes[0].Allocation);
        }

        [Fact]
        public void A_rate_lane_without_a_known_capacity_is_a_caller_error_refused_at_pass_2()
        {
            var spec = RateLane("NGUBP", ConstraintLayer.SelfLimiting, "NGU-0");
            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(),
                new List<ConstraintLayer.LaneSpec> { spec, Sink() });

            Assert.Equal(ConstraintLayer.PassId.Capacity, plan.Lanes[0].EliminatedBy);
            Assert.Contains("rate lane without a known capacity", plan.Lanes[0].Reason);
        }

        // ---- declared focus (amendment 18 §2) -----------------------------------------------------

        [Fact]
        public void Focused_lanes_seat_others_refuse_with_the_focus_reason_surplus_still_reaches_wandoos()
        {
            var pawg = new ConstraintLayer.FocusSet("PAWG", new[] { "NGU-0", "NGU-1", "NGU-3", "NGU-5" });
            var none = new FeasibilityPass.ExternalConstraints();

            ConstraintLayer.LaneSpec Ngu(string label, long cap)
            {
                var c = ConstraintLayer.WithFocus(none, pawg, label, isSurplusSink: false);
                return new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP", Label = label,
                    Feasibility = FeasibilityPass.ExternalGate(c),
                    Capacity = cap, RateLane = true,
                };
            }

            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Ngu("NGU-0", 100), Ngu("NGU-1", 100), Ngu("NGU-4", 100), Ngu("NGU-6", 100),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);

            Assert.Equal(100, plan.Lanes[0].Allocation);
            Assert.Equal(100, plan.Lanes[1].Allocation);

            Assert.False(plan.Lanes[2].Seated);
            Assert.Equal(ConstraintLayer.PassId.Feasibility, plan.Lanes[2].EliminatedBy);
            Assert.Equal("not in declared focus: PAWG", plan.Lanes[2].Reason);
            Assert.False(plan.Lanes[3].Seated);

            // The surplus reaches Wandoos — a focus cannot strand the pool.
            Assert.Equal(800, plan.SinkAllocation);
            Assert.Equal(0, plan.Unallocated);
        }

        [Fact]
        public void A_focused_but_saturated_state_does_not_read_as_nothing_to_allocate()
        {
            // Amendment 18 §2.4: the focused lanes are all at capacity and surplus remains —
            // it goes to the sink, not nowhere.
            var focus = new ConstraintLayer.FocusSet("PAWG", new[] { "NGU-0" });
            var none = new FeasibilityPass.ExternalConstraints();

            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP", Label = "NGU-0",
                    Feasibility = FeasibilityPass.ExternalGate(
                        ConstraintLayer.WithFocus(none, focus, "NGU-0", isSurplusSink: false)),
                    Capacity = 50, RateLane = true,
                },
                Sink(),
            };

            var plan = ConstraintLayer.Compose(10_000, NoBudgetPressure(), lanes);

            Assert.Equal(50, plan.Lanes[0].Allocation);
            Assert.Equal(9_950, plan.SinkAllocation);
            Assert.Equal(0, plan.Unallocated);
        }

        [Fact]
        public void The_sink_is_exempt_from_any_focus_by_construction()
        {
            var focus = new ConstraintLayer.FocusSet("PAWG", new[] { "NGU-0" });
            var none = new FeasibilityPass.ExternalConstraints();

            var c = ConstraintLayer.WithFocus(none, focus, "WAN", isSurplusSink: true);
            Assert.Null(c.FocusExcluded);
            Assert.True(FeasibilityPass.ExternalGate(c).Seated);

            var d = ConstraintLayer.WithFocus(none, focus, "WAN", isSurplusSink: false);
            Assert.Equal("PAWG", d.FocusExcluded);
        }

        [Fact]
        public void No_focus_or_membership_leaves_constraints_untouched()
        {
            var none = new FeasibilityPass.ExternalConstraints();

            Assert.Null(ConstraintLayer.WithFocus(none, null, "NGU-0", false).FocusExcluded);

            var focus = new ConstraintLayer.FocusSet("Adv/DC", new[] { "NGU-4", "NGU-6" });
            Assert.Null(ConstraintLayer.WithFocus(none, focus, "NGU-4", false).FocusExcluded);
        }

        // ---- Pass 3 want, from a target-table answer ----------------------------------------------

        [Fact]
        public void Only_a_satisfied_written_terminal_stops_a_lane()
        {
            string reason;

            var satisfied = new TargetPass.LaneAnswer
            {
                Disposition = TargetPass.Disposition.WriteTarget,
                Satisfaction = TargetPass.Satisfaction.Satisfied,
                TargetToWrite = 401,
            };
            Assert.False(ConstraintLayer.WantFromAnswer(satisfied, out reason));
            Assert.Contains("target met", reason);
            Assert.Contains("401", reason);

            var unsatisfied = new TargetPass.LaneAnswer
            {
                Disposition = TargetPass.Disposition.WriteTarget,
                Satisfaction = TargetPass.Satisfaction.Unsatisfied,
                TargetToWrite = 401,
            };
            Assert.True(ConstraintLayer.WantFromAnswer(unsatisfied, out reason));

            // Preconditions never terminate — writing one abandons the lane (23 §0.4).
            var precondition = new TargetPass.LaneAnswer
            {
                Disposition = TargetPass.Disposition.Precondition,
                Satisfaction = TargetPass.Satisfaction.NoClaim,
                MilestonesAllMet = true,
            };
            Assert.True(ConstraintLayer.WantFromAnswer(precondition, out reason));

            // A silence never defaults to satisfied or to a synthetic stop (23 §7).
            var silent = new TargetPass.LaneAnswer
            {
                Disposition = TargetPass.Disposition.Silent,
                Satisfaction = TargetPass.Satisfaction.NoClaim,
            };
            Assert.True(ConstraintLayer.WantFromAnswer(silent, out reason));

            // An unresolved operator decision must not stop a lane the operator has not decided.
            var decision = new TargetPass.LaneAnswer
            {
                Disposition = TargetPass.Disposition.OperatorDecision,
                Satisfaction = TargetPass.Satisfaction.NoClaim,
            };
            Assert.True(ConstraintLayer.WantFromAnswer(decision, out reason));
        }

        // ---- the live executor's fill is the same arithmetic --------------------------------------

        [Fact]
        public void FillSession_offers_the_remaining_pool_to_self_limiting_lanes_and_tracks_takes()
        {
            var session = new ConstraintLayer.FillSession(1000);
            var lane = new ConstraintLayer.LaneDecision
            {
                Seated = true,
                Capacity = ConstraintLayer.SelfLimiting,
            };

            string skip;
            Assert.Equal(1000, session.Offer(lane, out skip));
            session.Commit(300);   // the lane's own stair-snap took 300

            Assert.Equal(700, session.Offer(lane, out skip));
            session.Commit(700);

            Assert.Equal(0, session.TakeRemainder());
        }

        [Fact]
        public void FillSession_and_Compose_agree_when_capacities_are_known()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Lane("NGUBP", capacity: 200),
                RateLane("NGUBP", 500, "NGU-1"),
                Lane("AugmentBP", capacity: 150),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);
            Assert.True(plan.CapacitiesKnown);

            // Drive the executor loop by hand over the same plan — ROUND BY ROUND, exactly as
            // ConstraintLayerBridge.PerformSwap drives it (amendment 29). This is the contract that
            // keeps the tested fill and the live fill from diverging, and it is worth MORE now than
            // it was under one pass: a second round is a second place for them to disagree.
            var fill = new ConstraintLayer.Waterfill(1000, plan.Lanes, plan.SinkIndex);
            var takes = new long[plan.Lanes.Length];
            ConstraintLayer.FillSession session;
            while ((session = fill.BeginRound()) != null)
            {
                for (int i = 0; i < plan.Lanes.Length; i++)
                {
                    if (i == plan.SinkIndex || !fill.IsLive(i)) continue;
                    string skip;
                    var lane = fill.LaneForRound(i);
                    var offer = session.Offer(lane, out skip);
                    var take = Math.Min(offer, lane.Capacity == ConstraintLayer.SelfLimiting
                        ? offer : lane.Capacity);
                    takes[i] += take;
                    session.Commit(take);
                    fill.Record(i, offer, take);
                }
                fill.EndRound();
            }

            for (int i = 0; i < plan.Lanes.Length; i++)
                if (i != plan.SinkIndex)
                    Assert.Equal(plan.Lanes[i].Allocation, takes[i]);
            Assert.Equal(plan.SinkAllocation, fill.SinkTotal);
            Assert.Equal(1000, takes.Sum() + fill.SinkTotal);
        }

        [Fact]
        public void RecordExecutedRemainder_routes_to_the_sink_or_surfaces_it()
        {
            var seated = ConstraintLayer.Compose(100, NoBudgetPressure(),
                new List<ConstraintLayer.LaneSpec> { Sink() });
            ConstraintLayer.RecordExecutedRemainder(seated, 40);
            Assert.Equal(40, seated.SinkAllocation);
            Assert.Equal(0, seated.Unallocated);

            var noSink = ConstraintLayer.Compose(100, NoBudgetPressure(),
                new List<ConstraintLayer.LaneSpec>());
            ConstraintLayer.RecordExecutedRemainder(noSink, 40);
            Assert.Equal(40, noSink.Unallocated);
            Assert.Contains("no surplus sink", noSink.UnallocatedReason);
        }

        // ---- the 85%-idle-pool report, replayed from the live numbers -----------------------------

        // OPERATOR REPORT 2026-08-07: "over 80% of energy idle when there are multiple available
        // lanes of placement". Real, reproduced here to the unit — and NOT caused by the ten
        // BasicTraining lanes the report pointed at.
        //
        // The capacities below are the live TAKES from [AllocDbg] 8/7/2026-10:47 PM (5379s), a block
        // with NO BasicTraining lane in it at all, on a pool of 1,713,961,926,335. Replaying them
        // through the shipped fill reproduces every single `offered=` figure in that block and the
        // remainder to the unit, which is what makes it a characterisation of the LIVE arithmetic
        // rather than a model of it.
        private const long LivePool = 1_713_961_926_335L;

        private static List<ConstraintLayer.LaneSpec> LiveNonTrainingLanes() =>
            new List<ConstraintLayer.LaneSpec>
            {
                Lane("BestAug", capacity:   244_794_962_329L, label: "BestAug-0"),
                Lane("NGUBP",   capacity:        81_079_664L, label: "NGU-0"),
                Lane("NGUBP",   capacity:        81_113_322L, label: "NGU-1"),
                Lane("NGUBP",   capacity:        81_168_387L, label: "NGU-3"),
                Lane("NGUBP",   capacity:        81_410_002L, label: "NGU-4"),
                Lane("NGUBP",   capacity:        81_465_735L, label: "NGU-5"),
                Lane("NGUBP",   capacity:     4_732_524_855L, label: "NGU-6"),
            };

        [Fact]
        public void The_live_85_percent_remainder_is_reproduced_by_the_shipped_fill()
        {
            var plan = ConstraintLayer.Compose(LivePool, NoBudgetPressure(), LiveNonTrainingLanes());

            Assert.True(plan.CapacitiesKnown);
            foreach (var d in plan.Lanes)
                Assert.True(d.Seated);

            // Every lane took its whole capacity: not one of them was share-starved. The pool is not
            // being MISALLOCATED — it is being DECLINED, because a self-limiting lane past its
            // stair-snap discards whatever it is handed above one level's worth per tick.
            for (int i = 0; i < plan.Lanes.Length; i++)
                Assert.Equal(LiveNonTrainingLanes()[i].Capacity, plan.Lanes[i].Allocation);

            // The live figure, to the unit: debug.log 5379s "remainder=1464028202041 (85.418% of
            // pool) sink=absent".
            Assert.Equal(1_464_028_202_041L, plan.Unallocated);
            Assert.Contains("no surplus sink", plan.UnallocatedReason);
            Assert.True(plan.Unallocated * 100.0 / LivePool > 85.0);
        }

        // THE NEGATIVE CONTROL for the report's diagnosis. Put the ten BasicTraining lanes back in
        // front — each at the rebirth-floored capacity of 1 that the reporting account's save
        // actually carries (see LaneCapMathTests.BasicTrainingAllocation_AtTheRebirthFlooredCapOfOne)
        // — and the idle pool moves by TEN UNITS out of 1.46 TRILLION.
        //
        // So `took=1` cannot be the cause of the 85%: with those lanes present the remainder is
        // 85.418% of the pool, and with them gone it is 85.418% of the pool. They also cost the
        // later lanes nothing, because the fill's divisor is spent per lane OFFERED while only the
        // real take is committed — BestAug's share is remaining/7 either way, which is why its offer
        // differs by 2 units across the two lane sets and not by a factor of 17/7.
        [Fact]
        public void Removing_the_took_one_training_lanes_changes_the_idle_pool_by_ten_units()
        {
            var withoutTraining = ConstraintLayer.Compose(LivePool, NoBudgetPressure(),
                LiveNonTrainingLanes());

            var withTraining = new List<ConstraintLayer.LaneSpec>();
            foreach (var idx in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11 })
                withTraining.Add(Lane("BasicTrainingBP", capacity: 1, label: "BasicTraining-" + idx));
            withTraining.AddRange(LiveNonTrainingLanes());

            var plan = ConstraintLayer.Compose(LivePool, NoBudgetPressure(), withTraining);

            for (int i = 0; i < 10; i++)
            {
                Assert.True(plan.Lanes[i].Seated);
                Assert.Equal(1, plan.Lanes[i].Allocation);
            }
            // Offered a hundred billion, absorbing one — exactly the live shape.
            Assert.True(LivePool / 17 > 100_000_000_000L);

            Assert.Equal(withoutTraining.Unallocated - 10, plan.Unallocated);
            Assert.True(plan.Unallocated * 100.0 / LivePool > 85.0);
        }
    }
}
