using System;
using System.Globalization;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE BLOCK AT CURVE, AGAINST THE MEASURED levelFactor.
    //
    // decisions/G1-D3-V9-amendment-35 §5 item 2 left this open: "levelFactor for AT id 2 — a serialized
    // field, never captured… Until then the 100k ↔ 99.95% correspondence is conditional on the default."
    //
    // ⚠ "NEVER CAPTURED" WAS ALREADY FALSE WHEN IT WAS WRITTEN. ConstantCapture.EmitAt has emitted
    // levelFactor for all five AdvancedTrainingControllers since the E5 batch, and the operator's
    // inject.log carries it on every run:
    //
    //     [ConstCap] E5.block = id=2 baseTime=10000000 levelFactor=0.00999999978
    //                name="Block Damage Reduction"
    //
    // 0.00999999978 is the "G9" render of the float nearest 1/100, so the field's scene value is 0.01f.
    // All five controllers (ids 0-4) carry the same 0.01f. The correspondence is therefore MEASURED, not
    // conditional, and this file pins the arithmetic that follows from it.
    //
    // PROVENANCE OF THE NUMBER BELOW: [MEASURED] from the live instrument. NOT [DECOMP] — the decompile
    // shows `public float levelFactor;` with NO initializer (AdvancedTrainingController.cs:33) because
    // AdvancedTrainingController is a MonoBehaviour and the value lives in the Unity scene asset. It is
    // NOT in the player save either: the [Serializable] save class `AdvancedTraining` carries only
    // training/level/bankedLevel/energy/barProgress/levelTarget/transferredBankedLevels/autoAdvance.
    // The instrument is the only way to read it, and it already does.
    //
    // ⚠ NOTHING HERE IS A TARGET AND NOTHING HERE FEEDS AN ALLOCATION PATH. LevelPlanner.BlockStopLevel
    // was DELETED (see the tombstone comment at LevelPlanner.cs:186) and must not come back. This file
    // only guards ObjectiveTable.BlockDamageCurve — a data table with a known-corrupt rung — from
    // drifting away from the curve it claims to describe.
    public class BlockCurveLevelFactorTests
    {
        // [MEASURED] [ConstCap] E5.block levelFactor, operator's inject.log, build 1260.
        private const float BlockLevelFactor = 0.01f;

        // [DECOMP] AdvancedTrainingController.blockBonus(int) :264-276, reproduced in float32 because
        // the game is float32 and the rounding is what the tooltip shows. This is the REMAINING damage
        // fraction, not the reduction.
        private static float BlockBonus(long level)
        {
            if ((float)level <= 0f) return 0.5f;
            if (BlockLevelFactor * (float)level == 0f) return 0f;
            float num = 1f + BlockLevelFactor * (float)level;
            return 50f / num / 100f;
        }

        // [DECOMP] :248 — `(1f - blockBonus(0)) * 100f`, labelled "Current Block Reduction".
        private static float Reduction(long level) => (1f - BlockBonus(level)) * 100f;

        // [DECOMP] :248 — the tooltip's own format specifier. InvariantCulture here so the assertion is
        // deterministic; the game uses the ambient culture, which does not change the digits.
        private static string Tooltip(long level) =>
            Reduction(level).ToString("##.0#", CultureInfo.InvariantCulture);

        [Fact]
        public void The_captured_levelFactor_is_the_float_nearest_one_hundredth()
        {
            // Ties this file to the evidence: "G9" is what ConstantCapture.F emits, and this is the
            // exact string on the E5.block line. If a future capture disagrees, this fails and every
            // number below is suspect.
            Assert.Equal("0.00999999978", BlockLevelFactor.ToString("G9", CultureInfo.InvariantCulture));
            Assert.Equal(0.01f, BlockLevelFactor);
        }

        [Fact]
        public void Level_zero_is_fifty_percent_by_both_the_guard_and_the_formula()
        {
            // amendment 35 §1: the :266 guard returns 0.5f and the formula agrees there, so the curve
            // is continuous at its own special case.
            Assert.Equal(0.5f, BlockBonus(0));
            Assert.Equal(50f, Reduction(0));
        }

        [Fact]
        public void Every_usable_guide_rung_is_reached_at_its_stated_level()
        {
            // Each rung reads "Block Reduction reaches X% at Level L", so the test is reduction(L) >= X.
            // The 1,000,000 row's BlockReduction is prose, not a percentage, and is skipped by the
            // EndsWith check rather than by an index — an index would rot if the table were reordered.
            int checkedRungs = 0;
            foreach (var rung in ObjectiveTable.BlockDamageCurve)
            {
                if (!rung.Usable) continue;
                string pct = rung.BlockReduction;
                if (pct == null || !pct.EndsWith("%", StringComparison.Ordinal)) continue;

                double stated = double.Parse(pct.Substring(0, pct.Length - 1),
                                             CultureInfo.InvariantCulture);
                Assert.True(Reduction(rung.Level) >= stated,
                    $"rung '{pct} at {rung.Level}' is not reached: curve gives " +
                    $"{Reduction(rung.Level).ToString("G9", CultureInfo.InvariantCulture)}%");
                checkedRungs++;
            }

            // A silently emptied table would pass a foreach that never runs. The guide's usable numeric
            // rungs are 90%/400, 99%/5,000 and 99.99%/500,000.
            Assert.Equal(3, checkedRungs);
        }

        [Fact]
        public void The_broken_rung_is_impossible_at_5_and_the_conjectured_50000_is_confirmed()
        {
            // ObjectiveTable §2.4 carries "99.9% AT 5" verbatim with Usable == false and calls 50,000
            // the "almost certainly" intended value — a conjecture from MONOTONICITY alone. With the
            // measured levelFactor it stops being a conjecture: level 5 gives 52.38%, and 50,000 gives
            // a tooltip of exactly "99.9".
            var broken = Assert.Single(ObjectiveTable.BlockDamageCurve, r => r.Level == 5L);
            Assert.False(broken.Usable);
            Assert.Equal("99.9%", broken.BlockReduction);

            Assert.Equal("52.38", Tooltip(5));           // nowhere near 99.9%
            Assert.Equal("99.9", Tooltip(50000));        // the intended rung, confirmed
        }

        [Fact]
        public void The_operator_hard_cap_of_100000_displays_9995_percent()
        {
            // THIS IS amendment 35 §5 ITEM 2, CLOSED. [OPERATOR]'s "caps at 99.95% block" (recorded in
            // amendment 24 §5) is not an approximation and not a conflation — it is the literal string
            // the game's own tooltip prints at the ruled cap.
            Assert.Equal(100000L, ObjectiveTable.AtBlockHardCapLevel);
            Assert.Equal("99.95", Tooltip(ObjectiveTable.AtBlockHardCapLevel));
        }

        [Fact]
        public void The_99_percent_rung_and_the_99_95_percent_cap_are_the_same_curve()
        {
            // amendment 35 §5 item 2 asked whether the 100k ↔ 99.95% correspondence survives contact
            // with the guide's 99%-at-5,000 rung. It does: both are this one curve at f = 0.01, and
            // both stated LEVELS are the exact level rounded UP to a round number.
            //   99%    is exact at 4,900  -> guide says "5k"     (the ceil(49/f) BlockStopLevel used)
            //   99.95% is exact at 99,900 -> ruling says "100k"
            Assert.Equal(99f, Reduction(4900));                  // exact, and it is ceil(49/f)
            Assert.Equal("99.02", Tooltip(5000));                // the guide's rounded-up level
            Assert.Equal("99.95", Tooltip(99900));               // exact 99.95%
            Assert.Equal("99.95", Tooltip(100000));              // the ruled cap, same display

            // The curve is ASYMPTOTIC (amendment 35 §1) — strictly increasing forever, never 100%.
            Assert.True(Reduction(100000) > Reduction(99900));
            Assert.True(Reduction(1000000) > Reduction(100000));
            Assert.True(Reduction(1000000) < 100f);

            // …but the tooltip's "##.0#" rounds to "100.0" from 1,000,000, which is exactly what the
            // guide's last row means by "the UI rounds the display to 100%".
            Assert.Equal("100.0", Tooltip(1000000));
        }

        [Fact]
        public void The_community_formula_is_this_curve_with_levelFactor_one_hundredth()
        {
            // amendment 35 §2.1 records [OPERATOR]'s community formula as
            //     Block Damage Reduction = (Level + 50) / (Level + 100)
            // and notes it "omits levelFactor". It does not merely approximate the decomp — it is
            // ALGEBRAICALLY IDENTICAL to it at f = 0.01, which is why the measurement and the community
            // documentation corroborate each other:
            //     0.5 / (1 + fL) = 50 / (L + 100)   <=>   1 + fL = (L + 100)/100   <=>   f = 1/100
            //
            // ⚠ amendment 35 §2.1 states that equivalence as "1 + levelFactor × L equal (L + 100)/50".
            // THAT IS OFF BY A FACTOR OF TWO — at L = 0 it gives 2 where the curve gives 1. The correct
            // denominator is 100, as asserted here.
            foreach (long L in new[] { 0L, 1L, 400L, 4900L, 5000L, 50000L, 99900L, 100000L, 500000L })
            {
                double community = (L + 50.0) / (L + 100.0);
                double decomp = 1.0 - 0.5 / (1.0 + 0.01 * L);
                Assert.Equal(community, decomp, 12);

                double statedIdentity = (L + 100.0) / 100.0;
                Assert.Equal(statedIdentity, 1.0 + 0.01 * L, 12);
            }
        }
    }
}
