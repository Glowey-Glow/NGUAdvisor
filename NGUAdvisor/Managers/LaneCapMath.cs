using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // The remaining four lanes' Unity-free arithmetic (audit 01 §3.4, extraction step 5):
    // TimeMachineBP, WandoosBP, BasicTrainingBP, AdvancedTrainingBP.
    //
    // PARITY ONLY. Verbatim moves with the live reads lifted into the callers.
    //
    // These four are exactly the lanes 05 §8 lists as having NO value model at all (E3 Time Machine
    // speed, E4 Wandoos, M3 Time Machine gold-multiplier, plus Basic/Advanced Training which the record
    // deliberately leaves off the required list). Nothing here invents one — what comes out is the
    // feasibility arithmetic, which 05 §1 is explicit is NOT a value model. It is the surface those
    // models will have to attach to.
    public static class LaneCapMath
    {
        // The stair-snap epsilon that appears verbatim in the game's own code and in seven of the
        // eight inlined copies in this tree. NOT interchangeable with Wandoos' 1.000002f — see below.
        public const double StairEpsilon = 1.00000202655792;

        public struct CapResult
        {
            public long Num;
            public double PPT;
            public int Offset => (int)Math.Floor(PPT * 50 * 10);
        }

        // The shared tail of every cap calculation: snap `formula` down to a whole number of ticks
        // inside `maxAllocation`, then clamp to the idle pool. Six lanes write this same three-line
        // idiom; this is the one copy they now share.
        public static CapResult SnapAndClamp(double formula, long maxAllocation, long idlePool, double epsilon)
        {
            double num = Math.Ceiling(formula / Math.Ceiling(formula / maxAllocation) * epsilon);
            long n = num > idlePool ? idlePool : (long)num;
            return new CapResult { Num = n, PPT = num / formula };
        }

        // ---- TimeMachineBP -----------------------------------------------------------------------

        // Both halves of the Time Machine are the identical shape once the reads are resolved: energy
        // funds levelSpeed against baseSpeedDivider, magic funds levelGoldMulti against
        // baseGoldMultiDivider. That the two are arithmetically the same function is precisely why
        // 05 §6.4 asks whether TM is one consumer or two — the code cannot tell you, because on this
        // axis they are indistinguishable.
        public struct TimeMachineCapInputs
        {
            public double BaseDivider;       // baseSpeedDivider() | baseGoldMultiDivider()
            public float Level;              // machine.levelSpeed | machine.levelGoldMulti
            public int Offset;
            public double Power;             // totalEnergyPower() | totalMagicPower()
            public double HackTmSpeed;       // hacksController.totalTMSpeedBonus()
            public double ChallengeTmSpeed;  // allChallenges.timeMachineChallenge.TMSpeedBonus()
            public double CardTmSpeed;       // cardsController.getBonus(cardBonus.TMSpeed)
            public bool Sadistic;
            public double SadisticDivider;
            public long MaxAllocation;
            public long IdlePool;
        }

        public static CapResult TimeMachineCap(in TimeMachineCapInputs a)
        {
            var formula = 50000.0 * a.BaseDivider * (1f + a.Level + a.Offset);
            formula /= a.Power;
            formula /= a.HackTmSpeed;
            formula /= a.ChallengeTmSpeed;
            formula /= a.CardTmSpeed;

            if (a.Sadistic)
                formula *= a.SadisticDivider;
            formula = Math.Ceiling(formula);
            if (formula < 1.0)
                formula = 1.0;

            return SnapAndClamp(formula, a.MaxAllocation, a.IdlePool, StairEpsilon);
        }

        // ---- WandoosBP ---------------------------------------------------------------------------

        // CHARACTERISED — Wandoos is the ONE lane that does not use StairEpsilon. It uses `1.000002f`,
        // a FLOAT literal, and report 02 §12.4 established that this is not a typo to be converged: it
        // is game-verbatim ([DECOMP] Wandoos98Controller.cs:577). Converging it onto the other seven
        // copies would replace the only variant with provenance. Kept as its own named constant so the
        // difference reads as deliberate.
        public const float WandoosEpsilon = 1.000002f;

        public static long WandoosCap(double baseTime, double speed, long maxAllocation, long idlePool)
        {
            var num = Math.Ceiling(baseTime / speed);
            if (num < 1.0)
                num = 1.0;
            var num1 = Math.Ceiling(num / Math.Ceiling(num / maxAllocation) * WandoosEpsilon);
            return num1 > idlePool ? idlePool : (long)num1;
        }

        // WandoosBP.RecordShare: the fraction of the resource CAP this profile actually hands Wandoos,
        // recorded as a RATIO rather than an absolute so it stays valid as the cap grows between
        // breakpoint swaps (a swap may be hours apart). WandoosAdvisor's OS comparator needs it —
        // projecting at the full cap overstates any OS that does not saturate, by exactly 1/share.
        // Returns -1 for "do not record", which the caller reads as "leave the previous observation".
        public static double ShareOfCap(long maxAllocation, double cap)
        {
            if (cap <= 0 || maxAllocation <= 0) return -1;
            double share = maxAllocation / cap;
            if (double.IsNaN(share) || share <= 0) return -1;
            return share > 1.0 ? 1.0 : share;
        }

        // ---- BasicTrainingBP ---------------------------------------------------------------------

        // BasicTrainingBP.Unlocked. Slots 0-5 are attack, 6-11 defense; within each group the first
        // slot is always open and each later one needs 5000 x its position in the PREVIOUS slot.
        //
        // CHARACTERISED: the caller must pass the correct array — `Index <= 5 ? attackTraining :
        // defenseTraining` — and the index into it is `Index % 6 - 1`. For Index 0 and 6 that would be
        // -1, which is why the `Index % 6 == 0` early-true exists and must stay ahead of the read.
        public static bool BasicTrainingUnlocked(int index, Func<int, long> priorSlotLevel)
        {
            // `index < 0`: -6 % 6 == 0 in C#, so a -6 sentinel hit the early-true below and
            // reported unlocked; -1 fell through to priorSlotLevel(-1 % 6 - 1) = priorSlotLevel(-2).
            // The existing comment already flags -1 as the hazard here — it guards the wrong one.
            if (index < 0 || index > 11) return false;
            if (index % 6 == 0) return true;
            return priorSlotLevel(index % 6 - 1) >= 5000 * (index % 6);
        }

        // The training slot a flat BT index maps to, and how much it may take: min(the game's own cap
        // for that slot, the lane budget).
        public static int BasicTrainingSlot(int index) => index % 6;

        // ⚠ THE SLOT CAP IS `training.attackCaps[i]` / `defenseCaps[i]` — ONE ARRAY FOR THE WHOLE GAME,
        // ON EVERY DIFFICULTY. DO NOT "FIX" THIS TO READ `evilAttackCaps` ON AN EVIL RUN.
        //
        // `Training` declares FOUR cap arrays ([DECOMP] Training.cs:6-12):
        //     _attackCaps / _defenseCaps        { 2500, 15000, 30000, 50000, 70000, 100000 }
        //     evilAttackCaps / evilDefenseCaps  { 250000, 1500000, 3000000, 5000000, 7000000, 10000000 }
        //
        // ⚠ THE EVIL PAIR IS DEAD. Swept over the whole decompile: outside their own declaration, the
        // only occurrence of either name is a save-migration initialiser ([DECOMP] ImportExport.cs:
        // 562-563). NOTHING READS THEM — not the game, not this advisor. `Training.attackCaps` is a
        // plain property returning `_attackCaps` with no difficulty branch ([DECOMP] Training.cs:40-46),
        // and `OffenseTraining.updateOffenseTraining` reads `attackCaps[id]` directly, also with no
        // difficulty branch ([DECOMP] OffenseTraining.cs:99-101).
        //
        // They are a LANDMINE precisely because they look authoritative: a live save carries them at
        // their full initial values forever, so a reader who finds `evilAttackCaps = {250000, ...}`
        // next to a Normal array of `{1,1,1,1,1,1}` will conclude the advisor is reading the wrong one
        // on an Evil run. It is not. Wiring them up would allocate against a cap the game never
        // consults and starve the slot.
        //
        // WHY THE NORMAL ARRAY REACHES 1 AND STAYS THERE ([OPERATOR] 2026-08-07, confirmed against the
        // decompile): the cap is a REQUIRED-ENERGY cost that RATCHETS DOWN. `Rebirth.resetTraining()`
        // shrinks every slot on EVERY rebirth and clamps at one —
        //     `if (attackCaps[i] - num <= 1) { attackCaps[i] = 1; }`
        // — so a long-lived account reaches `{1,1,1,1,1,1}` during Normal and 1 is ABSORBING: it stays
        // 1 for the rest of the game, Evil and Sadistic included. On such an account `took=1` against a
        // hundred-billion offer is CORRECT AND PERMANENT, not a stall and not a bug. Making the take
        // track the offer would burn ~100B per lane per tick for zero levels, because at or above the
        // cap the bar already completes every tick (50 levels/s, the slot's maximum) and every further
        // unit buys nothing — see the ceiling note below.
        //
        // CONSEQUENCE FOR THE ALLOCATOR: on a matured save the basic-training lanes are a PERMANENT
        // non-destination for surplus energy, not a transient one. They will decline every offer for
        // the rest of the run. Seating them anyway is near-free — measured at 1 unit of pool cost
        // across all ten, because the fill spends a divisor slot per lane offered but only deducts the
        // real take.
        public static long BasicTrainingAllocation(long slotCap, long maxAllocation) =>
            Math.Min(slotCap, maxAllocation);

        // BasicTrainingBP.TargetMet — the P1 wiring (decision record amendment 16 §7; audit 20 §2.2).
        //
        // THE GAME'S OWN CEILING, and the only absolute throughput ceiling in the energy pool that
        // more energy cannot raise ([DECOMP] OffenseTraining.cs:99-101):
        //
        //     if (attackEnergy[id] >= attackCaps[id]) { attackBarProgress[id] = 1f; levelUp(); return; }
        //
        // At or above the cap the bar completes every tick — 50 levels/second, the fastest the slot can
        // ever go — and EVERY UNIT ABOVE THE CAP BUYS NOTHING.
        //
        // THE DEFENCE SIDE REACHES THE SAME CEILING BY A DIFFERENT ROUTE, and a reader comparing the
        // two files will notice the asymmetry. DefenseTraining.updateDefenseTraining has no such fast
        // path (:97-103): it always computes `num = defenseEnergy/defenseCaps` and adds it to the bar,
        // so `num` may exceed 1. It makes no difference, because levelUp() sets barProgress back to
        // **0f**, not `-= 1f`, on both sides (DefenseTraining.cs:127, OffenseTraining.cs:140) — the
        // overflow is discarded and the slot still levels at most once per tick. Same ceiling, so one
        // predicate serves both halves of the lane.
        //
        // It is not merely wasted: addEnergy() is
        // `attackEnergy[id] += num` (OffenseTraining.cs:175-196), so a saturated slot that stays in the
        // priority list absorbs its share again on every single pass, permanently, until a rebirth or a
        // gear change clears it. That is the silent loss this predicate closes.
        //
        // NOT COMBINED with the game's other BT number. `attackTraining[id] >= (id+1)*5000`
        // (OffenseTraining.cs:95-98) is the auto-advance THRESHOLD, and the game itself ANDs it with
        // this same ceiling before shedding the overflow to slot id+1. It is a completion notion, not a
        // funding one: below the cap and below the threshold, energy still buys levels at the full rate,
        // so ORing it in would retire a slot that is still converting energy, and ANDing it in would
        // leave a saturated slot in the list on every account without the auto-advance purchase. The
        // ceiling alone is the correct terminator; the threshold is what the GAME does with the
        // surplus once the ceiling is reached, which is its business and not the allocator's.
        //
        // NO ZERO-CAP GUARD. Rebirth.resetTraining() clamps every cap to 1 rather than letting it
        // reach 0 ([DECOMP] Rebirth.cs:652-655, :670-673), so `slotCap <= 0` is a state the game
        // cannot produce — same call `11` made on the hack divide-by-zero and `19` on minimumWishTime.
        public static bool BasicTrainingSaturated(long slotEnergy, long slotCap) => slotEnergy >= slotCap;

        // THE RECLAIM RACE, and the energy that has to be put back (operator-reported 2026-08-07: "the
        // auto-profile is removing BT energy sometimes"; the flip-flop half of audit/31).
        //
        // Two facts that are individually correct and wrong together:
        //
        //   1. MEMBERSHIP IS PER-SLOT AND PRE-RECLAIM. ConstraintLayerBridge builds its seating list
        //      from IsValid(), which is `... && !TargetMet()`, and BasicTrainingSaturated above is that
        //      predicate. A slot sitting AT its cap therefore drops out — deliberately, because
        //      ConstraintLayer's Pass 2 will not let a saturated lane hold a fill slot.
        //   2. THE RECLAIM IS PER-CONTROLLER AND TOTAL. `Reclaim` calls allOffenseController /
        //      allDefenseController.removeAllEnergy(), and [DECOMP] AllOffenseTraining.cs:53-61 is
        //      `for (i = 0; i < attackEnergy.Length; i++) { attackEnergy[i] -= num; idleEnergy += num; }`
        //      — every slot, membership be damned.
        //
        // So a full slot is excluded from the seating list for being full, then emptied anyway by a
        // reclaim that cannot see the list, and the fill only refills what it seated. Its energy is
        // gone. Next tick it is no longer full, so it seats and refills, and whichever slot filled up
        // in the meantime takes its place — the {0} <-> {1..5} alternation audit/31 recorded, with
        // all-six-full unreachable.
        //
        // WHY IT WILL NOT REPRODUCE ON DEMAND: it needs a MIXED pass. With no slot saturated everything
        // seats and refills; with EVERY slot saturated no BasicTrainingBP survives IsValid, the bridge's
        // `hasBasicTraining` flag is false and the reclaim never touches BT at all. Only "some full,
        // some not" loses anything.
        //
        // THE SAME HOLE, WIDER: a slot no lane in the profile covers at all (a `BT-0`-only profile, say)
        // is also stripped by the controller-wide reclaim and also never refilled. Restoring by SEAT
        // rather than by saturation closes both with one rule.
        //
        // Returns per-slot amounts to add back, indexed as BasicTrainingBP is: 0-5 attack, 6-11 defense.
        // A seated slot restores NOTHING — the fill is about to fund it, and re-adding first would
        // double-count against idle. This can only ever return energy that was on the slot moments
        // earlier, so it cannot invent allocation.
        public static long[] BasicTrainingReclaimRestore(long[] energyBefore, bool[] seated)
        {
            var restore = new long[12];
            if (energyBefore == null || seated == null) return restore;
            for (int i = 0; i < 12; i++)
            {
                if (i >= energyBefore.Length || i >= seated.Length) break;
                if (!seated[i] && energyBefore[i] > 0) restore[i] = energyBefore[i];
            }
            return restore;
        }

        // ---- AdvancedTrainingBP ------------------------------------------------------------------

        // `<`, not `<=`: length is a COUNT (decomp AllAdvancedTraining.length == 5) and the slots are
        // 0-based, so `<=` let AT-5 report VALID. That is worse than the equivalent RitualBP off-by-one,
        // because there is no slot 5 to no-op against: ControllerFor(5) returns null and levelTarget[5]
        // is out of bounds, so TargetMetAt/Allocate throw, CustomAllocation.RunStep swallows it, and the
        // ENTIRE energy lane is dead until the next profile load — with one log line per ten minutes.
        // No shipped profile uses AT-5, but the profile editor accepts free text.
        public static bool AdvancedTrainingIndexUnlocked(int index, int length) => index >= 0 && index < length;

        // AdvancedTrainingBP.FormulaFor. The AT rate is a SQUARE-ROOT integral in the game
        // (L(t) = sqrt((L0+1)^2 + 2Rt) - 1), which is why the power term is rooted here and nowhere else.
        public static double AdvancedTrainingFormula(double divisor, double sqrtEnergyPower, double atSpeedBonus)
        {
            if (divisor == 0.0)
                return 0;

            double formula = Math.Ceiling(50.0 * divisor / (sqrtEnergyPower * atSpeedBonus));
            return formula < 1.0 ? 1.0 : formula;
        }

        // The per-slot divisor: baseTime x (level + offset + 1). FLOAT, as the game's own is.
        public static float AdvancedTrainingDivisor(float baseTime, long level, int offset) =>
            baseTime * (level + offset + 1f);

        public static CapResult AdvancedTrainingCap(double formula, long maxAllocation, long idlePool)
        {
            if (formula == 0.0)
                return new CapResult { Num = 0, PPT = 1 };   // the lane's `new CapCalc(1, 0)` default

            return SnapAndClamp(formula, maxAllocation, idlePool, StairEpsilon);
        }

        // A group member's cap need: funded 500 levels ahead when the pool plausibly covers it, at the
        // current level otherwise. `formulaAt(offset)` is AdvancedTrainingFormula for that slot.
        public static long AdvancedTrainingNeed(Func<int, double> formulaAt, long idleEnergy)
        {
            double f = formulaAt(500);
            if (f <= 0) return 0;
            if (f > idleEnergy) f = formulaAt(0);
            if (f > long.MaxValue) return long.MaxValue;
            return (long)f;
        }

        // ⚠ NO PRODUCTION CALLER SINCE THE TOKEN-PROGRAM STRIP. AdvancedTrainingBP's GroupSpread
        // waterfill was deleted (amendment 11 §4.4 REPLACE; see that class's header for why the
        // constraint layer's own per-destination share subsumes it). This function is kept ONLY as the
        // pure-arithmetic record of what the group waterfill did, with its characterisation tests —
        // including the `needs.Count <= 1` hole audit 30 §S6 named. Do not re-wire it; deleting it is a
        // separate, separately-scoped decision.
        //
        // Even spread across the ALLAT/CAPALLAT group: waterfill the pool over the members that still
        // want energy (this slot and the ones allocating after it — earlier slots already took their
        // share out of idleEnergy, so front-to-back shares stay consistent). A slot never gets more
        // than its need; slack from cheap slots flows to expensive ones.
        //
        // `laterNeeds` is the need of every LATER group member that is unlocked and not target-met.
        public static long GroupShare(long myNeed, IEnumerable<long> laterNeeds, long available)
        {
            var needs = new List<long> { myNeed };
            if (laterNeeds != null)
                foreach (var n in laterNeeds)
                    if (n > 0) needs.Add(n);

            if (needs.Count <= 1) return myNeed;

            long avail = available;
            needs.Sort();
            int remaining = needs.Count;
            foreach (var n in needs)
            {
                long share = avail / remaining;
                if (n > share) return Math.Min(myNeed, share);   // waterlevel reached
                avail -= n;
                remaining--;
            }
            return myNeed;   // pool covers every member's full need
        }
    }
}
