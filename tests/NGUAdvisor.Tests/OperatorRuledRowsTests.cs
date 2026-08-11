using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // TWO ROWS THE OPERATOR RULED ON, 2026-08-07 — and both rulings are about what a number MEANS,
    // which is not something either the guide or the decomp can settle.
    //
    //   Respawn 401  REMOVED. "the 401 was in the guide because it's the 'best' for where the user
    //                is at at the time... the advisor already calculates what is the best use of
    //                energy already. So it's a moot point."
    //   Block AT     TERMINAL at 100,000. "the Block AT is a hard cap at 100,000 and should never
    //                be capped lower."
    //
    // ⚠ THESE TESTS EXIST BECAUSE BOTH ROWS ARE RE-DERIVABLE FROM SOURCES THAT DISAGREE WITH THEM.
    // 23 §0.4 still transcribes Respawn 401 as the guide's sole standing terminal; amendment 35 §1
    // still proves the Block curve never saturates. A later pass reading either will conclude the
    // table is wrong. It is not — it is ruled.
    public class OperatorRuledRowsTests
    {
        // ---- W1: Respawn 401 is gone -------------------------------------------------------------

        [Fact]
        public void No_Respawn_level_row_survives_in_the_objective_table()
        {
            var respawnLevels = ObjectiveTable.LaneRows
                .Where(r => r.System == TargetPass.SysNguEnergy && r.Covers(2) &&
                            r.Kind == TargetPass.RowKind.Level)
                .ToList();

            Assert.True(respawnLevels.Count == 0,
                "a Respawn level row is back — [OPERATOR] removed it 2026-08-07 as situational " +
                "guide advice, not a property of the curve:\n  " +
                string.Join("\n  ", respawnLevels.Select(r => r.ValueLow + " " + r.Objective)));
        }

        // The value itself must not survive anywhere as a level, in any row, on any track — the
        // failure mode is someone re-adding it under a different id or chapter.
        [Fact]
        public void The_number_401_is_not_a_level_target_anywhere()
        {
            var fourOhOne = ObjectiveTable.LaneRows
                .Where(r => r.Kind == TargetPass.RowKind.Level &&
                            (r.ValueLow == 401 || r.ValueHigh == 401))
                .ToList();

            Assert.True(fourOhOne.Count == 0,
                "401 is a level target again:\n  " +
                string.Join("\n  ", fourOhOne.Select(r => r.System + " " + r.Objective)));
        }

        // ---- W2: Block AT is a hard cap at 100,000 -----------------------------------------------

        private static ObjectiveTable.LaneRow BlockCap() =>
            ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysAt && r.Covers(2) &&
                r.Kind == TargetPass.RowKind.Level && r.ValueLow == 100000);

        [Fact]
        public void Block_AT_is_terminal_at_one_hundred_thousand()
        {
            var row = BlockCap();

            Assert.Equal(TargetPass.Terminality.Terminal, row.Terminality);
            Assert.Equal(100000L, row.ValueLow);
            Assert.Equal(100000L, row.ValueHigh);   // scalar — a ranged terminal is refused upstream
            Assert.Null(row.LiftGate);              // ruled unconditional, not gated
            Assert.Null(row.CampaignScope);         // standing, not campaign-scoped
        }

        // THE TWO HALVES OF THE RULING CARRY THE SAME NUMBER, AND CANNOT DRIFT APART. The table's row
        // and the live writer both read ObjectiveTable.AtBlockHardCapLevel, so a future edit to one
        // number moves both or fails here. This is the assertion that would have caught the original
        // defect: the table said 100,000 while LevelPlanner independently computed ceil(49/f) ≈ 5,000
        // for the same field, and nothing compared them.
        [Fact]
        public void The_table_row_and_the_live_writer_share_one_hard_cap_constant()
        {
            Assert.Equal(100000L, ObjectiveTable.AtBlockHardCapLevel);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, BlockCap().ValueLow);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, BlockCap().ValueHigh);

            // The writer's half, asserted at the SOURCE because LevelPlanner is welded to Character
            // and cannot link headless — the same technique the bridge proof below uses, and stronger
            // than a runtime check, which would only prove the value at one moment.
            var src = CodeOnly(Source("LevelPlanner.cs"));
            Assert.Contains("ApplyPurposeFloor(targets, 2, ObjectiveTable.AtBlockHardCapLevel)", src);

            // ⚠ AND THE SUPERSEDED DERIVATION IS GONE, NOT MERELY BYPASSED. While ceil(49/f) still
            // exists as callable code it is one edit away from being the slot-2 stop again, which is
            // the exact defect this pair of commits closes. CodeOnly strips comments, so the
            // derivation preserved in the comment there does not satisfy this.
            Assert.DoesNotContain("BlockStopLevel", src);
        }

        // "NEVER CAPPED LOWER" ON THE WRITER'S HALF: the floor must RAISE a stale low target, not
        // merely decline to lower a high one. A slot left at ~5,000 by an older build is the case
        // that matters — it is what every existing save carries — and it must heal on the first tick.
        [Theory]
        [InlineData(0L, 100000L)]        // unset: the case that used to take ~5,000 and keep it
        [InlineData(5000L, 100000L)]     // the stale 99% stop an older build wrote — RAISED
        [InlineData(99999L, 100000L)]    // anything under the cap
        [InlineData(100000L, 100000L)]   // at the cap: unchanged
        [InlineData(250000L, 250000L)]   // hand-typed above the cap: the operator's, and kept
        [InlineData(-1L, 100000L)]       // negative reads as "met" to the game — never kept
        public void The_block_floor_raises_every_target_below_the_cap_and_keeps_those_above(
            long current, long expected)
        {
            Assert.Equal(expected,
                LaneTargets.AdvancedTrainingPurposeFloor(current, ObjectiveTable.AtBlockHardCapLevel));
        }

        // It must actually reach the write path — Terminal alone is not enough, since campaign
        // scope, a gate, a missing track or a ranged value would each refuse it earlier.
        [Fact]
        public void The_Block_AT_cap_actually_routes_to_a_write()
        {
            var route = TargetPass.Route(BlockCap().ToTargetRow(2));

            Assert.Equal(TargetPass.Disposition.WriteTarget, route.Disposition);
            Assert.Equal(100000L, route.TargetToWrite);
        }

        // ⚠ "NEVER CAPPED LOWER" AS AN INVARIANT ON THE TABLE'S HALF. No AT id-2 row may carry a
        // level BELOW the cap as a TERMINAL — a lower terminal would stop the lane early, which is
        // exactly what the ruling forbids. Preconditions below it are fine and expected: the ch.3
        // 5,000 rung is a reach-before rung, and RouteLevel never writes a precondition.
        [Fact]
        public void No_AT_block_row_terminates_below_the_hard_cap()
        {
            var lowTerminals = ObjectiveTable.LaneRows
                .Where(r => r.System == TargetPass.SysAt && r.Covers(2) &&
                            r.Kind == TargetPass.RowKind.Level &&
                            r.Terminality == TargetPass.Terminality.Terminal &&
                            r.ValueLow < 100000)
                .ToList();

            Assert.True(lowTerminals.Count == 0,
                "an AT block row terminates below the 100,000 hard cap:\n  " +
                string.Join("\n  ", lowTerminals.Select(r => r.ValueLow + " " + r.Objective)));
        }

        // The ch.3 5,000 rung is still there and still a PRECONDITION — so it cannot write, and the
        // two rows do not conflict. If someone promotes it to Terminal to "match" the cap, the test
        // above fires; this one guards the other direction, that it was not deleted instead.
        [Fact]
        public void The_ninety_nine_percent_rung_survives_as_a_precondition()
        {
            var rung = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysAt && r.Covers(2) &&
                r.Kind == TargetPass.RowKind.Level && r.ValueLow == 5000);

            Assert.Equal(TargetPass.Terminality.Precondition, rung.Terminality);
            Assert.Equal(TargetPass.Disposition.Precondition,
                TargetPass.Route(rung.ToTargetRow(2)).Disposition);
        }

        // ---- W1d: what still exercises the terminal write path ------------------------------------

        // ⚠ AFTER THIS COMMIT, BLOCK AT IS THE ONLY STANDING TERMINAL IN THE TABLE. The other two
        // are the 100LC TM rows, and RouteLevel refuses those on CampaignScope BEFORE terminality is
        // read — so without Block AT, nothing would reach WriteTarget at all and the terminal path
        // would be dead code exercised only by synthetic rows. Asserted so that stays visible.
        [Fact]
        public void Block_AT_is_now_the_only_standing_terminal_and_the_rest_are_campaign_scoped()
        {
            var terminals = ObjectiveTable.LaneRows
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal)
                .ToList();

            var standing = terminals.Where(r => r.CampaignScope == null).ToList();
            var scoped = terminals.Where(r => r.CampaignScope != null).ToList();

            Assert.Single(standing);
            Assert.Equal(TargetPass.SysAt, standing[0].System);
            Assert.Equal(100000L, standing[0].ValueLow);

            // Every campaign-scoped terminal refuses before terminality is even consulted.
            Assert.All(scoped, r =>
                Assert.Equal(TargetPass.Disposition.Refused,
                    TargetPass.Route(r.ToTargetRow(r.Ids != null ? r.Ids[0] : 0)).Disposition));
        }

        // ---- W3: the allocation proof, with its negative control ----------------------------------

        // ConstraintLayerBridge is welded to Character and cannot link headless, so this is a SOURCE
        // assertion — stronger than a runtime null check, which would only prove the field was null
        // at one moment.
        // ⚠ THIS REPLACES `The_bridge_still_has_no_producer_so_no_row_reaches_Pass_3`, WHICH ASSERTED
        // THE OPPOSITE AND WAS RIGHT TO, UNTIL THE WIRE. Its message said, in advance, exactly what
        // wiring would mean — "Block AT is now a STANDING terminal, so a producer would write 100,000
        // to the AT block slot and terminate that lane" — and demanded the allocation proof be
        // RE-DERIVED rather than re-asserted. That re-derivation is `ObjectiveTargetsTests`
        // (1,152-case sweep, three-way partitioned negative control). What belongs here now is the
        // other half: a producer exists, and THIS IS WHAT IT MAY AND MAY NOT DO.
        //
        // Still a SOURCE assertion for the same reason the old one was: ConstraintLayerBridge is
        // welded to Character and cannot link headless, so a runtime check could only prove the field
        // held some value at one moment. The claims below are structural and hold for every moment.
        [Fact]
        public void The_bridge_has_one_producer_and_it_may_only_be_the_objective_layer()
        {
            var src = CodeOnly(Source("ConstraintLayerBridge.cs"));

            // The field and its single read site are unchanged — the wire added a writer, not a path.
            Assert.Contains("public static IList<TargetPass.TargetRow> TargetTable;", src);
            Assert.Contains("TargetPass.RowsFor(TargetTable,", src);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                src, @"TargetPass\.RowsFor\(TargetTable,"));

            var assignments = src.Split('\n')
                .Select(l => l.Trim())
                .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"TargetTable\s*=(?!=)"))
                .ToList();

            // EXACTLY THREE, ALL INSIDE RefreshTargetTable, AND ALL BUT ONE ARE `null`. The two nulls
            // are the fail-open paths — the unconditional reset on entry and the catch — and the one
            // that is not null is the producer call's result. A fourth assignment, or one outside that
            // method, is a second source of truth for a field that moves a live allocation.
            Assert.Equal(3, assignments.Count);
            Assert.Equal(2, assignments.Count(l => l == "TargetTable = null;"));

            var fromProducer = assignments.Single(l => l != "TargetTable = null;");
            Assert.Equal("TargetTable = table.Held ? null : table.Rows;", fromProducer);

            // ...and `table` is an ObjectiveTargets.Produce result and nothing else. This is the
            // "may only be the objective layer" half: the rows must come THROUGH the producer, whose
            // rules (no NGU, no TM, no Wandoos, no campaign scope, no whole-system row, Level kind
            // only) are what bound what can reach Pass 3.
            Assert.Contains("var table = ObjectiveTargets.Produce(new ObjectiveTargets.Query", src);

            // ⚠ NOTHING MAY HAND-BUILD A ROW INTO THE FIELD. ObjectiveTable.LaneRow.ToTargetRow is the
            // only constructor of a TargetRow on the production path, and it copies terminality and
            // kind across unchanged — so there is no step in this file in which a precondition could
            // become a target (23 §0.4). A `new TargetPass.TargetRow` here would be that step.
            Assert.DoesNotContain("new TargetPass.TargetRow", src);

            // ⚠ A HELD TABLE MUST NOT LEAVE THE PREVIOUS TICK'S ROWS STANDING. Rows are keyed to a
            // chapter and a track; one held over from a chapter the run has left would stop a lane on
            // a stop the guide no longer makes. The refresh is called from PerformSwap BEFORE the
            // membership filter, so no early return can skip it.
            Assert.Contains("RefreshTargetTable(c);", src);
            Assert.True(src.IndexOf("RefreshTargetTable(c);", StringComparison.Ordinal) <
                        src.IndexOf("ChallengeOverlay.TransformPriorities", StringComparison.Ordinal),
                "the target table is refreshed after an early return can skip it");

            // The chapter is read FRESH and its Known flag is passed through — a bare 0 is ChapterAny
            // and would supply every chapter's rows at once (ObjectiveTable.ChapterMatches).
            Assert.Contains("ChapterKnown = stage.Known", src);
            Assert.Contains("StageDetector.Detect()", src);
        }

        // The live consequence of that wiring, on the shipped table rather than on the source text:
        // one row can stop one lane, and it is AT slot 2 at the ruled hard cap. If this ever names a
        // second lane, something reached Pass 3 that no ruling authorised.
        [Fact]
        public void The_only_row_the_wired_producer_can_stop_a_lane_with_is_Block_AT()
        {
            var writable = new List<TargetPass.TargetRow>();
            for (int ch = ObjectiveTargets.FirstChapter; ch <= ObjectiveTargets.LastChapter; ch++)
            foreach (var ngu in new[] { TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic })
            foreach (var run in new[] { TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic })
                writable.AddRange(ObjectiveTargets.Writable(ObjectiveTargets.Produce(
                    new ObjectiveTargets.Query
                    {
                        Chapter = ch, ChapterKnown = true, NguTrack = ngu, RunTrack = run,
                    }).Rows));

            Assert.NotEmpty(writable);
            Assert.All(writable, r =>
            {
                Assert.Equal(TargetPass.SysAt, r.System);
                Assert.Equal(2, r.Index);
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, r.ValueLow);
            });
        }

        private static readonly long[] Levels = { 0, 1, 5000, 99999, 100000, 100001, 250000, 1000000 };

        private static TargetPass.LaneState AtBlockLane(long level) =>
            AtBlockLane(level, 0L);

        private static TargetPass.LaneState AtBlockLane(long level, long operatorTarget) =>
            new TargetPass.LaneState
            {
                System = TargetPass.SysAt,
                Index = 2,
                ActiveTrack = TargetPass.Track.Evil,
                LevelOnTrack = level,
                OperatorTarget = operatorTarget,
            };

        // ⚠ NEGATIVE CONTROL. The proof above says "no row reaches Pass 3, so allocation cannot
        // move". That is only worth reading if the sweep could have detected a move. Feed the rows
        // directly — bypassing the absent producer — and confirm the table's BEFORE and AFTER
        // shapes genuinely diverge. A sweep that cannot fail proves nothing (48 §4).
        [Fact]
        public void Negative_control_the_sweep_detects_the_change_when_rows_are_actually_fed()
        {
            var seat = FeasibilityPass.Verdict.Seat();

            // BEFORE this commit the row was a Precondition; now it is Terminal. Same row, same
            // value, same track — only the classification moved.
            var asPrecondition = new List<TargetPass.TargetRow> { BlockCap().ToTargetRow(2) };
            var before = asPrecondition[0];
            before.Terminality = TargetPass.Terminality.Precondition;
            asPrecondition[0] = before;

            var asTerminal = new List<TargetPass.TargetRow> { BlockCap().ToTargetRow(2) };

            int diverged = 0, agreed = 0;
            foreach (var level in Levels)
            {
                var pre = TargetPass.Evaluate(AtBlockLane(level), asPrecondition, seat);
                var term = TargetPass.Evaluate(AtBlockLane(level), asTerminal, seat);

                if (pre.Disposition != term.Disposition ||
                    pre.TargetToWrite != term.TargetToWrite) diverged++;
                else agreed++;
            }

            Assert.True(diverged > 0,
                $"the control detected NOTHING across {Levels.Length} levels — the sweep is blind " +
                "and the no-change proof above is void");
            Assert.Equal(Levels.Length, diverged + agreed);
        }

        // ---- W4: the ruled cap is a FLOOR — the operator's higher target wins ----------------------

        // [OPERATOR] 2026-08-07, verbatim and in full:
        //
        //     "the operator's higher target should win over the ruled cap but it should never be
        //      capped below the 100,000 level."
        //
        // ⚠ THIS IS A BEHAVIOUR CHANGE AND IT WAS AN OPEN QUESTION, NOT AN OVERSIGHT. 36ea654 wired
        // the target table, MEASURED the divergence it created, and reported it undecided: the
        // membership filter (AdvancedTrainingBP.TargetMet, reading levelTarget[2]) runs FIRST, so the
        // LOWER of the two stops wins — and a hand-set 250,000 with the slot at 150,000 read UNMET to
        // the game while Pass 3 stopped the lane at 100,000 anyway. The ruling above resolves it in
        // the operator's favour, in the raising direction only.
        //
        // THE RULE, ONE LINE: effectiveStop = AdvancedTrainingPurposeFloor(live levelTarget[2],
        // AtBlockHardCapLevel) — the operator's own target when it is POSITIVE and at least the cap,
        // the cap otherwise, and never less than the cap.
        //
        // ⚠ 0 IS THE GAME'S UNSET SENTINEL, NOT A TARGET OF ZERO, and a NEGATIVE is the game's
        // never-fund marker (AdvancedTrainingTargetMet: `if (target < 0L) return true;`). Neither is
        // an operator asking for a number, so both take the cap. See the two tests that name them.

        // The value table the ruling is defined over: unset, negative, and the four numbers either
        // side of the cap plus the operator's own 250,000.
        private static readonly long[] LiveTargets = { 0L, -1L, 50_000L, 99_999L, 100_000L, 100_001L, 250_000L };

        // The rule, computed the WRITER'S way — LevelPlanner.ApplyPurposeFloor calls exactly this.
        // Every expectation below is derived from it rather than typed out, which is what makes the
        // sweep a comparison of two implementations instead of a restatement of one.
        private static long EffectiveStop(long liveTarget) =>
            LaneTargets.AdvancedTrainingPurposeFloor(liveTarget, ObjectiveTable.AtBlockHardCapLevel);

        private static IList<TargetPass.TargetRow> BlockCapTable() =>
            new List<TargetPass.TargetRow> { BlockCap().ToTargetRow(2) };

        // THE TABLE-DRIVEN PROOF: every live-target value against a level sweep that straddles both
        // the cap and the operator's number. For each cell: what Pass 3 would write, whether the lane
        // is stopped, and — the half a satisfaction check alone would miss — that the two sides
        // AGREE, since the writer's function is where the expectation comes from.
        [Fact]
        public void The_stop_is_the_higher_of_the_ruled_cap_and_the_operators_own_target()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            var table = BlockCapTable();
            var levels = new[] { 0L, 1L, 50_000L, 99_999L, 100_000L, 100_001L, 150_000L,
                                 249_999L, 250_000L, 250_001L, 1_000_000L };

            int stoppedCells = 0, fundedCells = 0;

            foreach (var live in LiveTargets)
            {
                long stop = EffectiveStop(live);

                // "NEVER CAPPED BELOW THE 100,000 LEVEL" — the half of the ruling that pulls the
                // other way, asserted first because it holds for EVERY value including the two
                // sentinels, and because getting it wrong is the failure that defunds a lane early.
                Assert.True(stop >= ObjectiveTable.AtBlockHardCapLevel,
                    "live target " + live + " produced a stop of " + stop + ", below the ruled cap");

                foreach (var level in levels)
                {
                    var answer = TargetPass.Evaluate(AtBlockLane(level, live), table, seat);
                    string why;
                    bool wants = ConstraintLayer.WantFromAnswer(answer, out why);

                    // The row still routes to a write — the floor changes the NUMBER, never the
                    // disposition, so nothing about the scope of what can stop a lane moved.
                    Assert.Equal(TargetPass.Disposition.WriteTarget, answer.Disposition);
                    Assert.Equal(stop, answer.TargetToWrite);

                    bool shouldStop = level >= stop;
                    Assert.Equal(shouldStop, !wants);
                    Assert.Equal(shouldStop
                            ? TargetPass.Satisfaction.Satisfied
                            : TargetPass.Satisfaction.Unsatisfied,
                        answer.Satisfaction);

                    if (shouldStop)
                    {
                        // The surfaced reason quotes the EFFECTIVE stop, not the table's value —
                        // otherwise an operator stopped at 250,000 would read "target met: level >=
                        // 100000" and the log would name a number nothing enforced.
                        Assert.Contains(stop.ToString(System.Globalization.CultureInfo.InvariantCulture), why);
                        stoppedCells++;
                    }
                    else
                    {
                        Assert.Null(why);
                        fundedCells++;
                    }
                }
            }

            // Non-vacuous in BOTH directions: the sweep must contain stops and non-stops, or one of
            // the two assertions above was never exercised.
            Assert.Equal(LiveTargets.Length * levels.Length, stoppedCells + fundedCells);
            Assert.True(stoppedCells > 0 && fundedCells > 0,
                "the sweep only measured one outcome: " + stoppedCells + " stopped, " + fundedCells + " funded");
        }

        // ⚠ THE OPERATOR'S OWN SCENARIO, ON ITS OWN, BECAUSE IT IS THE ONE THAT CHANGED. Hand-set
        // 250,000, slot at 150,000: before this commit Pass 3 answered Satisfied and the lane was
        // eliminated at Pass 3 with "target met: level >= 100000" while the operator's own field read
        // UNMET. It is now funded, and it stops at 250,000 — theirs, not the table's.
        [Fact]
        public void A_hand_set_250k_keeps_the_slot_funded_at_150k_and_stops_at_250k()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            var table = BlockCapTable();

            var funded = TargetPass.Evaluate(AtBlockLane(150_000L, 250_000L), table, seat);
            string why;
            Assert.True(ConstraintLayer.WantFromAnswer(funded, out why),
                "the operator's 250,000 was overridden and Pass 3 stopped the slot at the ruled cap");
            Assert.Null(why);
            Assert.Equal(TargetPass.Satisfaction.Unsatisfied, funded.Satisfaction);
            Assert.Equal(250_000L, funded.TargetToWrite);

            // ...and the GAME agrees at the same level, which is the whole point: the field and the
            // pass now answer the same question the same way instead of one overruling the other.
            Assert.False(LaneTargets.AdvancedTrainingTargetMet(250_000L, 150_000L));

            // The stop is real, not merely deferred — one level short of 250,000 it still funds, and
            // at 250,000 it stops, with the operator's number in the surfaced reason.
            Assert.True(ConstraintLayer.WantFromAnswer(
                TargetPass.Evaluate(AtBlockLane(249_999L, 250_000L), table, seat), out why));

            var stopped = TargetPass.Evaluate(AtBlockLane(250_000L, 250_000L), table, seat);
            Assert.False(ConstraintLayer.WantFromAnswer(stopped, out why));
            Assert.Contains("250000", why);
            Assert.True(LaneTargets.AdvancedTrainingTargetMet(250_000L, 250_000L));
        }

        // ⚠ THE FLOOR ONLY EVER RAISES. A target BELOW the cap is not the operator asking for less —
        // "never capped below the 100,000 level" is explicit — so 50,000 and 99,999 take the cap and
        // the lane keeps being funded through them.
        [Fact]
        public void A_target_below_the_cap_never_lowers_the_stop()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            var table = BlockCapTable();

            foreach (var low in new[] { 1L, 50_000L, 99_999L })
            {
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, EffectiveStop(low));

                // The level is chosen to separate the two stops: the LOW target reads met here (so
                // it would have stopped the lane) and the ruled cap does not. `low` itself is the
                // only such level for 99,999, which is why this is a level and not `low + 1`.
                Assert.True(low < ObjectiveTable.AtBlockHardCapLevel);
                Assert.True(LaneTargets.AdvancedTrainingTargetMet(low, low));

                var answer = TargetPass.Evaluate(AtBlockLane(low, low), table, seat);
                string why;
                Assert.True(ConstraintLayer.WantFromAnswer(answer, out why),
                    "a target of " + low + " capped the slot below the ruled 100,000");
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, answer.TargetToWrite);
            }
        }

        // ⚠ THE TWO GAME SENTINELS, DECIDED AND JUSTIFIED RATHER THAN INHERITED.
        //
        //   0  is the UNSET sentinel, not a target of zero — no operator preference exists, so the
        //      ruled cap stands. Reading it as a number would stop the lane at 0 (or, floored the
        //      naive way, write 0 and erase the target — 23 §7's forbidden default).
        //
        //  <0  is the game's NEVER-FUND marker: AdvancedTrainingTargetMet returns true for any
        //      negative, so `IsValid()` drops the lane BEFORE Pass 3 ever runs and the pass's answer
        //      is moot LIVE. It still must not be allowed to defeat the cap, and there are two ways
        //      it could: passing -1 through as the stop makes WriteTargetGuard refuse, which reads as
        //      "keep funding" and removes the cap entirely; treating it as a number below the cap and
        //      then honouring it would stop the lane at a negative level. Taking the CAP is the only
        //      answer that satisfies "never capped below 100,000", and it is what the writer's floor
        //      already does — so this is agreement, not a second decision.
        [Fact]
        public void The_unset_sentinel_and_a_negative_target_both_take_the_ruled_cap()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            var table = BlockCapTable();

            foreach (var sentinel in new[] { 0L, -1L, -100_000L, long.MinValue })
            {
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, EffectiveStop(sentinel));

                var answer = TargetPass.Evaluate(AtBlockLane(150_000L, sentinel), table, seat);
                Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, answer.TargetToWrite);
                Assert.Equal(TargetPass.Satisfaction.Satisfied, answer.Satisfaction);

                // never 0, never negative — the two values the game reads specially
                Assert.True(answer.TargetToWrite > 0);
                Assert.NotEqual(TargetPass.GameUnsetSentinel, answer.TargetToWrite);
                Assert.NotEqual(TargetPass.GameNeverFundMarker, answer.TargetToWrite);
            }

            // The live consequence of a negative, named: the game reads it as MET, so IsValid() drops
            // the lane and Pass 3 is never asked. Its answer above is the headless one.
            Assert.True(LaneTargets.AdvancedTrainingTargetMet(-1L, 0L));
            Assert.False(LaneTargets.IsValid(true, true, LaneTargets.AdvancedTrainingTargetMet(-1L, 0L)));
        }

        // ⚠ THE AUTO-PROFILE-OFF STATE, WHICH IS NOT HYPOTHETICAL. LevelPlanner.Tick short-circuits on
        // `!s.AutoProfile` and ThawAll restores the pre-advisor snapshot, so levelTarget[2] can hold
        // anything — 0, or a number below the cap — while the constraint layer (gated on the SEPARATE
        // ConstraintAllocator switch) keeps running Pass 3. The rule is correct there because it is
        // defined purely on the live field and needs to know nothing about which switch is on:
        //
        //   · restored 0        -> the ruled cap, exactly as 36ea654 shipped;
        //   · restored 250,000  -> the operator's, funded past the cap;
        //   · restored 50,000   -> Pass 3's stop is STILL the cap. It never lowers one. What stops
        //     the lane at 50,000 is the GAME'S OWN membership filter reading the operator's own
        //     field — IsValid() runs before a spec is built, so that lane never reaches Pass 3 at
        //     all. Asserted below, because "the advisor capped it low" and "the operator's own
        //     restored number capped it low" are different findings and only the second is true.
        [Fact]
        public void With_auto_profile_off_a_restored_field_still_cannot_cap_below_the_ruled_level()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            var table = BlockCapTable();

            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, EffectiveStop(0L));
            Assert.Equal(250_000L, EffectiveStop(250_000L));
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, EffectiveStop(50_000L));

            // The 50,000 case in full. At level 60,000 the game's filter has already dropped the
            // lane...
            Assert.True(LaneTargets.AdvancedTrainingTargetMet(50_000L, 60_000L));
            Assert.False(LaneTargets.IsValid(true, true,
                LaneTargets.AdvancedTrainingTargetMet(50_000L, 60_000L)));

            // ...and had it arrived anyway, Pass 3 would have kept funding it, because Pass 3's stop
            // is the cap and 60,000 is short of it.
            var answer = TargetPass.Evaluate(AtBlockLane(60_000L, 50_000L), table, seat);
            string why;
            Assert.True(ConstraintLayer.WantFromAnswer(answer, out why));
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, answer.TargetToWrite);
        }

        // ⚠ THE FLOOR CANNOT CREATE A WRITE WHERE THERE WAS NONE. This is the constraint that would be
        // violated invisibly: an operator target raising a PRECONDITION into a target makes the
        // cascade abandon the lane permanently (23 §0.4), and raising a SILENCE into one writes a
        // number no source gave. Both are checked with an operator target far above every value in
        // the table, which is the state that would trigger them if the floor were applied before
        // routing rather than after.
        [Fact]
        public void An_operator_target_cannot_turn_a_silence_or_a_precondition_into_a_write()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            const long huge = 9_000_000L;

            // The ch.3 99% rung — a PRECONDITION on the Normal track, at the same slot.
            var rung = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysAt && r.Covers(2) &&
                r.Kind == TargetPass.RowKind.Level && r.ValueLow == 5000);
            var preconditionOnly = new List<TargetPass.TargetRow> { rung.ToTargetRow(2) };

            var pre = TargetPass.Evaluate(new TargetPass.LaneState
            {
                System = TargetPass.SysAt, Index = 2,
                ActiveTrack = rung.Track, LevelOnTrack = 10_000L, OperatorTarget = huge,
            }, preconditionOnly, seat);
            Assert.Equal(TargetPass.Disposition.Precondition, pre.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, pre.Satisfaction);
            Assert.Equal(0L, pre.TargetToWrite);

            // A SILENCE — the other four AT slots against the block table.
            foreach (var slot in new[] { 0, 1, 3, 4 })
            {
                var silent = TargetPass.Evaluate(new TargetPass.LaneState
                {
                    System = TargetPass.SysAt, Index = slot,
                    ActiveTrack = TargetPass.Track.Evil, LevelOnTrack = 150_000L,
                    OperatorTarget = huge,
                }, TargetPass.RowsFor(BlockCapTable(), TargetPass.SysAt, slot), seat);

                Assert.Equal(TargetPass.Disposition.Silent, silent.Disposition);
                Assert.Equal(0L, silent.TargetToWrite);
                Assert.False(string.IsNullOrEmpty(silent.Reason));
                string why;
                Assert.True(ConstraintLayer.WantFromAnswer(silent, out why));
            }
        }

        // ⚠ ONE RULE, TWO CONSUMERS, AND NO THIRD — the structural half of "they cannot disagree".
        // The last three defects on this branch's subject were two sources of truth drifting, so the
        // call sites are CENSUSED rather than trusted: LaneTargets defines the function once,
        // LevelPlanner calls it once, TargetPass calls it once, and nobody restates the arithmetic.
        [Fact]
        public void One_floor_function_two_call_sites_and_no_restatement_of_the_rule()
        {
            var laneTargets = CodeOnly(Source("LaneTargets.cs"));
            var levelPlanner = CodeOnly(Source("LevelPlanner.cs"));
            var targetPass = CodeOnly(Source("TargetPass.cs"));

            // Defined exactly once, and the definition is the ruling.
            Assert.Contains("public static long AdvancedTrainingPurposeFloor(long current, long stop)",
                laneTargets);
            Assert.Contains("current > 0L && current >= stop ? current : stop", laneTargets);

            // One call site each, both naming the function rather than re-deriving it.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                levelPlanner, @"LaneTargets\.AdvancedTrainingPurposeFloor\("));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                targetPass, @"LaneTargets\.AdvancedTrainingPurposeFloor\("));
            Assert.Contains(
                "LaneTargets.AdvancedTrainingPurposeFloor(lane.OperatorTarget, ruled)", targetPass);

            // ⚠ AND NO SECOND RULE ANYWHERE ON THE PASS-3 PATH. `Math.Max` is the shape a
            // "simplification" would take, and it is precisely the one that drops the `current > 0`
            // guard — the difference between a negative target being ignored and a negative target
            // winning. TargetPass does not use it, for anything.
            Assert.DoesNotContain("Math.Max", targetPass);

            // The live read is supplied as DATA by the bridge, not read from Character in the pass —
            // the design constraint LiftedGates already established.
            Assert.DoesNotContain("Character", targetPass);
            Assert.Contains("lane.OperatorTarget = c.advancedTraining.levelTarget[bp.LaneIndex];",
                CodeOnly(Source("ConstraintLayerBridge.cs")));
        }

        // The two implementations, driven through one value table and compared — the executable half
        // of "they cannot disagree". The writer's number goes into levelTarget[2]; Pass 3's is the
        // stop it enforces; for every live target they must be the SAME number, and the game's
        // comparator on the writer's number must give the same verdict as Pass 3's satisfaction at
        // every level. This is the test that fires if either side is ever changed alone.
        [Fact]
        public void Pass_3_and_the_live_writer_agree_at_every_target_and_every_level()
        {
            var seat = FeasibilityPass.Verdict.Seat();
            var table = BlockCapTable();

            foreach (var live in LiveTargets)
            {
                // what LevelPlanner.ApplyPurposeFloor would leave in the field
                long written = LaneTargets.AdvancedTrainingPurposeFloor(
                    live, ObjectiveTable.AtBlockHardCapLevel);

                foreach (var level in Levels.Concat(new[] { 150_000L, 249_999L }))
                {
                    var answer = TargetPass.Evaluate(AtBlockLane(level, live), table, seat);

                    // same number...
                    Assert.Equal(written, answer.TargetToWrite);

                    // ...and same verdict, the game's comparator against Pass 3's satisfaction
                    bool gameSaysMet = LaneTargets.AdvancedTrainingTargetMet(written, level);
                    bool passThreeSaysMet = answer.Satisfaction == TargetPass.Satisfaction.Satisfied;

                    Assert.True(gameSaysMet == passThreeSaysMet,
                        "live target " + live + " at level " + level + ": the game's comparator says " +
                        gameSaysMet + " and Pass 3 says " + passThreeSaysMet + " — the field write " +
                        "and the enforced stop have drifted apart");
                }
            }
        }

        // ---- helpers -------------------------------------------------------------------------------

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = null)
        {
            var dir = System.IO.Path.GetDirectoryName(here);
            while (dir != null &&
                   !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "NGUAdvisor", "Managers")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir;
        }

        private static string Source(string name)
        {
            var path = System.IO.Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", name);
            Assert.True(System.IO.File.Exists(path),
                $"source not found, so nothing was measured: {path}");
            return System.IO.File.ReadAllText(path);
        }

        private static string CodeOnly(string src)
            => string.Join("\n", src.Split('\n')
                .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i < 0 ? l : l.Substring(0, i); }));
    }
}
