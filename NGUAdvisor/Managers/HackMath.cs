using System;

namespace NGUAdvisor.Managers
{
    // Unity-free hack arithmetic, so the numbers that decide R3 allocation can be tested headlessly the way
    // DiggerMath and WandoosMath are. Everything here is a port of reference/decomp-full/HacksController.cs.
    //
    // THE ONE FACT THIS FILE EXISTS FOR. updateAllHacks (L227-271) does:
    //
    //     progress += progressPerTick(i);
    //     if (progress >= 1f) { progress = 0f; if (level < hardCap) level++; }
    //
    // progress is RESET, not decremented, so the overflow is thrown away. A hack therefore gains at most one
    // level per tick (50/sec), and every unit of R3 that pushes progressPerTick above 1.0 buys nothing. The
    // game agrees with this reading in its own offline catch-up, which computes ticks-per-level as
    // Mathf.CeilToInt(1f / ppt) (Character.cs:4105) rather than dividing.
    //
    // That makes the level rate a STAIRCASE, not a ramp: 1/ceil(1/ppt) levels per tick. Efficiency against
    // the ideal is 1/(ppt*ceil(1/ppt)), which is 1 only when ppt is exactly 1/n. Just under a step is the
    // worst place to be — ppt 0.9 runs at 55.6%, i.e. it wastes 44% of that hack's allocation for nothing.
    // AdvancedTrainingBP/NGUBP already solve this for their own systems; see SnapToStair.
    //
    // Nothing here reads baseDivider. It is Unity-inspector data with no value anywhere in this repository,
    // so the saturation point is recovered from the game's own progressPerTickCap instead (see Saturation).
    public static class HackMath
    {
        // progress is a float. Across [0.5,1) its ULP is 2^-24, so round-to-nearest swallows any increment
        // below half of that and the bar sticks at 0.5 forever. WishManager guards the same constant for
        // wishes, but with the RELATIVE test ppt/progress ("is it moving now"); the question here is "will it
        // ever finish", which is absolute, because progress must cross that binade to reach 1.
        public const double StallFloor = 1.0 / 33554432.0;   // 2^-25 == 2.98023223876953e-8

        // levelDivider(id) = 1.0078^level * (level+1)  (HacksController.cs:150-158).
        // Returns float.MaxValue past the float range, as the game does.
        public static double LevelDivider(long level)
        {
            if (level < 0) return 1.0;
            double d = Math.Pow(1.0078, level) * (level + 1.0);
            return d > float.MaxValue ? float.MaxValue : d;
        }

        // How much levelDivider grows over `offset` more levels — the ratio that turns "enough R3 to saturate
        // right now" into "enough to still be saturated `offset` levels from now".
        //
        // This is why a clamp cannot simply target the current level: 1.0078^500 is ~49x, and 500 levels is
        // exactly what one 10s allocation window buys at full rate. Clamp to the level you are on and the
        // hack drops off the cap after a single level and idles until the next pass.
        public static double GrowthOverLevels(long level, int offset)
        {
            if (offset <= 0 || level < 0) return 1.0;
            double g = Math.Pow(1.0078, offset) * ((double)level + offset + 1.0) / (level + 1.0);
            return g > float.MaxValue ? float.MaxValue : g;
        }

        // The allocation at which progressPerTick hits exactly 1.0 — one level per tick, the ceiling.
        //
        // progressPerTick and progressPerTickCap share every term except the R3 amount, so their ratio is
        // pure arithmetic and baseDivider, res3Power and the whole hack-speed stack all cancel:
        //     ppt(r) = r * K,  pptCap = cap * K   =>   r_saturating = cap / pptCap
        // Reading pptCap off the live controller is therefore both simpler and more accurate than
        // reconstructing the multiplier stack the way NGUBP has to.
        //
        // pptCap <= 0 means "not computable" (unreadable, or so slow it underflowed); returns 0 so callers
        // fall back to their existing behaviour rather than clamping to a garbage number.
        public static long Saturation(long capRes3, double pptCap)
        {
            if (capRes3 <= 0 || pptCap <= 0 || double.IsNaN(pptCap) || double.IsInfinity(pptCap)) return 0;
            double r = capRes3 / pptCap;
            if (r >= long.MaxValue) return long.MaxValue;
            return r < 1.0 ? 1L : (long)Math.Ceiling(r);
        }

        // Ticks to finish one level at this rate: ceil(1/ppt), matching Character.cs:4105.
        // 0 means "never" — either no rate at all, or below the float stall floor.
        public static long TicksPerLevel(double ppt)
        {
            if (ppt <= 0 || double.IsNaN(ppt) || ppt < StallFloor) return 0;
            if (ppt >= 1.0) return 1;
            double t = Math.Ceiling(1.0 / ppt);
            return t >= long.MaxValue ? 0 : (long)t;
        }

        // Fraction of this allocation that actually turns into levels: 1/(ppt*ceil(1/ppt)).
        // 1.0 exactly on a stair (ppt = 1/n); worst just below one (0.9 -> 0.556).
        public static double Efficiency(double ppt)
        {
            long n = TicksPerLevel(ppt);
            if (n <= 0) return 0;
            double e = 1.0 / (ppt * n);
            return e > 1.0 ? 1.0 : e;
        }

        // Below this a hack accumulates nothing at all, forever. Distinct from "slow": R3 movement never
        // clears progress (removeAllR3 touches only .res3), so a slow hack keeps what it banked.
        public static bool WillStall(double ppt) => !(ppt >= StallFloor);

        // "Get the first milestone, move on" — the guide ch.5 sweep's stopping rule (audit/10 §A1.1
        // row 3, amendment 09 §1), as a predicate MileHackBP can report TargetMet() with.
        //
        // `threshold` is the game's own hacksController.milestoneThreshold(id): the serialized
        // per-hack spacing minus the perk/quirk reducers, i.e. the level of the FIRST milestone.
        // Reducers only ever LOWER it, so a level at-or-past the threshold stays past it — terminal
        // stays terminal, no flap. No zero guard on purpose: the reducer caps keep the threshold >= 8
        // (audit/08 §0 capture; the divide-by-zero ruling in amendment 07), and if it ever were 0 the
        // `>=` reads "done", which fails safe — the lane drops out rather than absorbing forever.
        public static bool FirstMilestoneMet(long level, long threshold) => level >= threshold;

        // Spend as little as possible for the rate you were going to get anyway.
        //
        // With n = ceil(need/budget) chunks, need/n lands ppt on exactly 1/n — a stair peak — so the levels
        // per tick are identical to spending the whole budget while the remainder goes to the next priority.
        // At budget = need/3.5 that is a 12.5% refund for no loss at all.
        //
        // The 1.00000202655792 margin is the one AdvancedTrainingBP:143, NGUBP:122,160, TimeMachineBP:83,111,
        // AugmentBP:137 and RitualBP:48 already ship. It matters because progressPerTick casts the allocation
        // to float: land one ULP low and ceil(1/ppt) rounds up a whole stair, which at n=1 halves the rate —
        // the exact loss this is meant to prevent. Note it therefore returns slightly MORE than need/n; that
        // overshoot is the point, and it is bounded by the margin.
        //
        // The margin is ~2e-6 relative, so it covers the float error exactly at small n — the only place the
        // error is expensive (a lost stair costs 50% at n=1, 33% at n=2). Past a few hundred ticks accumulated
        // error exceeds it and a level can take one extra tick: 0.1% at n=1000, and identical to what the
        // shipped breakpoints above already accept with the same constant.
        public static long SnapToStair(double need, long budget, out long ticksPerLevel)
        {
            ticksPerLevel = 0;
            if (need <= 0 || budget <= 0) return 0;
            if (double.IsNaN(need) || double.IsInfinity(need)) return budget;

            double n = Math.Ceiling(need / budget);
            if (n < 1.0) n = 1.0;
            ticksPerLevel = n >= long.MaxValue ? 0 : (long)n;

            double alloc = Math.Ceiling(need / n * 1.00000202655792);
            if (alloc >= budget) return budget;
            return alloc < 1.0 ? 1L : (long)alloc;
        }
    }
}
