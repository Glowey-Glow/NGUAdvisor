using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // PASS 0 — budget exhaustion (constraint-layer-spec §3; 21 §A1-A3; amendment 17 §2-4). The rule
    // tests, then the allowlist AS DATA asserted against the nine canLevel() sites so the table
    // cannot drift from the derivation, then the beard temp/perm split — the one system on both
    // lists.
    public class BudgetPassTests
    {
        private static BudgetPass.BudgetState State(bool inChallenge, long rebirthLevels) =>
            new BudgetPass.BudgetState { InLevelChallenge = inChallenge, RebirthLevels = rebirthLevels };

        // Every advisor lane the allowlist names, plus the exempt lanes of CapacityPass.Table /
        // LaneTargets.Table. "Beards" is the temp-level claimant (BeardGate); perm levels are not a
        // lane at all, which is its own test below.
        private static readonly string[] CountingLanes =
        {
            "AugmentBP", "BestAug", "RitualBP", "BR", "TimeMachineBP", "WandoosBP", "Beards",
            "AdvancedTrainingBP",
        };

        private static readonly string[] ExemptLanes =
        {
            "NGUBP", "BasicTrainingBP", "HackBP", "Wishes",
        };

        // ---- the rule (Character.cs:2178-2185) ---------------------------------------------------

        // Not in challenge => no lane is budget-refused, whatever rebirthLevels reads — out of a
        // challenge the counter runs unbounded and means nothing.
        [Theory]
        [InlineData(0L)]
        [InlineData(99L)]
        [InlineData(100L)]
        [InlineData(1_000_000L)]
        public void Out_of_challenge_no_lane_is_budget_refused_at_any_counter_value(long rebirthLevels)
        {
            var s = State(inChallenge: false, rebirthLevels);

            Assert.False(BudgetPass.Exhausted(s));
            foreach (var lane in CountingLanes.Concat(ExemptLanes))
                Assert.True(BudgetPass.Evaluate(lane, s).Seated);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(50L)]
        [InlineData(99L)]
        public void In_challenge_under_100_no_lane_is_budget_refused(long rebirthLevels)
        {
            var s = State(inChallenge: true, rebirthLevels);

            Assert.False(BudgetPass.Exhausted(s));
            foreach (var lane in CountingLanes.Concat(ExemptLanes))
                Assert.True(BudgetPass.Evaluate(lane, s).Seated);
        }

        // At the cap, every allowlist lane refuses WITH a reason, and every exempt lane is
        // untouched. The counter is not clamped, so > 100 must behave identically to == 100 —
        // the game's own comparator is >=, not == (Character.cs:2180).
        [Theory]
        [InlineData(100L)]
        [InlineData(101L)]
        [InlineData(999L)]
        public void At_or_past_the_cap_every_allowlist_lane_refuses_and_every_exempt_lane_seats(long rebirthLevels)
        {
            var s = State(inChallenge: true, rebirthLevels);

            Assert.True(BudgetPass.Exhausted(s));
            foreach (var lane in CountingLanes)
            {
                var v = BudgetPass.Evaluate(lane, s);
                Assert.False(v.Seated);
                Assert.Contains("budget exhausted", v.Reason);
            }
            foreach (var lane in ExemptLanes)
                Assert.True(BudgetPass.Evaluate(lane, s).Seated);
        }

        // The game's default: a system that never consults canLevel() is exempt BY OMISSION, so a
        // lane this table has never heard of must pass through untouched — the allowlist posture the
        // spec demands. A blacklist model would refuse it here, and this test is what would catch
        // that regression.
        [Fact]
        public void An_unknown_future_lane_is_exempt_by_default()
        {
            var s = State(inChallenge: true, 100L);

            Assert.False(BudgetPass.Counts("SomeFutureLane"));
            Assert.True(BudgetPass.Evaluate("SomeFutureLane", s).Seated);
            Assert.True(BudgetPass.Evaluate(null, s).Seated);
        }

        // The blood refusal must carry the worst-of-the-nine fact: bloodPoints gain is inside the
        // gate (BloodMagicController.cs:72), so at the cap the ritual charges gold and produces zero
        // blood. The reason is the surfacing feed — it has to say so.
        [Fact]
        public void The_blood_refusal_names_the_gold_burn_and_the_stopped_blood_generation()
        {
            var s = State(inChallenge: true, 100L);

            foreach (var lane in new[] { "RitualBP", "BR" })
            {
                var v = BudgetPass.Evaluate(lane, s);
                Assert.False(v.Seated);
                Assert.Contains("currentCost() gold", v.Reason);
                Assert.Contains("bloodPoints generation stops", v.Reason);
            }
        }

        [Fact]
        public void Gold_charging_lanes_name_their_burn_and_pure_bar_lanes_do_not_claim_one()
        {
            var s = State(inChallenge: true, 100L);

            Assert.Contains("gold", BudgetPass.Evaluate("TimeMachineBP", s).Reason);
            Assert.Contains("gold", BudgetPass.Evaluate("AugmentBP", s).Reason);
            Assert.Contains("gold", BudgetPass.Evaluate("BestAug", s).Reason);
            // Wandoos, beards and AT burn no second resource — their refusals must not invent one.
            Assert.DoesNotContain("burns", BudgetPass.Evaluate("WandoosBP", s).Reason);
            Assert.DoesNotContain("burns", BudgetPass.Evaluate("Beards", s).Reason);
            Assert.DoesNotContain("burns", BudgetPass.Evaluate("AdvancedTrainingBP", s).Reason);
        }

        // ---- levels remaining (the dead helper's replacement) ------------------------------------

        // Character.levelsRemaining() is dead and defective — Math.Max(1L, …) reports 1 at 100/100.
        // The replacement must reach the 0 the game's version cannot, and floor there for the
        // unclamped counter.
        [Theory]
        [InlineData(0L, 100L)]
        [InlineData(99L, 1L)]
        [InlineData(100L, 0L)]
        [InlineData(150L, 0L)]
        public void LevelsRemaining_reaches_zero_and_floors_there(long rebirthLevels, long expected)
        {
            Assert.Equal(expected, BudgetPass.LevelsRemaining(rebirthLevels));
        }

        // ---- surfacing (spec §3.4, decision D2(b)) -----------------------------------------------

        [Fact]
        public void The_surface_message_names_the_idle_lane_count_and_the_consumed_budget()
        {
            var msg = BudgetPass.SurfaceMessage(rebirthLevels: 100, refusedLaneCount: 8);

            Assert.Equal("budget exhausted — 8 lanes idle (100/100 levels consumed), allocation directed to exempt systems", msg);
        }

        // A Pass 0 refusal filed into the roster surfaces exactly like a Pass 1 refusal — same
        // Verdict, same Refusals feed, no seat, no divisor inflation. That is the SeatRoster pattern
        // the spec tells Pass 0 to follow.
        [Fact]
        public void A_budget_refusal_files_into_the_roster_without_a_seat()
        {
            var s = State(inChallenge: true, 100L);
            var roster = new SeatRoster();
            roster.Add("WandoosBP", BudgetPass.Evaluate("WandoosBP", s));
            roster.Add("NGUBP", BudgetPass.Evaluate("NGUBP", s));

            Assert.Equal(1, roster.SeatCount);
            Assert.Contains("NGUBP", roster.Seated);
            var refusal = Assert.Single(roster.Refusals);
            Assert.Equal("WandoosBP", refusal.Lane);
            Assert.Contains("budget exhausted", refusal.Reason);
        }

        // ---- the allowlist as data, asserted against the nine sites (21 §A1) ---------------------
        //
        // The re-derivation procedure's fixture: grep canLevel() in decomp-full yields one
        // definition (Character.cs:2178) and exactly these nine call sites. A game build that moves,
        // adds or removes a site must fail HERE, not drift.

        private static readonly (string File, int GateLine, int IncrementLine)[] DerivedSites =
        {
            ("AugmentController.cs", 245, 248),
            ("AugmentController.cs", 285, 288),
            ("BloodMagicController.cs", 69, 73),
            ("TimeMachineController.cs", 354, 357),
            ("TimeMachineController.cs", 397, 400),
            ("Wandoos98Controller.cs", 277, 280),
            ("Wandoos98Controller.cs", 460, 463),
            ("AllBeardsController.cs", 200, 203),
            ("AdvancedTrainingController.cs", 165, 168),
        };

        [Fact]
        public void The_allowlist_matches_the_nine_derived_sites_exactly()
        {
            Assert.Equal(9, BudgetPass.Allowlist.Length);
            Assert.Equal(
                DerivedSites.OrderBy(x => x.File).ThenBy(x => x.GateLine),
                BudgetPass.Allowlist.Select(r => (r.File, r.GateLine, r.IncrementLine))
                    .OrderBy(x => x.File).ThenBy(x => x.GateLine));
        }

        // Nine sites, SIX systems — the count the in-game tooltip gets wrong twice over: the
        // Description whitelists five (omitting Advanced Training), the Restrictions line
        // blacklists two. The code counts six, and the table must agree with the code.
        [Fact]
        public void Nine_sites_resolve_to_six_systems_including_advanced_training()
        {
            var systems = BudgetPass.Allowlist.Select(r => r.System).Distinct().OrderBy(x => x).ToArray();

            Assert.Equal(new[]
            {
                "Advanced Training", "Augments", "Beards", "Blood Magic", "Time Machine", "Wandoos",
            }, systems);
        }

        [Fact]
        public void Every_site_is_distinct_and_every_row_names_at_least_one_advisor_lane()
        {
            Assert.Equal(9, BudgetPass.Allowlist.Select(r => r.Site).Distinct().Count());
            Assert.All(BudgetPass.Allowlist, r => Assert.NotEmpty(r.AdvisorLanes));
        }

        // Counts() is derived from the table — the exact lane union, nothing more. A lane the table
        // does not name must not count, or the omission model breaks.
        [Fact]
        public void Lane_membership_is_exactly_the_tables_lane_union()
        {
            var union = BudgetPass.Allowlist.SelectMany(r => r.AdvisorLanes).Distinct().OrderBy(x => x).ToArray();

            Assert.Equal(CountingLanes.OrderBy(x => x), union);
            foreach (var lane in union)
                Assert.True(BudgetPass.Counts(lane));
            foreach (var lane in ExemptLanes)
                Assert.False(BudgetPass.Counts(lane));
        }

        // ---- beards: temp counts, perm does not (spec §6) ----------------------------------------

        // The one system on both lists. The TEMP level is site 8 of 9 (beardLevel++ behind
        // canLevel(), AllBeardsController.cs:200-203); the PERM level is written by
        // convertToTrimmings() with no gate (:294-298) and must appear only as the row's
        // non-counting twin — never as a site, never as a counting lane.
        [Fact]
        public void Beard_temp_level_counts_and_perm_level_does_not()
        {
            var beardRow = Assert.Single(BudgetPass.Allowlist,
                r => r.Site == BudgetPass.CountingSite.BeardTemp);

            Assert.Equal("Beards", beardRow.System);
            Assert.Equal(new[] { "Beards" }, beardRow.AdvisorLanes);
            Assert.Contains("perm levels", beardRow.NonCountingTwin);
            Assert.Contains("no canLevel()", beardRow.NonCountingTwin);

            // Perm is not a lane, not a site, not a member — the temp half is the system's whole
            // presence on the allowlist.
            Assert.False(BudgetPass.Counts("BeardsPerm"));
            Assert.Single(BudgetPass.Allowlist, r => r.System == "Beards");

            // And the refusal itself lands on the temp claimant.
            var v = BudgetPass.Evaluate("Beards", State(inChallenge: true, 100L));
            Assert.False(v.Seated);
        }
    }
}
