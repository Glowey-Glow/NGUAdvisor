using System;

namespace NGUAdvisor.Managers
{
    // BR's and RitualBP's Unity-free decision core (audit 01 §3.4, extraction step 4).
    //
    // WHY THIS MATTERS BEYOND PARITY. Decision record amendment 01 makes these two lanes the whole of
    // the magic layer's blood-facing surface: at the magic layer, Counterfeit Gold / Spaghetti / Iron
    // Pill / Number Boost are ONE consumer (BR), not four, because none of them consumes magic. M1 —
    // "blood produced per unit of magic through rituals, as a rate" — is the hard prerequisite for
    // G1-D3, and it has to be written against the ritual funding decision. That decision is in here now
    // instead of behind the Character weld.
    //
    // PARITY ONLY. M1 is NOT written here. Nothing in this file converts magic to blood or expresses a
    // rate; RitualDecide is still the duration/feasibility filter it always was.
    public static class RitualMath
    {
        // What CastRituals decides for one ritual, before any allocation happens.
        public enum RitualAction
        {
            // Fund it: allocate up to the cap and add magic.
            Fund,
            // Skip it, and strip any magic already parked in it. NOTE the original does BOTH of these
            // for every skip reason — a ritual skipped because it is merely too slow this pass is
            // drained exactly like one that cannot be afforded.
            SkipAndDrain,
            // Not reached in the loop at all: locked, or the budget/pool ran out.
            NotConsidered
        }

        // One ritual as the funding decision sees it. The caller resolves these from the live
        // bloodMagic/bloodMagicController before calling.
        public struct RitualState
        {
            public int Id;
            public bool Unlocked;            // Id < bloodMagicController.ritualsUnlocked()
            public double GoldCost;          // bloodMagics[i].baseCost * totalDiscount()
            public double Progress;          // bloodMagic.ritual[i].progress
        }

        // BR.CastRituals' per-ritual ladder, verbatim, minus the game calls. `secondsToRun` is BR's
        // Index — the token's `-n` suffix, which is a DURATION, not a priority or a share (05 §3; the
        // amendment's P2 asks for it to be separated from the token's other two meanings).
        //
        // `rebirthDeadlineSec` <= 0 means no scheduled rebirth. `nowSec` is rebirthTime.totalseconds.
        //
        // timeLeftSec is LAZY, for the same reason BloodPillMath.DecideCast's predicates are: the
        // original never computed it unless the gold gate passed, and computing it means running the
        // whole ritual-rate stack (totalMagicPower, the difficulty dividers, sadisticDivider,
        // totalBloodMagicSpeedBonus) against the game. Deferring keeps the extraction observationally
        // identical, not merely arithmetically identical. It is invoked at most ONCE per call.
        public static RitualAction RitualDecide(in RitualState r, double gold, int secondsToRun,
                                                double nowSec, double rebirthDeadlineSec, Func<double> timeLeftSec)
        {
            if (!r.Unlocked) return RitualAction.NotConsidered;

            // Ritual costs too much gold
            if (r.GoldCost > gold && r.Progress <= 0.0)
                return RitualAction.SkipAndDrain;

            double tLeft = timeLeftSec();

            // Ritual will not finish before rebirth
            double completeTime = nowSec + tLeft;
            if (rebirthDeadlineSec > 0 && completeTime > rebirthDeadlineSec)
                return RitualAction.SkipAndDrain;

            // If the runtime is explicitly set, ignore the breakpoint logic
            if (secondsToRun > 0)
            {
                // The time left is more than the configured number of seconds to run
                if (tLeft > secondsToRun)
                    return RitualAction.SkipAndDrain;
            }
            // The time left is more than an hour
            else if (tLeft > 3600)
            {
                return RitualAction.SkipAndDrain;
            }

            return RitualAction.Fund;
        }

        // BR.RitualProgressPerTick's arithmetic. The difficulty branch is resolved by the caller into
        // (DividerScale, SpeedDivider) exactly as AugmentMath.AugCap does: normal and evil scale by
        // 50000.0, sadistic does not and additionally divides by the ritual's sadisticDivider().
        public struct RitualRateInputs
        {
            public long Remaining;          // the magic still unspent this pass
            public double TotalMagicPower;
            public double DividerScale;     // 50000.0 on normal/evil, 1.0 on sadistic
            public double SpeedDivider;
            public bool Sadistic;
            public double SadisticDivider;  // bloodMagics[id].sadisticDivider(), only read when Sadistic
            public double SpeedBonus;       // bloodMagics[id].totalBloodMagicSpeedBonus()
        }

        // Returns a FLOAT, as the game does — the clamps at float.MaxValue and 0 are part of the
        // contract, not defensive padding.
        public static float ProgressPerTick(in RitualRateInputs a)
        {
            var num1 = a.Remaining * a.TotalMagicPower;
            num1 /= a.DividerScale * a.SpeedDivider;
            if (a.Sadistic)
                num1 /= a.SadisticDivider;

            var num2 = num1 * a.SpeedBonus;

            if (num2 <= 0.0)
                num2 = 0.0;

            if (num2 >= float.MaxValue)
                num2 = float.MaxValue;

            return (float)num2;
        }

        // Seconds until this ritual completes at the given rate. 50 ticks/s.
        //
        // CHARACTERISED: with progressPerTick == 0 (no magic left, or an unreadable rate) this divides
        // by zero and returns +Infinity, which every caller then compares against a finite bound — so
        // it skips. That is the current behaviour and it happens to be the safe one; it is not guarded.
        public static float TimeLeft(double progress, float progressPerTick) =>
            (float)((1.0 - progress) / progressPerTick / 50.0);

        // BR.CalculateMaxAllocation. Note this is NOT the stair-snap idiom the other lanes use: there is
        // no 1.00000202655792 epsilon and the fallback branch adds a bare +1.
        //
        // CHARACTERISED: report 02 §12.4 identifies this `+ 1L` as the ONE wrong variant among the
        // eight inlined copies of the stair-snap formula (the others multiply by the game-verbatim
        // epsilon instead). It is preserved exactly — correcting it changes ritual allocation.
        public static long MaxAllocationFor(long capValue, long remaining)
        {
            if (remaining > capValue)
                return capValue;

            return (long)(capValue / Math.Ceiling(capValue / (double)remaining)) + 1L;
        }

        // ---------------------------------------------------------------------------------------
        // RitualBP — the per-ritual RIT-n lane. Same system, different token, different arithmetic.
        // ---------------------------------------------------------------------------------------

        // `index >= 0`: ParseBreakpointArray yields Index = -1 for a malformed token
        // (RIT-x, RIT-, RIT-abc). `-1 < ritualsUnlocked()` passes, the lane reports
        // unlocked, and RitualBP.Allocate() then throws on bloodMagics[-1].baseCost —
        // killing the whole magic lane until profile reload. Same sentinel class as
        // HACK-x (audit 09 §5). advisors/02:718.
       public static bool RitualIndexUnlocked(int index, int ritualsUnlocked) => index >= 0 && index < ritualsUnlocked;

        // RitualBP.Allocate's gold gate: refuse (and drain) a ritual we cannot pay for that has not
        // already started. Returns true to REFUSE.
        public static bool RitualGoldGateBlocks(double goldCost, double gold, double progress) =>
            goldCost > gold && progress <= 0;

        public struct RitualCapInputs
        {
            public double TotalMagicPower;
            public double SpeedBonus;       // bloodMagics[index].totalBloodMagicSpeedBonus()
            public double DifficultyTerm;   // 50000.0*divider on normal/evil; sadisticDivider*divider on
                                            // sadistic; see UnknownDifficulty below
            public bool UnknownDifficulty;  // rebirthDifficulty matched none of the three -> return 0
            public long MaxAllocation;
            public long IdleMagic;
        }

        // RitualBP.GetRitualCap, verbatim.
        //
        // CHARACTERISED: the `else return 0L` branch. If rebirthDifficulty is none of normal/evil/
        // sadistic the cap is ZERO, and Allocate then calls SetInput(Math.Min(0, MaxAllocation)) and
        // adds nothing — a silent no-op rather than a fallback. Unreachable with today's three-valued
        // enum, but it is the shape of the code and it is preserved.
        public static long RitualCap(in RitualCapInputs a)
        {
            if (a.UnknownDifficulty)
                return 0L;

            var num = 1 / (a.TotalMagicPower * a.SpeedBonus);
            num *= a.DifficultyTerm;

            num = Math.Ceiling(num);

            if (num < 1.0)
                num = 1.0;

            var num1 = Math.Ceiling(num / Math.Ceiling(num / a.MaxAllocation) * 1.00000202655792);

            return num1 > a.IdleMagic ? a.IdleMagic : (long)num1;
        }
    }
}
