using System.Collections.Generic;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // "Maximise X subject to Y >= floor" — the shape that replaced the weighted blend.
    //
    // The weighted design failed for a reason that is not a tuning problem: maximising a weighted sum
    // maximises a LINEAR function, so it can only ever select points on the convex hull of the Pareto
    // frontier, and gear selection's frontier is not convex. A floor states a precondition instead of
    // a preference, which is a thing no weight can express at any value.
    public class GearFloorsTests
    {
        private static GearFloorSet Parse(string s)
        {
            GearFloorSet set; string err;
            Assert.True(GearFloorSet.TryParse(s, out set, out err), s + " -> " + err);
            return set;
        }

        private static Dictionary<string, double> Stats(double atk = 0, double def = 0, double gold = 0)
            => new Dictionary<string, double> { { "Adventure Attack", atk }, { "Adventure Defense", def }, { "Gold Drops", gold } };

        [Fact]
        public void A_floor_is_met_or_it_is_not()
        {
            var f = Parse("Adventure Attack >= 1.2B");
            Assert.False(f.AllMet(Stats(atk: 1.1e9)));
            Assert.True(f.AllMet(Stats(atk: 1.2e9)));
        }

        // ALL floors must hold. This is the one place an AND is correct, and it is correct because an
        // unmeetable conjunction is reported infeasible in the same pass — unlike an AND in `until`,
        // which would silently never advance.
        [Fact]
        public void Every_floor_must_hold_not_just_one()
        {
            var f = Parse("Adventure Attack >= 1B and Adventure Defense >= 500M");
            Assert.False(f.AllMet(Stats(atk: 2e9, def: 4e8)));
            Assert.True(f.AllMet(Stats(atk: 2e9, def: 5e8)));
            Assert.Single(f.Unmet(Stats(atk: 2e9, def: 4e8)));
        }

        [Fact]
        public void A_missing_stat_is_a_shortfall_not_a_pass()
        {
            var f = Parse("Respawn >= 40");
            Assert.False(f.AllMet(Stats()));                 // the bag has no Respawn at all
            Assert.Equal(40, f.Floors[0].Shortfall(Stats()));
        }

        // The repair pass needs to know WHICH floor is furthest away, and stats here differ by orders
        // of magnitude — percent bonuses against base-zero accumulations. Normalising per floor is a
        // ranking aid; the moment it became a score we would be back to weights.
        [Fact]
        public void Shortfall_is_normalised_so_a_big_stat_does_not_drown_a_small_one()
        {
            var f = Parse("Adventure Attack >= 1B and Respawn >= 100");
            // attack is 10% short; respawn is 50% short. Respawn is the worse one despite being tiny.
            var s = Stats(atk: 9e8); s["Respawn"] = 50;
            Assert.Equal(0.5, f.RelativeShortfall(s), 3);
        }

        [Fact]
        public void A_feasible_set_has_zero_relative_shortfall()
            => Assert.Equal(0, Parse("Adventure Attack >= 1B").RelativeShortfall(Stats(atk: 5e9)));

        [Theory]
        [InlineData("Adventure Attack >= 500K", 5e5)]
        [InlineData("Adventure Attack >= 1.2B", 1.2e9)]
        [InlineData("Adventure Attack >= 3T", 3e12)]
        [InlineData("Adventure Attack >= 250", 250)]
        public void Magnitudes_read_the_way_the_game_writes_them(string text, double expected)
            => Assert.Equal(expected, Parse(text).Floors[0].Value);

        [Fact]
        public void Stat_names_keep_their_spaces_because_that_is_what_the_scorer_calls_them()
            => Assert.Equal("Adventure Attack", Parse("Adventure Attack >= 1B").Floors[0].Stat);

        [Theory]
        [InlineData("")]
        [InlineData("Adventure Attack")]            // no operator
        [InlineData("Adventure Attack > 1B")]       // > is not >=
        [InlineData(">= 1B")]                       // no stat
        [InlineData("Adventure Attack >= banana")]
        [InlineData("Adventure Attack >= -5")]
        public void Malformed_floors_are_refused_with_a_reason(string text)
        {
            GearFloorSet s; string err;
            Assert.False(GearFloorSet.TryParse(text, out s, out err));
            Assert.False(string.IsNullOrEmpty(err));
            Assert.Null(s);
        }

        [Fact]
        public void One_bad_floor_refuses_the_whole_set_rather_than_half_applying_it()
        {
            GearFloorSet s; string err;
            Assert.False(GearFloorSet.TryParse("Adventure Attack >= 1B and Gold Drops >= nope", out s, out err));
            Assert.Contains("nope", err);
        }

        [Fact]
        public void No_floors_means_the_objective_is_unconstrained_exactly_as_today()
        {
            var empty = new GearFloorSet();
            Assert.True(empty.IsEmpty);
            Assert.True(empty.AllMet(Stats()));            // nothing to fail
            Assert.Equal(0, empty.RelativeShortfall(Stats()));
        }

        // INFEASIBLE IS A RESULT, NOT A DEGRADED SET. The entire argument for floors over weights is
        // that an unreachable requirement is something you find out about instead of silently
        // receiving the least-bad guess.
        [Fact]
        public void Infeasible_names_the_shortfall_and_blames_the_floor_not_the_objective()
        {
            var f = Parse("Adventure Attack >= 1T");
            var have = Stats(atk: 2e9);
            var v = FloorVerdict.Infeasible(f.Unmet(have), have);

            Assert.False(v.Feasible);
            Assert.Contains("Adventure Attack", v.Message);
            Assert.Contains("out of reach", v.Message);
        }

        [Fact]
        public void A_clean_solve_says_it_traded_nothing_away()
        {
            var v = FloorVerdict.Ok(0);
            Assert.True(v.Feasible);
            Assert.Contains("already clears", v.Message);
        }

        [Fact]
        public void A_repaired_solve_says_how_much_it_cost()
        {
            var v = FloorVerdict.Ok(2);
            Assert.True(v.Feasible);
            Assert.Equal(2, v.Repairs);
            Assert.Contains("2 slot(s)", v.Message);
        }

        [Fact]
        public void It_reads_back_in_the_operators_words()
        {
            Assert.Contains("subject to", Parse("Adventure Attack >= 1B").Describe());
            Assert.Contains(" and ", Parse("Adventure Attack >= 1B and Gold Drops >= 200").Describe());
            Assert.Equal("no floors", new GearFloorSet().Describe());
        }
    }
}
