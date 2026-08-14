using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // THE TARGET-TABLE PRODUCER — the first of constraint-layer-spec §10's three inputs, materialised
    // from the objective layer (`ObjectiveTable`) into the shape `ConstraintLayerBridge.TargetTable`
    // declares (`IList<TargetPass.TargetRow>`).
    //
    // ⚠ THE BRIDGE NOW ASSIGNS ITS FIELD FROM THIS FILE. `ConstraintLayerBridge.RefreshTargetTable`
    // calls `Produce` at the top of every `PerformSwap`, so a row emitted here reaches a LIVE Pass 3
    // and can stop a live lane. This was written as data-in / rows-out with a caller nowhere; the
    // caller now exists, and what changed between then and now is a ruling, not a filter:
    //
    //   · WHEN THIS FILE WAS WRITTEN the only row that could route to `WriteTarget` was **Respawn
    //     (ngu-energy id 2) at 401**, and wiring it was declined because amendment 34 §7 item 1 gates
    //     the objective layer's consumption and because that row moves allocation.
    //   · [OPERATOR] THEN REMOVED THAT ROW (08b4344): *"the 401 was in the guide because it's the
    //     'best' for where the user is at at the time… the advisor already calculates what is the
    //     best use of energy already. So it's a moot point."* The carve-out below still compiles but
    //     matches nothing the table holds, and amendment 21 §1 stands unqualified again.
    //   · IN ITS PLACE, d614347/3e9816d made **AT slot 2 at `ObjectiveTable.AtBlockHardCapLevel`** a
    //     standing TERMINAL on [OPERATOR]'s ruling *"the Block AT is a hard cap at 100,000 and should
    //     never be capped lower."* That is now the ONLY row this producer can emit that routes to a
    //     write, measured over the whole query space by `ObjectiveTargetsTests`.
    //
    // ⚠ IT STOPS ONE SLOT, NOT A SYSTEM. `ALLAT` yields five separate `AdvancedTrainingBP` with
    // `Index` 0..4, each its own lane, and `TargetPass.RowsFor` filters on System AND Index — so slots
    // 0/1/3/4 receive null and keep funding at the same level. Advanced Training is never terminated.
    //
    // ⚠ AND THE STOP AGREES WITH THE LIVE WRITER. `LevelPlanner` writes the same const into
    // `advancedTraining.levelTarget[2]`, which is the field the `IsValid()` membership filter already
    // compares against — so Pass 3's stop is a second gate at the same number, not a new one. Both
    // sides read `ObjectiveTable.AtBlockHardCapLevel`; they cannot drift apart without one of them
    // stopping reading the const.
    //
    // WHAT IT EXCLUDES, AND WHY EACH EXCLUSION IS A DECISION AND NOT A FILTER. Every rule below has a
    // decision record behind it, is applied by NAME rather than by inspecting the data, and reports
    // itself through `Excluded` so a row that vanishes says why:
    //
    //   · NGU level rows — ALL of them. Amendment 21 §1: every NGU lane is a rate lane, on any track,
    //     in any pool, at any chapter. Amendment 34 §3's Respawn-401 narrowing is DORMANT — [OPERATOR]
    //     removed its subject at 08b4344 — so the plain rule is what runs and Pass 3 sees no NGU lane
    //     at all. The PAWG ladder is campaign readiness evaluated once at the gate, never by the
    //     per-tick cascade (amendment 21 §4).
    //   · TM level rows, all of them — amendment 24 §4/§6. *"`speedTarget` and `multiTarget` should be
    //     ZERO, always."* TM is a rate lane with no saturation point, so the 49 row (which the guide
    //     itself marks "don't stop at") and the two 100LC terminals are all withheld.
    //   · Wandoos rows — the surplus sink is correctly unterminated (spec §8; 23 §2.6). `TargetPass`
    //     refuses the slug at both the row and the lane level; this is the belt to that braces.
    //   · Campaign-scoped rows — they hold only inside their campaign (23 §2.5) and the Campaign
    //     Advisor is not built. `TargetPass.RouteLevel` refuses them; producing them would only fill
    //     `LaneAnswer.RowErrors` every tick.
    //   · Non-`Level` kinds — `TargetPass.Route` calls a rate/time/predicate row reaching Pass 3 a
    //     CALLER ERROR (23 §0.3; amendment 18 §1.2). ⚠ Emitting them would also DEGRADE SURFACING:
    //     `Evaluate` answers `Refused` when every selected row is misrouted, which replaces the
    //     silence ledger's recorded provenance with a caller-error string on a lane that is still
    //     funded exactly as before.
    //   · Whole-system rows (`Ids == EveryId`) — `TargetPass.RowsFor` selects on an EXACT index and
    //     can never hand one to a lane (`ObjectiveTable.cs:52-56`). Expanding one to concrete ids is
    //     precisely the widening that would let an unindexed rule speak for a specific slot.
    //
    // ⚠ A SILENCE IS NOT A ZERO, and this producer cannot manufacture one. It only ever COPIES rows
    // through `ObjectiveTable.LaneRow.ToTargetRow`; it never constructs a `TargetRow` of its own,
    // never defaults a value, and never emits a row for a slot the table is silent on. The absence of
    // a row still reaches `TargetPass.Evaluate` as `Disposition.Silent` carrying the ledger's reason —
    // which is a different thing from a row of 0, and 0 is the game's UNSET sentinel that funds
    // forever ([DECOMP] AllNGUController.cs:1311-1314).
    public static class ObjectiveTargets
    {
        // Chapters proper are 1-8 (ObjectiveTable's own bound).
        public const int FirstChapter = 1;
        public const int LastChapter = 8;

        // The four live facts a producer needs, in the same split `ConstraintLayerBridge.LaneStateFor`
        // makes: the NGUs follow settings.nguLevelTrack, everything else the run's difficulty.
        public struct Query
        {
            public int Chapter;
            public bool ChapterKnown;     // StageDetector.Detect().Known
            public TargetPass.Track NguTrack;
            public TargetPass.Track RunTrack;
        }

        // One withheld row, with the rule that withheld it. Surfacing only — but a rule that cannot
        // say what it dropped is indistinguishable from a table that never had the row.
        public struct Exclusion
        {
            public string System;
            public int[] Ids;
            public TargetPass.RowKind Kind;
            public TargetPass.Terminality Terminality;
            public long ValueLow;
            public long ValueHigh;
            public string Rule;           // the decision, in one line, with its cite
        }

        public struct Table
        {
            // ⚠ NULL IS `Held`, EMPTY IS "the guide says nothing here" — and both are spec §10's
            // STANDALONE contract, not a degraded mode. Neither can stop a lane: `RowsFor` returns
            // null for an empty table exactly as it does for a null one.
            public IList<TargetPass.TargetRow> Rows;

            public bool Held;
            public string HeldReason;     // non-null exactly when Held

            public IList<Exclusion> Excluded;

            public int Count { get { return Rows == null ? 0 : Rows.Count; } }
        }

        private static readonly TargetPass.TargetRow[] NoRows = new TargetPass.TargetRow[0];

        // ---- the one NGU carve-out — ⚠ DORMANT: NO ROW IN THE TABLE SATISFIES IT ------------------
        // Amendment 34 §3: *"NGUs take no target level — except where the curve saturates. That is
        // exactly one case: Respawn (energy id 2) at 401."* [OPERATOR] REMOVED THAT ROW AT 08b4344,
        // so this predicate now matches nothing `ObjectiveTable` holds and amendment 21 §1 runs
        // unqualified.
        //
        // ⚠ KEPT RATHER THAN DELETED, AND THAT IS A LIVE DECISION NOW THE BRIDGE IS WIRED: `Rule`
        // below still reads `IsNguSystem(row.System) && !IsRespawnCarveOut(row, id)`, so a Respawn-401
        // terminal re-added to the table WOULD be admitted and WOULD stop a live NGU lane. What closes
        // that hole is on the other side — the row cannot return without first failing
        // `OperatorRuledRowsTests.The_number_401_is_not_a_level_target_anywhere` and
        // `GuideRowsParityTests.The_number_401_is_not_a_level_in_the_reference_rows_either`.
        // `ObjectiveTargetsTests.The_NGU_carve_out_has_no_subject_left_and_is_pinned_as_dormant`
        // asserts the dormancy, exercises the predicate on synthetic rows (a guard only a future row
        // can trip must be tested without that row — the same treatment the Wandoos and EveryId rules
        // get), and names both guards as executable dependencies.
        public const string NguCarveOutSystem = TargetPass.SysNguEnergy;
        public const int NguCarveOutIndex = 2;
        public const long NguCarveOutValue = 401L;

        // Is this the one NGU row Pass 3 is allowed to see? Terminality is checked too: the carve-out
        // is *"a stopping point where further investment is provably worthless"* (amendment 34 §3.1),
        // so a PRECONDITION at 401 would not be it. ⚠ Returns false for every row currently shipped.
        public static bool IsRespawnCarveOut(in ObjectiveTable.LaneRow row, int id)
        {
            return row.Kind == TargetPass.RowKind.Level &&
                   row.Terminality == TargetPass.Terminality.Terminal &&
                   row.CampaignScope == null &&
                   string.Equals(row.System, NguCarveOutSystem, StringComparison.Ordinal) &&
                   id == NguCarveOutIndex &&
                   row.ValueLow == NguCarveOutValue && row.ValueHigh == NguCarveOutValue;
        }

        // ---- the rules, as one pure function ------------------------------------------------------

        // Admitted, or denied with the decision that denied it. PUBLIC because two of these rules are
        // DORMANT against the shipped table — no whole-system row and no non-NGU/non-TM row is
        // currently a `Level` — and a rule that never fires cannot be pinned through `Produce`. A
        // dormant guard that nothing can test is how the row it was written for gets through later.
        public struct Ruling
        {
            public bool Admitted;
            public string Rule;               // null exactly when Admitted
        }

        private static Ruling Deny(string rule) { return new Ruling { Admitted = false, Rule = rule }; }
        private static readonly Ruling Allow = new Ruling { Admitted = true, Rule = null };

        // ⚠ ORDER IS THE REASON EACH ROW GIVES, and it mirrors TargetPass's own: `Route` tests the
        // Wandoos slug FIRST, "before any kind logic", and `RouteLevel` tests campaign scope before
        // terminality. A row denied for two reasons should report the one a reader needs.
        public static Ruling Rule(in ObjectiveTable.LaneRow row, int id)
        {
            if (string.Equals(row.System, TargetPass.SysWandoos, StringComparison.Ordinal))
                return Deny("wandoos: DO NOT SYNTHESISE A TARGET — the surplus sink is correctly " +
                    "unterminated (spec §8; 23 §2.6)");

            if (row.Kind != TargetPass.RowKind.Level)
                return Deny("kind=" + row.Kind + " is not Pass 3 content — a rate/time/predicate " +
                    "row reaching Pass 3 is a CALLER ERROR (23 §0.3; amendment 18 §1.2), and " +
                    "supplying one replaces the lane's silence-ledger reason with a refusal while " +
                    "funding it identically");

            if (row.CampaignScope != null)
                return Deny("campaign-scoped (" + row.CampaignScope + "): holds only inside its " +
                    "campaign, never as a standing target (23 §2.5) — Campaign Advisor " +
                    "jurisdiction, and it is not built");

            if (string.Equals(row.System, TargetPass.SysTmSpeed, StringComparison.Ordinal) ||
                string.Equals(row.System, TargetPass.SysTmGoldMulti, StringComparison.Ordinal))
                return Deny("TM never terminates — it is a rate lane; \"speedTarget and multiTarget " +
                    "should be ZERO, always\" (amendment 24 §4, §6; spec §7)");

            if (row.Ids == ObjectiveTable.EveryId)
                return Deny("whole-system row (Ids == EveryId): TargetPass.RowsFor selects on an " +
                    "EXACT index and can never hand it to a lane (ObjectiveTable.cs:52-56) — " +
                    "expanding it would let an unindexed rule speak for a specific slot");

            // ⚠ THE CARVE-OUT IS DORMANT AND THE PLAIN RULE IS WHAT RUNS. Amendment 34 §3 narrowed
            // this to "except Respawn (ngu-energy id 2) at 401", but [OPERATOR] removed that row from
            // ObjectiveTable at 08b4344, so IsRespawnCarveOut matches nothing the table holds and
            // amendment 21 §1 stands unqualified: Pass 3 sees NO NGU lane. The predicate is kept as a
            // guard against the row's return rather than deleted — and pinned as dormant by
            // ObjectiveTargetsTests.The_NGU_carve_out_has_no_subject_left_and_is_pinned_as_dormant,
            // which also names the two tests that stop 401 becoming a level again.
            if (TargetPass.IsNguSystem(row.System) && !IsRespawnCarveOut(row, id))
                return Deny("NGUs take no target level (amendment 21 §1) — every NGU lane is a rate " +
                    "lane, on any track, in any pool, at any chapter. Amendment 34 §3's Respawn-401 " +
                    "carve-out is DORMANT: [OPERATOR] removed that row at 08b4344");

            return Allow;
        }

        // ---- the producer -------------------------------------------------------------------------

        public static Table Produce(in Query q)
        {
            // ⚠ AN UNKNOWN CHAPTER IS NOT CHAPTER ZERO. `ObjectiveTable.ChapterMatches` treats a query
            // chapter of `ChapterAny` (0) as "match EVERY row in the table" (:905-909), so a
            // StageDetector blip collapsed to a bare 0 would supply all eight chapters' rows at once —
            // the same hazard `ConstraintLayerBridge.ObjectiveCompare` documents at :639-647, but with
            // allocation rather than a log line on the other side of it. Hold instead.
            if (!q.ChapterKnown)
                return Hold("chapter unknown — a bare 0 is ChapterAny and would supply every " +
                            "chapter's rows at once (ObjectiveTable.cs:905-909); standalone mode " +
                            "until the stage is readable (spec §10)");

            if (q.Chapter < FirstChapter || q.Chapter > LastChapter)
                return Hold(string.Format(CultureInfo.InvariantCulture,
                    "chapter {0} is outside 1-8 — no row is keyed to it and ChapterAny is not a " +
                    "chapter; standalone mode (spec §10)", q.Chapter));

            var rows = new List<TargetPass.TargetRow>();
            var excluded = new List<Exclusion>();

            for (int i = 0; i < ObjectiveTable.LaneRows.Length; i++)
            {
                var row = ObjectiveTable.LaneRows[i];

                // Which track question does this row's system answer to? Same split as
                // ConstraintLayerBridge.LaneStateFor: NGUs read the live NGU selector, everything
                // else the run's difficulty.
                var track = TargetPass.IsNguSystem(row.System) ? q.NguTrack : q.RunTrack;

                if (!ObjectiveTable.ChapterMatches(row.Chapter, q.Chapter))
                    continue;                                   // not this chapter: not an exclusion
                if (!ObjectiveTable.TrackMatches(row, track))
                    continue;                                   // not this track: not an exclusion

                // ---- the decided exclusions ------------------------------------------------------
                // A whole-system row has no id to rule on per-slot, so it is ruled on once, with
                // NoIndex — the value ObjectiveTable already uses for an unindexed row.
                if (row.Ids == ObjectiveTable.EveryId)
                {
                    // Never expanded — and the exclusion is recorded whatever Rule says, because a
                    // whole-system row that vanished silently is exactly the failure the EveryId
                    // rule exists to prevent. Rule denies every one of them today; if that guard is
                    // ever removed, this reports the row rather than dropping it without a word.
                    var whole = Rule(row, ObjectiveTable.NoIndex);
                    Drop(excluded, row, whole.Admitted
                        ? "whole-system row admitted by Rule but NOT EXPANDED — there is no id to " +
                          "materialise it with, and TargetPass.RowsFor selects on an exact index"
                        : whole.Rule);
                    continue;
                }

                for (int j = 0; j < row.Ids.Length; j++)
                {
                    int id = row.Ids[j];

                    var ruling = Rule(row, id);
                    if (!ruling.Admitted)
                    {
                        Drop(excluded, row, ruling.Rule, id);
                        continue;
                    }

                    // ⚠ COPIED, NEVER CONSTRUCTED. ToTargetRow carries terminality, kind and track
                    // across unchanged — there is deliberately no step here in which a precondition
                    // could become a target (ObjectiveTable.cs:116-122).
                    rows.Add(row.ToTargetRow(id));
                }
            }

            return new Table
            {
                Rows = rows.Count == 0 ? (IList<TargetPass.TargetRow>)NoRows : rows,
                Held = false,
                HeldReason = null,
                Excluded = excluded,
            };
        }

        private static Table Hold(string why)
        {
            return new Table
            {
                Rows = null,
                Held = true,
                HeldReason = why,
                Excluded = new List<Exclusion>(),
            };
        }

        private static void Drop(List<Exclusion> into, in ObjectiveTable.LaneRow row, string rule)
        {
            Drop(into, row, rule, int.MinValue);
        }

        private static void Drop(List<Exclusion> into, in ObjectiveTable.LaneRow row, string rule,
            int id)
        {
            into.Add(new Exclusion
            {
                System = row.System,
                Ids = id == int.MinValue ? row.Ids : new[] { id },
                Kind = row.Kind,
                Terminality = row.Terminality,
                ValueLow = row.ValueLow,
                ValueHigh = row.ValueHigh,
                Rule = rule,
            });
        }

        // ---- what the table can actually DO -------------------------------------------------------

        // The rows that can STOP a lane. `ConstraintLayer.WantFromAnswer` returns false for exactly
        // one shape — `WriteTarget` + `Satisfied` — and `WriteTarget` is reachable only through a row
        // that routed to Write. So this is the complete inventory of the produced table's power to
        // move allocation, computed rather than claimed: everything not in it holds the want open at
        // every level, forever.
        public static IList<TargetPass.TargetRow> Writable(IList<TargetPass.TargetRow> table)
        {
            var hits = new List<TargetPass.TargetRow>();
            if (table == null)
                return hits;
            for (int i = 0; i < table.Count; i++)
            {
                if (TargetPass.Route(table[i]).Disposition == TargetPass.Disposition.WriteTarget)
                    hits.Add(table[i]);
            }
            return hits;
        }
    }
}
