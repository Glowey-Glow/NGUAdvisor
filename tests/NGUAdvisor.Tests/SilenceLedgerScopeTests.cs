using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // =============================================================================================
    // THE ANSWER TO audit/56 §6's LAST BULLET AND §8's LAST BULLET: **NO**.
    // =============================================================================================
    //
    // `56-at-track-model.md` closed on model (0) — the table stays as it is — and left exactly one
    // item open, in both §6 and §8:
    //
    //     "whether `at 2 Normal` / `at 2 Evil` should gain SilenceLedger entries (they have none
    //      today, §3.6) is a separate open question this document does not answer."
    //
    // THIS FILE ANSWERS IT: they should NOT, and these tests exist so nobody adds them later on the
    // reasonable-sounding grounds that `at 2` "has unledgered silences" — BECAUSE IT DOES. That is
    // the trap, and it is why a mere absence would not have survived contact with the next reader.
    //
    // ---------------------------------------------------------------------------------------------
    // ⚠ THE OBVIOUS ARGUMENT FOR "NO" IS WRONG, AND ANYONE RE-DERIVING IT WILL GET THE RIGHT ANSWER
    //   FOR A REASON THAT DOES NOT HOLD.
    //
    // The tempting reasoning runs: "`at 2` is not silent on either track — Normal has the ch.3 5,000
    // precondition (ObjectiveTable.cs:530) and Evil has the ch.5 100,000 terminal (:601). A slot WITH
    // a level row is not a silence, so FindSilence is never consulted for it, so an entry would be
    // dead data."
    //
    // ⚠ FALSE. ObjectiveTable.ChapterMatches (:1007-1011) is EXACT: a `Chapter = 3` row answers only
    // a chapter-3 query (and the ChapterAny wildcard). So on a chapter-KEYED query `at 2` IS silent
    // at 14 of the 16 (chapter, track) coordinates in the 1..8 x {Normal, Evil} grid, FindSilence IS
    // consulted at every one of them, and it returns false at every one of them. Entries would not be
    // dead data; they would be LIVE data — which is worse, because the reason they carried would be
    // consulted and believed. `The_at_2_silences_are_real_live_and_deliberately_unledgered` below
    // pins those 14 coordinates so the false premise cannot be re-adopted.
    //
    // ---------------------------------------------------------------------------------------------
    // ⚠ THE SECOND TEMPTING ARGUMENT IS ALSO WRONG: "a ledger entry may never name a slot the table
    //   levels."
    //
    // The ledger ALREADY does that, deliberately. Its Evil-NGU catch-all (TargetPass.cs:614-628,
    // amendment 18 §1) is `Ids = null, Track = Evil` over both pools, and the table carries Evil
    // LEVEL rows for ngu-energy 7 and 8 (ObjectiveTable.cs:444, :450). The catch-all's match set is
    // deliberately wider than its intent; FindSilence is simply never reached where a row exists, so
    // the over-coverage is harmless. `The_ledgers_catch_alls_deliberately_over_cover` pins that too,
    // so the real law below is not mistaken for a blanket prohibition it is not.
    //
    // ---------------------------------------------------------------------------------------------
    // THE THREE REASONS THE ANSWER IS STILL "NO":
    //
    // LEG 1 — SCOPE. The ledger is the register of the CHAPTER-AGNOSTIC silences.
    //   `ObjectiveLayerTests.The_guide_supplies_a_level_for_19_of_114_slots_and_names_the_other_95`
    //   defines it: of 38 slots x 3 tracks = 114, evaluated at ChapterAny, the guide SUPPLIES a level
    //   for 19 and is silent on 95. `at 2 Normal` and `at 2 Evil` are TWO OF THE NINETEEN SUPPLIED.
    //   An entry addressed at a supplied slot asserts the opposite of what that census counts.
    //
    //   ⚠ THIS LEG ORIGINALLY SAID "AND THE LEDGER NAMES ALL 95". IT NAMES 94. `ngu-energy 2`
    //   (Respawn) on Normal is silent at ChapterAny with SilenceKnown == false — measured by
    //   `ChapterMissDerivationTests.The_ledger_names_94_of_the_95_and_the_ninety_fifth_is_respawn`.
    //   The census test never checked: it asserts only that each silence carries a REASON, which the
    //   unledgered fallback text satisfies. The leg's conclusion is UNAFFECTED — `at 2` is in the 19,
    //   not the 95, on either count — but the "all 95" phrasing must not be quoted onward.
    //
    // LEG 2 — `at 2`'s SILENCES ARE CHAPTER MISSES, WHICH ARE NOT THE LEDGER'S BUSINESS.
    //   `at 2` contributes 14 of the parity fixture's 71 (7 Normal + 7 Evil, audit/56 §3.6's
    //   measured breakdown, and re-measured here). It is not a special case; it is 14 of one
    //   documented phenomenon.
    //
    //   ⚠ THIS LEG ORIGINALLY QUOTED ObjectiveParityTests AS SAYING THE 71 WERE "ALL OF THEM CHAPTER
    //   MISSES". THE QUOTE WAS ACCURATE AND THE QUOTED CLAIM WAS FALSE. Measured
    //   (ChapterMissDerivationTests), the 71 are 63 chapter misses + 8 GENUINE LEDGER HOLES —
    //   `ngu-energy 2` (Respawn) on Normal at every chapter, a slot with no row on that track at any
    //   chapter and no entry. `at 2`'s 14 are all in the 63, so this leg holds; but the 57 "others"
    //   counted below are 49 misses + those 8, not 57 misses.
    //
    // LEG 3 — MEASURED: ENTRIES WOULD DESTROY THE FIXTURE'S MEANING WITHOUT TRIPPING ANYTHING ELSE.
    //   Both entries were added to SilenceLedger on a scratch edit and the full suite run. Result:
    //   ObjectiveParityTests' `unrecorded` moved 71 -> 57 (exactly -7 Normal, -7 Evil, confirming
    //   §3.6) and that was the ONLY failure. `Every_ledger_entry_surfaces_and_never_defaults`
    //   (TargetPassTests.cs:487) gained two passing cases, because it checks an entry against
    //   TargetPass.Evaluate with no rows and never cross-checks the table. So the only guard is a
    //   count whose comment says "every one a chapter miss, not a hole in the ledger" — which would
    //   silently become false: 14 chapter misses reclassified as "recorded" while 57 identical ones
    //   stayed "unrecorded".
    //
    //   ⚠ THIS LEG CLOSED WITH "THE FIX FOR THE 71 IS A CHAPTER AXIS ON SilenceSpec". THAT DEFERRED
    //   ITEM HAS BEEN WORKED AND THE ANSWER IS NO — see ChapterMissDerivationTests. A chapter axis
    //   misses the 8 genuine holes entirely (there is no chapter at which the guide levels Respawn
    //   on Normal), cannot reach the 21 chapter misses the Evil catch-all ALREADY answers, and would
    //   hand-copy 84 facts ObjectiveTable already holds. The chapter miss is DERIVED in
    //   ObjectiveReader instead; SilenceSpec is unchanged and deliberately still has no Chapter
    //   field. The rest of this leg's measurement stands exactly as recorded.
    //
    // ⚠ NOT VALIDATED IN GAME. Nothing here touches a live path — the objective layer has no consumer
    // (audit/56 §0) and this file is tests only. No production file was modified to answer this.
    // =============================================================================================
    public class SilenceLedgerScopeTests
    {
        private const string At = TargetPass.SysAt;
        private const int BlockId = 2;

        // The chapters at which `at 2` has NO level row, per track. Derived, not asserted blind:
        // Normal's only level row is Chapter = 3 (ObjectiveTable.cs:530), Evil's is Chapter = 5
        // (:601), and ChapterMatches is exact — so every OTHER chapter in 1..8 is a miss.
        private static readonly int[] NormalMisses = { 1, 2, 4, 5, 6, 7, 8 };
        private static readonly int[] EvilMisses = { 1, 2, 3, 4, 6, 7, 8 };

        // -----------------------------------------------------------------------------------------
        // THE PIN ITSELF. Adding either entry fails this test first and by name.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void At_block_has_no_silence_ledger_entry_on_normal_or_evil_and_must_not_gain_one()
        {
            TargetPass.SilenceSpec spec;

            Assert.False(TargetPass.FindSilence(At, BlockId, TargetPass.Track.Normal, out spec),
                "at 2 Normal gained a SilenceLedger entry. audit/56 §8 left this open and it is " +
                "answered NO — the slot is LEVELLED chapter-agnostically (5,000 at ch.3, " +
                "ObjectiveTable.cs:530), so it is one of the NINETEEN SUPPLIED slots in " +
                "ObjectiveLayerTests' 19/114 census, not one of the 95 the ledger registers. Its " +
                "7 real silences are CHAPTER MISSES, which ObjectiveParityTests.cs:325-333 " +
                "explicitly declines to close with ledger entries. See this file's header.");

            Assert.False(TargetPass.FindSilence(At, BlockId, TargetPass.Track.Evil, out spec),
                "at 2 Evil gained a SilenceLedger entry. Same ruling, same reason — the slot is " +
                "LEVELLED chapter-agnostically (100,000 at ch.5, ObjectiveTable.cs:601) and is one " +
                "of the nineteen SUPPLIED slots. See this file's header.");

            // ⚠ AND THE CONTRAST THAT PROVES THE LEDGER IS NOT MERELY ABSENT HERE: the SADISTIC
            // coordinate of the very same slot IS registered, by the §7.1 S2 catch-all
            // (TargetPass.cs:672-676). The ledger is doing its job on the track where the guide
            // genuinely never supplies a level; the two it declines are the two it should decline.
            Assert.True(TargetPass.FindSilence(At, BlockId, TargetPass.Track.Sadistic, out spec),
                "at 2 Sadistic lost its ledger entry — the §7.1 S2 catch-all is what makes the " +
                "Normal/Evil absence a RULING rather than an oversight");
            Assert.Equal(TargetPass.SilenceClass.Silent, spec.Class);
            Assert.Contains("SADISTIC is silent in every slot", spec.Reason);
        }

        // -----------------------------------------------------------------------------------------
        // LEG 1 — the slot is SUPPLIED at the granularity the ledger keys on.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void At_block_is_one_of_the_nineteen_supplied_slots_on_both_normal_and_evil()
        {
            // The ledger keys on (system, id, track) with NO chapter (TargetPass.FindSilence,
            // :681), and the 19/114 census enumerates at ChapterAny for exactly that reason. At
            // that granularity the guide SUPPLIES a level on both tracks.
            var normal = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                TargetPass.Track.Normal, At, BlockId);
            Assert.True(normal.HasLevel,
                "at 2 Normal is no longer chapter-agnostically levelled — the ruling in this " +
                "file's header rests on it being one of the 19 SUPPLIED slots");
            Assert.Equal(5000L, Assert.Single(normal.LevelRows).ValueLow);

            var evil = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                TargetPass.Track.Evil, At, BlockId);
            Assert.True(evil.HasLevel);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel,
                Assert.Single(evil.LevelRows).ValueLow);

            // A supplied slot carries no silence and therefore no reason — which is precisely why
            // a ledger entry has nothing to attach to at this granularity.
            Assert.False(normal.SilenceKnown);
            Assert.False(evil.SilenceKnown);
            Assert.Null(normal.Reason);
            Assert.Null(evil.Reason);
        }

        // -----------------------------------------------------------------------------------------
        // ⚠ LEG 2 — THE SILENCES ARE REAL. This is the test that stops the WRONG "no" argument.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_at_2_silences_are_real_live_and_deliberately_unledgered()
        {
            var misses = new List<string>();

            foreach (var pair in new[]
                     {
                         new { Track = TargetPass.Track.Normal, Chapters = NormalMisses, Has = 3 },
                         new { Track = TargetPass.Track.Evil, Chapters = EvilMisses, Has = 5 },
                     })
            {
                // The one chapter that DOES carry the level — the reason the slot is "supplied".
                Assert.True(
                    ObjectiveReader.LevelSlot(pair.Has, pair.Track, At, BlockId).HasLevel,
                    "at 2 " + pair.Track + " lost its level row at chapter " + pair.Has);

                foreach (var chapter in pair.Chapters)
                {
                    var answer = ObjectiveReader.LevelSlot(chapter, pair.Track, At, BlockId);

                    // IT IS A SILENCE. ObjectiveReader derives that from ROW ABSENCE, not from the
                    // ledger — "NO ROW: a silence" (ObjectiveReader.cs:163).
                    Assert.False(answer.HasLevel,
                        "at 2 " + pair.Track + " ch." + chapter + " gained a level row");

                    // FindSilence WAS CONSULTED AND FOUND NOTHING. This is the fact the "dead data"
                    // argument denies.
                    Assert.False(answer.SilenceKnown);
                    Assert.Equal(TargetPass.SilenceClass.Unspecified, answer.SilenceClass);

                    // AND IT STILL SURFACES WITH A REASON. An unledgered silence is not a hole —
                    // the ledger supplies PROVENANCE, never the silence itself.
                    Assert.False(string.IsNullOrEmpty(answer.Reason));
                    Assert.Contains("no ledger entry", answer.Reason);
                    Assert.Contains("surfaced, never defaulted", answer.Reason);

                    // ⚠ AND NEVER AS A NUMBER. Target 0 is the game's UNSET SENTINEL
                    // (ObjectiveReader.cs:16-21): a lane written to 0 reads unmet and funds forever.
                    // There is deliberately no numeric field on a silent answer to mistake for one.
                    Assert.Empty(answer.LevelRows);

                    // ⚠ AND THE TWO KINDS OF "NO LEVEL" ARE BOTH PRESENT HERE, WHICH IS A THIRD
                    // REASON A FIXED CHAPTER-AGNOSTIC SENTENCE WOULD BE WRONG. Availability answers
                    // "does the guide say ANYTHING here"; HasLevel answers "does it supply a
                    // stopping level" (ObjectiveReader.cs:68-74). At some of these chapters the
                    // guide is wholly mute; at others it speaks with a RATE and still gives no
                    // level. One ledger sentence cannot be true of both.
                    var slot = ObjectiveReader.Slot(chapter, pair.Track, At, BlockId);
                    if (slot.Availability == ObjectiveReader.Availability.Silent)
                    {
                        // Wholly mute. The chapter-keyed reason NAMES THE CHAPTER — the accuracy a
                        // chapter-agnostic ledger entry would replace with a fixed sentence.
                        Assert.False(slot.SilenceKnown);
                        Assert.Contains(
                            "chapter " + chapter.ToString(CultureInfo.InvariantCulture),
                            slot.Reason);
                        Assert.Empty(slot.Rows);
                    }
                    else
                    {
                        // The guide SPEAKS here — but not with a level. Rows exist and every one is
                        // non-Level, so LevelSlot still routes to the unledgered silence above.
                        Assert.Equal(ObjectiveReader.Availability.Rows, slot.Availability);
                        Assert.NotEmpty(slot.Rows);
                        Assert.False(slot.HasLevelRow);
                        Assert.NotEmpty(answer.OtherRows);
                    }

                    misses.Add(pair.Track + ":" + chapter);
                }
            }

            // 7 + 7 = 14, exactly audit/56 §3.6's measured per-slot contribution to the parity
            // fixture's 71. MEASURED independently: adding both ledger entries moves that fixture
            // 71 -> 57, which is this 14.
            Assert.Equal(14, misses.Count);
        }

        // -----------------------------------------------------------------------------------------
        // LEG 2, continued — `at 2` is 14/71 of a documented phenomenon, not a special case.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_at_2_chapter_misses_are_fourteen_of_the_seventy_one_and_not_a_ledger_hole()
        {
            int atBlock = 0, otherSlots = 0;

            foreach (var track in new[]
                     {
                         TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
                     })
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var answer = ObjectiveReader.LevelSlot(chapter, track, system, id);
                if (answer.HasLevel || answer.SilenceKnown)
                    continue;

                if (system == At && id == BlockId) atBlock++;
                else otherSlots++;
            }

            // The same 71 ObjectiveParityTests.cs:364 pins, partitioned.
            Assert.Equal(71, atBlock + otherSlots);
            Assert.Equal(14, atBlock);

            // ⚠ THE POINT: 57 OTHERS WOULD BE LEFT BEHIND. Ledgering `at 2` alone would make them
            // all read as genuine ledger holes.
            Assert.Equal(57, otherSlots);

            // ⚠ AND 49 OF THOSE 57 ARE THE SAME PHENOMENON — NOT 57. The remaining 8 are
            // `ngu-energy 2` (Respawn) on Normal, which ARE genuine ledger holes: no row on that
            // track at any chapter, no entry. The original wording ("chapter misses exactly like the
            // 14") was false of those 8. Measured in ChapterMissDerivationTests; the ruling on `at 2`
            // is unaffected, since all 14 of its misses are in the 49.
            int otherMisses = 0, otherHoles = 0;
            foreach (var track in new[]
                     {
                         TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
                     })
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var answer = ObjectiveReader.LevelSlot(chapter, track, system, id);
                if (answer.HasLevel || answer.SilenceKnown)
                    continue;
                if (system == At && id == BlockId)
                    continue;
                if (answer.IsChapterMiss) otherMisses++; else otherHoles++;
            }

            Assert.Equal(49, otherMisses);
            Assert.Equal(8, otherHoles);
            Assert.Equal(57, otherMisses + otherHoles);
        }

        // -----------------------------------------------------------------------------------------
        // THE GENERAL LAW, in the only form that is TRUE of the corpus.
        // -----------------------------------------------------------------------------------------
        [Fact]
        public void The_ledgers_catch_alls_deliberately_over_cover_levelled_slots()
        {
            // The Evil-NGU catch-all (amendment 18 §1) is Ids = null over the whole pool, and the
            // table carries Evil LEVEL rows for ngu-energy 7 and 8. Both facts are load-bearing and
            // both are true at once, which is why the law below is scoped to NARROW entries.
            TargetPass.SilenceSpec spec;
            Assert.True(TargetPass.FindSilence(TargetPass.SysNguEnergy, 7,
                TargetPass.Track.Evil, out spec));
            Assert.Contains("amendment 18 §1", spec.Cite);
            Assert.True(ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                TargetPass.Track.Evil, TargetPass.SysNguEnergy, 7).HasLevel);

            // ⚠ HARMLESS **AT ChapterAny ONLY**, AND THIS TEST ORIGINALLY CLAIMED MORE THAN IT
            // CHECKED. Its comment read "harmless because FindSilence is never REACHED where a row
            // exists" — true here, because at a ChapterAny query a chapter-scoped row matches and
            // ObjectiveReader returns the rows without asking the ledger. It does NOT generalise to
            // the chapter-keyed grid, and the generalisation is false: ChapterMatches is exact, so at
            // the seven non-ch.5 chapters FindSilence IS reached for ngu-energy 7/8 and ngu-magic 5
            // on Evil, and the catch-all answers "no level exists" about slots the table levels at
            // ch.5. That is 21 coordinates where the recorded reason contradicts the table — and it
            // is db2cf88's own stated worst case ("LIVE data, not dead ... the reason would be
            // believed") occurring through the over-coverage this test calls harmless.
            //
            // The over-coverage is still DELIBERATE and still not a bug in the ledger — the law
            // below is correctly scoped to narrow entries. What was wrong was the safety argument.
            // ObjectiveReader now appends the derived chapter fact to the catch-all's own sentence,
            // so both appear; see ChapterMissDerivationTests.
            Assert.False(ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny,
                TargetPass.Track.Evil, TargetPass.SysNguEnergy, 7).SilenceKnown);

            // The correction, asserted rather than left as prose: at a CHAPTER-KEYED query the same
            // slot IS answered by the catch-all, and the answer is a chapter miss.
            var keyed = ObjectiveReader.LevelSlot(1, TargetPass.Track.Evil,
                TargetPass.SysNguEnergy, 7);
            Assert.False(keyed.HasLevel);
            Assert.True(keyed.SilenceKnown);          // FindSilence WAS reached
            Assert.True(keyed.IsChapterMiss);         // ...about a slot the table levels at ch.5
            Assert.Equal(new[] { 5 }, keyed.LevelledAtChapters);
        }

        // ⚠ THE LAW THAT FORBIDS THE `at 2` ENTRIES WITHOUT FORBIDDING THE CATCH-ALLS.
        //
        // A NARROW entry — one that names a system AND an explicit id list AND a single track — is a
        // precise claim about exactly the slots it lists. It cannot be a wider rule's collateral
        // coverage, because it has no wider rule. Such an entry must therefore name a slot the guide
        // is chapter-agnostically silent on, i.e. one of the 95, never one of the 19.
        //
        // Every narrow entry in the ledger satisfies this today. An `at 2 Normal` or `at 2 Evil`
        // entry would be the first that does not — which is the whole ruling, stated mechanically.
        [Fact]
        public void No_narrow_ledger_entry_may_name_a_slot_the_guide_supplies_a_level_for()
        {
            var narrow = TargetPass.SilenceLedger
                .Where(e => e.System != null && e.Ids != null &&
                            e.Track != TargetPass.Track.Unspecified)
                .ToArray();

            // Guard the guard: if the ledger ever loses all its narrow entries this test would pass
            // vacuously and stop protecting anything.
            Assert.True(narrow.Length >= 4,
                "the narrow-entry law has nothing to check — vacuous guards are how a pin dies");

            foreach (var entry in narrow)
            foreach (var id in entry.Ids)
            {
                var answer = ObjectiveReader.LevelSlot(ObjectiveTable.ChapterAny, entry.Track,
                    entry.System, id);
                Assert.False(answer.HasLevel, string.Format(CultureInfo.InvariantCulture,
                    "SilenceLedger names ({0}, {1}, {2}) as a silence, but the guide SUPPLIES it " +
                    "a level chapter-agnostically — so it is one of the 19 supplied slots, not " +
                    "one of the 95 the ledger registers. If this is the at-2 Block cap, read the " +
                    "header of SilenceLedgerScopeTests.cs: audit/56 §8's open question was " +
                    "answered NO. A narrow entry cannot claim a slot the table levels; only a " +
                    "catch-all may over-cover one, and only as collateral to a wider rule.",
                    entry.System, id, entry.Track));
            }
        }
    }
}
