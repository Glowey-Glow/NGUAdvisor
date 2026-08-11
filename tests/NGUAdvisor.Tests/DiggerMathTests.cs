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

        // ---- AllocateLevels ----
        //
        // The fixture is the real active set from the 2026-08-01 Evil save, with the game's own
        // drain/bonus constants (baseGPSDrain, gpsGrowthRate, startingBoost, boostPerLevel — all public
        // on AllGoldDiggerController) and the gross GPS reconstructed from that session's [DiggerDbg]
        // lines. So these tests pin behaviour against numbers the advisor actually ran on.

        private static DiggerMath.DiggerSpec Spec(int id, double baseDrain, double growth, long maxLevel,
                                                  double startingBoost, double boostPerLevel,
                                                  bool cubic = false, double weight = 1.0)
            => new DiggerMath.DiggerSpec
            {
                Id = id, BaseDrain = baseDrain, Growth = growth, MaxLevel = maxLevel,
                StartingBoost = startingBoost, BoostPerLevel = boostPerLevel, Cubic = cubic, Weight = weight
            };

        private const double LiveEvilBudget = 2.72e31;

        private static DiggerMath.DiggerSpec[] LiveEvilSet() => new[]
        {
            Spec(3, 1e12, 1.5,  139, 0.1,  0.005),                // Adventure
            Spec(1, 1e12, 1.5,  139, 0.5,  0.01),                 // Wandoos
            Spec(2, 1e12, 1.5,  139, 1.0,  0.01, cubic: true),    // Stats  (level enters cubed)
            Spec(4, 1e15, 1.75, 89,  0.2,  0.01),                 // Energy NGU
            Spec(5, 1e15, 1.75, 89,  0.2,  0.01),                 // Magic NGU
            Spec(9, 1e21, 1.75, 64,  0.05, 0.001),                // Daycare
        };

        private static double TotalDrain(DiggerMath.DiggerSpec[] specs, long[] levels)
        {
            double total = 0.0;
            for (int i = 0; i < specs.Length; i++) total += DiggerMath.DrainAt(specs[i], levels[i]);
            return total;
        }

        // The sequential residual allocator this replaced, reproduced so the regression test can compare
        // against the real prior behaviour rather than a remembered number.
        private static long[] LegacySequential(double budget, DiggerMath.DiggerSpec[] specs)
        {
            var levels = new long[specs.Length];
            for (int i = 0; i < specs.Length; i++) levels[i] = 1;
            for (int i = 0; i < specs.Length; i++)
            {
                double othersDrain = 0.0;
                for (int j = 0; j < specs.Length; j++)
                    if (j != i) othersDrain += DiggerMath.DrainAt(specs[j], levels[j]);
                double cap = budget - othersDrain;
                long lvl = cap < specs[i].BaseDrain
                    ? 1
                    : (long)System.Math.Floor(System.Math.Log(cap / specs[i].BaseDrain, specs[i].Growth) + 1L);
                levels[i] = System.Math.Max(1, System.Math.Min(lvl, specs[i].MaxLevel));
            }
            return levels;
        }

        [Fact]
        public void AllocateLevels_never_exceeds_the_drain_budget()
        {
            var specs = LiveEvilSet();
            var levels = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            Assert.True(TotalDrain(specs, levels) <= LiveEvilBudget);
        }

        [Fact]
        public void AllocateLevels_floors_every_bought_digger_at_one_and_respects_max_level()
        {
            var specs = LiveEvilSet();
            var levels = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            for (int i = 0; i < specs.Length; i++)
            {
                Assert.InRange(levels[i], 1, specs[i].MaxLevel);
            }
        }

        // A digger that was never bought (maxLevel 0) must stay at 0 — it cannot run at all.
        [Fact]
        public void AllocateLevels_leaves_an_unbought_digger_at_zero()
        {
            var specs = new[] { Spec(3, 1e12, 1.5, 139, 0.1, 0.005), Spec(10, 1e24, 1.75, 0, 0.5, 0.01) };
            var levels = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            Assert.Equal(0, levels[1]);
        }

        // THE REGRESSION THIS REPLACED. The sequential rule gave whichever digger sorted first everything
        // it could afford out of the remaining budget; on this exact set that was ~86% of gross to one
        // digger, with the tail pinned near level 1. Value-ranking must spread it materially wider.
        [Fact]
        public void AllocateLevels_does_not_concentrate_the_budget_the_way_the_sequential_rule_did()
        {
            var specs = LiveEvilSet();

            var legacy = LegacySequential(LiveEvilBudget, specs);
            double legacyTotal = TotalDrain(specs, legacy);
            double legacyTop = 0.0;
            for (int i = 0; i < specs.Length; i++)
                legacyTop = System.Math.Max(legacyTop, DiggerMath.DrainAt(specs[i], legacy[i]) / legacyTotal);

            var levels = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            double total = TotalDrain(specs, levels);
            double top = 0.0;
            for (int i = 0; i < specs.Length; i++)
                top = System.Math.Max(top, DiggerMath.DrainAt(specs[i], levels[i]) / total);

            Assert.True(legacyTop > 0.80, $"fixture no longer reproduces the old concentration ({legacyTop:P1})");
            Assert.True(top < legacyTop, $"new allocator concentrates {top:P1} vs legacy {legacyTop:P1}");
        }

        // The old rule left the tail at level 1 while the head sat near its ceiling. Nothing with real
        // headroom should be stranded at the floor when the budget is this large.
        [Fact]
        public void AllocateLevels_does_not_strand_the_tail_at_level_one()
        {
            var specs = LiveEvilSet();
            var levels = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            Assert.All(levels, lvl => Assert.True(lvl > 1, "a digger was left at the level-1 floor"));
        }

        // More budget must never buy fewer levels anywhere — the property that makes the allocation
        // stable against the continuous drift in gross GPS that made the old tail oscillate.
        [Fact]
        public void AllocateLevels_is_monotone_in_budget()
        {
            var specs = LiveEvilSet();
            var lo = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            var hi = DiggerMath.AllocateLevels(LiveEvilBudget * 1.05, specs);
            for (int i = 0; i < specs.Length; i++)
                Assert.True(hi[i] >= lo[i], $"digger {specs[i].Id} LOST a level when the budget grew");
        }

        // The cubic digger (Stats) keeps gaining relative value as it levels, so against an otherwise
        // identical linear twin it must end up ahead. Guards the Math.Pow(level, 3) branch.
        [Fact]
        public void AllocateLevels_favours_the_cubic_digger_over_an_identical_linear_one()
        {
            var specs = new[]
            {
                Spec(2, 1e12, 1.5, 139, 1.0, 0.01, cubic: true),
                Spec(1, 1e12, 1.5, 139, 1.0, 0.01),
            };
            var levels = DiggerMath.AllocateLevels(LiveEvilBudget, specs);
            Assert.True(levels[0] > levels[1], $"cubic {levels[0]} did not outrank linear {levels[1]}");
        }

        // Weight is the documented policy knob; doubling one digger's weight must move budget toward it.
        [Fact]
        public void AllocateLevels_respects_the_weight_knob()
        {
            var flat = new[] { Spec(4, 1e15, 1.75, 89, 0.2, 0.01), Spec(5, 1e15, 1.75, 89, 0.2, 0.01) };
            var tilted = new[] { Spec(4, 1e15, 1.75, 89, 0.2, 0.01, weight: 8.0), Spec(5, 1e15, 1.75, 89, 0.2, 0.01) };
            var a = DiggerMath.AllocateLevels(1e28, flat);
            var b = DiggerMath.AllocateLevels(1e28, tilted);
            Assert.Equal(a[0], a[1]);                 // identical specs, identical outcome
            Assert.True(b[0] > b[1], $"weighted digger got {b[0]} vs {b[1]}");
        }

        [Fact]
        public void AllocateLevels_handles_an_empty_set()
        {
            Assert.Empty(DiggerMath.AllocateLevels(1e30, new DiggerMath.DiggerSpec[0]));
            Assert.Empty(DiggerMath.AllocateLevels(1e30, null));
        }
    }
}
