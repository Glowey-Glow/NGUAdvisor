using System;

namespace NGUAdvisor.Managers
{
    // TURNING "I MUST SURVIVE THIS FIGHT" INTO A NUMBER THE GEAR SOLVER CAN USE.
    //
    // A floor is evaluated against GEAR STAT TOTALS — what the worn items contribute. But the thing an
    // operator actually needs to clear is an ADVENTURE stat, and those are different numbers separated
    // by a dozen multipliers ([DECOMP] Character.totalAdvAttack):
    //
    //     totalAdvAttack = (adventure.attack + gearBonus + cubePower)
    //                    × AT × NGU × NGU2 × beards × diggers × challenges
    //                    × itopod × beastMode × beastQuest × macguffin × hacks × wishes × cards
    //
    // Gear enters as ONE ADDITIVE TERM inside the bracket. Everything after it is a product, and — this
    // is the load-bearing fact — every one of those factors is IDENTICAL for every candidate loadout.
    // Beards, diggers, hacks, wishes, cards and the ITOPOD are separate systems; the gear optimizer
    // moves weapons, armour and accessories and touches none of them. So for one solve the whole tail
    // is a constant, and the conversion is exact rather than approximate:
    //
    //     requiredGear = requiredAdventureStat / multiplier − nonGearBase
    //
    // ⚠ A WRONG MULTIPLIER IS A SILENT WRONG ANSWER. It does not throw and it does not look odd — the
    // floor is simply in the wrong units, and a set that cannot survive comes back marked feasible.
    // That is the same failure shape that has already cost two rounds on this feature, so the
    // arithmetic is a pure function with named inputs, tested on its own, and the live read that
    // supplies those inputs is deliberately a separate, inspectable step.
    //
    // The multiplier is DERIVED, not reconstructed. Multiplying the dozen factors back together by hand
    // would mean maintaining a copy of a game formula that changes between versions — the exact
    // duplication that produced the titan-version defect. Instead it is measured: divide the stat the
    // game reports by the bracket the game exposes. If the game adds a fifteenth multiplier tomorrow,
    // this keeps working and nothing here needs to know.
    public static class AdventureFloor
    {
        // The floor value the solver compares against, in the units it actually measures.
        //
        // ⚠ THE FLOOR IS ON THE BRACKET, NOT ON THE GEAR TERM. GearOptimizer.FloorStats scores
        // WornList(), and WornList appends the cube and the nude base to the candidate items — so the
        // "Power" it reports is already (candidate gear + cubePower + adventure.attack), the whole
        // bracket. Subtracting the non-gear base here as well would remove it twice and produce a floor
        // far below the real requirement, marking unsurvivable sets feasible. There is deliberately only
        // ONE conversion function: two, one gear-only and one bracket, is an invitation to pick the
        // wrong one, and picking wrong is silent.
        //
        // NaN means "cannot be expressed" — a caller must treat that as "no floor", never as zero. Zero
        // is a floor every set trivially clears, which is the dangerous direction: an absent constraint
        // that reads as a satisfied one.
        public static double RequiredBracket(double requiredStat, double multiplier)
        {
            if (double.IsNaN(requiredStat) || double.IsNaN(multiplier)) return double.NaN;
            if (multiplier <= 0 || double.IsInfinity(multiplier)) return double.NaN;
            if (requiredStat <= 0) return double.NaN;      // no requirement is not a floor of zero

            double need = requiredStat / multiplier;
            return (double.IsNaN(need) || double.IsInfinity(need)) ? double.NaN : need;
        }

        // The measured multiplier: what the game turns one point of the bracket into.
        // total / bracket, with the bracket taken from the same live reads the total is built from.
        public static double MultiplierFrom(double totalStat, double bracket)
        {
            if (bracket <= 0 || double.IsNaN(bracket) || double.IsNaN(totalStat)) return double.NaN;
            if (double.IsInfinity(totalStat) || double.IsInfinity(bracket)) return double.NaN;
            double m = totalStat / bracket;
            return (m <= 0 || double.IsNaN(m) || double.IsInfinity(m)) ? double.NaN : m;
        }

        // The live read lives in AdventureFloorReader — a Character read in this file would make the
        // arithmetic above unlinkable by the test project, and this is precisely the arithmetic that
        // has to be checkable without a game build.
        public struct Reading
        {
            public double Multiplier;     // the constant tail, measured
            public double NonGearBase;    // adventure.attack (or defense) + cube: the bracket minus gear
            public bool Known;
        }
    }
}
