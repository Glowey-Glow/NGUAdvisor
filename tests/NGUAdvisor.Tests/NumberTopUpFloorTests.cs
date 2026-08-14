using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The pre-rebirth NUMBER top-up floor (BaseRebirth.CastBloodSpellsForRebirth). The old test was
    // `bloodPoints > 0`, which is true of float residue, so every rebirth paid an extra allocation
    // loop with all three pools stripped for a gain around 1e-10.
    //
    // The measured values below are from one 36 h session: 46 of 52 casts were under a single blood
    // point, against a rebirthPower that reaches ~7.25e9 within a run.
    public class NumberTopUpFloorTests
    {
        // The exact residues seen in the log. They are dyadic fractions — the signature of repeated
        // float subtraction as BloodPlanner spends the pool down during the run.
        [Theory]
        [InlineData(0.59375)]
        [InlineData(0.8203125)]
        [InlineData(0.560546875)]
        [InlineData(0.661806106567383)]
        [InlineData(0.940502166748047)]
        public void Float_residue_against_a_grown_multiplier_is_skipped(double residue)
        {
            Assert.False(BloodPillMath.ShouldCastNumberTopUp(residue, rebirthPower: 7.25e9));
        }

        // The two real casts from the same session — the first two rebirths, before the planner had
        // spent anything. These must still fire.
        [Theory]
        [InlineData(878697.719357491)]
        [InlineData(46677508.4829474)]
        public void A_genuine_bank_still_casts(double blood)
        {
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(blood, rebirthPower: 1.0));
            // ...and still casts even against a fully grown multiplier.
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(blood, rebirthPower: 7.25e9));
        }

        // THE FLOOR SELF-SCALES — this is why it is a ratio and not a blood constant. The same single
        // blood point is the whole multiplier at the start of a run and nothing at the end.
        [Fact]
        public void One_blood_point_casts_at_the_start_of_a_run_and_not_at_the_end()
        {
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(1.0, rebirthPower: 1.0));
            Assert.False(BloodPillMath.ShouldCastNumberTopUp(1.0, rebirthPower: 7.25e9));
        }

        // Exactly at the floor casts; a hair under does not.
        [Fact]
        public void The_boundary_is_inclusive()
        {
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(7250.0, rebirthPower: 7.25e9));   // == 1e-6
            Assert.False(BloodPillMath.ShouldCastNumberTopUp(7249.0, rebirthPower: 7.25e9));
        }

        [Fact]
        public void No_blood_never_casts()
        {
            Assert.False(BloodPillMath.ShouldCastNumberTopUp(0.0, rebirthPower: 1.0));
            Assert.False(BloodPillMath.ShouldCastNumberTopUp(-1.0, rebirthPower: 1.0));
            Assert.False(BloodPillMath.ShouldCastNumberTopUp(double.NaN, rebirthPower: 1.0));
        }

        // FAILS OPEN. Anything it cannot reason about must cast, so the floor can only ever skip a
        // cast it has positively shown to be negligible. ImportExport.cs:198-200 clamps rebirthPower
        // to >= 1.0 on load, so these are defensive rather than expected.
        [Fact]
        public void Unreasonable_multipliers_cast_rather_than_skip()
        {
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(0.5, rebirthPower: 0.0));
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(0.5, rebirthPower: -3.0));
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(0.5, rebirthPower: double.NaN));
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(0.5, rebirthPower: double.PositiveInfinity));
        }

        // A profile that genuinely banks blood to rebirth stays unaffected — the whole reason the
        // floor is relative rather than a blood threshold.
        [Fact]
        public void A_deliberate_blood_bank_is_never_skipped()
        {
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(1e12, rebirthPower: 1e9));
            Assert.True(BloodPillMath.ShouldCastNumberTopUp(1e6, rebirthPower: 1e9));
        }
    }
}
