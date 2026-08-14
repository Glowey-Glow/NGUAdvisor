using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // PASS 2 of the constraint layer: per-lane CAPACITY (audit/decisions/constraint-layer-spec.md §5)
    // — how much can a lane absorb before the marginal unit is provably wasted? Unity-free; NOT wired
    // into the live allocation path.
    //
    // Every energy system level-snaps and DISCARDS overflow — progress = 0f, never -= 1 (07 §8) — so
    // "capacity" is a real number the game itself defines for most lanes. This file holds the three
    // pieces that are pure logic: the provenance TABLE saying where each lane's cap comes from (the
    // LaneTargets treatment — inventory as data, asserted by tests, so it cannot drift as prose), the
    // VACUITY test, and the FLOAT STALL FLOORS. The cap VALUES themselves are live reads or existing
    // advisor math (LaneCapMath / AugmentMath / RitualMath / HackMath) — nothing here re-derives one.
    public static class CapacityPass
    {
        // Where a lane's capacity number comes from.
        public enum CapSource
        {
            // The game maintains the cap and ships a helper that returns it. Use it; never re-derive.
            Game,
            // No game-side helper exists; the advisor computes it (the existing *Math cores).
            Advisor,
            // Not a stock of resource but a RATE ceiling: allocation beyond the point where the rate
            // saturates is the waste. Wishes are the one lane with this shape (Math.Min against
            // minimumWishTime(), WishesController.cs:758).
            RateCeiling,
            // Capacity does not apply. Pass 2 REFUSES to produce a number for these lanes — that is
            // the beard rule (spec §6: never allocated to, never capped).
            None
        }

        public struct LaneCapSpec
        {
            public string Lane;      // class name, matching LaneTargets.Table where the lane has a row
            public string Pool;
            public CapSource Source;
            public string Helper;    // what the caller reads, with its decomp/advisor cite — or why not
            public bool FlatCap;     // true ONLY for Wandoos — see the vacuity note below
        }

        // Spec §5.1, held as data. The four GAME rows are the spec's table verbatim; the ADVISOR rows
        // name the existing in-tree math so the eventual assembly cannot go shopping for new
        // derivations; the beard row is the refusal, stated where a caller will trip over it.
        public static readonly LaneCapSpec[] Table =
        {
            new LaneCapSpec { Lane = "NGUBP", Pool = "Energy|Magic", Source = CapSource.Game,
                Helper = "AllNGUController.energyNGUCapAmount(id) / magicNGUCapAmount(id) " +
                         "[DECOMP AllNGUController.cs:1380-1424 / :1426-1470] — (level+1) term at :1385",
                FlatCap = false },

            // CONSTANT, not a function of level: capAmountEnergy() is baseEnergyTime()/speed + 1
            // ([DECOMP] Wandoos98Controller.cs:409-412; magic :434-437) with no level term anywhere.
            new LaneCapSpec { Lane = "WandoosBP", Pool = "Energy|Magic", Source = CapSource.Game,
                Helper = "Wandoos98Controller.capAmountEnergy() / capAmountMagic() " +
                         "[DECOMP Wandoos98Controller.cs:409-412 / :434-437]",
                FlatCap = true },

            new LaneCapSpec { Lane = "BasicTrainingBP", Pool = "Energy", Source = CapSource.Game,
                Helper = "training.attackCaps[s]/defenseCaps[s] via OffenseTraining.cap() " +
                         "[DECOMP OffenseTraining.cs:273-295]; addSum() totals all six slots [:342-352]",
                FlatCap = false },

            new LaneCapSpec { Lane = "Wishes", Pool = "Energy+Magic+R3", Source = CapSource.RateCeiling,
                Helper = "minimumWishTime() [DECOMP WishesController.cs:739-745]; " +
                         "progressPerTick clamps to it at :758",
                FlatCap = false },

            new LaneCapSpec { Lane = "TimeMachineBP", Pool = "Energy", Source = CapSource.Advisor,
                Helper = "LaneCapMath.TimeMachineCap", FlatCap = false },

            new LaneCapSpec { Lane = "TimeMachineBP", Pool = "Magic", Source = CapSource.Advisor,
                Helper = "LaneCapMath.TimeMachineCap", FlatCap = false },

            new LaneCapSpec { Lane = "AugmentBP", Pool = "Energy", Source = CapSource.Advisor,
                Helper = "AugmentMath.AugCap", FlatCap = false },

            new LaneCapSpec { Lane = "BestAug", Pool = "Energy", Source = CapSource.Advisor,
                Helper = "AugmentMath.AugCap, applied to the chosen pair", FlatCap = false },

            new LaneCapSpec { Lane = "AdvancedTrainingBP", Pool = "Energy", Source = CapSource.Advisor,
                Helper = "LaneCapMath.AdvancedTrainingCap", FlatCap = false },

            new LaneCapSpec { Lane = "RitualBP", Pool = "Magic", Source = CapSource.Advisor,
                Helper = "RitualMath.RitualCap", FlatCap = false },

            new LaneCapSpec { Lane = "BR", Pool = "Magic", Source = CapSource.Advisor,
                Helper = "RitualMath.MaxAllocationFor", FlatCap = false },

            new LaneCapSpec { Lane = "HackBP", Pool = "R3", Source = CapSource.Advisor,
                Helper = "HackMath.Saturation", FlatCap = false },

            // Beards allocate NOTHING: there is no beards[id].energy field and no addEnergy (21 §5).
            // Pass 1 applies to them (the full-bar gate — see BeardGate); Pass 2 does not.
            new LaneCapSpec { Lane = "Beards", Pool = "—", Source = CapSource.None,
                Helper = "no beards[id].energy field, no addEnergy — never allocated to, never capped",
                FlatCap = false },
        };

        // The type-level beard refusal: a lane whose Source is None has no capacity for Pass 2 to
        // produce, and there is no function in this file that returns one for it.
        public static bool Allocatable(in LaneCapSpec row) => row.Source != CapSource.None;

        // ---- the vacuity test (spec §5.2) --------------------------------------------------------

        public struct VacuityResult
        {
            // Σ capacity < pool: the allocation question is vacuous — no destination is better than
            // any other. Fill everything and pass Surplus to the surplus sink (Wandoos, §8: the only
            // unterminated consumer, so the layer can never stall).
            public bool Vacuous;
            public long TotalCapacity;   // saturates at long.MaxValue rather than wrapping
            public long Surplus;         // pool - TotalCapacity when vacuous, else 0
        }

        // Every lane's capacity grows with progression — (L+1) cost dividers, gear-driven BT caps —
        // EXCEPT Wandoos', which is flat (the FlatCap row above). So this fires early, when caps are
        // small against the pool, and stops firing as the account matures. That is the intended
        // shape: it is an anti-stall property for young accounts, not a steady state.
        //
        // A negative capacity is a caller bug (caps are counts); it is treated as 0 rather than
        // allowed to shrink the sum, same fail-closed posture as the rest of the layer.
        public static VacuityResult Vacuity(long pool, IEnumerable<long> capacities)
        {
            long total = 0;
            if (capacities != null)
            {
                foreach (var cap in capacities)
                {
                    var c = cap > 0 ? cap : 0;
                    if (total > long.MaxValue - c)
                    {
                        total = long.MaxValue;
                        break;
                    }
                    total += c;
                }
            }

            var vacuous = total < pool;
            return new VacuityResult
            {
                Vacuous = vacuous,
                TotalCapacity = total,
                Surplus = vacuous ? pool - total : 0,
            };
        }

        // ---- float stall floors (spec §5.3) ------------------------------------------------------

        // 2^-25 — half the ULP of the ENTIRE [0.5, 1) binade, which every float progress bar must
        // cross to reach 1f. Same constant, same home as HackMath.StallFloor.
        public const double FinalBinadeFloor = HackMath.StallFloor;

        // The distance from x to the next float above it. Bit-walk rather than log arithmetic so the
        // binade boundaries are exact — Math.Log lands on the wrong side of them. BitConverter's
        // byte round-trip is the net48-safe spelling (SingleToInt32Bits is not in the Framework).
        public static float FloatUlp(float x)
        {
            if (float.IsNaN(x) || float.IsInfinity(x))
                return float.NaN;
            if (x < 0f)
                x = -x;
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(x), 0);
            float next = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            return next - x;
        }

        // ⚠ THE CORRECT FLOOR IS ulp(progress)/2, AND IT DOES NOT SCALE WITH PROGRESS within a
        // binade (spec §5.3). WishManager's shipped guard tests ppt / progress <= 2^-25, which at
        // progress = 0.5 reduces to ppt <= 1.49e-8 while the real stall begins at 2.98e-8 — so a
        // wish at ppt ≈ 2e-8 passes that guard and stalls at exactly 0.5, the failure players report
        // from the field (10 §D4). A wrong floor is worse than no floor, because it looks handled.
        public static double StallFloorAt(float progress) => FloatUlp(progress) / 2.0;

        // Is `progress += ppt` a no-op RIGHT NOW? `<=`, not `<`: at exactly half an ULP,
        // round-to-nearest-even parks the bar on the first even mantissa it meets — one ULP of
        // motion at most, then frozen. (HackMath.WillStall keeps `<` for hacks; the difference is
        // one representable value and that shipped, validated behaviour is not converged here.)
        public static bool StalledNow(double ppt, float progress) =>
            ppt <= StallFloorAt(progress);

        // Will the bar EVER reach 1f at this rate? Absolute, independent of where progress is now:
        // the bar must cross [0.5, 1), whose floor is 2^-25, so a ppt at or below it completes no
        // level even if it is moving today — a wish at progress 0.25 with ppt 2e-8 advances now and
        // parks at 0.5. This is the capacity question ("is the marginal unit provably wasted?");
        // StalledNow is the diagnostic one ("is it frozen this tick?").
        public static bool CannotCompleteLevel(double ppt) =>
            !(ppt > FinalBinadeFloor);

        // The ritual bar floor is the game's own, and it is a CLIFF, not a float artifact: below
        // 1e-9, barFillsPerSecond returns 0 — not a small number
        // ([DECOMP] BloodMagicController.cs:298-304, strict <, mirrored exactly).
        public const float RitualBarFloor = 1E-09f;

        public static bool RitualBarZeroed(float progressPerTick) =>
            progressPerTick < RitualBarFloor;

        // Hacks share the wish binade math; the home for it is HackMath (progress is a float with a
        // bare +=, HacksController.cs:227-271).
        public static bool HackWillStall(double ppt) => HackMath.WillStall(ppt);

        // ---- wish saturation (spec §5.1's rate-ceiling row) --------------------------------------

        // minimumWishTime() is not a time — it is the PPT CEILING: 1 / ((14400 - reductions) * 50)
        // ([DECOMP] WishesController.cs:739-745), and progressPerTick clamps to it with Math.Min
        // (:758). Allocation past the point where the raw rate reaches the ceiling buys nothing —
        // that is the wish lane's whole capacity notion.
        public const float WishBaseMinimumSeconds = 14400f;   // WishesController.cs:741

        public static float WishPptCeiling(float totalMinReduction) =>
            1f / ((WishBaseMinimumSeconds - totalMinReduction) * 50f);

        // The game's own zero-floor, BELOW the real stall floor: rawProgressPerTick under 1e-8
        // returns 0f outright ([DECOMP] WishesController.cs:753-757, strict <). Note the gap: 2^-25
        // ≈ 2.98e-8, so a raw rate in [1e-8, 2.98e-8] passes the game's floor, ticks — and freezes
        // in the [0.5, 1) binade anyway. The stall window the shipped guard misses lives exactly here.
        public const double WishGameZeroFloor = 1E-08;

        // progressPerTick's tail, faithfully: zero-floor first, then the ceiling clamp
        // (WishesController.cs:747-759).
        public static double WishEffectivePpt(double rawPpt, float pptCeiling)
        {
            if (rawPpt < WishGameZeroFloor)
                return 0.0;
            return Math.Min(pptCeiling, rawPpt);
        }

        public static bool WishSaturated(double rawPpt, float pptCeiling) =>
            rawPpt >= pptCeiling;
    }
}
