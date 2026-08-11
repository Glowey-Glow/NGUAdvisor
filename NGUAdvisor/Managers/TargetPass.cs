using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // PASS 3 of the constraint layer: TARGETS (audit/decisions/constraint-layer-spec.md §7; 23 §0.3,
    // §0.4, §7; decision record amendment 18). Unity-free — plain-old-data in, an answer out.
    //
    // WIRED (37 §S5 A1): ConstraintLayerBridge.WantsMore builds a LaneState per membership lane,
    // calls Evaluate here and feeds ConstraintLayer.WantFromAnswer — the field that used to be the
    // literal `true`. ⚠ A TARGET TABLE IS NOW SUPPLIED: ConstraintLayerBridge.RefreshTargetTable
    // fills TargetTable from ObjectiveTargets.Produce on every swap, so this pass CAN now return a
    // satisfaction — for exactly one lane. Measured over the producer's whole query space
    // (ObjectiveTargetsTests), the only row that routes to WriteTarget is AT slot 2 at
    // ObjectiveTable.AtBlockHardCapLevel on the Evil track; every other produced row is a
    // Precondition, and every slot the table is silent on still answers Silent with the ledger's own
    // words. With NO table — a held chapter or a failed live read — nothing changes at all and the
    // allocation is bit-for-bit what the hardcoded `true` produced, which is spec §10's STANDALONE
    // contract, not a stub. Pass3WiringTests proves that equality; ObjectiveTargetsTests measures
    // the one departure from it.
    //
    // ⚠ AND THAT ONE STOP IS A FLOOR, NOT A CEILING (the ruling 36ea654 raised and did not decide).
    // [OPERATOR] 2026-08-07: "the operator's higher target should win over the ruled cap but it
    // should never be capped below the 100,000 level." LaneState.OperatorTarget carries the live
    // field in as DATA and Evaluate floors the row's value with it through the LIVE WRITER'S OWN
    // decision function, LaneTargets.AdvancedTrainingPurposeFloor — one rule, two consumers, so the
    // number Pass 3 stops at and the number LevelPlanner writes cannot diverge.
    //
    // THE QUESTION THIS PASS ANSWERS is "does this lane still want more?" — the TargetMet() surface.
    // Six of seven energy consumers terminate here, and for most the GAME supplies both the mechanism
    // and the signal: five systems cascade a satisfied sub-lane's ENTIRE allocation to the next index
    // whose target is unmet, wrapping ([DECOMP] AllNGUController.cs:1245-1300 — autoAdvance moves
    // skills[id].energy whole to the first num with !reachedTarget(num)); Basic Training sheds
    // overflow only. Intra-system ranking is a mechanic the advisor OVERRIDES, not a gap it must
    // fill — prefer letting the game's cascade run.
    //
    // Already wired elsewhere and NOT re-implemented here: BestAug (hitAugmentTarget/
    // hitUpgradeTarget) and BasicTrainingBP (attackEnergy >= attackCaps) — see LaneTargets. Faithful
    // `false` with NO game terminator: RitualBP, BR, WandoosBP — not "fixed" here or anywhere.
    //
    // ORDERING (spec §2): this pass runs LAST. A lane eliminated by Pass 0, 1 or 2 never reaches it,
    // so nothing here re-checks budget, feasibility or capacity — Evaluate ASSERTS the contract
    // instead: a lane arriving without a seat is a caller error, refused with a reason.
    //
    // ⚠ IsValid() EVALUATES ALL THREE TERMS EAGERLY — TargetMet() runs even when Unlocked() is false
    // and BEFORE Allocate() (the BestAug._useUpgrades hazard: a field assigned inside Allocate was
    // read unset). Everything in this file is a pure static over the inputs of the call — there is
    // no field any evaluation order could catch unassigned, and default inputs produce a fail-closed
    // refusal, never a throw. TargetPassTests pins both.
    public static class TargetPass
    {
        // ---- the vocabulary (23 §0.1, §0.3, §0.4) ------------------------------------------------

        // KIND — only ONE of the four reaches this pass (23 §0.3). Unspecified = 0 so that a
        // default(TargetRow) routes to a refusal, never to Level.
        public enum RowKind
        {
            Unspecified = 0,
            // A stopping level the cascade can consume. THIS PASS'S INPUT — the only one.
            Level,
            // An allocation-SUFFICIENCY condition ("BB the first 5"). Pass 2 capacity content.
            Rate,
            // A phase / wall-clock split. Auto-profile content.
            Time,
            // A target SELECTOR. Computed upstream, then re-emitted as kind=Level — a raw predicate
            // row reaching this pass is a caller error exactly like Rate and Time.
            Predicate
        }

        // The game stores THREE targets per NGU — skills[id].target / evilTarget / sadisticTarget
        // ([DECOMP] NGU.cs:22-26), compared to level / evilLevel / sadisticLevel (:8-16). A row
        // without a track is unusable (23 §0.1) unless it is structurally TrackNeutral (TM: one
        // speedTarget, no per-track split — 23 §2.5 tags its rows "track-neutral").
        //
        // The live selector for NGU lanes is settings.nguLevelTrack ([DECOMP] PlayerSettings.cs:188;
        // reachedTarget switches on it at AllNGUController.cs:1304), NOT rebirthDifficulty directly —
        // a player runs Normal NGUs inside an Evil rebirth for most of the day (23 §2.3's Evil-hour
        // rule). rebirthDifficulty bounds which tracks are reachable; the caller reads the live
        // track and passes it here.
        public enum Track
        {
            Unspecified = 0,
            Normal,
            Evil,
            Sadistic
        }

        // TERMINALITY — get this wrong and the cascade abandons lanes permanently (23 §0.4).
        // Unspecified = 0: an unfilled terminality is treated exactly like Ambiguous — surfaced,
        // never guessed, never written.
        //
        // ⚠ SOFTCAP IS NOT ONE CONCEPT (23 §0.5). Ch.3 says "keep going" past Adventure a's softcap
        // and "don't invest further yet" at Respawn 401, and BOTH are correct: Respawn's post-400
        // branch saturates (level/(level*5+200000) + 0.2, bounded — [DECOMP] AllNGUController.cs:
        // 449-458) while Adventure a's post-1000 branch is unbounded sqrt (Mathf.Sqrt(level)*31.7f*
        // factor — :568-572). Terminality is therefore A FIELD ON EVERY ROW, never derived from the
        // word "softcap" — there is deliberately no function in this file that accepts prose and
        // returns a Terminality.
        //
        // ⚠ ONLY ONE HALF OF THAT PAIR IS STILL A ROW. [OPERATOR] removed the Respawn level row on
        // 2026-08-07 (see GuideRows below). The saturation is still real and the softcap CONSTANT
        // 400 is still transcribed; what went is the instruction to STOP there. The argument above
        // is unchanged — it is why the FIELD exists, not a claim that both rows still ship.
        public enum Terminality
        {
            Unspecified = 0,
            // "Stop here." Safe to write to the game's target field. 23 found EXACTLY ONE standing
            // terminal in the guide's own text — Respawn (energy id 2) at 401 — and [OPERATOR]
            // removed it 2026-08-07 as situational advice rather than a curve fact. There is STILL
            // exactly one standing terminal, and it is now AT Block at
            // ObjectiveTable.AtBlockHardCapLevel, terminal by RULING and not by curve shape. The
            // cardinality survived the swap; the row did not.
            Terminal,
            // "Reach this before doing X." WRITING IT TO target MAKES THE CASCADE ABANDON THE LANE
            // FOREVER — "2-3k Adventure a before Beardverse" does NOT mean stop at 3k.
            Precondition,
            // The row's own text does not distinguish the two. Not guessed — surfaced as needing an
            // operator decision.
            Ambiguous
        }

        // One row of the target table, in memory — 23 §0.1's schema minus the fields the objective
        // layer owns (chapter; the file format is NOT built here). Value is a range where the guide
        // gives one ("2-3k"): ValueLow == ValueHigh for scalars, and a RANGED TERMINAL is refused
        // rather than collapsed. ⚠ THAT REFUSAL DOES NOT REST ON WHICH ROW HAPPENS TO BE TERMINAL —
        // it once did ("the sole standing terminal 401 is a scalar") and went stale the day that row
        // was removed. It rests on the RANGE: collapsing one invents a number no source gave, the
        // low end stopping a lane early and the high end overstating the stop.
        public struct TargetRow
        {
            public string System;        // 23's slugs: augments · ngu-energy · ngu-magic · at · tm-speed · tm-goldmulti · wandoos
            public int Index;            // the decomp id (23 §0.2)
            public Track Track;
            public bool TrackNeutral;    // TM only: matches every active track (23 §2.5)
            public RowKind Kind;
            public Terminality Terminality;
            public long ValueLow;
            public long ValueHigh;
            public string CampaignScope; // null = standing. "100lc" marks the two campaign-scoped
                                         // terminals (TM 59/10) that must never write as standing
            // null = the row's terminality is unconditional. Non-null names a PROGRESSION CONDITION
            // that, once reached, LIFTS the stop — see the LiftGate block below.
            public string LiftGate;
            public string Objective;     // surfacing only
            public string Cite;          // no row without one (23 §0.1)
        }

        // ---- LIFT GATES — "when does this stop being a stop" -------------------------------------
        //
        // ⚠ TERMINALITY AND GATING ARE DIFFERENT AXES, AND THIS FIELD EXISTS BECAUSE CONFLATING THEM
        // IS A KNOWN FAILURE. Terminality is a property of the CURVE: what does the next level buy —
        // nothing (terminal), almost nothing (the `diminishing` kind amendment 35 §3 specifies and
        // this enum still lacks), or normally (precondition). A LIFT GATE is a property of
        // PROGRESSION STATE: the curve has not changed shape, the OPERATOR'S ADVICE about it has.
        //
        // Amendment 35 §3 diagnosed what happens when the two are mixed: the objective table filed
        // Block AT as PRECONDITION while amendment 24 called it TERMINAL, and "both were reaching
        // for a kind that did not exist". Encoding a lifting condition as a fourth TERMINALITY value
        // would rebuild that same confusion one axis over.
        //
        // ⚠ THIS DOES NOT GIVE BLOCK AT A HOME. Block AT's problem is the curve axis — it is
        // asymptotic, every level still buys something — so it still needs `diminishing`. The two
        // rows look alike and are not. Amendment 35 §3 stays open.
        //
        // The precedent is already here: CampaignScope is the same shape — a separate field saying
        // WHEN a terminal applies, checked before terminality and refusing rather than writing.
        //
        // ⚠ FAIL CLOSED, TWICE OVER:
        //   1. An UNRECOGNISED gate name is refused. A row can only be written by a build that
        //      knows what its gate means.
        //   2. A gate-UNAWARE caller — Route(row) with no gate state — refuses any gated row. A
        //      consumer that does not pass gate state must never write a gated row as though it
        //      were unconditional, which is the whole hazard: writing 401 permanently would
        //      abandon the lane exactly as a mis-filed precondition would.
        //
        // beast-v4: [OPERATOR] "respawn to 401 is only for early normal, once multiple NGUs are
        // easily capped it's fine to allocate", lifting "after a user's first Beast v4 kill".
        // The guide's own sentence already carries the tense — "don't invest further YET".
        // ⚠ FIRST-KILL-EVER, NOT FIRST-KILL-THIS-RUN, and the game agrees: the record is
        // achievementComplete[151], which [DECOMP] Rebirth.cs:121-123 reads to gate the Evil switch
        // with "You need to slay the BEAST v4 at least once". Achievements are only ever set true
        // ([DECOMP] AllAchievementsController.cs:137, :160) — nothing clears them, and rebirth does
        // not touch them. A per-RUN flag would re-apply the ceiling every rebirth, which is the one
        // behaviour this row must not have. The advisor's existing reader is
        // OptimizationAdvisor.VersionKilled(5, 4) (Beast is index 5 = titan6).
        //
        // ⚠ NO LIVE ROW IS GATED, AND THAT IS NOT A BUG. The row this gate was written for — Respawn
        // 401 — was removed by [OPERATOR] on 2026-08-07 (see GuideRows below, and ObjectiveTable's
        // id-2 block for the ruling). The MECHANISM was kept deliberately: it is the schema's second
        // gate-shaped field alongside CampaignScope, its fail-closed design is what a future gated
        // row will need, and LiftGateTests drives it with SYNTHETIC rows for exactly that reason —
        // so it cannot rot between the row that motivated it and the row that next needs it.
        public const string GateBeastV4 = "beast-v4";

        private static readonly string[] KnownLiftGates = { GateBeastV4 };

        public static bool IsKnownLiftGate(string gate) =>
            gate != null && Array.IndexOf(KnownLiftGates, gate) >= 0;

        private static bool GateIsLifted(string gate, string[] liftedGates) =>
            liftedGates != null && Array.IndexOf(liftedGates, gate) >= 0;

        // ---- system slugs and their advisor lanes ------------------------------------------------
        // The table is keyed by 23's schema, not by breakpoint class names. The wiring map, now
        // EXECUTABLE below (SystemForLane) rather than only descriptive: ngu-energy/ngu-magic ↔
        // NGUBP, tm-speed ↔ TimeMachineBP (Energy row), tm-goldmulti ↔ TimeMachineBP (Magic row),
        // at ↔ AdvancedTrainingBP, augments ↔ AugmentBP, wandoos ↔ WandoosBP — see LaneTargets.Table.
        public const string SysAugments = "augments";
        public const string SysNguEnergy = "ngu-energy";
        public const string SysNguMagic = "ngu-magic";
        public const string SysAt = "at";
        public const string SysTmSpeed = "tm-speed";
        public const string SysTmGoldMulti = "tm-goldmulti";
        public const string SysWandoos = "wandoos";

        public static bool IsNguSystem(string system) =>
            system == SysNguEnergy || system == SysNguMagic;

        // The map above as a function — the bridge's half of spec §10's "target table: per lane,
        // per pool". Takes the lane's CLASS NAME and its pool, because those are the only two facts
        // that identify a system; the index and the level are live reads the caller owns.
        //
        // A lane the schema cannot NAME is not addressable by a target row and gets no Pass 3
        // opinion at all — null here, and the caller keeps the game-field fallback that the
        // IsValid() membership filter already applied (spec §10 standalone).
        //
        // FIVE lane families have no system in 23's schema, and none of them is an omission:
        // BasicTrainingBP (the GAME supplies the number, (id+1)×5000 — spec §11 O1 excludes it by
        // name), RitualBP, BR and HackBP (no game terminator exists at all — spec §7's "faithful
        // false … do not fix these"), and beards (no breakpoint class, no profile token — 37 §S5 A4
        // is the open decision).
        //
        // ⚠ BestAug IS DELIBERATELY UNNAMED, and it is the one exclusion that is not obvious.
        // The augment pair it funds is chosen INSIDE Allocate() — the eager-eval hazard this file's
        // header records — so at Pass 3 time the lane has no augment slot: its LaneIndex is the
        // profile token's, not a slot's. Naming it (augments, LaneIndex) would let a row written for
        // AUG-0 silently speak for a BESTAUG lane. Its terminator is already the game's own signal
        // (hitAugmentTarget/hitUpgradeTarget — spec §7's "wired to the game's own signal" list),
        // evaluated by IsValid() before this pass runs at all.
        public static string SystemForLane(string laneClassName, bool energyPool)
        {
            switch (laneClassName)
            {
                case "NGUBP": return energyPool ? SysNguEnergy : SysNguMagic;
                case "TimeMachineBP": return energyPool ? SysTmSpeed : SysTmGoldMulti;
                case "AdvancedTrainingBP": return SysAt;
                case "AugmentBP": return SysAugments;
                case "WandoosBP": return SysWandoos;
                default: return null;
            }
        }

        // The table is FLAT: Evaluate selects rows by TRACK only and asserts nothing about system or
        // index, so its caller must have selected by (system, index) first. This is that selection.
        //
        // Returns null — which Evaluate reads as "no rows", identically to an empty list — rather
        // than an empty List, so the no-table case (every tick today) allocates nothing per lane.
        public static IList<TargetRow> RowsFor(IList<TargetRow> table, string system, int index)
        {
            if (table == null || table.Count == 0 || system == null)
                return null;

            List<TargetRow> hits = null;
            for (int i = 0; i < table.Count; i++)
            {
                var row = table[i];
                if (row.Index != index || !string.Equals(row.System, system, StringComparison.Ordinal))
                    continue;
                if (hits == null)
                    hits = new List<TargetRow>();
                hits.Add(row);
            }
            return hits;
        }

        // The NGU hardcap — the only NGU number that applies to all three tracks (23 §7.4):
        // hardCapNormalLevel() returns 1000000000L ([DECOMP] AllNGUController.cs:85-88) and clamps
        // level, evilLevel AND sadisticLevel ([DECOMP] NGUController.cs:60-63, :78-81, :107-110).
        // A ceiling, not a target — but it bounds every target field an operator could write: a
        // target ABOVE it can never be met by a clamped level, so the cascade never terminates.
        // Equality is fine — the clamp parks level AT the cap and >= holds.
        public const long NguHardCap = 1000000000L;

        // ---- the game's field sentinels — why a silence is NOT a zero ----------------------------
        // reachedTarget/reachedMagicTarget ([DECOMP] AllNGUController.cs:1302-1339 / :1341-1378),
        // per track:
        //     target == -1  ->  true   (the never-fund marker: lane reads MET, cascade skips it)
        //     target ==  0  ->  false  (the UNSET sentinel: lane reads unmet, funds FOREVER)
        //     else              level >= target
        // The advisor mirrors: NguValueMath.NguTargetMet, LaneTargets.TimeMachineTargetMet /
        // AdvancedTrainingTargetMet — all treat 0 as "no target". SO: writing 0 does not write "no
        // target", it ERASES the target and defaults the lane to unsatisfied-and-fundable — which
        // is exactly the default 23 §7 forbids for a silence. A silence is the ABSENCE of a row and
        // surfaces as Disposition.Silent with the ledger's reason; there is no path from a silence
        // to a numeric write, and WriteTargetGuard refuses 0 so one cannot be smuggled in by hand.
        public const long GameUnsetSentinel = 0L;
        public const long GameNeverFundMarker = -1L;

        // ---- the per-row answer ------------------------------------------------------------------

        public enum Disposition
        {
            // Fail-closed default: an unevaluated row/lane holds no disposition. Also every caller
            // error: a misrouted kind, a trackless row, a wandoos target, an invalid value, an
            // unseated lane.
            Refused = 0,
            // Terminal level row, track-matched, value validated: safe to write to the game's
            // target field, and the ONLY disposition that carries a satisfaction claim.
            WriteTarget,
            // Precondition level row: milestone surfaced for upstream layers, NEVER written.
            Precondition,
            // Ambiguous (or unspecified) terminality, or conflicting terminals: surfaced as needing
            // an operator decision. Not guessed, not written.
            OperatorDecision,
            // No row exists for (system, index, track): a SURFACED state carrying the silence
            // ledger's reason — never a default of 0, never long.MaxValue, never
            // "unsatisfied so keep funding" (23 §7).
            Silent
        }

        // Satisfaction is a CLAIM, and most dispositions make none. The mandated shape: an Evil NGU
        // lane with no level row is NOT treated as unsatisfied-and-fundable — its satisfaction is
        // NoClaim, not Unsatisfied.
        public enum Satisfaction
        {
            NoClaim = 0,
            Satisfied,
            Unsatisfied
        }

        // Private-constructor result, the FeasibilityPass.Verdict pattern: RowRoute is only
        // constructible through the factory methods, so a WriteTarget cannot exist without a
        // validated value and a refusal cannot exist without a reason. default(RowRoute) is a
        // refusal reading "unevaluated".
        public struct RowRoute
        {
            private readonly Disposition _disposition;
            private readonly long _targetToWrite;
            private readonly string _reason;

            private RowRoute(Disposition d, long target, string reason)
            {
                _disposition = d;
                _targetToWrite = target;
                _reason = reason;
            }

            public Disposition Disposition => _disposition;

            // Non-zero exactly when Disposition == WriteTarget. Zero everywhere else — and zero is
            // never a writable value (GameUnsetSentinel), so this cannot be misread as one.
            public long TargetToWrite => _targetToWrite;

            public string Reason =>
                _disposition == Disposition.WriteTarget ? null : (_reason ?? "unevaluated");

            public static RowRoute Write(long target) =>
                new RowRoute(Disposition.WriteTarget, target, null);

            public static RowRoute AsPrecondition(string why) =>
                new RowRoute(Disposition.Precondition, 0L, why ?? "precondition");

            public static RowRoute NeedsDecision(string why) =>
                new RowRoute(Disposition.OperatorDecision, 0L, why ?? "needs operator decision");

            public static RowRoute Refuse(string why) =>
                new RowRoute(Disposition.Refused, 0L,
                    string.IsNullOrEmpty(why) ? "refused (no reason given)" : why);
        }

        // ---- routing (23 §0.3): four kinds, one consumer -----------------------------------------

        // Gate-UNAWARE entry point, kept so every existing consumer compiles unchanged. It supplies
        // no gate state, so a gated row REFUSES here — see the LiftGate block. That is the point: a
        // caller that does not know about gates must not be able to write one as unconditional.
        public static RowRoute Route(in TargetRow row) => Route(row, null);

        // Gate-AWARE entry point. `liftedGates` is the set the caller has MEASURED as satisfied;
        // null means "I did not look", which is not the same as "none are satisfied" and is treated
        // as the stricter of the two.
        public static RowRoute Route(in TargetRow row, string[] liftedGates)
        {
            // Wandoos first, before any kind logic: Pass 3 REFUSES to produce a Wandoos target no
            // matter what shape the row claims to be. The guide is silent on a level on every track
            // AND CORRECTLY SO — Wandoos is the surplus sink (20 §2.8; spec §8), verified by
            // exhaustive search: the string "target" does not occur in Wandoos98Controller.cs or
            // Wandoos98.cs. The P1 campaign established that a synthetic Wandoos target is exactly
            // what makes amendment 16 §4's ranking come out at zero (23 §2.6).
            if (row.System == SysWandoos)
                return RowRoute.Refuse(
                    "wandoos: DO NOT SYNTHESISE A TARGET — the surplus sink is correctly " +
                    "unterminated (23 §2.6; 20 §2.8; the string \"target\" does not occur in " +
                    "Wandoos98Controller.cs or Wandoos98.cs)");

            switch (row.Kind)
            {
                case RowKind.Level:
                    return RouteLevel(row, liftedGates);

                case RowKind.Rate:
                    // Amendment 18 §1.2: consumed ENTIRELY by Pass 2 — "blank the bar" IS "funded
                    // to capacity". Refused with a reason, never silently ignored.
                    return RowRoute.Refuse(
                        "kind=rate is an allocation-sufficiency condition — Pass 2 capacity " +
                        "content, never Pass 3's (23 §0.3; amendment 18 §1.2); a rate row " +
                        "reaching Pass 3 is a caller error");

                case RowKind.Time:
                    return RowRoute.Refuse(
                        "kind=time is a phase / wall-clock split — auto-profile content, not the " +
                        "constraint layer's (23 §0.3); a time row reaching Pass 3 is a caller error");

                case RowKind.Predicate:
                    return RowRoute.Refuse(
                        "kind=predicate is a target SELECTOR — it is computed upstream and " +
                        "re-emitted as kind=level (23 §0.3); a raw predicate row reaching Pass 3 " +
                        "is a caller error");

                default:
                    return RowRoute.Refuse("kind unspecified — an unfilled row routes nowhere");
            }
        }

        private static RowRoute RouteLevel(in TargetRow row, string[] liftedGates)
        {
            // A row without a track is unusable (23 §0.1) — except the structurally track-neutral
            // TM rows (23 §2.5: one speedTarget, no per-track fields to disagree with).
            if (!row.TrackNeutral && row.Track == Track.Unspecified)
                return RowRoute.Refuse("row without a track is unusable (23 §0.1)");

            // Campaign-scoped rows (the 100LC's TM 59/10) hold ONLY inside their campaign, never as
            // a standing target (23 §2.5). Campaign activation is Campaign Advisor jurisdiction and
            // the Campaign Advisor is not built — so this core refuses to write them, with the
            // scope surfaced. This is a narrowing of "sole terminal", not a contradiction of it:
            // 22 §Q1.2's claim is NGU-scoped (23 §0.4).
            if (row.CampaignScope != null)
                return RowRoute.Refuse(string.Format(CultureInfo.InvariantCulture,
                    "campaign-scoped row ({0}): holds only inside its campaign, never as a " +
                    "standing target — Campaign Advisor jurisdiction (23 §2.5)", row.CampaignScope));

            // LIFT GATE — same shape as CampaignScope above and for the same reason: it answers
            // WHEN the row applies, which is not what terminality answers. Both outcomes refuse, so
            // neither can write a target the operator's ruling does not license.
            if (row.LiftGate != null)
            {
                if (!IsKnownLiftGate(row.LiftGate))
                    return RowRoute.Refuse(string.Format(CultureInfo.InvariantCulture,
                        "unrecognised lift-gate ({0}): this build does not know what lifts this " +
                        "row, so it CANNOT be written — an unknown gate fails closed, never open",
                        row.LiftGate));

                if (liftedGates == null)
                    return RowRoute.Refuse(string.Format(CultureInfo.InvariantCulture,
                        "gated row ({0}) reached a gate-UNAWARE caller: no gate state was supplied, " +
                        "so whether the stop still holds is unknown — refused rather than written " +
                        "as unconditional", row.LiftGate));

                if (GateIsLifted(row.LiftGate, liftedGates))
                    return RowRoute.Refuse(string.Format(CultureInfo.InvariantCulture,
                        "lift-gate {0} is SATISFIED: the stop has lifted and this row no longer " +
                        "speaks — the lane returns to being funded as a rate lane [OPERATOR]",
                        row.LiftGate));

                // Gate known and NOT lifted: the row is a live stop. Fall through to terminality.
            }

            switch (row.Terminality)
            {
                case Terminality.Terminal:
                    // Amendment 18 §1: EVERY Evil NGU is a rate row, both pools, all ids — there
                    // are no Evil NGU levels. An operator writing evilTarget is TRANSLATING the
                    // guide, not transcribing it. (§1.4's residue — CBlock recommended levels — is
                    // precondition-shaped and campaign-gated, so it never arrives Terminal.)
                    if (IsNguSystem(row.System) && !row.TrackNeutral && row.Track == Track.Evil)
                        return RowRoute.Refuse(
                            "amendment 18 §1: every Evil NGU is a rate row, both pools, all ids — " +
                            "there are no Evil NGU levels; \"BB the first five\" is a breakpoint, " +
                            "not a partition, and Pass 2 handles these lanes entirely");
                    // ⚠ THIS MESSAGE NAMES NO ROW, DELIBERATELY. It used to justify itself with "the
                    // sole standing terminal (Respawn 401) is a scalar" — a LIVE production string
                    // asserting a fact about the TABLE from inside the ROUTER, which went false the
                    // day [OPERATOR] removed that row and could not be caught by any table test.
                    // Why a range cannot be written is a property of ranges; that is all it says
                    // now, and GuideRowsParityTests pins that it names no row again.
                    if (row.ValueLow != row.ValueHigh)
                        return RowRoute.NeedsDecision(string.Format(CultureInfo.InvariantCulture,
                            "ranged terminal {0}-{1}: a range is not a writable stopping level — " +
                            "collapsing it would invent a number no source gave (the low end stops " +
                            "the lane early, the high end overstates the stop); surface for " +
                            "operator resolution", row.ValueLow, row.ValueHigh));
                    return WriteTargetGuard(row.System, row.ValueLow);

                case Terminality.Precondition:
                    // NOT written — "reach this before X" fed to target makes the cascade abandon
                    // the lane forever (23 §0.4). Surfaced for the layers that own preconditions.
                    return RowRoute.AsPrecondition(
                        "precondition: reach-before, not stop-at — never written to target (23 §0.4)");

                case Terminality.Ambiguous:
                    return RowRoute.NeedsDecision(
                        "AMBIGUOUS terminality: the row's own text does not distinguish stop-here " +
                        "from reach-before — not guessed; operator decision required (23 §0.4)");

                default:
                    return RowRoute.NeedsDecision(
                        "terminality unspecified: treated as AMBIGUOUS — surfaced, never guessed, " +
                        "never written (23 §0.4)");
            }
        }

        // The write validation — the game's sentinels and the hardcap, checked at the only door a
        // value can leave this pass through.
        private static RowRoute WriteTargetGuard(string system, long value)
        {
            if (value == GameUnsetSentinel)
                return RowRoute.Refuse(
                    "target 0 is the game's UNSET sentinel — reachedTarget returns false at 0 " +
                    "([DECOMP] AllNGUController.cs:1311-1314), so writing it erases the target " +
                    "and defaults the lane to unsatisfied-and-fundable; a silence must surface, " +
                    "never render as zero (23 §7)");
            if (value < 0)
                return RowRoute.Refuse(
                    "negative target is the game's never-fund marker (-1 reads MET — [DECOMP] " +
                    "AllNGUController.cs:1307-1310) — a different intent this pass must not emit " +
                    "as a stopping level");
            if (IsNguSystem(system) && value > NguHardCap)
                return RowRoute.Refuse(string.Format(CultureInfo.InvariantCulture,
                    "target {0} exceeds the NGU hardcap {1} — levels clamp at " +
                    "hardCapNormalLevel() ([DECOMP] AllNGUController.cs:85-88; NGUController.cs:" +
                    "60-63, :78-81, :107-110), so this target can never be met and the cascade " +
                    "never terminates", value, NguHardCap));
            return RowRoute.Write(value);
        }

        // The satisfaction comparator, exactly as the game evaluates a written target:
        // level >= target, per track ([DECOMP] AllNGUController.cs:1316, :1326, :1336). Equality
        // satisfies. Callers hand in the level OF THE MATCHING TRACK (NGU.cs:8-16) — handing a
        // Normal level to an Evil target is the class of error the Track machinery exists to stop.
        public static bool TargetMetByGame(long level, long target) => level >= target;

        // ---- the silence ledger (23 §7) ----------------------------------------------------------
        // An unfilled slot is a SURFACED state with a recorded reason — a LIST, not a discovery.
        // Held as data, the BudgetPass.Allowlist treatment, so the inventory cannot drift as prose
        // and TargetPassTests asserts every entry surfaces.

        public enum SilenceClass
        {
            Unspecified = 0,
            // Nothing anywhere: no level, no rate, no predicate, no mention.
            Silent,
            // Guidance exists but only as rate/time/predicate — no level for this pass to consume.
            NonLevel,
            // The guide's method produces a different ARTIFACT than the field consumes — augments:
            // a chosen augment, not a level (23 §7.1 S1).
            DifferentShape,
            // Correctly targetless — the surplus sink. Never synthesise (23 §2.6).
            SurplusSink
        }

        public struct SilenceSpec
        {
            public string System;    // null = every system (the Sadistic catch-all)
            public int[] Ids;        // null = every id in the system
            public Track Track;      // Unspecified = every track
            public SilenceClass Class;
            public string Reason;    // surfaced verbatim
            public string Cite;
        }

        // Declaration order is match order: specific entries first, catch-alls last. FindSilence
        // takes the first match, so M3's "never named" wins over the Sadistic catch-all for
        // (ngu-magic, 3, Sadistic) — the more informative reason.
        public static readonly SilenceSpec[] SilenceLedger =
        {
            // -- specific slots (23 §7.2) --
            new SilenceSpec { System = SysNguMagic, Ids = new[] { 3 }, Track = Track.Unspecified,
                Class = SilenceClass.Silent,
                Reason = "Number is never named in any chapter, any track — the only NGU id the " +
                         "guide never mentions once",
                Cite = "23 §7.2" },

            new SilenceSpec { System = SysNguEnergy, Ids = new[] { 7 }, Track = Track.Normal,
                Class = SilenceClass.NonLevel,
                Reason = "no level — GO priority-1 predicate only (E/M NGU >1.05x)",
                Cite = "23 §7.2; [GUIDE ch.4 §NGU Priority]" },

            new SilenceSpec { System = SysNguEnergy, Ids = new[] { 8 }, Track = Track.Normal,
                Class = SilenceClass.NonLevel,
                Reason = "no level — GO priority-2 predicate only; the evil softcap row's " +
                         "terminality is AMBIGUOUS (23 §2.3)",
                Cite = "23 §7.2" },

            new SilenceSpec { System = SysNguMagic, Ids = new[] { 0, 1 }, Track = Track.Normal,
                Class = SilenceClass.NonLevel,
                Reason = "no level — orders only (\"focus NGU Yggdrasil\"; \"Ygg/EXP becomes a " +
                         "big focus\")",
                Cite = "23 §7.2" },

            new SilenceSpec { System = SysNguMagic, Ids = new[] { 2 }, Track = Track.Normal,
                Class = SilenceClass.NonLevel,
                Reason = "SILENT except \"split mNGU into Power B/TM\" — a rate",
                Cite = "23 §7.2" },

            new SilenceSpec { System = SysNguMagic, Ids = new[] { 4, 5, 6 }, Track = Track.Normal,
                Class = SilenceClass.NonLevel,
                Reason = "no level — GO predicates only (TM >1.2x; Energy NGU priority-1; " +
                         "Adventure b >1.05x)",
                Cite = "23 §7.2" },

            // -- the Evil NGU rule (amendment 18 §1) — supersedes 23 §7.2's per-id evil rows,
            //    INCLUDING the M0/M1 conflict: "there is nothing to translate; the silence is
            //    correct." Both pools, ALL ids. --
            new SilenceSpec { System = SysNguEnergy, Ids = null, Track = Track.Evil,
                Class = SilenceClass.NonLevel,
                Reason = "amendment 18 §1: every Evil NGU is a rate row, both pools, all ids — " +
                         "no level exists; \"BB the first five\" is a breakpoint (where the " +
                         "player switches from Normal to Evil NGUs), not a partition; the end " +
                         "state is BB every NGU; Pass 2 handles these lanes entirely and Pass 3 " +
                         "never sees them",
                Cite = "amendment 18 §1; 23 §7.2" },

            new SilenceSpec { System = SysNguMagic, Ids = null, Track = Track.Evil,
                Class = SilenceClass.NonLevel,
                Reason = "amendment 18 §1: every Evil NGU is a rate row, both pools, all ids — " +
                         "no level exists (this resolves 23 §2.3's M0/M1 conflict: the silence " +
                         "is correct); Pass 2 handles these lanes entirely",
                Cite = "amendment 18 §1; 23 §2.3" },

            // -- AT (23 §7.2) --
            new SilenceSpec { System = SysAt, Ids = new[] { 0, 1 }, Track = Track.Evil,
                Class = SilenceClass.NonLevel,
                Reason = "no AT level — terminates on an ADVENTURE STAT (5-7T power to snipe EV " +
                         "exploder), not an AT level",
                Cite = "23 §7.2; [GUIDE ch.5 §24HR RB Schedule]" },

            new SilenceSpec { System = SysAt, Ids = new[] { 3, 4 }, Track = Track.Unspecified,
                Class = SilenceClass.NonLevel,
                Reason = "Wandoos dumps: SILENT on a level, every track — only the cost predicate " +
                         "\"Run Wandoos AT until cheap to run Wandoos 98\"",
                Cite = "23 §7.2; [GUIDE ch.5 §24HR RB Schedule]" },

            // -- TM (23 §7.2, §2.5) --
            new SilenceSpec { System = SysTmSpeed, Ids = null, Track = Track.Unspecified,
                Class = SilenceClass.NonLevel,
                Reason = "no standing level — a TIME BOX; the only numbers are 49 (with an " +
                         "explicit \"don't stop\") and the 100LC's campaign-scoped 59",
                Cite = "23 §7.2, §2.5" },

            new SilenceSpec { System = SysTmGoldMulti, Ids = null, Track = Track.Unspecified,
                Class = SilenceClass.Silent,
                Reason = "SILENT — the guide gives mechanism only; the sole number is the 100LC's " +
                         "campaign-scoped 10",
                Cite = "23 §7.2, §2.5" },

            // -- the two big ones (23 §7.1) --
            new SilenceSpec { System = SysAugments, Ids = null, Track = Track.Unspecified,
                Class = SilenceClass.DifferentShape,
                Reason = "S1: 14 slots x 3 tracks = 42 slots, ZERO values — and the silence is " +
                         "PRINCIPLED: the guide's method is a live per-rebirth solver (GO), so " +
                         "the operator's artifact is A CHOSEN AUGMENT, not a level — a different " +
                         "shape from what augmentTarget consumes",
                Cite = "23 §7.1 S1; [GUIDE guides/go-guide §Augments]" },

            new SilenceSpec { System = SysWandoos, Ids = null, Track = Track.Unspecified,
                Class = SilenceClass.SurplusSink,
                Reason = "SILENT on a level, every track — and correctly so: the terminator is an " +
                         "OS SWITCH and Wandoos is the surplus sink; DO NOT SYNTHESISE a target",
                Cite = "23 §2.6, §7.2; 20 §2.8" },

            // -- the Sadistic catch-all (23 §7.1 S2) — LAST, so specific reasons win --
            new SilenceSpec { System = null, Ids = null, Track = Track.Sadistic,
                Class = SilenceClass.Silent,
                Reason = "S2: SADISTIC is silent in every slot of every system — ch.8 gives no " +
                         "target of any kind for any of the seven systems",
                Cite = "23 §7.1 S2, §2.9" },
        };

        // First declared match wins. `found` false means the slot is not in the ledger — the answer
        // is STILL a surfaced silence (fail closed), just without a recorded provenance.
        public static bool FindSilence(string system, int id, Track track, out SilenceSpec spec)
        {
            foreach (var s in SilenceLedger)
            {
                if (s.System != null && s.System != system)
                    continue;
                if (s.Track != Track.Unspecified && s.Track != track)
                    continue;
                if (s.Ids != null && Array.IndexOf(s.Ids, id) < 0)
                    continue;
                spec = s;
                return true;
            }
            spec = default(SilenceSpec);
            return false;
        }

        // ---- the reference rows — the guide's standing level rows, as data (23 §2) ---------------
        // NOT the objective layer's file format (out of scope) and NOT exhaustive — the rows Pass 3
        // semantics are pinned against. This is 37 §S5 B6's "reference subset with no chapter field
        // and no reader outside tests", and it is kept deliberately: it is the only STATIC
        // TargetRow[] in the tree — ConstraintLayerBridge.TargetTable is now assigned, but it is
        // BUILT PER SWAP by ObjectiveTargets.Produce and is never a literal — so this is what
        // exercises RowsFor / Evaluate / Route on SHIPPED data with no translation step in between.
        // ObjectiveTable.LaneRows is a different type and has to go through ToTargetRow(id) to reach
        // this pass, which is the step the live table takes.
        //
        // ⚠ THIS IS A SUBSET OF ObjectiveTable.LaneRows AND MAY NOT DISAGREE WITH IT. It did, for
        // one day: 08b4344 removed Respawn 401 from LaneRows and d614347/3e9816d made Block AT a
        // hard TERMINAL at 100,000, and this table went on shipping the old value of both because
        // nothing compared the two. GuideRowsParityTests is that comparison now — every row here
        // must be matched field-for-field by a materialised LaneRow, and the two tables' TERMINAL
        // SETS must be equal in both directions. LaneRows is the authority; this is the fixture.
        // Being a SUBSET is legal and intended (LaneRows carries rungs this table omits); DISAGREE-
        // ING is not.
        //
        // TargetPassTests asserts the load-bearing shape claims: exactly ONE standing terminal in
        // the whole set — now AT Block at ObjectiveTable.AtBlockHardCapLevel, terminal by [OPERATOR]
        // RULING and not by curve shape — and the surviving "softcap" row carrying Precondition
        // against the ABSENCE of a Respawn row.
        public static readonly TargetRow[] GuideRows =
        {
            // ⚠ THERE IS NO RESPAWN (energy id 2) ROW, AND ITS ABSENCE IS THE DECISION. This table
            // shipped one — Terminal at 401, labelled here as "THE sole standing terminal in the
            // entire guide" — for a day after ObjectiveTable had already dropped it. [OPERATOR],
            // 2026-08-07:
            //
            //   "the 401 was in the guide because it's the 'best' for where the user is at at the
            //    time. the diminishing returns comes for it at 10,000 levels. and then the advisor
            //    already calculates what is the best use of energy already. So it's a moot point...
            //    if it's going to have good gains, then it's worth investing in, just like the
            //    other NGUs."
            //
            // ⚠ DO NOT RE-ADD IT FROM 23 §0.4 OR FROM [GUIDE ch.3 §NGUs]. Both still say it, and
            // both are faithful transcriptions of a sentence the operator has ruled situational.
            // The post-400 branch does still saturate ([DECOMP] AllNGUController.cs:449-458) —
            // saturating is not the same as worthless. The full reasoning, and the history of the
            // three passes that tried to keep the number, lives in ObjectiveTable's id-2 block;
            // the absence is pinned by GuideRowsParityTests and OperatorRuledRowsTests.

            // The surviving half of 23 §0.5: Adventure a's softcap 1000 carries an
            // explicit "When you hit softcaps, KEEP GOING. You will need adventure stats all game"
            // — and the mechanics agree: the post-1000 branch is UNBOUNDED sqrt
            // ([DECOMP] AllNGUController.cs:568-572). An implementation that treats "softcap" as
            // one concept gets this row wrong — it is the half of §0.5's pair that survived the
            // ruling above, and it was always the harder call: a softcap that means KEEP GOING.
            new TargetRow { System = SysNguEnergy, Index = 4, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 1000, ValueHigh = 1000,
                Objective = "adventure stats, all game — keep going",
                Cite = "[GUIDE ch.3 §NGUs] + [GUIDE mechanics/ngu]; [DECOMP AllNGUController.cs:568-572]" },

            new TargetRow { System = SysNguEnergy, Index = 6, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 1000, ValueHigh = 1000,
                Objective = "drop chance softcap — same keep-going clause as id 4",
                Cite = "[GUIDE ch.3 §NGUs] + [GUIDE mechanics/ngu]" },

            // The PAWG ladder, id 0 (ids 1, 3, 5 share all four rungs — 23 §2.2: the guide's single
            // most target-shaped NGU artifact, four ids, one value, four rungs, chapter-keyed, and
            // every rung a PRECONDITION: campaign prep, levelled before the CBlock because NGUs are
            // exempt from canLevel() (21 §A2).
            new TargetRow { System = SysNguEnergy, Index = 0, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 500, ValueHigh = 500,
                Objective = "PAWG rung 1 — Mini-CBlock prep", Cite = "[GUIDE ch.3 §NGUs]" },
            new TargetRow { System = SysNguEnergy, Index = 0, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 5000, ValueHigh = 5000,
                Objective = "PAWG rung 2 — CBlock1 prep", Cite = "[GUIDE ch.3 §CBlock1]" },
            new TargetRow { System = SysNguEnergy, Index = 0, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 150000, ValueHigh = 150000,
                Objective = "PAWG rung 3 — CBlock2 prep", Cite = "[GUIDE ch.4 §Post-v2]" },
            new TargetRow { System = SysNguEnergy, Index = 0, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 5000000, ValueHigh = 5000000,
                Objective = "PAWG rung 4 (\"5m+\") — Evil entry", Cite = "[GUIDE ch.4 §Evil Prep]" },

            // The three Evil-track level rows ch.5 does emit — every one precondition or AMBIGUOUS,
            // never terminal, consistent with amendment 18 §1 (no Evil NGU level is ever WRITTEN).
            new TargetRow { System = SysNguEnergy, Index = 7, Track = Track.Evil,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 1000, ValueHigh = 1000,
                Objective = "\"both NGU E/M NGU to softcap, THEN...\" — T8 LRB day 1",
                Cite = "[GUIDE ch.5 §LRB to T8]" },
            new TargetRow { System = SysNguMagic, Index = 5, Track = Track.Evil,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 1000, ValueHigh = 1000,
                Objective = "the other half of \"both E/M NGU to softcap\"",
                Cite = "[GUIDE ch.5 §LRB to T8]" },
            new TargetRow { System = SysNguEnergy, Index = 8, Track = Track.Evil,
                Kind = RowKind.Level, Terminality = Terminality.Ambiguous,
                ValueLow = 1000, ValueHigh = 1000,
                Objective = "\"Softcap NGU PP\" — no post-softcap PP instruction exists anywhere, " +
                            "so stop vs reach-then-continue is undetermined by the guide's own text",
                Cite = "[GUIDE ch.5 §LRB to T8]; 23 §2.3" },

            // AT Block Damage (23 §2.4). The 99.9%-at-5 rung is the BROKEN RUNG and is deliberately
            // NOT a row: unusable, not adjudicated (23 §0.6).
            //
            // ⚠ THE TWO ROWS CARRY DIFFERENT TERMINALITY AND THAT IS THE POINT. The ch.3 5,000 rung
            // is the 99% reach-before rung and stays a PRECONDITION, which RouteLevel never writes.
            // The ch.5 row is TERMINAL BY [OPERATOR] RULING, NOT BY THE CURVE — 2026-08-07, "the
            // Block AT is a hard cap at 100,000 and should never be capped lower". This table said
            // Precondition here until now; 08b4344 moved ObjectiveTable and nothing moved this.
            //
            // ⚠ DO NOT "CORRECT" IT BACK. Amendment 35 §1 is right that the Block curve is
            // 0.5 / (1 + levelFactor × Level) — asymptotic, no branch change, no clamp — so it never
            // mechanically saturates, and a reader deriving terminality from the curve will conclude
            // this row cannot be terminal and will be reasoning correctly from the wrong premise. An
            // operator may draw a line on a curve that has none. The stated 99% reason belongs to
            // the OTHER number (the 5,000 rung); the ruling stands on its number. ObjectiveTable's
            // id-2 block carries the whole argument and is not repeated here.
            //
            // The value is ObjectiveTable.AtBlockHardCapLevel — a compile-time const shared with
            // that row and with LevelPlanner's live write, so the three cannot drift apart. This
            // row is EVIL-TRACK ONLY; the "every difficulty" half of the ruling is carried by the
            // live writer, because levelTarget[2] is one field per slot with no per-track split.
            new TargetRow { System = SysAt, Index = 2, Track = Track.Normal,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 5000, ValueHigh = 5000,
                Objective = "BDW -> T6 LRB", Cite = "[GUIDE ch.3 §BDW/BAE]" },
            new TargetRow { System = SysAt, Index = 2, Track = Track.Evil,
                Kind = RowKind.Level, Terminality = Terminality.Terminal,
                ValueLow = ObjectiveTable.AtBlockHardCapLevel,
                ValueHigh = ObjectiveTable.AtBlockHardCapLevel,
                Objective = "first Evil 24h rebirth: \"100k AT Block levels, THEN AT Power\" — a " +
                            "HARD CAP by [OPERATOR] ruling, never capped lower",
                Cite = "[GUIDE ch.5 §24HR RB Schedule]; [OPERATOR] hard cap 100,000" },

            // TM speed 49 — the trap row: the guide names the number, explains what happens at 50,
            // and says DON'T STOP. Setting speedTarget = 49 would implement the number and invert
            // the advice (23 §2.5). Track-neutral: TM stores one speedTarget, no per-track split.
            new TargetRow { System = SysTmSpeed, Index = 0, TrackNeutral = true,
                Kind = RowKind.Level, Terminality = Terminality.Precondition,
                ValueLow = 49, ValueHigh = 49,
                Objective = "bar reaches max speed at 50/s — \"Don't stop at Level 49\"",
                Cite = "[GUIDE mechanics/time-machine]; [GUIDE ch.2]" },

            // The two campaign-scoped terminals — the 100LC's 59/10, the only place the guide
            // allocates TM numbers as a complete checkable statement (59+10 = 69 of the 100 budget,
            // TimeMachineController.cs:354-357/:397-400 both counting). They hold ONLY inside the
            // 100LC and are never a standing speedTarget/multiTarget (23 §2.5) — Route refuses them
            // while the Campaign Advisor does not exist.
            new TargetRow { System = SysTmSpeed, Index = 0, TrackNeutral = true,
                Kind = RowKind.Level, Terminality = Terminality.Terminal,
                ValueLow = 59, ValueHigh = 59, CampaignScope = "100lc",
                Objective = "100LC post-boss-30: 59 energy TM levels for 100x gold, then stop",
                Cite = "[GUIDE mechanics/challenges §Challenge Tips]; 23 §2.5" },
            new TargetRow { System = SysTmGoldMulti, Index = 0, TrackNeutral = true,
                Kind = RowKind.Level, Terminality = Terminality.Terminal,
                ValueLow = 10, ValueHigh = 10, CampaignScope = "100lc",
                Objective = "100LC: 10 magic TM levels",
                Cite = "[GUIDE mechanics/challenges §Challenge Tips]; 23 §2.5" },
        };

        // ---- the lane-level pass -----------------------------------------------------------------

        public struct LaneState
        {
            public string System;     // 23's slug
            public int Index;         // the decomp id
            public Track ActiveTrack; // NGU: settings.nguLevelTrack, read live (PlayerSettings.cs:188)
            public long LevelOnTrack; // level | evilLevel | sadisticLevel matching ActiveTrack (NGU.cs:8-16)
            // Lift gates the CALLER has measured as satisfied. null/empty = none satisfied, which is
            // the safe default: every gated row stays a stop. The live read belongs to the caller —
            // this file stays pure and headless-testable, which is why the gate arrives as data
            // rather than being read from Character here.
            public string[] LiftedGates;

            // THE OPERATOR'S OWN LIVE TARGET in this lane's game field, supplied as DATA for exactly
            // the reason LiftedGates is: the live read belongs to the caller and this file stays
            // pure. 0 is the game's UNSET sentinel and reads here as "no operator preference", which
            // is also the default — so a caller that does not supply it gets the row's own value,
            // bit-for-bit the behaviour before this field existed.
            //
            // ⚠ IT CAN ONLY RAISE A TERMINAL STOP, NEVER LOWER ONE. [OPERATOR] 2026-08-07: "the
            // operator's higher target should win over the ruled cap but it should never be capped
            // below the 100,000 level." Both halves are load-bearing and they pull opposite ways —
            // see the floor applied in Evaluate for how one function delivers both.
            //
            // ⚠ ONLY THE AT LANES SUPPLY IT, AND THAT IS THE RULING'S SCOPE RATHER THAN AN OVERSIGHT.
            // ConstraintLayerBridge.LaneStateFor reads advancedTraining.levelTarget[i] and nothing
            // else; every other lane leaves this 0. There is nothing for them to raise in any case —
            // AT slot 2 is the only row in the shipped table that reaches WriteTarget at all, and a
            // silence or a precondition never consults this field (a precondition that could be
            // raised into a write would be 23 §0.4's abandon-the-lane defect wearing a new hat).
            public long OperatorTarget;
        }

        // The per-lane product: one disposition, at most one satisfaction claim, and EVERY misrouted
        // row's refusal — a rate row reaching Pass 3 is refused with a reason, not silently ignored,
        // even when a valid level row sits beside it.
        public struct LaneAnswer
        {
            public Disposition Disposition;
            public Satisfaction Satisfaction;
            public long TargetToWrite;      // non-zero ONLY when Disposition == WriteTarget
            public long NextMilestone;      // Precondition only: the lowest unmet rung's upper value, 0 if all met
            public bool MilestonesAllMet;   // Precondition only
            public string Reason;           // non-null unless WriteTarget
            public string[] RowErrors;      // every refused row's reason, always present (may be empty)
        }

        private static LaneAnswer Refused(string reason, string[] rowErrors) =>
            new LaneAnswer
            {
                Disposition = Disposition.Refused,
                Satisfaction = Satisfaction.NoClaim,
                Reason = reason,
                RowErrors = rowErrors ?? EmptyErrors,
            };

        private static readonly string[] EmptyErrors = new string[0];

        // The pass. `seat` is the lane's Pass 0-2 outcome — the ordering contract (spec §2) asserted,
        // not re-derived: this function performs no budget, feasibility or capacity check of its own,
        // and an unseated lane arriving here is a caller error.
        public static LaneAnswer Evaluate(in LaneState lane, IList<TargetRow> rows,
            FeasibilityPass.Verdict seat)
        {
            if (!seat.Seated)
                return Refused(
                    "contract violation: lane arrived at Pass 3 unseated (" + seat.Reason + ") — " +
                    "a lane eliminated by Pass 0, 1 or 2 never reaches the target pass (spec §2)",
                    null);

            // Wandoos refuses at the LANE level too, before any row logic — there is no path on
            // which this pass produces a Wandoos target (23 §2.6).
            if (lane.System == SysWandoos)
                return Refused(
                    "wandoos: DO NOT SYNTHESISE A TARGET — the surplus sink is correctly " +
                    "unterminated (23 §2.6; 20 §2.8)", null);

            // Select by track: only a row on the lane's ACTIVE track (or a structurally
            // track-neutral row) can speak for it. A Normal-track row does not satisfy an
            // Evil-track lane — it is not an error, it is simply not selected.
            var errors = new List<string>();
            TargetRow? writeRow = null;
            string decisionReason = null;
            bool conflictingTerminals = false;
            var preconditions = new List<TargetRow>();
            int selectedCount = 0;

            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (!row.TrackNeutral && row.Track != lane.ActiveTrack)
                        continue;
                    selectedCount++;

                    var route = Route(row, lane.LiftedGates);
                    switch (route.Disposition)
                    {
                        case Disposition.WriteTarget:
                            if (writeRow != null)
                                conflictingTerminals = true;
                            else
                                writeRow = row;
                            break;
                        case Disposition.Precondition:
                            preconditions.Add(row);
                            break;
                        case Disposition.OperatorDecision:
                            if (decisionReason == null)
                                decisionReason = route.Reason;
                            break;
                        default:
                            errors.Add(route.Reason);
                            break;
                    }
                }
            }

            var rowErrors = errors.ToArray();

            // Precedence, conservative outward: an unresolved operator decision on the lane's own
            // track blocks a write — the operator has not finished deciding this lane.
            if (conflictingTerminals)
                return Refused(
                    "two terminal rows on one track: which stopping level is intended is an " +
                    "operator question, not a coin toss", rowErrors);

            if (decisionReason != null)
                return new LaneAnswer
                {
                    Disposition = Disposition.OperatorDecision,
                    Satisfaction = Satisfaction.NoClaim,
                    Reason = decisionReason,
                    RowErrors = rowErrors,
                };

            if (writeRow != null)
            {
                // ⚠ THE RULED STOP IS A FLOOR, NOT A CEILING. [OPERATOR] 2026-08-07, verbatim: "the
                // operator's higher target should win over the ruled cap but it should never be
                // capped below the 100,000 level." So the stop this pass enforces is the ruled value
                // OR the operator's own live target, whichever is HIGHER — and the ruled value is the
                // least it can ever be.
                //
                // ⚠ THE DECISION IS NOT MADE HERE. LaneTargets.AdvancedTrainingPurposeFloor is the
                // function LevelPlanner.ApplyPurposeFloor already writes the game field with, and
                // Pass 3 calls THAT rather than restating the rule, because two sources of truth
                // drifting apart is what 3e9816d and 818759b were both written to close. Calling it
                // is what makes them unable to disagree; OperatorRuledRowsTests pins the single
                // call-site census and drives both sides through one value table.
                //
                // ⚠ ITS NAME IS AT'S BECAUSE AT IS WHERE THE RULING IS, and only AT lanes supply an
                // OperatorTarget — see LaneState. The unsupplied case is 0, which floors to the row's
                // own value, so every other lane is answered exactly as before.
                //
                // ⚠ AND A 0 OR NEGATIVE TARGET CANNOT ESCAPE THROUGH HERE. The floor returns either
                // the ruled value or an operator target that is strictly POSITIVE and >= it, and the
                // ruled value already cleared WriteTargetGuard (which refuses the UNSET sentinel 0
                // and the never-fund marker -1). So the two sentinels the game reads specially are
                // structurally unreachable on this path, not merely unlikely.
                var ruled = writeRow.Value.ValueLow;
                var target = LaneTargets.AdvancedTrainingPurposeFloor(lane.OperatorTarget, ruled);
                return new LaneAnswer
                {
                    Disposition = Disposition.WriteTarget,
                    Satisfaction = TargetMetByGame(lane.LevelOnTrack, target)
                        ? Satisfaction.Satisfied
                        : Satisfaction.Unsatisfied,
                    TargetToWrite = target,
                    Reason = null,
                    RowErrors = rowErrors,
                };
            }

            if (preconditions.Count > 0)
            {
                // Milestone surfacing for the upstream layers that own preconditions. Met-ness is
                // conservative — a ranged rung reads met only past its UPPER bound — and the next
                // milestone is the lowest unmet rung, which is what a ladder consumer asks first.
                long next = 0;
                bool allMet = true;
                foreach (var p in preconditions)
                {
                    if (lane.LevelOnTrack >= p.ValueHigh)
                        continue;
                    allMet = false;
                    if (next == 0 || p.ValueHigh < next)
                        next = p.ValueHigh;
                }
                return new LaneAnswer
                {
                    Disposition = Disposition.Precondition,
                    Satisfaction = Satisfaction.NoClaim,
                    NextMilestone = next,
                    MilestonesAllMet = allMet,
                    Reason = "precondition rows only: reach-before milestones, never written to " +
                             "target (23 §0.4) — the cascade must not abandon this lane",
                    RowErrors = rowErrors,
                };
            }

            if (selectedCount > 0)
                // Every selected row was a misrouted kind — the lane-level answer is the caller
                // error, with each row's reason preserved. Satisfaction stays NoClaim: rate rows
                // belong to Pass 2 and this pass makes no funding claim over them (amendment 18).
                return Refused(rowErrors.Length > 0 ? rowErrors[0] : "no consumable row", rowErrors);

            // NO row on the active track: a silence. SURFACED, with the ledger's recorded reason —
            // never a default of 0, never long.MaxValue, never unsatisfied-and-fundable (23 §7).
            SilenceSpec silence;
            var found = FindSilence(lane.System, lane.Index, lane.ActiveTrack, out silence);
            return new LaneAnswer
            {
                Disposition = Disposition.Silent,
                Satisfaction = Satisfaction.NoClaim,
                Reason = found
                    ? "silent (" + silence.Class + "): " + silence.Reason + " [" + silence.Cite + "]"
                    : "silent: no row for (" + (lane.System ?? "?") + ", " +
                      lane.Index.ToString(CultureInfo.InvariantCulture) + ", " + lane.ActiveTrack +
                      ") and no ledger entry — surfaced, never defaulted (23 §7)",
                RowErrors = rowErrors,
            };
        }
    }
}
