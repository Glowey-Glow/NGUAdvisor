using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The 100 Level Challenge's no-beards rule. Every assertion here is a regression: the rule was
    // written only as data (CBlock3.1-E100LC.json's empty "Beards" list) and was unenforceable in the
    // configuration most runs use, because CustomAllocation.cs:239 runs the profile beard timeline
    // only while the beards advisor is OFF.
    public class BeardRuleTests
    {
        // ChallengeDetector.Current()'s complete vocabulary, plus null for "not in a challenge".
        private static readonly string[] AllCodes =
        {
            "BASIC", "NOAUG", "24HR", "100LC", "NOEC", "TC", "NORB", "LSC", "BLIND", "NONGU", "NOTM"
        };

        [Fact]
        public void Only_the_100_level_challenge_forbids_beards()
        {
            Assert.True(BeardRule.Forbidden("100LC"));

            foreach (var code in AllCodes.Where(c => c != "100LC"))
                Assert.False(BeardRule.Forbidden(code));
        }

        // Not being in a challenge is not a rule. Beards outside a challenge are ordinary.
        [Fact]
        public void No_challenge_does_not_forbid_beards()
        {
            Assert.False(BeardRule.Forbidden(null));
            Assert.False(BeardRule.Forbidden(""));
        }

        // THE LOAD-BEARING INVARIANT. AdvisorApply.ApplyBeards returns early on a null set ("no
        // opinion"), so if None were null the rule would ABSTAIN rather than clear — and abstaining
        // leaves the beards equipped before the challenge started on for its whole duration, which is
        // the exact failure this fixes. None must be a real, empty array.
        [Fact]
        public void None_is_an_empty_array_and_never_null()
        {
            Assert.NotNull(BeardRule.None);
            Assert.Empty(BeardRule.None);
        }

        // The observed failure, replayed: OptimizationAdvisor's fill-every-slot branch produced
        // 5,1,3,0,2,4,6 and it was equipped seven minutes into a 100LC run.
        [Fact]
        public void The_full_advisor_set_is_reduced_to_none_under_the_rule()
        {
            var wanted = new[] { 5, 1, 3, 0, 2, 4, 6 };

            var got = BeardRule.Apply("100LC", wanted);

            Assert.Empty(got);
        }

        [Fact]
        public void Apply_passes_the_wanted_set_through_outside_the_rule()
        {
            var wanted = new[] { 0, 4, 2, 6, 5 };   // CBlock3.2-E's real list

            foreach (var code in AllCodes.Where(c => c != "100LC"))
                Assert.Equal(wanted, BeardRule.Apply(code, wanted));

            Assert.Equal(wanted, BeardRule.Apply(null, wanted));
        }

        // A profile that already asks for none must survive the rule unchanged rather than becoming
        // a null "no opinion" — the empty list is the instruction, not the absence of one.
        [Fact]
        public void An_already_empty_set_stays_empty_and_non_null_either_way()
        {
            Assert.NotNull(BeardRule.Apply("100LC", new int[0]));
            Assert.Empty(BeardRule.Apply("100LC", new int[0]));

            Assert.NotNull(BeardRule.Apply("BASIC", new int[0]));
            Assert.Empty(BeardRule.Apply("BASIC", new int[0]));
        }

        // BeardManager.EquipBeards is the backstop and is reached with a null set by both restore
        // paths (RestoreBeards/RestoreTempBeards before anything has been saved). Under the rule that
        // must still resolve to a clear, not throw.
        [Fact]
        public void A_null_wanted_set_becomes_none_under_the_rule_and_stays_null_outside_it()
        {
            Assert.Empty(BeardRule.Apply("100LC", null));
            Assert.Null(BeardRule.Apply("BASIC", null));
        }
    }
}
