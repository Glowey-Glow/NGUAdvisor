using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE OBJECTIVE LAYER — audit/23's §2 lane table and §3 zone table, transcribed into typed data,
    // plus the reader over them (37 §S5 B6).
    //
    // WHAT THESE TESTS ARE FOR. The table is a TRANSCRIPTION, so the failure mode is not "the code
    // computes the wrong thing" — it is "the row says something the guide does not." The load-bearing
    // invariants, in the order they will bite:
    //
    //   1. TERMINALITY. A `precondition` fed to the game's target field makes the CASCADE ABANDON THE
    //      LANE FOREVER ([DECOMP] AllNGUController.cs:1245-1300 moves a satisfied sub-lane's ENTIRE
    //      allocation to the next unmet id). "2-3k Adventure a before Beardverse" does NOT mean stop
    //      at 3k. Every level row is routed here and its disposition asserted.
    //   2. SOFTCAP IS NOT ONE CONCEPT. The same word carries opposite terminality on two rows, and
    //      both are correct on the mechanics. Asserted from the table, both directions.
    //   3. SILENCE IS NOT A ZERO. Target 0 is the game's UNSET sentinel and funds FOREVER
    //      (AllNGUController.cs:1311-1314). A slot the guide does not fill must surface as a silence
    //      with its ledger reason, never as 0, -1 or long.MaxValue.
    //   4. TRACK. The game stores three target fields per NGU (NGU.cs:22-26). A Normal-track row must
    //      not satisfy an Evil-track query.
    //
    // ⚠ NOTHING CONSUMES THIS TABLE. These tests are its only caller, by design — it is an ADDITIVE
    // second membership source that will be compared against the profile JSON before either is
    // trusted. No assertion here touches allocation.
    public class ObjectiveLayerTests
    {
        // =====================================================================================
        // 1. TERMINALITY ROUND-TRIPS, AND A PRECONDITION CAN NEVER BE READ AS A TARGET
        // =====================================================================================

        // ToTargetRow is the only bridge from this table into Pass 3, so it is the only place a
        // terminality could be lost or rewritten. Every field that decides a disposition must cross
        // unchanged, for every row, at every id the row covers.
        [Fact]
        public void Every_row_round_trips_its_terminality_kind_track_and_value()
        {
            foreach (var row in ObjectiveTable.LaneRows)
            {
                var ids = row.Ids ?? new[] { ObjectiveTable.NoIndex };
                foreach (var id in ids)
                {
                    var t = row.ToTargetRow(id);
                    Assert.Equal(row.Terminality, t.Terminality);
                    Assert.Equal(row.Kind, t.Kind);
                    Assert.Equal(row.ValueLow, t.ValueLow);
                    Assert.Equal(row.ValueHigh, t.ValueHigh);
                    Assert.Equal(row.System, t.System);
                    Assert.Equal(row.CampaignScope, t.CampaignScope);
                    Assert.Equal(row.TrackNeutral, t.TrackNeutral);
                    Assert.Equal(id, t.Index);
                    // An AllTracks row must NOT acquire a track on the way through — it fails closed
                    // at RouteLevel ("row without a track is unusable") instead.
                    Assert.Equal(row.AllTracks ? TargetPass.Track.Unspecified : row.Track, t.Track);
                }
            }
        }

        // THE CENTRAL SAFETY PROPERTY. Route every level row in the table and assert that no
        // precondition anywhere produces a writable number. There is no chapter, no track, no system
        // and no id on which a `precondition` becomes a `target`.
        [Fact]
        public void No_precondition_row_anywhere_can_be_read_as_a_target()
        {
            var preconditions = 0;
            foreach (var row in ObjectiveTable.LaneRows)
            {
                if (row.Terminality != TargetPass.Terminality.Precondition)
                    continue;
                if (row.Kind != TargetPass.RowKind.Level)
                    continue;

                foreach (var id in row.Ids ?? new[] { ObjectiveTable.NoIndex })
                {
                    var route = TargetPass.Route(row.ToTargetRow(id));
                    Assert.Equal(TargetPass.Disposition.Precondition, route.Disposition);
                    Assert.Equal(0L, route.TargetToWrite);
                    Assert.NotNull(route.Reason);
                    preconditions++;
                }
            }
            // Guard the guard: if a refactor silently emptied the table this test would pass vacuously.
            Assert.True(preconditions >= 20,
                "expected the PAWG ladder plus the AT/Adventure-a rungs; found " + preconditions);
        }

        // The complement: the ONLY rows that may produce a writable number are terminal, non-ranged,
        // non-campaign-scoped, and not an Evil NGU (amendment 18 §1 refuses those at the router).
        [Fact]
        public void Only_a_scalar_standing_terminal_ever_produces_a_writable_target()
        {
            foreach (var row in ObjectiveTable.LaneRows)
            {
                foreach (var id in row.Ids ?? new[] { ObjectiveTable.NoIndex })
                {
                    var route = TargetPass.Route(row.ToTargetRow(id));
                    if (route.Disposition != TargetPass.Disposition.WriteTarget)
                        continue;

                    Assert.Equal(TargetPass.RowKind.Level, row.Kind);
                    Assert.Equal(TargetPass.Terminality.Terminal, row.Terminality);
                    Assert.False(row.IsRange, "a ranged terminal is not a writable stopping level");
                    Assert.Null(row.CampaignScope);
                    Assert.True(route.TargetToWrite > 0,
                        "0 is the game's UNSET sentinel and funds forever; -1 is never-fund");
                    Assert.False(TargetPass.IsNguSystem(row.System) &&
                                 row.Track == TargetPass.Track.Evil,
                        "amendment 18 §1: there are no Evil NGU levels");
                }
            }
        }

        // =====================================================================================
        // 2. SOFTCAP IS NOT ONE CONCEPT — the same word, the opposite answer, both from the table
        // =====================================================================================
        // Ch.3 says "don't invest further yet" at Respawn 401 and "When you hit softcaps, KEEP GOING"
        // for Adventure a, AND BOTH ARE CORRECT ON THE MECHANICS: Respawn's post-400 branch saturates
        // (level/(level*5 + 200000) + 0.2, bounded — AllNGUController.cs:449-458) while Adventure a's
        // post-1000 branch is unbounded sqrt (:568-572). An implementation that derives terminality
        // from the word "softcap" gets exactly one of these two rows wrong.
        //
        // ⚠ ONLY ONE HALF IS STILL A ROW. [OPERATOR] removed the Respawn level row 2026-08-07 — 401
        // was "the 'best' for where the user is at at the time", not a property of the curve. The
        // SATURATION IS STILL REAL and the softcap constant 400 is still transcribed (see
        // Softcap_rows_resolve_against_the_published_curve_constant); what went is the instruction
        // to stop there. The surviving half is the one that was always the harder call: a softcap
        // that means KEEP GOING. Removal is pinned in OperatorRuledRowsTests.
        [Fact]
        public void The_adventure_a_softcap_is_a_precondition_and_respawn_has_no_row_at_all()
        {
            var advA = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysNguEnergy && r.Covers(4) &&
                r.Track == TargetPass.Track.Normal && r.Kind == TargetPass.RowKind.Level &&
                r.ValueLow == 1000);

            Assert.Equal(TargetPass.Terminality.Precondition, advA.Terminality);

            // Same softcap word in both source sentences — assert it, so the test cannot silently
            // stop being about the ambiguity it exists for.
            Assert.Contains("softcap", advA.ValueText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("KEEP GOING", advA.ValueText, StringComparison.OrdinalIgnoreCase);

            var advRoute = TargetPass.Route(advA.ToTargetRow(4));
            Assert.Equal(TargetPass.Disposition.Precondition, advRoute.Disposition);
            Assert.Equal(0L, advRoute.TargetToWrite);

            // The counterpart is gone entirely — not demoted to precondition, not gated. Absence.
            Assert.Empty(ObjectiveTable.LaneRows.Where(r =>
                r.System == TargetPass.SysNguEnergy && r.Covers(2) &&
                r.Kind == TargetPass.RowKind.Level));
        }

        // The Drop Chance softcap (id 6) carries the SAME "keep going" clause as id 4 and must reach
        // the same answer — a second instance, so the distinction is not pinned by one row alone.
        [Fact]
        public void The_drop_chance_softcap_is_also_a_precondition_not_a_stop()
        {
            var dc = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysNguEnergy && r.Covers(6) &&
                r.Kind == TargetPass.RowKind.Level);

            Assert.Equal(TargetPass.Terminality.Precondition, dc.Terminality);
            Assert.Equal(1000L, dc.ValueLow);
            Assert.Equal(TargetPass.Disposition.Precondition,
                TargetPass.Route(dc.ToTargetRow(6)).Disposition);
        }

        // The softcap VALUES are curve constants, not targets — and the two softcap-shaped
        // preconditions must resolve against the published constant for their own id.
        [Fact]
        public void Softcap_rows_resolve_against_the_published_curve_constant()
        {
            ObjectiveTable.Softcap advA, dc, respawn;
            Assert.True(ObjectiveTable.TryGetSoftcap(TargetPass.SysNguEnergy, 4, out advA));
            Assert.True(ObjectiveTable.TryGetSoftcap(TargetPass.SysNguEnergy, 6, out dc));
            Assert.True(ObjectiveTable.TryGetSoftcap(TargetPass.SysNguEnergy, 2, out respawn));

            Assert.Equal(1000L, advA.Value);
            Assert.Equal(1000L, dc.Value);
            // ⚠ THE SOFTCAP CONSTANT SURVIVES THE ROW'S REMOVAL, and that is the point of asserting
            // it here. Respawn's softcap is 400 — a CURVE CONSTANT, decomp-derived, unaffected by
            // any ruling. The guide's stopping instruction was 401, a different kind of number, and
            // [OPERATOR] removed it 2026-08-07 as situational advice. Constants describe the game;
            // rows describe what to do about it. Only the second kind was ruled on.
            Assert.Equal(400L, respawn.Value);
            Assert.Empty(ObjectiveTable.LaneRows.Where(r =>
                r.System == TargetPass.SysNguEnergy && r.Covers(2) &&
                r.Kind == TargetPass.RowKind.Level));
        }

        // All sixteen NGU curves are transcribed; ELEVEN of them have a softcap at all, and TEN of
        // those eleven carry 23 §2.7's decomp-verified tick.
        //
        // ⚠ 23 §2.7's PROSE AND ITS TABLE DISAGREE, and the disagreement is recorded rather than
        // resolved. The prose says "22 §0.4 verified all sixteen against the decomp branch constants;
        // ALL SIXTEEN MATCH EXACTLY"; the table ticks TEN. Five of the six unticked rows have no
        // softcap to verify ("(none)"), which accounts for the gap benignly — but magic id 4 (Time
        // Machine) HAS a softcap of 1000 and is the one row with a value and no tick. Transcribed as
        // the table has it, because the table is the per-row record.
        [Fact]
        public void All_sixteen_softcaps_are_transcribed_and_ten_carry_the_decomp_tick()
        {
            Assert.Equal(16, ObjectiveTable.Softcaps.Length);
            Assert.Equal(9, ObjectiveTable.Softcaps.Count(s => s.System == TargetPass.SysNguEnergy));
            Assert.Equal(7, ObjectiveTable.Softcaps.Count(s => s.System == TargetPass.SysNguMagic));
            Assert.Equal(11, ObjectiveTable.Softcaps.Count(s => s.HasSoftcap));
            Assert.Equal(10, ObjectiveTable.Softcaps.Count(s => s.DecompVerified));

            // A tick without a softcap would be meaningless; assert the ticks sit only on values.
            foreach (var s in ObjectiveTable.Softcaps.Where(x => x.DecompVerified))
                Assert.True(s.HasSoftcap);

            var unverified = ObjectiveTable.Softcaps.Single(s => !s.DecompVerified && s.HasSoftcap);
            Assert.Equal(TargetPass.SysNguMagic, unverified.System);
            Assert.Equal(4, unverified.Index);

            // The M0/M1 constants the conflict says are NOT in dispute.
            ObjectiveTable.Softcap ygg, exp;
            Assert.True(ObjectiveTable.TryGetSoftcap(TargetPass.SysNguMagic, 0, out ygg));
            Assert.True(ObjectiveTable.TryGetSoftcap(TargetPass.SysNguMagic, 1, out exp));
            Assert.Equal(400L, ygg.Value);
            Assert.Equal(2000L, exp.Value);

            // Four curves have NO softcap and must not be readable as one at level 0.
            foreach (var s in ObjectiveTable.Softcaps.Where(x => !x.HasSoftcap))
                Assert.Equal(0L, s.Value);
        }

        // =====================================================================================
        // 3. EVERY SILENCE IN 23 §7 IS A SILENCE, WITH ITS REASON, AND IS NOT A ZERO
        // =====================================================================================

        // 23 §7.2's ledger, slot by slot. Each entry asserts THREE things: the reader reports a
        // silence (not an absence and not a value), the ledger supplies a recorded reason, and no
        // level row exists anywhere in the table for that (system, id, track) on any chapter.
        [Theory]
        // §7.1 S1 — augments: 14 slots x 3 tracks = 42 slots, ZERO values.
        [InlineData(TargetPass.SysAugments, 0, TargetPass.Track.Normal)]
        [InlineData(TargetPass.SysAugments, 6, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysAugments, 13, TargetPass.Track.Sadistic)]
        // §7.2 — ngu-energy
        [InlineData(TargetPass.SysNguEnergy, 0, TargetPass.Track.Evil)]   // PAWGs: only "BB the first 5"
        [InlineData(TargetPass.SysNguEnergy, 1, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysNguEnergy, 2, TargetPass.Track.Evil)]   // Respawn: only the GO <0.95x
        [InlineData(TargetPass.SysNguEnergy, 3, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysNguEnergy, 5, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysNguEnergy, 4, TargetPass.Track.Evil)]   // Adv a: a time and a rate
        [InlineData(TargetPass.SysNguEnergy, 6, TargetPass.Track.Evil)]   // DC: "Run NGU DC if needed" is Normal-scoped
        [InlineData(TargetPass.SysNguEnergy, 7, TargetPass.Track.Normal)] // GO priority-1 predicate only
        [InlineData(TargetPass.SysNguEnergy, 8, TargetPass.Track.Normal)] // GO predicate only
        // §7.2 — ngu-magic
        [InlineData(TargetPass.SysNguMagic, 0, TargetPass.Track.Normal)]
        [InlineData(TargetPass.SysNguMagic, 1, TargetPass.Track.Normal)]
        [InlineData(TargetPass.SysNguMagic, 2, TargetPass.Track.Normal)]
        [InlineData(TargetPass.SysNguMagic, 2, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysNguMagic, 3, TargetPass.Track.Normal)]  // Number: never named, any track
        [InlineData(TargetPass.SysNguMagic, 3, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysNguMagic, 3, TargetPass.Track.Sadistic)]
        [InlineData(TargetPass.SysNguMagic, 4, TargetPass.Track.Normal)]
        [InlineData(TargetPass.SysNguMagic, 4, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysNguMagic, 5, TargetPass.Track.Normal)]
        [InlineData(TargetPass.SysNguMagic, 6, TargetPass.Track.Normal)]
        // §7.2 — at
        [InlineData(TargetPass.SysAt, 0, TargetPass.Track.Evil)]          // terminates on an ADVENTURE STAT
        [InlineData(TargetPass.SysAt, 1, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysAt, 3, TargetPass.Track.Normal)]        // Wandoos dumps: cost predicate only
        [InlineData(TargetPass.SysAt, 3, TargetPass.Track.Evil)]
        [InlineData(TargetPass.SysAt, 4, TargetPass.Track.Sadistic)]
        // §7.1 S2 — SADISTIC: every slot, every system
        [InlineData(TargetPass.SysNguEnergy, 4, TargetPass.Track.Sadistic)]
        [InlineData(TargetPass.SysNguMagic, 6, TargetPass.Track.Sadistic)]
        [InlineData(TargetPass.SysAt, 2, TargetPass.Track.Sadistic)]
        [InlineData(TargetPass.SysWandoos, 0, TargetPass.Track.Sadistic)]
        public void Every_ledger_silence_surfaces_with_a_reason_and_never_as_a_number(
            string system, int id, TargetPass.Track track)
        {
            // A silence is about a LEVEL. Rate/time/predicate guidance may exist and does not fill
            // the slot — 23 §7.2's wording is "no level", not "no mention".
            var levelRows = ObjectiveTable.LaneRows.Where(r =>
                r.System == system && r.Covers(id) &&
                r.Kind == TargetPass.RowKind.Level &&
                ObjectiveTable.TrackMatches(r, track)).ToArray();
            Assert.True(levelRows.Length == 0,
                string.Format(CultureInfo.InvariantCulture,
                    "({0}, {1}, {2}) is a 23 §7 silence but the table carries {3} level row(s)",
                    system, id, track, levelRows.Length));

            // The ledger must NAME it — a silence without a recorded reason is a discovery, and
            // 23 §7 exists so the operator input is a LIST.
            TargetPass.SilenceSpec spec;
            Assert.True(TargetPass.FindSilence(system, id, track, out spec),
                string.Format(CultureInfo.InvariantCulture,
                    "({0}, {1}, {2}) has no silence-ledger entry", system, id, track));
            Assert.False(string.IsNullOrEmpty(spec.Reason));
            Assert.False(string.IsNullOrEmpty(spec.Cite));
            Assert.NotEqual(TargetPass.SilenceClass.Unspecified, spec.Class);
        }

        // The reader's own contract on a silent slot: SURFACED, with the reason, and holding NO
        // number. Asserted on a chapter where the guide is otherwise loud, so the answer is about the
        // slot and not about an empty query.
        [Fact]
        public void A_silent_slot_reads_as_a_surfaced_silence_and_not_as_zero()
        {
            // ngu-magic id 3 (Number) — the only NGU id the guide never mentions once, on any track.
            var answer = ObjectiveReader.Slot(3, TargetPass.Track.Normal,
                TargetPass.SysNguMagic, 3);

            Assert.Equal(ObjectiveReader.Availability.Silent, answer.Availability);
            Assert.True(answer.SilenceKnown);
            Assert.False(answer.HasRows);
            Assert.Empty(answer.Rows);                     // never null — a caller cannot NRE into a default
            Assert.NotNull(answer.Reason);
            Assert.Contains("never named", answer.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TargetPass.SilenceClass.Silent, answer.SilenceClass);

            // Routing a silence yields NOTHING — not a zero row, not a refusal carrying a value.
            var routed = ObjectiveReader.Route(3, TargetPass.Track.Normal,
                TargetPass.SysNguMagic, 3);
            Assert.Empty(routed);
        }

        // A slot with no ledger entry is STILL a silence — fail closed, with the fallback reason.
        // (The Sadistic catch-all makes an unnamed slot hard to construct, so this uses a system the
        // schema does not name at all.)
        [Fact]
        public void An_unledgered_slot_still_answers_silence_rather_than_defaulting()
        {
            var answer = ObjectiveReader.Slot(3, TargetPass.Track.Normal, "not-a-system", 0);

            Assert.Equal(ObjectiveReader.Availability.Silent, answer.Availability);
            Assert.False(answer.SilenceKnown);
            Assert.Contains("surfaced, never defaulted", answer.Reason);
            Assert.Empty(answer.Rows);
        }

        // An empty system slug is a caller error, not an answer.
        [Fact]
        public void A_slot_query_without_a_system_is_unevaluated_not_silent()
        {
            var answer = ObjectiveReader.Slot(3, TargetPass.Track.Normal, null, 0);
            Assert.Equal(ObjectiveReader.Availability.Unevaluated, answer.Availability);
            Assert.NotNull(answer.Reason);
            Assert.Empty(answer.Rows);
        }

        // The two big silences, as counts rather than samples: augments hold 42 slots with ZERO
        // VALUES, and Sadistic holds every slot of every system.
        //
        // ⚠ THIS COUNTS LEVEL-SILENCES, WHICH IS WHAT 23 §7 LEDGERS. The guide is not MUTE about
        // augments — it restates a selection rule four times — it just never states a level, and
        // §7.1 S1's claim is "42 slots, ZERO VALUES". Counting Availability.Silent instead would
        // report 0 augment silences and quietly contradict the ledger.
        [Fact]
        public void Augments_have_no_level_in_all_42_slots_and_sadistic_in_every_slot()
        {
            var tracks = new[] { TargetPass.Track.Normal, TargetPass.Track.Evil,
                                 TargetPass.Track.Sadistic };

            var augmentSilences = 0;
            foreach (var track in tracks)
            {
                var silent = ObjectiveReader.Silences(ObjectiveTable.ChapterAny, track,
                    TargetPass.SysAugments, ObjectiveReader.IdCount(TargetPass.SysAugments));
                augmentSilences += silent.Count;
                foreach (var s in silent)
                {
                    Assert.Equal(TargetPass.SilenceClass.DifferentShape, s.Class);
                    Assert.True(s.Known);
                }
            }
            Assert.Equal(42, augmentSilences);

            // ⚠ FIVE OF THE SEVEN SYSTEMS, NOT SEVEN — and the exception is a real tension INSIDE
            // 23 §2.5, recorded rather than resolved. §2.5's last row says tm-speed and tm-goldmulti
            // are "SILENT" on sadistic (ch.8 states nothing new for either), while the SAME section
            // tags the TM 49 row and the two 100LC rows "track-neutral" — because the game stores
            // ONE speedTarget and ONE multiTarget with no per-track split. Both statements are true:
            // the guide adds nothing on Sadistic, and the rows it already stated still apply there,
            // since there is no Sadistic TM field for them to be absent from. So TM is asserted
            // separately below, and neither reading is adjudicated.
            foreach (var system in new[] { TargetPass.SysAugments, TargetPass.SysNguEnergy,
                                           TargetPass.SysNguMagic, TargetPass.SysAt,
                                           TargetPass.SysWandoos })
            {
                var count = ObjectiveReader.IdCount(system);
                var silent = ObjectiveReader.Silences(ObjectiveTable.ChapterAny,
                    TargetPass.Track.Sadistic, system, count);
                Assert.Equal(count, silent.Count);
                foreach (var s in silent)
                {
                    Assert.False(string.IsNullOrEmpty(s.Reason));
                    Assert.True(s.Known, "every Sadistic slot is covered by the §7.1 S2 catch-all");
                }
            }
        }

        // The TM exception, asserted so it is a recorded finding and not an untested gap: on Sadistic
        // the TM lanes carry level rows, and every one of them is TRACK-NEUTRAL — none is a Sadistic
        // row. If a Sadistic-track TM row ever appears, 23 §2.5's SILENT claim has been violated and
        // this fails.
        [Fact]
        public void The_only_levels_reaching_a_sadistic_query_are_the_track_neutral_tm_rows()
        {
            foreach (var system in new[] { TargetPass.SysTmSpeed, TargetPass.SysTmGoldMulti })
            {
                var answer = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                    TargetPass.Track.Sadistic, system, 0);
                Assert.True(answer.HasLevel);
                foreach (var row in answer.LevelRows)
                {
                    Assert.True(row.TrackNeutral,
                        "a non-track-neutral row reached a Sadistic query — 23 §2.5 says SILENT");
                    Assert.NotEqual(TargetPass.Track.Sadistic, row.Track);
                }
            }

            // No system other than TM reaches a Sadistic query with a level, on any id.
            foreach (var system in new[] { TargetPass.SysAugments, TargetPass.SysNguEnergy,
                                           TargetPass.SysNguMagic, TargetPass.SysAt,
                                           TargetPass.SysWandoos })
            {
                for (int id = 0; id < ObjectiveReader.IdCount(system); id++)
                    Assert.False(ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                        TargetPass.Track.Sadistic, system, id).HasLevel);
            }
        }

        // ⚠ The augment silence is PRINCIPLED, not an omission: the guide's method is a live
        // per-rebirth solver, so the operator's artifact is A CHOSEN AUGMENT, not a level — a
        // DIFFERENT SHAPE from what augmentTarget consumes. The ledger must say so by class, because
        // "we have no number yet" and "a number is the wrong answer" are different states.
        //
        // And it is the case that separates the two questions: on ch.1 the guide DOES speak (the
        // "most expensive you can finish within 30m" selector), so Availability is Rows — while the
        // LEVEL answer is still a silence. Both are asserted, because a reader that collapsed them
        // would either lose the guidance or lose the ledger entry.
        [Fact]
        public void The_augment_silence_is_classified_as_a_different_shape_not_a_gap()
        {
            var slot = ObjectiveReader.Slot(1, TargetPass.Track.Normal, TargetPass.SysAugments, 4);
            Assert.Equal(ObjectiveReader.Availability.Rows, slot.Availability);
            Assert.False(slot.HasLevelRow);
            Assert.Contains(slot.Rows, r => r.Kind == TargetPass.RowKind.Predicate);

            var level = ObjectiveReader.LevelSlot(1, TargetPass.Track.Normal,
                TargetPass.SysAugments, 4);
            Assert.False(level.HasLevel);
            Assert.Empty(level.LevelRows);
            Assert.NotEmpty(level.OtherRows);          // the guidance survives the silence
            Assert.Equal(TargetPass.SilenceClass.DifferentShape, level.SilenceClass);
            Assert.Contains("CHOSEN AUGMENT", level.Reason, StringComparison.OrdinalIgnoreCase);

            // And Wandoos is classified as the SURPLUS SINK — correctly targetless, never to be
            // synthesised (23 §2.6; amendment 16 §4's "sole unterminated consumer").
            var wandoos = ObjectiveReader.LevelSlot(2, TargetPass.Track.Normal,
                TargetPass.SysWandoos, 0);
            Assert.False(wandoos.HasLevel);
            Assert.Equal(TargetPass.SilenceClass.SurplusSink, wandoos.SilenceClass);
        }

        // =====================================================================================
        // 4. TRACK SELECTION — a normal-track row does not satisfy an evil-track query
        // =====================================================================================
        // The game stores three targets per NGU — skills[id].target / evilTarget / sadisticTarget
        // ([DECOMP] NGU.cs:22-26) — compared to level / evilLevel / sadisticLevel. Handing a Normal
        // row to an Evil lane is the class of error the Track machinery exists to stop.
        [Fact]
        public void A_normal_track_row_does_not_satisfy_an_evil_track_query()
        {
            // The PAWG rungs are Normal-only. On Evil the same ids are a silence (rate only).
            foreach (var id in new[] { 0, 1, 3, 5 })
            {
                var normal = ObjectiveReader.Slot(3, TargetPass.Track.Normal,
                    TargetPass.SysNguEnergy, id);
                Assert.Equal(ObjectiveReader.Availability.Rows, normal.Availability);
                Assert.Contains(normal.Rows, r => r.Kind == TargetPass.RowKind.Level);

                var evil = ObjectiveReader.Slot(3, TargetPass.Track.Evil,
                    TargetPass.SysNguEnergy, id);
                Assert.Equal(ObjectiveReader.Availability.Silent, evil.Availability);
                Assert.DoesNotContain(evil.Rows, r => r.Kind == TargetPass.RowKind.Level);
            }

            // And the reverse: AT Block's Evil 100k row must not appear on a Normal query, where the
            // guide's number is 5,000 instead.
            var atNormal = ObjectiveReader.Slot(ObjectiveTable.ChapterAny, TargetPass.Track.Normal,
                TargetPass.SysAt, 2).Rows.Where(r => r.Kind == TargetPass.RowKind.Level).ToArray();
            var atEvil = ObjectiveReader.Slot(ObjectiveTable.ChapterAny, TargetPass.Track.Evil,
                TargetPass.SysAt, 2).Rows.Where(r => r.Kind == TargetPass.RowKind.Level).ToArray();

            Assert.Equal(5000L, Assert.Single(atNormal).ValueLow);
            Assert.Equal(100000L, Assert.Single(atEvil).ValueLow);
        }

        // The two structural exemptions are exemptions, not leaks: TrackNeutral is the game storing
        // ONE field (TM), AllTracks is the guide naming no track. Nothing else crosses tracks.
        [Fact]
        public void Only_track_neutral_and_all_tracks_rows_cross_a_track_boundary()
        {
            foreach (var row in ObjectiveTable.LaneRows)
            {
                if (row.TrackNeutral || row.AllTracks)
                    continue;
                Assert.NotEqual(TargetPass.Track.Unspecified, row.Track);
                foreach (var other in new[] { TargetPass.Track.Normal, TargetPass.Track.Evil,
                                              TargetPass.Track.Sadistic })
                {
                    if (other == row.Track)
                        Assert.True(ObjectiveTable.TrackMatches(row, other));
                    else
                        Assert.False(ObjectiveTable.TrackMatches(row, other));
                }
            }

            // Only TM rows may claim TrackNeutral — the game stores one speedTarget/multiTarget with
            // no per-track split. Anything else claiming it would be borrowing TM's storage shape.
            foreach (var row in ObjectiveTable.LaneRows.Where(r => r.TrackNeutral))
                Assert.True(row.System == TargetPass.SysTmSpeed ||
                            row.System == TargetPass.SysTmGoldMulti,
                    "TrackNeutral is a claim about the GAME'S STORAGE and is TM-only: " + row.System);
        }

        // Chapter keying is the field GuideRows lacks and 37 §S5 B6 asks for. A ch.3 rung must not
        // answer a ch.2 query, and a standing row must answer both.
        [Fact]
        public void Chapter_keying_selects_rungs_and_standing_rows_answer_every_chapter()
        {
            long RungAt(int chapter)
            {
                var rows = ObjectiveReader.Slot(chapter, TargetPass.Track.Normal,
                    TargetPass.SysNguEnergy, 0).Rows
                    .Where(r => r.Kind == TargetPass.RowKind.Level).ToArray();
                return rows.Length == 0 ? -1 : rows.Min(r => r.ValueLow);
            }

            Assert.Equal(-1, RungAt(1));        // ch.1 names no PAWG level
            Assert.Equal(-1, RungAt(2));        // ch.2 is the "focus Power a and Gold" ORDER only
            Assert.Equal(500L, RungAt(3));      // Mini-CBlock prep
            Assert.Equal(150000L, RungAt(4));   // CBlock2 prep; 5m+ is the other ch.4 rung

            var ch4 = ObjectiveReader.Slot(4, TargetPass.Track.Normal, TargetPass.SysNguEnergy, 0)
                .Rows.Where(r => r.Kind == TargetPass.RowKind.Level)
                .Select(r => r.ValueLow).OrderBy(v => v).ToArray();
            Assert.Equal(new[] { 150000L, 5000000L }, ch4);

            // The 100LC TM rows carry no chapter and must answer any chapter query.
            foreach (var chapter in new[] { 1, 5, 8 })
                Assert.Contains(ObjectiveReader.Slot(chapter, TargetPass.Track.Normal,
                    TargetPass.SysTmSpeed, 0).Rows, r => r.CampaignScope == "100lc");
        }

        // =====================================================================================
        // 5. THE FOUR GUIDE ONE-HIT POWERS AGREE WITH ZoneStatHelper'S DERIVED VALUES
        // =====================================================================================
        // ⚠ THE GUIDE IS THE CROSS-CHECK, NOT THE SOURCE. ZoneStatHelper.OPower is decomp-derived
        // (maxEnemyHP/1.2 + def/2, ZoneStatHelper.cs:152-170); the guide's figures are field-observed.
        // Two independent methods, four agreements. The decomp wins on any disagreement — this test
        // records the SIZE of the disagreement so an edit that widens it is visible.
        //
        // The 5% band is ZoneOPowerTests'. It is not a tolerance anyone chose; it is the measured
        // agreement. Choco is published as a TWO-hit figure, so it is compared doubled.
        //
        // ⚠ 23 §3.5 recorded EV and PPPL as "EXACT to every published digit". That is now true of EV
        // only: ZoneStatHelper's 2026-08-03 regeneration moved zone 22 by -0.31% (2.27e15 ->
        // 2.263e15, ZoneStatHelper.cs:185 records the move). Still an agreement; no longer an
        // identity. Recorded here rather than adjudicated.
        [Fact]
        public void The_four_guide_one_hit_powers_agree_with_the_derived_zone_stat_helper_values()
        {
            var stated = ObjectiveZones.ZonesWithGuideOneHitPower();
            Assert.Equal(4, stated.Count);
            Assert.Equal(new[] { 18, 20, 21, 22 }, stated.Select(z => z.Id).OrderBy(i => i).ToArray());

            var derived = DerivedOPower();
            foreach (var zone in stated)
            {
                Assert.True(derived.ContainsKey(zone.Id),
                    "zone " + zone.Id + " has no ZoneStatHelper row to cross-check against");

                var ratio = derived[zone.Id] / zone.OneHitAsOneShot;
                Assert.True(ratio > 0.95 && ratio < 1.05, string.Format(CultureInfo.InvariantCulture,
                    "zone {0} ({1}): derived OPower {2:e4} is {3:0.000}x the guide's {4:e4} " +
                    "({5} hit(s) at {6:e3}). The decomp wins, but a divergence this large is a finding.",
                    zone.Id, zone.Name, derived[zone.Id], ratio, zone.OneHitAsOneShot,
                    zone.OneHitHits, zone.OneHitPowerGuide));
            }
        }

        // The guide's one-hit coverage IS the finding: 4 of 33, and ZERO of the nine Sadistic zones.
        // A consumer that expects this column to be populated is expecting the wrong thing.
        [Fact]
        public void One_hit_power_coverage_is_four_of_thirty_three_and_none_in_sadistic()
        {
            Assert.Equal(2, ObjectiveZones.Zones.Count(z =>
                z.Band == TargetPass.Track.Normal && z.HasOneHitPower));
            Assert.Equal(2, ObjectiveZones.Zones.Count(z =>
                z.Band == TargetPass.Track.Evil && z.HasOneHitPower));
            Assert.Equal(0, ObjectiveZones.Zones.Count(z =>
                z.Band == TargetPass.Track.Sadistic && z.HasOneHitPower));

            // Choco is the only multi-hit figure the guide publishes.
            var choco = ObjectiveZones.Zones.Single(z => z.Id == 20);
            Assert.Equal(2, choco.OneHitHits);
            foreach (var z in ObjectiveZones.Zones.Where(x => x.HasOneHitPower && x.Id != 20))
                Assert.Equal(1, z.OneHitHits);
        }

        // =====================================================================================
        // 6. ZONE 43'S ESTIMATE COLUMNS ARE FLAGGED AS ESTIMATES
        // =====================================================================================
        // 01 line 253: ZoneStatHelper.Defaults had no zone-43 row, so the OPower gate's TryGetValue
        // missed and the zone read one-shottable at ANY attack in two advisors — a fail-OPEN. The
        // guide supplies four of the five fields the missing row needed, AS A COMMUNITY ESTIMATE, and
        // states no one-hit power for this or any other Sadistic zone. The honest form of that row is
        // one carrying a provenance flag, not a silent addition.
        [Fact]
        public void Zone_43_carries_its_stats_as_a_labelled_community_estimate()
        {
            ObjectiveZones.ZoneRow z43;
            Assert.True(ObjectiveZones.TryGetZone(43, out z43));

            Assert.True(z43.StatsAreCommunityEstimate);
            Assert.False(z43.HasOneHitPower);          // the guide states none, here or anywhere in Sadistic
            Assert.Equal(1.7e34, z43.ManualPower);
            Assert.Equal(6e33, z43.ManualToughness);
            Assert.Equal(4.7e34, z43.IdlePower);
            Assert.Equal(3.4e34, z43.IdleToughness);
            Assert.Contains("PLAYER CONSENSUS", z43.Technique);
            Assert.Contains("NO PHASE RULE", z43.PhaseRule);

            // It is the ONLY zone flagged this way — the flag must not spread by copy-paste.
            Assert.Equal(43, ObjectiveZones.Zones.Single(z => z.StatsAreCommunityEstimate).Id);
        }

        // The guide's OTHER self-flagged rows: it marks Breadverse / 70's / Halloweenies IDLE (and
        // ⟨BM⟩) with an asterisk and says its own numbers are the worse of two sources it holds.
        // Three zones, all Sadistic, and no others.
        [Fact]
        public void The_three_guide_flagged_unreliable_idle_rows_are_marked_and_no_others_are()
        {
            var flagged = ObjectiveZones.Zones.Where(z => z.IdleFlaggedUnreliableByGuide)
                .Select(z => z.Id).OrderBy(i => i).ToArray();
            Assert.Equal(new[] { 35, 36, 37 }, flagged);
            foreach (var z in ObjectiveZones.Zones.Where(x => x.IdleFlaggedUnreliableByGuide))
                Assert.Equal(TargetPass.Track.Sadistic, z.Band);
        }

        // ⟨BM⟩ is published for Evil and Sadistic only; the Normal band has ONE inline figure (Choco).
        // A 0 in a BM field means NOT PUBLISHED — the same silence-is-not-a-zero rule as the lanes.
        [Fact]
        public void Beast_mode_stats_are_published_for_evil_and_sadistic_and_one_normal_zone()
        {
            var normalWithBm = ObjectiveZones.Zones
                .Where(z => z.Band == TargetPass.Track.Normal && z.HasBeastMode).ToArray();
            Assert.Equal(20, Assert.Single(normalWithBm).Id);

            foreach (var z in ObjectiveZones.Zones.Where(x => x.Band != TargetPass.Track.Normal))
                Assert.True(z.HasBeastMode, "zone " + z.Id + " (" + z.Name + ") has no BM row");

            // No row may carry a BM number without claiming to have one, and vice versa.
            foreach (var z in ObjectiveZones.Zones)
            {
                if (z.HasBeastMode)
                    Assert.True(z.BeastModePower > 0 && z.BeastModeToughness > 0);
                else
                    Assert.True(z.BeastModePower == 0 && z.BeastModeToughness == 0);
            }
        }

        // =====================================================================================
        // TRANSCRIPTION INVARIANTS — the things a bad row would break first
        // =====================================================================================

        [Fact]
        public void No_row_is_emitted_without_a_cite()
        {
            foreach (var row in ObjectiveTable.LaneRows)
                Assert.False(string.IsNullOrEmpty(row.Cite), "lane row for " + row.System);
            foreach (var z in ObjectiveZones.Zones)
                Assert.False(string.IsNullOrEmpty(z.Cite), "zone row " + z.Id);
            foreach (var c in ObjectiveTable.Conflicts)
                Assert.False(string.IsNullOrEmpty(c.Cite));
            foreach (var r in ObjectiveTable.CrossCuttingRules)
                Assert.False(string.IsNullOrEmpty(r.Cite));
            foreach (var i in ObjectiveZones.Itopod)
                Assert.False(string.IsNullOrEmpty(i.Cite));
        }

        // ⚠ DO NOT SYNTHESISE A WANDOOS TARGET. The guide's terminator for Wandoos is an OS SWITCH,
        // never a level; amendment 16 §4 independently found it "the sole unterminated consumer"; and
        // the P1 lane-target campaign established that a synthetic Wandoos target is exactly what
        // makes amendment 16 §4's ranking come out at zero. The table carries the guide's Wandoos
        // GUIDANCE — which is the evidence for the finding — and none of it is a level.
        [Fact]
        public void No_wandoos_row_is_a_level_and_every_wandoos_row_refuses_at_the_router()
        {
            var wandoos = ObjectiveTable.LaneRows.Where(r => r.System == TargetPass.SysWandoos)
                .ToArray();
            Assert.NotEmpty(wandoos);

            foreach (var row in wandoos)
            {
                Assert.NotEqual(TargetPass.RowKind.Level, row.Kind);
                var route = TargetPass.Route(row.ToTargetRow(0));
                Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
                Assert.Contains("DO NOT SYNTHESISE", route.Reason);
                Assert.Equal(0L, route.TargetToWrite);
            }
        }

        // A RANGE STAYS A RANGE. Four rows carry one; none is averaged, interpolated or collapsed,
        // and a ranged terminal would be refused rather than silently taking an end.
        [Fact]
        public void Ranges_stay_ranges_and_are_never_collapsed_to_an_end()
        {
            var ranges = ObjectiveTable.LaneRows.Where(r => r.IsRange)
                .Select(r => Tuple.Create(r.System, r.ValueLow, r.ValueHigh))
                .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ToArray();

            Assert.Equal(new[]
            {
                Tuple.Create(TargetPass.SysAt, 2000L, 3000L),          // ch.2 T4 LRB
                Tuple.Create(TargetPass.SysAt, 60000L, 80000L),        // ch.3 BDW -> T6
                Tuple.Create(TargetPass.SysNguEnergy, 2000L, 3000L),   // Adventure a, Beardverse
                Tuple.Create(TargetPass.SysNguEnergy, 60000L, 80000L), // Adventure a, BDW -> T6
            }, ranges);

            foreach (var row in ObjectiveTable.LaneRows.Where(r => r.IsRange))
            {
                Assert.True(row.ValueHigh > row.ValueLow);
                // Every range in §2 is a precondition; a ranged TERMINAL would be an upstream error
                // and Route surfaces it for the operator rather than picking an end.
                Assert.Equal(TargetPass.Terminality.Precondition, row.Terminality);
            }
        }

        // A level row with value 0 would be the game's UNSET sentinel — the one number a target must
        // never be. WriteTargetGuard refuses it at the router; this stops one reaching the router.
        [Fact]
        public void No_level_row_carries_the_games_unset_sentinel_or_the_never_fund_marker()
        {
            foreach (var row in ObjectiveTable.LaneRows.Where(r => r.Kind == TargetPass.RowKind.Level))
            {
                Assert.True(row.ValueLow > 0, "level row for " + row.System + " has value 0 or less");
                Assert.True(row.ValueHigh >= row.ValueLow);
                Assert.True(row.ValueHigh <= TargetPass.NguHardCap,
                    "a target above the 1e9 hardcap can never be met, so the cascade never terminates");
                Assert.NotEqual(TargetPass.Terminality.Unspecified, row.Terminality);
            }

            // The two `rate = 0` rows (ch.1 TM and ch.1 Wandoos, both "don't level this yet") DO
            // carry 0 — and that is why they must not be level rows.
            var zeroRate = ObjectiveTable.LaneRows
                .Where(r => r.Kind == TargetPass.RowKind.Rate && r.ValueLow == 0 &&
                            r.ValueText != null && r.ValueText.StartsWith("rate = 0",
                                StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, zeroRate.Length);
            foreach (var r in zeroRate)
                Assert.NotEqual(TargetPass.RowKind.Level, r.Kind);
        }

        // The 100LC's TM 59/10 are terminal but CAMPAIGN-SCOPED — they hold only inside the 100 Level
        // Challenge and never as a standing speedTarget. 59 + 10 = 69 of the challenge's 100, both
        // counting against the budget (TimeMachineController.cs:354-357, :397-400 via 21 §A2). This is
        // the NARROWING of 22 §Q1.2's "only terminal" claim, which is NGU-scoped (23 §0.4).
        [Fact]
        public void The_two_campaign_scoped_terminals_never_write_as_standing_targets()
        {
            var scoped = ObjectiveTable.LaneRows.Where(r => r.CampaignScope == "100lc" &&
                r.Kind == TargetPass.RowKind.Level).ToArray();
            Assert.Equal(2, scoped.Length);
            Assert.Equal(69L, scoped.Sum(r => r.ValueLow));

            foreach (var row in scoped)
            {
                Assert.Equal(TargetPass.Terminality.Terminal, row.Terminality);
                var route = TargetPass.Route(row.ToTargetRow(0));
                Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
                Assert.Contains("campaign-scoped", route.Reason);
                Assert.Equal(0L, route.TargetToWrite);
            }

            // Still exactly ONE standing terminal — but it is no longer the one 23 §0.4 named.
            //
            // ⚠ 23 §0.4's "sole standing terminal" was Respawn 401 and that row is GONE, removed by
            // [OPERATOR] 2026-08-07 as situational guide advice. The count survives by coincidence,
            // not by continuity: Block AT became TERMINAL at 100,000 in the same commit, on a
            // separate ruling ("a hard cap at 100,000 and should never be capped lower"). Do not
            // read this Single() as confirming 23 §0.4 — it now confirms something 23 never said.
            var standing = ObjectiveTable.LaneRows
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal &&
                            r.CampaignScope == null).ToArray();
            var only = Assert.Single(standing);
            Assert.Equal(TargetPass.SysAt, only.System);
            Assert.True(only.Covers(2));
            Assert.Equal(100000L, only.ValueLow);
        }

        // ⚠ THE TRAP ROW. TM speed 49: the guide names the number, explains what happens at 50, and
        // says DON'T STOP. Setting speedTarget = 49 would implement the number and invert the advice.
        [Fact]
        public void Tm_49_is_a_precondition_carrying_the_dont_stop_clause()
        {
            var tm49 = ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysTmSpeed && r.Kind == TargetPass.RowKind.Level &&
                r.ValueLow == 49);

            Assert.Equal(TargetPass.Terminality.Precondition, tm49.Terminality);
            Assert.True(tm49.TrackNeutral);
            Assert.Null(tm49.CampaignScope);
            Assert.Contains("DON'T STOP", tm49.ValueText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TargetPass.Disposition.Precondition,
                TargetPass.Route(tm49.ToTargetRow(0)).Disposition);
        }

        // ⚠ THE BROKEN RUNG. "Block Reduction reaches 90% at Level 400, 99% at 5k, 99.9% AT 5, and
        // 99.99% at 500k" — a monotone ladder 400 -> 5k -> 5 -> 500k is impossible. Both neighbours
        // are usable; the broken rung is carried UNUSABLE with its stated level preserved, and is not
        // adjudicated. DO NOT TRANSCRIBE 5 INTO A levelTarget.
        [Fact]
        public void The_block_damage_curves_broken_rung_is_carried_as_unusable()
        {
            var broken = ObjectiveTable.BlockDamageCurve.Single(r => !r.Usable);
            Assert.Equal(5L, broken.Level);
            Assert.Equal("99.9%", broken.BlockReduction);
            Assert.Contains("BROKEN RUNG", broken.Note);

            // Its neighbours survive intact and stay monotone without it.
            var usable = ObjectiveTable.BlockDamageCurve.Where(r => r.Usable)
                .Select(r => r.Level).ToArray();
            Assert.Equal(new[] { 400L, 5000L, 500000L, 1000000L }, usable);

            // No level row anywhere may have been built from the broken rung.
            Assert.DoesNotContain(ObjectiveTable.LaneRows,
                r => r.System == TargetPass.SysAt && r.Kind == TargetPass.RowKind.Level &&
                     r.ValueLow == 5);

            // The 5,000 rung is the one ch.5's ">99%" T8 requirement pins against, and it is also the
            // ch.3 AT Block level row.
            Assert.Equal(5000L, ObjectiveTable.LaneRows.Single(r =>
                r.System == TargetPass.SysAt && r.Covers(2) &&
                r.Track == TargetPass.Track.Normal &&
                r.Kind == TargetPass.RowKind.Level).ValueLow);
        }

        // The M0/M1 conflict: TWO readings, NEITHER adjudicated by 23, and NO numeric value emitted
        // on either. The curve constants (400 / 2000) are what is NOT in dispute and live in
        // Softcaps, where they cannot be mistaken for targets.
        [Fact]
        public void The_m0_m1_conflict_emits_both_readings_and_no_number()
        {
            var conflict = Assert.Single(ObjectiveTable.Conflicts);
            Assert.Equal(TargetPass.SysNguMagic, conflict.System);
            Assert.Equal(new[] { 0, 1 }, conflict.Ids);
            Assert.Equal(TargetPass.Track.Evil, conflict.Track);
            Assert.False(string.IsNullOrEmpty(conflict.ReadingA));
            Assert.False(string.IsNullOrEmpty(conflict.ReadingB));
            Assert.False(string.IsNullOrEmpty(conflict.NotInDispute));
            // The decision record DOES resolve it (amendment 18 §1 / 21 §1, in favour of reading B);
            // 23 does not. Both facts are carried.
            Assert.Contains("reading B", conflict.Resolution, StringComparison.OrdinalIgnoreCase);

            foreach (var id in new[] { 0, 1 })
            {
                var answer = ObjectiveReader.Slot(5, TargetPass.Track.Evil,
                    TargetPass.SysNguMagic, id);
                Assert.Equal(ObjectiveReader.Availability.Conflict, answer.Availability);
                Assert.Empty(answer.Rows);
                Assert.Single(answer.Conflicts);
                Assert.Contains("operator must choose", answer.Reason);
            }

            // No level row exists for either id on Evil, on any chapter — the conflict is not a
            // number wearing a label.
            Assert.DoesNotContain(ObjectiveTable.LaneRows,
                r => r.System == TargetPass.SysNguMagic && (r.Covers(0) || r.Covers(1)) &&
                     r.Track == TargetPass.Track.Evil && r.Kind == TargetPass.RowKind.Level);
        }

        // ⚠ 23 §0.3's single most consequential fact: the guide's entire Evil NGU policy is rate,
        // time and predicate, never level. The three Evil level rows it DOES emit (E7, E8, M5) are
        // preconditions or AMBIGUOUS — never terminal — so no Evil NGU is ever WRITTEN, which is the
        // operative consequence of amendment 18 §1 whichever reading of it you take.
        [Fact]
        public void No_evil_ngu_row_is_terminal_and_the_three_level_rows_are_soft()
        {
            var evilNguLevels = ObjectiveTable.LaneRows.Where(r =>
                TargetPass.IsNguSystem(r.System) && r.Track == TargetPass.Track.Evil &&
                r.Kind == TargetPass.RowKind.Level).ToArray();

            Assert.Equal(3, evilNguLevels.Length);
            foreach (var row in evilNguLevels)
            {
                Assert.NotEqual(TargetPass.Terminality.Terminal, row.Terminality);
                Assert.Equal(1000L, row.ValueLow);   // all three are the "softcap" 1000
                foreach (var id in row.Ids)
                    Assert.NotEqual(TargetPass.Disposition.WriteTarget,
                        TargetPass.Route(row.ToTargetRow(id)).Disposition);
            }

            // Exactly one AMBIGUOUS row in the whole table — the PP softcap, whose stop-vs-continue
            // the guide's own text does not settle. NOT GUESSED.
            var ambiguous = Assert.Single(ObjectiveTable.LaneRows,
                r => r.Terminality == TargetPass.Terminality.Ambiguous);
            Assert.Equal(TargetPass.SysNguEnergy, ambiguous.System);
            Assert.True(ambiguous.Covers(8));
            Assert.Equal(TargetPass.Disposition.OperatorDecision,
                TargetPass.Route(ambiguous.ToTargetRow(8)).Disposition);
        }

        // Non-level kinds must never reach Pass 3, and must be REFUSED WITH A REASON rather than
        // silently ignored — a rate row arriving there is a caller error, not a no-op.
        [Fact]
        public void Rate_time_and_predicate_rows_are_refused_with_a_reason()
        {
            var refused = 0;
            foreach (var row in ObjectiveTable.LaneRows)
            {
                if (row.Kind == TargetPass.RowKind.Level)
                    continue;
                Assert.NotEqual(TargetPass.RowKind.Unspecified, row.Kind);

                foreach (var id in row.Ids ?? new[] { ObjectiveTable.NoIndex })
                {
                    var route = TargetPass.Route(row.ToTargetRow(id));
                    Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
                    Assert.False(string.IsNullOrEmpty(route.Reason));
                    Assert.Equal(0L, route.TargetToWrite);
                    refused++;
                }
            }
            Assert.True(refused >= 20, "expected the predicate/rate/time bulk; found " + refused);
        }

        // An unindexed row (23 §0.2: augments emit no index) must not be selectable by
        // TargetPass.RowsFor for a real slot — the fail-closed half of NoIndex.
        [Fact]
        public void An_unindexed_rule_row_cannot_speak_for_a_specific_slot()
        {
            var augmentRule = ObjectiveTable.LaneRows.First(r =>
                r.System == TargetPass.SysAugments);
            var asTargetRow = augmentRule.ToTargetRow(ObjectiveTable.NoIndex);
            Assert.Equal(-1, asTargetRow.Index);

            var table = new List<TargetPass.TargetRow> { asTargetRow };
            for (int slot = 0; slot < 14; slot++)
                Assert.Null(TargetPass.RowsFor(table, TargetPass.SysAugments, slot));
        }

        // =====================================================================================
        // COVERAGE — the counts, so a dropped row is visible rather than merely absent
        // =====================================================================================

        // The census, pinned. A transcription's failure mode is a row quietly going missing, which no
        // behavioural test can see because nothing consumes the table. These numbers are the ones
        // reported to the operator; if an edit moves one, it has to move this too.
        [Fact]
        public void The_lane_table_census_matches_what_was_transcribed_from_23_section_2()
        {
            // ⚠ 69, NOT 70 — the Respawn 401 row was removed by [OPERATOR] 2026-08-07. This census
            // exists so a dropped row is visible rather than merely absent, so the drop is recorded
            // here rather than the number quietly following the table.
            Assert.Equal(69, ObjectiveTable.LaneRows.Length);

            // By track. Sadistic is ZERO by design (23 §2.9 / §7.1 S2).
            Assert.Equal(39, ObjectiveTable.LaneRows.Count(r =>
                r.Track == TargetPass.Track.Normal && !r.AllTracks && !r.TrackNeutral));
            Assert.Equal(23, ObjectiveTable.LaneRows.Count(r =>
                r.Track == TargetPass.Track.Evil && !r.AllTracks && !r.TrackNeutral));
            Assert.Equal(0, ObjectiveTable.LaneRows.Count(r =>
                r.Track == TargetPass.Track.Sadistic));
            Assert.Equal(3, ObjectiveTable.LaneRows.Count(r => r.TrackNeutral));   // the three TM rows
            Assert.Equal(4, ObjectiveTable.LaneRows.Count(r => r.AllTracks));

            // By kind. Only `level` can ever reach Pass 3; the other 38 rows are the guide's
            // guidance in the shapes the constraint layer cannot consume — which is the finding.
            Assert.Equal(31, ObjectiveTable.LaneRows.Count(r => r.Kind == TargetPass.RowKind.Level));
            Assert.Equal(10, ObjectiveTable.LaneRows.Count(r => r.Kind == TargetPass.RowKind.Rate));
            Assert.Equal(9, ObjectiveTable.LaneRows.Count(r => r.Kind == TargetPass.RowKind.Time));
            Assert.Equal(19, ObjectiveTable.LaneRows.Count(r =>
                r.Kind == TargetPass.RowKind.Predicate));

            // By terminality: ONE standing terminal + two campaign-scoped, 29 preconditions, ONE
            // ambiguous. Every level row carries one of the three — none is Unspecified.
            //
            // ⚠ THE TERMINAL COUNT IS UNCHANGED AT 3 AND THAT IS A COINCIDENCE, NOT CONTINUITY.
            // Respawn 401 left the table (-1) and Block AT was promoted Precondition -> Terminal
            // (+1) in the same commit, on two unrelated [OPERATOR] rulings. Preconditions fell 30
            // -> 29 for the same reason. A future edit must not read "still 3" as "nothing moved".
            Assert.Equal(3, ObjectiveTable.LaneRows.Count(r =>
                r.Terminality == TargetPass.Terminality.Terminal));
            Assert.Equal(29, ObjectiveTable.LaneRows.Count(r =>
                r.Terminality == TargetPass.Terminality.Precondition));
            Assert.Equal(1, ObjectiveTable.LaneRows.Count(r =>
                r.Terminality == TargetPass.Terminality.Ambiguous));

            // By system.
            Assert.Equal(4, ObjectiveTable.LaneRows.Count(r => r.System == TargetPass.SysAugments));
            Assert.Equal(36, ObjectiveTable.LaneRows.Count(r => r.System == TargetPass.SysNguEnergy));
            Assert.Equal(6, ObjectiveTable.LaneRows.Count(r => r.System == TargetPass.SysNguMagic));
            Assert.Equal(11, ObjectiveTable.LaneRows.Count(r => r.System == TargetPass.SysAt));
            Assert.Equal(6, ObjectiveTable.LaneRows.Count(r => r.System == TargetPass.SysTmSpeed));
            Assert.Equal(1, ObjectiveTable.LaneRows.Count(r =>
                r.System == TargetPass.SysTmGoldMulti));
            Assert.Equal(5, ObjectiveTable.LaneRows.Count(r => r.System == TargetPass.SysWandoos));

            Assert.Equal(4, ObjectiveTable.LaneRows.Count(r => r.IsRange));
            Assert.Equal(3, ObjectiveTable.LaneRows.Count(r => r.CampaignScope != null));

            // The four LEVEL rows on the Evil track: three NGU (E7, E8, M5) plus AT Block's 100k.
            Assert.Equal(4, ObjectiveTable.LaneRows.Count(r =>
                r.Kind == TargetPass.RowKind.Level && r.Track == TargetPass.Track.Evil));
        }

        // 23 §7's ledger as a number: of the 38 addressable slots x 3 tracks = 114, the guide supplies
        // a LEVEL for 20 and is silent on 94. Every one of the 94 carries a recorded reason.
        //
        // ⚠ 38, NOT 37. 23 §7.2 leaves the two counts unreconciled ("14 augment + 16 NGU + 5 AT +
        // 2 TM = 37" against amendment 16 §8's and 22 §Q1.0's 38). Enumerated per system the gap is
        // arithmetic: the 37 omits WANDOOS, which 23 gives its own section (§2.6) and its own silence
        // row (§7.2). Asserted here so the enumeration this table is built on is explicit.
        [Fact]
        public void The_guide_supplies_a_level_for_19_of_114_slots_and_names_the_other_95()
        {
            Assert.Equal(38, ObjectiveReader.AllSystems.Sum(s => ObjectiveReader.IdCount(s)));
            Assert.Equal(37, ObjectiveReader.AllSystems
                .Where(s => s != TargetPass.SysWandoos).Sum(s => ObjectiveReader.IdCount(s)));

            var silent = 0;
            var slots = 0;
            var unledgered = new List<string>();
            foreach (var system in ObjectiveReader.AllSystems)
            {
                var count = ObjectiveReader.IdCount(system);
                foreach (var track in new[] { TargetPass.Track.Normal, TargetPass.Track.Evil,
                                              TargetPass.Track.Sadistic })
                {
                    slots += count;
                    foreach (var s in ObjectiveReader.Silences(ObjectiveTable.ChapterAny, track,
                        system, count))
                    {
                        silent++;
                        Assert.False(string.IsNullOrEmpty(s.Reason),
                            "silence at (" + s.System + ", " + s.Id + ", " + s.Track +
                            ") carries no reason — 23 §7 is a LIST, not a discovery");
                        if (!s.Known)
                            unledgered.Add(s.System + "/" + s.Id + "/" + s.Track);
                    }
                }
            }

            // ⚠ 19 SUPPLIED / 95 SILENT, WAS 20 / 94. The Respawn (ngu-energy id 2, Normal) slot
            // moved from supplied to silent when [OPERATOR] removed the row 2026-08-07. The slot
            // total is unchanged — 23 §7's ledger is about which slots have a level, and one now
            // does not. Every silence still carries a reason, asserted in the loop above.
            Assert.Equal(114, slots);
            Assert.Equal(95, silent);
            Assert.Equal(19, slots - silent);

            // ⚠ "NAMES THE OTHER 95" IS THIS TEST'S OWN NAME AND IT IS OFF BY ONE. The loop above
            // only ever asserted that each silence carries a REASON, which ObjectiveReader's
            // unledgered fallback text satisfies — so the naming claim was never checked. The ledger
            // names 94. The ninety-fifth is the very slot [OPERATOR] emptied on 2026-08-07: removing
            // the Respawn 401 row moved (ngu-energy, 2, Normal) from SUPPLIED to SILENT (recorded in
            // the block above, which is why 20/94 became 19/95) — but nothing gave the new silence a
            // ledger entry, so it is silent with no recorded provenance.
            //
            // ⚠ IT IS SURFACED, NOT DEFAULTED — no 0, no -1, no long.MaxValue — so this is a
            // PROVENANCE gap, not a correctness one. Pinned by name rather than closed: writing its
            // reason means asserting why the guide is silent about Respawn on Normal, which is the
            // ledger owner's call. See ChapterMissDerivationTests for the measurement.
            Assert.Equal(new[] { TargetPass.SysNguEnergy + "/2/" + TargetPass.Track.Normal },
                unledgered);
            Assert.Equal(94, silent - unledgered.Count);
        }

        [Fact]
        public void The_lane_table_covers_every_section_of_23_section_2()
        {
            // 23 §1: augments emit ZERO level rows on every track; Wandoos emits ZERO on every track;
            // Sadistic emits NOTHING, every system.
            Assert.DoesNotContain(ObjectiveTable.LaneRows, r =>
                r.System == TargetPass.SysAugments && r.Kind == TargetPass.RowKind.Level);
            Assert.DoesNotContain(ObjectiveTable.LaneRows, r =>
                r.System == TargetPass.SysWandoos && r.Kind == TargetPass.RowKind.Level);
            Assert.DoesNotContain(ObjectiveTable.LaneRows, r =>
                r.Track == TargetPass.Track.Sadistic);

            // Per-system presence, so a whole section cannot vanish silently.
            foreach (var system in ObjectiveReader.AllSystems)
                Assert.Contains(ObjectiveTable.LaneRows, r => r.System == system);

            // §2.8's cross-cutting rules — 23's heading says "three", its table carries FOUR.
            // Transcribed as four; recorded, not adjudicated.
            Assert.Equal(4, ObjectiveTable.CrossCuttingRules.Length);
            Assert.Contains(ObjectiveTable.CrossCuttingRules, r => r.Name.Contains("CBlock2"));
            foreach (var r in ObjectiveTable.CrossCuttingRules)
            {
                Assert.NotEqual(TargetPass.RowKind.Level, r.Kind);   // "NOT targets" is the section title
                Assert.False(string.IsNullOrEmpty(r.Trigger));
            }
        }

        [Fact]
        public void The_zone_table_covers_all_33_zones_across_the_three_bands()
        {
            Assert.Equal(33, ObjectiveZones.Zones.Length);
            Assert.Equal(16, ObjectiveZones.Zones.Count(z => z.Band == TargetPass.Track.Normal));
            Assert.Equal(8, ObjectiveZones.Zones.Count(z => z.Band == TargetPass.Track.Evil));
            Assert.Equal(9, ObjectiveZones.Zones.Count(z => z.Band == TargetPass.Track.Sadistic));

            // No duplicate ids, and the titan zones (6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42)
            // plus the two final-boss zones (44, 45) are correctly ABSENT — their max HP is a titan,
            // not a farm target.
            Assert.Equal(33, ObjectiveZones.Zones.Select(z => z.Id).Distinct().Count());
            foreach (var titan in new[] { 6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42, 44, 45 })
                Assert.DoesNotContain(ObjectiveZones.Zones, z => z.Id == titan);

            // Every zone but the Safe Zone carries a full manual + idle pair; the Safe Zone has no
            // P/T row in the guide at all and must not pretend to.
            foreach (var z in ObjectiveZones.Zones)
            {
                Assert.False(string.IsNullOrEmpty(z.Name));
                Assert.True(z.UnlockBoss > 0, "zone " + z.Id + " has no unlock boss");
                if (z.Id == -1)
                {
                    Assert.False(z.HasStats);
                    Assert.Equal(0.0, z.IdlePower);
                }
                else
                {
                    Assert.True(z.HasStats);
                    Assert.True(z.ManualPower > 0 && z.ManualToughness > 0);
                    Assert.True(z.IdlePower > 0 && z.IdleToughness > 0);
                    Assert.True(z.IdlePower >= z.ManualPower,
                        "zone " + z.Id + ": idle should never be easier than manual");
                }
            }
        }

        // The unlock gate binds only on a run of the zone's OWN difficulty: on Evil every Normal zone
        // is free, on Sadistic every Normal AND Evil zone is free
        // ([GUIDE mechanics/general-info §Difficulties]). A consumer reading UnlockBoss for a lower
        // band on a higher-difficulty run reads a gate that does not apply.
        [Fact]
        public void A_lower_band_unlocks_freely_on_a_higher_difficulty_run()
        {
            Assert.True(ObjectiveZones.BandUnlocksFreelyOn(
                TargetPass.Track.Normal, TargetPass.Track.Evil));
            Assert.True(ObjectiveZones.BandUnlocksFreelyOn(
                TargetPass.Track.Normal, TargetPass.Track.Sadistic));
            Assert.True(ObjectiveZones.BandUnlocksFreelyOn(
                TargetPass.Track.Evil, TargetPass.Track.Sadistic));

            Assert.False(ObjectiveZones.BandUnlocksFreelyOn(
                TargetPass.Track.Normal, TargetPass.Track.Normal));
            Assert.False(ObjectiveZones.BandUnlocksFreelyOn(
                TargetPass.Track.Evil, TargetPass.Track.Normal));
            Assert.False(ObjectiveZones.BandUnlocksFreelyOn(
                TargetPass.Track.Sadistic, TargetPass.Track.Sadistic));
        }

        // The Rad-Lands row is 23 §3.2's "most useful single finding": the ONLY zone with an explicit
        // instruction NOT to snipe, and the reason is a DROP CHANCE property, not a stat threshold.
        // Any advisor that routes on P/T alone will send a player to snipe Rad and they get nothing.
        [Fact]
        public void The_rad_lands_anti_snipe_rule_is_transcribed_and_stated_as_a_drop_chance_reason()
        {
            ObjectiveZones.ZoneRow rad;
            Assert.True(ObjectiveZones.TryGetZone(31, out rad));
            Assert.Contains("ANTI-SNIPE", rad.PhaseRule);
            Assert.Contains("DC IS VERY LOW", rad.PhaseRule, StringComparison.OrdinalIgnoreCase);
            Assert.Null(rad.SnipeStats);   // the one Evil zone deliberately without them
        }

        // The zone reader answers by (chapter, band) and does not leak across either axis.
        [Fact]
        public void Zone_queries_select_by_chapter_and_band()
        {
            var ch1 = ObjectiveReader.Zones(1, TargetPass.Track.Normal);
            Assert.Equal(new[] { -1, 0, 1, 2, 3, 4, 5 },
                ch1.Select(z => z.Id).OrderBy(i => i).ToArray());

            // No Evil zone answers a ch.1 query at all.
            Assert.Empty(ObjectiveReader.Zones(1, TargetPass.Track.Evil));

            // ⚠ ZONE 43 IS THE ONE EXCEPTION, AND DELIBERATELY SO. Ch.8 never names it, so it is
            // chapter-less and answers EVERY chapter query. That is the behaviour that closes 01's
            // fail-open: the row a chapter-keyed consumer would otherwise never see is the row whose
            // absence made zone 43 read one-shottable at any attack.
            Assert.Equal(new[] { 43 }, ObjectiveReader.Zones(1, TargetPass.Track.Sadistic)
                .Select(z => z.Id).ToArray());

            // Ch.5 opens the Evil band; the Normal band has nothing there.
            Assert.Equal(new[] { 21, 22, 24, 25 },
                ObjectiveReader.Zones(5, TargetPass.Track.Evil)
                    .Select(z => z.Id).OrderBy(i => i).ToArray());
            Assert.Empty(ObjectiveReader.Zones(5, TargetPass.Track.Normal));

            // Zone 43 has no chapter (ch.8 never names it) and must therefore answer any Sadistic
            // chapter query — which is exactly how a consumer would find the fail-open row.
            Assert.Contains(ObjectiveReader.Zones(8, TargetPass.Track.Sadistic), z => z.Id == 43);
            Assert.Contains(ObjectiveReader.Zones(ObjectiveTable.ChapterAny,
                TargetPass.Track.Sadistic), z => z.Id == 43);
        }

        // =====================================================================================
        // THE READER TOUCHES NOTHING (T2d) — asserted structurally, not by inspection
        // =====================================================================================
        // The three objective-layer files must not reference the game, the profile, or any writer.
        // This is the same source-as-text technique ZoneOPowerTests uses on ZoneStatHelper, applied
        // to the "this commit changes no allocation" claim rather than to a table of numbers.
        [Theory]
        [InlineData("ObjectiveTable.cs")]
        [InlineData("ObjectiveZones.cs")]
        [InlineData("ObjectiveReader.cs")]
        public void The_objective_layer_reaches_no_game_state_and_no_writer(string file)
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", file));
            var code = StripComments(src);

            foreach (var forbidden in new[]
            {
                "Main.Character", "Main.Log", "Main.Settings", "UnityEngine", "MonoBehaviour",
                "ProfileService", "ProfileModel", "ChallengeOverlay", "FeasibilityPass",
                "ConstraintLayer", "AdvisorApply", "File.", "Directory.",
            })
            {
                Assert.False(code.Contains(forbidden),
                    file + " references " + forbidden + " — the objective layer is data plus a " +
                    "pure reader; nothing in it may reach game state, the profile, or a writer");
            }
        }

        // ---- helpers ---------------------------------------------------------------------------

        // Comments in these files quote decomp symbols and name other components on purpose (that is
        // the provenance record). Only executable code is checked.
        private static string StripComments(string src)
        {
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            src = Regex.Replace(src, @"(?m)^\s*//.*$", " ");
            src = Regex.Replace(src, @"(?m)(?<=[^:])//.*$", " ");
            return src;
        }

        // ZoneStatHelper.cs reaches Main.Character (:96, :107, :116), Main.Log and ZoneHelpers, so it
        // cannot be compiled into this headless net9.0 project. ZoneOPowerTests parses it as source
        // for the same reason; this is the same parse, narrowed to the OPower column.
        private static Dictionary<int, double> DerivedOPower()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "NGUAdvisor", "Managers",
                "ZoneStatHelper.cs"));
            int start = src.IndexOf("Defaults = new Dictionary<int, ZoneStats>", StringComparison.Ordinal);
            Assert.True(start > 0, "could not find the Defaults table in ZoneStatHelper.cs");
            src = src.Substring(start);

            var rows = new Dictionary<int, double>();
            foreach (Match m in Regex.Matches(src, @"(?<zone>\d+),\s*new ZoneStats\s*\{(?<body>[^}]*)\}"))
            {
                var body = m.Groups["body"].Value;
                var v = Regex.Match(body,
                    @"(?m)^\s*OPower\s*=\s*(?<v>-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
                if (!v.Success)
                    continue;
                rows[int.Parse(m.Groups["zone"].Value, CultureInfo.InvariantCulture)] =
                    double.Parse(v.Groups["v"].Value, CultureInfo.InvariantCulture);
            }
            Assert.True(rows.Count >= 30, "parsed only " + rows.Count + " OPower rows");
            return rows;
        }

        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }
    }
}
