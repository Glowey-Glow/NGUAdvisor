using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // PASS 1 of the constraint layer: per-lane FEASIBILITY (audit/decisions/constraint-layer-spec.md
    // §4). Binary, re-evaluated every tick, Unity-free — plain-old-data in, a Verdict out.
    //
    // WIRED, AND LIVE. Every composite below is called per tick by ConstraintLayerBridge.BuildSpecs
    // (:230-291), whose PerformSwap is the energy/magic allocation path whenever the new path is
    // enabled — EnergyBreakpoints.cs:22-23 / MagicBreakpoints.cs:19-20, gated on the INVERTED kill
    // switch `_constraintAllocatorDisabled`, so absent means ON. Validated in game 2026-08-04 on both
    // tracks (amendment 28; audit 31d8315 §7).
    //
    // ⚠ This comment used to read "NOT WIRED. Nothing in the live allocation path calls this yet."
    // That was true when the file was written and stopped being true when the bridge landed; it was
    // still here on 2026-08-04 and audit 37 §S5 A5 had to list it as housekeeping. What remains
    // unwired is PASS 3 (TargetPass) — the bridge still hardcodes `WantsMore = true`
    // (ConstraintLayerBridge.cs:214) because the target table it needs has no producer. Pass 1 is not
    // that; Pass 1 decides the seats the live allocator actually uses.
    //
    // NO CACHING, BY CONSTRUCTION (§4.5). Every function here is pure: state arrives as an input
    // struct per call and nothing is held between calls. Gold stalls clear when gold arrives, boss
    // gates clear on a kill, troll flags clear at rebirth — and this codebase has already shipped both
    // failure modes of remembering: ResourceBreakpoint froze hack ordering at profile-parse time, and
    // ChallengeOverlay cached its parsed list for a whole session. A lane must not stay dead after its
    // blocker lifts.
    //
    // THE SEAT RULE (§4.1) — the single most important line in the spec: an infeasible lane must not
    // occupy a slot in the priority list. The defect class has appeared FOUR times in this tree:
    //   - RIT-7 (advisors/02): infeasible, inflated the equal-share divisor for every magic lane,
    //     allocated nothing;
    //   - NGU-x parsed to index -1 (14f290e): same;
    //   - AUG-x at index -1, SILENT: -1 % 2 == -1 took the upgrade branch and returned true;
    //   - BasicTrainingBP at cap (7791969): the saturated slot re-absorbed its whole share on every
    //     pass, 6 or 12 at once under ALLBT.
    // The enforcement is in the types, not in a convention: a Verdict is only constructible through
    // Seat() or Refuse(reason), a default Verdict is a refusal, and SeatRoster files the two cases
    // into different lists — the priority divisor comes off SeatCount, which a refused lane has no
    // path into.
    public static class FeasibilityPass
    {
        // The per-lane, per-tick answer. Private constructor; Seat() and Refuse(reason) are the only
        // doors, so there is no way to hold a "seated" verdict that carries a refusal reason, or a
        // refusal without one. default(Verdict) is fail-CLOSED — an unevaluated lane holds no seat —
        // for the same reason ZoneGate treats an unknown zone as not one-shottable.
        public struct Verdict
        {
            private readonly bool _seated;
            private readonly string _reason;

            private Verdict(bool seated, string reason)
            {
                _seated = seated;
                _reason = reason;
            }

            public bool Seated => _seated;

            // Non-null exactly when the lane is refused, ready to surface verbatim — every lane
            // receiving zero must carry a reason (spec §10, auto-profile spec §8).
            public string Reason => _seated ? null : (_reason ?? "unevaluated");

            public static Verdict Seat() => new Verdict(true, null);

            public static Verdict Refuse(string reason)
            {
                if (string.IsNullOrEmpty(reason))
                    reason = "infeasible (no reason given)";
                return new Verdict(false, reason);
            }
        }

        // Constraints that arrive from OUTSIDE game state — null means "no constraint". This is the
        // slot the spec requires the input shape to have NOW even though nothing fills it in this
        // commit (§4.3): challenge lane locks arrive from the Campaign Advisor, opt-outs from the
        // user (auto-profile spec §7, "the same shape as challenge locks and boss gates").
        //
        // The KIND handling of §4.3 happens upstream: the Campaign Advisor sends a ChallengeLock for
        // allocation-layer locks (No Rebirth) AND effect-layer locks (No Equipment / No NGU / No Time
        // Machine — the game accepts the allocation and returns nothing, so they must be modelled as
        // infeasible anyway). It must NOT send one for No Augs (UI-only; decision D1 deliberately
        // funds through it and surfaces that) or Blind (display-only). This layer does not know the
        // kinds; it refuses on any non-null lock and surfaces the sender's reason verbatim.
        //
        // THREE constraint sources, one shape (amendment 18 §2.2): challenge locks (Campaign
        // Advisor), opt-outs (user), declared focus (user). A focus is the INVERSE of an opt-out —
        // "these lanes are in; everything else waits" — carried per lane as FocusExcluded: non-null
        // exactly when a focus is declared AND this lane is not in it, value = the focus name.
        // ConstraintLayer.WithFocus is the one door that sets it, and it never sets it on the
        // surplus sink — Wandoos remains the sink under any focus (amendment 18 §2.4), which is what
        // keeps a focused-but-saturated state from stranding the pool.
        public struct ExternalConstraints
        {
            public string ChallengeLock;   // Campaign Advisor; null = no lock
            public string OptOut;          // user opt-out; null = not opted out
            public string FocusExcluded;   // declared focus name when this lane is OUTSIDE it; null = no focus or in focus
        }

        // ---- the predicates (spec §4.2) ----------------------------------------------------------

        public static Verdict ExternalGate(in ExternalConstraints c)
        {
            if (c.ChallengeLock != null)
                return Verdict.Refuse("challenge lock: " + c.ChallengeLock);
            if (c.OptOut != null)
                return Verdict.Refuse("opted out: " + c.OptOut);
            if (c.FocusExcluded != null)
                return Verdict.Refuse("not in declared focus: " + c.FocusExcluded);
            return Verdict.Seat();
        }

        // Gold sufficiency — BINARY, zero not reduced. All three gold-charging systems share the same
        // shape: the cost is tested and charged only when the bar is UNSTARTED, and an unpayable
        // unstarted bar makes the whole tick return without moving:
        //   TM speed      [DECOMP] TimeMachineController.cs:334-344   (realGold < num -> return)
        //   TM gold-multi [DECOMP] TimeMachineController.cs:377-387   (same — the MAGIC half stalls
        //                 on gold exactly like the energy half; spec §4.2 lists only TM-speed)
        //   augment       [DECOMP] AugmentController.cs:226-233, upgrade :266-273
        //   ritual        [DECOMP] BloodMagicController.cs:53-59
        // Equality PAYS on all of them (the refusal comparators are strict <, or !(>=) for rituals).
        // A mid-bar lane (progress > 0) is not gold-blocked: the game finishes the bar it already
        // charged for. The arithmetic is RitualMath.RitualGoldGateBlocks — one home, per the
        // LaneTargets rule; that RitualBP still calls its own copy directly is the live path, and the
        // live path is untouched.
        public static Verdict GoldGate(double realGold, double cost, double barProgress)
        {
            if (!RitualMath.RitualGoldGateBlocks(cost, realGold, barProgress))
                return Verdict.Seat();
            return Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "gold stall: bar unstarted and realGold {0:G6} < cost {1:G6}", realGold, cost));
        }

        // Boss gate. One comparator serves the three systems that have one:
        //   augment  [DECOMP] AugmentController.cs:78-81 (unlock), :216/:256 (update gates,
        //            bossID <= required -> return); fields augBossRequired :55, upgradeBossRequired :57
        //   ritual   [DECOMP] BloodMagicController.cs:49 (bossID <= ritual[id].boss -> return)
        //   TM       [DECOMP] TimeMachineController.cs:81 (bossID > 29 wraps the whole tick — a
        //            source LITERAL, not a field; see TimeMachineBossRequired)
        // The pass sense is strict >: at bossID == required the lane is still locked. Rituals and the
        // TM RE-LOCK every run (09 §6; amendment 23 §3) with no code of their own: the gate reads
        // live bossID, and Rebirth sets bossID = 0 ([DECOMP] Rebirth.cs:691) — which is exactly why
        // this is re-evaluated per tick and never latched.
        public static Verdict BossGate(int bossId, int bossRequired)
        {
            if (bossId > bossRequired)
                return Verdict.Seat();
            return Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "boss gate: bossID {0} has not passed required {1}", bossId, bossRequired));
        }

        // Wish difficulty gate: every add* refuses below the wish's difficulty
        // ([DECOMP] WishesController.cs:895, :959, :1006 — difficultyRequirement > rebirthDifficulty
        // -> refuse). difficultyRequirement itself is Unity-serialized (16 §0) — the CALLER reads it
        // live; on a Normal rebirth 0 of 231 wishes are fundable, so this gate is expected to refuse
        // the whole system there.
        public static Verdict WishDifficultyGate(int difficultyRequirement, int rebirthDifficulty)
        {
            if (difficultyRequirement <= rebirthDifficulty)
                return Verdict.Seat();
            return Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "difficulty gate: wish requires difficulty {0}, rebirth is {1}",
                difficultyRequirement, rebirthDifficulty));
        }

        // ⚠ THE MAXED-WISH GUARD — a guard the game does NOT have (spec §4.4; 15 §A1).
        // addEnergy/addMagic/addRes3 test invalidID, wishLocked and difficultyRequirement, and never
        // maxWishLevel ([DECOMP] WishesController.cs:884-919, :948, :995), while the tick skips a
        // maxed wish outright (updateAllWishes :274 `continue`). The auto-reclaim fires only on the
        // level-up TRANSITION into max (:284-287, removeAllResources) — resources placed on an
        // ALREADY-maxed wish are stranded permanently AND hold one of the at-most-four wish slots
        // (numAllocatedWishes() >= curWishSlots() refuses new wishes, :900). Wish-side twin of the
        // slot-15 hack black hole, and worse, because slots are scarce. maxWishLevel(invalid) is 0L
        // (:690-696), so an invalid id refuses here too.
        public static Verdict WishMaxLevelGate(long level, long maxWishLevel)
        {
            if (level < maxWishLevel)
                return Verdict.Seat();
            return Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "wish maxed: level {0} >= maxWishLevel {1} — resources placed here strand permanently and burn a wish slot",
                level, maxWishLevel));
        }

        // The wish AND-gate: progress is a PRODUCT of three Pow(power x amount, 0.17) factors
        // ([DECOMP] WishesController.cs:699-706; factors :801-814, :816-829, :831-844) — zero of one
        // voids the other two, because Pow(0, 0.17) = 0. What "funded" means is the caller's choice:
        // current placement (wishes[id].energy/magic/res3) when auditing live state, or the planned
        // allocation when the layer is deciding — the layer must fund all three or none (15 §B4).
        public static Verdict WishAndGate(long energy, long magic, long res3)
        {
            if (energy > 0 && magic > 0 && res3 > 0)
                return Verdict.Seat();
            return Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "wish AND-gate: energy/magic/res3 = {0}/{1}/{2} — a zero in any one voids the other two (Pow(0, 0.17) = 0)",
                energy, magic, res3));
        }

        // AT prerequisite: BT slot 4 at 25000 on BOTH sides, tested live by the game at
        // [DECOMP] AllAdvancedTraining.cs:40-47 (unlock) and re-tested at the top of every AT tick
        // (AdvancedTrainingController.cs:151). BT levels reset every rebirth, so this is RE-EARNED
        // every run (09 §B2) — another gate that must never be latched.
        public static Verdict AtPrerequisiteGate(long attackTrainingSlot4, long defenseTrainingSlot4)
        {
            if (attackTrainingSlot4 >= 25000 && defenseTrainingSlot4 >= 25000)
                return Verdict.Seat();
            return Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "AT prerequisite: BT slot 4 attack {0} / defense {1}, both must reach 25000 (re-earned every run)",
                attackTrainingSlot4, defenseTrainingSlot4));
        }

        // Wish 190 makes AT free — and therefore makes AT ALLOCATION permanently infeasible (07 §3).
        // progressPerTick returns 1f regardless of energy once wishes[190].level >= 1
        // ([DECOMP] AdvancedTrainingController.cs:128-131), and the tick itself runs with zero energy
        // (:151 accepts energy <= 0 when wish 190 is owned). Every unit of energy in AT from then on
        // is strictly wasted. Wishes are permanent, so unlike every other predicate here this one
        // never clears — but it is still re-evaluated per tick like the rest, because holding state
        // is how lanes go dead (§4.5).
        public static Verdict AtWish190Gate(long wish190Level)
        {
            if (wish190Level < 1)
                return Verdict.Seat();
            return Verdict.Refuse(
                "wish 190 owned: AT runs at the 1f fast path free of charge — any energy allocated to it is strictly wasted");
        }

        // Trolled off. Four public serialized flags, POLLED — no event fires when the Troll sets one
        // (21 §B2): NGU.disabled ([DECOMP] NUMBERSSGOUP.cs:13), beards.disabled (Beards.cs:17),
        // wandoos98.disabled (Wandoos98.cs:33), inventory.disabled (Inventory.cs:68, unreachable in
        // play). All four gate only the BONUS getters — the level ticks run unmodified — and all four
        // reset at rebirth (resetTrolls, Character.cs:928-932).
        //
        // The refusal is per spec §4.3's effect-layer rule: allocation succeeds and output is zeroed,
        // which is silent waste. One recorded nuance the caller may surface alongside: NGU levels
        // survive rebirth, so energy in a trolled-off NGU is DEFERRED value rather than pure waste —
        // the only lane in 21's table where that is true (§D4 row 4). The spec still says refuse, so
        // this refuses.
        public static Verdict TrollGate(bool disabled, string flagPath)
        {
            if (!disabled)
                return Verdict.Seat();
            return Verdict.Refuse("trolled off: " + (flagPath ?? "disabled flag") +
                " is set (polled; clears at rebirth)");
        }

        // ---- per-lane composites -----------------------------------------------------------------
        // One Evaluate per lane family, external constraints first, then the game predicates in the
        // order the game itself tests them. First refusal wins and is the surfaced reason.

        public struct AugmentHalfState
        {
            public int BossId;             // character.bossID
            public int BossRequired;       // augBossRequired | upgradeBossRequired (AugmentController.cs:55/:57)
            public double RealGold;        // character.realGold
            public double Cost;            // getAugCost() | getUpgradeCost()
            public double BarProgress;     // augs[id].augProgress | .upgradeProgress
            public ExternalConstraints External;
        }

        public static Verdict AugmentHalf(in AugmentHalfState s)
        {
            var v = ExternalGate(s.External);
            if (!v.Seated) return v;
            v = BossGate(s.BossId, s.BossRequired);
            if (!v.Seated) return v;
            return GoldGate(s.RealGold, s.Cost, s.BarProgress);
        }

        // The Time Machine boss lock is a LITERAL, not a serialized field — unlike augments there is
        // no bossRequired on the controller; the entire TM tick runs inside
        // `if (character.bossID > 29)` ([DECOMP] TimeMachineController.cs:81). Read exactly as
        // written: 29, under the strict > BossGate already applies — seating begins at bossID 30,
        // which is what "unlocks on Boss 30" (amendment 23 §3) compiles to. The gate is SHARED by
        // both halves: the one :81 check wraps advanceSpeedProgress (:84-87) and
        // advanceGoldMultiProgress (:88-91), and neither per-half method carries a boss test of its
        // own (:320-361, :363-404). It re-locks every rebirth exactly like rituals — Rebirth sets
        // bossID = 0 ([DECOMP] Rebirth.cs:691) — so under 100LC's 3-10 minute cadence both halves
        // cross this gate every cycle (amendment 23 §5).
        public const int TimeMachineBossRequired = 29;   // [DECOMP] TimeMachineController.cs:81

        // Both Time Machine halves are this shape — the magic half's gold stall
        // (TimeMachineController.cs:377-387) is byte-identical in structure to the energy half's,
        // and the boss lock is one shared gate over both (TimeMachineController.cs:81).
        public struct TimeMachineHalfState
        {
            public int BossId;             // character.bossID — re-locks every run (Rebirth.cs:691)
            public int BossRequired;       // TimeMachineBossRequired — the :81 literal, shared by both halves
            public double RealGold;        // character.realGold
            public double Cost;            // machineSpeedGoldCost() | machineGoldMultiCost()
            public double BarProgress;     // machine.speedProgress | machine.goldMultiProgress
            public ExternalConstraints External;
        }

        public static Verdict TimeMachineHalf(in TimeMachineHalfState s)
        {
            var v = ExternalGate(s.External);
            if (!v.Seated) return v;
            v = BossGate(s.BossId, s.BossRequired);
            if (!v.Seated) return v;
            return GoldGate(s.RealGold, s.Cost, s.BarProgress);
        }

        public struct RitualState
        {
            public int BossId;             // character.bossID — re-locks every run (Rebirth.cs:691)
            public int BossRequired;       // bloodMagic.ritual[id].boss (BloodMagicController.cs:49)
            public double RealGold;        // character.realGold
            public double Cost;            // currentCost()
            public double BarProgress;     // bloodMagic.ritual[id].progress
            public ExternalConstraints External;
        }

        public static Verdict Ritual(in RitualState s)
        {
            var v = ExternalGate(s.External);
            if (!v.Seated) return v;
            v = BossGate(s.BossId, s.BossRequired);
            if (!v.Seated) return v;
            return GoldGate(s.RealGold, s.Cost, s.BarProgress);
        }

        public struct AdvancedTrainingState
        {
            public long AttackTrainingSlot4;    // training.attackTraining[4]
            public long DefenseTrainingSlot4;   // training.defenseTraining[4]
            public long Wish190Level;           // wishes.wishes[190].level
            public ExternalConstraints External;
        }

        public static Verdict AdvancedTraining(in AdvancedTrainingState s)
        {
            var v = ExternalGate(s.External);
            if (!v.Seated) return v;
            // Wish 190 first: it outranks the prerequisite because it is permanent — an account that
            // owns it must see THAT reason, not a transient BT one that clears mid-run.
            v = AtWish190Gate(s.Wish190Level);
            if (!v.Seated) return v;
            return AtPrerequisiteGate(s.AttackTrainingSlot4, s.DefenseTrainingSlot4);
        }

        public struct WishState
        {
            public int DifficultyRequirement;   // properties[id].difficultyRequirement (serialized — read live)
            public int RebirthDifficulty;       // settings.rebirthDifficulty
            public long Level;                  // wishes.wishes[id].level
            public long MaxWishLevel;           // wishesController.maxWishLevel(id)
            public long Energy;                 // funded amounts — current placement or planned; see WishAndGate
            public long Magic;
            public long Res3;
            public ExternalConstraints External;
        }

        public static Verdict Wish(in WishState s)
        {
            var v = ExternalGate(s.External);
            if (!v.Seated) return v;
            v = WishDifficultyGate(s.DifficultyRequirement, s.RebirthDifficulty);
            if (!v.Seated) return v;
            // Maxed before funding: a maxed wish is refused no matter what is or would be placed on
            // it — that is the black-hole guard, and it must not be reachable-around via funding.
            v = WishMaxLevelGate(s.Level, s.MaxWishLevel);
            if (!v.Seated) return v;
            return WishAndGate(s.Energy, s.Magic, s.Res3);
        }

        public static Verdict NguLane(bool nguDisabled, in ExternalConstraints external)
        {
            var v = ExternalGate(external);
            if (!v.Seated) return v;
            return TrollGate(nguDisabled, "NGU.disabled");
        }

        public static Verdict WandoosLane(bool wandoosDisabled, in ExternalConstraints external)
        {
            var v = ExternalGate(external);
            if (!v.Seated) return v;
            return TrollGate(wandoosDisabled, "wandoos98.disabled");
        }

        // Lanes with no game-side Pass 1 predicate at all — Basic Training, BR, hacks. They still
        // pass through so external locks and opt-outs have one uniform entry, and so the roster sees
        // every lane exactly once.
        public static Verdict PlainLane(in ExternalConstraints external) => ExternalGate(external);
    }

    // The tick's seating record — built fresh each tick (holding one across ticks is the §4.5 caching
    // defect), filled by FeasibilityPass verdicts, and the ONLY source of the priority count.
    //
    // The seat rule lives here as structure: Add() routes on the verdict, so a refused lane lands in
    // Refusals — with its reason, because every zero must be surfaceable — and has no path into
    // Seated. SeatCount and EqualShare read the seated list alone. There is deliberately no combined
    // count and no combined list: the RIT-7 divisor inflation is not a bug this type can express.
    public sealed class SeatRoster
    {
        public struct Refusal
        {
            public string Lane;
            public string Reason;
        }

        private readonly List<string> _seated = new List<string>();
        private readonly List<Refusal> _refusals = new List<Refusal>();

        public void Add(string lane, FeasibilityPass.Verdict verdict)
        {
            if (verdict.Seated)
                _seated.Add(lane);
            else
                _refusals.Add(new Refusal { Lane = lane, Reason = verdict.Reason });
        }

        public int SeatCount => _seated.Count;

        public IReadOnlyList<string> Seated => _seated;

        // Every lane receiving zero, and why — the surfacing feed (spec §10).
        public IReadOnlyList<Refusal> Refusals => _refusals;

        // The equal-share divisor the seat rule protects. Refused lanes cannot inflate it because
        // they are not in the count; zero seats yields zero rather than a divide, matching the
        // "there is nothing to decide" posture rather than throwing on an all-refused tick.
        public long EqualShare(long pool)
        {
            if (_seated.Count == 0 || pool <= 0)
                return 0;
            return pool / _seated.Count;
        }
    }
}
