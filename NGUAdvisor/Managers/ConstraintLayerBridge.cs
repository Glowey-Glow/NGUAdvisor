using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.AllocationProfiles.BreakpointTypes;

namespace NGUAdvisor.Managers
{
    // THE LIVE WIRING of the constraint layer (constraint-layer-spec §2, §10): builds one fresh
    // LaneSpec per membership lane from live game state, runs ConstraintLayer.Compose, and executes
    // the plan through the lanes' own Allocate() — each lane's stair-snap math IS its Pass 2
    // capacity (CapacityPass.Table's Advisor rows), so a seated lane is offered the remaining pool
    // and self-limits below it. Evil-track NGU lanes are the exception: RATE lanes (amendment 18
    // §1), capacity read from the game's own cap helpers (energyNGUCapAmount/magicNGUCapAmount,
    // [DECOMP] AllNGUController.cs:1380-1424 / :1426-1470), chunked into their share per amendment
    // 19 §3.1's NguCap arithmetic. ⚠ EVERY seated lane — self-limiting or rate — is now offered
    // min(capacity, remaining / seated destinations not yet offered), NOT the whole remaining pool
    // (amendment 28: the two regimes are gone, and so is offer-what-is-left).
    //
    // Entered from Energy/MagicBreakpoints.PerformSwap behind the kill switch
    // (SavedSettings.ConstraintAllocator, default ON). R3Breakpoints is NOT routed here: it never
    // had the prioCount divisor (prioCount stays 1 by design), already walks the whole list with
    // full-remaining offers and self-limiting lanes, and its surplus destination is the wish spare
    // pass — it is the fill model this layer generalises, not a path it replaces.
    //
    // MEMBERSHIP comes from the same construction as the old path: parse-null + IsValid() filter,
    // then ChallengeOverlay.TransformPriorities (challenge templates, LSC, the auto profile). The
    // three REPLACED token programs (AUGMENTATION / NGU+AT / EVIL NGU) define membership and ORDER
    // and NOTHING ELSE: their share-shaping semantics — CAP-vs-non-cap, :percent, absorber ordering —
    // were inert here because there is no divisor to work around, and are now stripped from the
    // tokens themselves rather than left as dead decorations on a live grammar. Every OTHER token
    // source still carries them (the challenge templates, the all-dead fallback, the remaining
    // segments, and every hand-written profile) — inert on this path, live on the kill switch's, and
    // the grammar still parses them unchanged. Consequence of reusing the
    // IsValid() filter: the game-field target fallback (spec §10 standalone mode) is applied BEFORE
    // Compose, so Pass 3 inside the composition only ever fires from a supplied target table. ⚠ ONE
    // IS NOW SUPPLIED — RefreshTargetTable fills TargetTable from ObjectiveTargets on every swap,
    // and its whole writable inventory is AT slot 2 at ObjectiveTable.AtBlockHardCapLevel. Because
    // the game-field fallback runs FIRST, that stop is a SECOND gate behind LevelPlanner's write of
    // the same constant to levelTarget[2], not a new one: the two agree by construction, both
    // reading AtBlockHardCapLevel.
    //
    // NO CACHING ACROSS TICKS (spec §4.5): every call rebuilds every verdict from live reads. The
    // only statics here are surfacing latches (log-on-change) and the parity throttle — none of
    // them feed allocation.
    public static class ConstraintLayerBridge
    {
        public static bool NewPathEnabled => Main.Settings != null && Main.Settings.ConstraintAllocator;

        // Declared focus (amendment 18 §2) — a user input; nothing sets it yet (the UI slot is a
        // follow-up). Keyed on lane LABELS ("NGU-4"). The sink is exempt inside WithFocus.
        public static ConstraintLayer.FocusSet DeclaredFocus;

        // THE TARGET TABLE — the first of spec §10's three inputs, and the objective layer's seat.
        // ⚠ IT NOW HAS A PRODUCER, AND THIS IS THE FIRST TIME THE OBJECTIVE TABLE CAN MOVE A UNIT OF
        // THE POOL. ObjectiveTargets.Produce is called at the top of PerformSwap — see
        // RefreshTargetTable below for the read and its hold conditions. 37 §S5 A2's "none of §10's
        // three inputs has a producer" is discharged for this one; DeclaredFocus still has none.
        //
        // WHAT THE TABLE CAN ACTUALLY DO, MEASURED RATHER THAN ASSUMED (ObjectiveTargetsTests):
        // across the producer's entire query space — 8 chapters x 3 NGU tracks x 3 run tracks — the
        // rows it can emit are ALL `at` rows, and exactly ONE of them routes to WriteTarget:
        // at-2 @ ObjectiveTable.AtBlockHardCapLevel on the EVIL track at chapter 5. WriteTarget +
        // Satisfied is the only shape ConstraintLayer.WantFromAnswer turns into a stop, so that row
        // is the complete inventory of this field's power. Every other produced row is a
        // Precondition and holds the want open at every level, forever.
        //
        // ⚠ AND IT STOPS ONE SLOT, NOT A SYSTEM. `ALLAT` yields FIVE separate AdvancedTrainingBP
        // with Index 0..4 (ResourceBreakpoint.cs:321-334) and LaneIndex => Index, so each AT slot is
        // its OWN LANE; RowsFor filters on System AND Index, so slots 0/1/3/4 get null and keep
        // funding at any level. Advanced Training as a whole is never terminated. [OPERATOR]: the
        // Block AT is a hard cap at 100,000.
        //
        // ⚠ NULL OR EMPTY IS NOT A DEGRADED MODE, it is spec §10's STANDALONE contract: Passes 0-2
        // run and Pass 3 falls back to whatever targets are written to the game's own fields. That
        // fallback is already applied — by the IsValid() membership filter above, which evaluates
        // each lane's own TargetMet() against those fields before a spec is ever built. So a lane
        // reaching Pass 3 has ALREADY passed the game's own comparator, and with no rows there is
        // nothing further a target can say. "Every constraint remains enforced … only less directed."
        public static IList<TargetPass.TargetRow> TargetTable;

        // The last computed plan per pool — every zero with its reason, for the UI/companion to
        // render (spec §10: "a surfaced reason for every lane receiving zero").
        public static ConstraintLayer.Plan LastEnergyPlan { get; private set; }
        public static ConstraintLayer.Plan LastMagicPlan { get; private set; }

        // Surfacing latches: state-change logging, never per-tick spam, never silence (spec §3.4).
        private static readonly Dictionary<ResourceType, string> _lastBudgetMsg =
            new Dictionary<ResourceType, string>();
        private static readonly Dictionary<ResourceType, string> _lastSinkIssue =
            new Dictionary<ResourceType, string>();
        private static readonly Dictionary<ResourceType, string> _rateSkipSig =
            new Dictionary<ResourceType, string>();
        private static readonly Dictionary<ResourceType, DateTime> _rateSkipAt =
            new Dictionary<ResourceType, DateTime>();

        // Parity throttle state (ConstraintParity owns the pure decision).
        private static readonly Dictionary<ResourceType, string> _paritySig =
            new Dictionary<ResourceType, string>();
        private static readonly Dictionary<ResourceType, DateTime> _parityAt =
            new Dictionary<ResourceType, DateTime>();

        // Objective-comparison throttle state (ObjectiveParity owns the pure decision, and defers
        // the throttle arithmetic itself to ConstraintParity so there is one 30s/600s pair). The
        // third dictionary latches the HELD state — a comparison that could not run, which must be
        // said once rather than either spammed or swallowed.
        private static readonly Dictionary<ResourceType, string> _objectiveSig =
            new Dictionary<ResourceType, string>();
        private static readonly Dictionary<ResourceType, DateTime> _objectiveAt =
            new Dictionary<ResourceType, DateTime>();
        private static readonly Dictionary<ResourceType, string> _objectiveHeld =
            new Dictionary<ResourceType, string>();

        // [AllocDbg] twin throttle state (AllocTelemetry owns the pure decision): a 60s heartbeat per
        // pool so a steady state stays visible, overridden by a disposition change behind a one-tick
        // floor. Per RESOURCE, not per lane — the founding defect was a RATIO ACROSS lanes inside one
        // tick, which a per-lane latch would have split across independently gated lines.
        private static readonly Dictionary<ResourceType, string> _telemetrySig =
            new Dictionary<ResourceType, string>();
        private static readonly Dictionary<ResourceType, DateTime> _telemetryAt =
            new Dictionary<ResourceType, DateTime>();

        public static bool PerformSwap(ResourceBreakpoint[] original, ResourceType type)
        {
            var c = Main.Character;
            if (c == null || original == null)
                return false;

            // ⚠ THE TARGET TABLE IS REBUILT FIRST AND UNCONDITIONALLY, on every entry, before any
            // early return below can leave a previous tick's rows standing. Spec §4.5 forbids
            // caching across ticks, and a STALE table is worse than none here: rows are keyed to a
            // chapter and a track, so one held over from a chapter the run has left would stop a
            // lane on a stop the guide no longer makes.
            RefreshTargetTable(c);

            // Membership: identical construction to the old path (null filter for unparseable
            // tokens, IsValid for locked/satisfied lanes, then the overlay's transforms).
            var valid = original.Where(x => x != null && x.IsValid()).ToList();
            var membership = ChallengeOverlay.TransformPriorities(original, valid, type);
            if (membership.Count == 0)
                return false;

            // Pass 0 state — LIVE reads, never shadowed (spec §3.2).
            var budget = new BudgetPass.BudgetState();
            try
            {
                budget.InLevelChallenge = c.challenges.levelChallenge10k.inChallenge;
                budget.RebirthLevels = c.settings.rebirthLevels;
            }
            catch (Exception e) { Main.LogDebug($"Constraint budget read: {e.Message}"); }

            // Amendment 18: the LIVE track selector is settings.nguLevelTrack — NOT
            // rebirthDifficulty; a player runs Normal NGUs inside an Evil rebirth for most of the
            // day (23 §2.3). Evil track => every NGU lane is a rate lane, both pools.
            bool evilTrack = false;
            try { evilTrack = c.settings.nguLevelTrack == difficulty.evil; } catch { }

            var specs = new List<ConstraintLayer.LaneSpec>(membership.Count);
            foreach (var bp in membership)
                specs.Add(BuildSpec(bp, c, evilTrack));

            // Reclaim before reading the pool — same reclaim the old loop performs.
            //
            // ⚠ THE RECLAIM IS TOTAL AND THE SEATING LIST IS NOT. removeAllEnergy() empties EVERY basic
            // training slot, including the ones IsValid() just excluded for being at cap, and the fill
            // only refills what it seated — so a saturated slot loses its energy and gets nothing back.
            // Snapshot first, put back what no seated lane will refund. Full account in
            // LaneCapMath.BasicTrainingReclaimRestore.
            bool reclaimsBasicTraining = membership.Any(x => x is BasicTrainingBP);
            long[] btBefore = reclaimsBasicTraining ? SnapshotBasicTraining(c) : null;
            Reclaim(c, type, reclaimsBasicTraining);
            if (btBefore != null) RestoreUnseatedBasicTraining(c, membership, btBefore);
            long pool = Idle(c, type);

            var plan = ConstraintLayer.Compose(pool, budget, specs);

            // ONE ROUND of the fill: seated lanes in list order, each offered its SHARE of what
            // remains at its turn — min(capacity, remaining / seated destinations not yet offered) —
            // and self-limiting below it; rate lanes chunk that share through NguCap (the lane's own
            // Allocate then runs the same NguCap on the offered budget, as it always did under the
            // old allocator). The arithmetic is ConstraintLayer.FillSession — the same one the
            // tests drive; ⚠ the LANES OVERLOAD carries the divisor, and the pool-only overload
            // degenerates to one destination, which is allocation-as-a-residual again.
            //
            // THE WATERFILL (amendment 36) [OPERATOR]: "All of the systems that can take a resource
            // should be in consideration for the sink, not just wandoos … spread across multiple
            // systems for constant gain as well as wandoos." Round 1 is the single pass above,
            // byte for byte; the loop then re-offers what that round left to the lanes that PROVED
            // in it that they would convert more (ConstraintLayer.AppetiteProven — rule A is a
            // theorem about the game's own stair arithmetic, rule B kills the constant-take case).
            // Lanes at a ceiling — a cap-1 BasicTraining slot taking 1 of 100 B, an NGU past its
            // one-level-per-tick price — fail rule A in the round that discovers them and are never
            // offered again. Termination is proved in the Waterfill header, not counted.
            var fill = new ConstraintLayer.Waterfill(pool, plan.Lanes, plan.SinkIndex);
            var takes = new long[membership.Count];
            // What the fill OFFERED each lane, kept alongside what it TOOK. FillSession already
            // returns the number — it is not hidden state — but nothing downstream held it, and the
            // GAP between offered and taken is the single most diagnostic figure on this path: a
            // self-limiting lane offered the whole pool and absorbing one unit of it looks identical
            // to a healthy one in every existing channel. Same local-array shape as `takes`, which
            // Parity already consumes. Both are CUMULATIVE across the rounds.
            var offers = new long[membership.Count];
            ConstraintLayer.FillSession session;
            ConstraintLayer.FillSession firstRound = null;
            while ((session = fill.BeginRound()) != null)
            {
                for (int i = 0; i < membership.Count; i++)
                {
                    if (i == plan.SinkIndex || !fill.IsLive(i))
                        continue;
                    string skip;
                    long offer = session.Offer(fill.LaneForRound(i), out skip);
                    // Reasons and the rate-skip tally stay ROUND 1's, so every surfaced number means
                    // exactly what it meant before this commit.
                    if (firstRound == null && skip != null)
                        plan.Lanes[i].Reason = skip;
                    if (offer <= 0)
                    {
                        fill.Record(i, 0, 0);
                        continue;
                    }

                    var bp = membership[i];
                    long before = Idle(c, type);
                    bp.OfferBudget(Math.Min(offer, before));
                    try { bp.Allocate(); }
                    catch (Exception e) { Main.LogDebug($"Constraint fill {bp.Label}: {e.Message}"); }
                    long take = before - Idle(c, type);
                    if (take < 0) take = 0;
                    offers[i] += offer;
                    takes[i] += take;
                    plan.Lanes[i].Allocation = takes[i];
                    plan.Lanes[i].Offered = offers[i];   // survives the tick, unlike the local array
                    session.Commit(take);
                    fill.Record(i, offer, take);
                }
                // The GAME is the authority on what is left, not the session's running subtraction.
                fill.EndRound(Idle(c, type));
                if (firstRound == null)
                    firstRound = session;
            }
            ConstraintLayer.RecordRateSkips(plan, firstRound);

            // The remainder → the surplus sink, funded LAST and through its own Allocate() so its
            // stair-snap capacity and WandoosBP.RecordShare stay live. Anything BEYOND the sink's
            // per-tick absorptive capacity (capAmountEnergy/-Magic — CapacityPass.Table) is left
            // idle ON PURPOSE: the wish share pass (CustomAllocation "Wishes (share of remaining
            // idle)" → WishManager.Allocate, gated by the Wish % sliders) runs after this swap and
            // is the pool's other legitimate destination; drinking it into Wandoos would starve
            // wishes through their AND-gate.
            long remainder = Idle(c, type);
            if (plan.SinkSeated)
            {
                // Only a SEATED sink is offered anything; a refused one must not read as if it were.
                offers[plan.SinkIndex] = remainder;
                var sink = membership[plan.SinkIndex];
                sink.OfferBudget(remainder);
                try { sink.Allocate(); }
                catch (Exception e) { Main.LogDebug($"Constraint sink {sink.Label}: {e.Message}"); }
                long sinkTake = remainder - Idle(c, type);
                if (sinkTake < 0) sinkTake = 0;
                takes[plan.SinkIndex] = sinkTake;
                plan.Lanes[plan.SinkIndex].Allocation = sinkTake;
                plan.Lanes[plan.SinkIndex].Offered = remainder;   // the sink is offered the whole remainder
                plan.SinkAllocation = sinkTake;
                long residue = remainder - sinkTake;
                if (residue > 0)
                {
                    plan.Unallocated = residue;
                    plan.UnallocatedReason = "beyond the sink's per-tick absorptive capacity — " +
                        "offered to the wish share pass (the Wish % sliders decide its take); reclaimed next swap";
                }
            }
            else
            {
                // Sink refused (budget-exhausted Wandoos at 100LC, trolled off) or absent from this
                // lane set: the remainder is SURFACED, never silently dropped.
                ConstraintLayer.RecordExecutedRemainder(plan, remainder);
            }

            Surface(type, plan);
            Telemetry(type, plan, offers);
            if (Main.Settings != null && Main.Settings.ConstraintParityLog)
                Parity(c, type, plan, membership, takes, pool);
            // THE OBJECTIVE LAYER'S COMPARISON RUN (amendment 34 §7.1). Placed here, after the fill
            // has committed, for the same reason Parity is: it is a report on a tick that is already
            // over. It is handed the MEMBERSHIP and nothing else — no plan, no takes, no pool — so
            // there is no argument through which it could move a unit of the pool.
            if (Main.Settings != null && Main.Settings.ObjectiveCompareLog)
                ObjectiveCompare(c, type, membership);

            if (type == ResourceType.Energy) LastEnergyPlan = plan; else LastMagicPlan = plan;

            RefreshMenus(c, type);
            return false;   // same contract as the old path: re-run every allocation tick
        }

        // ---- per-lane Pass 1 verdicts + rate capacities, from live state --------------------------

        private static ConstraintLayer.LaneSpec BuildSpec(ResourceBreakpoint bp, Character c, bool evilTrack)
        {
            var spec = new ConstraintLayer.LaneSpec
            {
                Name = bp.GetType().Name,
                Label = bp.Label,
                Capacity = ConstraintLayer.SelfLimiting,
                // The FAIL-OPEN default only. The real answer is Pass 3's, computed at the bottom of
                // this method once the lane's Pass 1 verdict and rate/sink shape are known.
                WantsMore = true,
                SurplusSink = bp is WandoosBP,
            };

            var external = ConstraintLayer.WithFocus(new FeasibilityPass.ExternalConstraints(),
                DeclaredFocus, bp.Label, spec.SurplusSink);

            try
            {
                // BestAug BEFORE AugmentBP — it derives from it. Its pair choice happens inside
                // Allocate() (the eager-eval hazard), so the per-half boss/gold gates cannot run
                // before the pick without widening AugmentMath; it seats on the external gate and
                // keeps its own internal gold guard. Reported as a composition note.
                if (bp is BestAug)
                {
                    spec.Feasibility = FeasibilityPass.PlainLane(external);
                }
                else if (bp is AugmentBP)
                {
                    var pair = c.augmentsController.augments[bp.LaneIndex / 2];
                    bool augHalf = bp.LaneIndex % 2 == 0;
                    spec.Feasibility = FeasibilityPass.AugmentHalf(new FeasibilityPass.AugmentHalfState
                    {
                        BossId = c.bossID,
                        BossRequired = augHalf ? pair.augBossRequired : pair.upgradeBossRequired,
                        RealGold = c.realGold,
                        Cost = augHalf ? pair.getAugCost() : pair.getUpgradeCost(),
                        BarProgress = augHalf ? pair.AugProgress() : pair.UpgradeProgress(),
                        External = external,
                    });
                }
                else if (bp is TimeMachineBP)
                {
                    bool energyHalf = bp.LaneType == ResourceType.Energy;
                    spec.Feasibility = FeasibilityPass.TimeMachineHalf(new FeasibilityPass.TimeMachineHalfState
                    {
                        // Live bossID every tick, never latched (spec §4.5) — Rebirth zeroes it
                        // (Rebirth.cs:691), re-locking both halves; the requirement is the :81
                        // literal, one gate shared by both halves.
                        BossId = c.bossID,
                        BossRequired = FeasibilityPass.TimeMachineBossRequired,
                        RealGold = c.realGold,
                        Cost = energyHalf
                            ? c.timeMachineController.machineSpeedGoldCost()
                            : c.timeMachineController.machineGoldMultiCost(),
                        BarProgress = energyHalf ? c.machine.speedProgress : c.machine.goldMultiProgress,
                        External = external,
                    });
                }
                else if (bp is RitualBP)
                {
                    spec.Feasibility = FeasibilityPass.Ritual(new FeasibilityPass.RitualState
                    {
                        BossId = c.bossID,
                        BossRequired = c.bloodMagic.ritual[bp.LaneIndex].boss,
                        RealGold = c.realGold,
                        Cost = c.bloodMagicController.bloodMagics[bp.LaneIndex].baseCost * c.totalDiscount(),
                        BarProgress = c.bloodMagic.ritual[bp.LaneIndex].progress,
                        External = external,
                    });
                }
                else if (bp is AdvancedTrainingBP)
                {
                    spec.Feasibility = FeasibilityPass.AdvancedTraining(new FeasibilityPass.AdvancedTrainingState
                    {
                        AttackTrainingSlot4 = c.training.attackTraining[4],
                        DefenseTrainingSlot4 = c.training.defenseTraining[4],
                        Wish190Level = c.wishes.wishes[190].level,
                        External = external,
                    });
                }
                else if (bp is NGUBP)
                {
                    // F2 (39 §F2). The refusal below is correct and stays exactly as it was; what was
                    // wrong is the SENTENCE it carried. NGU.disabled is set by the Troll challenge
                    // ([DECOMP] TrollChallengeController.cs:643) AND by the NGU Challenge
                    // ([DECOMP] Rebirth.cs:523) and the flag cannot tell them apart, so TrollGate's
                    // "trolled off … clears at rebirth" was emitted for every NGU lane for the whole
                    // NGU Challenge, where neither clause is true.
                    //
                    // Resolved from CHALLENGE state, per-lane: ExternalConstraints is a struct, so this
                    // copy leaves the shared `external` (built once at :231 for every lane) untouched —
                    // the NGU Challenge locks NGU lanes only. ExternalGate runs BEFORE TrollGate inside
                    // NguLane, so on the NGU-Challenge path the Troll wording is unreachable; during the
                    // Troll the lock is null and TrollGate keeps its own, correct, sentence.
                    // Live read, never latched (spec §4.5).
                    var nguExternal = external;
                    if (nguExternal.ChallengeLock == null)
                        nguExternal.ChallengeLock = ChallengeLocks.NguLane(c.challenges.nguChallenge.inChallenge);
                    spec.Feasibility = FeasibilityPass.NguLane(c.NGU.disabled, nguExternal);
                    if (evilTrack)
                    {
                        // Amendment 18 §1: every Evil NGU is a rate row, both pools, all ids —
                        // capacity from the GAME's cap helper (CapacityPass.Table's Game row),
                        // never re-derived.
                        spec.RateLane = true;
                        spec.Capacity = bp.LaneType == ResourceType.Energy
                            ? c.NGUController.energyNGUCapAmount(bp.LaneIndex)
                            : c.NGUController.magicNGUCapAmount(bp.LaneIndex);
                    }
                }
                else if (bp is WandoosBP)
                {
                    spec.Feasibility = FeasibilityPass.WandoosLane(c.wandoos98.disabled, external);
                }
                else
                {
                    // BasicTrainingBP, BR (its per-ritual gold/boss gates stay inside CastRituals —
                    // the pass API is per-ritual and the lane is an aggregate; not widened), HackBP.
                    spec.Feasibility = FeasibilityPass.PlainLane(external);
                }
            }
            catch (Exception e)
            {
                // Fail CLOSED, with a reason — an unreadable lane must not seat on garbage.
                spec.Feasibility = FeasibilityPass.Verdict.Refuse(
                    "state read failed: " + e.Message + " — refusing this tick, re-evaluated next");
            }

            // PASS 3 — targets (spec §7). Computed LAST (spec §2's ordering) because it needs the
            // lane's Pass 1 verdict and its rate/sink shape, both settled just above.
            string wantReason;
            spec.WantsMore = WantsMore(bp, c, spec, out wantReason);
            spec.WantReason = wantReason;

            return spec;
        }

        // ---- Pass 3's INPUT: the objective layer's rows, rebuilt every swap ------------------------

        // THE PRODUCER CALL. Three live reads, the same three ObjectiveCompare makes and for the same
        // reasons, and each of them can hold the table rather than guess:
        //
        //  · THE CHAPTER, with its `Known` flag as the discriminator. A bare 0 is ChapterAny, and
        //    ObjectiveTable.ChapterMatches treats a ChapterAny QUERY as "match EVERY row in the
        //    table" (:1007-1010) — so a StageDetector blip collapsed to 0 would supply all eight
        //    chapters' rows at once. ObjectiveTargets.Produce holds on !Known for exactly that
        //    reason; StageDetector.Detect() is read FRESH here rather than through
        //    ChallengeOverlay's 60s cache, so a blip costs one tick instead of a minute.
        //  · THE TWO TRACKS, in the same split LaneStateFor makes: the NGUs follow the live selector
        //    settings.nguLevelTrack, everything else the run's difficulty (23 §2.3).
        //
        // ⚠ A FAILED TRACK READ HOLDS THE WHOLE TABLE, it does not pass Unspecified through.
        // TrackMatches would answer Unspecified by admitting only the TrackNeutral and AllTracks
        // rows — which Rule denies anyway — so producing on a failed read yields an EMPTY table that
        // is indistinguishable from "the guide says nothing here". A hold says which it was.
        //
        // EVERY FAILURE PATH LEAVES THE TABLE NULL, which is spec §10's standalone mode and the
        // state this file shipped in for its whole life: Passes 0-2 run, Pass 3 falls back to the
        // game's own target fields, every want stays open. Fail-open is the required direction — a
        // lane stopped by a bad read is silently defunded for as long as the read keeps failing.
        private static void RefreshTargetTable(Character c)
        {
            TargetTable = null;
            try
            {
                var stage = StageDetector.Detect();

                var nguTrack = TrackOf(c.settings.nguLevelTrack);
                var runTrack = TrackOf(c.settings.rebirthDifficulty);

                var table = ObjectiveTargets.Produce(new ObjectiveTargets.Query
                {
                    Chapter = stage.Chapter,
                    ChapterKnown = stage.Known,
                    NguTrack = nguTrack,
                    RunTrack = runTrack,
                });

                TargetTable = table.Held ? null : table.Rows;
            }
            catch (Exception e)
            {
                TargetTable = null;
                Main.LogDebug($"Constraint target table: {e.Message} — standalone mode this tick");
            }
        }

        // ---- Pass 3: does this lane still want more? (spec §7) ------------------------------------

        // This used to be the literal `true` on the LaneSpec initialiser. TargetPass — the pass, its
        // seat guard, its silence ledger — and ConstraintLayer.WantFromAnswer all existed, compiled
        // and fully tested, and NOTHING CALLED THEM (37 §S5 A1). This is the call.
        //
        // ⚠ THE TABLE IS NOW SUPPLIED, so this CAN return false — for exactly one lane. RefreshTargetTable
        // above fills TargetTable from ObjectiveTargets, whose entire writable inventory over the whole
        // query space is one row: AT slot 2 at ObjectiveTable.AtBlockHardCapLevel on the Evil track,
        // chapter 5 — and since [OPERATOR]'s floor ruling that row's value is the LEAST it can stop
        // at, raised by the operator's own levelTarget[2] when theirs is higher (LaneStateFor
        // below). WriteTarget is the only disposition Evaluate can attach a Satisfaction to, and
        // WantFromAnswer holds Silent, Precondition, OperatorDecision and Refused all open — so every
        // other lane, including the other four AT slots at the same level, is answered exactly as it
        // was before the wire. With no table (held chapter, failed read) nothing changes at all.
        private static bool WantsMore(ResourceBreakpoint bp, Character c,
            ConstraintLayer.LaneSpec spec, out string reason)
        {
            reason = null;

            // Three lane shapes never reach Pass 3, each for its own reason:
            //  · the SURPLUS SINK — Wandoos is correctly unterminated, and Evaluate refuses it at
            //    the lane level in any case (spec §8; 23 §2.6: DO NOT SYNTHESISE a target);
            //  · RATE lanes — consumed entirely by Pass 2, "blank the bar" IS "funded to capacity"
            //    (amendment 18 §1.2). ⚠ On this path that means the EVIL-track NGU lanes only;
            //    amendment 21 §1 rules every NGU lane rate on every track and TM a rate lane too
            //    (spec §7), but neither is derived that way here yet and CHANGING IT WOULD CHANGE
            //    ALLOCATION, which this commit must not. Both still reach Pass 3 and both answer
            //    Silent — the ledger's own reason for them is "no level exists", so the outcome
            //    agrees with the decision even though the mechanism does not yet express it;
            //  · a lane PASS 1 ALREADY REFUSED — Evaluate ASSERTS the seat rather than re-deriving
            //    it (spec §2), so handing it an unseated lane is a caller error by its own contract.
            //    Its verdict is settled; what it would have wanted is not a live question.
            if (spec.SurplusSink || spec.RateLane || !spec.Feasibility.Seated)
                return true;

            try
            {
                TargetPass.LaneState lane;
                if (!LaneStateFor(bp, c, out lane))
                    return true;   // no schema key — see TargetPass.SystemForLane for the five families

                var answer = TargetPass.Evaluate(lane,
                    TargetPass.RowsFor(TargetTable, lane.System, lane.Index), spec.Feasibility);
                return ConstraintLayer.WantFromAnswer(answer, out reason);
            }
            catch (Exception e)
            {
                // ⚠ PASS 3 FAILS OPEN — the OPPOSITE of Pass 1 above, deliberately. A failed
                // feasibility read must refuse the lane, because seating on garbage spends the pool
                // somewhere the game will reject. A failed target read must NOT stop one: stopping
                // is the irreversible direction here (a lane eliminated at Pass 3 is silently
                // defunded for as long as the read keeps failing), and TargetPass's own fail-closed
                // Refused already reads as "keep funding" through WantFromAnswer.
                Main.LogDebug($"Constraint target {bp.Label}: {e.Message}");
                reason = null;
                return true;
            }
        }

        // The LIVE half of TargetPass's wiring map: the lane's schema key, its level on the track a
        // row for it would be keyed to, and — for the AT lanes only — the operator's own live target
        // in the game's field. False = the schema cannot name this lane.
        private static bool LaneStateFor(ResourceBreakpoint bp, Character c,
            out TargetPass.LaneState lane)
        {
            lane = default(TargetPass.LaneState);
            bool energyPool = bp.LaneType == ResourceType.Energy;

            var system = TargetPass.SystemForLane(bp.GetType().Name, energyPool);
            if (system == null)
                return false;

            lane.System = system;
            lane.Index = bp.LaneIndex;

            if (bp is NGUBP)
            {
                // The live selector is settings.nguLevelTrack ([DECOMP] PlayerSettings.cs:188), NOT
                // rebirthDifficulty: a player runs Normal NGUs inside an Evil rebirth for most of
                // the day (23 §2.3). Same read NGUBP.TargetMet() makes, and the same three-way
                // level split ([DECOMP] NGU.cs:8-16).
                lane.ActiveTrack = TrackOf(c.settings.nguLevelTrack);
                var ngus = energyPool ? c.NGU.skills : c.NGU.magicSkills;
                var skill = ngus[bp.LaneIndex];
                lane.LevelOnTrack =
                    lane.ActiveTrack == TargetPass.Track.Normal ? skill.level :
                    lane.ActiveTrack == TargetPass.Track.Evil ? skill.evilLevel :
                    skill.sadisticLevel;
                return true;
            }

            // ⚠ NO OTHER SYSTEM HAS A PER-TRACK LEVEL. augmentTarget/augLevel, levelTarget[]/level[],
            // speedTarget and multiTarget are single fields; only the NGUs carry the three-way split.
            // 23 still keys its rows by track for these systems (the AT silence entry is Evil-only),
            // so a lane must declare one, and the only track signal that exists away from the NGU
            // selector is the RUN's difficulty — which TargetPass's header calls the bound on which
            // tracks are reachable.
            //
            // ⚠ INERT TODAY: with no table every answer is a silence whatever the track says, and
            // the ONE thing this choice can change is which ledger reason gets surfaced. It is the
            // single wiring decision in this commit an operator should ratify when rows arrive.
            lane.ActiveTrack = TrackOf(c.settings.rebirthDifficulty);

            if (bp is TimeMachineBP)
                lane.LevelOnTrack = energyPool ? c.machine.levelSpeed : c.machine.levelGoldMulti;
            else if (bp is AdvancedTrainingBP)
            {
                lane.LevelOnTrack = c.advancedTraining.level[bp.LaneIndex];

                // THE OPERATOR'S OWN NUMBER, READ LIVE — the other half of [OPERATOR]'s 2026-08-07
                // ruling, "the operator's higher target should win over the ruled cap but it should
                // never be capped below the 100,000 level". TargetPass floors the ruled stop with it
                // (LaneState.OperatorTarget), so a hand-typed 250,000 keeps the slot funded past the
                // table's 100,000 instead of Pass 3 stopping it there.
                //
                // ⚠ THIS IS THE SAME FIELD, AND THE SAME SLOT, THAT AdvancedTrainingBP.TargetMet()
                // AND LevelPlanner BOTH USE — advancedTraining.levelTarget[i], one array, no
                // per-track split (see the note above). Reading anything else would give Pass 3 a
                // different operator than the membership filter has.
                //
                // ⚠ DELIBERATELY UNGUARDED, exactly like the level read on the line above. A null or
                // short levelTarget throws into WantsMore's catch, which FAILS OPEN and keeps the
                // lane funded — the required direction (stopping is the irreversible one). Defaulting
                // to 0 instead would read as "no operator preference" and could stop a slot at
                // 100,000 that the operator had asked to run to 250,000, which is fail-CLOSED on the
                // one axis this ruling forbids it on.
                lane.OperatorTarget = c.advancedTraining.levelTarget[bp.LaneIndex];
            }
            else if (bp is AugmentBP)
            {
                var pair = c.augments.augs[bp.LaneIndex / 2];
                lane.LevelOnTrack = bp.LaneIndex % 2 == 0 ? pair.augLevel : pair.upgradeLevel;
            }
            // WandoosBP falls through with level 0 and is never read: the sink is filtered out by
            // the caller, and Evaluate refuses the wandoos slug at the lane level regardless.

            return true;
        }

        private static TargetPass.Track TrackOf(difficulty d) =>
            d == difficulty.normal ? TargetPass.Track.Normal :
            d == difficulty.evil ? TargetPass.Track.Evil :
            TargetPass.Track.Sadistic;

        // ---- surfacing (spec §3.4, §10): log on state CHANGE, hold the full plan for the UI -------

        private static void Surface(ResourceType type, ConstraintLayer.Plan plan)
        {
            string last;
            _lastBudgetMsg.TryGetValue(type, out last);
            if (plan.BudgetMessage != last)
            {
                _lastBudgetMsg[type] = plan.BudgetMessage;
                if (plan.BudgetMessage != null)
                    Main.Log($"Constraint layer [{type}]: {plan.BudgetMessage}");
                else if (last != null)
                    Main.Log($"Constraint layer [{type}]: budget pressure cleared — all lanes eligible again");
            }

            // Only the sink-refused/absent states are logged; the routine beyond-sink-capacity
            // residue is normal operation (it is offered to the wish share pass) and stays plan-only.
            string sinkIssue = plan.SinkSeated ? null : plan.UnallocatedReason;
            string lastIssue;
            _lastSinkIssue.TryGetValue(type, out lastIssue);
            if (sinkIssue != lastIssue)
            {
                _lastSinkIssue[type] = sinkIssue;
                if (sinkIssue != null)
                    Main.Log($"Constraint layer [{type}]: {NumberFormatter.Abbrev(plan.Unallocated)} unallocated — {sinkIssue}");
            }

            // The rate-skip line (amendment 19 §4): a seated rate lane at zero is invisible to
            // parity (equal rows drop), to the sink surface (the sink still seats) and to debug.log
            // (nothing throws) — the ONLY channel that names it is this one. State-change with the
            // parity throttle's 30s floor / 600s refresh; the per-lane reasons stay in the plan.
            var skipSig = ConstraintLayer.RateSkipSignature(plan);
            string lastSkipSig;
            _rateSkipSig.TryGetValue(type, out lastSkipSig);
            DateTime lastSkipAt;
            if (!_rateSkipAt.TryGetValue(type, out lastSkipAt))
                lastSkipAt = DateTime.MinValue;

            var emit = ConstraintLayer.RateSkipSurfaceDecision(skipSig, lastSkipSig,
                (DateTime.UtcNow - lastSkipAt).TotalSeconds);
            if (emit == ConstraintLayer.RateSkipEmit.Skips)
            {
                _rateSkipSig[type] = skipSig;
                _rateSkipAt[type] = DateTime.UtcNow;
                Main.Log($"Constraint layer [{type}]: {plan.RateLanesSkipped} rate lane(s) skipped, " +
                    $"pool {NumberFormatter.Abbrev(plan.RateSkipPool)} < cheapest capacity " +
                    $"{NumberFormatter.Abbrev(plan.RateSkipCheapest)} (amendment 19 §4)");
            }
            else if (emit == ConstraintLayer.RateSkipEmit.Cleared)
            {
                _rateSkipSig[type] = "";
                _rateSkipAt[type] = DateTime.UtcNow;
                Main.Log($"Constraint layer [{type}]: rate-lane skips cleared — the pool reaches capacity again");
            }
        }

        // ---- the [AllocDbg] twin (AllocTelemetry) --------------------------------------------------

        // The new path's per-lane dump, on the DEBUG channel — this is diagnostic volume, not
        // operator narration, and it is deliberately NOT gated on ConstraintParityLog: parity is an
        // opt-in comparison against a model being retired, whereas this is the only place a
        // CORRECTLY funded lane says anything at all. AllocDiagnostic's counterpart on the old path
        // is likewise unconditional and likewise debug-only.
        private static void Telemetry(ResourceType type, ConstraintLayer.Plan plan, long[] offers)
        {
            try
            {
                var sig = AllocTelemetry.Signature(plan, offers);
                string lastSig;
                _telemetrySig.TryGetValue(type, out lastSig);
                DateTime lastAt;
                if (!_telemetryAt.TryGetValue(type, out lastAt))
                    lastAt = DateTime.MinValue;

                if (!AllocTelemetry.ShouldEmit(sig, lastSig, (DateTime.UtcNow - lastAt).TotalSeconds))
                    return;

                _telemetrySig[type] = sig;
                _telemetryAt[type] = DateTime.UtcNow;
                var block = AllocTelemetry.Render(type.ToString(), plan, offers);
                if (block != null)
                    Main.LogDebug(block);
            }
            catch (Exception e) { Main.LogDebug($"Constraint telemetry [{type}]: {e.Message}"); }
        }

        // ---- parity logging (both allocations computed, the new one used) -------------------------

        private static void Parity(Character c, ResourceType type, ConstraintLayer.Plan plan,
            List<ResourceBreakpoint> membership, long[] takes, long pool)
        {
            try
            {
                var legacy = new List<LegacyShareModel.LaneInput>(membership.Count);
                for (int i = 0; i < membership.Count; i++)
                {
                    var bp = membership[i];
                    legacy.Add(new LegacyShareModel.LaneInput
                    {
                        Label = bp.Label,
                        IsCap = bp.IsCap,
                        HasPercent = bp.CapPercentView.HasValue,
                        Percent = bp.CapPercentView ?? 0,
                        // Need approximation (stated in LegacyShareModel's contract): the new path's
                        // observed take for seated lanes — exact when the lane self-limited; an
                        // upper-bound stand-in for lanes the new path refused, where the old model
                        // would have offered a share and the true need is unknowable without
                        // allocating twice.
                        Need = plan.Lanes[i].Seated ? takes[i] : long.MaxValue,
                    });
                }

                var oldShares = LegacyShareModel.Simulate(pool, Cur(c, type), legacy);
                var diffs = ConstraintParity.Compare(plan, takes, oldShares);
                var sig = ConstraintParity.Signature(diffs);

                string lastSig;
                _paritySig.TryGetValue(type, out lastSig);
                DateTime lastAt;
                if (!_parityAt.TryGetValue(type, out lastAt))
                    lastAt = DateTime.MinValue;

                if (ConstraintParity.ShouldEmit(sig, lastSig, (DateTime.UtcNow - lastAt).TotalSeconds))
                {
                    _paritySig[type] = sig;
                    _parityAt[type] = DateTime.UtcNow;
                    Main.Log(ConstraintParity.Format(type.ToString(), diffs));
                }
            }
            catch (Exception e) { Main.LogDebug($"Constraint parity [{type}]: {e.Message}"); }
        }

        // ---- the objective layer's comparison run (amendment 34 §7.1) ------------------------------

        // THE ADDITIVE PATH [OPERATOR]: the objective layer's membership is computed against the same
        // tick's state as the profile's, the two are compared, and THE PROFILE IS STILL THE ONE THAT
        // ALLOCATED. Nothing computed here is applied — the report is logged and dropped.
        //
        // ⚠ THE CHAPTER IS READ FRESH, AND ITS `Known` FLAG IS THE DISCRIMINATOR. ChallengeOverlay's
        // Chapter() is private, caches for 60s, and — the reason not to widen it even if it were
        // reachable — collapses StageDetector's Unknown to a bare 0. A bare 0 read as a real chapter
        // is a FALSE DIVERGENCE REPORT rather than a missing one: ObjectiveTable.ChapterMatches
        // (:905-909) treats a query chapter of 0 as ChapterAny and matches EVERY row in the table.
        // Detect() is a handful of guarded field reads (StageDetector.cs:53-96) and costs nothing at
        // this cadence, and reading it fresh means a detector blip costs ONE TICK instead of the 60s
        // the cached path costs — which is advisors/01:406's finding, on the channel where it would
        // otherwise be loudest.
        private static void ObjectiveCompare(Character c, ResourceType type,
            List<ResourceBreakpoint> membership)
        {
            try
            {
                var stage = StageDetector.Detect();

                // The SAME track split LaneStateFor makes (:427-453): the NGUs follow the live
                // selector settings.nguLevelTrack, everything else the run's difficulty. A failed
                // read stays Unspecified and ObjectiveParity holds the comparison rather than
                // reading every slot as a silence.
                var nguTrack = TargetPass.Track.Unspecified;
                try { nguTrack = TrackOf(c.settings.nguLevelTrack); } catch { }
                var runTrack = TargetPass.Track.Unspecified;
                try { runTrack = TrackOf(c.settings.rebirthDifficulty); } catch { }

                var lanes = new List<ObjectiveParity.ProfileLane>(membership.Count);
                foreach (var bp in membership)
                    lanes.Add(new ObjectiveParity.ProfileLane
                    {
                        Label = bp.Label,
                        ClassName = bp.GetType().Name,
                        Index = bp.LaneIndex,
                        EnergyPool = bp.LaneType == ResourceType.Energy,
                    });

                var report = ObjectiveParity.Compare(stage.Chapter, stage.Known, nguTrack, runTrack,
                    type == ResourceType.Energy, lanes);

                // The held state is LATCHED, not discarded (spec §3.4): a comparison that silently
                // never ran is indistinguishable from one that always agrees, and the difference is
                // the whole value of the run.
                string lastHeld;
                _objectiveHeld.TryGetValue(type, out lastHeld);
                if (report.HeldReason != lastHeld)
                {
                    _objectiveHeld[type] = report.HeldReason;
                    Main.LogDebug(report.Held
                        ? $"Objective comparison [{type}]: HELD — {report.HeldReason}"
                        : $"Objective comparison [{type}]: running again (chapter {report.Chapter})");
                }

                var sig = ObjectiveParity.Signature(report);

                string lastSig;
                _objectiveSig.TryGetValue(type, out lastSig);
                DateTime lastAt;
                if (!_objectiveAt.TryGetValue(type, out lastAt))
                    lastAt = DateTime.MinValue;

                if (ObjectiveParity.ShouldEmit(sig, lastSig, (DateTime.UtcNow - lastAt).TotalSeconds))
                {
                    _objectiveSig[type] = sig;
                    _objectiveAt[type] = DateTime.UtcNow;
                    var block = ObjectiveParity.Format(type.ToString(), report);
                    if (block != null)
                        Main.Log(block);
                }
            }
            catch (Exception e) { Main.LogDebug($"Objective comparison [{type}]: {e.Message}"); }
        }

        // ---- pool plumbing ------------------------------------------------------------------------

        private static long Idle(Character c, ResourceType type) =>
            type == ResourceType.Energy ? c.idleEnergy : c.magic.idleMagic;

        private static long Cur(Character c, ResourceType type) =>
            type == ResourceType.Energy ? c.curEnergy : c.magic.curMagic;

        // The 12 basic-training slots as BasicTrainingBP indexes them: 0-5 attack, 6-11 defense. Short
        // arrays are tolerated rather than assumed — the decomp loops on `.Length` and so does this.
        private static long[] SnapshotBasicTraining(Character c)
        {
            var snap = new long[12];
            try
            {
                var atk = c.training.attackEnergy;
                var def = c.training.defenseEnergy;
                for (int i = 0; i < 6; i++)
                {
                    if (atk != null && i < atk.Length) snap[i] = atk[i];
                    if (def != null && i < def.Length) snap[i + 6] = def[i];
                }
            }
            catch (Exception e) { Main.LogDebug($"BT snapshot: {e.Message}"); }
            return snap;
        }

        // Put back exactly what the controller-wide reclaim took from slots the fill will not reseat.
        // The inverse of [DECOMP] AllOffenseTraining.removeAllEnergy, unit for unit: that method does
        // `attackEnergy[i] -= num; idleEnergy += num`, so this does the same in reverse and the pool
        // read on the next line sees the correct remainder.
        private static void RestoreUnseatedBasicTraining(Character c, List<ResourceBreakpoint> membership,
                                                          long[] before)
        {
            try
            {
                var seated = new bool[12];
                foreach (var bp in membership)
                {
                    if (!(bp is BasicTrainingBP)) continue;
                    int idx = bp.LaneIndex;
                    if (idx >= 0 && idx < 12) seated[idx] = true;
                }

                var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);
                var atk = c.training.attackEnergy;
                var def = c.training.defenseEnergy;
                long total = 0;
                for (int i = 0; i < 6; i++)
                {
                    if (restore[i] > 0 && atk != null && i < atk.Length)
                    {
                        atk[i] += restore[i];
                        total += restore[i];
                    }
                    if (restore[i + 6] > 0 && def != null && i < def.Length)
                    {
                        def[i] += restore[i + 6];
                        total += restore[i + 6];
                    }
                }
                if (total > 0) c.idleEnergy -= total;
            }
            catch (Exception e) { Main.LogDebug($"BT reclaim restore: {e.Message}"); }
        }

        private static void Reclaim(Character c, ResourceType type, bool hasBasicTraining)
        {
            if (type == ResourceType.Energy)
            {
                c.wandoos98Controller.removeAllEnergy();
                c.augmentsController.removeAllEnergy();
                c.timeMachineController.removeAllEnergy();
                c.advancedTrainingController.removeAllEnergy();
                c.NGUController.removeAllEnergy();
                if (hasBasicTraining)
                {
                    c.allOffenseController.removeAllEnergy();
                    c.allDefenseController.removeAllEnergy();
                }
            }
            else
            {
                c.wandoos98Controller.removeAllMagic();
                c.timeMachineController.removeAllMagic();
                c.bloodMagicController.removeAllMagic();
                c.NGUController.removeAllMagic();
            }
        }

        private static void RefreshMenus(Character c, ResourceType type)
        {
            if (type == ResourceType.Energy)
            {
                c.NGUController.refreshMenu();
                c.wandoos98Controller.refreshMenu();
                c.advancedTrainingController.refresh();
                c.timeMachineController.updateMenu();
                c.allOffenseController.refresh();
                c.allDefenseController.refresh();
                c.augmentsController.updateMenu();
            }
            else
            {
                c.timeMachineController.updateMenu();
                c.bloodMagicController.updateMenu();
                c.NGUController.refreshMenu();
                c.wandoos98Controller.refreshMenu();
            }
        }
    }
}
