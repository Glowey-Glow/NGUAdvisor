using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // The ResourceBreakpoint base's Unity-free surface, and the TargetMet() predicate for every lane
    // subclass (audit 01 §3.4, extraction step 3).
    //
    // WHY THIS FILE EXISTS AS A TABLE AND NOT JUST AS FUNCTIONS. Decision record amendment 01 §P1 makes
    // "can this lane report DONE?" a hard prerequisite for both pools: target-then-surplus allocation is
    // built on that signal, and a lane that hardcodes `false` is structurally incapable of producing it.
    // The audit's inventory of which lanes those are (05 §6.3) is one short. Rather than fix the prose
    // and let it drift again, the inventory lives here as data, and LaneTargetsTests asserts every row
    // against the predicate it describes.
    //
    // PARITY, EXCEPT WHERE THE GAME SUPPLIES THE ANSWER. The extraction was parity-only and left all
    // five `=> false` lanes alone. Decision record amendment 16 §7 then made P1 THE prerequisite and
    // established that for some of them the game already supplies the terminator the advisor was
    // discarding. Those are now wired, one lane at a time, each to the game predicate named in its own
    // comment. A lane still reading `NeverDone()` is one where an exhaustive search of the decomp found
    // no terminator to wire — the `false` is faithful, and the comment on the lane says so.
    public static class LaneTargets
    {
        public enum TargetKind
        {
            // `=> false`. The lane can NEVER report done. This is the P1 blocker.
            HardcodedFalse,
            // Reads a target FIELD off game state and compares it to a level. A user- or profile-typed
            // number, not a computed worth (05 §1) — but it is at least reachable.
            ThresholdField,
            // Hands the question to the game's own predicate.
            GameDelegated,
            // The game's own SATURATION CEILING: a number the GAME maintains, above which further
            // allocation provably buys nothing. Distinct from ThresholdField because no operator types
            // it and there is nothing to declare — 20 §2.2's point that Basic Training is the one
            // energy system whose targets are constants rather than fields, so "the game ships a
            // complete policy needing no user input at all". Do not go looking for a BT target field.
            GameCeiling
        }

        public struct LaneTargetSpec
        {
            public string Lane;            // class name
            public string Pool;            // the pool THIS ROW is on — see the CONSUMER note below
            public string Tokens;          // the profile tokens that construct it
            public TargetKind Kind;
            public string Reads;           // the target field(s), or "—"
            public bool NeverFundMarker;   // does a NEGATIVE target mean "never fund this"?
            public bool ExplicitNeverFund; // ...spelled out as its own branch, or reached incidentally?
        }

        // Every ResourceBreakpoint subclass.
        //
        // The P1 inventory started at FIVE hardcoded-false lanes: BestAug, BR, BasicTrainingBP,
        // RitualBP and WandoosBP. 05 §6.3 enumerated four of them and omitted BR — BR's `=> false` is
        // recorded in that document's §3 table and flagged as ambiguous in §6.2, but it is missing from
        // the §6.3 list the count came from. Amendment 01 §P1 named all five.
        //
        // Rows move off HardcodedFalse as each lane is wired to the game predicate that answers its
        // DONE question. What is left is the honest residue: lanes for which the decomp has no
        // terminator at all.
        //
        // AS OF THE P1 PASS: BestAug and BasicTrainingBP are wired. THREE remain, and all three were
        // checked by exhaustive search rather than assumed — each lane's TargetMet() carries the
        // citation. WandoosBP has no target in either Wandoos file (20 §2.8). RitualBP and BR have no
        // target in any of the four blood-magic files; amendment 16 §7 named `ritualsUnlocked` for
        // them, but that is a LOCK the advisor already applies in Unlocked(), not a target. The
        // amendment's "only WandoosBP's false is faithful" is therefore one lane short in the other
        // direction: THREE of the five `false`s are faithful, not one.
        //
        // ONE ROW PER CONSUMER, NOT PER CLASS. Decision record amendment 05 §4 decided that TM-speed
        // (energy) and TM-goldmulti (magic) are SEPARATE consumers with separate value models — they
        // use different serialized divider families, different levels, different targets, and they
        // satisfy independently. So TimeMachineBP occupies two rows and the row identity is
        // (Lane, Pool), not Lane. This replaced a `PoolDependent` flag on one merged row: the fact it
        // encoded is now structural, which is the point of splitting rather than annotating.
        //
        // A lane whose Pool reads "Energy|Magic" is the other case — ONE consumer that can be parsed
        // into either pool and reads the SAME target either way. That is not a split.
        public static readonly LaneTargetSpec[] Table =
        {
            new LaneTargetSpec { Lane = "AdvancedTrainingBP", Pool = "Energy", Tokens = "AT-n / CAPAT-n / ALLAT / CAPALLAT",
                Kind = TargetKind.ThresholdField, Reads = "advancedTraining.levelTarget[i] vs .level[i]",
                NeverFundMarker = true, ExplicitNeverFund = true },

            new LaneTargetSpec { Lane = "AugmentBP", Pool = "Energy", Tokens = "AUG-n / CAPAUG-n",
                Kind = TargetKind.ThresholdField, Reads = "augs[i].augmentTarget|upgradeTarget vs .augLevel|.upgradeLevel",
                NeverFundMarker = true, ExplicitNeverFund = false },

            new LaneTargetSpec { Lane = "BasicTrainingBP", Pool = "Energy", Tokens = "BT-n / CAPBT-n / ALLBT / CAPALLBT",
                Kind = TargetKind.GameCeiling, Reads = "training.attackEnergy[s]|defenseEnergy[s] vs .attackCaps[s]|.defenseCaps[s]",
                NeverFundMarker = false, ExplicitNeverFund = false },

            new LaneTargetSpec { Lane = "BestAug", Pool = "Energy", Tokens = "BESTAUG / CAPBESTAUG",
                Kind = TargetKind.GameDelegated, Reads = "augments[0..6].hitAugmentTarget()|hitUpgradeTarget() — done only when NO half is live",
                NeverFundMarker = true, ExplicitNeverFund = false },

            new LaneTargetSpec { Lane = "BR", Pool = "Magic", Tokens = "BR-n / CAPBR-n",
                Kind = TargetKind.HardcodedFalse, Reads = "—",
                NeverFundMarker = false, ExplicitNeverFund = false },

            new LaneTargetSpec { Lane = "HackBP", Pool = "R3", Tokens = "HACK-n / CAPHACK-n / ALLHACK / CAPALLHACK",
                Kind = TargetKind.GameDelegated, Reads = "hacksController.hitTarget(i)",
                NeverFundMarker = true, ExplicitNeverFund = true },

            new LaneTargetSpec { Lane = "NGUBP", Pool = "Energy|Magic", Tokens = "NGU-n / CAPNGU-n / ALLNGU / CAPALLNGU",
                Kind = TargetKind.ThresholdField, Reads = "skills[i].target|evilTarget|sadisticTarget vs matching level",
                NeverFundMarker = true, ExplicitNeverFund = true },

            new LaneTargetSpec { Lane = "RitualBP", Pool = "Magic", Tokens = "RIT-n / CAPRIT-n",
                Kind = TargetKind.HardcodedFalse, Reads = "—",
                NeverFundMarker = false, ExplicitNeverFund = false },

            // TWO ROWS, one per consumer (amendment 05 §4). One class, but the energy half funds
            // levelSpeed toward speedTarget and the magic half funds levelGoldMulti toward multiTarget:
            // different divider families (base*SpeedDivider vs base*GoldMultiDivider), different levels,
            // different targets, satisfied independently. 20 §0.3 counts TM as ONE energy consumer for
            // exactly this reason — its gold-multiplier half is not in that pool.
            new LaneTargetSpec { Lane = "TimeMachineBP", Pool = "Energy", Tokens = "TM / CAPTM",
                Kind = TargetKind.ThresholdField, Reads = "machine.speedTarget vs .levelSpeed",
                NeverFundMarker = true, ExplicitNeverFund = false },

            new LaneTargetSpec { Lane = "TimeMachineBP", Pool = "Magic", Tokens = "TM / CAPTM",
                Kind = TargetKind.ThresholdField, Reads = "machine.multiTarget vs .levelGoldMulti",
                NeverFundMarker = true, ExplicitNeverFund = false },

            new LaneTargetSpec { Lane = "WandoosBP", Pool = "Energy|Magic", Tokens = "WAN / CAPWAN",
                Kind = TargetKind.HardcodedFalse, Reads = "—",
                NeverFundMarker = false, ExplicitNeverFund = false },
        };

        public static LaneTargetSpec[] HardcodedFalseLanes =>
            Table.Where(t => t.Kind == TargetKind.HardcodedFalse).ToArray();

        // ---- the predicates ---------------------------------------------------------------------

        // Named rather than inlined so every remaining call site points at one place, and so
        // `git grep NeverDone` still finds every lane that cannot report done. Each surviving caller
        // carries its own comment saying which exhaustive search established there is nothing to wire.
        public static bool NeverDone() => false;

        // AdvancedTrainingBP.TargetMetAt. Note `target != 0` where NGUBP uses `target > 0` — after the
        // negative early-return the two are equivalent, and both forms are preserved as written.
        public static bool AdvancedTrainingTargetMet(long target, long level)
        {
            if (target < 0L) return true;
            return target != 0L && level >= target;
        }

        // LevelPlanner's Block purpose cap, as a FLOOR rather than a set.
        //
        // ⚠ TWO CONSUMERS NOW, AND THAT IS THE POINT — THIS IS THE ONE PLACE THE RULE LIVES.
        // LevelPlanner.ApplyPurposeFloor writes the game field with it, and TargetPass.Evaluate
        // computes the stop Pass 3 enforces with it. Those are the two numbers that used to be able
        // to disagree: 36ea654 measured a live divergence in which a hand-set 250,000 was kept in
        // the field while Pass 3 defunded the slot at 100,000 anyway. [OPERATOR] ruled on it
        // 2026-08-07 — "the operator's higher target should win over the ruled cap but it should
        // never be capped below the 100,000 level" — which is EXACTLY the function below, already
        // written. Making Pass 3 call it rather than restate it is what closes the divergence.
        //
        // ⚠ SO DO NOT INLINE, COPY OR "SIMPLIFY" IT AT EITHER CALL SITE. It is `Math.Max` over the
        // whole domain that matters and a reader will notice; what Math.Max does NOT carry is the
        // `current > 0` guard, which is the difference between a non-positive stop being ignored and
        // a negative operator target winning. OperatorRuledRowsTests censuses the call sites for
        // exactly this reason.
        //
        // THE DEFECT THIS CLOSES. LevelPlanner.TickPurposeTargets runs every tick while the auto profile
        // is on and wrote `levelTarget[2] = ceil(49 / block.levelFactor)` unconditionally — the level at
        // which blockBonus reaches 99% damage reduction ([DECOMP] AdvancedTrainingController.blockBonus:
        // `50f / (1 + levelFactor * level) / 100f`, so 99% needs levelFactor*level >= 49). The arithmetic
        // is right and the rule was the user's, but there was NO override of any kind: a larger number
        // typed into the game's own AT target box was overwritten within one tick, every tick, and the
        // only escape was turning the auto profile off. Operator-reported 2026-08-07: a hand-set 100,000
        // would not stick.
        //
        // So the 99% level is now the LEAST the advisor will accept, never a ceiling it imposes. A
        // POSITIVE target at or above the stop is the operator's and is returned unchanged.
        //
        // WHY BLOCK AND NOT THE WANDOOS ATs (slots 3/4). Because slot 2 is now the ONLY AT target the
        // advisor writes at all — there is no second caller to give a floor to. The wandoos stops were
        // priced against the ACTIVE loadout's E/M caps, which is why a floor was refused here first
        // (it would strand an inflated reading forever), and on 2026-08-10 that same gear-dependence
        // retired the write itself: LevelPlanner.WandoosStopLevel is deleted and slots 3/4 take no
        // advisor target ever. Block's stop moves only with levelFactor, which is fixed for the run,
        // so a floor is safe here and nowhere else.
        //
        // <= 0 TAKES THE STOP, deliberately. 0 is the game's unset sentinel and a negative target reads
        // as "met" to AdvancedTrainingTargetMet above; neither is a number the operator is asking to
        // keep, and both are what this slot already got before this change. The floor only ever REMOVES
        // writes, so it cannot introduce a target that was not being written already.
        public static long AdvancedTrainingPurposeFloor(long current, long stop) =>
            current > 0L && current >= stop ? current : stop;

        // TimeMachineBP.TargetMet, for whichever pool the lane is on. Like AugmentBP there is no
        // explicit negative branch: a negative target reads as met only because levels are non-negative.
        public static bool TimeMachineTargetMet(long target, long level) => target != 0 && level >= target;

        // The two lanes whose target logic already has a home keep it there — one implementation each,
        // no second copy. These are here so every lane's predicate is reachable from one place.
        public static bool NguTargetMet(long target, long level) => NguValueMath.NguTargetMet(target, level);

        public static bool AugmentTargetMet(int index, long target, long level) =>
            AugmentMath.AugmentTargetMet(index, target, level);

        // BestAug.TargetMet. Was NeverDone(); now the game's own hitAugmentTarget()/hitUpgradeTarget()
        // folded across all 7 pairs (amendment 16 §7). Same home rule as above — the arithmetic stays
        // in AugmentMath, this is the one place every lane's predicate is reachable from.
        public static bool BestAugTargetMet(IList<AugmentMath.AugPairTargetState> pairs, bool useUpgrades) =>
            AugmentMath.BestAugTargetMet(pairs, useUpgrades);

        // BasicTrainingBP.TargetMet. Was NeverDone(); now the game's own energy-vs-cap ceiling, whose
        // home is LaneCapMath alongside the lane's other three helpers.
        public static bool BasicTrainingTargetMet(long slotEnergy, long slotCap) =>
            LaneCapMath.BasicTrainingSaturated(slotEnergy, slotCap);

        // ---- the base class's own surface --------------------------------------------------------

        // ResourceBreakpoint.IsValid(). Three predicates, ANDed, with TargetMet inverted. Trivial, and
        // the whole point of P1: a lane that cannot make the third term true can never leave the list.
        public static bool IsValid(bool correctResourceType, bool unlocked, bool targetMet) =>
            correctResourceType && unlocked && !targetMet;

        // ResourceBreakpoint.Label — mirrors the profile token syntax so a log line can be pasted back
        // into a profile ("CAPNGU-5"). `typeName` is GetType().Name.
        public static string Label(string typeName, bool isCap, int index) =>
            (isCap ? "CAP" : "") + (typeName ?? "").Replace("BP", "") + "-" + index;
    }
}
