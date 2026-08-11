using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE KILL SWITCH'S TESTABLE HALF + PARITY LOGGING. Flag off runs the ORIGINAL compiled loop in
    // Energy/MagicBreakpoints.PerformSwap — that code is Unity-welded and untouched, so what CAN be
    // pinned headlessly is the arithmetic it performs: LegacyShareModel is the byte-for-byte replay
    // of ResourceBreakpoint.UpdateMaxAllocation (:71-79) and the walk around it, characterised here
    // INCLUDING its known-wrong parts (the divisor inflation, LAW 1b's last-lane windfall), so the
    // parity log's counterfactual is the real old model and a drift in either is a visible test
    // failure. Then the parity comparison itself: computed from both allocations, changing neither.
    public class ConstraintParityTests
    {
        private static LegacyShareModel.LaneInput NonCap(string label, long need) =>
            new LegacyShareModel.LaneInput { Label = label, Need = need };

        private static LegacyShareModel.LaneInput Cap(string label, long need,
            double? percent = null) =>
            new LegacyShareModel.LaneInput
            {
                Label = label,
                IsCap = true,
                Need = need,
                HasPercent = percent.HasValue,
                Percent = percent ?? 0,
            };

        // ---- LegacyShareModel characterises the old arithmetic exactly ---------------------------

        [Fact]
        public void The_prioCount_divisor_splits_idle_across_non_cap_lanes()
        {
            var shares = LegacyShareModel.Simulate(900, 900, new[]
            {
                NonCap("A", need: long.MaxValue),
                NonCap("B", need: long.MaxValue),
                NonCap("C", need: long.MaxValue),
            });

            // 900/3 = 300; B then sees idle 600 / prioCount 2 = 300; C sees 300/1 = 300 (LAW 1b:
            // the LAST non-cap lane gets idle/1 — everything left).
            Assert.Equal(300, shares[0].Take);
            Assert.Equal(300, shares[1].Take);
            Assert.Equal(300, shares[2].Take);
        }

        [Fact]
        public void The_remainder_sign_term_rounds_the_share_up_by_one()
        {
            var shares = LegacyShareModel.Simulate(10, 10, new[]
            {
                NonCap("A", need: long.MaxValue),
                NonCap("B", need: long.MaxValue),
                NonCap("C", need: long.MaxValue),
            });

            // 10/3 + Sign(10%3) = 3+1 = 4; then 6/2 + Sign(0) = 3; then 3/1 = 3.
            Assert.Equal(4, shares[0].Take);
            Assert.Equal(3, shares[1].Take);
            Assert.Equal(3, shares[2].Take);
        }

        [Fact]
        public void An_infeasible_lane_with_a_seat_inflates_the_divisor_for_everyone_the_RIT7_defect()
        {
            // The old model's founding defect, reproduced on purpose: a lane that will take nothing
            // (gold-stalled, boss-locked) still counts in prioCount, so every other lane's share
            // shrinks. The constraint layer refuses such a lane a seat; the old model cannot.
            var with = LegacyShareModel.Simulate(900, 900, new[]
            {
                NonCap("NGU-0", need: long.MaxValue),
                NonCap("RIT-7", need: 0),              // seated, allocates nothing
                NonCap("NGU-1", need: long.MaxValue),
            });
            var without = LegacyShareModel.Simulate(900, 900, new[]
            {
                NonCap("NGU-0", need: long.MaxValue),
                NonCap("NGU-1", need: long.MaxValue),
            });

            Assert.Equal(300, with[0].Take);     // 900/3 — the dead seat cost NGU-0 a third
            Assert.Equal(450, without[0].Take);  // 900/2 — without it
            Assert.Equal(0, with[1].Take);
        }

        [Fact]
        public void A_cap_lane_without_percent_is_an_absorber_offered_everything_left()
        {
            var shares = LegacyShareModel.Simulate(1000, 5000, new[]
            {
                NonCap("A", need: 100),
                Cap("CAPNGU-3", need: long.MaxValue),
            });

            // A takes min(need 100, 1000/1 + 0) = 100; the cap lane's capMax = cur 5000, offer =
            // min(5000, idle 900) = 900 — the whole remainder, exactly the absorber behaviour.
            Assert.Equal(100, shares[0].Take);
            Assert.Equal(900, shares[1].Take);
        }

        [Fact]
        public void Percent_applies_to_cur_for_cap_lanes_and_to_idle_for_non_cap_and_skips_the_divisor()
        {
            var shares = LegacyShareModel.Simulate(1000, 4000, new[]
            {
                Cap("CAPWAN:25", need: long.MaxValue, percent: 0.25),   // ceil(4000*0.25) = 1000 of CUR
                NonCap("TM:10", need: long.MaxValue),                    // in the divisor
            });
            Assert.Equal(1000, shares[0].Take);   // min(1000, idle 1000)
            Assert.Equal(0, shares[1].Take);      // nothing left

            var pct = LegacyShareModel.Simulate(1000, 4000, new[]
            {
                new LegacyShareModel.LaneInput
                {
                    Label = "NGU:40", Need = long.MaxValue, HasPercent = true, Percent = 0.40,
                },
                NonCap("B", need: long.MaxValue),
            });
            // Non-cap WITH percent: ceil(idle 1000 * 0.40) = 400, and it does NOT divide by
            // prioCount (the percent branch runs instead of the divisor branch).
            Assert.Equal(400, pct[0].Take);
            Assert.Equal(600, pct[1].Take);       // last non-cap: idle/1
        }

        [Fact]
        public void Golden_walk_a_marathon_shaped_token_list_through_the_old_arithmetic()
        {
            // The NGU MARATHON program's shape (ChallengeOverlay.AutoTokens): percent caps first,
            // two hot non-cap NGU lanes, an absorber last. This is the allocation the kill switch's
            // OFF position produces — pinned end to end so any drift in the counterfactual (or an
            // accidental "fix" to the old path while it waits behind the flag) is a visible failure.
            var shares = LegacyShareModel.Simulate(10_000, 20_000, new[]
            {
                Cap("CAPTM:5", need: 400, percent: 0.05),        // ceil(20000*.05)=1000 of CUR
                Cap("CAPWAN:60", need: 2_000, percent: 0.60),    // ceil(20000*.60)=12000, idle-bounded
                NonCap("NGU-0", need: long.MaxValue),            // 9600? no — idle 7600 / prioCount 2
                NonCap("NGU-4", need: long.MaxValue),            // then idle/1 — LAW 1b
                Cap("CAPNGU-6", need: long.MaxValue),            // absorber: whatever is left
            });

            Assert.Equal(1_000, shares[0].Offer);
            Assert.Equal(400, shares[0].Take);
            Assert.Equal(9_600, shares[1].Offer);
            Assert.Equal(2_000, shares[1].Take);
            Assert.Equal(3_800, shares[2].Take);   // 7600/2 + Sign(0)
            Assert.Equal(3_800, shares[3].Take);   // 3800/1 — the last non-cap drinks the rest
            Assert.Equal(0, shares[4].Take);       // nothing left for the absorber
        }

        // ---- the parity comparison ---------------------------------------------------------------

        private static ConstraintLayer.Plan PlanFor(long pool,
            params ConstraintLayer.LaneSpec[] lanes) =>
            ConstraintLayer.Compose(pool,
                new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 },
                new List<ConstraintLayer.LaneSpec>(lanes));

        [Fact]
        public void A_divergence_is_recorded_with_the_causing_pass_and_both_amounts_without_changing_the_allocation()
        {
            // A gold-stalled TM lane: the audit's predicted divergence class — zero under the new
            // path where the old path handed it a reduced share.
            var plan = PlanFor(900,
                new ConstraintLayer.LaneSpec
                {
                    Name = "TimeMachineBP",
                    Feasibility = FeasibilityPass.Verdict.Refuse("gold stall: bar unstarted and realGold 0 < cost 5"),
                    Capacity = 500, WantsMore = true,
                },
                new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP", Label = "NGU-0",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 600, WantsMore = true,
                },
                new ConstraintLayer.LaneSpec
                {
                    Name = "WandoosBP",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = ConstraintLayer.SelfLimiting, WantsMore = true, SurplusSink = true,
                });

            var newTakes = new long[] { 0, 600, 300 };
            var oldShares = LegacyShareModel.Simulate(900, 900, new[]
            {
                NonCap("TimeMachineBP", need: 500),
                NonCap("NGU-0", need: 600),
                Cap("WandoosBP", need: long.MaxValue),
            });

            var before = plan.Lanes.Select(l => l.Allocation).ToArray();
            var diffs = ConstraintParity.Compare(plan, newTakes, oldShares);
            var after = plan.Lanes.Select(l => l.Allocation).ToArray();

            // Read-only: comparing changed nothing.
            Assert.Equal(before, after);

            // Old: TM takes min(500, 900/2+0=450) = 450; NGU takes min(600, 450/1) = 450; wandoos
            // absorbs 0. New: TM 0 (pass 1), NGU 600, sink 300. All three diverge.
            var tm = diffs.Single(d => d.Label == "TimeMachineBP");
            Assert.Equal(ConstraintLayer.PassId.Feasibility, tm.Pass);
            Assert.Equal(450, tm.OldAmount);
            Assert.Equal(0, tm.NewAmount);
            Assert.Contains("gold stall", tm.Reason);

            var ngu = diffs.Single(d => d.Label == "NGU-0");
            Assert.Equal(ConstraintLayer.PassId.None, ngu.Pass);   // both seated — share model only
            Assert.Equal(450, ngu.OldAmount);
            Assert.Equal(600, ngu.NewAmount);

            var sink = diffs.Single(d => d.Label == "WandoosBP");
            Assert.Equal(0, sink.OldAmount);
            Assert.Equal(300, sink.NewAmount);
        }

        [Fact]
        public void Equal_takes_produce_no_entry()
        {
            var plan = PlanFor(1000,
                new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP", Label = "NGU-0",
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 100, WantsMore = true,
                });

            var diffs = ConstraintParity.Compare(plan,
                new long[] { 100 },
                LegacyShareModel.Simulate(1000, 1000, new[] { NonCap("NGU-0", need: 100) }));

            Assert.Empty(diffs);
        }

        // ---- the throttle ------------------------------------------------------------------------

        [Fact]
        public void Signature_tracks_which_lanes_diverge_and_why_not_by_how_much()
        {
            var a = new List<ConstraintParity.Divergence>
            {
                new ConstraintParity.Divergence { Label = "TM", Pass = ConstraintLayer.PassId.Feasibility, OldAmount = 450, NewAmount = 0 },
                new ConstraintParity.Divergence { Label = "NGU-0", Pass = ConstraintLayer.PassId.None, OldAmount = 450, NewAmount = 600 },
            };
            var b = new List<ConstraintParity.Divergence>
            {
                // Same lanes, same passes, different amounts — same signature.
                new ConstraintParity.Divergence { Label = "NGU-0", Pass = ConstraintLayer.PassId.None, OldAmount = 1, NewAmount = 2 },
                new ConstraintParity.Divergence { Label = "TM", Pass = ConstraintLayer.PassId.Feasibility, OldAmount = 9, NewAmount = 0 },
            };
            var c = new List<ConstraintParity.Divergence>
            {
                new ConstraintParity.Divergence { Label = "TM", Pass = ConstraintLayer.PassId.Budget, OldAmount = 450, NewAmount = 0 },
            };

            Assert.Equal(ConstraintParity.Signature(a), ConstraintParity.Signature(b));
            Assert.NotEqual(ConstraintParity.Signature(a), ConstraintParity.Signature(c));
            Assert.Equal("", ConstraintParity.Signature(new List<ConstraintParity.Divergence>()));
        }

        [Fact]
        public void Emission_fires_on_signature_change_respects_the_floor_and_refreshes_slowly()
        {
            // Change → emit (past the floor).
            Assert.True(ConstraintParity.ShouldEmit("a@x", "", 31));
            // Change inside the 30s floor → suppressed (fires on a later tick).
            Assert.False(ConstraintParity.ShouldEmit("a@x", "", 5));
            // Unchanged → silent until the slow refresh.
            Assert.False(ConstraintParity.ShouldEmit("a@x", "a@x", 300));
            Assert.True(ConstraintParity.ShouldEmit("a@x", "a@x", 600));
            // No divergence → nothing to say, ever.
            Assert.False(ConstraintParity.ShouldEmit("", "a@x", 10_000));
        }

        [Fact]
        public void The_formatted_block_names_the_pool_each_lane_both_amounts_and_the_pass()
        {
            var text = ConstraintParity.Format("Energy", new List<ConstraintParity.Divergence>
            {
                new ConstraintParity.Divergence
                {
                    Label = "TM", Pass = ConstraintLayer.PassId.Feasibility,
                    OldAmount = 450, NewAmount = 0, Reason = "gold stall",
                },
            });

            Assert.Contains("Energy", text);
            Assert.Contains("TM", text);
            Assert.Contains("pass 1 feasibility", text);
            Assert.Contains("gold stall", text);
            Assert.Contains("expected", text);   // divergence is expected; the log must say so
        }
    }
}
