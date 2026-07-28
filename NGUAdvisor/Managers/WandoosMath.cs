using System;

namespace NGUAdvisor.Managers
{
    // Unity-free core of the Wandoos OS comparator, split out so the arithmetic that decides the
    // recommendation is headless-tested instead of only reachable through a live Character (same
    // split as TitanTables / DiggerMath / ExpRatio). WandoosAdvisor does the game reads and hands
    // the numbers here.
    //
    // The game's math (reference/decomp-full/Wandoos98Controller.cs):
    //   every 0.02s tick   energyProgress += alloc * totalWandoosSpeed / baseTime
    //   on progress >= 1   level++ and progress RESETS TO 0 (it is NOT decremented), so a tick can
    //                      never bank more than one level -> levels/sec = 50 * min(p, 1).
    //   bonus              98: ((1+E/100)(1+M/25))^0.8  MEH: (1+E/5)(1+2M)  XL: ((1+6E)(1+40M))^1.05
    //   baseTime           normal 1e9/1e12/1e15   evil+ 1e21/1e27/1e33
    //
    // WHY THE CLAMP IS THE INTERESTING PART. min(p,1) is not a rare edge case — it is the normal
    // state of the cheaper OSs. On a real account one or two of the three are saturated (p >= 1)
    // while the next one up is not, and A SATURATED OS DOES NOT RESPOND TO SPEED AT ALL. So a speed
    // change that looks "uniform across the three options" is anything but:
    //
    //   current and best both saturated  ->  advantage ratio unchanged (speed does nothing)
    //   current saturated, best is not   ->  advantage ratio scales as k^2.0 (MEH) / k^2.1 (XL)
    //   neither saturated                ->  advantage ratio scales as k^(2.1-1.6) = k^0.5
    //
    // (The exponents are the large-level asymptotes of the three bonus curves: 98 ~ k^1.6 because
    // both its factors are linear in levels and the product is raised to 0.8, MEH ~ k^2.0, XL ~ k^2.1.)
    //
    // That middle row is why a time-varying speed is integrated WITH THE CLAMP INSIDE the sum rather
    // than folded into one average multiplier: min(avg) != avg(min), and they differ most in exactly
    // the regime where the advisor is deciding whether to leave a saturated cheap OS for an
    // unsaturated expensive one — the decision that matters.
    //
    // The clamp is also not merely "the game discards overflow". Cap-allocating never OVER-fills:
    // Wandoos98Controller.capAmountEnergy() = baseTime/speed + 1 is exactly p = 1, and both the
    // game's addCapEnergy() and the advisor's own WandoosBP allocate min(budget, capAmount). So
    // min(p,1) doubles as a faithful model of what a CAPWAN breakpoint actually puts in.
    public static class WandoosMath
    {
        // Sub-intervals for the piecewise projection. The speed curve is smooth (AT levels follow a
        // sqrt in t), so midpoint sampling at 64 steps is far finer than a comparison needs — a 4h
        // window samples every 3.75 min — and costs a few hundred flops per Compare().
        public const int Steps = 64;

        // Everything Project() needs, so the whole comparison is exercisable without a Character.
        public struct Scenario
        {
            public double[] BaseTimes;      // per OS, from the difficulty table
            public double CapE, CapM;       // assumed allocation (see WandoosAdvisor's header)
            public double SpeedE, SpeedM;   // totalWandoos*Speed at t = 0
            public double Seconds;          // projection window
            public int CurOs;               // the OS installed now — the only one with banked levels
            public double BankedE, BankedM; // its already-earned levels (the others are wiped on swap)
            // Per-sub-interval speed MULTIPLIER relative to Speed*, length Steps. null = speed is
            // constant across the window, which takes the exact closed form instead of the sum.
            public double[] CurveE, CurveM;
        }

        // Midpoints of the Steps sub-intervals of [0, seconds] — where a speed curve is sampled.
        public static double[] SampleTimes(double seconds)
        {
            var t = new double[Steps];
            double dt = seconds / Steps;
            for (int i = 0; i < Steps; i++) t[i] = (i + 0.5) * dt;
            return t;
        }

        // Levels banked over `seconds` at allocation `alloc` and t=0 speed `speed0`, optionally with
        // a per-sub-interval speed multiplier curve. A null/empty curve is the constant-speed closed
        // form and is BIT-IDENTICAL to the pre-existing model, which is what makes "no AT feed" an
        // exact no-op rather than an approximate one.
        public static double LevelsGained(double alloc, double speed0, double baseTime,
                                          double seconds, double[] speedCurve)
        {
            if (alloc <= 0 || speed0 <= 0 || baseTime <= 0 || seconds <= 0) return 0;
            double p0 = alloc * speed0 / baseTime;
            if (double.IsNaN(p0) || p0 <= 0) return 0;

            if (speedCurve == null || speedCurve.Length == 0)
                return Math.Min(p0, 1.0) * 50.0 * seconds;

            double dt = seconds / speedCurve.Length;
            double sum = 0;
            for (int i = 0; i < speedCurve.Length; i++)
            {
                double m = speedCurve[i];
                // A multiplier below 1 would mean the speed DROPS mid-run, which no AT level can
                // cause (levels only rise, and a slot already past its target simply stops).
                if (double.IsNaN(m) || m < 1.0) m = 1.0;
                sum += Math.Min(p0 * m, 1.0);
            }
            return sum * dt * 50.0;
        }

        // Wandoos98Controller.wandoosBonus(), with the levels as inputs instead of live fields.
        public static double BonusFor(int os, double levelsE, double levelsM)
        {
            switch (os)
            {
                case 0: return Math.Pow((1.0 + levelsE / 100.0) * (1.0 + levelsM / 25.0), 0.8);
                case 1: return (1.0 + levelsE / 5.0) * (1.0 + levelsM * 2.0);
                default: return Math.Pow((1.0 + levelsE * 6.0) * (1.0 + levelsM * 40.0), 1.05);
            }
        }

        // Projected E/M levels and A/D bonus for all three OSs at the end of the window. Only CurOs
        // keeps its banked levels; switching to either of the others wipes theirs (changeOS zeroes
        // energyLevel/magicLevel), and without that the current OS is understated and the advantage
        // ratio is biased toward recommending a switch.
        public static void Project(Scenario s, double[] levelsE, double[] levelsM, double[] bonus)
        {
            for (int os = 0; os < 3; os++)
            {
                double bt = s.BaseTimes[os];
                double lE = LevelsGained(s.CapE, s.SpeedE, bt, s.Seconds, s.CurveE);
                double lM = LevelsGained(s.CapM, s.SpeedM, bt, s.Seconds, s.CurveM);
                if (os == s.CurOs) { lE += s.BankedE; lM += s.BankedM; }
                levelsE[os] = lE;
                levelsM[os] = lM;
                bonus[os] = BonusFor(os, lE, lM);
            }
        }

        // Highest-bonus UNLOCKED OS. 98 is always unlocked, so index 0 is a safe starting point.
        public static int BestOs(double[] bonus, bool[] unlocked)
        {
            int best = 0;
            for (int os = 1; os < 3; os++)
                if (unlocked[os] && bonus[os] > bonus[best]) best = os;
            return best;
        }

        // Speed multiplier curve for one Advanced Training slot that feeds Wandoos (slots 3 and 4).
        //
        // The AT->Wandoos bonus is AdvancedTrainingController.trainingBonus() = levelFactor * level,
        // entering totalWandoos*Speed() as (1 + levelFactor*L). It is LINEAR — NOT the 0.1*L^0.4
        // adventure Power/Toughness curve that AtHourPlanner.Ratio() uses for slots 0/1. Same
        // forecaster, different bonus curve.
        //
        // `bonusNow` is the live trainingBonus(0), i.e. the levelFactor*L0 already baked into the
        // sampled speed, so the curve is a pure relative correction and equals 1 at t = 0.
        // Returns null when the slot cannot move the speed, which routes LevelsGained back to its
        // exact constant-speed form.
        public static double[] AtSpeedCurve(double[] projectedLevels, double levelFactor,
                                            double bonusNow, double l0)
        {
            if (projectedLevels == null || projectedLevels.Length == 0) return null;
            if (levelFactor <= 0 || double.IsNaN(levelFactor)) return null;
            if (double.IsNaN(bonusNow) || bonusNow < 0) bonusNow = levelFactor * Math.Max(l0, 0);

            double denom = 1.0 + bonusNow;
            if (denom <= 0) return null;

            var curve = new double[projectedLevels.Length];
            bool moves = false;
            for (int i = 0; i < curve.Length; i++)
            {
                double m = (1.0 + levelFactor * Math.Max(projectedLevels[i], 0)) / denom;
                if (double.IsNaN(m) || m < 1.0) m = 1.0;
                curve[i] = m;
                if (m > 1.000001) moves = true;
            }
            // A slot that is levelling but adds nothing measurable inside the window must leave the
            // projection bit-identical, not merely close to it.
            return moves ? curve : null;
        }
    }
}
