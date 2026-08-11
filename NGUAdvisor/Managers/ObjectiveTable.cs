using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE OBJECTIVE LAYER'S LANE TABLE — audit/23-objective-layer.md §2, transcribed into a typed
    // table, chapter-keyed. This is 37 §S5 B6's "objective layer as a real artifact: chapter-keyed
    // rows, a file or table, and a consumer"; B6 records TargetPass.GuideRows as "a reference subset
    // with no chapter field and no reader outside tests", which is exactly what this EXTENDS.
    //
    // ⚠ NOTHING CONSUMES THIS. It is data plus a reader (ObjectiveReader); no live allocation path
    // calls either. The objective layer is an ADDITIVE second membership source [OPERATOR] — it runs
    // alongside the profile JSON so the two can be compared before either is trusted. It does not
    // replace the profile, and this commit builds neither the comparison nor a consumer.
    //
    // A CODE TABLE, NOT A FILE [OPERATOR]. These rows are guidance the operator owns. A user-editable
    // file would inherit the profile JSON's defect classes — 36-profile-audit found twelve silently
    // skipped tokens and a duplicate ALLNGU that survived the life of the project. A typo here is a
    // compile error or a failing test; a typo in a JSON token is a silent no-op.
    //
    // RELATIONSHIP TO TargetPass (the T2c reconciliation): this table EXTENDS GuideRows, it does not
    // supersede it and does not sit beside it.
    //   - It REUSES TargetPass's vocabulary outright — RowKind, Track, Terminality, TargetRow,
    //     SilenceSpec, SilenceLedger. A second Terminality enum is precisely how a `precondition`
    //     comes to be read as a `target`, so there is deliberately only one.
    //   - It ADDS the two fields TargetRow's own header defers to the objective layer: `Chapter`, and
    //     an id GROUP (23 states "0-4", "0, 1", "all 16" as single rows; TargetRow.Index is scalar).
    //   - GuideRows STAYS. It is the fixture Pass 3's semantics are pinned against — ConstraintLayer
    //     Tests:73, Pass3WiringTests:459/:491 and nine TargetPassTests assertions read it — and it is
    //     a deliberately minimal subset, which is what makes "exactly one standing terminal" a
    //     checkable claim about the ROUTER. This table is the full §2 transcription; the subset is
    //     re-derivable from it but is NOT regenerated here (that would be a behaviour change to
    //     Pass 3's fixture, and this commit changes nothing).
    //
    // PROVENANCE. Every row is PLAYER CONSENSUS, retrieved by 23 from sayolove/ngu-guide @ aa50a4f.
    // reference/decomp-full/ remains the authority; where a decomp value exists for the same quantity
    // 23 cites it alongside. Nothing here is adjudicated. Cites are by page, always — [GUIDE ch.5
    // §Hacks] and [GUIDE mechanics/hacks] are different provenance and are never merged.
    //
    // ⚠ SILENCE IS A ROW VALUE, NOT AN OMISSION, and a silence is NOT a zero. The silences live in
    // TargetPass.SilenceLedger (already complete against 23 §7) and surface through
    // ObjectiveReader.Slot. In the game, target 0 is the UNSET sentinel and funds FOREVER
    // ([DECOMP] AllNGUController.cs:1311-1314); -1 is the never-fund marker. There is no path from a
    // silence in this table to a numeric write.
    public static class ObjectiveTable
    {
        // A row the guide states without a chapter gate — a mechanics page, or an explicitly standing
        // rule. It matches EVERY chapter query, because that is what "standing" means. Also the
        // "return everything" value on the query side. Chapters proper are 1-8.
        public const int ChapterAny = 0;

        // 23 §0.2: augments emit no indexed rows ("n/a"), and several rules are stated for a whole
        // system at once. Such a row carries NoIndex and Ids == EveryId, and TargetPass.RowsFor —
        // which selects on an exact index — can therefore never hand it to a lane. That is the
        // intended fail-closed behaviour: an unindexed rule must not speak for a specific slot.
        public const int NoIndex = -1;

        // Ids == null means "the guide states this for the whole system" (23's "all 16" rows).
        public static readonly int[] EveryId = null;

        // ---- one row of §2, in memory ------------------------------------------------------------
        // 23 §0.1's nine fields plus `Chapter`, with `index` widened to an id GROUP and `value` split
        // into a numeric pair (kind=level) and the guide's own words (every other kind).
        public struct LaneRow
        {
            // 1-8, or ChapterAny for a mechanics-page / standing row.
            public int Chapter;

            // 23's slugs, as TargetPass.Sys* — the two files share one vocabulary.
            public string System;

            // The decomp ids this row speaks for (23 §0.2). null == every id in the system.
            public int[] Ids;

            public TargetPass.Track Track;

            // 23 states the track as "all" for four rows (§2.1 the one-augment rule, §2.4 the Wandoos
            // dumps, §2.6 the OS selector). Distinct from TrackNeutral, which is TargetRow's
            // STRUCTURAL claim that the game stores one field with no per-track split (TM only).
            // AllTracks means the guide's sentence is track-agnostic, not that the game's storage is.
            public bool AllTracks;

            public bool TrackNeutral;

            public TargetPass.RowKind Kind;
            public TargetPass.Terminality Terminality;

            // A RANGE STAYS A RANGE (23 §0.1, T1c). "2-3k" is ValueLow 2000 / ValueHigh 3000 and is
            // never averaged, interpolated, or collapsed to an end. Scalars set both equal. Both are
            // 0 on every non-level row — and 0 can never be written, because WriteTargetGuard refuses
            // the game's unset sentinel.
            public long ValueLow;
            public long ValueHigh;

            // The guide's own words for `value` when the value is not a number — the rate, the rule,
            // or the predicate. Present on level rows too where the guide qualified the number
            // ("5m+", "Don't stop at Level 49").
            public string ValueText;

            // Non-null marks a row that holds ONLY inside a named campaign, never as a standing
            // target (23 §2.5's 100LC rows).
            public string CampaignScope;

            // Non-null names the progression condition that LIFTS this row's stop. Terminality is
            // the curve's shape; this is progression state. See TargetPass's LiftGate block.
            public string LiftGate;

            public string Objective;
            public string Cite;

            // Set when 23 stated this as ONE row spanning several ids, so a consumer that expands the
            // group can say so. Surfacing only.
            public string GroupNote;

            public bool Covers(int id)
            {
                return Ids == null || Array.IndexOf(Ids, id) >= 0;
            }

            // Materialise this row for ONE id, in the shape Pass 3 consumes. Terminality, kind and
            // track cross unchanged — there is no translation step in which a precondition could
            // become a target.
            //
            // An AllTracks row materialises with Track.Unspecified, which RouteLevel refuses as
            // "row without a track is unusable (23 §0.1)". Every AllTracks row in §2 is a predicate
            // or a rate, so this costs nothing and fails closed if one is ever added.
            public TargetPass.TargetRow ToTargetRow(int id)
            {
                return new TargetPass.TargetRow
                {
                    System = System,
                    Index = id,
                    Track = AllTracks ? TargetPass.Track.Unspecified : Track,
                    TrackNeutral = TrackNeutral,
                    Kind = Kind,
                    Terminality = Terminality,
                    ValueLow = ValueLow,
                    ValueHigh = ValueHigh,
                    CampaignScope = CampaignScope,
                    LiftGate = LiftGate,
                    Objective = Objective,
                    Cite = Cite,
                };
            }

            public bool IsRange { get { return ValueLow != ValueHigh; } }
        }

        // Shorthand for the four id groups 23 names repeatedly.
        private static readonly int[] Pawgs = { 0, 1, 3, 5 };            // Power Augments Wandoos Gold
        private static readonly int[] First5EvilE = { 0, 1, 2, 3, 4 };
        private static readonly int[] First2EvilM = { 0, 1 };
        private static readonly int[] AtPowerToughness = { 0, 1 };
        private static readonly int[] AtAll3 = { 0, 1, 2 };
        private static readonly int[] AtWandoosDumps = { 3, 4 };

        // =========================================================================================
        // §2 THE TARGET TABLE
        // =========================================================================================
        //
        // ⚠ NO NGU ROW IS EVER `terminal` EXCEPT ONE, and that one is contested. Amendment 21 §1
        // decides "NGUs never take a target level. On any track, in any pool, at any chapter. Every
        // NGU lane is a rate lane." 23 §2.2 transcribes Respawn 401 as TERMINAL from the guide's own
        // "don't invest further yet", and the mechanics agree that the post-400 branch saturates.
        // BOTH ARE RECORDED AND NEITHER IS ADJUDICATED HERE: the row is transcribed as 23 states it
        // (which is also what GuideRows ships and TargetPassTests pins), and amendment 21 §1's
        // contrary decision is noted on the row. Nothing consumes either, so nothing turns on it in
        // this commit.
        //
        // ⚠ AMENDMENT 21 §4 RECLASSIFIES THE PAWG LADDER as campaign readiness thresholds — "these
        // rows were never cascade targets" — and observes the schema already had it right: every rung
        // is `precondition`, which Route refuses to write. Transcribed unchanged.
        public static readonly LaneRow[] LaneRows =
        {
            // -------------------------------------------------------------------------------------
            // §2.1 augments — 14 slots x 3 tracks, ZERO level rows on every track.
            // The guide publishes the full 7-pair constant table and NO target column. What it gives
            // instead is a SELECTION RULE, restated four times and never once as a level. These four
            // rows ARE that restatement; they carry NoIndex because 23 §0.2 emits no augment index.
            // The operator's artifact for this system is A CHOSEN AUGMENT, not a level — a different
            // shape from what augmentTarget consumes (23 §7.1 S1).
            // -------------------------------------------------------------------------------------
            new LaneRow { Chapter = 1, System = TargetPass.SysAugments, Ids = EveryId,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"Focus the most expensive augmentation you can still afford/finish " +
                            "within 30m\"; \"Upgrades cost too much gold to be worth focusing just " +
                            "yet\" — a selector over cost and rebirth length, no level",
                Objective = "augment progression", Cite = "[GUIDE ch.1 §Augmentations]" },

            new LaneRow { Chapter = 2, System = TargetPass.SysAugments, Ids = EveryId,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"Focus on Safety Scissors / Danger Scissors... Keep running Scissors " +
                            "until you can start idling A Very Strange Place, when you should be " +
                            "able to transition to running only Milk Infusion / Drinking the Milk " +
                            "Too\" — terminates on a ZONE IDLE THRESHOLD, not a level",
                Objective = "augment switch at AVSP",
                Cite = "[GUIDE ch.2 §Augmentations / Time Machine Changes]" },

            new LaneRow { Chapter = 5, System = TargetPass.SysAugments, Ids = EveryId,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"Run your best augment until the 3 hour mark. Check GO to find your " +
                            "best augment\" — a time box plus an EXTERNAL SOLVER",
                Objective = "24h RB schedule, 00:30-03:00",
                Cite = "[GUIDE ch.5 §24HR RB Schedule]" },

            new LaneRow { Chapter = ChapterAny, System = TargetPass.SysAugments, Ids = EveryId,
                AllTracks = true, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"It's more efficient to focus on ONE augmentation than running " +
                            "multiple\" (they stack additively) — a REJECTION OF THE CASCADE SHAPE",
                Objective = "one augment, not a ladder", Cite = "[GUIDE mechanics/augmentation]" },

            // -------------------------------------------------------------------------------------
            // §2.2 ngu-energy, track NORMAL — the guide's most target-shaped material.
            // -------------------------------------------------------------------------------------

            // id 0 Augments
            new LaneRow { Chapter = 2, System = TargetPass.SysNguEnergy, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"Focus on NGU Power a and NGU Gold\" — an ORDER, no level (id 0 is " +
                            "not named)",
                Objective = "offline E/M dump", Cite = "[GUIDE ch.2 §NGU]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 500, ValueHigh = 500,
                ValueText = "PAWG rung 1", Objective = "Mini-CBlock prep",
                Cite = "[GUIDE ch.3 §NGUs]", GroupNote = "PAWG ladder rung 1 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 5000, ValueHigh = 5000,
                ValueText = "PAWG rung 2, with the ceiling clause \"Not advised to prep beyond 5k, " +
                            "the extra time investment in NGUs does not help much\"",
                Objective = "CBlock1 prep", Cite = "[GUIDE ch.3 §CBlock1]",
                GroupNote = "PAWG ladder rung 2 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 150000, ValueHigh = 150000,
                ValueText = "PAWG rung 3", Objective = "CBlock2 prep",
                Cite = "[GUIDE ch.4 §Adventure Zones · Post-v2]",
                GroupNote = "PAWG ladder rung 3 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 5000000, ValueHigh = 5000000,
                ValueText = "PAWG rung 4 — \"5m+\", \"Highly Recommended\"", Objective = "Evil entry",
                Cite = "[GUIDE ch.4 §Evil Prep]",
                GroupNote = "PAWG ladder rung 4 of 4 (ids 0,1,3,5)" },

            // id 1 Wandoos NGU
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 1 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 500, ValueHigh = 500,
                ValueText = "PAWG rung 1", Objective = "Mini-CBlock prep",
                Cite = "[GUIDE ch.3 §NGUs]", GroupNote = "PAWG ladder rung 1 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 1 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 5000, ValueHigh = 5000,
                ValueText = "PAWG rung 2 (same ceiling clause)", Objective = "CBlock1 prep",
                Cite = "[GUIDE ch.3 §CBlock1]", GroupNote = "PAWG ladder rung 2 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 1 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 150000, ValueHigh = 150000,
                ValueText = "PAWG rung 3", Objective = "CBlock2 prep", Cite = "[GUIDE ch.4 §Post-v2]",
                GroupNote = "PAWG ladder rung 3 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 1 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 5000000, ValueHigh = 5000000,
                ValueText = "PAWG rung 4 — \"5m+\"", Objective = "Evil entry",
                Cite = "[GUIDE ch.4 §Evil Prep]", GroupNote = "PAWG ladder rung 4 of 4 (ids 0,1,3,5)" },

            // id 2 Respawn — THE one standing terminal in the whole guide (23 §0.4, NGU-scoped).
            // ⚠ THERE IS NO id 2 RESPAWN LEVEL ROW, AND ITS ABSENCE IS THE DECISION. Removed
            // 2026-08-07 by [OPERATOR], who is the authority on what the guide's number MEANT:
            //
            //   "the 401 was in the guide because it's the 'best' for where the user is at at the
            //    time. the diminishing returns comes for it at 10,000 levels. and then the advisor
            //    already calculates what is the best use of energy already. So it's a moot point...
            //    if it's going to have good gains, then it's worth investing in, just like the
            //    other NGUs."
            //
            // So 401 was never a property of the curve — it was the best use of energy AT ONE
            // MOMENT, and computing the best use of energy is what the allocator already does. A
            // stop written here would freeze a lane the allocator is better placed to judge every
            // tick. The post-400 branch does saturate ([DECOMP] AllNGUController.cs:449-458) and
            // that mechanic is unchanged — saturating is simply not the same as worthless, and the
            // returns the operator is pricing turn at 10,000, not 401.
            //
            // ⚠ DO NOT RE-ADD IT FROM THE GUIDE. [GUIDE ch.3 §NGUs] still says "don't invest
            // further yet", and 23 §0.4 still transcribes it as the sole standing terminal. Both
            // are faithful transcriptions of a sentence the operator has ruled situational. A
            // future pass reading only 23 will think this row is missing. It is not.
            //
            // History, so the removal is not re-litigated: amendment 21 §1 said NGUs never take a
            // target level anywhere; amendment 34 §3 narrowed that to admit this one row; c3f8122
            // made it conditional with a LiftGate. All three were attempts to keep a number the
            // operator has now removed the premise for. 21 §1's blanket rule stands unqualified.

            // id 3 Gold
            new LaneRow { Chapter = 2, System = TargetPass.SysNguEnergy, Ids = new[] { 3 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"Focus on NGU Power a and NGU GOLD\" — an order, no level",
                Objective = "fund the Time Machine", Cite = "[GUIDE ch.2 §NGU]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 3 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 500, ValueHigh = 500,
                ValueText = "PAWG rung 1", Objective = "Mini-CBlock prep",
                Cite = "[GUIDE ch.3 §NGUs]", GroupNote = "PAWG ladder rung 1 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 3 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 5000, ValueHigh = 5000,
                ValueText = "PAWG rung 2", Objective = "CBlock1 prep", Cite = "[GUIDE ch.3 §CBlock1]",
                GroupNote = "PAWG ladder rung 2 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 3 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 150000, ValueHigh = 150000,
                ValueText = "PAWG rung 3", Objective = "CBlock2 prep", Cite = "[GUIDE ch.4 §Post-v2]",
                GroupNote = "PAWG ladder rung 3 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 3 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 5000000, ValueHigh = 5000000,
                ValueText = "PAWG rung 4 — \"5m+\"", Objective = "Evil entry",
                Cite = "[GUIDE ch.4 §Evil Prep]", GroupNote = "PAWG ladder rung 4 of 4 (ids 0,1,3,5)" },

            // id 4 Adventure a — the other half of §0.5. Same word "softcap", OPPOSITE terminality:
            // the post-1000 branch is UNBOUNDED sqrt(level)*31.7*factor
            // ([DECOMP] AllNGUController.cs:568-572), and the guide says "keep going" outright.
            new LaneRow { Chapter = 2, System = TargetPass.SysNguEnergy, Ids = new[] { 4 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 250, ValueHigh = 250,
                ValueText = "\"250+\"", Objective = "T4 LRB (AVSP -> Mega -> T4)",
                Cite = "[GUIDE ch.2 §LRB to Mega/T4]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 4 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 2000, ValueHigh = 3000,
                ValueText = "\"2-3k\" — A RANGE; it stays a range",
                Objective = "Beardverse unlock", Cite = "[GUIDE ch.3 §Beardverse]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 4 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 60000, ValueHigh = 80000,
                ValueText = "\"60-80k\" — A RANGE", Objective = "BDW -> T6 LRB",
                Cite = "[GUIDE ch.3 §BDW/BAE]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 4 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 1000, ValueHigh = 1000,
                ValueText = "softcap 1000, with an explicit \"When you hit softcaps, KEEP GOING. " +
                            "You will need adventure stats all game\" — ⚠ NOT TERMINAL",
                Objective = "adventure stats, all game",
                Cite = "[GUIDE ch.3 §NGUs] + [GUIDE mechanics/ngu]; " +
                       "[DECOMP AllNGUController.cs:568-572]",
                GroupNote = "23 §0.5: the same word \"softcap\" as Respawn 401, the opposite answer" },

            // id 5 Power a
            new LaneRow { Chapter = 2, System = TargetPass.SysNguEnergy, Ids = new[] { 5 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"FOCUS ON NGU POWER A and NGU Gold\" — an order",
                Objective = "kill bosses", Cite = "[GUIDE ch.2 §NGU]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 5 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 500, ValueHigh = 500,
                ValueText = "PAWG rung 1", Objective = "Mini-CBlock prep",
                Cite = "[GUIDE ch.3 §NGUs]", GroupNote = "PAWG ladder rung 1 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 5 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 5000, ValueHigh = 5000,
                ValueText = "PAWG rung 2", Objective = "CBlock1 prep", Cite = "[GUIDE ch.3 §CBlock1]",
                GroupNote = "PAWG ladder rung 2 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 5 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 150000, ValueHigh = 150000,
                ValueText = "PAWG rung 3", Objective = "CBlock2 prep", Cite = "[GUIDE ch.4 §Post-v2]",
                GroupNote = "PAWG ladder rung 3 of 4 (ids 0,1,3,5)" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 5 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 5000000, ValueHigh = 5000000,
                ValueText = "PAWG rung 4 — \"5m+\"", Objective = "Evil entry",
                Cite = "[GUIDE ch.4 §Evil Prep]", GroupNote = "PAWG ladder rung 4 of 4 (ids 0,1,3,5)" },

            // id 6 Drop Chance
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 6 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"split half your energy in Adv/DC. Splitting 50/50 will naturally " +
                            "lead to a ~10:1 Adv:DC level ratio\". The alternative \"specialized " +
                            "approach\" alternates Adv -> DC on zone entry",
                Objective = "gear drops", Cite = "[GUIDE ch.3 §NGUs]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 6 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 1000, ValueHigh = 1000,
                ValueText = "softcap 1000, the same \"keep going\" clause as id 4 — ⚠ NOT TERMINAL",
                Objective = "drop chance, all game",
                Cite = "[GUIDE ch.3 §NGUs] + [GUIDE mechanics/ngu]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysNguEnergy, Ids = new[] { 6 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"the drop chance sucks. Beardverse base DC drops to 1%. Run NGU DC if " +
                            "needed\"; \"Both BDW and BAE have terrible drop rates, so it may be " +
                            "helpful to run NGU DC\"",
                Objective = "zone farming", Cite = "[GUIDE ch.3 §Beardverse / §BDW]" },

            // ids 7, 8 — the GO bonus-change rule's first two priorities, per-id.
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 7 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "GO rule priority 1: \"NGU E/M NGU with >1.05x\" -> put all E/M there " +
                            "for an hour",
                Objective = "NGU speed", Cite = "[GUIDE ch.4 §NGU Priority]" },
            new LaneRow { Chapter = 4, System = TargetPass.SysNguEnergy, Ids = new[] { 8 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "GO rule priority 2: \"NGU Adventure Beta/PP with >1.05x\"",
                Objective = "PP gain", Cite = "[GUIDE ch.4 §NGU Priority]" },

            // -------------------------------------------------------------------------------------
            // §2.3 ngu-energy and ngu-magic, track EVIL.
            // ⚠ 23 §0.3's single most consequential fact: [DECOMP AllNGUController.cs:1307-1374]
            // terminates a lane on level >= target — a LEVEL comparison, per track — and the guide's
            // entire Evil NGU policy is expressed as rate, time and predicate. The three Evil rows
            // that ARE levels are preconditions or AMBIGUOUS; none is terminal.
            // -------------------------------------------------------------------------------------
            new LaneRow { Chapter = 5, System = TargetPass.SysNguEnergy, Ids = new[] { 0 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"23:00-24:00 Evil NGU Phase — Focus NGU Augments and NGU Ygg/EXP to " +
                            "push towards T7\"",
                Objective = "push T7", Cite = "[GUIDE ch.5 §24HR RB Schedule]" },
            new LaneRow { Chapter = 6, System = TargetPass.SysNguEnergy, Ids = First5EvilE,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"Aim to BB the first 5 Evil eNGUs and Ygg/EXP\"; BB = 50 levels/s " +
                            "[GUIDE mechanics/general-info §Ticks]",
                Objective = "post-HD1 standing", Cite = "[GUIDE ch.6 §Post-HD1 / FAQ]",
                GroupNote = "23 states ids 0-4 as ONE row (Aug/Wand/Resp/Gold/Adv a)" },
            new LaneRow { Chapter = 5, System = TargetPass.SysNguEnergy, Ids = new[] { 4 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"04:00-21:00: Farm Meta with full NGU gear, focus Adventure a + " +
                            "Adventure b\"",
                Objective = "T8 LRB", Cite = "[GUIDE ch.5 §LRB to T8]" },
            new LaneRow { Chapter = 6, System = TargetPass.SysNguEnergy, Ids = new[] { 4 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"Keep NGU Adventure a BB'd all rebirth\"",
                Objective = "T9 LRB", Cite = "[GUIDE ch.6 §T9 LRB]" },
            new LaneRow { Chapter = 5, System = TargetPass.SysNguEnergy, Ids = new[] { 7 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 1000, ValueHigh = 1000,
                ValueText = "\"Get both NGU E/M NGU to SOFTCAP, THEN NGU PP and Ygg/EXP for 2-3 " +
                            "hours\" — the \"then\" is what makes it reach-before, not stop-at",
                Objective = "T8 LRB day 1, 01:00-04:00", Cite = "[GUIDE ch.5 §LRB to T8]" },
            new LaneRow { Chapter = 5, System = TargetPass.SysNguEnergy, Ids = new[] { 8 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Ambiguous, ValueLow = 1000, ValueHigh = 1000,
                ValueText = "\"21:00-24:00: SOFTCAP NGU PP and split Magic into NGU Ygg/EXP\". The " +
                            "guide gives NO post-softcap PP instruction anywhere, so stop vs " +
                            "reach-then-continue is UNDETERMINED BY ITS OWN TEXT",
                Objective = "T8 LRB day 1, tail",
                Cite = "[GUIDE ch.5 §LRB to T8]; 23 §2.3",
                GroupNote = "the only AMBIGUOUS row in §2 — not guessed" },
            new LaneRow { Chapter = 5, System = TargetPass.SysNguMagic, Ids = new[] { 5 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 1000, ValueHigh = 1000,
                ValueText = "the other half of \"both NGU E/M NGU to softcap\"",
                Objective = "T8 LRB", Cite = "[GUIDE ch.5 §LRB to T8]" },
            new LaneRow { Chapter = 6, System = TargetPass.SysNguMagic, Ids = First2EvilM,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"BB the first 5 Evil eNGUs and the FIRST 2 EVIL mNGUs all rebirth\"",
                Objective = "post-HD1 standing", Cite = "[GUIDE ch.6 §Post-HD1]",
                GroupNote = "23 states ids 0-1 as ONE row (Ygg / Exp)" },
            new LaneRow { Chapter = 5, System = TargetPass.SysNguMagic, Ids = new[] { 6 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"focus Adventure a + ADVENTURE B\" (04:00-21:00)",
                Objective = "T8 LRB", Cite = "[GUIDE ch.5 §LRB to T8]" },
            new LaneRow { Chapter = 6, System = TargetPass.SysNguMagic, Ids = new[] { 6 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"BB NGU Adventure b for as long as possible\"",
                Objective = "T9 LRB", Cite = "[GUIDE ch.6 §T9 LRB]" },

            // The two "all 16" rows. 23 states each ONCE, spanning both pools; System is a required
            // field, so each is carried per pool with Ids == EveryId. Both are time/predicate and
            // neither can ever route to a target.
            new LaneRow { Chapter = 5, System = TargetPass.SysNguEnergy, Ids = EveryId,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"run NORMAL NGUs for most of your rebirth, then run EVIL NGUs for the " +
                            "LAST HOUR... As you defeat T7 versions, run an EXTRA HOUR PER T7 " +
                            "VERSION DEFEATED\"",
                Objective = "the two-track split", Cite = "[GUIDE ch.5 §Evil NGUs]",
                GroupNote = "23 states this once for all 16 NGUs; carried per pool" },
            new LaneRow { Chapter = 5, System = TargetPass.SysNguMagic, Ids = EveryId,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"run NORMAL NGUs for most of your rebirth, then run EVIL NGUs for the " +
                            "LAST HOUR... an EXTRA HOUR PER T7 VERSION DEFEATED\"",
                Objective = "the two-track split", Cite = "[GUIDE ch.5 §Evil NGUs]",
                GroupNote = "23 states this once for all 16 NGUs; carried per pool" },
            new LaneRow { Chapter = 6, System = TargetPass.SysNguEnergy, Ids = EveryId,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Predicate,
                ValueText = "capability gate: \"the EVIL -> NORMAL NGU QUIRK (15k QP)... will let " +
                            "you level BOTH... getting rid of any downsides to running Evil NGUs\" " +
                            "— REMOVES THE TIME SPLIT ENTIRELY",
                Objective = "post-HD1", Cite = "[GUIDE ch.6 §Quirk Order / §Post-HD1]",
                GroupNote = "23 states this once for all 16 NGUs; carried per pool" },
            new LaneRow { Chapter = 6, System = TargetPass.SysNguMagic, Ids = EveryId,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Predicate,
                ValueText = "capability gate: \"the EVIL -> NORMAL NGU QUIRK (15k QP)... will let " +
                            "you level BOTH\" — removes the time split entirely",
                Objective = "post-HD1", Cite = "[GUIDE ch.6 §Quirk Order / §Post-HD1]",
                GroupNote = "23 states this once for all 16 NGUs; carried per pool" },

            // -------------------------------------------------------------------------------------
            // §2.4 at — 5 ids. The Wandoos dumps (3, 4) emit no level on any track.
            // -------------------------------------------------------------------------------------
            new LaneRow { Chapter = 2, System = TargetPass.SysAt, Ids = AtPowerToughness,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 2000, ValueHigh = 3000,
                ValueText = "\"2-3k\" — A RANGE", Objective = "T4 LRB",
                Cite = "[GUIDE ch.2 §LRB to Mega/T4]",
                GroupNote = "23 states ids 0,1 as one row (Adv Toughness+ / Power+)" },
            new LaneRow { Chapter = 3, System = TargetPass.SysAt, Ids = AtPowerToughness,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"push Advanced Training until ~2.5m Power/1.8m Toughness\" — " +
                            "terminates on an ADVENTURE STAT, not an AT level. ⚠ the guide typos " +
                            "the destination as \"Breadverse\" (a Sadistic zone); the intent is " +
                            "BEARDVERSE, whose zone-list idle is exactly 2.5M/1.8M (23 §0.6)",
                Objective = "Beardverse idle", Cite = "[GUIDE ch.3 §Beardverse]",
                GroupNote = "23 states ids 0,1 as one row" },
            new LaneRow { Chapter = 3, System = TargetPass.SysAt, Ids = AtPowerToughness,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition,
                ValueLow = 60000, ValueHigh = 80000,
                ValueText = "\"60-80k\" — A RANGE", Objective = "BDW -> T6 LRB",
                Cite = "[GUIDE ch.3 §BDW/BAE]", GroupNote = "23 states ids 0,1 as one row" },
            new LaneRow { Chapter = 3, System = TargetPass.SysAt, Ids = new[] { 2 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 5000, ValueHigh = 5000,
                ValueText = "5,000 — the 99% rung of the Block Damage curve",
                Objective = "BDW -> T6 LRB", Cite = "[GUIDE ch.3 §BDW/BAE]" },
            // ⚠ TERMINAL AT 100,000 BY [OPERATOR] RULING, NOT BY THE CURVE — and the distinction is
            // the whole reason this comment exists. 2026-08-07: "the Block AT is a hard cap at
            // 100,000 and should never be capped lower."
            //
            // ⚠ DO NOT "CORRECT" THIS BACK BY READING AMENDMENT 35 §1. That section is right: the
            // Block curve is 0.5 / (1 + levelFactor × Level), asymptotic, with no branch change and
            // no clamp, so it NEVER mechanically saturates and every level buys something,
            // arbitrarily little. A reader who derives terminality from the curve will conclude
            // this row cannot be terminal and will be reasoning correctly from the wrong premise.
            // THE LINE IS DRAWN BY THE OPERATOR, AND AN OPERATOR MAY DRAW A LINE ON A CURVE THAT
            // HAS NONE. That is what amendment 35 §3 was reaching for with `diminishing`; the
            // ruling settles this row without needing the kind, and 35 §3 stays open for the
            // general case.
            //
            // ⚠ IT WAS Precondition UNTIL NOW, which is why 35 §3 recorded the table and amendment
            // 24 disagreeing — "both were reaching for a kind that did not exist". The disagreement
            // is resolved by the ruling, not by the vocabulary.
            //
            // ⚠ THE STATED REASON DOES NOT MATCH THE NUMBER, AND THAT IS RECORDED ON PURPOSE.
            // [OPERATOR] gave the justification as "gives the user the 99% block damage reduction".
            // 99% IS THE 5,000 RUNG, not this one: the guide's own ladder is 90% at 400, 99% at 5k,
            // [broken rung], 99.99% at 500k (BlockCurveRungs below), and LevelPlanner's ceil(49/f)
            // targets exactly 99%. 100,000 sits between the 99.9% and 99.99% rungs — amendment 24 §5
            // independently records [OPERATOR] describing this cap as "caps at 99.95% block", which
            // is the figure that actually belongs to 100,000.
            //
            // THE RULING STANDS ON ITS NUMBER, NOT ON ITS REASON. It was given twice ("the bridges
            // 100,000 is the correct one. the 5000 is not acceptable. this goes for every
            // difficulty"; "a hard cap at 100,000 and should never be capped lower"). ⚠ DO NOT
            // "CORRECT" 100,000 BACK TO 5,000 ON THE STRENGTH OF THE 99% REASON — that is a
            // rediscovery of this mismatch, not a bug find. The number is the ruling.
            //
            // ⚠ DO NOT "CORRECT" IT BY READING AMENDMENT 35 §1 EITHER. That section is right: the
            // Block curve is 0.5 / (1 + levelFactor × Level), asymptotic, with no branch change and
            // no clamp, so it NEVER mechanically saturates and every level buys something,
            // arbitrarily little. A reader who derives terminality from the curve will conclude
            // these rows cannot be terminal and will be reasoning correctly from the wrong premise.
            // THE LINE IS DRAWN BY THE OPERATOR, AND AN OPERATOR MAY DRAW A LINE ON A CURVE THAT
            // HAS NONE. That is what amendment 35 §3 was reaching for with `diminishing`; the
            // ruling settles these rows without needing the kind, and 35 §3 stays open for the
            // general case.
            //
            // ⚠ IT WAS Precondition UNTIL 08b4344, which is why 35 §3 recorded the table and
            // amendment 24 disagreeing — "both were reaching for a kind that did not exist". The
            // disagreement is resolved by the ruling, not by the vocabulary.
            //
            // ⚠ THIS ROW IS STILL EVIL-TRACK ONLY, AND THE "every difficulty" HALF OF THE RULING IS
            // NOT IN THIS TABLE. It is carried by the LIVE WRITER instead — LevelPlanner writes
            // ObjectiveTable.AtBlockHardCapLevel into levelTarget[2], which is ONE field per slot
            // with NO per-track split (ConstraintLayerBridge.LaneStateFor reads
            // advancedTraining.level[i] with no track branch, unlike the NGU lanes right above it),
            // so that single write already holds on Normal, Evil and Sadistic alike.
            //
            // ADDING per-track rows here was attempted and REVERTED, deliberately. A Sadistic row
            // falsifies a documented invariant rather than a count: ObjectiveLayerTests'
            // `The_only_levels_reaching_a_sadistic_query_are_the_track_neutral_tm_rows` fails with
            // its own message, "a non-track-neutral row reached a Sadistic query — 23 §2.5 says
            // SILENT", alongside `Augments_have_no_level_in_all_42_slots_and_sadistic_in_every_slot`
            // and §2's "Sadistic emits NOTHING". Those are not fixtures to renumber.
            //
            // AND THE SCHEMA POINTS SOMEWHERE ELSE. `TrackNeutral` is exactly "the game stores one
            // field with no per-track split", which is TRUE of AT and is the flag that test says a
            // Sadistic query may legitimately see. So the likely-correct model is ONE TrackNeutral
            // row REPLACING this one — not three per-track rows. That re-models AT's track
            // structure table-wide, contradicts this row's guide citation (ch.5 is an Evil-specific
            // sentence), and is an [OPERATOR] design call. REPORTED, NOT TAKEN.
            new LaneRow { Chapter = 5, System = TargetPass.SysAt, Ids = new[] { 2 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = AtBlockHardCapLevel, ValueHigh = AtBlockHardCapLevel,
                ValueText = "\"First 24H RB: Get 100k AT Block levels, THEN AT Power...\" — and " +
                            "[OPERATOR] 2026-08-07: a HARD CAP at 100,000, never capped lower",
                Objective = "first Evil 24h rebirth — hard cap",
                Cite = "[GUIDE ch.5 §24HR RB Schedule]; [OPERATOR] hard cap 100,000",
                GroupNote = "TERMINAL by operator ruling, not by curve shape — amendment 35 §1's " +
                            "asymptote is unchanged and 35 §3 stays open" },
            new LaneRow { Chapter = 5, System = TargetPass.SysAt, Ids = new[] { 2 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"With AT Block levels (>99%) and the right timing, almost all damage " +
                            "can be mitigated\" => >= 5,000 via the Block Damage curve",
                Objective = "T8: The Godmother", Cite = "[GUIDE ch.5 §T8: The Godmother]" },
            new LaneRow { Chapter = 5, System = TargetPass.SysAt, Ids = AtPowerToughness,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"then AT Power UNTIL YOU HAVE ENOUGH ADV POWER TO SNIPE EV EXPLODER " +
                            "(5-7T pow)\" — an ADVENTURE-STAT terminal, not an AT level",
                Objective = "GPS for the EXP digger", Cite = "[GUIDE ch.5 §24HR RB Schedule]",
                GroupNote = "23 states ids 0,1 as one row" },
            new LaneRow { Chapter = 5, System = TargetPass.SysAt, Ids = AtPowerToughness,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"Next few RBs: Run AT P/T until you can IDLE EV for accessories " +
                            "only\"; \"When you reach Boss 115/120, BB AT P/T FOR THE ENTIRE " +
                            "REBIRTH to prep for T7\" (the second clause is a rate)",
                Objective = "EV idle -> T7", Cite = "[GUIDE ch.5 §24HR RB Schedule]",
                GroupNote = "23 states ids 0,1 as one row" },
            new LaneRow { Chapter = 5, System = TargetPass.SysAt, Ids = AtAll3,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"In general, you can run AT with <40% OF YOUR ENERGY CAP. PRIORITIZE " +
                            "NGUs FIRST\". ⚠ written for a PRE-WISH-190 world and never retracted: " +
                            "once wish 190 is owned, progressPerTick returns 1f INDEPENDENT of " +
                            "allocated energy ([DECOMP AdvancedTrainingController.cs:151])",
                Objective = "budget ceiling", Cite = "[GUIDE ch.5 §24HR RB Schedule]",
                GroupNote = "23 states ids 0,1,2 as one row" },
            new LaneRow { Chapter = 6, System = TargetPass.SysAt, Ids = AtAll3,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Rate,
                ValueText = "\"BB AT and Normal NGUs all rebirth\" (pre-Typo)",
                Objective = "standing", Cite = "[GUIDE ch.6 FAQ]",
                GroupNote = "23 states ids 0,1,2 as one row" },
            new LaneRow { Chapter = 5, System = TargetPass.SysAt, Ids = AtWandoosDumps,
                AllTracks = true, Kind = TargetPass.RowKind.Predicate,
                ValueText = "\"Run Wandoos AT until CHEAP TO RUN WANDOOS 98\" — a COST predicate. " +
                            "[GUIDE mechanics/advanced-training] records only \"The Wandoos skills " +
                            "have double the cost\"",
                Objective = "boot Wandoos", Cite = "[GUIDE ch.5 §24HR RB Schedule]",
                GroupNote = "23 states ids 3,4 as one row, track \"all\"" },

            // -------------------------------------------------------------------------------------
            // §2.5 tm-speed and tm-goldmulti. TM is TRACK-NEUTRAL where the game stores one field.
            // -------------------------------------------------------------------------------------
            new LaneRow { Chapter = 1, System = TargetPass.SysTmSpeed, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Rate,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 0, ValueHigh = 0,
                ValueText = "rate = 0: \"This early, it's too difficult to get meaningful levels in " +
                            "TM, so IT'S NOT ADVISED TO LEVEL\" — LIFTED by ch.2's trigger below",
                Objective = "do not level yet", Cite = "[GUIDE ch.1 §Time Machine]" },
            new LaneRow { Chapter = 2, System = TargetPass.SysTmSpeed, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Predicate,
                ValueText = "enable: \"ONCE YOU CAN KILL T1 CONSISTENTLY, you can also start " +
                            "putting E/M into Time Machine at the beginning of rebirths\"",
                Objective = "gold production",
                Cite = "[GUIDE ch.2 §Augmentations / Time Machine Changes]" },
            // ⚠ THE TRAP ROW. The guide names the number, explains what happens at 50, and says
            // DON'T STOP. Writing speedTarget = 49 would implement the number and invert the advice.
            new LaneRow { Chapter = 2, System = TargetPass.SysTmSpeed, Ids = new[] { 0 },
                TrackNeutral = true, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 49, ValueHigh = 49,
                ValueText = "49 = the bar reaches max speed (50/s). FROM 50 ONWARDS, LEVELS ADD A " +
                            "NEW LINEAR MULTIPLIER TO GOLD OUTPUT. \"DON'T STOP AT LEVEL 49\" — " +
                            "⚠ EXPLICITLY NOT TERMINAL",
                Objective = "gold production",
                Cite = "[GUIDE mechanics/time-machine]; [GUIDE ch.2]" },
            new LaneRow { Chapter = 3, System = TargetPass.SysTmSpeed, Ids = new[] { 0 },
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Time,
                ValueText = "\"FIRST HOUR: TIME MACHINE, GO priority: Time Machine\"",
                Objective = "24h phase 1", Cite = "[GUIDE ch.3 §Rebirths]" },
            new LaneRow { Chapter = 5, System = TargetPass.SysTmSpeed, Ids = new[] { 0 },
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Time,
                ValueText = "\"0:00-0:30: TM Phase ... Get as many levels in TM as you can within " +
                            "30 minutes\". An EXPECTATION, not a target: \"Yes, it's normal to only " +
                            "be able to get 0-2 levels early. Welcome to Evil.\"",
                Objective = "24h phase 1", Cite = "[GUIDE ch.5 §24HR RB Schedule]" },
            // The two campaign-scoped terminals — 59 + 10 = 69 of the 100LC's 100, both counting
            // against the budget ([DECOMP TimeMachineController.cs:354-357, :397-400] via 21 §A2).
            // The one context in which the guide's TM numbers form a complete, checkable statement.
            new LaneRow { Chapter = ChapterAny, System = TargetPass.SysTmSpeed, Ids = new[] { 0 },
                TrackNeutral = true, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal, ValueLow = 59, ValueHigh = 59,
                CampaignScope = "100lc",
                ValueText = "\"Post-Boss 30: Get 59 ENERGY LEVELS and 10 Magic levels in Time " +
                            "Machine for 100x gold. REMAINING ENERGY GOES INTO ENERGY BUSTERS " +
                            "AUGMENT\" — the explicit stop for the TM lane INSIDE the 100LC budget",
                Objective = "100 Level Challenge",
                Cite = "[GUIDE mechanics/challenges §Challenge Tips]",
                GroupNote = "terminal but CAMPAIGN-SCOPED — never a standing speedTarget (23 §2.5)" },
            new LaneRow { Chapter = ChapterAny, System = TargetPass.SysTmGoldMulti, Ids = new[] { 0 },
                TrackNeutral = true, Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal, ValueLow = 10, ValueHigh = 10,
                CampaignScope = "100lc",
                ValueText = "\"...and 10 MAGIC LEVELS in Time Machine\"",
                Objective = "100 Level Challenge",
                Cite = "[GUIDE mechanics/challenges §Challenge Tips]",
                GroupNote = "terminal but CAMPAIGN-SCOPED — never a standing multiTarget (23 §2.5)" },

            // -------------------------------------------------------------------------------------
            // §2.6 wandoos — NO `level` ROWS ON ANY TRACK, and that is a FINDING, not a gap.
            // The guide's terminator for Wandoos is an OS SWITCH, never a level. Amendment 16 §4
            // independently found Wandoos is "the sole UNTERMINATED consumer"; these agree from
            // opposite directions, and the P1 lane-target campaign established that a synthetic
            // Wandoos target is exactly what makes amendment 16 §4's ranking come out at zero.
            // ⚠ Every row below is non-level, and TargetPass.Route refuses ANY wandoos row outright
            // before it looks at the kind. There is no path from this table to a Wandoos target.
            // -------------------------------------------------------------------------------------
            new LaneRow { Chapter = 1, System = TargetPass.SysWandoos, Ids = EveryId,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Rate,
                Terminality = TargetPass.Terminality.Precondition, ValueLow = 0, ValueHigh = 0,
                ValueText = "rate = 0: \"In this chapter, Wandoos is TOO EXPENSIVE to provide much " +
                            "benefit, JUST IGNORE IT FOR NOW\"",
                Objective = "do not fund yet", Cite = "[GUIDE ch.1 §Wandoos]" },
            new LaneRow { Chapter = 2, System = TargetPass.SysWandoos, Ids = EveryId,
                Track = TargetPass.Track.Normal, Kind = TargetPass.RowKind.Time,
                ValueText = "\"Wandoos is still very slow and NOT WORTH OVERINVESTING into. Can be " +
                            "useful running for A FEW MINUTES AT THE END OF A REBIRTH... Wandoos 98 " +
                            "formula provides constantly diminishing returns, so don't focus heavily\"",
                Objective = "end-of-rebirth trickle", Cite = "[GUIDE ch.2]" },
            new LaneRow { Chapter = ChapterAny, System = TargetPass.SysWandoos, Ids = EveryId,
                AllTracks = true, Kind = TargetPass.RowKind.Predicate,
                ValueText = "OS selector: \"5 MINUTES OF MEH BB provides better bonuses than 24 " +
                            "HOURS OF 98 BB; 1 level/s of XL provides better bonuses than 24 hours " +
                            "of MEH BB\"; \"it's better to STICK WITH THE LOWER OS until you can " +
                            "get enough levels\" — THE TERMINATOR IS AN OS SWITCH, NEVER A LEVEL",
                Objective = "choose the OS", Cite = "[GUIDE mechanics/wandoos §Switching OS]" },
            new LaneRow { Chapter = 5, System = TargetPass.SysWandoos, Ids = EveryId,
                Track = TargetPass.Track.Evil, Kind = TargetPass.RowKind.Predicate,
                ValueText = "cost: \"Run Wandoos AT until CHEAP TO RUN WANDOOS 98\"",
                Objective = "boot Wandoos", Cite = "[GUIDE ch.5 §24HR RB Schedule]" },
            new LaneRow { Chapter = ChapterAny, System = TargetPass.SysWandoos, Ids = EveryId,
                AllTracks = true, Kind = TargetPass.RowKind.Rate, CampaignScope = "100lc",
                ValueText = "\"Pre-Boss 30: SPLIT LEVELS IN WANDOOS E/M DUMPS, rebirth when number " +
                            "gain is 10-100x\"",
                Objective = "100 Level Challenge",
                Cite = "[GUIDE mechanics/challenges §Challenge Tips]" },
        };

        // =========================================================================================
        // §2.3 THE M0 / M1 CONFLICT — emitted as a conflict, not resolved.
        // =========================================================================================
        // 23 could not resolve a conflict inside its own source without interpolating: 22 is
        // internally inconsistent between its §Q1.2 grouping (which puts E8, M0, M1 under one
        // "softcap" row) and its §Q1.5 per-id verdict (which records M0 evil = "NO — split Magic
        // into NGU Ygg/EXP" and M1 evil = "NO — paired with Ygg throughout").
        //
        // ⚠ NO NUMERIC VALUE IS EMITTED FOR EITHER READING. Reading A's numbers are recorded as
        // TEXT precisely so that no consumer can pick them up as a target by accident. They are
        // available as CURVE CONSTANTS in Softcaps below — which is what is NOT in dispute.
        public struct ConflictRow
        {
            public int Chapter;
            public string System;
            public int[] Ids;
            public TargetPass.Track Track;
            public string ReadingA;
            public string ReadingB;
            public string NotInDispute;
            public string Resolution;   // null when nothing in the corpus resolves it
            public string Cite;
        }

        public static readonly ConflictRow[] Conflicts =
        {
            new ConflictRow { Chapter = 5, System = TargetPass.SysNguMagic, Ids = First2EvilM,
                Track = TargetPass.Track.Evil,
                ReadingA = "\"Softcap\" governs the whole clause, so M0 and M1 are softcap targets " +
                           "too — 22 §Q1.2's softcap table groups E8, M0 and M1 under one row, and " +
                           "the operator brief instructs \"M0=400, M1=2000\"",
                ReadingB = "\"Softcap\" scopes to NGU PP ALONE; \"split Magic into NGU Ygg/EXP\" " +
                           "carries no level. This is the guide's sentence as written, and 22 " +
                           "§Q1.5's own per-id rows agree: M0 evil = \"NO\", M1 evil = \"NO\" — " +
                           "NO TARGET",
                NotInDispute = "[GUIDE mechanics/ngu] publishes M0 softcap = 400 and M1 softcap = " +
                               "2000 as CURVE CONSTANTS, both confirmed against " +
                               "[DECOMP AllNGUController.cs:692, :747]. Whether the guide asks you " +
                               "to TARGET them is the open question",
                Resolution = "amendment 18 §1 / amendment 21 §1 decide it in favour of reading B: " +
                             "every Evil NGU is a rate row, both pools, all ids — no level exists. " +
                             "TargetPass.SilenceLedger already carries that as the Evil-NGU " +
                             "silence. 23 itself adjudicates NEITHER reading; the decision record " +
                             "does, and both are recorded here",
                Cite = "[GUIDE ch.5 §LRB to T8]; 23 §2.3; amendment 18 §1; amendment 21 §1" },
        };

        // =========================================================================================
        // §2.7 REFERENCE CONSTANTS — the sixteen published softcaps.
        // =========================================================================================
        // ⚠ THESE ARE CURVE CONSTANTS, NOT TARGETS. They are what any softcap-shaped instruction
        // resolves AGAINST. 22 §0.4 verified all sixteen against the decomp branch constants and all
        // sixteen match exactly. A softcap is NOT a stopping level: Respawn's post-400 branch
        // saturates and Adventure a's post-1000 branch is unbounded sqrt (23 §0.5).
        public struct Softcap
        {
            public string System;
            public int Index;
            public string Name;
            public string EffectPerLevel;
            public long Value;          // 0 == the curve has NO softcap
            public bool HasSoftcap;
            public string DecompLine;   // AllNGUController.cs
            public bool DecompVerified; // 22 §0.4 checked the branch constant itself
            public string BaseCost;
        }

        public static readonly Softcap[] Softcaps =
        {
            new Softcap { System = TargetPass.SysNguEnergy, Index = 0, Name = "Augments",
                EffectPerLevel = "1%", HasSoftcap = false, DecompLine = ":374", BaseCost = "200B" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 1, Name = "Wandoos",
                EffectPerLevel = "0.1%", HasSoftcap = false, DecompLine = ":406", BaseCost = "200B" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 2, Name = "Respawn",
                EffectPerLevel = "0.05%", Value = 400, HasSoftcap = true, DecompLine = ":449",
                DecompVerified = true, BaseCost = "200B" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 3, Name = "Gold",
                EffectPerLevel = "1%", HasSoftcap = false, DecompLine = ":530", BaseCost = "200B" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 4, Name = "Adventure a",
                EffectPerLevel = "0.1%", Value = 1000, HasSoftcap = true, DecompLine = ":568",
                DecompVerified = true, BaseCost = "200B" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 5, Name = "Power a",
                EffectPerLevel = "5%", HasSoftcap = false, DecompLine = ":623", BaseCost = "200B" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 6, Name = "Drop Chance",
                EffectPerLevel = "0.1%", Value = 1000, HasSoftcap = true, DecompLine = ":802",
                DecompVerified = true, BaseCost = "20T" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 7, Name = "Magic NGU",
                EffectPerLevel = "0.1%", Value = 1000, HasSoftcap = true, DecompLine = ":994",
                DecompVerified = true, BaseCost = "400T" },
            new Softcap { System = TargetPass.SysNguEnergy, Index = 8, Name = "PP",
                EffectPerLevel = "0.05%", Value = 1000, HasSoftcap = true, DecompLine = ":1053",
                DecompVerified = true, BaseCost = "10q" },
            new Softcap { System = TargetPass.SysNguMagic, Index = 0, Name = "Yggdrasil",
                EffectPerLevel = "0.1%", Value = 400, HasSoftcap = true, DecompLine = ":692",
                DecompVerified = true, BaseCost = "400B" },
            new Softcap { System = TargetPass.SysNguMagic, Index = 1, Name = "Exp",
                EffectPerLevel = "0.01%", Value = 2000, HasSoftcap = true, DecompLine = ":747",
                DecompVerified = true, BaseCost = "1.2T" },
            new Softcap { System = TargetPass.SysNguMagic, Index = 2, Name = "Power b",
                EffectPerLevel = "1%", HasSoftcap = false, DecompLine = ":655", BaseCost = "4T" },
            new Softcap { System = TargetPass.SysNguMagic, Index = 3, Name = "Number",
                EffectPerLevel = "1%", Value = 1000, HasSoftcap = true, DecompLine = ":849",
                DecompVerified = true, BaseCost = "12T" },
            // ⚠ the one softcap row 23 §2.7 does NOT mark decomp-verified.
            new Softcap { System = TargetPass.SysNguMagic, Index = 4, Name = "Time Machine",
                EffectPerLevel = "0.2%", Value = 1000, HasSoftcap = true, DecompLine = ":1147",
                DecompVerified = false, BaseCost = "100T" },
            new Softcap { System = TargetPass.SysNguMagic, Index = 5, Name = "Energy NGU",
                EffectPerLevel = "0.1%", Value = 1000, HasSoftcap = true, DecompLine = ":943",
                DecompVerified = true, BaseCost = "1q" },
            new Softcap { System = TargetPass.SysNguMagic, Index = 6, Name = "Adventure b",
                EffectPerLevel = "0.03%", Value = 1000, HasSoftcap = true, DecompLine = ":1104",
                DecompVerified = true, BaseCost = "10q" },
        };

        // "Each NGU has a hardcap (max level) of 1 billion levels" [GUIDE mechanics/ngu] — the ONE
        // NGU number in the guide that applies to all three tracks, and it agrees with the decomp:
        // hardCapNormalLevel() clamps level, evilLevel AND sadisticLevel
        // ([DECOMP NGUController.cs:60-63, :78-81, :107-110]). A CEILING, NOT A TARGET — but it
        // bounds every target field an operator could write, on every track. Same constant as
        // TargetPass.NguHardCap, which is where the write guard enforces it.
        public const long NguHardCapFromGuide = 1000000000L;

        // "most features are limited to gaining at most one level/tick... 50 LEVELS/SECOND"
        // [GUIDE mechanics/general-info §Ticks]. The unit every `rate` row above is denominated in.
        public const int BlankBarLevelsPerSecond = 50;

        // THE AT BLOCK HARD CAP — [OPERATOR] 2026-08-07, stated twice: "the bridges 100,000 is the
        // correct one. the 5000 is not acceptable. this goes for every difficulty" and "the Block AT
        // is a hard cap at 100,000 and should never be capped lower."
        //
        // ONE constant, TWO consumers, so the number cannot drift between them: the AT id-2 Terminal
        // row above, and LevelPlanner's live write to levelTarget[2]. Before this existed the table
        // said 100,000 while the live writer said ceil(49/f) ≈ 5,000 — the two-writers defect the
        // ruling settles. See the row comment above for why the operator's STATED REASON (99%
        // reduction) belongs to 5,000 and not to this number, and why that does not move it.
        //
        // ⚠ THE LIVE WRITER IS WHAT MAKES THE CAP HOLD ON EVERY DIFFICULTY, not a per-track row:
        // levelTarget[2] is a single field with no per-track split. See the row comment above.
        //
        // An operator line on an asymptotic curve, NOT a mechanical saturation point (amendment 35
        // §1) — so nothing derives it and nothing may re-derive it.
        public const long AtBlockHardCapLevel = 100000L;

        // =========================================================================================
        // §2.4 THE BLOCK DAMAGE CURVE — track-neutral, and ONE RUNG IS UNUSABLE.
        // =========================================================================================
        // The curve the AT id-2 rows resolve against, and the thing ch.5's ">99%" T8 requirement pins
        // itself to (the 5,000 rung).
        //
        // ⚠ THE BROKEN RUNG. [GUIDE mechanics/advanced-training] reads "Block Reduction reaches 90% at
        // Level 400, 99% at 5k, 99.9% AT 5, and 99.99% at 500k". A monotone ladder 400 -> 5k -> 5 ->
        // 500k is IMPOSSIBLE. The intended value is almost certainly 50,000. BOTH NEIGHBOURS ARE
        // TRANSCRIBED AND USABLE; the broken rung is carried with Usable == false and its stated Level
        // preserved verbatim so nobody re-derives it from the percentage. DO NOT TRANSCRIBE 5 INTO A
        // levelTarget. NOT ADJUDICATED (23 §0.6).
        public struct BlockCurveRung
        {
            public long Level;
            public string BlockReduction;
            public bool Usable;
            public string Note;
        }

        public static readonly BlockCurveRung[] BlockDamageCurve =
        {
            new BlockCurveRung { Level = 400, BlockReduction = "90%", Usable = true },
            new BlockCurveRung { Level = 5000, BlockReduction = "99%", Usable = true,
                Note = "the rung [GUIDE ch.5]'s \">99%\" T8 requirement pins against" },
            new BlockCurveRung { Level = 5, BlockReduction = "99.9%", Usable = false,
                Note = "⛔ BROKEN RUNG — monotonically impossible between 5k and 500k. Intended " +
                       "value almost certainly 50,000. NOT ADJUDICATED, and NOT usable as a level" },
            new BlockCurveRung { Level = 500000, BlockReduction = "99.99%", Usable = true },
            new BlockCurveRung { Level = 1000000, BlockReduction = "\"the UI rounds the display to " +
                "100%, but it never truly blocks all damage\"", Usable = true,
                Note = "a display note, not a threshold (\"1,000,000+\")" },
        };

        public const string BlockDamageCurveCite = "[GUIDE mechanics/advanced-training]";

        // =========================================================================================
        // §2.8 THE CROSS-CUTTING RULES THAT ARE NOT TARGETS
        // =========================================================================================
        // ⚠ 23 §2.8's heading says "three" and its table carries FOUR rows. Recorded, not
        // adjudicated; all four are transcribed.
        //
        // These are held separately from LaneRows because they are RULES OVER SEVERAL LANES with a
        // TRIGGER, not per-lane guidance. Rules 2 and 3 restate §2.3 rows already in LaneRows; rule 4
        // (the CBlock2 magic split) appears ONLY here, so dropping this array would lose it.
        //
        // ⚠ THE STRUCTURAL TRAP (22 §Q2.3). Amendment 16 §3 established the cascade moves a sub-lane's
        // ENTIRE allocation to the next unmet id. The guide's ch.4 rule is the same all-or-nothing
        // move — BUT THE GUIDE'S RULE RE-EVALUATES HOURLY AND THE CASCADE RE-EVALUATES ON TARGET-MET.
        // Wiring the guide's ORDER into id+1 without its TRIGGER produces a system that walks the
        // right sequence at the wrong times.
        public struct CrossCuttingRule
        {
            public string Name;
            public string Rule;
            public string Ids;              // 23's own id list, verbatim — spans both pools
            public TargetPass.RowKind Kind;
            public string Track;
            public string Trigger;          // what makes it fire — the half id+1 does not have
            public string Cite;
        }

        public static readonly CrossCuttingRule[] CrossCuttingRules =
        {
            new CrossCuttingRule { Name = "The GO bonus-change rule",
                Rule = "\"if you meet % bonus change, PUT ALL E/M INTO THAT NGU FOR AN HOUR\": " +
                       "1 E/M NGU >1.05x · 2 Adventure Beta/PP >1.05x · 3 Respawn <0.95x · " +
                       "4 Gold/TM >1.2x · 5 \"split Energy into Adv/DC, split Magic into Ygg/EXP\"",
                Ids = "E7/M5, M6/E8, E2, E3/M4, E4+E6/M0+M1",
                Kind = TargetPass.RowKind.Predicate,
                Track = "normal (ch.4), REUSED FOR EVIL (\"use a similar NGU priority to last " +
                        "chapter\", ch.5)",
                Trigger = "MARGINAL GAIN PER HOUR, re-evaluated HOURLY — not a level, and not " +
                          "target-met",
                Cite = "[GUIDE ch.4 §NGU Priority]; [GUIDE ch.5]" },

            new CrossCuttingRule { Name = "The BB rule",
                Rule = "\"Aim to BB the first 5 Evil eNGUs and the first 2 Evil mNGUs all rebirth\"",
                Ids = "E0-E4, M0-M1", Kind = TargetPass.RowKind.Rate, Track = "evil",
                Trigger = "standing, post-HD1 — allocation SUFFICIENCY (50 levels/s), not a " +
                          "stopping level",
                Cite = "[GUIDE ch.6 §Post-HD1]" },

            new CrossCuttingRule { Name = "The Evil-hour rule",
                Rule = "\"run Normal NGUs for most of your rebirth, then run Evil NGUs for the last " +
                       "hour... an extra hour per T7 version defeated\"",
                Ids = "all 16 x 2 tracks", Kind = TargetPass.RowKind.Time, Track = "evil",
                Trigger = "WALL CLOCK",
                Cite = "[GUIDE ch.5 §Evil NGUs]" },

            new CrossCuttingRule { Name = "The CBlock2 magic split",
                Rule = "\"split mNGU into POWER B/TM\"",
                Ids = "M2, M4", Kind = TargetPass.RowKind.Rate, Track = "normal, ch.4",
                Trigger = "CBlock2 prep",
                Cite = "[GUIDE ch.4 §Post-v2]" },
        };

        // =========================================================================================
        // §2.9 SADISTIC — ZERO ROWS, EVERY SYSTEM, and that is deliberate.
        // =========================================================================================
        // [GUIDE ch.8] is 166 lines and contains no NGU level, no AT level, no augment level, no TM
        // level, no Wandoos level. Its six allocation-shaped instructions are EXP-chain stop rules
        // ("stop buying at 3.5e12 cap", "stop buying at 4e8 pow", "stop buying R3 around 50k-60k
        // power", "if you reach 150 Decillion Power, stop buying Power and switch to HP Regen"), a
        // capability gate (the sNGU -> eNGU quirk) and a campaign retirement (Snugdays). NONE is a
        // target field in any of the seven systems — they belong to the EXP chain (amendment 13),
        // not to the objective layer's target table, and are deliberately NOT transcribed as rows.
        //
        // The Sadistic silence itself is carried by TargetPass.SilenceLedger's catch-all (23 §7.1 S2).

        // ---- queries over the table --------------------------------------------------------------
        // Pure selectors. The READER (ObjectiveReader) is what a consumer calls; these are the
        // primitives it and the tests are built from.

        // Chapter matching: a row gated on chapter N matches only a query for N; a ChapterAny row is
        // STANDING and matches every chapter; a ChapterAny QUERY matches every row.
        public static bool ChapterMatches(int rowChapter, int queryChapter)
        {
            return queryChapter == ChapterAny || rowChapter == ChapterAny ||
                   rowChapter == queryChapter;
        }

        // Track matching. ⚠ A NORMAL-TRACK ROW DOES NOT SATISFY AN EVIL-TRACK QUERY — the game
        // stores three separate target fields per NGU (skills[id].target / evilTarget /
        // sadisticTarget, [DECOMP NGU.cs:22-26]) and 23 §0.1 says a row without a track is unusable.
        // The two exemptions are structural, not convenient: TrackNeutral (the game stores ONE field
        // — TM) and AllTracks (the guide's sentence names no track).
        public static bool TrackMatches(in LaneRow row, TargetPass.Track query)
        {
            if (row.TrackNeutral || row.AllTracks)
                return true;
            return row.Track == query;
        }

        public static IList<LaneRow> LanesFor(int chapter, TargetPass.Track track)
        {
            var hits = new List<LaneRow>();
            for (int i = 0; i < LaneRows.Length; i++)
            {
                var row = LaneRows[i];
                if (!ChapterMatches(row.Chapter, chapter))
                    continue;
                if (!TrackMatches(row, track))
                    continue;
                hits.Add(row);
            }
            return hits;
        }

        public static IList<LaneRow> LanesFor(int chapter, TargetPass.Track track, string system,
            int id)
        {
            var hits = new List<LaneRow>();
            for (int i = 0; i < LaneRows.Length; i++)
            {
                var row = LaneRows[i];
                if (!string.Equals(row.System, system, StringComparison.Ordinal))
                    continue;
                if (!row.Covers(id))
                    continue;
                if (!ChapterMatches(row.Chapter, chapter))
                    continue;
                if (!TrackMatches(row, track))
                    continue;
                hits.Add(row);
            }
            return hits;
        }

        public static bool TryGetSoftcap(string system, int index, out Softcap cap)
        {
            for (int i = 0; i < Softcaps.Length; i++)
            {
                if (Softcaps[i].Index == index &&
                    string.Equals(Softcaps[i].System, system, StringComparison.Ordinal))
                {
                    cap = Softcaps[i];
                    return true;
                }
            }
            cap = default(Softcap);
            return false;
        }

        public static IList<ConflictRow> ConflictsFor(int chapter, TargetPass.Track track)
        {
            var hits = new List<ConflictRow>();
            for (int i = 0; i < Conflicts.Length; i++)
            {
                var c = Conflicts[i];
                if (!ChapterMatches(c.Chapter, chapter))
                    continue;
                if (c.Track != track)
                    continue;
                hits.Add(c);
            }
            return hits;
        }
    }
}
