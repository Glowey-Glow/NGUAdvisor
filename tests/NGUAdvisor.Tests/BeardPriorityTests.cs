using System.Linq;
using Xunit;
using NGUAdvisor.Managers;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// The beard slot order [OPERATOR ruling 2026-08-18]:
    ///
    ///   BEARd > Neckbeard > Beard Cage > (Reverse / LadyBeard, to the 1000 perm softcap)
    ///         > Golden Beard (once TC7) > Fu Manchu > Reverse > LadyBeard
    ///
    /// Reverse and LadyBeard appearing twice is the rule's shape, not a typo — a bounded push while
    /// they are under the softcap, an unbounded tail once they are past it.
    /// </summary>
    public class BeardPriorityTests
    {
        private const long Under = 999;
        private const long At = 1000;      // the next level is 1001 — already past the break
        private const long Over = 5000;

        [Fact]
        public void The_ruling_reads_back_exactly_when_both_are_under_the_softcap()
        {
            Assert.Equal(
                new[] { BeardPriority.Bear, BeardPriority.Neckbeard, BeardPriority.BeardCage,
                        BeardPriority.Reverse, BeardPriority.Lady,
                        BeardPriority.Golden, BeardPriority.FuManchu },
                BeardPriority.Order(Under, Under, goldenUnlocked: true));
        }

        [Fact]
        public void Past_the_softcap_the_pair_falls_to_the_tail_behind_Fu_Manchu()
        {
            Assert.Equal(
                new[] { BeardPriority.Bear, BeardPriority.Neckbeard, BeardPriority.BeardCage,
                        BeardPriority.Golden, BeardPriority.FuManchu,
                        BeardPriority.Reverse, BeardPriority.Lady },
                BeardPriority.Order(Over, Over, goldenUnlocked: true));
        }

        [Fact]
        public void The_pair_is_judged_independently()
        {
            // Reverse still climbing, Lady already saturated.
            Assert.Equal(
                new[] { BeardPriority.Bear, BeardPriority.Neckbeard, BeardPriority.BeardCage,
                        BeardPriority.Reverse, BeardPriority.Golden, BeardPriority.FuManchu,
                        BeardPriority.Lady },
                BeardPriority.Order(Under, Over, goldenUnlocked: true));
        }

        [Fact]
        public void Exactly_1000_is_already_past_it()
        {
            // The break is `permLevel > 1000`, so at 1000 the NEXT level is 1001 and is bought at
            // sqrt rates. Pushing here would be buying the far side of the curve.
            Assert.False(BeardPriority.UnderSoftcap(At));
            Assert.True(BeardPriority.UnderSoftcap(At - 1));

            var o = BeardPriority.Order(At, At, goldenUnlocked: true);
            Assert.Equal(BeardPriority.FuManchu, o[4]);          // Fu Manchu promoted above the pair
            Assert.True(System.Array.IndexOf(o, BeardPriority.Reverse) > 4);
        }

        // ── THE TWO GATES ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Golden_is_omitted_entirely_before_TC7_not_demoted()
        {
            // It cannot be activated at all before Troll Challenge 7, so leaving it in the list would
            // silently spend a slot on nothing.
            var o = BeardPriority.Order(Under, Under, goldenUnlocked: false);
            Assert.DoesNotContain(BeardPriority.Golden, o);
            Assert.Equal(6, o.Length);
            Assert.Equal(BeardPriority.FuManchu, o[5]);
        }

        [Fact]
        public void Every_beard_appears_exactly_once_in_every_configuration()
        {
            foreach (var rev in new[] { 0L, Under, At, Over })
                foreach (var lady in new[] { 0L, Under, At, Over })
                    foreach (var gold in new[] { true, false })
                    {
                        var o = BeardPriority.Order(rev, lady, gold);
                        Assert.Equal(o.Length, o.Distinct().Count());
                        Assert.Equal(gold ? 7 : 6, o.Length);
                        Assert.Contains(BeardPriority.Reverse, o);
                        Assert.Contains(BeardPriority.Lady, o);
                    }
        }

        // ── WHAT A LIMITED SLOT COUNT ACTUALLY GETS ──────────────────────────────────────────────

        [Theory]
        [InlineData(1, new[] { 5 })]
        [InlineData(2, new[] { 5, 1 })]
        [InlineData(3, new[] { 5, 1, 3 })]
        [InlineData(4, new[] { 5, 1, 3, 2 })]
        [InlineData(5, new[] { 5, 1, 3, 2, 4 })]
        public void A_fresh_account_fills_its_slots_in_the_ruling_order(int slots, int[] expected)
        {
            // permLevel 0 on a new account, Golden locked — the common case this ruling is for.
            Assert.Equal(expected, BeardPriority.Order(0, 0, goldenUnlocked: false).Take(slots));
        }

        [Fact]
        public void The_first_three_are_the_energy_beards_and_that_is_the_ruling_not_an_accident()
        {
            // 5 BEARd, 1 Neckbeard, 3 Beard Cage are all ENERGY beards; beardCountDivider divides
            // every beard on a resource by how many are active on it, so the first three slots put
            // the whole divider on energy and leave magic untouched. Recorded because it is a real
            // consequence of the order, not because the order is wrong.
            var o = BeardPriority.Order(0, 0, goldenUnlocked: false);
            Assert.Equal(new[] { BeardPriority.Bear, BeardPriority.Neckbeard, BeardPriority.BeardCage },
                         o.Take(3));
        }
    }
}
