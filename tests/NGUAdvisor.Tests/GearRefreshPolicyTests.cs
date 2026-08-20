using Xunit;
using NGUAdvisor.Managers;
using Verdict = NGUAdvisor.Managers.GearRefreshPolicy.Verdict;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// The gear re-equip decision, and specifically the divergence that shipped: the manual button
    /// scored where the periodic pass switched.
    ///
    /// SCOPE, STATED HONESTLY. These tests cover the SHARED GUARD and what counts as a changed request.
    /// The two behavioural fixes are sequencing inside Unity-bound methods (commit the trackers only
    /// after the pass resolves; leave _lastGearCheck to the periodic pass) and no test here can observe
    /// them. They are verified by reading AdvisorApply.ForceGearReoptimize and by the new
    /// "gear already optimal … nothing re-equipped" line appearing in a live log.
    /// </summary>
    public class GearRefreshPolicyTests
    {
        // ---- the shared guard ----------------------------------------------------------------------

        /// <summary>
        /// The guard does NOT consult objectiveChanged, and that is deliberate. `best &lt;= worn` on a
        /// freshly picked objective means the worn set already IS that objective's best — the optimiser
        /// searched the whole inventory — so equipping would churn for nothing, and ChangeGear zeroes
        /// energy/magic/R3 allocation every time it runs. ApplyGearRefresh declines here too
        /// (AdvisorApply.cs:1601-1606); a first pass at this fix made the button diverge instead.
        /// </summary>
        [Theory]
        [InlineData(100.0, 99.9)]    // best marginally worse
        [InlineData(100.0, 50.0)]    // best much worse
        [InlineData(100.0, 100.0)]   // exactly equal — the boundary
        [InlineData(1e87, 1e86)]     // end-game magnitudes: the save this was found on
        public void The_worn_set_wins_when_it_out_scores_the_solved_one(double worn, double best)
        {
            Assert.Equal(Verdict.AlreadyOptimal, GearRefreshPolicy.Decide(worn, best, locksWorn: true));
        }

        [Theory]
        [InlineData(100.0, 100.01)]
        [InlineData(1e86, 1e87)]
        public void A_better_solved_set_equips(double worn, double best)
        {
            Assert.Equal(Verdict.Equip, GearRefreshPolicy.Decide(worn, best, locksWorn: true));
        }

        // ---- the guard's other conjuncts, each of which earns its place ------------------------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void An_unknown_worn_score_is_not_evidence_of_optimality(double worn)
        {
            // worn <= 0 means "we could not score what's on". Declining on that would leave gear
            // unexamined forever.
            Assert.Equal(Verdict.Equip, GearRefreshPolicy.Decide(worn, bestScore: 0.0, locksWorn: true));
        }

        [Fact]
        public void A_locked_set_that_is_not_worn_equips_even_though_it_scores_lower()
        {
            // A lock COSTS score by construction — it pins an item the optimiser would not have chosen.
            // Without this clause the button answers "already optimal" while wearing the unlocked set
            // and the user's explicit pin never goes on.
            Assert.Equal(Verdict.Equip, GearRefreshPolicy.Decide(wornScore: 100, bestScore: 80, locksWorn: false));
        }

        // ---- what counts as a changed request -------------------------------------------------------

        [Fact]
        public void Same_objective_and_same_locks_is_not_a_change()
        {
            Assert.False(GearRefreshPolicy.ObjectiveChanged("Wishes", "Wishes", "1,2", "1,2"));
        }

        [Fact]
        public void A_different_objective_name_is_a_change()
        {
            Assert.True(GearRefreshPolicy.ObjectiveChanged("Wishes", "NGUs", "", ""));
        }

        [Fact]
        public void A_changed_gear_lock_counts_as_a_changed_objective()
        {
            // Editing a profile to pin two items, objective name untouched, must still re-equip —
            // otherwise the locks never go on.
            Assert.True(GearRefreshPolicy.ObjectiveChanged("Wishes", "Wishes", "1,2", ""));
            Assert.True(GearRefreshPolicy.ObjectiveChanged("Wishes", "Wishes", "1,2", "1,3"));
            Assert.True(GearRefreshPolicy.ObjectiveChanged("Wishes", "Wishes", "", "1,2"));
        }

        [Fact]
        public void The_first_pass_of_a_session_is_a_change()
        {
            // _lastGearObjective starts null; that must read as "changed" so the first pass asserts gear.
            Assert.True(GearRefreshPolicy.ObjectiveChanged("Wishes", null, "", ""));
        }

        [Fact]
        public void Objective_names_are_compared_case_sensitively_and_exactly()
        {
            // Names come from a fixed catalog and from hand-edited profile JSON. Ordinal comparison is
            // what both call sites did before extraction; asserted so a "helpful" case-insensitive
            // rewrite has to argue with a test.
            Assert.True(GearRefreshPolicy.ObjectiveChanged("Wishes", "wishes", "", ""));
        }

        [Fact]
        public void Null_and_empty_are_the_same_absent_value()
        {
            Assert.False(GearRefreshPolicy.ObjectiveChanged(null, "", "", null));
            Assert.False(GearRefreshPolicy.ObjectiveChanged("", null, null, ""));
        }

        // ---- lock keys -------------------------------------------------------------------------------

        [Fact]
        public void LockKey_of_nothing_is_empty()
        {
            Assert.Equal("", GearRefreshPolicy.LockKey(null));
            Assert.Equal("", GearRefreshPolicy.LockKey(new int[0]));
        }

        [Fact]
        public void LockKey_joins_ids_in_the_authored_order()
        {
            Assert.Equal("1,2,3", GearRefreshPolicy.LockKey(new[] { 1, 2, 3 }));
            // NOT sorted: a reordered profile row reads as a change, which is the conservative direction.
            Assert.NotEqual(GearRefreshPolicy.LockKey(new[] { 1, 2, 3 }),
                            GearRefreshPolicy.LockKey(new[] { 3, 2, 1 }));
        }

        [Fact]
        public void LockKey_matches_the_expression_both_call_sites_used_before_extraction()
        {
            // Guards the extraction itself: AdvisorApply built this string inline in two places, and any
            // change to the format would silently read as "locks changed" on the first pass after a
            // deploy, re-equipping everyone's gear once for no reason.
            var ids = new[] { 415, 468, 461 };
            Assert.Equal("415,468,461", GearRefreshPolicy.LockKey(ids));
        }
    }
}
