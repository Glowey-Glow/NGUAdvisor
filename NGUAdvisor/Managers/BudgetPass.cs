using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // PASS 0 of the constraint layer: BUDGET EXHAUSTION (audit/decisions/constraint-layer-spec.md §3;
    // 21 §A1-A3; amendment 17 §2-4). Unity-free — plain-old-data in, a Verdict out. NOT WIRED: nothing
    // in the live allocation path calls this; it is the tested core alongside FeasibilityPass and
    // CapacityPass.
    //
    // THE FOURTH STALL CLASS. At all nine canLevel() sites the shape is
    //
    //     if (progress >= 1f) {
    //         progress = 0f;                      <- ZEROED FIRST, unconditionally
    //         if (character.canLevel()) { level++; rebirthLevels++; }
    //
    // so at 100/100 a counting lane charges its bar to full, DISCARDS it, and repeats — consuming
    // energy or magic, charging gold, producing nothing, forever. Blood is worse: the bloodPoints
    // gain is INSIDE the gate ([DECOMP] BloodMagicController.cs:66-76, the += at :72), so at the cap
    // ALL blood generation stops. A budget-exhausted lane is feasible, under capacity and
    // target-unmet — Passes 1-3 cannot detect it, which is why this pass runs FIRST.
    //
    // THE LIVE READ, NEVER A SHADOW. The caller fills BudgetState fresh every tick from
    // challenges.levelChallenge10k.inChallenge and settings.rebirthLevels ([DECOMP]
    // PlayerSettings.cs:62, public long, serialized). Nine independent game ticks increment the
    // counter, several the advisor never touches — beards level off a full-bar gate the advisor does
    // not allocate to — so a shadow drifts within seconds and cannot be reconciled (21 §A1).
    //
    // ⚠ NEVER CALL Character.levelsRemaining() ([DECOMP] Character.cs:2187-2203). It is DEAD (no
    // callers anywhere in the decomp) and DEFECTIVE: Math.Max(1L, 100 - rebirthLevels) never returns
    // 0 — at 100/100 it reports 1 level remaining. LevelsRemaining below is the caller-side compute
    // the audit prescribes instead.
    //
    // BANK RESTORES ARE OUTSIDE THE BUDGET — and outside the counter, so the live read needs no
    // adjustment. AT (level[i] += bankedLevel[i], [DECOMP] AllAdvancedTraining.cs:22-33), beards
    // (beardLevel = bankedLevel, AllBeardsController.cs:255-270) and TM (levelSpeed = speedBankLevels,
    // TimeMachineController.cs:104-114) write levels directly with NO canLevel() and NO
    // rebirthLevels++ — restored levels never appear in the counter, so reading it live cannot
    // mistake them for budget consumption. Starting the challenge zeroes all three banks
    // ([DECOMP] Rebirth.cs:213-219 under hardReset), but they refill on the next rebirth inside it —
    // the hole is open from rebirth two onward. Pass 0 does not act on any of this.
    // ⚠ Noted, not fixed: the TM restore trigger is an EXACT EQUALITY — if (character.bossID == 30)
    // ([DECOMP] BossController.cs:176-178) — so any path that sets bossID past 30 without stepping
    // through it leaves the bank unrestored until the next boss-30 crossing.
    public static class BudgetPass
    {
        // The 100 is a SOURCE LITERAL, not a serialized constant — it appears verbatim in canLevel()
        // ([DECOMP] Character.cs:2180), in levelsRemaining() (:2192) and in the UI string
        // (CurrentChallengeInfo.cs:50). No capture needed; no capture possible.
        public const long BudgetCap = 100L;

        // The tick's snapshot — BOTH fields read live from game state at the moment of the call.
        public struct BudgetState
        {
            public bool InLevelChallenge;   // challenges.levelChallenge10k.inChallenge
            public long RebirthLevels;      // settings.rebirthLevels — LIVE READ, never shadowed
        }

        // The gate, exactly as the game tests it ([DECOMP] Character.cs:2178-2185): >=, not ==,
        // because nothing clamps the counter — out of challenge it runs unbounded, and a save-edited
        // or drifted value above 100 must still refuse.
        public static bool Exhausted(in BudgetState s) =>
            s.InLevelChallenge && s.RebirthLevels >= BudgetCap;

        // The caller-side compute that replaces the dead, defective Character.levelsRemaining():
        // 100 - rebirthLevels, floored at 0 — the floor the game's Math.Max(1L, …) makes unreachable.
        // Meaningful only in-challenge; out of one there is no budget to have a remainder of.
        public static long LevelsRemaining(long rebirthLevels) =>
            Math.Max(0L, BudgetCap - rebirthLevels);

        // ---- the allowlist — derived, not declared (spec §3.3) -----------------------------------
        //
        // ⚠ THE EXEMPTION IS ENFORCED BY OMISSION. There is no list in the game: canLevel() is one
        // predicate that nine call sites choose to consult. No enum, no attribute, no registry, no
        // switch — everything else is exempt BECAUSE IT NEVER ASKS, and a future system is exempt by
        // default (hacks, wishes and cards all postdate the challenge and none needed a code change).
        // So this is an ALLOWLIST OF COUNTING SITES, never "these systems are exempt": a blacklist
        // that missed a new system would silently start counting it, and this model mirrors the
        // game's own default — a lane absent from the table is untouched by Pass 0.
        //
        // RE-DERIVATION IS A PROCEDURE, NOT A MEMORY. On every game build:
        //   1. grep -rn "canLevel" reference/decomp-full/ — expect exactly ONE definition
        //      (Character.cs:2178) plus N call sites.
        //   2. Every call site must have a row here; every row must match a call site (File,
        //      GateLine, IncrementLine). BudgetPassTests asserts the current table against the
        //      derivation verbatim, so a game update that moves or adds a site fails the build's
        //      tests instead of drifting silently. build/decomp-diff.ps1 + api-manifest.txt is the
        //      existing drift detector for the decomp itself.
        // Derived against game build 1.260 ([DECOMP] Character.cs:450; decomp hash
        // reference/decomp-full/_source.sha256 byte-matches the live DLL).
        //
        // ⚠ DO NOT TRUST THE IN-GAME TEXT. Both strings live in
        // LevelChallenge10KController.showChallengeInfo() ([DECOMP] :262-266, the string at :264):
        // the Description whitelists FIVE systems and omits Advanced Training, which counts; the
        // Restrictions line in the same tooltip blacklists two. The code counts SIX.
        public enum CountingSite
        {
            AugmentAug,
            AugmentUpgrade,
            BloodMagicRitual,
            TimeMachineSpeed,
            TimeMachineGoldMulti,
            WandoosEnergy,
            WandoosMagic,
            BeardTemp,
            AdvancedTraining,
        }

        public struct CountingSiteSpec
        {
            public CountingSite Site;
            public string System;            // one of six — the count the in-game text gets wrong
            public string File;              // decomp file the site lives in
            public int GateLine;             // the `if (character.canLevel())` line
            public int IncrementLine;        // the `character.settings.rebirthLevels++` line
            public string LevelUpBlock;      // the five-line shape, as a :from-to range
            public string EnclosingFunction; // with its declaration line
            public string[] AdvisorLanes;    // lane names as in CapacityPass.Table / LaneTargets.Table
            public string BurnsAtCap;        // the SECOND resource still charged per bar at 100/100, or null
            public string NonCountingTwin;   // same-system level path that does NOT count, or null
        }

        public static readonly CountingSiteSpec[] Allowlist =
        {
            new CountingSiteSpec { Site = CountingSite.AugmentAug, System = "Augments",
                File = "AugmentController.cs", GateLine = 245, IncrementLine = 248,
                LevelUpBlock = ":242-251", EnclosingFunction = "advanceAug() (:214)",
                AdvisorLanes = new[] { "AugmentBP", "BestAug" },
                BurnsAtCap = "getAugCost() gold (:226-233)" },

            new CountingSiteSpec { Site = CountingSite.AugmentUpgrade, System = "Augments",
                File = "AugmentController.cs", GateLine = 285, IncrementLine = 288,
                LevelUpBlock = ":282-291", EnclosingFunction = "advanceUpgrade() (:254)",
                AdvisorLanes = new[] { "AugmentBP", "BestAug" },
                BurnsAtCap = "getUpgradeCost() gold (:266-273)" },

            // Worst of the nine: the bloodPoints gain sits INSIDE the canLevel() branch (:72), so at
            // 100/100 the ritual still charges gold every cycle and produces ZERO blood — the whole
            // blood economy halts, silently. (The only other bloodPoints += in the game is the
            // offline path, Character.cs:3443, and 100LC disables offline progress outright.)
            new CountingSiteSpec { Site = CountingSite.BloodMagicRitual, System = "Blood Magic",
                File = "BloodMagicController.cs", GateLine = 69, IncrementLine = 73,
                LevelUpBlock = ":66-76", EnclosingFunction = "updateBloodMagic() (:46)",
                AdvisorLanes = new[] { "RitualBP", "BR" },
                BurnsAtCap = "currentCost() gold (:53-61) — and ALL bloodPoints generation stops (:72 is inside the gate)" },

            new CountingSiteSpec { Site = CountingSite.TimeMachineSpeed, System = "Time Machine",
                File = "TimeMachineController.cs", GateLine = 354, IncrementLine = 357,
                LevelUpBlock = ":351-360", EnclosingFunction = "advanceSpeedProgress() (:320)",
                AdvisorLanes = new[] { "TimeMachineBP" },
                BurnsAtCap = "machineSpeedGoldCost() gold (:334-344)" },

            new CountingSiteSpec { Site = CountingSite.TimeMachineGoldMulti, System = "Time Machine",
                File = "TimeMachineController.cs", GateLine = 397, IncrementLine = 400,
                LevelUpBlock = ":394-403", EnclosingFunction = "advanceGoldMultiProgress() (:363)",
                AdvisorLanes = new[] { "TimeMachineBP" },
                BurnsAtCap = "machineGoldMultiCost() gold (:377-387)" },

            new CountingSiteSpec { Site = CountingSite.WandoosEnergy, System = "Wandoos",
                File = "Wandoos98Controller.cs", GateLine = 277, IncrementLine = 280,
                LevelUpBlock = ":274-284", EnclosingFunction = "advanceEnergyProgress() (:261)",
                AdvisorLanes = new[] { "WandoosBP" },
                NonCountingTwin = "OS levels — wandoos98.OSlevel++ from consuming an item " +
                    "([DECOMP] ItemController.cs:370, :377), no canLevel()" },

            new CountingSiteSpec { Site = CountingSite.WandoosMagic, System = "Wandoos",
                File = "Wandoos98Controller.cs", GateLine = 460, IncrementLine = 463,
                LevelUpBlock = ":457-465", EnclosingFunction = "advanceMagicProgress() (:444)",
                AdvisorLanes = new[] { "WandoosBP" },
                NonCountingTwin = "OS levels — see the energy row" },

            // The one system on BOTH lists (spec §6): the TEMP level counts — this row — while the
            // PERM level does not — the twin. Same system, opposite answers, and the reason Pass 0
            // applies to a lane that Passes 2-3 never see: beards allocate nothing (no
            // beards[id].energy, no addEnergy) but their bar fills still consume the budget.
            new CountingSiteSpec { Site = CountingSite.BeardTemp, System = "Beards",
                File = "AllBeardsController.cs", GateLine = 200, IncrementLine = 203,
                LevelUpBlock = ":197-205", EnclosingFunction = "advanceBeard(int id) (:194)",
                AdvisorLanes = new[] { "Beards" },
                NonCountingTwin = "perm levels — convertToTrimmings(): beards[id].permLevel += addedTrimmings(id) " +
                    "([DECOMP] AllBeardsController.cs:294-298), no canLevel()" },

            // ⚠ The system the in-game Description omits. It counts: same five-line shape as the
            // other eight.
            new CountingSiteSpec { Site = CountingSite.AdvancedTraining, System = "Advanced Training",
                File = "AdvancedTrainingController.cs", GateLine = 165, IncrementLine = 168,
                LevelUpBlock = ":162-171", EnclosingFunction = "updateAdvancedTraining() (:149)",
                AdvisorLanes = new[] { "AdvancedTrainingBP" } },
        };

        // Lane membership, derived from the table — the only door into a budget refusal. Built once
        // from Allowlist so the two cannot disagree; a lane name absent here is exempt by omission,
        // exactly as a level path that never calls canLevel() is exempt in the game.
        private static readonly HashSet<string> CountingLaneSet = BuildCountingLaneSet();

        private static HashSet<string> BuildCountingLaneSet()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Allowlist)
                foreach (var lane in row.AdvisorLanes)
                    set.Add(lane);
            return set;
        }

        public static bool Counts(string lane) =>
            lane != null && CountingLaneSet.Contains(lane);

        // ---- the pass ----------------------------------------------------------------------------

        // The per-lane answer, in FeasibilityPass.Verdict so a refusal cannot exist without its
        // reason (the SeatRoster pattern — spec §3.4, decision D2(b)). Seat here means "Pass 0
        // imposes nothing; continue to Pass 1", NOT "feasible": an exempt lane still faces every
        // later pass.
        public static FeasibilityPass.Verdict Evaluate(string lane, in BudgetState s)
        {
            if (!Exhausted(s))
                return FeasibilityPass.Verdict.Seat();
            if (!Counts(lane))
                return FeasibilityPass.Verdict.Seat();

            var burns = BurnsAtCapFor(lane);
            return FeasibilityPass.Verdict.Refuse(string.Format(CultureInfo.InvariantCulture,
                "budget exhausted: {0}/{1} rebirth levels consumed — bar fills are discarded at the canLevel() gate{2}",
                s.RebirthLevels, BudgetCap,
                burns == null ? "" : "; still burns " + burns));
        }

        private static string BurnsAtCapFor(string lane)
        {
            // A lane can sit on two sites (TimeMachineBP, WandoosBP, AugmentBP); the burn is the
            // same family on both, so the first row's description serves.
            foreach (var row in Allowlist)
                foreach (var l in row.AdvisorLanes)
                    if (l == lane)
                        return row.BurnsAtCap;
            return null;
        }

        // ---- surfacing (spec §3.4, operator decision D2(b)) --------------------------------------

        // A lane going to zero is indistinguishable from a bug; the aggregate message is the layer's
        // required output alongside the per-lane reasons the Verdicts already carry. Callers hand in
        // the refused-lane count off their SeatRoster.
        public static string SurfaceMessage(long rebirthLevels, int refusedLaneCount) =>
            string.Format(CultureInfo.InvariantCulture,
                "budget exhausted — {0} lanes idle ({1}/{2} levels consumed), allocation directed to exempt systems",
                refusedLaneCount, rebirthLevels, BudgetCap);
    }
}
