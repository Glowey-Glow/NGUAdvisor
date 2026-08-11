using System;

namespace NGUAdvisor.Managers
{
    // Level caps (user-approved plan): the advisor manages the game's own target fields
    // (advancedTraining.levelTarget, machine.speedTarget/multiTarget), which the allocation
    // breakpoints already respect (a met target drops out and its share redistributes — the
    // ALLAT waterfill sends it to the remaining slots) and which display in the game's own
    // AT/TM target boxes.
    //
    // AT slots are PURPOSE-DRIVEN (user rule): Toughness (0) and Power (1) are the titan push
    // and stay uncapped; Block (2) stops at 99% damage reduction. BLOCK IS THE ONLY SLOT THE
    // ADVISOR WRITES A TARGET INTO — slots 0/1/3/4 are the operator's.
    //
    // THE WANDOOS ATs (3/4) TAKE NO ADVISOR-WRITTEN TARGET, EVER (operator ruling 2026-08-10).
    // They used to be set to the level at which Wandoos' cap-speed dump costs <= 1% of max E/M,
    // in whichever segment was judged to run Wandoos as an intended E/M sink. That was wrong twice
    // over, and the second failure is what closed the feature:
    //
    //   PRICED AGAINST THE WRONG LOADOUT. The stop divides by the CURRENT totalCapEnergy/Magic, so
    //   a loadout that is not cap-optimised inflates it. AT HOUR was excluded for exactly this
    //   ("its weaker caps inflate the targets and steal AT from Power/Toughness", user-caught) —
    //   but AUGMENTATION, added later, wears the Augments loadout and had the same defect. On a
    //   ~30-minute rebirth cycle AUGMENTATION is the pre-rebirth segment, so the operator got a
    //   fresh inflated target on both wandoos ATs once per rebirth, all day.
    //
    //   THE WRITE OUTLIVED ITS WINDOW. The stops were applied only inside the segment list and
    //   never withdrawn on the way out, so each window stranded a number in a serialized game
    //   field for the rest of the run. Clearing it by hand held only until the next window.
    //
    // The guide's rule here ("run Wandoos AT until cheap to run Wandoos 98") is a COST PREDICATE,
    // not a lane instruction. A predicate has no business being a TARGET.
    //
    // Sufficiency freezes (marathon only):
    //   Power/Toughness — frozen only while adventure stats beat the NEXT titan AK requirement
    //        with 10% headroom (TitanAk table); a new titan/version target automatically thaws.
    //   TM — frozen only while the TM holds gold AND augments are affordable (the drain
    //        ledger's starvation check); gold trouble thaws it.
    // User targets are snapshotted before the first override and restored on auto-profile off.
    public static class LevelPlanner
    {
        private static long[] _atSnapshot;        // slots 0..1 (sufficiency freeze)
        private static long _blockSnapshot;       // slot 2 — the only advisor-written AT target
        private static long _speedSnapshot, _multiSnapshot;
        private static bool _frozenAt, _frozenTm, _purposeOn;
        private static bool _wanReclaimed;        // one-shot per process; see TickPurposeTargets

        public static string Status { get; private set; } = "";
        public static bool AtFrozen => _frozenAt;
        public static bool TmFrozen => _frozenTm;

        // R11: the outer whole-Tick catch was removed so AdvisorApply's RunStep("Level planner", ...) owns
        // the bounded fault report. The narrow NextObjective / gold probes keep their own catches.
        public static void Tick()
        {
            var c = Main.Character;
            var s = Main.Settings;
            if (c == null || s == null) return;

            if (!s.AutoProfile)
            {
                ThawAll(c);
                Status = "";
                return;
            }

            TickPurposeTargets(c);
            TickNguTrack(c);

            bool marathon = ChallengeOverlay.Segment == "NGU MARATHON";

            // Power/Toughness sufficiency vs the REALISTIC objective: the next un-AK'd
            // titan+version at THIS difficulty (never Evil content while on Normal — the
            // T7 overreach bug).
            bool atSufficient = false;
            try
            {
                var obj = OptimizationAdvisor.NextObjective();
                atSufficient = !obj.Known
                    || (c.totalAdvAttack() >= obj.ReqAttack * 1.1 && c.totalAdvDefense() >= obj.ReqDefense * 1.1);
            }
            catch { }

            bool wantAt = marathon && atSufficient;
            if (wantAt && !_frozenAt) FreezeAt(c);
            else if (!wantAt && _frozenAt) ThawAt(c);

            // TM sufficiency: funded and not starving the augment budget.
            bool goldOk = false;
            try { goldOk = c.machine.realBaseGold > 0 && !OptimizationAdvisor.GoldStarvedForAugs(c, 1.0); } catch { }

            bool wantTm = marathon && goldOk;
            if (wantTm && !_frozenTm) FreezeTm(c);
            else if (!wantTm && _frozenTm) ThawTm(c);

            Status = _frozenAt || _frozenTm
                ? $"caps: {(_frozenAt ? "AT" : "")}{(_frozenAt && _frozenTm ? "+" : "")}{(_frozenTm ? "TM" : "")} frozen"
                : "caps: none";
        }

        // Guide ch5 24h structure: Normal NGUs most of the run, EVIL NGUs the LAST N hours (N = T7 versions
        // defeated; 1h post-T7v1, 2h post-T7v2 …). Replaces the profile's hardcoded ~22h NGUDiff switch with
        // a dynamic one — but ONLY in the Ch.5 24h shape (T7-capable, Boss 125+) with a TIME-based rebirth
        // target; elsewhere the profile's NGUDiff owns the track. UNTESTED until Boss 125+ (T7-version read
        // via TitanVersion(6)-1 is a first cut).
        private static void TickNguTrack(Character c)
        {
            try
            {
                int chapter = 0;
                try { chapter = StageDetector.Detect().Chapter; } catch { }
                double target = -1;
                try { target = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1; } catch { }
                if (chapter != 5 || ZoneHelpers.CurrentHighestBoss(c) < 125 || target <= 0) return;

                int t7 = 0;
                try { t7 = Math.Max(0, ZoneHelpers.TitanVersion(6) - 1); } catch { }
                if (t7 < 1) return; // no T7 version defeated → no Evil NGU hours (guide ch5 rule: N = versions defeated)
                double evilHours = t7;
                double switchAt = target - evilHours * 3600.0;
                var want = c.rebirthTime.totalseconds >= switchAt ? difficulty.evil : difficulty.normal;

                if (c.settings.nguLevelTrack != want)
                {
                    c.settings.nguLevelTrack = want;
                    try { c.NGUController.refreshMenu(); } catch { }
                    ChallengeOverlay.Record("NGU track", $"→ {(want == difficulty.evil ? "EVIL" : "Normal")} NGUs",
                        $"guide ch5: last {evilHours:0}h evil (T7 v{t7} done)");
                }
            }
            catch (Exception e) { Main.LogDebug($"LevelPlanner NGU track: {e.Message}"); }
        }

        // ---- Purpose-driven AT caps (slots 2..4), live every tick while the auto profile runs. ----

        private static void TickPurposeTargets(Character c)
        {
            try
            {
                if (!c.buttons.advancedTraining.interactable) return;
                var targets = c.advancedTraining.levelTarget;
                if (targets == null || targets.Length < 5) return;

                if (!_purposeOn)
                {
                    _blockSnapshot = targets[2];
                    _purposeOn = true;

                    // RECLAIM, NOT AN OVERRIDE, AND IT FIRES ONCE PER PROCESS. Until the ruling in the
                    // class header this was the only writer that had ever touched levelTarget[3]/[4],
                    // and it left a number behind every time it left its segment window — so a value
                    // sitting in those slots at the first engage of a session is one the advisor
                    // stranded, and withdrawing it is the exit the old code never performed. After
                    // this, nothing writes them again, so a target typed later stands for the session.
                    //
                    // The one-shot is what makes that promise keepable. Reclaiming on EVERY engage
                    // would zero a hand-typed target each time the operator toggled the auto profile
                    // back on, which is the same class of defect as the one being closed.
                    bool reclaimed = false;
                    if (!_wanReclaimed)
                    {
                        _wanReclaimed = true;
                        reclaimed = targets[3] != 0 || targets[4] != 0;
                        targets[3] = 0;   // 0 = the game's unset sentinel
                        targets[4] = 0;
                    }

                    ChallengeOverlay.Record("AT purpose caps on",
                        "block → 99% reduction · rest to Power/Toughness" +
                        (reclaimed ? " · cleared a stale advisor target off the wandoos ATs (they take none now)"
                                   : " · wandoos ATs untouched"));
                }

                ApplyPurpose(targets, 2, BlockStopLevel(c));
                // SLOTS 3/4 ARE DELIBERATELY ABSENT. See the wandoos paragraph in the class header.
            }
            catch (Exception e) { Main.LogDebug($"LevelPlanner purpose caps: {e.Message}"); }
        }

        private static void ApplyPurpose(long[] targets, int slot, long stop)
        {
            if (stop == long.MinValue) return;   // unknown — leave the current target alone
            if (targets[slot] != stop) targets[slot] = stop;
        }

        // Block stops at 99% damage reduction. The game's blockBonus = 0.5 / (1 + f·L) (remaining
        // damage fraction; tooltip shows reduction = 1 − that), so 99% needs f·L >= 49.
        private static long BlockStopLevel(Character c)
        {
            try
            {
                float f = c.advancedTrainingController.block.levelFactor;
                if (f <= 0) return long.MinValue;
                return (long)Math.Ceiling(49.0 / f);
            }
            catch { return long.MinValue; }
        }

        // WandoosStopLevel WAS HERE AND IS DELETED (operator ruling 2026-08-10) — a second way to
        // compute a wandoos-AT stop is a second writer waiting to be re-wired, and this one had
        // already been re-wired once, into the segment that broke it. Its derivation, preserved so
        // nobody re-derives it from scratch and reintroduces the target:
        //
        //     The dump cost is baseTime / totalWandoosSpeed and speed scales with (1 + f·L), so the
        //     level at which the cost falls to 1% of max E/M is
        //         sOther     = speed / (1 + f·L₀)              // speed without this AT's factor
        //         needFactor = baseTime / (0.01 · cap · sOther)
        //         L          = ceil((needFactor − 1) / f),     f = wandoosEnergy/Magic.levelFactor
        //     with <= 0 meaning "already under 1% at level 0". baseEnergyTime()/baseMagicTime() are
        //     CONSTANT per difficulty and OS ([DECOMP] Wandoos98Controller.cs:73-83) — the two live
        //     terms are SPEED and CAP, and BOTH ARE GEAR. That is precisely why it could not be
        //     written as a target: it is a reading of the worn loadout, and the worn loadout in the
        //     segments this ran in was Augments, not a cap loadout.
        //
        // The 1%-dump rung itself is NOT lost. It survives where a cost predicate belongs — as the
        // "run Wandoos AT until CHEAP TO RUN WANDOOS 98" predicate row, never as a level.

        private static void ThawPurpose(Character c)
        {
            // SLOT 2 ONLY. Slots 3/4 are restored by NOT being touched: the advisor no longer writes
            // them, so there is nothing of its own to withdraw, and a snapshot-restore here would be
            // the last surviving way for a stale number to reach them — it would re-impose whatever
            // sat in the boxes at engage time over anything the operator typed since.
            try
            {
                var targets = c.advancedTraining.levelTarget;
                if (targets != null && targets.Length >= 5) targets[2] = _blockSnapshot;
            }
            catch { }
            _blockSnapshot = 0;
            _purposeOn = false;
            ChallengeOverlay.Record("AT purpose caps off", "auto profile off — user Block target restored");
        }

        // ---- Sufficiency freeze: Power/Toughness (slots 0..1) only — 2..4 are purpose-owned. ----

        private static void FreezeAt(Character c)
        {
            var targets = c.advancedTraining.levelTarget;
            _atSnapshot = new long[2];
            for (int i = 0; i < 2 && i < targets.Length; i++)
            {
                _atSnapshot[i] = targets[i];
                long lvl = c.advancedTraining.level[i];
                targets[i] = lvl > 0 ? lvl : -1;   // -1 = hold at zero (target 0 means uncapped)
            }
            _frozenAt = true;
            ChallengeOverlay.Record("Power/Toughness capped at current levels", "AK requirement beaten ×1.1 — energy to the marathon");
        }

        private static void ThawAt(Character c)
        {
            if (_atSnapshot != null)
            {
                var targets = c.advancedTraining.levelTarget;
                for (int i = 0; i < 2 && i < targets.Length && i < _atSnapshot.Length; i++)
                    targets[i] = _atSnapshot[i];
            }
            _atSnapshot = null;
            _frozenAt = false;
            ChallengeOverlay.Record("Power/Toughness caps released", "push/AK target needs stats again");
        }

        private static void FreezeTm(Character c)
        {
            _speedSnapshot = c.machine.speedTarget;
            _multiSnapshot = c.machine.multiTarget;
            c.machine.speedTarget = Math.Max(1, c.machine.levelSpeed);
            c.machine.multiTarget = Math.Max(1, c.machine.levelGoldMulti);
            _frozenTm = true;
            ChallengeOverlay.Record("TM capped at current levels", "gold funded — energy/magic to the marathon");
        }

        private static void ThawTm(Character c)
        {
            c.machine.speedTarget = _speedSnapshot;
            c.machine.multiTarget = _multiSnapshot;
            _frozenTm = false;
            ChallengeOverlay.Record("TM caps released", "gold needs levels again");
        }

        private static void ThawAll(Character c)
        {
            if (_frozenAt) ThawAt(c);
            if (_frozenTm) ThawTm(c);
            if (_purposeOn) ThawPurpose(c);
        }
    }
}
