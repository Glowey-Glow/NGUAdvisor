using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // PASS 2 — capacity (constraint-layer-spec §5): the provenance table, the vacuity test, and the
    // float stall floors — including the one the spec is loudest about, the binade boundary where the
    // shipped relative guard reports a stalled wish as healthy.
    public class CapacityPassTests
    {
        // ---- the provenance table (spec §5.1) ----------------------------------------------------

        // The game-supplied cap helpers are exactly the spec's four. A fifth Game row means someone
        // invented a helper; a missing one means someone re-derived a number the game maintains.
        [Fact]
        public void The_game_supplied_cap_rows_are_exactly_the_specs_four()
        {
            var gameRows = CapacityPass.Table
                .Where(r => r.Source == CapacityPass.CapSource.Game || r.Source == CapacityPass.CapSource.RateCeiling)
                .Select(r => r.Lane)
                .OrderBy(l => l)
                .ToArray();

            Assert.Equal(new[] { "BasicTrainingBP", "NGUBP", "WandoosBP", "Wishes" }, gameRows);
        }

        // Wandoos' capacity is CONSTANT (capAmountEnergy has no level term) while every other lane's
        // absorption grows with progression — which is why vacuity fires early and stops firing as
        // the account matures. Exactly one flat row, and it is Wandoos.
        [Fact]
        public void Wandoos_is_the_only_flat_capacity_lane()
        {
            var flat = CapacityPass.Table.Where(r => r.FlatCap).ToArray();

            Assert.Single(flat);
            Assert.Equal("WandoosBP", flat[0].Lane);
        }

        [Fact]
        public void Every_allocatable_row_names_the_helper_that_supplies_its_number()
        {
            foreach (var row in CapacityPass.Table)
                if (CapacityPass.Allocatable(row))
                    Assert.False(string.IsNullOrEmpty(row.Helper));
        }

        // ---- beards: Pass 2 refuses to cap them (spec §6) ----------------------------------------

        // There is no beards[id].energy and no addEnergy — never allocated to, never capped. The
        // refusal is structural: the beard row is CapSource.None, Allocatable says no, and no
        // function in CapacityPass produces a capacity for a None row.
        [Fact]
        public void The_beard_row_is_not_allocatable_and_says_why()
        {
            var beard = CapacityPass.Table.Single(r => r.Lane == "Beards");

            Assert.Equal(CapacityPass.CapSource.None, beard.Source);
            Assert.False(CapacityPass.Allocatable(beard));
            Assert.Contains("never allocated", beard.Helper);
        }

        // ---- the vacuity test (spec §5.2) --------------------------------------------------------

        [Fact]
        public void A_pool_larger_than_total_capacity_is_vacuous_and_routes_the_surplus()
        {
            var r = CapacityPass.Vacuity(pool: 1000, capacities: new long[] { 100, 200, 300 });

            Assert.True(r.Vacuous);
            Assert.Equal(600, r.TotalCapacity);
            Assert.Equal(400, r.Surplus);
        }

        [Fact]
        public void A_pool_at_or_below_total_capacity_leaves_a_real_allocation_question()
        {
            Assert.False(CapacityPass.Vacuity(600, new long[] { 100, 200, 300 }).Vacuous);
            Assert.False(CapacityPass.Vacuity(599, new long[] { 100, 200, 300 }).Vacuous);
            Assert.Equal(0, CapacityPass.Vacuity(600, new long[] { 100, 200, 300 }).Surplus);
        }

        // Late-game caps are astronomical; the sum must saturate rather than wrap negative and
        // declare a 10^18 pool vacuous by overflow.
        [Fact]
        public void Capacity_sums_saturate_instead_of_overflowing()
        {
            var r = CapacityPass.Vacuity(long.MaxValue, new[] { long.MaxValue / 2 + 1, long.MaxValue / 2 + 1 });

            Assert.Equal(long.MaxValue, r.TotalCapacity);
            Assert.False(r.Vacuous);
        }

        [Fact]
        public void Negative_capacities_are_treated_as_zero_not_subtracted()
        {
            var r = CapacityPass.Vacuity(100, new long[] { -50, 30 });

            Assert.Equal(30, r.TotalCapacity);
            Assert.True(r.Vacuous);
            Assert.Equal(70, r.Surplus);
        }

        // ---- THE FLOAT FLOOR AT THE BINADE BOUNDARY (spec §5.3) ----------------------------------

        // First, the physics, straight from IEEE 754: at progress 0.5f, an increment of 2e-8 (below
        // half the binade's 2^-24 ULP) is swallowed by round-to-nearest, and one of 4e-8 is not.
        // Every assertion about floors below is downstream of this one.
        [Fact]
        public void A_two_e_minus_8_increment_really_is_a_no_op_at_progress_half()
        {
            float progress = 0.5f;

            Assert.Equal(progress, progress + 2e-8f);
            Assert.True(progress + 4e-8f > progress);
        }

        // THE REQUIRED CASE: ppt = 2e-8 at progress = 0.5 must be caught. The shipped WishManager
        // guard (ppt / progress <= 2^-25) computes 4e-8 > 2.98e-8 and reports the wish HEALTHY while
        // it freezes at exactly 0.5 — the failure players report from the field (10 §D4).
        [Fact]
        public void The_stall_at_the_binade_boundary_is_caught_where_the_relative_guard_misses_it()
        {
            const double ppt = 2e-8;
            const float progress = 0.5f;

            Assert.True(CapacityPass.StalledNow(ppt, progress));

            // The old guard's arithmetic, verbatim, declaring the same wish healthy — kept here so
            // the defect this floor replaces stays visible.
            Assert.False(ppt / progress <= CapacityPass.FinalBinadeFloor);
        }

        // The floor is ulp/2 and does NOT scale with progress inside a binade: 2^-25 at 0.5, at
        // 0.75, and at 0.99999994 alike. It halves only when progress drops into the binade below.
        [Fact]
        public void The_floor_is_constant_across_the_final_binade_and_halves_below_it()
        {
            Assert.Equal(CapacityPass.FinalBinadeFloor, CapacityPass.StallFloorAt(0.5f));
            Assert.Equal(CapacityPass.FinalBinadeFloor, CapacityPass.StallFloorAt(0.75f));
            Assert.Equal(CapacityPass.FinalBinadeFloor, CapacityPass.StallFloorAt(0.99999994f));
            Assert.Equal(CapacityPass.FinalBinadeFloor / 2.0, CapacityPass.StallFloorAt(0.25f));
        }

        // Exactly half an ULP parks the bar too (ties-to-even lands on 0.5's even mantissa), which
        // is why the comparison is <= and not <.
        [Fact]
        public void Exactly_half_an_ulp_is_a_stall_not_an_advance()
        {
            float progress = 0.5f;

            Assert.Equal(progress, progress + (float)CapacityPass.FinalBinadeFloor);
            Assert.True(CapacityPass.StalledNow(CapacityPass.FinalBinadeFloor, progress));
        }

        // A wish at progress 0.25 with ppt 2e-8 is moving TODAY (its local floor is 2^-26) and will
        // still never finish the level: it parks the moment it reaches 0.5. The capacity question is
        // the absolute one.
        [Fact]
        public void A_rate_below_the_final_binade_floor_moves_now_but_never_completes()
        {
            const double ppt = 2e-8;

            Assert.False(CapacityPass.StalledNow(ppt, 0.25f));
            Assert.True(CapacityPass.CannotCompleteLevel(ppt));
            Assert.False(CapacityPass.CannotCompleteLevel(4e-8));
        }

        // ---- the ritual bar floor (BloodMagicController.cs:298-304) ------------------------------

        // A cliff, not a float artifact: below 1e-9 barFillsPerSecond returns 0, not a small number.
        // Strict <, mirroring the game exactly — 1e-9 itself still fills.
        [Fact]
        public void The_ritual_bar_zeroes_strictly_below_1e_minus_9()
        {
            Assert.True(CapacityPass.RitualBarZeroed(0.9e-9f));
            Assert.False(CapacityPass.RitualBarZeroed(1e-9f));
            Assert.False(CapacityPass.RitualBarZeroed(1.1e-9f));
        }

        // ---- hacks delegate to the existing home -------------------------------------------------

        [Fact]
        public void The_hack_floor_is_HackMaths_and_answers_the_same_absolute_question()
        {
            Assert.True(CapacityPass.HackWillStall(2e-8));
            Assert.False(CapacityPass.HackWillStall(4e-8));
        }

        // ---- wish saturation (WishesController.cs:739-759) ---------------------------------------

        // minimumWishTime() is the ppt CEILING, not a time: 1/((14400 - reductions) * 50). At zero
        // reductions that is one full level in 4 hours of ticks, and reductions RAISE the ceiling.
        [Fact]
        public void The_wish_ppt_ceiling_is_the_games_formula_and_reductions_raise_it()
        {
            Assert.Equal(1f / (14400f * 50f), CapacityPass.WishPptCeiling(0f));
            Assert.True(CapacityPass.WishPptCeiling(3600f) > CapacityPass.WishPptCeiling(0f));
        }

        [Fact]
        public void Raw_rate_above_the_ceiling_is_saturated_and_clamps()
        {
            float ceiling = CapacityPass.WishPptCeiling(0f);

            Assert.True(CapacityPass.WishSaturated(ceiling * 2.0, ceiling));
            Assert.False(CapacityPass.WishSaturated(ceiling * 0.5, ceiling));
            Assert.Equal(ceiling, CapacityPass.WishEffectivePpt(ceiling * 2.0, ceiling));
        }

        // The game's own zero-floor, mirrored with its strict <: raw ppt under 1e-8 produces
        // literally nothing.
        [Fact]
        public void Raw_rate_below_the_games_zero_floor_produces_exactly_zero()
        {
            float ceiling = CapacityPass.WishPptCeiling(0f);

            Assert.Equal(0.0, CapacityPass.WishEffectivePpt(0.9e-8, ceiling));
            Assert.Equal(1e-8, CapacityPass.WishEffectivePpt(1e-8, ceiling));
        }

        // The stall window the shipped guard misses, as an arithmetic fact: the game's zero-floor
        // (1e-8) sits BELOW the real stall floor (2^-25 ≈ 2.98e-8), so a rate between them passes
        // the game's check, ticks — and freezes in the [0.5, 1) binade anyway.
        [Fact]
        public void The_games_zero_floor_sits_below_the_real_stall_floor()
        {
            Assert.True(CapacityPass.WishGameZeroFloor < CapacityPass.FinalBinadeFloor);
            Assert.True(CapacityPass.CannotCompleteLevel(2e-8));
            Assert.False(CapacityPass.WishEffectivePpt(2e-8, CapacityPass.WishPptCeiling(0f)) == 0.0);
        }
    }
}
