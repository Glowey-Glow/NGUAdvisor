using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The "approx time to farm them all" figure behind the opt-out offer ([OPERATOR] 2026-08-05:
    // "n rare accessories available for farm, approx time to farm n").
    //
    // ⚠ THE RULE IS COUNTER-INTUITIVE AND A NAIVE Sum IS WRONG. Rares dropping in the SAME zone
    // advance TOGETHER — items 226 and 227 share one roll with Span 2, so standing in Chocolate World
    // farms both at once. Summing them roughly doubles that zone's quoted cost, which could talk the
    // operator out of a farm at twice its real price. That is the whole reason this is a linked,
    // tested function instead of an inline LINQ Sum.
    public class RareRollupTests
    {
        [Fact]
        public void Two_rares_in_one_zone_cost_the_slower_one_not_both()
        {
            // The live shape: 226 (~54.5h) and 227 (~35h) share zone 20's boss roll.
            var h = RareRollup.SequentialHours(new[] { 20, 20 }, new[] { 54.5, 35.0 });
            Assert.Equal(54.5, h, 3);          // the slower one
            Assert.NotEqual(89.5, h, 3);       // NOT the sum
        }

        [Fact]
        public void Rares_in_different_zones_add_because_routing_farms_one_zone_at_a_time()
        {
            // SnipeZone is a single value (audit/40 §0) — zones are visited in sequence.
            var h = RareRollup.SequentialHours(new[] { 20, 21 }, new[] { 54.5, 642.2 });
            Assert.Equal(696.7, h, 3);
        }

        [Fact]
        public void The_mixed_case_takes_the_worst_per_zone_then_adds_across_zones()
        {
            var h = RareRollup.SequentialHours(
                new[] { 20, 20, 21, 22, 22, 22 },
                new[] { 54.5, 35.0, 642.2, 10.0, 80.0, 20.0 });
            Assert.Equal(54.5 + 642.2 + 80.0, h, 3);
        }

        [Fact]
        public void One_rare_costs_its_own_hours()
            => Assert.Equal(35.0, RareRollup.SequentialHours(new[] { 20 }, new[] { 35.0 }), 3);

        // ── the figure feeds a display line, so bad input must degrade, never throw ────────────────

        [Fact]
        public void Nothing_eligible_is_zero_hours()
        {
            Assert.Equal(0, RareRollup.SequentialHours(new int[0], new double[0]));
            Assert.Equal(0, RareRollup.SequentialHours(null, null));
            Assert.Equal(0, RareRollup.SequentialHours(new[] { 20 }, null));
        }

        [Fact]
        public void An_unreachable_rare_contributes_nothing_rather_than_infinity()
        {
            var h = RareRollup.SequentialHours(
                new[] { 20, 21 }, new[] { double.PositiveInfinity, 12.0 });
            Assert.Equal(12.0, h, 3);
            Assert.False(double.IsInfinity(h));
        }

        [Fact]
        public void Nan_and_negative_hours_are_treated_as_zero()
        {
            var h = RareRollup.SequentialHours(new[] { 20, 21 }, new[] { double.NaN, -5.0 });
            Assert.Equal(0, h, 3);
            Assert.False(double.IsNaN(h));
        }

        // Mismatched lengths take the shorter — a malformed input must not take down the zone pass.
        [Fact]
        public void Mismatched_lengths_use_the_shorter_side()
        {
            Assert.Equal(35.0, RareRollup.SequentialHours(new[] { 20, 21, 22 }, new[] { 35.0 }), 3);
            Assert.Equal(35.0, RareRollup.SequentialHours(new[] { 20 }, new[] { 35.0, 99.0, 42.0 }), 3);
        }
    }
}
