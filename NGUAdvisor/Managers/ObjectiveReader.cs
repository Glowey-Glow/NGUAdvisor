using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // THE OBJECTIVE LAYER'S READER — the "consumer" half of 37 §S5 B6, which records that
    // TargetPass.GuideRows has "no reader outside tests".
    //
    // ⚠ THIS READER DECIDES NOTHING, WRITES NOTHING, AND TOUCHES NO GAME FIELD. It answers "what does
    // the guide say for (chapter, track)" and returns data. It performs no allocation, holds no state,
    // reaches nothing Unity, and has no caller on any live path. Routing a row to a disposition is
    // TargetPass's job and stays there — Slot() delegates to TargetPass.Route rather than
    // reimplementing it, so there is exactly one place where a terminality becomes a decision.
    //
    // ⚠ A SILENCE MUST BE DISTINGUISHABLE FROM A ZERO, and this is where that is enforced at the query
    // boundary. In the game, target 0 is the UNSET SENTINEL: reachedTarget returns false at 0
    // ([DECOMP] AllNGUController.cs:1311-1314), so a lane written to 0 reads unmet and FUNDS FOREVER;
    // -1 is the never-fund marker and reads MET. A slot the guide does not fill therefore cannot be
    // rendered as 0, as -1, or as long.MaxValue. Availability.Silent is a SURFACED STATE carrying the
    // silence ledger's recorded reason, and no field on a silent answer holds a number.
    //
    // The shape is TargetPass's, deliberately: Availability mirrors Disposition, HasValue mirrors
    // "non-zero exactly when WriteTarget", and every non-row answer carries a non-null Reason.
    public static class ObjectiveReader
    {
        public enum Availability
        {
            // Fail-closed default. A default(SlotAnswer) is unevaluated, never "no target".
            Unevaluated = 0,
            // The guide fills this slot for this (chapter, track). Rows is non-empty.
            Rows,
            // The guide does not fill it. Carries the silence ledger's class and reason. NOT a zero.
            Silent,
            // 23 records two irreconcilable readings of the same sentence and adjudicates neither.
            // No numeric value is emitted on either reading (23 §2.3's M0/M1).
            Conflict
        }

        // One slot's answer: (chapter, track, system, id).
        public struct SlotAnswer
        {
            public Availability Availability;

            // The guide's rows for this slot, in table order. Empty on every non-Rows answer —
            // never null, so a caller cannot NRE its way into treating a silence as an absence.
            public IList<ObjectiveTable.LaneRow> Rows;

            // Present only when Availability == Silent.
            public bool SilenceKnown;                  // false == not in the ledger; STILL a silence
            public TargetPass.SilenceClass SilenceClass;
            public string SilenceCite;

            // ⚠ DERIVED FROM THE TABLE, NOT FROM THE LEDGER — see the ChaptersSpeaking block below.
            // The chapters at which the guide DOES have a row for this (system, id, track). Non-empty
            // exactly when this answer is a CHAPTER MISS: the guide is not silent about the slot, it
            // is silent about the slot AT THE QUERIED CHAPTER. Empty (never null) on every other
            // answer, including a genuine every-chapter silence.
            public int[] SpokenAtChapters;

            public bool IsChapterMiss
            {
                get
                {
                    return Availability == Availability.Silent &&
                           SpokenAtChapters != null && SpokenAtChapters.Length > 0;
                }
            }

            // Present only when Availability == Conflict.
            public IList<ObjectiveTable.ConflictRow> Conflicts;

            // Non-null on every answer except Rows — the surfaced reason, ready to log verbatim.
            public string Reason;

            // ⚠ THERE IS DELIBERATELY NO `long Value` ON THIS TYPE. A reader that could return a
            // number would be one edit away from returning 0 for a silence. A caller that wants a
            // writable target must route a row through TargetPass.Route and read RowRoute.
            public bool HasRows
            {
                get { return Availability == Availability.Rows && Rows != null && Rows.Count > 0; }
            }

            // ⚠ ROWS AND A LEVEL ARE DIFFERENT QUESTIONS, and conflating them is how a silence gets
            // filled by accident. 23 §7's ledger is of slots the guide does not fill WITH A LEVEL —
            // its wording is "no level", "SILENT on a level", never "no mention". The guide says
            // plenty about augments (a selector), about Evil Adventure a (a time and a rate) and
            // about Wandoos (an OS switch); none of it is a level, and all of those slots are in
            // the ledger. Availability answers "does the guide say ANYTHING here"; this answers
            // "does it supply a stopping level", which is the only question Pass 3 can consume.
            public bool HasLevelRow
            {
                get
                {
                    if (Rows == null)
                        return false;
                    for (int i = 0; i < Rows.Count; i++)
                        if (Rows[i].Kind == TargetPass.RowKind.Level)
                            return true;
                    return false;
                }
            }
        }

        private static readonly ObjectiveTable.LaneRow[] NoRows = new ObjectiveTable.LaneRow[0];
        private static readonly ObjectiveTable.ConflictRow[] NoConflicts =
            new ObjectiveTable.ConflictRow[0];

        // ---- the whole-view queries (T2a) --------------------------------------------------------
        // Query by (chapter, track). ObjectiveTable.ChapterAny returns every chapter.

        public static IList<ObjectiveTable.LaneRow> Lanes(int chapter, TargetPass.Track track)
        {
            return ObjectiveTable.LanesFor(chapter, track);
        }

        public static IList<ObjectiveZones.ZoneRow> Zones(int chapter, TargetPass.Track band)
        {
            return ObjectiveZones.ZonesFor(chapter, band);
        }

        public static IList<ObjectiveTable.ConflictRow> Conflicts(int chapter, TargetPass.Track track)
        {
            return ObjectiveTable.ConflictsFor(chapter, track);
        }

        // ---- THE CHAPTER MISS, DERIVED — never recorded --------------------------------------------
        //
        // ⚠ TWO DIFFERENT FACTS SHARED ONE SENTENCE, AND ONLY ONE OF THEM IS A SILENCE.
        //
        // TargetPass.FindSilence takes (system, id, track) and SilenceSpec has NO Chapter field, so
        // the silence ledger is CHAPTER-AGNOSTIC BY CONSTRUCTION. ObjectiveTable.ChapterMatches
        // (:1007-1011) is EXACT. A slot the guide levels at ch.3 therefore has no matching row at a
        // ch.5 query, falls to the silence path, and used to render the SAME sentence as a slot the
        // guide never mentions at all:
        //
        //     "no level for (ngu-energy, 0, Normal) and no ledger entry — surfaced, never defaulted"
        //     "no level for (ngu-energy, 2, Normal) and no ledger entry — surfaced, never defaulted"
        //
        // The second is true. THE FIRST IS FALSE AS AN ACCOUNT OF THE GUIDE — the guide does level
        // ngu-energy 0 on Normal, at ch.3 and ch.4. "The guide is silent about this slot" and "the
        // guide speaks about this slot, at a different chapter" are different facts, and the ledger
        // cannot state the second: it has no chapter to state it at.
        //
        // ⚠ AND THE FIX IS DERIVATION, NOT A CHAPTER AXIS ON SilenceSpec. A chapter miss is a fact
        // ObjectiveTable ALREADY HOLDS — it is the Chapter field of the very rows that failed to
        // match. Copying it into the ledger by hand would create recorded facts whose only source of
        // truth is the table, which is the drift class this project keeps paying for. Measured over
        // the corpus (38 slots x 3 tracks x 8 chapters = 912 chapter-keyed queries) that hand-written
        // duplication would be 84 coordinates; computed here it is zero. The ledger keeps its one
        // job — WHY a chapter-agnostic silence exists — and this computes the other fact fresh on
        // every call, so it cannot go stale.
        //
        // ⚠ IT ALSO REACHES THE 21 A LEDGER FIELD COULD NOT. The Evil-NGU catch-all
        // (TargetPass.cs:614-628) answers ngu-energy 7/8 and ngu-magic 5 on Evil at all seven
        // non-ch.5 chapters with "every Evil NGU is a rate row ... no level exists" — while the
        // table carries Evil LEVEL rows for all three at ch.5. Those silences are LEDGERED, so no
        // amount of new ledger data would have been consulted for them; only a derived fact
        // appended to the ledger's own reason can correct the record. It is appended, not
        // substituted: the ledger's recorded provenance still surfaces verbatim.

        private static readonly int[] NoChapters = new int[0];

        // The chapters at which the guide has ANY row for this slot — the negation of Slot()'s own
        // question, computed over the same predicates LanesFor uses.
        //
        // ⚠ ChapterAny ROWS ARE EXCLUDED, and not as a convenience: a ChapterAny row is STANDING and
        // matches every chapter query (ChapterMatches), so it can never be missed. Listing one here
        // would claim the guide speaks "at chapter 0", which is not a chapter.
        public static int[] ChaptersSpeaking(TargetPass.Track track, string system, int id)
        {
            return ChaptersMatching(track, system, id, false);
        }

        // The chapters at which the guide supplies a LEVEL row — the negation of LevelSlot()'s
        // question. Same exclusion, same reason.
        public static int[] ChaptersWithLevel(TargetPass.Track track, string system, int id)
        {
            return ChaptersMatching(track, system, id, true);
        }

        private static int[] ChaptersMatching(TargetPass.Track track, string system, int id,
            bool levelOnly)
        {
            if (string.IsNullOrEmpty(system))
                return NoChapters;

            List<int> found = null;
            for (int i = 0; i < ObjectiveTable.LaneRows.Length; i++)
            {
                var row = ObjectiveTable.LaneRows[i];
                if (levelOnly && row.Kind != TargetPass.RowKind.Level)
                    continue;
                if (!string.Equals(row.System, system, StringComparison.Ordinal))
                    continue;
                if (!row.Covers(id))
                    continue;
                if (!ObjectiveTable.TrackMatches(row, track))
                    continue;
                if (row.Chapter == ObjectiveTable.ChapterAny)
                    continue;
                if (found == null)
                    found = new List<int>();
                if (!found.Contains(row.Chapter))
                    found.Add(row.Chapter);
            }

            if (found == null)
                return NoChapters;
            found.Sort();
            return found.ToArray();
        }

        // The suffix a chapter miss earns, appended to whatever reason the answer already carried.
        // Empty when there is nothing to add, so a GENUINE silence — the guide mute about this slot
        // on this track at every chapter — reads exactly as it always has.
        //
        // ⚠ NO NUMBER HERE CAN BE READ AS A LEVEL. The only integers are chapter indices, and the
        // queried one is parenthesised, so this text can never produce the substring "level <n>" —
        // the shape ObjectiveParityTests forbids on every no-opinion row, because 0 is the game's
        // UNSET SENTINEL and a rendered number on a silence is the hazard this reader exists to
        // refuse.
        private static string ChapterMissNote(int[] chapters, int queryChapter, bool levelled)
        {
            if (chapters == null || chapters.Length == 0)
                return "";

            var sb = new StringBuilder();
            sb.Append(" — ⚠ CHAPTER MISS, not a silence about the slot: the guide ");
            sb.Append(levelled
                ? "DOES supply a stopping level for this slot on this track"
                : "DOES speak about this slot on this track");
            sb.Append(", at chapter(s) ");
            for (int i = 0; i < chapters.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(chapters[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(" — just not at the queried chapter (")
              .Append(queryChapter.ToString(CultureInfo.InvariantCulture))
              .Append("). DERIVED FROM THE TABLE, NOT THE LEDGER: SilenceSpec has no chapter axis, " +
                      "so this is the Chapter field of the rows that failed to match, read fresh");
            return sb.ToString();
        }

        // ---- the per-slot query (T2b) ------------------------------------------------------------

        public static SlotAnswer Slot(int chapter, TargetPass.Track track, string system, int id)
        {
            if (string.IsNullOrEmpty(system))
                return new SlotAnswer
                {
                    Availability = Availability.Unevaluated,
                    Rows = NoRows,
                    Conflicts = NoConflicts,
                    SpokenAtChapters = NoChapters,
                    Reason = "no system named — a slot query without a system slug is unanswerable " +
                             "(23 §0.1)",
                };

            // A conflict outranks a row set: 23 records the M0/M1 sentence as having two readings and
            // adjudicates NEITHER, so the honest answer is "the operator must choose", not one of
            // them. Reading B (no target) is what the decision record later settles on, and the
            // silence ledger already carries it — but that is recorded on the conflict, not silently
            // applied here.
            var conflicts = ObjectiveTable.ConflictsFor(chapter, track);
            var mine = new List<ObjectiveTable.ConflictRow>();
            for (int i = 0; i < conflicts.Count; i++)
            {
                var c = conflicts[i];
                if (!string.Equals(c.System, system, StringComparison.Ordinal))
                    continue;
                if (c.Ids != null && Array.IndexOf(c.Ids, id) < 0)
                    continue;
                mine.Add(c);
            }

            if (mine.Count > 0)
                return new SlotAnswer
                {
                    Availability = Availability.Conflict,
                    Rows = NoRows,
                    Conflicts = mine,
                    // ⚠ NOT A CHAPTER MISS AND DELIBERATELY NOT ANNOTATED AS ONE. A conflict is the
                    // guide speaking HERE, irreconcilably; "it speaks at another chapter" would be
                    // a category error and would dilute an adjudication 23 declines to make.
                    SpokenAtChapters = NoChapters,
                    Reason = "CONFLICT: 23 records two irreconcilable readings of the guide's own " +
                             "sentence and adjudicates neither — the operator must choose. No " +
                             "numeric value is emitted on either reading",
                };

            var rows = ObjectiveTable.LanesFor(chapter, track, system, id);
            if (rows.Count > 0)
                return new SlotAnswer
                {
                    Availability = Availability.Rows,
                    Rows = rows,
                    Conflicts = NoConflicts,
                    // The guide speaks AT THIS CHAPTER; there is no miss to report.
                    SpokenAtChapters = NoChapters,
                    Reason = null,
                };

            // NO ROW: a silence. Surfaced with the ledger's recorded reason — never a default of 0,
            // never -1, never long.MaxValue, never "unsatisfied so keep funding" (23 §7). The ledger
            // lives in TargetPass because Pass 3 already answers this question for the live lane;
            // there is one ledger, not two.
            TargetPass.SilenceSpec spec;
            var found = TargetPass.FindSilence(system, id, track, out spec);

            // ⚠ THE VERDICT IS ALREADY DECIDED ABOVE AND NOTHING BELOW MAY MOVE IT. This is Silent
            // either way; the derivation only decides whether the REASON gains the chapter fact.
            // At a ChapterAny query it is provably empty — a chapter-scoped row MATCHES ChapterAny,
            // so reaching this line at ChapterAny means the slot has no rows at any chapter — which
            // is why there is no ChapterAny guard here; the invariant is asserted, not defended.
            var spoken = ChaptersSpeaking(track, system, id);
            return new SlotAnswer
            {
                Availability = Availability.Silent,
                Rows = NoRows,
                Conflicts = NoConflicts,
                SilenceKnown = found,
                SilenceClass = found ? spec.Class : TargetPass.SilenceClass.Unspecified,
                SilenceCite = found ? spec.Cite : null,
                SpokenAtChapters = spoken,
                Reason = (found
                    ? "silent (" + spec.Class + "): " + spec.Reason + " [" + spec.Cite + "]"
                    : "silent: no row for (" + system + ", " +
                      id.ToString(CultureInfo.InvariantCulture) + ", " + track + ", chapter " +
                      chapter.ToString(CultureInfo.InvariantCulture) +
                      ") and no ledger entry — surfaced, never defaulted (23 §7)")
                    + ChapterMissNote(spoken, chapter, false),
            };
        }

        // ---- routing a slot's rows, without duplicating Pass 3 -----------------------------------
        // Convenience for a consumer that wants to know what Pass 3 WOULD do with this slot's rows.
        // Every row is materialised for the given id and handed to TargetPass.Route unchanged, so a
        // `precondition` cannot become a `target` on the way: there is no code path in this file that
        // reads a Terminality and writes a number.
        //
        // ⚠ Nothing calls this on a live path. It exists so the objective layer can be COMPARED
        // against the profile before either is trusted ([OPERATOR], the additive path), which is what
        // this whole layer is for.
        public struct RoutedRow
        {
            public ObjectiveTable.LaneRow Row;
            public TargetPass.RowRoute Route;
        }

        public static IList<RoutedRow> Route(int chapter, TargetPass.Track track, string system,
            int id)
        {
            var answer = Slot(chapter, track, system, id);
            var routed = new List<RoutedRow>();
            if (!answer.HasRows)
                return routed;

            for (int i = 0; i < answer.Rows.Count; i++)
            {
                var row = answer.Rows[i];
                routed.Add(new RoutedRow
                {
                    Row = row,
                    Route = TargetPass.Route(row.ToTargetRow(id)),
                });
            }
            return routed;
        }

        // ---- the LEVEL question, and the silence inventory ---------------------------------------
        // 23 §7 is "a LIST, not a DISCOVERY" — an operator asks the reader what is unfilled rather
        // than finding out one slot at a time. "Unfilled" means NO LEVEL: see SlotAnswer.HasLevelRow.

        public struct LevelAnswer
        {
            // True only when the guide supplies a stopping level for this slot. When it does, the
            // rows are here and a caller routes them through TargetPass; when it does not, THIS IS A
            // SILENCE and there is no number anywhere on this type to mistake for one.
            public bool HasLevel;
            public IList<ObjectiveTable.LaneRow> LevelRows;

            // The non-level guidance that DOES exist for the slot, if any — the rate, the time box,
            // the predicate. Empty is different from "the guide is mute", and both are different
            // from a zero.
            public IList<ObjectiveTable.LaneRow> OtherRows;

            public bool SilenceKnown;
            public TargetPass.SilenceClass SilenceClass;

            // ⚠ DERIVED FROM THE TABLE, NOT FROM THE LEDGER. The chapters at which the guide DOES
            // supply a stopping level for this (system, id, track). Non-empty exactly when this
            // no-level answer is a CHAPTER MISS — the guide levels the slot, elsewhere in the
            // progression. Empty (never null) when HasLevel, and when the guide levels it nowhere on
            // this track.
            //
            // ⚠ THIS IS ORTHOGONAL TO SilenceKnown, NOT A REFINEMENT OF IT, and that is the whole
            // reason it is a separate field rather than a fifth SilenceClass. 21 of the corpus's
            // chapter misses are LEDGERED (the Evil-NGU catch-all covers ngu-energy 7/8 and
            // ngu-magic 5 at every non-ch.5 chapter while the table levels all three at ch.5), so
            // SilenceKnown is true and this is non-empty at the same coordinate. A single enum would
            // have had to choose between the two facts; both are true.
            public int[] LevelledAtChapters;

            public bool IsChapterMiss
            {
                get
                {
                    return !HasLevel && LevelledAtChapters != null &&
                           LevelledAtChapters.Length > 0;
                }
            }

            public string Reason;   // non-null exactly when HasLevel is false
        }

        // The question Pass 3 can actually consume: does the guide give this slot a stopping level?
        public static LevelAnswer LevelSlot(int chapter, TargetPass.Track track, string system,
            int id)
        {
            var answer = Slot(chapter, track, system, id);

            var levels = new List<ObjectiveTable.LaneRow>();
            var others = new List<ObjectiveTable.LaneRow>();
            if (answer.Rows != null)
            {
                for (int i = 0; i < answer.Rows.Count; i++)
                {
                    if (answer.Rows[i].Kind == TargetPass.RowKind.Level)
                        levels.Add(answer.Rows[i]);
                    else
                        others.Add(answer.Rows[i]);
                }
            }

            if (levels.Count > 0)
                return new LevelAnswer
                {
                    HasLevel = true,
                    LevelRows = levels,
                    OtherRows = others,
                    LevelledAtChapters = NoChapters,
                    Reason = null,
                };

            // NO LEVEL. Whether or not other guidance exists, this slot is one of 23 §7's, and the
            // ledger's recorded reason is the answer. Never 0, never -1, never long.MaxValue.
            TargetPass.SilenceSpec spec;
            var found = TargetPass.FindSilence(system, id, track, out spec);

            // ⚠ HasLevel IS ALREADY false AND STAYS false. The derivation cannot reach the verdict —
            // it reads the table a second time to describe the same absence more precisely, and
            // feeds nothing but text.
            var levelled = ChaptersWithLevel(track, system, id);

            // A conflict is NOT annotated: at a conflicted coordinate the guide speaks here, and
            // "it levels the slot at another chapter" would talk past an adjudication 23 declines
            // to make. The field is still populated — the fact is true — but the sentence is left
            // exactly as the conflict wrote it.
            var conflicted = answer.Availability == Availability.Conflict;
            var note = conflicted ? "" : ChapterMissNote(levelled, chapter, true);

            return new LevelAnswer
            {
                HasLevel = false,
                LevelRows = NoRows,
                OtherRows = others,
                SilenceKnown = found,
                SilenceClass = found ? spec.Class : TargetPass.SilenceClass.Unspecified,
                LevelledAtChapters = levelled,
                Reason = (found
                    ? "no level (" + spec.Class + "): " + spec.Reason + " [" + spec.Cite + "]"
                    : (conflicted
                        ? answer.Reason
                        : "no level for (" + (system ?? "?") + ", " +
                          id.ToString(CultureInfo.InvariantCulture) + ", " + track +
                          ") and no ledger entry — surfaced, never defaulted (23 §7)"))
                    + note,
            };
        }

        public struct SilentSlot
        {
            public string System;
            public int Id;
            public TargetPass.Track Track;
            public TargetPass.SilenceClass Class;
            public bool Known;
            public bool HasNonLevelGuidance;   // the guide speaks, but not with a level

            // ⚠ THE THIRD KIND OF ROW IN THIS INVENTORY, and it is the one an operator most needs
            // separated out. Known says whether the LEDGER named the silence; HasNonLevelGuidance
            // says whether the guide speaks HERE without a level; this says the guide LEVELS THE
            // SLOT, at another chapter. A row with this set is not a gap in the guide at all — it is
            // this query landing between the chapters the guide addressed. Derived from the table on
            // every call, never recorded.
            public bool IsChapterMiss;
            public int[] LevelledAtChapters;

            public string Reason;
        }

        // Every slot in [0, idCount) for which the guide supplies NO LEVEL — 23 §7's ledger,
        // enumerated rather than discovered.
        public static IList<SilentSlot> Silences(int chapter, TargetPass.Track track, string system,
            int idCount)
        {
            var hits = new List<SilentSlot>();
            for (int id = 0; id < idCount; id++)
            {
                var answer = LevelSlot(chapter, track, system, id);
                if (answer.HasLevel)
                    continue;
                hits.Add(new SilentSlot
                {
                    System = system,
                    Id = id,
                    Track = track,
                    Class = answer.SilenceClass,
                    Known = answer.SilenceKnown,
                    HasNonLevelGuidance = answer.OtherRows != null && answer.OtherRows.Count > 0,
                    IsChapterMiss = answer.IsChapterMiss,
                    LevelledAtChapters = answer.LevelledAtChapters ?? NoChapters,
                    Reason = answer.Reason,
                });
            }
            return hits;
        }

        // The id counts the seven systems carry, so a caller enumerating silences does not have to
        // know them. They sum to 38.
        //
        // ⚠ THE 37-VS-38 DISCREPANCY, AND WHAT THIS ENUMERATION SHOWS ABOUT IT. 23 §7.2 records:
        // "the O1 enumeration is 14 augment + 16 NGU + 5 AT + 2 TM = 37; amendment 16 §8 and 22
        // §Q1.0 both say 38. The discrepancy is not resolved here." Written out per system, the gap
        // is arithmetic rather than substantive: 14 + 16 + 5 + 2 = 37 counts SIX systems and omits
        // WANDOOS, which 23 itself treats as the seventh (§2.6 gives it a section, §7.2 gives it a
        // silence row, and TargetPass.SysWandoos names it). Adding it gives 38 and matches the other
        // two sources.
        //
        // That is an OBSERVATION ABOUT THE SUM, not an adjudication: the reconciliation belongs to
        // whoever owns O1's enumeration. Recorded so a later pass does not re-open it as new. As 23
        // says, it affects no row — every one of the seven systems is enumerated here regardless.
        public static int IdCount(string system)
        {
            if (system == TargetPass.SysNguEnergy) return 9;
            if (system == TargetPass.SysNguMagic) return 7;
            if (system == TargetPass.SysAt) return 5;
            if (system == TargetPass.SysAugments) return 14;   // 7 augments + 7 upgrades
            if (system == TargetPass.SysTmSpeed) return 1;
            if (system == TargetPass.SysTmGoldMulti) return 1;
            if (system == TargetPass.SysWandoos) return 1;
            return 0;
        }

        public static readonly string[] AllSystems =
        {
            TargetPass.SysAugments,
            TargetPass.SysNguEnergy,
            TargetPass.SysNguMagic,
            TargetPass.SysAt,
            TargetPass.SysTmSpeed,
            TargetPass.SysTmGoldMulti,
            TargetPass.SysWandoos,
        };
    }
}
