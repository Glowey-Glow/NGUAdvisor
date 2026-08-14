using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Converting "I must survive this fight" into a number the gear solver can use.
    //
    // The requirement is an ADVENTURE stat; the solver measures a BRACKET; the two are separated by a
    // dozen multipliers. The conversion is exact — every one of those factors is identical for every
    // candidate loadout — but a wrong multiplier or wrong units is a SILENT wrong answer: no throw,
    // nothing odd-looking, just an unsurvivable set marked feasible.
    //
    // That is the same failure shape that has already cost three rounds on this feature, which is why
    // the arithmetic is a pure function and why these tests exist at all.
    public class AdventureFloorTests
    {
        [Fact]
        public void The_floor_is_the_requirement_divided_by_the_measured_multiplier()
        {
            // need 1000 adventure attack, everything in the bracket is multiplied by 10
            //   -> the bracket must reach 100
            Assert.Equal(100, AdventureFloor.RequiredBracket(1000, 10));
        }

        // ⚠ The floor is on the BRACKET, not on the gear term. GearOptimizer.FloorStats scores WornList(),
        // which appends the cube and the nude base to the candidate items — so what it reports is already
        // gear + cubePower + adventure.attack. Subtracting the non-gear base here too would remove it
        // twice and put the floor far below the real requirement.
        [Fact]
        public void The_non_gear_base_is_NOT_subtracted_because_the_solver_already_counts_it()
        {
            double floor = AdventureFloor.RequiredBracket(1000, 10);

            // A candidate contributing 80 gear, on top of a 20 non-gear base, exactly reaches it.
            Assert.Equal(floor, 80 + 20);

            // The gear-only reading (60) would have been cleared by a set that cannot survive.
            Assert.True(floor > 1000 / 10 - 20 - 19);
        }

        // NaN means "cannot be expressed" and callers must read it as NO FLOOR. Zero would be a floor
        // every set trivially clears — the dangerous direction, because it looks like a satisfied
        // constraint rather than an absent one.
        [Theory]
        [InlineData(1000, 0)]                        // no multiplier
        [InlineData(1000, -5)]                       // nonsense multiplier
        [InlineData(0, 10)]                          // no requirement is not a floor of zero
        [InlineData(-1, 10)]
        [InlineData(double.NaN, 10)]
        [InlineData(1000, double.NaN)]
        [InlineData(1000, double.PositiveInfinity)]
        public void Unexpressible_conversions_return_NaN_never_zero(double req, double mult)
            => Assert.True(double.IsNaN(AdventureFloor.RequiredBracket(req, mult)));

        [Fact]
        public void The_multiplier_is_measured_from_the_game_not_reconstructed()
        {
            // total / bracket. Deriving it this way means a new game multiplier needs no change here —
            // rebuilding the product by hand would be a second copy of a formula that already caused the
            // titan-version defect.
            Assert.Equal(12.5, AdventureFloor.MultiplierFrom(1250, 100));
        }

        [Theory]
        [InlineData(1250, 0)]
        [InlineData(1250, -1)]
        [InlineData(double.NaN, 100)]
        [InlineData(double.PositiveInfinity, 100)]
        [InlineData(0, 100)]              // a zero total means the read is not usable
        public void An_unusable_reading_is_NaN(double total, double bracket)
            => Assert.True(double.IsNaN(AdventureFloor.MultiplierFrom(total, bracket)));

        // The round trip is the property that actually matters: a set landing exactly on the floor must
        // land exactly on the requirement.
        [Theory]
        [InlineData(1.2e15, 4.0e6)]
        [InlineData(9.8e10, 137.5)]
        [InlineData(5.0e8, 2.5)]
        public void A_set_that_exactly_meets_the_floor_exactly_meets_the_requirement(
            double required, double multiplier)
        {
            double floor = AdventureFloor.RequiredBracket(required, multiplier);
            Assert.False(double.IsNaN(floor));
            Assert.Equal(required, floor * multiplier, 6);   // to 6 significant places
        }

        [Fact]
        public void More_than_the_floor_clears_the_requirement_and_less_does_not()
        {
            double floor = AdventureFloor.RequiredBracket(1000, 10);
            Assert.True((floor + 1) * 10 > 1000);
            Assert.True((floor - 1) * 10 < 1000);
        }

        // The multiplier and the floor are inverses through the game's own reads: measure the tail from a
        // live bracket, convert a requirement back, and the arithmetic closes.
        [Fact]
        public void Measuring_the_multiplier_and_converting_back_is_self_consistent()
        {
            double baseStat = 3.0e7, gear = 8.0e7, cube = 1.0e7;
            double bracket = baseStat + gear + cube;
            double total = bracket * 4.0e6;

            double m = AdventureFloor.MultiplierFrom(total, bracket);
            Assert.Equal(bracket, AdventureFloor.RequiredBracket(total, m), 6);
        }
    }
}
