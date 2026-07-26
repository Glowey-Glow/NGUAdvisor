using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless guards for the two digger decisions that shipped bugs (audit M5 migration). Both are
    // Unity-free, so a regression is caught in CI instead of only in-game.
    public class DiggerMathTests
    {
        // Bug3 (stale/empty priority order sent the leveler the wrong ranking): the recommendation's
        // priority members must come FIRST, in priority order, then the rest of the active set.
        [Fact]
        public void OrderByPriority_puts_priority_members_first_then_the_rest()
        {
            var active = new[] { 7, 3, 11, 0 };       // live active set order
            var priority = new[] { 11, 7 };           // advisor's ranking
            var ordered = DiggerMath.OrderByPriority(active, priority).ToArray();
            Assert.Equal(new[] { 11, 7, 3, 0 }, ordered);
        }

        [Fact]
        public void OrderByPriority_ignores_priority_ids_not_in_the_active_set()
        {
            var active = new[] { 3, 8 };
            var priority = new[] { 11, 8, 3 };        // 11 isn't active
            Assert.Equal(new[] { 8, 3 }, DiggerMath.OrderByPriority(active, priority).ToArray());
        }

        [Fact]
        public void OrderByPriority_null_priority_returns_items_unchanged()
        {
            var active = new[] { 5, 2, 9 };
            Assert.Equal(active, DiggerMath.OrderByPriority(active, null).ToArray());
        }

        [Fact]
        public void MaxAffordableLevel_returns_curLevel_when_it_cannot_afford_level_one()
        {
            // cap below the level-1 drain: keep the current level, don't drop to 0.
            Assert.Equal(7, DiggerMath.MaxAffordableLevel(cap: 50, baseDrain: 100, growthRate: 2,
                drainAtLevel1: 200, curLevel: 7, maxLevel: 100));
        }

        [Fact]
        public void MaxAffordableLevel_solves_the_growth_formula()
        {
            // cap/base = 8, log2(8) = 3, +1 => level 4.
            Assert.Equal(4, DiggerMath.MaxAffordableLevel(cap: 800, baseDrain: 100, growthRate: 2,
                drainAtLevel1: 200, curLevel: 1, maxLevel: 100));
        }

        [Fact]
        public void MaxAffordableLevel_clamps_to_max_level()
        {
            Assert.Equal(3, DiggerMath.MaxAffordableLevel(cap: 800, baseDrain: 100, growthRate: 2,
                drainAtLevel1: 200, curLevel: 1, maxLevel: 3));
        }

        [Fact]
        public void MaxAffordableLevel_never_drops_below_current_level()
        {
            // Formula would give 4, but the digger is already level 10 — recap must not de-level it.
            Assert.Equal(10, DiggerMath.MaxAffordableLevel(cap: 800, baseDrain: 100, growthRate: 2,
                drainAtLevel1: 200, curLevel: 10, maxLevel: 100));
        }
    }
}
