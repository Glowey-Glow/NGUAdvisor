using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // =============================================================================================
    // THE CHAPTER MISS, DERIVED — and why SilenceSpec did NOT get a Chapter field.
    // =============================================================================================
    //
    // `db2cf88` closed audit/56 §8 with "the coherent fix for the 71 is a CHAPTER AXIS ON
    // SilenceSpec, not 14 entries", and deferred it. This file is that item, worked — and the
    // answer is that a chapter axis is the WRONG INSTRUMENT. The measurement is below; the short
    // version is three findings, each of which independently rules it out.
    //
    // ---------------------------------------------------------------------------------------------
    // FINDING 1 — ⚠ THE 71 ARE NOT ALL CHAPTER MISSES. THEY ARE 63 + 8.
    //
    // ObjectiveParityTests' fixture comment said "71 of the 912 slot-queries, ALL OF THEM chapter
    // misses ... not a hole in the ledger", and SilenceLedgerScopeTests' header repeated it.
    // MEASURED, THAT IS FALSE. 63 are chapter misses. The other 8 are `ngu-energy 2` (Respawn) on
    // Normal at every chapter 1..8 — a slot with ZERO rows on the Normal track at ANY chapter and
    // NO ledger entry. That is a genuine hole, and it is exactly what the fixture's own parenthesis
    // records without drawing the conclusion: "(Was 70; the Respawn slot joined them when
    // [OPERATOR] removed its row 2026-08-07.)" The count was updated; the classification was not.
    //
    // A chapter axis would not have touched those 8. There is no chapter at which the guide levels
    // Respawn on Normal, so there is no chapter for the axis to carry.
    //
    // ---------------------------------------------------------------------------------------------
    // FINDING 2 — ⚠ 21 CHAPTER MISSES ARE ALREADY LEDGERED, AND THE LEDGER IS WRONG AT ALL 21.
    //
    // `db2cf88` pinned that the Evil-NGU catch-all deliberately over-covers levelled slots and
    // called it "harmless, because FindSilence is never reached where a row exists". THAT IS TRUE
    // ONLY AT ChapterAny — which is the only place its test looked. On a chapter-KEYED query,
    // FindSilence IS reached for ngu-energy 7, ngu-energy 8 and ngu-magic 5 on Evil at all seven
    // non-ch.5 chapters, and answers with amendment 18 §1's "every Evil NGU is a rate row, both
    // pools, all ids — NO LEVEL EXISTS" — about three slots the table levels at ch.5
    // (ObjectiveTable.cs:444, :450, and the Evil M5 row). 21 coordinates where the recorded reason
    // contradicts the table.
    //
    // ⚠ THIS IS `db2cf88`'s OWN STATED WORST CASE, ALREADY HAPPENING: "Entries would be LIVE data,
    // not dead — the worse failure, since the reason would be believed."
    //
    // A Chapter field on SilenceSpec could not fix these either. They are already answered; a new
    // field would only be consulted if someone also rewrote the catch-alls to enumerate chapters,
    // which is the duplication in its most expensive form.
    //
    // ---------------------------------------------------------------------------------------------
    // FINDING 3 — THE FACT IS ALREADY IN THE TABLE. RECORDING IT WOULD BE COPYING IT.
    //
    // A chapter miss is the Chapter field of the rows that failed to match. ObjectiveTable holds it;
    // ObjectiveReader can read it; and the total population is 84 coordinates (63 unledgered + 21
    // ledgered). Writing 84 chapter facts into a provenance ledger creates 84 records whose only
    // source of truth is the table — the drift class this project keeps paying for. Derived, the
    // count of recorded facts is ZERO and it cannot go stale.
    //
    // And "what does ChapterAny mean for a SILENCE?" has no good answer, which is the design smell
    // that settles it. On a row it means STANDING. On a silence it would have to mean either "silent
    // at every chapter" (a claim the table can refute, as it does for all 84) or "this reason applies
    // whenever the slot is silent" (its current, implicit meaning). Both readings are defensible, so
    // the field would be ambiguous the day it was added.
    //
    // ---------------------------------------------------------------------------------------------
    // WHAT WAS BUILT INSTEAD: two derived fields and a reason suffix, in ObjectiveReader.
    //
    //   SlotAnswer.SpokenAtChapters      — chapters with ANY row (the negation of Slot()'s question)
    //   LevelAnswer.LevelledAtChapters   — chapters with a LEVEL row (the negation of LevelSlot()'s)
    //
    // ⚠ THEY ARE ORTHOGONAL TO SilenceKnown, NOT A REFINEMENT OF IT. Finding 2's 21 coordinates are
    // ledgered AND chapter misses at once. A fifth SilenceClass would have had to pick one of the
    // two facts; both are true, so they are two fields.
    //
    // ⚠ AND NOTHING'S VERDICT MOVED. `The_disposition_matrix_is_byte_identical_to_the_pre_change_run`
    // pins all 1026 (system, id, track, chapter) coordinates — Availability, HasLevel, SilenceKnown,
    // SilenceClass, row counts and the ObjectiveParity verdict — as a SHA-256 taken on the tree
    // BEFORE ObjectiveReader was touched. Only reason TEXT changed, which the hash deliberately
    // excludes.
    //
    // ---------------------------------------------------------------------------------------------
    // ⚠ WHAT IS STILL OPEN, AND IT IS NOT THIS FILE'S TO CLOSE: the 8. `ngu-energy 2 Normal` is
    // silent at ChapterAny with no ledger entry — the ONE slot of the 95 the ledger does not name
    // (see The_ledger_names_94_of_the_95_and_the_ninety_fifth_is_respawn). Giving it an entry means
    // asserting WHY the guide is silent about Respawn on Normal, which is guide provenance, and
    // [OPERATOR] removed its row deliberately on 2026-08-07. Recorded and pinned here so it cannot
    // drift; adjudicating it remains the ledger owner's call.
    //
    // ⚠ NOT VALIDATED IN GAME. The objective layer has no live consumer (audit/56 §0); this file and
    // the ObjectiveReader change it covers reach no game field.
    // =============================================================================================
    public class ChapterMissDerivationTests
    {
        private static readonly TargetPass.Track[] Tracks =
        {
            TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
        };

        private static string ClassFor(string system)
        {
            if (system == TargetPass.SysNguEnergy || system == TargetPass.SysNguMagic) return "NGUBP";
            if (system == TargetPass.SysAt) return "AdvancedTrainingBP";
            if (system == TargetPass.SysAugments) return "AugmentBP";
            if (system == TargetPass.SysWandoos) return "WandoosBP";
            return "TimeMachineBP";
        }

        private struct Coord
        {
            public string System;
            public int Id;
            public TargetPass.Track Track;
            public int Chapter;
            public override string ToString()
            {
                return System + " " + Id.ToString(CultureInfo.InvariantCulture) + " " + Track +
                       " ch" + Chapter.ToString(CultureInfo.InvariantCulture);
            }
        }

        // Every chapter-KEYED coordinate: 38 slots x 3 tracks x 8 chapters = 912. ChapterAny is
        // deliberately excluded — it is not a chapter (ObjectiveParity C1) and cannot miss one.
        private static IEnumerable<Coord> Keyed()
        {
            foreach (var track in Tracks)
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
                yield return new Coord
                {
                    System = system, Id = id, Track = track, Chapter = chapter,
                };
        }

        // -----------------------------------------------------------------------------------------
        // THE VERDICT-INVARIANCE PROOF. This is the load-bearing test in the file.
        // -----------------------------------------------------------------------------------------

        // ⚠ THIS HASH WAS TAKEN ON THE TREE AT 8e5d81b, BEFORE ObjectiveReader WAS MODIFIED, and it
        // still holds after. It covers all 1026 coordinates — 38 slots x 3 tracks x (ChapterAny +
        // chapters 1..8) — and every field on which a decision could turn: Availability, the row and
        // conflict counts, SilenceKnown, SilenceClass, SilenceCite, HasRows, HasLevelRow, HasLevel,
        // the level/other row counts, and ObjectiveParity's verdict plus its four report counters.
        //
        // REASON TEXT IS DELIBERATELY EXCLUDED, because reason text is the only thing this change was
        // allowed to move. If a future edit changes a DISPOSITION, this fails and no amount of
        // careful prose will hide it. If it fails, the fix is not to re-take the hash — it is to
        // find out which coordinate moved and why.
        private const string BaselineDisposition =
            "589095CDCD79097335C3BDA2DE026F9ED1E647FDD1164364216AD6D3FB92EC1D";

        [Fact]
        public void The_disposition_matrix_is_byte_identical_to_the_pre_change_run()
        {
            var sb = new StringBuilder();
            var chapters = new List<int> { ObjectiveTable.ChapterAny };
            chapters.AddRange(Enumerable.Range(1, 8));
            int coords = 0;

            foreach (var track in Tracks)
            foreach (var chapter in chapters)
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var slot = ObjectiveReader.Slot(chapter, track, system, id);
                var lvl = ObjectiveReader.LevelSlot(chapter, track, system, id);

                bool energy = system != TargetPass.SysNguMagic &&
                              system != TargetPass.SysTmGoldMulti;
                var r = ObjectiveParity.Compare(chapter, true, track, track, energy,
                    new List<ObjectiveParity.ProfileLane>
                    {
                        new ObjectiveParity.ProfileLane
                        {
                            ClassName = ClassFor(system), Label = "L", Index = id,
                            EnergyPool = energy,
                        },
                    });
                // ChapterAny is HELD by the comparator (C1), so there is no row to read there.
                var prow = r.Held || r.Rows == null
                    ? default(ObjectiveParity.Row)
                    : r.Rows.SingleOrDefault(x => x.Label == "L");

                sb.Append(system).Append('|').Append(id).Append('|').Append(track).Append('|')
                  .Append(chapter).Append('|')
                  .Append(slot.Availability).Append('|')
                  .Append(slot.Rows == null ? -1 : slot.Rows.Count).Append('|')
                  .Append(slot.Conflicts == null ? -1 : slot.Conflicts.Count).Append('|')
                  .Append(slot.SilenceKnown).Append('|').Append(slot.SilenceClass).Append('|')
                  .Append(slot.SilenceCite ?? "-").Append('|')
                  .Append(slot.HasRows).Append('|').Append(slot.HasLevelRow).Append('|')
                  .Append(lvl.HasLevel).Append('|')
                  .Append(lvl.LevelRows == null ? -1 : lvl.LevelRows.Count).Append('|')
                  .Append(lvl.OtherRows == null ? -1 : lvl.OtherRows.Count).Append('|')
                  .Append(lvl.SilenceKnown).Append('|').Append(lvl.SilenceClass).Append('|')
                  .Append(prow.Verdict).Append('|')
                  .Append(r.NoOpinion).Append('|').Append(r.Adds).Append('|')
                  .Append(r.Agreements).Append('|').Append(r.CampaignScoped).Append('|')
                  .Append(r.Unnameable).Append('\n');
                coords++;
            }

            // Guard the guard: a hash over an empty sweep would pass forever.
            Assert.Equal(38 * 3 * 9, coords);
            Assert.Equal(1026, coords);

            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "");

            Assert.Equal(BaselineDisposition, hash);
        }

        // -----------------------------------------------------------------------------------------
        // FINDING 1 — the 71 partitioned.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_seventy_one_unledgered_coordinates_are_sixty_three_misses_and_eight_holes()
        {
            var misses = new List<Coord>();
            var holes = new List<Coord>();
            int levelled = 0, ledgered = 0;

            foreach (var c in Keyed())
            {
                var a = ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id);
                if (a.HasLevel) { levelled++; continue; }
                if (a.SilenceKnown) { ledgered++; continue; }

                if (a.IsChapterMiss) misses.Add(c); else holes.Add(c);
            }

            Assert.Equal(38 * 3 * 8, levelled + ledgered + misses.Count + holes.Count);

            // ⚠ THE FIXTURE ObjectiveParityTests PINS IS 71, AND IT HAS NOT MOVED. What moved is the
            // claim ABOUT it: "all of them chapter misses, not a hole in the ledger" was false.
            Assert.Equal(71, misses.Count + holes.Count);
            Assert.Equal(63, misses.Count);
            Assert.Equal(8, holes.Count);
        }

        [Fact]
        public void The_eight_holes_are_respawn_on_normal_at_every_chapter_and_nothing_else()
        {
            var holes = new List<Coord>();
            foreach (var c in Keyed())
            {
                var a = ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id);
                if (a.HasLevel || a.SilenceKnown || a.IsChapterMiss)
                    continue;
                holes.Add(c);
            }

            Assert.Equal(8, holes.Count);
            Assert.All(holes, h =>
            {
                Assert.Equal(TargetPass.SysNguEnergy, h.System);
                Assert.Equal(2, h.Id);                       // Respawn
                Assert.Equal(TargetPass.Track.Normal, h.Track);
            });
            Assert.Equal(Enumerable.Range(1, 8), holes.Select(h => h.Chapter).OrderBy(x => x));

            // ⚠ AND IT IS A HOLE IN THE FULLEST SENSE: not "no level here" but NO ROW ANYWHERE on
            // this track, at any chapter. There is no chapter for a chapter axis to carry, which is
            // why the deferred fix would not have reached these 8.
            Assert.Empty(ObjectiveReader.ChaptersSpeaking(
                TargetPass.Track.Normal, TargetPass.SysNguEnergy, 2));
            Assert.Empty(ObjectiveReader.ChaptersWithLevel(
                TargetPass.Track.Normal, TargetPass.SysNguEnergy, 2));
        }

        // ⚠ THE CLAIM THE 19/95 CENSUS TEST MAKES IN ITS OWN NAME IS OFF BY ONE, and it never
        // checked: it asserts only that every silence carries a REASON, which the unledgered
        // fallback text satisfies. The ledger names 94 of the 95. This is the ninety-fifth.
        [Fact]
        public void The_ledger_names_94_of_the_95_and_the_ninety_fifth_is_respawn()
        {
            var unnamed = new List<string>();
            int silent = 0;

            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var track in Tracks)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var a = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny, track, system, id);
                if (a.HasLevel)
                    continue;
                silent++;
                if (!a.SilenceKnown)
                    unnamed.Add(system + "/" + id + "/" + track);
            }

            Assert.Equal(95, silent);
            Assert.Equal(94, silent - unnamed.Count);
            Assert.Equal(new[] { TargetPass.SysNguEnergy + "/2/" + TargetPass.Track.Normal },
                unnamed);

            // ⚠ AND IT STILL SURFACES. An unledgered silence is not a defaulted one — no 0, no -1,
            // no long.MaxValue. What it lacks is PROVENANCE, which is the open item, not a bug.
            var respawn = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                TargetPass.Track.Normal, TargetPass.SysNguEnergy, 2);
            Assert.False(respawn.HasLevel);
            Assert.False(respawn.SilenceKnown);
            Assert.False(respawn.IsChapterMiss);
            Assert.Contains("no ledger entry", respawn.Reason);
            Assert.Contains("surfaced, never defaulted", respawn.Reason);
            Assert.Empty(respawn.LevelRows);
        }

        // -----------------------------------------------------------------------------------------
        // FINDING 2 — the 21 the ledger already answers, wrongly.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Twenty_one_chapter_misses_are_ledgered_and_the_recorded_reason_contradicts_it()
        {
            var ledgeredMisses = new List<Coord>();
            foreach (var c in Keyed())
            {
                var a = ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id);
                if (a.HasLevel || !a.SilenceKnown || !a.IsChapterMiss)
                    continue;
                ledgeredMisses.Add(c);

                // The ledger's own sentence says no level exists; the table says otherwise, at a
                // chapter this query did not ask about. BOTH now appear, in that order.
                Assert.Contains("no level exists", a.Reason);
                Assert.Contains("CHAPTER MISS", a.Reason);
                Assert.Contains("DOES supply a stopping level", a.Reason);
                Assert.NotEmpty(a.LevelledAtChapters);
            }

            Assert.Equal(21, ledgeredMisses.Count);

            // Three slots x the seven non-ch.5 chapters. All Evil, all under amendment 18 §1's
            // catch-all (TargetPass.cs:614-628, :623-629).
            Assert.All(ledgeredMisses, m => Assert.Equal(TargetPass.Track.Evil, m.Track));
            Assert.Equal(new[] { "ngu-energy/7", "ngu-energy/8", "ngu-magic/5" },
                ledgeredMisses.Select(m => m.System + "/" + m.Id).Distinct().OrderBy(x => x));
            Assert.Equal(new[] { 1, 2, 3, 4, 6, 7, 8 },
                ledgeredMisses.Select(m => m.Chapter).Distinct().OrderBy(x => x));

            // ⚠ AND THE CH.5 COORDINATE IS NOT AMONG THEM — that is where the row lives, so
            // FindSilence is never reached. `db2cf88`'s "harmless" claim is true THERE and only
            // there; it does not generalise to the chapter-keyed grid, which is the correction.
            foreach (var slot in new[] { 7, 8 })
                Assert.True(ObjectiveReader.LevelSlot(5, TargetPass.Track.Evil,
                    TargetPass.SysNguEnergy, slot).HasLevel);
            Assert.True(ObjectiveReader.LevelSlot(5, TargetPass.Track.Evil,
                TargetPass.SysNguMagic, 5).HasLevel);
        }

        [Fact]
        public void The_corpus_carries_eighty_four_chapter_misses_across_both_ledger_states()
        {
            int ledgered = 0, unledgered = 0;
            foreach (var c in Keyed())
            {
                var a = ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id);
                if (!a.IsChapterMiss)
                    continue;
                if (a.SilenceKnown) ledgered++; else unledgered++;
            }

            Assert.Equal(84, ledgered + unledgered);
            Assert.Equal(21, ledgered);
            Assert.Equal(63, unledgered);

            // ⚠ THE NUMBER THAT SETTLED THE DESIGN. A Chapter field on SilenceSpec would have had to
            // carry 84 hand-written chapter facts sourced entirely from ObjectiveTable, and would
            // still have missed the 8 genuine holes. Derived, the count of recorded facts is zero.
        }

        // -----------------------------------------------------------------------------------------
        // THE POINT OF THE EXERCISE — the two facts no longer share one sentence.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void A_chapter_miss_and_a_true_silence_no_longer_render_the_same_sentence()
        {
            // Two coordinates that differ in exactly one respect: the guide LEVELS ngu-energy 0 on
            // Normal (at ch.3 and ch.4) and says nothing whatever about ngu-energy 2 on Normal.
            var miss = ObjectiveReader.LevelSlot(1, TargetPass.Track.Normal,
                TargetPass.SysNguEnergy, 0);
            var real = ObjectiveReader.LevelSlot(1, TargetPass.Track.Normal,
                TargetPass.SysNguEnergy, 2);

            // ⚠ BEFORE THE DERIVATION THESE WERE THE SAME SENTENCE modulo the id. Both are still
            // unledgered, both still answer "no level", both still refuse to default — the verdict
            // did not move. Only the reason gained the fact that separates them.
            Assert.False(miss.HasLevel);
            Assert.False(real.HasLevel);
            Assert.False(miss.SilenceKnown);
            Assert.False(real.SilenceKnown);
            Assert.Contains("no level for (ngu-energy, 0, Normal) and no ledger entry — surfaced, " +
                            "never defaulted (23 §7)", miss.Reason);
            Assert.Contains("no level for (ngu-energy, 2, Normal) and no ledger entry — surfaced, " +
                            "never defaulted (23 §7)", real.Reason);

            // AND NOW THEY DIVERGE.
            Assert.True(miss.IsChapterMiss);
            Assert.False(real.IsChapterMiss);
            Assert.Equal(new[] { 3, 4 }, miss.LevelledAtChapters);
            Assert.Empty(real.LevelledAtChapters);
            Assert.Contains("CHAPTER MISS, not a silence about the slot", miss.Reason);
            Assert.Contains("at chapter(s) 3, 4", miss.Reason);
            Assert.Contains("just not at the queried chapter (1)", miss.Reason);
            Assert.DoesNotContain("CHAPTER MISS", real.Reason);

            // ⚠ THE SILENCE THAT IS REALLY A SILENCE READS EXACTLY AS IT ALWAYS DID. A derivation
            // that annotated everything would be no more informative than one that annotated
            // nothing.
            Assert.Equal("no level for (ngu-energy, 2, Normal) and no ledger entry — surfaced, " +
                         "never defaulted (23 §7)", real.Reason);

            // Slot() answers the OTHER question and gets the other list: ANY row, not a level row.
            // ngu-energy 0 has a non-level row at ch.2, so Slot's list is wider than LevelSlot's.
            // Two questions, two negations — not one fact rendered twice.
            var slotMiss = ObjectiveReader.Slot(1, TargetPass.Track.Normal,
                TargetPass.SysNguEnergy, 0);
            Assert.Equal(new[] { 2, 3, 4 }, slotMiss.SpokenAtChapters);
            Assert.True(slotMiss.IsChapterMiss);
            Assert.Contains("DOES speak about this slot", slotMiss.Reason);
        }

        // -----------------------------------------------------------------------------------------
        // THE INVARIANTS THE DERIVATION RESTS ON — asserted, not guarded.
        // -----------------------------------------------------------------------------------------

        // ⚠ WHY ObjectiveReader HAS NO `if (chapter == ChapterAny)` GUARD. A chapter-scoped row
        // MATCHES a ChapterAny query (ObjectiveTable.ChapterMatches), so reaching the silence path
        // at ChapterAny means the slot has no matching row at ANY chapter — which makes the derived
        // list provably empty. The invariant is pinned here instead of defended in code, so an
        // unreachable branch is not carried forever.
        [Fact]
        public void No_chapterany_query_can_ever_be_a_chapter_miss()
        {
            foreach (var track in Tracks)
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var slot = ObjectiveReader.Slot(ObjectiveTable.ChapterAny, track, system, id);
                var lvl = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny, track, system, id);

                Assert.False(slot.IsChapterMiss,
                    system + "/" + id + "/" + track + ": ChapterAny produced a chapter miss");
                Assert.False(lvl.IsChapterMiss,
                    system + "/" + id + "/" + track + ": ChapterAny produced a chapter miss");
                Assert.Empty(slot.SpokenAtChapters);
                Assert.Empty(lvl.LevelledAtChapters);
            }
        }

        [Fact]
        public void Every_derived_chapter_list_is_sorted_distinct_real_and_excludes_the_query()
        {
            int annotated = 0;
            foreach (var c in Keyed())
            {
                var slot = ObjectiveReader.Slot(c.Chapter, c.Track, c.System, c.Id);
                var lvl = ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id);

                foreach (var list in new[] { slot.SpokenAtChapters, lvl.LevelledAtChapters })
                {
                    Assert.NotNull(list);
                    if (list.Length == 0)
                        continue;
                    annotated++;

                    // ⚠ ChapterAny IS NOT A CHAPTER and must never appear in a list that claims the
                    // guide speaks "at" one. A standing row matches every query, so it can never be
                    // missed; listing it would be a false statement about where the guide speaks.
                    Assert.DoesNotContain(ObjectiveTable.ChapterAny, list);
                    Assert.Equal(list.Distinct().OrderBy(x => x), list);
                    Assert.All(list, ch => Assert.InRange(ch, 1, 8));

                    // ⚠ THE QUERIED CHAPTER CAN NEVER BE IN ITS OWN MISS LIST. If it were, the row
                    // would have matched and this would not be a silence at all.
                    Assert.DoesNotContain(c.Chapter, list);
                }
            }
            Assert.True(annotated > 0, "vacuous: no coordinate produced a derived chapter list");
        }

        // The derivation must agree with the reader it describes: if it says the guide levels the
        // slot at ch.N, then querying ch.N must actually answer HasLevel. Two independent walks of
        // the same table, cross-checked at every coordinate.
        [Fact]
        public void The_derived_chapters_agree_with_the_reader_at_every_chapter_they_name()
        {
            int checks = 0;
            foreach (var track in Tracks)
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var levelled = ObjectiveReader.ChaptersWithLevel(track, system, id);
                var spoken = ObjectiveReader.ChaptersSpeaking(track, system, id);

                // A level row is a row: the level list is a subset of the speaking list.
                Assert.Empty(levelled.Except(spoken));

                for (int ch = 1; ch <= 8; ch++)
                {
                    var lvl = ObjectiveReader.LevelSlot(ch, track, system, id);
                    var slot = ObjectiveReader.Slot(ch, track, system, id);

                    // ⚠ ONE-WAY, DELIBERATELY. A named chapter must answer; the converse fails on
                    // CONFLICT coordinates, where Slot() returns Conflict and suppresses the rows
                    // it would otherwise have carried. That is the conflict outranking a row set
                    // (ObjectiveReader.Slot), not a disagreement.
                    if (levelled.Contains(ch))
                        Assert.True(lvl.HasLevel, "derived says levelled at ch" + ch +
                                                  " but the reader disagrees: " + system + "/" +
                                                  id + "/" + track);
                    if (spoken.Contains(ch) &&
                        slot.Availability != ObjectiveReader.Availability.Conflict)
                        Assert.True(slot.HasRows, "derived says spoken at ch" + ch +
                                                  " but the reader disagrees: " + system + "/" +
                                                  id + "/" + track);
                    checks++;
                }
            }
            Assert.Equal(38 * 3 * 8, checks);
        }

        // ⚠ A SILENCE IS NOT A ZERO, AND THE NOTE MUST NOT MAKE IT LOOK LIKE ONE. The suffix's only
        // integers are chapter indices; ObjectiveParityTests forbids the substring shape "level <n>"
        // on every no-opinion row precisely because 0 is the game's UNSET SENTINEL ([DECOMP]
        // AllNGUController.cs:1311-1314) and a rendered number on a silence funds forever. Swept over
        // every coordinate rather than argued.
        [Fact]
        public void The_derived_note_never_renders_a_number_that_could_be_read_as_a_level()
        {
            foreach (var c in Keyed())
            foreach (var reason in new[]
                     {
                         ObjectiveReader.Slot(c.Chapter, c.Track, c.System, c.Id).Reason,
                         ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id).Reason,
                     })
            {
                if (string.IsNullOrEmpty(reason))
                    continue;
                var note = reason.IndexOf("CHAPTER MISS, not a silence", StringComparison.Ordinal);
                if (note < 0)
                    continue;

                var tail = reason.Substring(note);
                Assert.False(Regex.IsMatch(tail, @"level \d"), c + ": " + tail);
                Assert.DoesNotContain("[terminal]", tail);
                Assert.DoesNotContain("[PRECONDITION", tail);

                // The queried chapter is parenthesised so the note cannot satisfy a
                // `Contains("chapter " + n)` check that the base sentence is supposed to answer.
                Assert.DoesNotMatch(new Regex(@"queried chapter \d"), tail);
            }
        }

        // -----------------------------------------------------------------------------------------
        // THE DESIGN PIN — SilenceSpec did NOT gain a chapter axis, and must not gain one quietly.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void SilenceSpec_has_no_chapter_axis_and_FindSilence_still_takes_no_chapter()
        {
            var fields = typeof(TargetPass.SilenceSpec).GetFields()
                .Select(f => f.Name).ToArray();

            Assert.Equal(new[] { "Cite", "Class", "Ids", "Reason", "System", "Track" },
                fields.OrderBy(n => n, StringComparer.Ordinal));

            Assert.DoesNotContain(fields, n =>
                n.IndexOf("chapter", StringComparison.OrdinalIgnoreCase) >= 0);

            // ⚠ IF THIS FAILS, A CHAPTER AXIS WAS ADDED TO THE LEDGER. Read this file's header
            // first: the axis was measured and rejected on three independent grounds — it misses the
            // 8 genuine holes entirely, it cannot reach the 21 already-ledgered misses, and it would
            // hand-copy 84 facts ObjectiveTable already holds. If it is being added anyway, the
            // 63/8/21 counts above are the evidence that has to be answered.
            var find = typeof(TargetPass).GetMethod("FindSilence");
            Assert.NotNull(find);
            Assert.DoesNotContain(find.GetParameters(), p =>
                p.Name.IndexOf("chapter", StringComparison.OrdinalIgnoreCase) >= 0);

            // The ledger itself is untouched by this work: 15 entries, same order, same classes.
            Assert.Equal(15, TargetPass.SilenceLedger.Length);
            Assert.All(TargetPass.SilenceLedger, e =>
            {
                Assert.False(string.IsNullOrEmpty(e.Reason));
                Assert.False(string.IsNullOrEmpty(e.Cite));
            });
        }

        // ⚠ THE LEDGER STILL SUPPLIES THE REASON AND THE DERIVATION NEVER REPLACES IT. Every
        // ledgered silence surfaces its recorded prose and cite verbatim; the note is APPENDED. A
        // derivation that overwrote provenance would have traded one lost fact for another.
        [Fact]
        public void Every_ledgered_silence_still_surfaces_its_recorded_reason_verbatim()
        {
            int checked_ = 0;
            foreach (var c in Keyed())
            {
                var a = ObjectiveReader.LevelSlot(c.Chapter, c.Track, c.System, c.Id);
                if (a.HasLevel || !a.SilenceKnown)
                    continue;

                TargetPass.SilenceSpec spec;
                Assert.True(TargetPass.FindSilence(c.System, c.Id, c.Track, out spec));
                Assert.StartsWith("no level (" + spec.Class + "): " + spec.Reason +
                                  " [" + spec.Cite + "]", a.Reason, StringComparison.Ordinal);
                checked_++;
            }
            Assert.Equal(773, checked_);
        }
    }
}
