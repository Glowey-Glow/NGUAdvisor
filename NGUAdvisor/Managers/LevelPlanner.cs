using System;

namespace NGUAdvisor.Managers
{
    // Level caps: the advisor manages the AT slots of the game's own target field
    // (advancedTraining.levelTarget), which the allocation breakpoints already respect (a met
    // target drops out and its share redistributes — the ALLAT waterfill sends it to the
    // remaining slots) and which display in the game's own AT target boxes.
    //
    // AT slots are PURPOSE-DRIVEN (user rule): Toughness (0) and Power (1) are the titan push
    // and stay uncapped; Block (2) stops at 99% damage reduction. BLOCK IS THE ONLY SLOT THE
    // ADVISOR WRITES A TARGET INTO — slots 0/1/3/4 are the operator's.
    //
    // BLOCK'S STOP IS A FLOOR (operator ruling 2026-08-07). It used to be written unconditionally,
    // so a target the operator typed into the game's own AT box was overwritten within one tick and
    // could not be made to stick at all — reported as "capping Block below the 100,000 threshold I
    // set". The 99% level is now the LEAST this will accept and a larger hand-set number wins.
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
    // not a lane instruction — ObjectiveTable already carries it as a predicate/rate row and
    // TargetPass already reports it as NO OPINION. A predicate has no business being a TARGET.
    //
    // Every number written here must be DERIVED (a saturating curve or a cost threshold),
    // never a snapshot of the current level — a snapshot is a FREEZE, and a freeze in a
    // serialized game field strands across reloads with no restore path and shrinks the
    // rebirth banks (rate × level-at-rebirth). The old sufficiency freezes (FreezeAt/FreezeTm)
    // did exactly that and were removed (decision record amendment 24 §3). TM in particular
    // takes NO advisor-written target, ever: it never saturates, so speedTarget/multiTarget
    // stay zero — the game's unset sentinel (amendment 24 §4).
    public static class LevelPlanner
    {
        private static long _blockSnapshot;       // slot 2 — the only advisor-written AT target
        private static bool _purposeOn;
        private static bool _wanReclaimed;        // one-shot per process; see TickPurposeTargets

        // R11: the outer whole-Tick catch was removed so AdvisorApply's RunStep("Level planner", ...) owns
        // the bounded fault report. TickPurposeTargets / TickNguTrack keep their own catches.
        public static void Tick()
        {
            var c = Main.Character;
            var s = Main.Settings;
            if (c == null || s == null) return;

            if (!s.AutoProfile)
            {
                ThawAll(c);
                return;
            }

            TickPurposeTargets(c);
            TickNguTrack(c);
        }

        // Guide ch5 24h structure: Normal NGUs most of the run, EVIL NGUs the LAST N hours (N = T7 versions
        // defeated; 1h post-T7v1, 2h post-T7v2 …). Replaces the profile's hardcoded ~22h NGUDiff switch with
        // a dynamic one — but ONLY in the Ch.5 24h shape (T7-capable, Boss 125+) with a TIME-based rebirth
        // target; elsewhere the profile's NGUDiff owns the track. UNTESTED until Boss 125+ (T7-version read
        // via TitanVersion(6)-1 is a first cut).
        // True while this method is past all of its gates and actively steering the NGU level track —
        // i.e. while a SECOND writer is on the same field the profile's NGUDiff timeline owns. Read by
        // the Profiles section readout; cleared every tick so a stale true cannot outlive the window.
        public static bool NguTrackOwned { get; private set; }

        private static void TickNguTrack(Character c)
        {
            NguTrackOwned = false;
            try
            {
                int chapter = 0;
                try { chapter = StageDetector.Detect().Chapter; } catch { }
                double target = -1;
                try { target = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1; } catch { }
                // GATED ON DIFFICULTY, NOT CHAPTER — matching ChallengeOverlay.cs:509-523.
                // Chapter() ends at Evil boss 166 (IDP unlock), which is mid-Evil, so `chapter != 5`
                // stopped being true while the run was still Evil and the Evil-NGU tail was still
                // scheduled. StageDetector also returns chapter 0 on any exception, dropping a
                // healthy Evil run out of its shape for up to 60s. rebirthDifficulty is a stored
                // field on character.settings and does neither.
                //
                // Closes G1-D1: ChallengeOverlay was migrated and this sibling site was not, so past
                // boss 166 the allocation entered EVIL NGU while the level track was never switched
                // to Evil — funding the Evil-NGU tail on the NORMAL NGU track.
                // (advisors/01:427-438, :697; decision record amendment 11 §2)
                bool isEvil = false;
                try { isEvil = c.settings.rebirthDifficulty == difficulty.evil; } catch { }
                if (!isEvil || ZoneHelpers.CurrentHighestBoss(c) < 125 || target <= 0) return;

                // THE PROFILE WINS IF IT SCHEDULED THE TRACK ITSELF. The header above says "elsewhere the
                // profile's NGUDiff owns the track" — but inside this ch.5 window it did not, and both
                // writers hit c.settings.nguLevelTrack every tick with no arbitration. Concretely, on
                // 24hr-EarlyEvil (Diff:0 -> Diff:1 at h22) with a 24h target and T7 v1 down, switchAt
                // lands near h23, so from h22 to h23 this method wrote the track back to NORMAL — quietly
                // undoing the author's swap and costing an hour of Evil NGUs in the one window the guide
                // says they matter most. A profile with a real NGUDiff timeline now keeps ownership.
                try { if (Main.Profile != null && Main.Profile.ProfileOwnsNguTrack) return; } catch { }

                int t7 = 0;
                // Kills, not the selector. Under the old read this returned 0 on an account that had killed
                // T7 v2 eight times, so the `t7 < 1` guard below returned every tick and the Evil NGU track
                // switch has never fired once in this installation's entire log history.
                try { t7 = ZoneHelpers.VersionsDefeatedByKills(6); } catch { }
                if (t7 < 1) return; // no T7 version defeated → no Evil NGU hours (guide ch5 rule: N = versions defeated)

                // PAST EVERY GATE, SO THIS METHOD IS A LIVE WRITER OF nguLevelTrack THIS TICK — and it is
                // not the only one. NGUDiffBreakpoints writes the same field from the profile's own
                // timeline, with no arbitration between them beyond the ProfileOwnsNguTrack deferral
                // above. The Profiles readout needs to say so; there is no way to infer it from outside.
                NguTrackOwned = true;
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

                // Recorded whether or not this tick changed anything: the ledger's job here is to say
                // that a SECOND writer is live on this field, which is true while the window is open
                // regardless of whether the two writers currently agree. They agreeing is exactly when
                // a contested field looks healthy and is not.
                WriteLedger.Record("ngu.track.planner", want == difficulty.evil ? "Evil" : "Normal",
                    $"guide ch.5 — the last {evilHours:0}h of the run go on Evil NGUs",
                    ChallengeOverlay.Segment,
                    $"T7 versions beaten (from the bestiary): {t7} → {evilHours:0} Evil hour(s)",
                    $"Switch point is {switchAt / 3600.0:0.0}h; the run is at {c.rebirthTime.totalseconds / 3600.0:0.0}h",
                    "Your profile's NGUDiff timeline writes this field too — last writer each tick wins");
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

                    // RECLAIM, NOT AN OVERRIDE, AND IT FIRES ONCE PER PROCESS. Until the ruling above
                    // this was the only writer that had ever touched levelTarget[3]/[4], and it left a
                    // number behind every time it left its segment window — so a value sitting in
                    // those slots at the first engage of a session is one the advisor stranded, and
                    // withdrawing it is the exit that the old code never performed. After this, nothing
                    // in the advisor writes them again, so a target typed later stands for the session.
                    //
                    // The one-shot is what makes that promise keepable. Reclaiming on EVERY engage
                    // would zero a hand-typed target each time the operator toggled the auto profile
                    // back on, which is the same class of defect as the one being closed.
                    bool reclaimed = false;
                    if (!_wanReclaimed)
                    {
                        _wanReclaimed = true;
                        reclaimed = targets[3] != 0 || targets[4] != 0;
                        if (reclaimed)
                            WriteLedger.Record("at.wandoos.reclaim", "0 (unset)",
                                "withdrew a target the advisor stranded here before this rule existed",
                                ChallengeOverlay.Segment,
                                "Slots 3 and 4 held " + targets[3] + " / " + targets[4] + " at engage",
                                "Nothing writes these any more, so a value here was left by the old rule",
                                "Cleared once, this process only — a target you type from here survives");
                        targets[3] = 0;   // 0 = the game's unset sentinel
                        targets[4] = 0;
                    }

                    ChallengeOverlay.Record("AT purpose caps on",
                        "block → 100,000 levels, every difficulty (a higher target of yours is kept) · " +
                        "rest to Power/Toughness" +
                        (reclaimed ? " · cleared a stale advisor target off the wandoos ATs (they take none now)"
                                   : " · wandoos ATs untouched"));
                }

                // Block is a FLOOR, not a set — a hand-typed target above the cap is the operator's and
                // is never lowered to it. See LaneTargets.AdvancedTrainingPurposeFloor for why this
                // slot and not the Wandoos ones.
                //
                // ⚠ THE STOP IS THE RULED CONSTANT, NOT A DERIVED ONE, AND THAT IS THE WHOLE CHANGE.
                // This used to pass BlockStopLevel(c) = ceil(49 / block.levelFactor), which solves the
                // Block curve for 99% damage reduction and lands near 5,000 — twenty times below the
                // cap. Because the floor only protects an ALREADY-SET higher target, it could not pull
                // a 100,000 down, but ON AN UNSET SLOT IT WROTE ~5,000 FIRST and nothing ever raised
                // it: the slot sat at 99% for the rest of the run. [OPERATOR] 2026-08-07: "the 5000 is
                // not acceptable... this goes for every difficulty".
                //
                // THIS SINGLE WRITE COVERS ALL THREE DIFFICULTIES BY ITSELF. levelTarget[2] is one
                // field per slot with NO per-track split — ConstraintLayerBridge.LaneStateFor reads
                // advancedTraining.level[i] with no track branch, unlike the NGU lanes — so there is
                // no Normal/Evil/Sadistic variant of this target to write.
                //
                // AMENDMENT 24 §3 KEPT THIS WRITE AS "DERIVED FROM A SATURATING CURVE" AND IT STILL
                // QUALIFIES, more strongly than before. 24 §1's disqualifying case is a FREEZE —
                // max(1, current level), meaningless outside the process. A ruled constant is stable
                // and re-derivable after any reload, which is the actual serialized-safe test; it is
                // ceil(49/f) that was the weaker citizen here, moving with a Unity-serialized field
                // (AdvancedTrainingController.cs:33, no initializer) the advisor cannot verify. ⚠ AND
                // 24 §5 JUSTIFIED KEEPING IT BY CITING [OPERATOR]'s "100k levels / 99.95% block" —
                // a number this formula does not produce. That conflation is the defect being closed.
                ApplyPurposeFloor(targets, 2, ObjectiveTable.AtBlockHardCapLevel);
                // SLOTS 3/4 ARE DELIBERATELY ABSENT. See the wandoos paragraph in the class header for
                // the ruling; `c` is still the parameter this method takes because slot 2 needs it.
            }
            catch (Exception e) { Main.LogDebug($"LevelPlanner purpose caps: {e.Message}"); }
        }

        // The stop is a MINIMUM: an existing higher target is the operator's and
        // survives. The decision itself is LaneTargets.AdvancedTrainingPurposeFloor (Unity-free, tested).
        private static void ApplyPurposeFloor(long[] targets, int slot, long stop)
        {
            if (stop == long.MinValue) return;   // unknown — leave the current target alone
            var next = LaneTargets.AdvancedTrainingPurposeFloor(targets[slot], stop);
            if (targets[slot] != next) targets[slot] = next;

            // Recorded EVERY tick, deliberately. WriteLedger keeps one row per field and ignores an
            // unchanged value, so re-asserting a floor sixty times a minute costs one comparison and
            // produces one row — while a value the operator raises by hand shows up on the very next
            // tick as a new row rather than waiting for the advisor to happen to write again.
            WriteLedger.Record("at.block", next.ToString("N0"),
                next > stop
                    ? "your hand-set target is higher than the floor, so it stands"
                    : "Block hard cap — the least the advisor will accept here",
                ChallengeOverlay.Segment,
                "Written once when the auto profile engaged, then held as a FLOOR",
                "A larger number you type is kept; a smaller one is raised back to this",
                "Restored to your own value when the auto profile goes off");
        }

        // BlockStopLevel WAS HERE AND IS DELETED, not merely bypassed — a second way to compute a
        // block stop is a second writer waiting to be re-wired. Its derivation, preserved so nobody
        // re-derives it from scratch and reintroduces the number:
        //
        //     blockBonus = 0.5 / (1 + f·L) is the REMAINING damage fraction (the tooltip shows
        //     reduction = 1 − that), so 99% reduction needs f·L >= 49, i.e. L = ceil(49 / f) where
        //     f = advancedTrainingController.block.levelFactor. Near 5,000 in practice — but f is
        //     Unity-serialized with no initializer ([DECOMP] AdvancedTrainingController.cs:33), so
        //     "5,000" was always an INFERENCE from the guide's 99%-at-5k rung, never a measurement.
        //
        // It has no other caller and never had one: TickPurposeTargets' slot-2 write was its only
        // use. The 99% rung itself is NOT lost — it survives as the ch.3 precondition row and as
        // BlockCurveRungs in ObjectiveTable, which is where a milestone belongs. What it must not be
        // again is a TARGET.

        // WandoosStopLevel WAS HERE AND IS DELETED, on the same terms as BlockStopLevel above: a
        // second way to compute a wandoos-AT stop is a second writer waiting to be re-wired, and
        // this one had already been re-wired once (marathon → + EVIL CLIMB / AUGMENTATION / NGU+AT /
        // EVIL NGU, commit 047cd33) into the segment that broke it. Its derivation, preserved so
        // nobody re-derives it from scratch and reintroduces the target:
        //
        //     The dump cost is baseTime / totalWandoosSpeed and speed scales with (1 + f·L), so the
        //     level at which the cost falls to 1% of max E/M is
        //         sOther     = speed / (1 + f·L₀)              // speed without this AT's factor
        //         needFactor = baseTime / (0.01 · cap · sOther)
        //         L          = ceil((needFactor − 1) / f),     f = wandoosEnergy/Magic.levelFactor
        //     with <= 0 meaning "already under 1% at level 0". baseEnergyTime()/baseMagicTime() are
        //     CONSTANT per difficulty and OS ([DECOMP] Wandoos98Controller.cs:73-83, `advisors/01`
        //     G1-D18) — the two live terms are SPEED and CAP, and BOTH ARE GEAR. That is precisely
        //     why it could not be written as a target: it is a reading of the worn loadout, and the
        //     worn loadout in the segments this ran in was Augments, not a cap loadout.
        //
        // The 1%-dump rung itself is NOT lost. It survives where a cost predicate belongs — as
        // ObjectiveTable's "run Wandoos AT until CHEAP TO RUN WANDOOS 98" predicate/rate row, which
        // TargetPass reports as NO OPINION rather than converting into a level.
        //
        // Amendment 26 §1's gear wording in constraint-layer-spec §10 and `03` §7.2 was transcribed
        // from the comment that lived here; the derivation above is its source and still stands.

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

        private static void ThawAll(Character c)
        {
            if (_purposeOn) ThawPurpose(c);
        }
    }
}
