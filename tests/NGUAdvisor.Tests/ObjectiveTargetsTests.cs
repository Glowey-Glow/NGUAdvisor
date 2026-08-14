using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE TARGET-TABLE PRODUCER (`ObjectiveTargets`) — constraint-layer-spec §10's first input,
    // materialised from the objective layer, and SINCE THE WIRE the live source of
    // `ConstraintLayerBridge.TargetTable`. Three jobs:
    //
    //   T1  MEASURE WHAT THE TABLE CAN DO. `ConstraintLayer.WantFromAnswer` stops a lane for exactly
    //       one shape — `WriteTarget` + `Satisfied` — so the produced table's entire power to move
    //       allocation is its set of rows that route to `WriteTarget`. Over the WHOLE query space
    //       (8 chapters x 3 NGU tracks x 3 run tracks) that set has exactly ONE member, and it is
    //       AT SLOT 2 at `ObjectiveTable.AtBlockHardCapLevel` on the EVIL track. Counted, not claimed.
    //
    //   T2  PIN THE DECIDED EXCLUSIONS. NGUs take NO target level at all (amendment 21 §1, unqualified
    //       again since [OPERATOR] removed Respawn 401 at 08b4344 — see the dormancy test below); TM
    //       never terminates (amendment 24 §4/§6); terminality is load-bearing (23 §0.4 — a
    //       precondition written to `target` makes the cascade abandon the lane forever); a silence is
    //       not a zero (0 is the game's UNSET sentinel and funds forever). Each is asserted as a
    //       property of the producer over the full query space, and each NON-VACUOUSLY: the rule must
    //       be shown to have dropped something.
    //
    //   T3  THE SWEEP. Compose the same pool over the same live-shaped lane set twice — once with the
    //       shipped `WantsMore = true`, once with Pass 3 fed the produced table — across pool sizes,
    //       budgets, chapters and tracks, and compare field for field. ⚠ WITH A NEGATIVE CONTROL:
    //       the same sweep, with the AT lanes at the hard cap instead of one below it, MUST detect a
    //       change. A no-change proof that cannot fail proves nothing.
    //
    // ⚠ WHAT THE SWEEP ESTABLISHES, STATED HONESTLY. Allocation is byte-identical for every lane
    // state EXCEPT a seated chapter-5 Evil-run AT SLOT 2 at level >= 100,000 — where the produced
    // table stops that ONE SLOT, deliberately and by [OPERATOR] ruling ("the Block AT is a hard cap
    // at 100,000 and should never be capped lower"). ⚠ AND IT IS ONE SLOT, NOT A SYSTEM: `ALLAT`
    // yields five separate `AdvancedTrainingBP` with `Index` 0..4 and `LaneIndex => Index`, and
    // `TargetPass.RowsFor` filters on System AND Index — so slots 0/1/3/4 receive null and keep
    // funding AT THE SAME LEVEL. `Only_AT_slot_2_is_reachable_...` demonstrates exactly that, with
    // all five slots driven to the cap together.
    public class ObjectiveTargetsTests
    {
        private static readonly TargetPass.Track[] Tracks =
        {
            TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
        };

        // The full query space the producer is defined over: every chapter x every track pair.
        private static IEnumerable<ObjectiveTargets.Query> EveryQuery()
        {
            for (int ch = ObjectiveTargets.FirstChapter; ch <= ObjectiveTargets.LastChapter; ch++)
            foreach (var ngu in Tracks)
            foreach (var run in Tracks)
                yield return new ObjectiveTargets.Query
                {
                    Chapter = ch, ChapterKnown = true, NguTrack = ngu, RunTrack = run,
                };
        }

        private static IEnumerable<TargetPass.TargetRow> EveryProducedRow()
        {
            foreach (var q in EveryQuery())
            {
                var t = ObjectiveTargets.Produce(q);
                foreach (var r in t.Rows)
                    yield return r;
            }
        }

        private static IEnumerable<ObjectiveTargets.Exclusion> EveryExclusion()
        {
            foreach (var q in EveryQuery())
            foreach (var x in ObjectiveTargets.Produce(q).Excluded)
                yield return x;
        }

        // ---- T0: the query space is real, so nothing below is vacuous -----------------------------

        [Fact]
        public void The_query_space_is_eight_chapters_by_nine_track_pairs()
        {
            Assert.Equal(8 * 3 * 3, EveryQuery().Count());
            Assert.All(EveryQuery(), q => Assert.NotNull(ObjectiveTargets.Produce(q).Rows));
        }

        // ---- T1: what the table can DO ------------------------------------------------------------

        // THE HEADLINE MEASUREMENT, AND B5's ANSWER. Every row the producer can emit, on every chapter
        // and every track pair, routed. Exactly one routes to WriteTarget — the only disposition that
        // carries a satisfaction and therefore the only one WantFromAnswer can turn into a stop. If
        // anything but AT slot 2 ever appears here, a lane nobody authorised has become stoppable.
        [Fact]
        public void Across_the_whole_query_space_exactly_one_row_can_stop_a_lane_and_it_is_Block_AT()
        {
            var writable = new List<TargetPass.TargetRow>();
            foreach (var q in EveryQuery())
                writable.AddRange(ObjectiveTargets.Writable(ObjectiveTargets.Produce(q).Rows));

            // The same row is produced on three (chapter, track-pair) queries — chapter 5 with the
            // RUN track Evil, for each of the three NGU tracks — so the DISTINCT inventory is what
            // the claim is about.
            var distinct = writable
                .Select(r => r.System + "-" + r.Index + "@" + r.ValueLow)
                .Distinct()
                .ToList();

            Assert.Single(distinct);
            Assert.Equal("at-2@" + ObjectiveTable.AtBlockHardCapLevel, distinct[0]);

            Assert.All(writable, r =>
            {
                Assert.Equal(TargetPass.SysAt, r.System);
                Assert.Equal(2, r.Index);
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, r.ValueLow);
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, r.ValueHigh);
                Assert.Equal(TargetPass.Terminality.Terminal, r.Terminality);
                Assert.Equal(TargetPass.Track.Evil, r.Track);
                Assert.Null(r.CampaignScope);
                Assert.Null(r.LiftGate);        // an ungated row: RouteLevel reaches terminality
            });

            // ...AND NOTHING ELSE EVEN GETS AS FAR AS BEING A CANDIDATE. Every row the producer can
            // emit at all is an `at` row — no NGU, no TM, no augment, no Wandoos row exists to be
            // routed. So "only AT-2 can stop a lane" is bounded twice over: by the routing above and
            // by the system inventory below.
            var systems = EveryProducedRow().Select(r => r.System).Distinct().ToList();
            Assert.Single(systems);
            Assert.Equal(TargetPass.SysAt, systems[0]);
        }

        // ...and it is reachable only where the guide puts it: chapter 5, RUN track Evil (the AT
        // lanes read the run's difficulty, not the NGU selector — ConstraintLayerBridge.LaneStateFor).
        // A row that leaked into another chapter would stop a lane in a phase the guide never
        // discussed.
        [Fact]
        public void The_one_writable_row_appears_only_at_chapter_5_on_the_Evil_run_track()
        {
            int inScope = 0;
            foreach (var q in EveryQuery())
            {
                var writable = ObjectiveTargets.Writable(ObjectiveTargets.Produce(q).Rows);
                bool expected = q.Chapter == 5 && q.RunTrack == TargetPass.Track.Evil;
                Assert.True(expected == (writable.Count > 0),
                    "chapter " + q.Chapter + " ngu=" + q.NguTrack + " run=" + q.RunTrack +
                    " produced " + writable.Count + " writable row(s)");
                if (expected) inScope++;
            }

            // One chapter x one run track x three NGU tracks: the row does not vary with the NGU
            // selector, and that is the split LaneStateFor makes for every non-NGU system.
            Assert.Equal(3, inScope);
        }

        // Every OTHER produced row is a precondition, at every level, forever. This is the assertion
        // that makes "one row moves allocation" a complete statement rather than a spot check: a
        // precondition never carries a satisfaction, so no level can turn it into a stop.
        [Fact]
        public void Every_non_terminal_produced_row_holds_the_want_open_at_every_level()
        {
            var levels = new[] { 0L, 1L, 400L, 401L, 5_000L, 100_000L, 5_000_000L,
                                 TargetPass.NguHardCap, long.MaxValue };
            int checkedPairs = 0;

            foreach (var q in EveryQuery())
            {
                var table = ObjectiveTargets.Produce(q).Rows;
                foreach (var row in table)
                {
                    if (row.Terminality == TargetPass.Terminality.Terminal)
                        continue;

                    foreach (var level in levels)
                    {
                        var lane = new TargetPass.LaneState
                        {
                            System = row.System,
                            Index = row.Index,
                            ActiveTrack = row.Track,
                            LevelOnTrack = level,
                        };
                        var answer = TargetPass.Evaluate(lane,
                            TargetPass.RowsFor(table, row.System, row.Index),
                            FeasibilityPass.Verdict.Seat());

                        Assert.NotEqual(TargetPass.Satisfaction.Satisfied, answer.Satisfaction);

                        string why;
                        Assert.True(ConstraintLayer.WantFromAnswer(answer, out why),
                            row.System + "-" + row.Index + " at level " + level + " was stopped");
                        checkedPairs++;
                    }
                }
            }

            Assert.True(checkedPairs > 0, "no non-terminal row was produced — the sweep was vacuous");
        }

        // ---- T2: the decided exclusions ------------------------------------------------------------

        // ⚠ AMENDMENT 21 §1, UNQUALIFIED AGAIN. The branch that wrote this file asserted 21 §1 "as
        // NARROWED by amendment 34 §3" — "except where the curve saturates… exactly one case: Respawn
        // (energy id 2) at 401". [OPERATOR] then removed that row from ObjectiveTable outright at
        // 08b4344 ("the advisor already calculates what is the best use of energy already. So it's a
        // moot point"), so the narrowing has no subject and the plain rule is what stands: NO NGU ROW
        // IS PRODUCED, on any chapter, on any track, at any level.
        [Fact]
        public void No_NGU_row_is_produced_at_all()
        {
            Assert.DoesNotContain(EveryProducedRow(), r => TargetPass.IsNguSystem(r.System));

            // NON-VACUITY: the rule must be shown to have dropped real NGU LEVEL rows, or it is a
            // filter over an empty set and proves nothing. The PAWG ladder alone is 16 of them.
            var droppedNguLevels = EveryExclusion()
                .Where(x => TargetPass.IsNguSystem(x.System) && x.Kind == TargetPass.RowKind.Level)
                .ToList();
            Assert.True(droppedNguLevels.Count >= 16,
                "the NGU rule dropped only " + droppedNguLevels.Count + " level rows");
            Assert.All(droppedNguLevels, x => Assert.Contains("amendment 21 §1", x.Rule));
        }

        // ⚠ THIS REPLACES `The_carve_out_predicate_separates_the_two_softcap_rows`, WHICH WAS DELETED
        // RATHER THAN REPAIRED. That test asserted the predicate told the Respawn-401 TERMINAL apart
        // from the Adventure-a-1000 PRECONDITION — "the same word 'softcap', only one of which
        // saturates" (amendment 34 §3.1). Both rows were NGU and the Respawn half no longer exists in
        // ObjectiveTable, so the pair it separated is half a pair: `LaneRows.Single(... ngu-energy id
        // 2 … Level)` throws, and every way of "fixing" the lookup ends in a test that separates one
        // surviving row from nothing. Deleted with the reason recorded here, and replaced by the claim
        // that is actually true and actually checkable now — that the carve-out is DORMANT.
        //
        // ⚠ THE PREDICATE ITSELF IS KEPT, and this is what keeps it honest. `ObjectiveTargets.Rule`
        // still reads `IsNguSystem(row.System) && !IsRespawnCarveOut(row, id)`, so a Respawn-401
        // terminal re-added to the table WOULD be admitted — and now that the bridge is wired, admitted
        // means it stops a live lane. That hole is closed from the other side, by
        // `OperatorRuledRowsTests.The_number_401_is_not_a_level_target_anywhere` and
        // `GuideRowsParityTests.The_number_401_is_not_a_level_in_the_reference_rows_either`: the row
        // cannot return without failing those first. Asserted here as a dependency, not assumed.
        [Fact]
        public void The_NGU_carve_out_has_no_subject_left_and_is_pinned_as_dormant()
        {
            // No row in the table can satisfy the predicate — checked over EVERY row and EVERY id it
            // covers, so this is not a lookup that happens to miss.
            foreach (var row in ObjectiveTable.LaneRows)
            {
                var ids = row.Ids ?? new[] { ObjectiveTable.NoIndex };
                foreach (var id in ids)
                    Assert.False(ObjectiveTargets.IsRespawnCarveOut(row, id),
                        "the NGU carve-out has a subject again: " + row.System + " id " + id +
                        " @" + row.ValueLow + " — [OPERATOR] removed Respawn 401 at 08b4344, and the " +
                        "bridge is now WIRED, so this row would stop a live lane");
            }

            // The row it was written for is gone; its "softcap" twin is not, and is still a
            // precondition. That surviving half is what makes the deletion legible rather than a gap.
            Assert.DoesNotContain(ObjectiveTable.LaneRows, r =>
                r.System == TargetPass.SysNguEnergy && r.Covers(2) &&
                r.Kind == TargetPass.RowKind.Level);
            var adventureA = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysNguEnergy && r.Ids != null && r.Ids.Contains(4) &&
                r.Kind == TargetPass.RowKind.Level && r.ValueLow == 1000);
            Assert.Equal(TargetPass.Terminality.Precondition, adventureA.Terminality);
            Assert.False(ObjectiveTargets.IsRespawnCarveOut(adventureA, 4));

            // The DORMANT guard, exercised on synthetic rows because no real one can reach it — the
            // same treatment this file already gives the Wandoos and EveryId rules. A look-alike is
            // still refused on each of the four fields the predicate reads.
            var respawn = new ObjectiveTable.LaneRow
            {
                Chapter = 3,
                System = TargetPass.SysNguEnergy,
                Ids = new[] { 2 },
                Track = TargetPass.Track.Normal,
                Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = 401, ValueHigh = 401,
                Cite = "synthetic — the row [OPERATOR] removed at 08b4344",
            };
            Assert.True(ObjectiveTargets.IsRespawnCarveOut(respawn, 2));

            var precondAt401 = respawn;
            precondAt401.Terminality = TargetPass.Terminality.Precondition;
            Assert.False(ObjectiveTargets.IsRespawnCarveOut(precondAt401, 2));

            var terminalAt402 = respawn;
            terminalAt402.ValueLow = 402; terminalAt402.ValueHigh = 402;
            Assert.False(ObjectiveTargets.IsRespawnCarveOut(terminalAt402, 2));

            var scoped = respawn;
            scoped.CampaignScope = "100lc";
            Assert.False(ObjectiveTargets.IsRespawnCarveOut(scoped, 2));

            Assert.False(ObjectiveTargets.IsRespawnCarveOut(respawn, 3));

            // AND THE HOLE IS CLOSED FROM THE OTHER SIDE. The predicate would admit the synthetic row
            // above; what stops it reaching the table is that 401 may not BE a level target. Stated
            // as an executable dependency so deleting that guard breaks this test too.
            Assert.DoesNotContain(ObjectiveTable.LaneRows, r =>
                r.Kind == TargetPass.RowKind.Level && (r.ValueLow == 401 || r.ValueHigh == 401));
            Assert.DoesNotContain(TargetPass.GuideRows, r => r.ValueLow == 401 || r.ValueHigh == 401);
        }

        // ⚠ AMENDMENT 24 §4/§6: "speedTarget and multiTarget should be ZERO, always." TM is a rate
        // lane with no saturation point, so NO TM row is produced — including the guide's own 49,
        // which is the trap row (it names the number and then says "don't stop at Level 49").
        [Fact]
        public void No_TM_row_is_ever_produced_on_any_chapter_or_track()
        {
            Assert.DoesNotContain(EveryProducedRow(), r =>
                r.System == TargetPass.SysTmSpeed || r.System == TargetPass.SysTmGoldMulti);

            // NON-VACUITY plus provenance: the TM rule dropped real LEVEL rows, and the trap row and
            // both 100LC terminals are among them.
            var droppedTm = EveryExclusion()
                .Where(x => (x.System == TargetPass.SysTmSpeed ||
                             x.System == TargetPass.SysTmGoldMulti) &&
                            x.Kind == TargetPass.RowKind.Level)
                .ToList();
            Assert.NotEmpty(droppedTm);
            Assert.Contains(droppedTm, x => x.ValueLow == 49);
            Assert.Contains(droppedTm, x => x.ValueLow == 59);
            Assert.Contains(droppedTm, x => x.ValueLow == 10);

            // The 49 row is dropped by the TM rule and cites it; 59/10 are campaign-scoped and are
            // dropped BEFORE the TM rule, by 23 §2.5's rule — different reasons, both recorded.
            Assert.All(droppedTm.Where(x => x.ValueLow == 49),
                x => Assert.Contains("amendment 24 §4", x.Rule));
            Assert.All(droppedTm.Where(x => x.ValueLow == 59 || x.ValueLow == 10),
                x => Assert.Contains("campaign-scoped", x.Rule));
        }

        // The surplus sink is correctly unterminated (spec §8; 23 §2.6). TargetPass refuses the slug
        // at both the row and the lane level; the producer must not hand it one to refuse.
        [Fact]
        public void No_Wandoos_row_is_ever_produced()
        {
            Assert.DoesNotContain(EveryProducedRow(), r => r.System == TargetPass.SysWandoos);
            Assert.Contains(EveryExclusion(), x =>
                x.System == TargetPass.SysWandoos && x.Rule.Contains("DO NOT SYNTHESISE"));

            // The sink rule is tested BEFORE the kind rule, the same order TargetPass.Route uses
            // ("Wandoos first, before any kind logic"). Every Wandoos row in the shipped table is a
            // rate/time/predicate, so without that ordering the sink would be excluded for the wrong
            // reason — and a synthetic Wandoos LEVEL row would fall through to the kind test and be
            // ADMITTED. This asserts the ordering, not just the outcome.
            var syntheticWandoosLevel = new ObjectiveTable.LaneRow
            {
                Chapter = 3,
                System = TargetPass.SysWandoos,
                Ids = new[] { 0 },
                Track = TargetPass.Track.Normal,
                Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = 42, ValueHigh = 42,
                Cite = "synthetic — the shape 23 §2.6 forbids",
            };
            var ruling = ObjectiveTargets.Rule(syntheticWandoosLevel, 0);
            Assert.False(ruling.Admitted);
            Assert.Contains("DO NOT SYNTHESISE", ruling.Rule);
        }

        // Campaign-scoped rows hold only inside their campaign (23 §2.5) and the Campaign Advisor is
        // not built. They are the only OTHER terminals in the table, so this is what keeps the
        // writable inventory at one.
        [Fact]
        public void No_campaign_scoped_row_is_ever_produced()
        {
            Assert.DoesNotContain(EveryProducedRow(), r => r.CampaignScope != null);
            Assert.Contains(EveryExclusion(), x => x.Rule.Contains("campaign-scoped"));
        }

        // ⚠ TERMINALITY IS LOAD-BEARING (23 §0.4): writing a precondition to `target` makes the
        // cascade abandon the lane FOREVER. The producer copies terminality through ToTargetRow and
        // never derives it, so the property to assert is that no precondition can reach a write —
        // over every row it can emit, not over a chosen one.
        [Fact]
        public void No_produced_precondition_can_route_to_a_write()
        {
            int preconditions = 0;
            foreach (var row in EveryProducedRow())
            {
                var route = TargetPass.Route(row);
                if (row.Terminality == TargetPass.Terminality.Precondition)
                {
                    preconditions++;
                    Assert.Equal(TargetPass.Disposition.Precondition, route.Disposition);
                    Assert.Equal(0L, route.TargetToWrite);
                    Assert.Contains("never written to target", route.Reason);
                }
                else
                {
                    Assert.Equal(TargetPass.Terminality.Terminal, row.Terminality);
                }
            }
            Assert.True(preconditions > 0, "no precondition was produced — the assertion was vacuous");
        }

        // ⚠ A SILENCE IS NOT A ZERO. Target 0 is the game's UNSET sentinel: `reachedTarget` returns
        // false at 0 ([DECOMP] AllNGUController.cs:1311-1314), so a lane written to 0 reads unmet and
        // funds FOREVER. The producer only ever COPIES rows, so the way it could manufacture a zero is
        // by materialising a non-level row's empty value pair — which the kind filter prevents.
        [Fact]
        public void The_producer_never_emits_a_zero_valued_row()
        {
            var produced = EveryProducedRow().ToList();
            Assert.NotEmpty(produced);
            Assert.All(produced, r =>
            {
                Assert.Equal(TargetPass.RowKind.Level, r.Kind);
                Assert.NotEqual(TargetPass.GameUnsetSentinel, r.ValueLow);
                Assert.NotEqual(TargetPass.GameUnsetSentinel, r.ValueHigh);
                Assert.True(r.ValueLow > 0 && r.ValueHigh > 0);
            });

            // NON-VACUITY: the table DOES hold rows whose value pair is (0, 0) — the two "rate = 0"
            // rows, ch.1's "not advised to level" TM and Wandoos — and the kind filter is what stops
            // them being materialised as a level of zero.
            Assert.Contains(ObjectiveTable.LaneRows, r =>
                r.Kind != TargetPass.RowKind.Level && r.ValueLow == 0 && r.ValueHigh == 0 &&
                r.Terminality == TargetPass.Terminality.Precondition);
        }

        // ...and the ABSENCE of a row still surfaces as a silence with the ledger's own words, which
        // is the thing a zero would have destroyed. ngu-magic 3 is the slot the guide never mentions
        // once, on any track, in any chapter.
        [Fact]
        public void A_slot_the_table_is_silent_on_still_answers_a_surfaced_silence()
        {
            foreach (var q in EveryQuery())
            {
                var table = ObjectiveTargets.Produce(q).Rows;
                var rows = TargetPass.RowsFor(table, TargetPass.SysNguMagic, 3);
                Assert.Null(rows);

                var lane = new TargetPass.LaneState
                {
                    System = TargetPass.SysNguMagic,
                    Index = 3,
                    ActiveTrack = q.NguTrack,
                    LevelOnTrack = 0,
                };
                var answer = TargetPass.Evaluate(lane, rows, FeasibilityPass.Verdict.Seat());
                Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
                Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
                Assert.False(string.IsNullOrEmpty(answer.Reason));
            }
        }

        // A whole-system row cannot be addressed by TargetPass.RowsFor, which selects on an exact
        // index — so expanding one to concrete ids would be the producer inventing reach the table
        // never had. The augment rules are the live instance: four restatements of a SELECTOR, and
        // the operator's artifact there is a chosen augment, not a level (23 §7.1 S1).
        //
        // ⚠ MEASURED WHILE WRITING THIS: the EveryId rule is DORMANT against the shipped table. Every
        // whole-system row in §2 is a rate/time/predicate, so the KIND rule denies it first and the
        // EveryId rule never fires on real data. It is therefore pinned against `Rule` directly — a
        // guard that only a future row can trip is one that must be tested without that row.
        [Fact]
        public void Whole_system_rows_are_never_expanded_to_concrete_ids()
        {
            Assert.DoesNotContain(EveryProducedRow(), r => r.System == TargetPass.SysAugments);
            Assert.Contains(EveryExclusion(), x => x.System == TargetPass.SysAugments);

            // Every whole-system row in the shipped table is excluded, one way or another.
            foreach (var row in ObjectiveTable.LaneRows.Where(r => r.Ids == ObjectiveTable.EveryId))
                Assert.False(ObjectiveTargets.Rule(row, ObjectiveTable.NoIndex).Admitted);

            // The dormant guard itself: a whole-system row that WAS a level — the shape that does not
            // exist today — is still denied, and denied for being unindexed.
            var syntheticWholeSystemLevel = new ObjectiveTable.LaneRow
            {
                Chapter = 3,
                System = TargetPass.SysAt,
                Ids = ObjectiveTable.EveryId,
                Track = TargetPass.Track.Normal,
                Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = 777, ValueHigh = 777,
                Cite = "synthetic — the shape the EveryId guard exists for",
            };
            var ruling = ObjectiveTargets.Rule(syntheticWholeSystemLevel, ObjectiveTable.NoIndex);
            Assert.False(ruling.Admitted);
            Assert.Contains("whole-system row", ruling.Rule);

            // ...and the same row WITH an id is admitted, so the denial is about the id group and
            // nothing else. This is what makes the guard a guard rather than a coincidence.
            var indexed = syntheticWholeSystemLevel;
            indexed.Ids = new[] { 2 };
            Assert.True(ObjectiveTargets.Rule(indexed, 2).Admitted);
        }

        // ---- the hold: an unknown chapter is not chapter zero --------------------------------------

        [Fact]
        public void An_unknown_chapter_holds_rather_than_supplying_every_chapter_at_once()
        {
            var held = ObjectiveTargets.Produce(new ObjectiveTargets.Query
            {
                Chapter = 0, ChapterKnown = false,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Normal,
            });

            Assert.True(held.Held);
            Assert.Null(held.Rows);
            Assert.Equal(0, held.Count);
            Assert.Contains("ChapterAny", held.HeldReason);

            // The hazard it avoids, demonstrated on the primitive: a query of ChapterAny matches
            // EVERY row (ObjectiveTable.cs:905-909), which is what a bare 0 would have meant.
            Assert.True(ObjectiveTable.ChapterMatches(3, ObjectiveTable.ChapterAny));
            Assert.True(ObjectiveTable.ChapterMatches(5, ObjectiveTable.ChapterAny));

            // And a held table is spec §10 standalone: RowsFor reads null identically to empty.
            Assert.Null(TargetPass.RowsFor(held.Rows, TargetPass.SysNguEnergy, 2));
        }

        [Fact]
        public void A_chapter_outside_one_to_eight_holds_too()
        {
            foreach (var ch in new[] { -1, 0, 9, 99 })
            {
                var t = ObjectiveTargets.Produce(new ObjectiveTargets.Query
                {
                    Chapter = ch, ChapterKnown = true,
                    NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Normal,
                });
                Assert.True(t.Held, "chapter " + ch + " did not hold");
                Assert.Null(t.Rows);
            }
        }

        // ---- T3: THE SWEEP -------------------------------------------------------------------------

        // The bridge's Pass 3 guard, verbatim, minus the live reads — the same mirror
        // Pass3WiringTests.BridgeWant uses, for the same reason: ConstraintLayerBridge reads
        // Main.Character and is deliberately not linkable into this assembly.
        private static bool BridgeWant(ConstraintLayer.LaneSpec spec, string system, int index,
            TargetPass.Track track, long level, IList<TargetPass.TargetRow> table, out string reason,
            long operatorTarget = 0L)
        {
            reason = null;
            if (spec.SurplusSink || spec.RateLane || !spec.Feasibility.Seated)
                return true;
            if (system == null)
                return true;

            // ⚠ `operatorTarget` MIRRORS LaneStateFor's AT-only read of advancedTraining.levelTarget[i]
            // and defaults to 0 — the game's UNSET sentinel, "no operator preference" — which is what
            // every sweep below supplies and why they are unmoved by the floor ruling.
            var lane = new TargetPass.LaneState
            {
                System = system, Index = index, ActiveTrack = track, LevelOnTrack = level,
                OperatorTarget = operatorTarget,
            };
            var answer = TargetPass.Evaluate(lane, TargetPass.RowsFor(table, system, index),
                spec.Feasibility);
            return ConstraintLayer.WantFromAnswer(answer, out reason);
        }

        private sealed class Lane
        {
            public ConstraintLayer.LaneSpec Spec;
            public string System;
            public int Index;
            public TargetPass.Track Track;
            public long Level;
            public long OperatorTarget;   // 0 = the game's UNSET sentinel, "no operator preference"
        }

        // Levels for the sweep, named so the two sweeps differ by ONE number and it is visible which.
        //
        // ⚠ `AtBelowCap` IS ABOVE EVERY AT PRECONDITION RUNG THE TABLE HOLDS (the highest is the
        // 60-80k range) AND BELOW THE ONE TERMINAL. That is deliberate and it is the load-bearing
        // half of the no-change proof: every precondition reads "met" in the milestone sense and
        // STILL cannot stop a lane, which is what would silently defund four AT slots if terminality
        // were derived from the level rather than carried on the row (23 §0.4).
        private const long AtBelowCap = 99_999L;
        private const long AtAtCap = 100_000L;      // == ObjectiveTable.AtBlockHardCapLevel

        // ⚠ 401 ON THE RESPAWN LANE, ON PURPOSE. It is the exact number the removed row stopped at,
        // so every case in BOTH sweeps also demonstrates that the row [OPERATOR] deleted at 08b4344
        // is genuinely gone rather than merely unreachable in the queries chosen.
        private const long RespawnAtOldStop = 401L;

        // A lane set shaped like a real energy pool, with the two levels the produced table can speak
        // to as parameters: the Respawn NGU (the removed carve-out) and the AT lanes.
        //
        // ⚠ ALL FIVE AT SLOTS TAKE THE SAME LEVEL. `ALLAT` yields five separate AdvancedTrainingBP
        // with Index 0..4 (ResourceBreakpoint.cs:321-334), each its own lane, and driving them to one
        // level together is what makes "only slot 2 stops" a measurement instead of a claim.
        //
        // `atOperatorTarget` is the operator's own advancedTraining.levelTarget[i], applied to all
        // five AT slots. It DEFAULTS TO 0 — the unset sentinel — so every sweep that does not name it
        // is measuring exactly what it measured before the floor ruling.
        private static List<Lane> LiveShapedSet(long respawnLevel, long atLevel,
            TargetPass.Track nguTrack, TargetPass.Track runTrack, long atOperatorTarget = 0L)
        {
            var lanes = new List<Lane>();

            Action<ConstraintLayer.LaneSpec, string, int, TargetPass.Track, long> add =
                (spec, system, index, track, level) =>
                    lanes.Add(new Lane
                    {
                        Spec = spec, System = system, Index = index, Track = track, Level = level,
                    });

            add(new ConstraintLayer.LaneSpec
            {
                Name = "TimeMachineBP", Label = "TM",
                Feasibility = FeasibilityPass.Verdict.Refuse(
                    "gold stall: bar unstarted and realGold 0 < cost 5"),
                Capacity = 5000,
            }, TargetPass.SysTmSpeed, 0, runTrack, 12);

            for (int i = 0; i <= 8; i++)
                add(new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP", Label = "NGU-" + i,
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 100 + i * 37,
                }, TargetPass.SysNguEnergy, i, nguTrack, i == 2 ? respawnLevel : 250 + i * 90);

            for (int i = 0; i <= 4; i++)
            {
                add(new ConstraintLayer.LaneSpec
                {
                    Name = "AdvancedTrainingBP", Label = "AT-" + i,
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 640 - i * 55,
                }, TargetPass.SysAt, i, runTrack, atLevel);
                lanes[lanes.Count - 1].OperatorTarget = atOperatorTarget;
            }

            add(new ConstraintLayer.LaneSpec
            {
                Name = "AugmentBP", Label = "AUG-4",
                Feasibility = FeasibilityPass.Verdict.Seat(), Capacity = 2200,
            }, TargetPass.SysAugments, 4, runTrack, 8000);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "AugmentBP", Label = "AUG-5",
                Feasibility = FeasibilityPass.Verdict.Seat(), Capacity = 1900,
            }, TargetPass.SysAugments, 5, runTrack, 7400);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "BasicTrainingBP", Label = "BT-3",
                Feasibility = FeasibilityPass.Verdict.Seat(), Capacity = 0,   // Pass 2 eliminates it
            }, null, 3, runTrack, 0);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "BR", Label = "BR",
                Feasibility = FeasibilityPass.Verdict.Seat(), Capacity = 700,
            }, null, 0, runTrack, 0);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "Beards", Label = "BEARD",
                Feasibility = FeasibilityPass.Verdict.Seat(), NoAllocation = true,
            }, null, 0, runTrack, 0);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "NGUBP", Label = "CAPNGU-6",
                Feasibility = FeasibilityPass.Verdict.Seat(), Capacity = 3100,
                RateLane = true,                              // Evil-track NGU: Pass 3 never sees it
            }, TargetPass.SysNguEnergy, 6, nguTrack, 44);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP", Label = "WAN",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting, SurplusSink = true,
            }, TargetPass.SysWandoos, 0, runTrack, 0);

            return lanes;
        }

        private static List<ConstraintLayer.LaneSpec> Wanted(List<Lane> lanes,
            IList<TargetPass.TargetRow> table, bool usePass3)
        {
            var specs = new List<ConstraintLayer.LaneSpec>(lanes.Count);
            foreach (var l in lanes)
            {
                var spec = l.Spec;
                if (usePass3)
                {
                    string why;
                    spec.WantsMore = BridgeWant(spec, l.System, l.Index, l.Track, l.Level, table,
                        out why, l.OperatorTarget);
                    spec.WantReason = why;
                }
                else
                {
                    spec.WantsMore = true;                    // the shipped literal
                    spec.WantReason = null;
                }
                specs.Add(spec);
            }
            return specs;
        }

        private static readonly long[] Pools =
            { 0, 1, 97, 1000, 12_345, 250_000, 9_000_000, long.MaxValue / 4 };

        private static readonly BudgetPass.BudgetState[] Budgets =
        {
            new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 },
            new BudgetPass.BudgetState { InLevelChallenge = true, RebirthLevels = 100 },
        };

        // Returns the labels of every lane whose plan row differs between the two compositions, so a
        // divergence is NAMED rather than merely detected. Empty == byte-identical.
        private static List<string> Divergences(long pool, BudgetPass.BudgetState budget,
            List<Lane> lanes, IList<TargetPass.TargetRow> table)
        {
            var oldPlan = ConstraintLayer.Compose(pool, budget, Wanted(lanes, null, false));
            var newPlan = ConstraintLayer.Compose(pool, budget, Wanted(lanes, table, true));

            var diffs = new List<string>();

            if (oldPlan.Pool != newPlan.Pool ||
                oldPlan.CapacitiesKnown != newPlan.CapacitiesKnown ||
                oldPlan.SinkIndex != newPlan.SinkIndex ||
                oldPlan.SinkSeated != newPlan.SinkSeated ||
                oldPlan.SinkAllocation != newPlan.SinkAllocation ||
                oldPlan.SinkRefusalReason != newPlan.SinkRefusalReason ||
                oldPlan.Unallocated != newPlan.Unallocated ||
                oldPlan.UnallocatedReason != newPlan.UnallocatedReason ||
                oldPlan.BudgetExhausted != newPlan.BudgetExhausted ||
                oldPlan.BudgetMessage != newPlan.BudgetMessage ||
                oldPlan.RateLanesSkipped != newPlan.RateLanesSkipped ||
                oldPlan.RateSkipCheapest != newPlan.RateSkipCheapest ||
                oldPlan.RateSkipPool != newPlan.RateSkipPool ||
                oldPlan.Vacuity.Vacuous != newPlan.Vacuity.Vacuous ||
                oldPlan.Vacuity.TotalCapacity != newPlan.Vacuity.TotalCapacity ||
                oldPlan.Vacuity.Surplus != newPlan.Vacuity.Surplus)
                diffs.Add("<plan>");

            Assert.Equal(oldPlan.Lanes.Length, newPlan.Lanes.Length);
            for (int i = 0; i < oldPlan.Lanes.Length; i++)
            {
                var o = oldPlan.Lanes[i];
                var n = newPlan.Lanes[i];
                if (o.Allocation != n.Allocation || o.Seated != n.Seated ||
                    o.EliminatedBy != n.EliminatedBy || o.Reason != n.Reason ||
                    o.Capacity != n.Capacity || o.RateLane != n.RateLane ||
                    o.NoAllocation != n.NoAllocation || o.SurplusSink != n.SurplusSink)
                    diffs.Add(o.Label ?? o.Name);
            }

            if (oldPlan.Lanes.Sum(l => l.Allocation) != newPlan.Lanes.Sum(l => l.Allocation))
                diffs.Add("<total>");

            return diffs;
        }

        // THE PROOF. Supplying the produced table changes NOTHING — not one unit of the pool, not one
        // seat, not one surfaced reason — across every pool size, both budget states, all eight
        // chapters and every track pair, while no lane sits at a met terminal.
        //
        // The AT lanes sit one level below the hard cap and above every precondition rung the table
        // holds, and the Respawn lane sits exactly on the removed row's old stop. So every
        // precondition reads "met" in the milestone sense, the deleted NGU terminal is exercised at
        // the one level that would have fired it, and NOTHING STOPS.
        [Fact]
        public void Supplying_the_produced_table_changes_no_allocation_while_no_terminal_is_met()
        {
            int cases = 0;
            foreach (var q in EveryQuery())
            {
                var table = ObjectiveTargets.Produce(q).Rows;
                var lanes = LiveShapedSet(RespawnAtOldStop, AtBelowCap, q.NguTrack, q.RunTrack);

                foreach (var pool in Pools)
                foreach (var budget in Budgets)
                {
                    var diffs = Divergences(pool, budget, lanes, table);
                    Assert.True(diffs.Count == 0,
                        "chapter " + q.Chapter + " ngu=" + q.NguTrack + " run=" + q.RunTrack +
                        " pool " + pool + ": " + string.Join(", ", diffs));
                    cases++;
                }
            }

            Assert.Equal(8 * 3 * 3 * Pools.Length * Budgets.Length, cases);
            Assert.Equal(1152, cases);
        }

        // Which lanes the new composition eliminated at PASS 3 specifically — the only pass the
        // target table can reach. Distinct from `Divergences`, which reports every lane whose row
        // moved for any reason, including the knock-on described below.
        private static List<string> TargetEliminated(long pool, BudgetPass.BudgetState budget,
            List<Lane> lanes, IList<TargetPass.TargetRow> table)
        {
            var plan = ConstraintLayer.Compose(pool, budget, Wanted(lanes, table, true));
            return plan.Lanes
                .Where(l => l.EliminatedBy == ConstraintLayer.PassId.Target)
                .Select(l => l.Label ?? l.Name)
                .ToList();
        }

        // Which lanes the new composition eliminated at PASS 0 — the budget. Named separately because
        // Pass 0 runs FIRST and PRE-EMPTS Pass 3 (ConstraintLayer.Compose:135-143): a lane refused
        // there never reaches the target pass, so the control cannot fire on it and must say so
        // rather than counting it as "no change".
        private static List<string> BudgetEliminated(long pool, BudgetPass.BudgetState budget,
            List<Lane> lanes, IList<TargetPass.TargetRow> table)
        {
            var plan = ConstraintLayer.Compose(pool, budget, Wanted(lanes, table, true));
            return plan.Lanes
                .Where(l => l.EliminatedBy == ConstraintLayer.PassId.Budget)
                .Select(l => l.Label ?? l.Name)
                .ToList();
        }

        // ⚠ THE NEGATIVE CONTROL. The sweep above must be ABLE to fail, or it proves nothing (48 §4).
        // Same sweep, same lane set, ONE NUMBER CHANGED — the AT lanes at 100,000 instead of 99,999 —
        // and it fires: at chapter 5 on the Evil RUN track, and NOWHERE ELSE. Both halves are the
        // control. A sweep that stayed silent here would be proving nothing; one that fired everywhere
        // would be detecting its own noise.
        //
        // ⚠ THE 1,152 CASES PARTITION THREE WAYS, NOT TWO, AND THE THIRD BUCKET IS A REAL FINDING.
        //   · 1,104 OUT OF SCOPE — no writable row at that chapter/track. Silent, and must be.
        //   ·    24 IN SCOPE, BUDGET OPEN — the control fires. AT-2 stops at Pass 3.
        //   ·    24 IN SCOPE, BUDGET EXHAUSTED — silent, because `AdvancedTrainingBP` IS one of the
        //        nine canLevel() counting sites (BudgetPass.cs:186-189) and Pass 0 already refused the
        //        lane. THE BLOCK-AT STOP IS INERT INSIDE A 100-LEVEL CHALLENGE. The branch this file
        //        came from could not have seen this: its subject was NGUBP, which is exempt by
        //        omission, so both budget states behaved alike and all 48 fired. Asserted below rather
        //        than absorbed into the silent count, because a control that quietly stops firing is
        //        indistinguishable from one that never could.
        //
        // ⚠ AND IT IS NOT A ONE-LANE CHANGE. Measured, not predicted: eliminating AT-2 removes a SEAT,
        // and amendment 28 §5.2a made the denominator the count of seated destinations not yet offered
        // — so every remaining lane's share moves with it. The row that stops is one; the allocation
        // that moves is the whole pool. That is why the assertion below is about PASS 3 ELIMINATIONS
        // rather than about which labels appear in the diff.
        [Fact]
        public void The_same_sweep_detects_the_change_when_the_Block_AT_terminal_is_met()
        {
            int fired = 0, outOfScope = 0, preempted = 0;
            bool sawKnockOn = false;

            foreach (var q in EveryQuery())
            {
                var table = ObjectiveTargets.Produce(q).Rows;
                var lanes = LiveShapedSet(RespawnAtOldStop, AtAtCap, q.NguTrack, q.RunTrack);
                bool rowLive = q.Chapter == 5 && q.RunTrack == TargetPass.Track.Evil;

                foreach (var pool in Pools)
                foreach (var budget in Budgets)
                {
                    var diffs = Divergences(pool, budget, lanes, table);
                    var stopped = TargetEliminated(pool, budget, lanes, table);

                    if (!rowLive)
                    {
                        Assert.True(diffs.Count == 0,
                            "chapter " + q.Chapter + " run=" + q.RunTrack + " changed allocation " +
                            "with no writable row in scope: " + string.Join(", ", diffs));
                        Assert.Empty(stopped);
                        outOfScope++;
                        continue;
                    }

                    if (BudgetPass.Exhausted(budget))
                    {
                        // PASS 0 PRE-EMPTS PASS 3. Named, not swallowed: the lane is out before the
                        // target pass runs, so nothing changed and nothing could have.
                        Assert.Contains("AT-2", BudgetEliminated(pool, budget, lanes, table));
                        Assert.True(diffs.Count == 0,
                            "the budget refused AT-2 at Pass 0 and yet the plans differ: " +
                            string.Join(", ", diffs));
                        Assert.Empty(stopped);
                        preempted++;
                        continue;
                    }

                    // THE CONTROL FIRES: the sweep detected a change...
                    Assert.NotEmpty(diffs);

                    // ...and exactly one lane was stopped BY PASS 3, and it is AT slot 2.
                    Assert.Single(stopped);
                    Assert.Equal("AT-2", stopped[0]);

                    // ⚠ THE FOUR SIBLING SLOTS ARE AT THE SAME LEVEL AND ARE NOT STOPPED. This is the
                    // scope claim, measured: RowsFor filters on Index, so a row for slot 2 is not a
                    // row for the system.
                    foreach (var sibling in new[] { "AT-0", "AT-1", "AT-3", "AT-4" })
                        Assert.DoesNotContain(sibling, stopped);

                    // The three shapes Pass 3 never sees are untouched by the same table, and so is
                    // the lane whose terminal [OPERATOR] deleted — at the very level it used to stop.
                    Assert.DoesNotContain("CAPNGU-6", stopped);   // rate lane (amendment 18 §1.2)
                    Assert.DoesNotContain("WAN", stopped);        // the surplus sink (spec §8)
                    Assert.DoesNotContain("NGU-2", stopped);      // the removed Respawn row, at 401

                    if (diffs.Any(d => d != "AT-2" && d != "<plan>" && d != "<total>"))
                        sawKnockOn = true;

                    fired++;
                }
            }

            // EXACT FIGURES, all three buckets, summing to the whole sweep.
            Assert.Equal(24, fired);
            Assert.Equal(1104, outOfScope);
            Assert.Equal(24, preempted);
            Assert.Equal(1152, fired + outOfScope + preempted);
            Assert.Equal(8 * 3 * 3 * Pools.Length * Budgets.Length, fired + outOfScope + preempted);

            // The knock-on, asserted rather than described: one row stops one lane, and the seat it
            // vacates moves units in others.
            Assert.True(sawKnockOn,
                "the stop changed only its own lane — the divisor knock-on is not what was " +
                "measured, and the finding above should be re-stated");
        }

        // ⚠ THE FLOOR RULING AT THE COMPOSITION LEVEL — THE SAME 1,152 CASES, THE OPERATOR'S NUMBER
        // SUPPLIED. [OPERATOR] 2026-08-07: "the operator's higher target should win over the ruled cap
        // but it should never be capped below the 100,000 level."
        //
        // The control above drives the AT lanes to exactly the cap and fires 24 times. Hand-set
        // levelTarget[2] to 250,000 with the slots still at 100,000 and it must fire ZERO times: the
        // operator's number is not met, so the lane keeps its seat and the whole plan returns to the
        // shipped `WantsMore = true` shape byte for byte. That last part is the load-bearing half —
        // amendment 28 §5.2a makes the denominator the count of seated destinations, so a lane that
        // keeps its seat is a POOL-WIDE difference, and asserting "AT-2 was not eliminated" alone
        // would miss whether the units went back where they came from.
        //
        // ⚠ AND THE THREE BUCKETS BECOME TWO, WHICH IS THE MEASUREMENT. 1,104 out-of-scope + 24
        // budget-pre-empted are unchanged (neither depends on the target), and the 24 that FIRED are
        // now 24 that DO NOT. Nothing else moved.
        [Fact]
        public void The_operators_own_higher_target_un_fires_the_control_and_restores_the_plan()
        {
            const long handSet = 250_000L;
            int wouldHaveFired = 0, outOfScope = 0, preempted = 0;

            foreach (var q in EveryQuery())
            {
                var table = ObjectiveTargets.Produce(q).Rows;

                // identical to the control's lane set except for the operator's own field
                var lanes = LiveShapedSet(RespawnAtOldStop, AtAtCap, q.NguTrack, q.RunTrack, handSet);
                bool rowLive = q.Chapter == 5 && q.RunTrack == TargetPass.Track.Evil;

                foreach (var pool in Pools)
                foreach (var budget in Budgets)
                {
                    // NOTHING is eliminated at Pass 3 anywhere in the sweep, in scope or out.
                    Assert.Empty(TargetEliminated(pool, budget, lanes, table));

                    // ...and the plan is byte-identical to the shipped `WantsMore = true` one.
                    var diffs = Divergences(pool, budget, lanes, table);
                    Assert.True(diffs.Count == 0,
                        "chapter " + q.Chapter + " run=" + q.RunTrack + " pool " + pool +
                        ": the operator's " + handSet + " was supplied and the plan still moved: " +
                        string.Join(", ", diffs));

                    if (!rowLive) outOfScope++;
                    else if (BudgetPass.Exhausted(budget)) preempted++;
                    else wouldHaveFired++;
                }
            }

            // The same partition as the control, with the middle bucket inverted: these 24 are the
            // cases that fired there and are silent here.
            Assert.Equal(24, wouldHaveFired);
            Assert.Equal(1104, outOfScope);
            Assert.Equal(24, preempted);
            Assert.Equal(1152, wouldHaveFired + outOfScope + preempted);

            // ⚠ NON-VACUITY. "Nothing was eliminated" is only worth reading if the SAME sweep with the
            // operator target unset still eliminates — otherwise this would pass on a broken table.
            int stillFires = 0;
            foreach (var q in EveryQuery().Where(x =>
                         x.Chapter == 5 && x.RunTrack == TargetPass.Track.Evil))
            {
                var table = ObjectiveTargets.Produce(q).Rows;
                var unset = LiveShapedSet(RespawnAtOldStop, AtAtCap, q.NguTrack, q.RunTrack);
                foreach (var pool in Pools)
                    if (TargetEliminated(pool, new BudgetPass.BudgetState(), unset, table).Count > 0)
                        stillFires++;
            }
            Assert.Equal(3 * Pools.Length, stillFires);

            // AND THE STOP IS NOT MERELY DEFERRED — it moved to the operator's number. At 250,000 the
            // lane stops again, which is what "the operator's higher target WINS" means: it is still
            // a stop, at their level.
            var atTheirNumber = LiveShapedSet(RespawnAtOldStop, handSet, TargetPass.Track.Normal,
                TargetPass.Track.Evil, handSet);
            var liveTable = ObjectiveTargets.Produce(new ObjectiveTargets.Query
            {
                Chapter = 5, ChapterKnown = true,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Evil,
            }).Rows;
            var stopped = TargetEliminated(100_000, new BudgetPass.BudgetState(), atTheirNumber, liveTable);
            Assert.Single(stopped);
            Assert.Equal("AT-2", stopped[0]);
        }

        // ⚠ THE SCOPE CLAIM ON ITS OWN, AT ONE QUERY AND WITHOUT THE COMPOSITION IN THE WAY. All five
        // AT slots at exactly the hard cap, each asked Pass 3's question directly: slot 2 is stopped
        // with the game's own comparator quoted, and slots 0, 1, 3 and 4 answer a SILENCE and keep
        // their want open. "Advanced Training is never terminated as a system" is this, executable.
        [Fact]
        public void Only_AT_slot_2_is_stopped_when_all_five_slots_sit_at_the_hard_cap()
        {
            var table = ObjectiveTargets.Produce(new ObjectiveTargets.Query
            {
                Chapter = 5, ChapterKnown = true,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Evil,
            }).Rows;

            for (int slot = 0; slot <= 4; slot++)
            {
                var lane = new TargetPass.LaneState
                {
                    System = TargetPass.SysAt,
                    Index = slot,
                    ActiveTrack = TargetPass.Track.Evil,
                    LevelOnTrack = AtAtCap,
                };
                var rows = TargetPass.RowsFor(table, TargetPass.SysAt, slot);
                var answer = TargetPass.Evaluate(lane, rows, FeasibilityPass.Verdict.Seat());

                string why;
                bool wants = ConstraintLayer.WantFromAnswer(answer, out why);

                if (slot == 2)
                {
                    Assert.NotNull(rows);
                    Assert.Single(rows);
                    Assert.Equal(TargetPass.Disposition.WriteTarget, answer.Disposition);
                    Assert.Equal(TargetPass.Satisfaction.Satisfied, answer.Satisfaction);
                    Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, answer.TargetToWrite);
                    Assert.False(wants);
                    Assert.Contains("target met", why);
                    Assert.Contains("100000", why);
                }
                else
                {
                    Assert.Null(rows);                                  // RowsFor filters on Index
                    Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
                    Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
                    Assert.True(wants, "AT slot " + slot + " was stopped by a row for slot 2");
                    Assert.Null(why);
                    // ⚠ A SILENCE IS NOT A ZERO: the slot surfaces the ledger's own words, and no
                    // number is written. 0 is the game's UNSET sentinel and would fund forever.
                    Assert.False(string.IsNullOrEmpty(answer.Reason));
                    Assert.Equal(0L, answer.TargetToWrite);
                }
            }

            // ...and one level short of the cap, slot 2 keeps funding too. The stop is a comparator,
            // not a state the lane enters near the number.
            var justBelow = new TargetPass.LaneState
            {
                System = TargetPass.SysAt, Index = 2,
                ActiveTrack = TargetPass.Track.Evil, LevelOnTrack = AtBelowCap,
            };
            var belowAnswer = TargetPass.Evaluate(justBelow,
                TargetPass.RowsFor(table, TargetPass.SysAt, 2), FeasibilityPass.Verdict.Seat());
            string belowWhy;
            Assert.True(ConstraintLayer.WantFromAnswer(belowAnswer, out belowWhy));
            Assert.Null(belowWhy);
            Assert.Equal(TargetPass.Satisfaction.Unsatisfied, belowAnswer.Satisfaction);
        }

        // ⚠ THE TRACK IS PART OF THE SCOPE, NOT DECORATION. The row is Evil-track only, and AT lanes
        // read the RUN's difficulty (ConstraintLayerBridge.LaneStateFor: "NO OTHER SYSTEM HAS A
        // PER-TRACK LEVEL"). On a Normal or Sadistic run the row is not even produced, so slot 2 at
        // the cap funds exactly as slots 0/1/3/4 do.
        [Fact]
        public void On_a_non_Evil_run_the_Block_AT_stop_does_not_exist_at_all()
        {
            foreach (var run in new[] { TargetPass.Track.Normal, TargetPass.Track.Sadistic })
            {
                var table = ObjectiveTargets.Produce(new ObjectiveTargets.Query
                {
                    Chapter = 5, ChapterKnown = true,
                    NguTrack = TargetPass.Track.Normal, RunTrack = run,
                }).Rows;

                Assert.Empty(ObjectiveTargets.Writable(table));

                var lane = new TargetPass.LaneState
                {
                    System = TargetPass.SysAt, Index = 2,
                    ActiveTrack = run, LevelOnTrack = long.MaxValue,
                };
                var answer = TargetPass.Evaluate(lane,
                    TargetPass.RowsFor(table, TargetPass.SysAt, 2), FeasibilityPass.Verdict.Seat());
                string why;
                Assert.True(ConstraintLayer.WantFromAnswer(answer, out why),
                    "the Evil-track Block AT row stopped a " + run + " run");
            }
        }

        // What the control actually does to the lane, named: eliminated at PASS 3, unseated, zero,
        // with the surfaced reason quoting the game's own comparator and the number.
        [Fact]
        public void The_Block_AT_cap_eliminates_slot_2_at_pass_3_with_a_surfaced_reason()
        {
            var q = new ObjectiveTargets.Query
            {
                Chapter = 5, ChapterKnown = true,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Evil,
            };
            var table = ObjectiveTargets.Produce(q).Rows;
            var lanes = LiveShapedSet(RespawnAtOldStop, AtAtCap, q.NguTrack, q.RunTrack);
            var specs = Wanted(lanes, table, true);

            int block = lanes.FindIndex(l => l.System == TargetPass.SysAt && l.Index == 2);

            Assert.False(specs[block].WantsMore);
            Assert.Contains("target met", specs[block].WantReason);
            Assert.Contains("100000", specs[block].WantReason);

            var plan = ConstraintLayer.Compose(100_000, new BudgetPass.BudgetState(), specs);
            Assert.Equal(ConstraintLayer.PassId.Target, plan.Lanes[block].EliminatedBy);
            Assert.False(plan.Lanes[block].Seated);
            Assert.Equal(0, plan.Lanes[block].Allocation);
            Assert.True(plan.SinkSeated);

            // Exactly one lane in the whole plan was eliminated at Pass 3.
            Assert.Single(plan.Lanes, l => l.EliminatedBy == ConstraintLayer.PassId.Target);

            // One short of it and the lane is funded exactly as before — the other half of the wire,
            // and the one that would defund the whole system if WantFromAnswer inverted.
            var below = Wanted(LiveShapedSet(RespawnAtOldStop, AtBelowCap, q.NguTrack, q.RunTrack),
                table, true);
            Assert.True(below[block].WantsMore);
            Assert.Null(below[block].WantReason);
        }

        // ---- B7: Pass 3's stop and the LIVE WRITER now say the same number -------------------------

        // ⚠ THE TWO STOPS DISAGREED TWENTY-FOLD UNTIL 3e9816d. LevelPlanner writes a target into
        // `advancedTraining.levelTarget[2]`, and that field is what `AdvancedTrainingBP.TargetMet()`
        // — and therefore the `IsValid()` membership filter — compares against. It used to write
        // ceil(49 / block.levelFactor) ≈ 5,000; the objective table said 100,000. Wiring Pass 3 while
        // those differed would have put two stops on one lane at different numbers.
        //
        // They now agree BY CONSTRUCTION, not by coincidence: both sides read
        // `ObjectiveTable.AtBlockHardCapLevel`, one const. This test pins the agreement at the
        // COMPARATOR level — same field, same number, same verdict at the boundary — and
        // OperatorRuledRowsTests pins that both sides still read the const at the source.
        //
        // ⚠ WHAT HAPPENS IF THEY EVER DISAGREE AGAIN: the membership filter runs FIRST (IsValid,
        // before a spec is built), so the LOWER of the two wins and Pass 3 can only ever stop a lane
        // the game's own field has not stopped yet. A LevelPlanner number BELOW the table's makes
        // Pass 3 dead on this lane — silent, no surfaced reason, the exact drift 818759b was written
        // about. Neither is caught by the field write alone; this is the test that would catch it.
        //
        // ⚠ THE OTHER DIRECTION IS NO LONGER A DISAGREEMENT — [OPERATOR] RULED ON IT. A LevelPlanner
        // number ABOVE the table's is what `AdvancedTrainingPurposeFloor` deliberately permits (it
        // keeps an operator's higher hand-set target), and this test used to RECORD, as a measurement,
        // that Pass 3 then defunded the slot at 100,000 anyway while the operator's own field read
        // unmet. 2026-08-07: "the operator's higher target should win over the ruled cap but it
        // should never be capped below the 100,000 level." Pass 3 now floors the row's value with the
        // live field through that same function, so the two sides carry the same number by
        // construction and the block below asserts the AGREEMENT the old one asserted the absence of.
        // The rule's own value table lives in OperatorRuledRowsTests §W4.
        [Fact]
        public void Pass_3_and_the_live_writer_stop_the_Block_slot_at_the_same_number()
        {
            var table = ObjectiveTargets.Produce(new ObjectiveTargets.Query
            {
                Chapter = 5, ChapterKnown = true,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Evil,
            }).Rows;

            // What LevelPlanner writes into levelTarget[2] on an UNSET slot: the floor takes the stop.
            long written = LaneTargets.AdvancedTrainingPurposeFloor(0L,
                ObjectiveTable.AtBlockHardCapLevel);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, written);

            // What Pass 3 would write, read off the produced table.
            var writable = ObjectiveTargets.Writable(table);
            Assert.Single(writable);
            Assert.Equal(written, TargetPass.Route(writable[0]).TargetToWrite);

            // ...and the two comparators agree at the boundary and on both sides of it. The game's is
            // LaneTargets.AdvancedTrainingTargetMet (the one AdvancedTrainingBP.TargetMetAt calls);
            // Pass 3's is TargetPass.TargetMetByGame, reached through Evaluate.
            foreach (var level in new[] { 0L, 1L, 5_000L, 99_999L, 100_000L, 100_001L, 5_000_000L })
            {
                bool gameSaysMet = LaneTargets.AdvancedTrainingTargetMet(written, level);

                var answer = TargetPass.Evaluate(new TargetPass.LaneState
                {
                    System = TargetPass.SysAt, Index = 2,
                    ActiveTrack = TargetPass.Track.Evil, LevelOnTrack = level,
                }, TargetPass.RowsFor(table, TargetPass.SysAt, 2), FeasibilityPass.Verdict.Seat());

                bool passThreeSaysMet = answer.Satisfaction == TargetPass.Satisfaction.Satisfied;

                Assert.True(gameSaysMet == passThreeSaysMet,
                    "level " + level + ": the game's comparator says " + gameSaysMet +
                    " and Pass 3 says " + passThreeSaysMet + " — the live write and the table have " +
                    "drifted apart again");
            }

            // THE DIVERGENCE THIS FILE USED TO RECORD, NOW CLOSED — same scenario, opposite verdict.
            // An operator's HIGHER hand-set target is kept by the floor on the writer's side AND is
            // now the stop Pass 3 enforces, because Evaluate floors the row's value with the live
            // field (LaneState.OperatorTarget) through that same function. At 150,000 with 250,000
            // hand-set, the game reads UNMET and so does Pass 3, and the lane keeps being funded.
            //
            // ⚠ THE OPERATOR TARGET MUST ARRIVE AS DATA FOR THIS TO BE TRUE. Leaving it unset is 0 —
            // the game's UNSET sentinel and "no preference" here — which floors to the table's own
            // 100,000, so the two lanes below differ ONLY in what the caller supplied. That is the
            // wiring the bridge does (LaneStateFor reads advancedTraining.levelTarget[i]) and it is
            // asserted at the source in OperatorRuledRowsTests.
            long operatorTarget = LaneTargets.AdvancedTrainingPurposeFloor(250_000L,
                ObjectiveTable.AtBlockHardCapLevel);
            Assert.Equal(250_000L, operatorTarget);          // the floor keeps the higher number
            Assert.False(LaneTargets.AdvancedTrainingTargetMet(operatorTarget, 150_000L));

            var midAnswer = TargetPass.Evaluate(new TargetPass.LaneState
            {
                System = TargetPass.SysAt, Index = 2,
                ActiveTrack = TargetPass.Track.Evil, LevelOnTrack = 150_000L,
                OperatorTarget = 250_000L,
            }, TargetPass.RowsFor(table, TargetPass.SysAt, 2), FeasibilityPass.Verdict.Seat());
            Assert.Equal(TargetPass.Satisfaction.Unsatisfied, midAnswer.Satisfaction);
            Assert.Equal(operatorTarget, midAnswer.TargetToWrite);

            // ...and with nothing supplied, the table's number still stands. Both halves of the
            // ruling, at one level, in two lines.
            var unsupplied = TargetPass.Evaluate(new TargetPass.LaneState
            {
                System = TargetPass.SysAt, Index = 2,
                ActiveTrack = TargetPass.Track.Evil, LevelOnTrack = 150_000L,
            }, TargetPass.RowsFor(table, TargetPass.SysAt, 2), FeasibilityPass.Verdict.Seat());
            Assert.Equal(TargetPass.Satisfaction.Satisfied, unsupplied.Satisfaction);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, unsupplied.TargetToWrite);
        }

        // ---- B4: what the objective table can and cannot supply -------------------------------------

        // ⚠ O1 IS HALF DISCHARGED BY THIS PRODUCER, and the half that is not is a property of the
        // SOURCE rather than of the filters. `00-STATE` §A5 item 1 states O1 as "augment targets
        // (7 pairs x 2 halves) and AT (5 ids) are undeclared". The objective table supplies:
        //   · augments — ZERO level rows on any track, by principle (23 §7.1 S1: the guide's method is
        //     a live per-rebirth solver, so the operator's artifact is A CHOSEN AUGMENT, not a level —
        //     a different SHAPE from what augmentTarget consumes). Still undeclared, and not for want
        //     of a filter.
        //   · AT — level rows in FOUR of the five slots, and since d614347/3e9816d EXACTLY ONE of them
        //     is terminal: slot 2 at the hard cap. The branch this file came from asserted "not one AT
        //     terminal exists"; [OPERATOR] made one on 2026-08-07 and it is the only reason the
        //     produced table can move a unit of the pool at all.
        // This test is the ledger entry, executable.
        [Fact]
        public void The_objective_table_supplies_no_augment_target_and_exactly_one_AT_terminal()
        {
            Assert.DoesNotContain(ObjectiveTable.LaneRows, r =>
                r.System == TargetPass.SysAugments && r.Kind == TargetPass.RowKind.Level);
            Assert.DoesNotContain(EveryProducedRow(), r => r.System == TargetPass.SysAugments);

            var atLevels = ObjectiveTable.LaneRows
                .Where(r => r.System == TargetPass.SysAt && r.Kind == TargetPass.RowKind.Level)
                .ToList();
            Assert.NotEmpty(atLevels);

            var atTerminals = atLevels
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal)
                .ToList();
            Assert.Single(atTerminals);
            Assert.Equal(new[] { 2 }, atTerminals[0].Ids);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, atTerminals[0].ValueLow);
            Assert.Equal(TargetPass.Track.Evil, atTerminals[0].Track);
            Assert.Null(atTerminals[0].CampaignScope);

            // Every OTHER AT level row is a precondition and cannot write, at any level.
            Assert.All(atLevels.Where(r => r.Terminality != TargetPass.Terminality.Terminal),
                r => Assert.Equal(TargetPass.Terminality.Precondition, r.Terminality));
            Assert.Equal(atLevels.Count - 1,
                atLevels.Count(r => r.Terminality == TargetPass.Terminality.Precondition));

            // ...and the produced side agrees: of the AT rows emitted, exactly the slot-2 hard cap
            // routes to a write, and only on the Evil track.
            var producedAt = EveryProducedRow().Where(r => r.System == TargetPass.SysAt).ToList();
            Assert.NotEmpty(producedAt);
            Assert.All(producedAt.Where(r =>
                    TargetPass.Route(r).Disposition == TargetPass.Disposition.WriteTarget),
                r =>
                {
                    Assert.Equal(2, r.Index);
                    Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, r.ValueLow);
                    Assert.Equal(TargetPass.Track.Evil, r.Track);
                });
            Assert.Contains(producedAt, r =>
                TargetPass.Route(r).Disposition == TargetPass.Disposition.WriteTarget);
        }

        // The SHAPE question, answered against the two types rather than in prose: what the objective
        // layer materialises IS what the bridge's field declares. There is no adapter and no
        // translation step — which is exactly why a precondition cannot become a target in transit.
        [Fact]
        public void ToTargetRow_produces_the_shape_the_bridge_field_declares()
        {
            // The row this is measured on is the ONE that now crosses into a live allocation — the
            // Block AT hard cap. It used to be Respawn 401, which [OPERATOR] removed at 08b4344.
            var source = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysAt && r.Ids != null && r.Ids.Contains(2) &&
                r.Kind == TargetPass.RowKind.Level &&
                r.Terminality == TargetPass.Terminality.Terminal);

            TargetPass.TargetRow materialised = source.ToTargetRow(2);

            Assert.Equal(source.System, materialised.System);
            Assert.Equal(2, materialised.Index);
            Assert.Equal(source.Track, materialised.Track);
            Assert.Equal(source.Kind, materialised.Kind);
            Assert.Equal(source.Terminality, materialised.Terminality);
            Assert.Equal(source.ValueLow, materialised.ValueLow);
            Assert.Equal(source.ValueHigh, materialised.ValueHigh);
            Assert.Equal(source.CampaignScope, materialised.CampaignScope);
            Assert.Equal(source.Cite, materialised.Cite);

            // The field the bridge declares takes it without a cast: IList<TargetPass.TargetRow>.
            IList<TargetPass.TargetRow> asBridgeField = new List<TargetPass.TargetRow> { materialised };
            Assert.NotNull(TargetPass.RowsFor(asBridgeField, TargetPass.SysAt, 2));

            // ⚠ AND RowsFor IS WHAT MAKES THIS ONE SLOT RATHER THAN ONE SYSTEM. The same table,
            // queried for the other four AT slots, returns NULL — which Evaluate reads as "no rows"
            // and answers Silent. This is the mechanism the whole scope claim rests on.
            foreach (var otherSlot in new[] { 0, 1, 3, 4 })
                Assert.Null(TargetPass.RowsFor(asBridgeField, TargetPass.SysAt, otherSlot));

            // ⚠ THE ONE SHAPE GAP, and it fails closed: an AllTracks row materialises with
            // Track.Unspecified, which RouteLevel refuses as "row without a track is unusable".
            // Every AllTracks row in §2 is a predicate or a rate, so it costs nothing today — and
            // this asserts it stays that way.
            Assert.All(ObjectiveTable.LaneRows.Where(r => r.AllTracks),
                r => Assert.NotEqual(TargetPass.RowKind.Level, r.Kind));
        }

        // ⚠ BOTH STEPS ARE DONE NOW — the producer exists AND ConstraintLayerBridge assigns the field
        // from it. This test is the census of what the second step actually delivers, at the two
        // queries that matter: the busiest non-writing chapter, and the one chapter/track pair where
        // a row can stop a lane. Exact figures, so a row appearing or vanishing is caught here rather
        // than surfacing as an allocation change nobody sized.
        [Fact]
        public void Both_steps_are_done_and_this_is_the_census_of_what_the_second_supplies()
        {
            // Chapter 3, Normal/Normal — three PRECONDITION rows (at-0 and at-1 at 60-80k, at-2 at
            // the 99% rung of 5,000) and nothing that can write.
            var quiet = ObjectiveTargets.Produce(new ObjectiveTargets.Query
            {
                Chapter = 3, ChapterKnown = true,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Normal,
            });
            Assert.False(quiet.Held);
            Assert.Equal(3, quiet.Count);
            Assert.Empty(ObjectiveTargets.Writable(quiet.Rows));
            Assert.All(quiet.Rows,
                r => Assert.Equal(TargetPass.Terminality.Precondition, r.Terminality));

            // Chapter 5, Evil run track — ONE row, the hard cap, and it is the entire live effect of
            // wiring this producer.
            var live = ObjectiveTargets.Produce(new ObjectiveTargets.Query
            {
                Chapter = 5, ChapterKnown = true,
                NguTrack = TargetPass.Track.Normal, RunTrack = TargetPass.Track.Evil,
            });
            Assert.False(live.Held);
            Assert.Equal(1, live.Count);
            Assert.Single(ObjectiveTargets.Writable(live.Rows));
            Assert.Equal(TargetPass.SysAt, live.Rows[0].System);
            Assert.Equal(2, live.Rows[0].Index);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, live.Rows[0].ValueLow);

            // Over the WHOLE query space the producer emits 18 rows and 3 of them are writable
            // instances of the one distinct row. Both counts exact.
            Assert.Equal(18, EveryQuery().Sum(q => ObjectiveTargets.Produce(q).Count));
            Assert.Equal(3, EveryQuery()
                .Sum(q => ObjectiveTargets.Writable(ObjectiveTargets.Produce(q).Rows).Count));
        }
    }
}
