using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE [AllocDbg] TWIN for the constraint layer (AllocTelemetry).
    //
    // The old path's AllocDiagnostic caught the founding defect — CAPBestAug-0 taking 732,735,009,221
    // of 732,767,363,921 idle energy, 99.996% of the pool into one lane. It is reached BELOW the
    // branch to ConstraintLayerBridge, so the new path had no per-lane dump at all: parity emits only
    // divergences (and only in the direction its Need approximation can express), Surface emits only
    // three aggregate state lines, and LastEnergyPlan/LastMagicPlan have no reader. A lane funded
    // correctly — or catastrophically, as long as the catastrophe was a legal seat — said nothing.
    //
    // These tests pin the two properties that make the instrument worth having:
    //   1. EVERY ZERO CARRIES ITS REASON (spec §10), including the three a seated lane can reach that
    //      the plan itself does not record.
    //   2. The throttle keeps a STEADY state visible. The failure this exists to prevent (audit/25 §4,
    //      the two-hour Evil-NGU zero) never changed state, so a change-only trigger would be blind
    //      to it by construction.
    public class AllocTelemetryTests
    {
        // ---- builders ----------------------------------------------------------------------------

        private static ConstraintLayer.LaneDecision Seated(string label, long cap, long took,
            bool rate = false, bool sink = false, bool noAlloc = false, string reason = null)
            => new ConstraintLayer.LaneDecision
            {
                Name = label,
                Label = label,
                Seated = true,
                EliminatedBy = ConstraintLayer.PassId.None,
                Capacity = cap,
                Allocation = took,
                RateLane = rate,
                SurplusSink = sink,
                NoAllocation = noAlloc,
                Reason = reason,
            };

        private static ConstraintLayer.LaneDecision Refused(string label, ConstraintLayer.PassId pass,
            string reason)
            => new ConstraintLayer.LaneDecision
            {
                Name = label,
                Label = label,
                Seated = false,
                EliminatedBy = pass,
                Reason = reason,
                Capacity = 1000,
            };

        private static ConstraintLayer.Plan PlanOf(long pool, params ConstraintLayer.LaneDecision[] lanes)
            => new ConstraintLayer.Plan { Pool = pool, Lanes = lanes, SinkIndex = -1 };

        // The line for one lane, pulled out of the block by label.
        private static string LineFor(string block, string label)
            => block.Split('\n').First(l => l.TrimStart().StartsWith(label));

        // ---- the mixed plan ------------------------------------------------------------------------

        [Fact]
        public void A_mixed_seated_and_refused_plan_renders_every_lane_with_disposition_capacity_offer_and_take()
        {
            var plan = PlanOf(1000,
                Seated("AugmentBP", cap: 300, took: 300),
                Refused("TimeMachineBP", ConstraintLayer.PassId.Feasibility,
                    "gold stall: bar unstarted and realGold 0 < cost 5"),
                Seated("NGU-4", cap: 200, took: 200, rate: true),
                Refused("BasicTrainingBP", ConstraintLayer.PassId.Capacity,
                    "at capacity: the marginal unit is provably wasted (spec §5)"),
                Seated("Beards", cap: 0, took: 0, noAlloc: true));
            plan.SinkIndex = 5;
            plan.SinkSeated = true;
            plan.Lanes = plan.Lanes.Concat(new[]
            {
                Seated("WandoosBP", cap: ConstraintLayer.SelfLimiting, took: 500, sink: true)
            }).ToArray();
            plan.SinkAllocation = 500;

            var offers = new long[] { 300, 0, 200, 0, 0, 500 };
            var block = AllocTelemetry.Render("Energy", plan, offers);

            Assert.StartsWith("[AllocDbg] Energy pool=1000 lanes=6 seated=4", block);

            // A seated, funded lane: its capacity, its offer, its take and its share of the pool.
            Assert.Contains("seated", LineFor(block, "AugmentBP"));
            Assert.Contains("cap=300 offered=300 took=300 (30% of pool)", LineFor(block, "AugmentBP"));

            // Refused lanes name the PASS, in ConstraintParity's spelling (one source, no drift).
            Assert.Contains("refused [pass 1 feasibility]", LineFor(block, "TimeMachineBP"));
            Assert.Contains("gold stall", LineFor(block, "TimeMachineBP"));
            Assert.Contains("refused [pass 2 capacity]", LineFor(block, "BasicTrainingBP"));

            // The two lane KINDS the fill treats differently are tagged, because the reader has to
            // know which arithmetic produced the number.
            Assert.Contains("[rate]", LineFor(block, "NGU-4"));
            Assert.Contains("[sink]", LineFor(block, "WandoosBP"));
        }

        [Fact]
        public void The_composed_plan_from_the_real_four_passes_renders_a_reason_beside_every_zero()
        {
            // Built through Compose so the reasons under test are the ones the LAYER writes, not
            // strings this test invented.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                new ConstraintLayer.LaneSpec
                {
                    Name = "TimeMachineBP",
                    Feasibility = FeasibilityPass.Verdict.Refuse("gold stall: bar unstarted"),
                    Capacity = 1000, WantsMore = true,
                },
                new ConstraintLayer.LaneSpec
                {
                    Name = "AugmentBP",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 300, WantsMore = true,
                },
                new ConstraintLayer.LaneSpec          // saturated — Pass 2
                {
                    Name = "BasicTrainingBP",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 0, WantsMore = true,
                },
                new ConstraintLayer.LaneSpec          // target met — Pass 3
                {
                    Name = "NGUBP", Label = "NGU-2",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 400, WantsMore = false, WantReason = "target met: level >= 401",
                },
                new ConstraintLayer.LaneSpec          // the beard shape: seats, never fills
                {
                    Name = "Beards",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    NoAllocation = true,
                },
                new ConstraintLayer.LaneSpec
                {
                    Name = "WandoosBP",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = ConstraintLayer.SelfLimiting,
                    WantsMore = true, SurplusSink = true,
                },
            };

            var plan = ConstraintLayer.Compose(1000,
                new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 }, lanes);
            var offers = plan.Lanes.Select(l => l.Allocation).ToArray();
            var block = AllocTelemetry.Render("Energy", plan, offers);

            // THE INVARIANT. Not "most zeros" — every one, structurally, before any string matching.
            for (int i = 0; i < plan.Lanes.Length; i++)
            {
                var d = plan.Lanes[i];
                if (d.Allocation > 0)
                    continue;
                Assert.NotNull(AllocTelemetry.ZeroReason(d, offers[i]));
                Assert.Contains(" — ", LineFor(block, d.Label ?? d.Name));
            }

            Assert.Contains("gold stall", block);
            Assert.Contains("at capacity", block);
            Assert.Contains("target met", block);
        }

        // ---- the three zeros the plan does NOT record --------------------------------------------

        [Fact]
        public void A_seated_beard_at_zero_says_it_has_no_allocation_cost_rather_than_nothing()
        {
            var d = Seated("Beards", cap: 0, took: 0, noAlloc: true);
            var why = AllocTelemetry.ZeroReason(d, 0);

            Assert.NotNull(why);
            Assert.Contains("no allocation cost", why);
            // And the capacity column must not read "0", which is Pass 2's REFUSAL value — a beard
            // seats. Two different states must not print the same character.
            Assert.Contains("cap=none", AllocTelemetry.Render("Energy", PlanOf(100, d), new long[] { 0 }));
        }

        [Fact]
        public void A_seated_lane_whose_turn_came_after_the_pool_ran_dry_says_so()
        {
            // Non-rate lanes get NO skipReason from FillSession — Offer just returns min(cap, 0).
            // Without this synthesis the lane would print a bare "took=0".
            var d = Seated("NGU-6", cap: 200, took: 0);
            var why = AllocTelemetry.ZeroReason(d, offered: 0);

            Assert.NotNull(why);
            Assert.Contains("pool was exhausted by the lanes ahead of it", why);
        }

        [Fact]
        public void A_lane_offered_resource_whose_own_Allocate_took_none_of_it_says_so()
        {
            // THE LIVE-PATH GAP. A self-limiting lane is offered the whole remaining pool and
            // self-limits below it; when it self-limits to ZERO the plan records nothing at all —
            // no elimination, no skip reason — and every existing channel reads it as healthy.
            var d = Seated("BestAug", cap: ConstraintLayer.SelfLimiting, took: 0);
            var why = AllocTelemetry.ZeroReason(d, offered: 732767363921);

            Assert.NotNull(why);
            Assert.Contains("took none of it", why);
        }

        [Fact]
        public void A_refused_lane_with_no_recorded_reason_is_itself_reported_as_the_defect()
        {
            // Fail LOUD, not silent: a bare refusal is the exact omission spec §10 forbids, so the
            // dump must not paper over it with an empty string.
            var d = Refused("Mystery", ConstraintLayer.PassId.Budget, null);
            var why = AllocTelemetry.ZeroReason(d, 0);

            Assert.NotNull(why);
            Assert.Contains("pass 0 budget", why);
            Assert.Contains("NO recorded reason", why);
        }

        [Fact]
        public void A_funded_lane_has_no_zero_reason()
        {
            Assert.Null(AllocTelemetry.ZeroReason(Seated("AugmentBP", 300, 300), 300));
        }

        // ---- the founding defect ------------------------------------------------------------------

        [Fact]
        public void The_founding_defect_renders_raw_amounts_and_a_three_decimal_share()
        {
            // The live line that started this: CAPBestAug-0 taking 732,735,009,221 of
            // 732,767,363,921. NumberFormatter.Abbrev renders BOTH of those as "732.7G", which
            // destroys the discrimination that made the defect legible — hence raw longs here.
            var plan = PlanOf(732767363921,
                Seated("CAPBestAug-0", cap: ConstraintLayer.SelfLimiting, took: 732735009221),
                Seated("NGU-4", cap: ConstraintLayer.SelfLimiting, took: 0));
            plan.Unallocated = 32354700;
            plan.UnallocatedReason = "no surplus sink in this lane set";

            var block = AllocTelemetry.Render("Energy", plan, new long[] { 732767363921, 0 });

            Assert.Contains("732735009221", block);
            Assert.Contains("732767363921", block);
            Assert.DoesNotContain("732.7G", block);
            Assert.Contains("(99.996% of pool)", block);
            Assert.Contains("remainder=32354700 (0.004% of pool)", block);
            Assert.Contains("no surplus sink in this lane set", block);
        }

        [Fact]
        public void The_remainder_is_stated_even_when_it_is_zero()
        {
            // A remainder the reader has to derive by subtracting nine other numbers is a remainder
            // nobody checks.
            var plan = PlanOf(1000, Seated("AugmentBP", 1000, 1000));
            var block = AllocTelemetry.Render("Energy", plan, new long[] { 1000 });

            Assert.Contains("remainder=0", block);
            Assert.Contains("sink=absent", block);
        }

        [Fact]
        public void A_refused_sink_is_named_and_the_stranded_pool_surfaced()
        {
            var plan = PlanOf(1000,
                Seated("AugmentBP", 300, 300),
                Refused("WandoosBP", ConstraintLayer.PassId.Budget, "100LC: Wandoos consumes levels"));
            plan.Lanes[1].SurplusSink = true;
            plan.SinkIndex = 1;
            plan.SinkSeated = false;
            plan.Unallocated = 700;
            plan.UnallocatedReason = "surplus sink refused: 100LC: Wandoos consumes levels";

            var block = AllocTelemetry.Render("Energy", plan, new long[] { 300, 0 });

            Assert.Contains("sink=WandoosBP REFUSED", block);
            Assert.Contains("remainder=700 (70% of pool)", block);
            Assert.Contains("surplus sink refused", block);
        }

        [Fact]
        public void The_capacity_column_starts_at_the_same_offset_on_every_lane()
        {
            // The founding defect was found by EYE, scanning one tick's lanes for the one holding
            // the pool. Ragged columns cost exactly that, so the disposition field is padded to the
            // widest tag ConstraintParity.PassTag can produce.
            var plan = PlanOf(1000,
                Seated("A", 300, 300),
                Seated("B", 200, 200, rate: true),
                Refused("C", ConstraintLayer.PassId.Budget, "budget"),
                Refused("D", ConstraintLayer.PassId.Feasibility, "gold stall"),
                Refused("E", ConstraintLayer.PassId.Capacity, "at capacity"),
                Refused("F", ConstraintLayer.PassId.Target, "target met"));

            var block = AllocTelemetry.Render("Energy", plan, new long[] { 300, 200, 0, 0, 0, 0 });
            var offsets = block.Split('\n').Skip(1).Take(6)
                .Select(l => l.IndexOf("cap=")).Distinct().ToList();

            Assert.Single(offsets);
            Assert.True(offsets[0] > 0);
        }

        [Fact]
        public void Self_limiting_capacity_reads_as_self_never_as_minus_one()
        {
            var block = AllocTelemetry.Render("Energy",
                PlanOf(100, Seated("BestAug", ConstraintLayer.SelfLimiting, 100)), new long[] { 100 });

            Assert.Contains("cap=self", block);
            Assert.DoesNotContain("cap=-1", block);
        }

        [Fact]
        public void A_null_offers_array_prints_a_question_mark_rather_than_a_wrong_number()
        {
            // Offer and take are the SAME number on the Compose-side fill and DIFFERENT on the live
            // one. Defaulting the offer to the take would erase the only gap worth measuring.
            var block = AllocTelemetry.Render("Energy",
                PlanOf(100, Seated("AugmentBP", 50, 50)), null);

            Assert.Contains("offered=? took=50", block);
        }

        [Fact]
        public void A_null_plan_renders_nothing()
        {
            Assert.Null(AllocTelemetry.Render("Energy", null, null));
            Assert.Equal("", AllocTelemetry.Signature(null, null));
        }

        [Fact]
        public void The_rate_skip_and_budget_states_ride_along_in_the_same_block()
        {
            // They have their own state-change lines on the operator channel; repeating them here
            // means a reader never has to correlate two log files by timestamp.
            var plan = PlanOf(50, Seated("NGU-4", cap: 200, took: 0, rate: true,
                reason: "pool short: remaining 50 < capacity 200"));
            plan.BudgetMessage = "budget exhausted — 3 lanes idle";
            plan.RateLanesSkipped = 1;
            plan.RateSkipPool = 50;
            plan.RateSkipCheapest = 200;

            var block = AllocTelemetry.Render("Energy", plan, new long[] { 0 });

            Assert.Contains("budget: budget exhausted — 3 lanes idle", block);
            Assert.Contains("rate skips: 1 lane(s), pool 50 < cheapest capacity 200", block);
            Assert.Contains("pool short: remaining 50 < capacity 200", LineFor(block, "NGU-4"));
        }

        [Fact]
        public void Render_is_culture_invariant()
        {
            using (new CultureScope("de-DE"))
            {
                var block = AllocTelemetry.Render("Energy",
                    PlanOf(732767363921,
                        Seated("CAPBestAug-0", ConstraintLayer.SelfLimiting, 732735009221)),
                    new long[] { 732767363921 });

                Assert.Contains("99.996%", block);
                Assert.DoesNotContain("99,996", block);
            }
        }

        // ---- the signature -------------------------------------------------------------------------

        [Fact]
        public void The_signature_ignores_amounts_because_the_pool_grows_every_tick()
        {
            var a = AllocTelemetry.Signature(
                PlanOf(1000, Seated("AugmentBP", 300, 300)), new long[] { 300 });
            var b = AllocTelemetry.Signature(
                PlanOf(9999, Seated("AugmentBP", 400, 400)), new long[] { 400 });

            Assert.Equal(a, b);
        }

        [Fact]
        public void The_signature_changes_when_a_seated_lane_falls_to_zero()
        {
            // The founding failure mode is a lane that STAYS SEATED and stops being funded. If the
            // signature only tracked seats, the heartbeat would be the sole witness.
            var funded = AllocTelemetry.Signature(
                PlanOf(1000, Seated("NGU-4", 300, 300)), new long[] { 300 });
            var starved = AllocTelemetry.Signature(
                PlanOf(1000, Seated("NGU-4", 300, 0)), new long[] { 0 });

            Assert.NotEqual(funded, starved);
        }

        [Fact]
        public void The_signature_distinguishes_an_absent_sink_from_a_refused_one()
        {
            var absent = PlanOf(1000, Seated("AugmentBP", 300, 300));

            var refused = PlanOf(1000,
                Seated("AugmentBP", 300, 300),
                Refused("WandoosBP", ConstraintLayer.PassId.Budget, "100LC"));
            refused.SinkIndex = 1;

            var seatedSink = PlanOf(1000,
                Seated("AugmentBP", 300, 300),
                Seated("WandoosBP", ConstraintLayer.SelfLimiting, 700, sink: true));
            seatedSink.SinkIndex = 1;
            seatedSink.SinkSeated = true;

            var a = AllocTelemetry.Signature(absent, null);
            var r = AllocTelemetry.Signature(refused, null);
            var s = AllocTelemetry.Signature(seatedSink, null);

            Assert.Contains("#sink=absent", a);
            Assert.Contains("#sink=refused", r);
            Assert.Contains("#sink=seated", s);
            Assert.NotEqual(r, s);
        }

        [Fact]
        public void The_signature_is_order_insensitive_so_a_reordered_list_is_not_a_state_change()
        {
            var forward = AllocTelemetry.Signature(
                PlanOf(1000, Seated("AugmentBP", 300, 300), Seated("NGU-4", 200, 200)),
                new long[] { 300, 200 });
            var reversed = AllocTelemetry.Signature(
                PlanOf(1000, Seated("NGU-4", 200, 200), Seated("AugmentBP", 300, 300)),
                new long[] { 200, 300 });

            Assert.Equal(forward, reversed);
        }

        // ---- the throttle ---------------------------------------------------------------------------

        [Fact]
        public void An_unchanged_repeat_inside_the_window_is_suppressed()
        {
            Assert.False(AllocTelemetry.ShouldEmit("A", "A", secondsSinceLastEmit: 10));
            Assert.False(AllocTelemetry.ShouldEmit("A", "A", secondsSinceLastEmit: 30));
            Assert.False(AllocTelemetry.ShouldEmit("A", "A", secondsSinceLastEmit: 59.9));
        }

        [Fact]
        public void An_unchanged_signature_still_heartbeats_at_sixty_seconds()
        {
            // THE LOAD-BEARING HALF. The two-hour Evil-NGU zero (audit/25 §4) never changed state;
            // a change-only trigger is blind to it by construction. AllocDiagnostic's 60s cadence,
            // unchanged — this is the property being ported, not its prioCount/passes numbers.
            Assert.True(AllocTelemetry.ShouldEmit("A", "A", secondsSinceLastEmit: 60));
            Assert.True(AllocTelemetry.ShouldEmit("A", "A", secondsSinceLastEmit: 600));
        }

        [Fact]
        public void A_disposition_change_emits_on_the_next_allocator_tick()
        {
            // A flat 60s throttle shows one tick in six, so a lane that seats and refuses inside a
            // window would be invisible.
            Assert.True(AllocTelemetry.ShouldEmit("B", "A", secondsSinceLastEmit: 10));
        }

        [Fact]
        public void A_flapping_signature_cannot_beat_the_allocator_cadence()
        {
            Assert.False(AllocTelemetry.ShouldEmit("B", "A", secondsSinceLastEmit: 0));
            Assert.False(AllocTelemetry.ShouldEmit("B", "A", secondsSinceLastEmit: 9.9));
        }

        [Fact]
        public void The_first_tick_of_a_session_always_emits()
        {
            // The caller seeds lastEmit at DateTime.MinValue, so the very first plan of a session is
            // never swallowed by the floor — a startup misallocation is exactly when someone looks.
            Assert.True(AllocTelemetry.ShouldEmit("A", null, secondsSinceLastEmit: double.MaxValue));
        }

        [Fact]
        public void An_empty_signature_is_still_emitted_because_an_empty_plan_is_itself_a_state()
        {
            // Unlike ConstraintParity, which is silent when nothing diverges: here "no lanes" is a
            // finding, not an absence of one.
            Assert.True(AllocTelemetry.ShouldEmit("", "", secondsSinceLastEmit: 60));
        }
    }
}
