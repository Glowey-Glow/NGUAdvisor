using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless guard for the Wandoos OS comparator's arithmetic. Three things are pinned here:
    //
    //  * the three bonus curves and the per-tick level clamp, verbatim from the game
    //    (reference/decomp-full/Wandoos98Controller.cs), so a refactor can't quietly re-derive them;
    //  * WHERE the clamp binds, at both Normal and Evil base times. This is the load-bearing fact:
    //    a saturated OS does not respond to speed at all, which is why a "uniform" speed change is
    //    non-uniform across the three options and why an Advanced Training level landing mid-window
    //    is worth modelling at all;
    //  * that feeding the Advanced Training Wandoos slots moves the recommendation UP the OS ladder
    //    and never down, and that NOT feeding them is an exact, bit-identical no-op.
    public class WandoosMathTests
    {
        private static readonly double[] Normal = { 1e9, 1e12, 1e15 };
        private static readonly double[] Evil = { 1e21, 1e27, 1e33 };
        private static readonly bool[] AllUnlocked = { true, true, true };

        private const double TwoHours = 7200.0;

        // cap*speed folded into one number X, since only the product enters the projection.
        private static WandoosMath.Scenario Sc(double[] baseTimes, double x, double seconds = TwoHours,
                                               int curOs = 0, double banked = 0,
                                               double[] curve = null)
            => new WandoosMath.Scenario
            {
                BaseTimes = baseTimes,
                CapE = x,
                CapM = x,
                SpeedE = 1,
                SpeedM = 1,
                Seconds = seconds,
                CurOs = curOs,
                BankedE = banked,
                BankedM = banked,
                CurveE = curve,
                CurveM = curve
            };

        private static (int best, double advantage, double[] bonus) Run(WandoosMath.Scenario s)
        {
            var lE = new double[3];
            var lM = new double[3];
            var bonus = new double[3];
            WandoosMath.Project(s, lE, lM, bonus);
            int best = WandoosMath.BestOs(bonus, AllUnlocked);
            return (best, bonus[best] / bonus[s.CurOs], bonus);
        }

        private static double[] Flat(double k)
        {
            var c = new double[WandoosMath.Steps];
            for (int i = 0; i < c.Length; i++) c[i] = k;
            return c;
        }

        // The multiplier curve an AT slot actually produces: level L(t) = sqrt((L0+1)^2 + 2Rt) - 1
        // (AtHourPlanner's forecaster), turned into (1 + f*L(t)) / (1 + f*L0).
        private static double[] AtRamp(double l0, double r, double f, double seconds = TwoHours)
        {
            var t = WandoosMath.SampleTimes(seconds);
            var levels = new double[t.Length];
            for (int i = 0; i < t.Length; i++)
                levels[i] = Math.Sqrt((l0 + 1) * (l0 + 1) + 2 * r * t[i]) - 1;
            return WandoosMath.AtSpeedCurve(levels, f, f * l0, l0);
        }

        // ---- the game's three bonus curves ----

        [Fact]
        public void Bonus_curves_are_the_games_own()
        {
            // 98: ((1+E/100)(1+M/25))^0.8
            Assert.Equal(Math.Pow((1 + 500 / 100.0) * (1 + 300 / 25.0), 0.8),
                         WandoosMath.BonusFor(0, 500, 300), 9);
            // MEH: (1+E/5)(1+2M)
            Assert.Equal((1 + 500 / 5.0) * (1 + 2 * 300.0), WandoosMath.BonusFor(1, 500, 300), 9);
            // XL: ((1+6E)(1+40M))^1.05
            Assert.Equal(Math.Pow((1 + 6 * 500.0) * (1 + 40 * 300.0), 1.05),
                         WandoosMath.BonusFor(2, 500, 300), 6);
        }

        [Fact]
        public void Zero_levels_is_a_flat_one_on_every_os()
        {
            for (int os = 0; os < 3; os++)
                Assert.Equal(1.0, WandoosMath.BonusFor(os, 0, 0), 12);
        }

        // ---- the per-tick clamp ----

        [Fact]
        public void Below_saturation_levels_are_linear_in_speed()
        {
            // p = 0.1 -> 5 levels/sec
            Assert.Equal(0.1 * 50 * TwoHours, WandoosMath.LevelsGained(1e8, 1, 1e9, TwoHours, null), 6);
            Assert.Equal(0.2 * 50 * TwoHours, WandoosMath.LevelsGained(2e8, 1, 1e9, TwoHours, null), 6);
        }

        [Fact]
        public void At_and_above_saturation_levels_stop_responding_to_speed()
        {
            // advanceEnergyProgress() sets energyProgress = 0 on level-up, it does NOT subtract 1,
            // so a tick can never bank more than one level: 50 levels/sec is the hard ceiling.
            double ceiling = 50 * TwoHours;
            Assert.Equal(ceiling, WandoosMath.LevelsGained(1e9, 1, 1e9, TwoHours, null), 6);
            Assert.Equal(ceiling, WandoosMath.LevelsGained(1e12, 1, 1e9, TwoHours, null), 6);
            Assert.Equal(ceiling, WandoosMath.LevelsGained(1e30, 1, 1e9, TwoHours, null), 6);
        }

        // The brief this was built from assumed the clamp is a Normal-only concern and that "on Evil
        // the base times are large enough that nothing is cap-limited". It is not a difficulty
        // property at all — it is a cap*speed property, and the Evil table is the Normal one shifted
        // 12 decades up. Both are pinned so that claim can't come back.
        [Theory]
        // X (= cap * speed), which OSs are saturated: C = saturated
        [InlineData(1e8, "...")]
        [InlineData(1e9, "C..")]
        [InlineData(1e11, "C..")]
        [InlineData(1e12, "CC.")]
        [InlineData(1e15, "CCC")]
        public void Saturation_regimes_at_normal_base_times(double x, string expected)
            => Assert.Equal(expected, Regime(Normal, x));

        [Theory]
        [InlineData(1e20, "...")]
        [InlineData(1e21, "C..")]
        [InlineData(1e24, "C..")]
        [InlineData(1e27, "CC.")]
        [InlineData(1e33, "CCC")]
        public void Saturation_regimes_at_evil_base_times(double x, string expected)
            => Assert.Equal(expected, Regime(Evil, x));

        private static string Regime(double[] baseTimes, double x)
        {
            string s = "";
            double ceiling = 50 * TwoHours;
            for (int os = 0; os < 3; os++)
                s += WandoosMath.LevelsGained(x, 1, baseTimes[os], TwoHours, null) >= ceiling ? "C" : ".";
            return s;
        }

        // ---- the AT speed curve ----

        [Fact]
        public void An_at_slot_that_is_not_moving_produces_no_curve_at_all()
        {
            // Not merely "a curve of 1.0s": null, so LevelsGained takes its exact closed form. An
            // all-ones curve would go through the Riemann sum and land 2e-12 off (verified), which
            // is enough to perturb a knife-edge comparison. The no-op has to be bit-exact.
            var flatLevels = new double[WandoosMath.Steps];
            for (int i = 0; i < flatLevels.Length; i++) flatLevels[i] = 500;
            Assert.Null(WandoosMath.AtSpeedCurve(flatLevels, 2.0, 1000, 500));

            Assert.Null(WandoosMath.AtSpeedCurve(null, 2.0, 0, 0));
            Assert.Null(WandoosMath.AtSpeedCurve(new double[0], 2.0, 0, 0));
            Assert.Null(WandoosMath.AtSpeedCurve(flatLevels, 0.0, 0, 500));    // levelFactor unreadable
            Assert.Null(WandoosMath.AtSpeedCurve(flatLevels, -1.0, 0, 500));
        }

        [Fact]
        public void The_closed_form_and_a_no_op_curve_are_not_interchangeable()
        {
            double closed = WandoosMath.LevelsGained(1e10, 1, 1e12, TwoHours, null);
            double summed = WandoosMath.LevelsGained(1e10, 1, 1e12, TwoHours, Flat(1.0));
            Assert.Equal(closed, summed, 6);
            Assert.NotEqual(closed, summed);   // ...but not bit-identical - hence the null contract
        }

        [Fact]
        public void At_curve_is_relative_to_the_bonus_already_inside_the_sampled_speed()
        {
            // trainingBonus(0) = levelFactor * L0 is already baked into totalWandoosEnergySpeed(),
            // so the curve must start at ~1 and only describe the ADDITIONAL levels.
            var curve = AtRamp(l0: 400, r: 40, f: 2.0);
            Assert.NotNull(curve);
            Assert.Equal(1.0, curve[0], 1);
            Assert.True(curve[0] < 1.05, $"curve starts at {curve[0]}, should be ~1");
            Assert.True(curve[curve.Length - 1] > curve[0], "an AT slot being fed must ramp up");
        }

        [Fact]
        public void At_curve_never_drops_below_one()
        {
            // A slot sitting past its level target projects BELOW its current level (LevelAt clamps
            // at the target). Wandoos speed does not fall inside a run, so the curve floors at 1 --
            // and a curve that is all floor is no curve at all.
            var falling = new double[WandoosMath.Steps];
            for (int i = 0; i < falling.Length; i++) falling[i] = 100;   // target 100 < current 500
            Assert.Null(WandoosMath.AtSpeedCurve(falling, 2.0, /*bonusNow = f*L0*/ 2.0 * 500, 500));

            // Mixed: the floor applies per sample, it does not discard the samples that do rise.
            var mixed = new[] { 100.0, 500.0, 900.0 };
            var curve = WandoosMath.AtSpeedCurve(mixed, 2.0, 2.0 * 500, 500);
            Assert.Equal(1.0, curve[0], 9);
            Assert.Equal(1.0, curve[1], 9);
            Assert.Equal((1 + 2.0 * 900) / (1 + 2.0 * 500), curve[2], 9);
        }

        [Fact]
        public void Levels_gained_floors_a_sub_unit_multiplier_too()
        {
            // Belt and braces: even if a caller hands in a curve dipping under 1, the projection
            // must not model Wandoos slowing down.
            var dip = Flat(1.0);
            dip[0] = 0.25;
            Assert.Equal(WandoosMath.LevelsGained(0.1, 1, 1, TwoHours, Flat(1.0)),
                         WandoosMath.LevelsGained(0.1, 1, 1, TwoHours, dip), 9);
        }

        [Fact]
        public void At_curve_matches_the_linear_training_bonus_not_the_adventure_curve()
        {
            // AdvancedTrainingController.trainingBonus() = levelFactor * level - LINEAR. The
            // 0.1*L^0.4 curve AtHourPlanner.Ratio() uses is the ADVENTURE Power/Toughness bonus and
            // belongs to slots 0/1 only; using it here would be wrong by orders of magnitude.
            var levels = new double[] { 1000 };
            double f = 2.0, l0 = 500;
            var curve = WandoosMath.AtSpeedCurve(levels, f, f * l0, l0);
            Assert.Equal((1 + f * 1000) / (1 + f * 500), curve[0], 9);

            double adventureShaped = (1 + 0.1 * Math.Pow(1000, 0.4)) / (1 + 0.1 * Math.Pow(500, 0.4));
            Assert.True(Math.Abs(curve[0] - adventureShaped) > 0.5,
                        "the two curves must be visibly different, or this test proves nothing");
        }

        // ---- integration: a mid-window step is not a uniform multiplier ----

        [Fact]
        public void A_mid_window_step_is_not_the_same_as_its_average_when_an_os_saturates()
        {
            // Speed doubles halfway through: mean multiplier 1.5.
            var step = new double[64];
            for (int i = 0; i < 32; i++) step[i] = 1.0;
            for (int i = 32; i < 64; i++) step[i] = 2.0;

            // p0 = 0.6 -> the second half saturates (0.6*2 = 1.2 -> clamped to 1).
            //   piecewise: 0.5*0.6 + 0.5*1.0 = 0.80
            //   average:   min(0.6*1.5, 1)   = 0.90   -> 12.5% too high
            double piecewise = WandoosMath.LevelsGained(0.6, 1, 1, TwoHours, step);
            double average = WandoosMath.LevelsGained(0.6 * 1.5, 1, 1, TwoHours, null);
            Assert.Equal(0.80 * 50 * TwoHours, piecewise, 6);
            Assert.Equal(0.90 * 50 * TwoHours, average, 6);
            Assert.Equal(1.125, average / piecewise, 9);
        }

        [Fact]
        public void A_mid_window_step_equals_its_average_when_nothing_saturates()
        {
            var step = new double[64];
            for (int i = 0; i < 32; i++) step[i] = 1.0;
            for (int i = 32; i < 64; i++) step[i] = 2.0;

            // p0 = 0.3: 0.3 and 0.6 both stay under the clamp, so min() is the identity and the
            // integral is linear -> the two agree exactly. The clamp is the ONLY reason to integrate.
            Assert.Equal(WandoosMath.LevelsGained(0.3 * 1.5, 1, 1, TwoHours, null),
                         WandoosMath.LevelsGained(0.3, 1, 1, TwoHours, step), 6);

            // ...and once EVERYTHING saturates they agree again, at the ceiling.
            Assert.Equal(WandoosMath.LevelsGained(5 * 1.5, 1, 1, TwoHours, null),
                         WandoosMath.LevelsGained(5, 1, 1, TwoHours, step), 6);
        }

        // ---- what a speed change does to the recommendation ----

        // THE load-bearing fact. A speed change is not a uniform nudge to three comparable options:
        // what it does to the advantage ratio is decided entirely by which of current/best is
        // saturated. The middle two rows are why modelling AT inside the window is worth anything.
        [Fact]
        public void Advantage_response_to_speed_is_set_by_which_os_is_saturated()
        {
            const double k = 1.2;

            // 98 (current) saturated, MEH (best) not: only MEH's levels move, bonus ~ levels^2.0.
            AssertAdvantageScaling(x: Math.Pow(10, 10.5), k, expected: Math.Pow(k, 2.0), tolerance: 1e-3);

            // 98 and MEH both saturated: speed changes nothing at all.
            AssertAdvantageScaling(x: Math.Pow(10, 12.5), k, expected: 1.0);

            // 98 and MEH saturated, XL (best) not: XL's bonus goes as levels^2.1.
            AssertAdvantageScaling(x: Math.Pow(10, 14.0), k, expected: Math.Pow(k, 2.1), tolerance: 1e-3);

            // everything saturated: speed is irrelevant, the answer is fixed.
            AssertAdvantageScaling(x: Math.Pow(10, 16.0), k, expected: 1.0);
        }

        private static void AssertAdvantageScaling(double x, double k, double expected, double tolerance = 1e-9)
        {
            var flat = Run(Sc(Normal, x));
            var boosted = Run(Sc(Normal, x, curve: Flat(k)));
            double ratio = boosted.advantage / flat.advantage;
            Assert.True(Math.Abs(ratio - expected) <= tolerance * Math.Max(1, expected),
                        $"X={x:e2}: advantage scaled by {ratio:0.0000}, expected {expected:0.0000}");
        }

        [Fact]
        public void An_at_ramp_moves_the_recommendation_up_the_ladder_never_down()
        {
            var ramp = AtRamp(l0: 400, r: 40, f: 2.0);
            Assert.NotNull(ramp);

            for (double lx = 6.0; lx <= 18.0; lx += 0.05)
            {
                double x = Math.Pow(10, lx);
                int flat = Run(Sc(Normal, x)).best;
                int boosted = Run(Sc(Normal, x, curve: ramp)).best;
                Assert.True(boosted >= flat,
                            $"X=1e{lx:0.00}: an AT boost recommended a CHEAPER OS ({flat} -> {boosted})");
            }
        }

        [Fact]
        public void An_at_ramp_flips_a_recommendation_that_is_sitting_just_under_the_crossover()
        {
            // Flat speed puts the 98 -> MEH crossover at X = 1e9.728 (2h window, no banked levels).
            double justBelow = Math.Pow(10, 9.70);
            var ramp = AtRamp(l0: 400, r: 40, f: 2.0);

            var flat = Run(Sc(Normal, justBelow));
            Assert.Equal(0, flat.best);                       // "98 is best for your cap"
            Assert.Equal(1.0, flat.advantage, 9);

            var withAt = Run(Sc(Normal, justBelow, curve: ramp));
            Assert.Equal(1, withAt.best);                     // "switch 98 -> MEH"
            Assert.True(withAt.advantage > 1.25,
                        $"advantage {withAt.advantage:0.000} should clear the 1.25x auto-switch gate");
        }

        // ---- bookkeeping ----

        [Fact]
        public void Only_the_current_os_keeps_its_banked_levels()
        {
            var lE = new double[3];
            var lM = new double[3];
            var bonus = new double[3];
            WandoosMath.Project(Sc(Normal, 1e8, curOs: 1, banked: 12345), lE, lM, bonus);

            double earned = WandoosMath.LevelsGained(1e8, 1, Normal[1], TwoHours, null);
            Assert.Equal(earned + 12345, lE[1], 6);
            Assert.Equal(WandoosMath.LevelsGained(1e8, 1, Normal[0], TwoHours, null), lE[0], 6);
            Assert.Equal(WandoosMath.LevelsGained(1e8, 1, Normal[2], TwoHours, null), lE[2], 6);
        }

        [Fact]
        public void Best_os_ignores_options_that_are_not_unlocked()
        {
            var bonus = new[] { 10.0, 100.0, 1000.0 };
            Assert.Equal(2, WandoosMath.BestOs(bonus, new[] { true, true, true }));
            Assert.Equal(1, WandoosMath.BestOs(bonus, new[] { true, true, false }));
            Assert.Equal(0, WandoosMath.BestOs(bonus, new[] { true, false, false }));
        }

        [Fact]
        public void Degenerate_inputs_project_zero_rather_than_NaN()
        {
            Assert.Equal(0, WandoosMath.LevelsGained(0, 1, 1e9, TwoHours, null));
            Assert.Equal(0, WandoosMath.LevelsGained(1e9, 0, 1e9, TwoHours, null));
            Assert.Equal(0, WandoosMath.LevelsGained(1e9, 1, 0, TwoHours, null));
            Assert.Equal(0, WandoosMath.LevelsGained(1e9, 1, 1e9, 0, null));
            Assert.Equal(0, WandoosMath.LevelsGained(double.NaN, 1, 1e9, TwoHours, null));

            var bonus = new double[3];
            WandoosMath.Project(Sc(Normal, 0), new double[3], new double[3], bonus);
            foreach (var b in bonus) Assert.Equal(1.0, b, 12);
        }

        [Fact]
        public void A_NaN_inside_the_speed_curve_degrades_to_no_boost()
        {
            var curve = Flat(1.5);
            curve[10] = double.NaN;
            double v = WandoosMath.LevelsGained(0.1, 1, 1, TwoHours, curve);
            Assert.False(double.IsNaN(v));
            Assert.True(v > 0);
        }
    }
}
