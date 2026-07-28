using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The CHALLENGE ROTATION ENGINE, headlessly. BaseRebirth itself is not linkable here (it reaches into
    // Character / Main), so the two things that make the rotation behave are hoisted out of it:
    //   * the eligibility rule is BreakpointEditor.ChallengeEligible, which BaseRebirth.RCTarget.ChallengeValid
    //     calls — the SAME code the game runs, not a paraphrase;
    //   * the selection rule is one line (FirstOrDefault over the entries in file order), reproduced by the
    //     Sim below along with ParseChallenges' drop rules (CampaignTables.TryParseOrdinal is the existing
    //     mirror of that parse, and an unknown code is dropped because CreateInstance returns null).
    // Everything else about a rebirth is irrelevant to which challenge starts.
    //
    // These tests exist for two jobs. The first is the CROSS-PROFILE CHAIN: the campaign hands one challenge
    // code from block to block by completion ordinal, and a single missing ordinal strands every entry above
    // it in every later profile — a failure that is invisible in any one file. The second is the record of a
    // schema proposal that was implemented, measured, and REJECTED; see RejectedGreedySentinel below.
    public class ChallengeRotationTests
    {
        /// <summary>
        /// One profile's Challenges array plus a completion counter per code, stepped one rebirth at a time.
        /// Each Rebirth() = "the advisor decided to rebirth": pick the first eligible entry, engage it, and
        /// count the completion it earns. Completions are per-code and per-difficulty in the game; one Sim is
        /// one difficulty.
        /// </summary>
        private sealed class Sim
        {
            private readonly List<KeyValuePair<string, int>> _entries = new List<KeyValuePair<string, int>>();
            private readonly Dictionary<string, int> _cur = new Dictionary<string, int>();

            public Sim(params string[] rotation)
            {
                foreach (var raw in rotation)
                {
                    if (!CampaignTables.TryParseOrdinal(raw, out var code, out var index)) continue;
                    if (CampaignTables.CapOf(code) <= 0) continue;      // unknown code: CreateInstance returns null
                    _entries.Add(new KeyValuePair<string, int>(code, index));
                }
            }

            /// <summary>Seed already-earned completions, e.g. everything the earlier C-blocks did.</summary>
            public Sim With(string code, int completions) { _cur[code.ToUpperInvariant()] = completions; return this; }

            public int Completions(string code) { int v; return _cur.TryGetValue(code.ToUpperInvariant(), out v) ? v : 0; }

            private bool Valid(KeyValuePair<string, int> e) =>
                BreakpointEditor.ChallengeEligible(e.Value, Completions(e.Key), CampaignTables.CapOf(e.Key));

            /// <summary>BaseRebirth.AnyChallengesValid — also what pulls a rebirth in ahead of its timer.</summary>
            public bool AnyValid() => _entries.Any(Valid);

            /// <summary>
            /// BaseRebirth.TryStartChallenge: engage the FIRST eligible entry. Returns the code that started,
            /// or null for a plain rebirth. The completion is credited immediately — the run itself is not
            /// modelled, only its effect on the counter the next rebirth reads.
            /// </summary>
            public string Rebirth()
            {
                foreach (var e in _entries)
                {
                    if (!Valid(e)) continue;
                    _cur[e.Key] = Completions(e.Key) + 1;
                    return e.Key;
                }
                return null;
            }

            /// <summary>What the next `n` rebirths engage, in order (null == a plain rebirth).</summary>
            public List<string> Run(int n)
            {
                var log = new List<string>();
                for (int i = 0; i < n; i++) log.Add(Rebirth());
                return log;
            }
        }

        // ------------------------------------------------------------------ the rule itself

        [Fact]
        public void An_ordinal_fires_on_exactly_one_rebirth()
        {
            Assert.False(BreakpointEditor.ChallengeEligible(3, 1, 5));
            Assert.True(BreakpointEditor.ChallengeEligible(3, 2, 5));      // the rebirth that earns the 3rd
            Assert.False(BreakpointEditor.ChallengeEligible(3, 3, 5));     // inert once earned
            Assert.False(BreakpointEditor.ChallengeEligible(3, 4, 5));
            Assert.True(BreakpointEditor.ChallengeEligible(1, 0, 5));
            Assert.False(BreakpointEditor.ChallengeEligible(6, 5, 5));     // above the cap: never
        }

        [Fact]
        public void An_already_earned_ordinal_is_inert_not_an_error()
        {
            // The property the whole "write the full 1..N range" convention rests on.
            var sim = new Sim("BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5").With("BASIC", 3);
            Assert.Equal(new[] { "BASIC", "BASIC", null }, sim.Run(3));
            Assert.Equal(5, sim.Completions("BASIC"));
        }

        // ------------------------------------------------------------------ the Evil 24HR-8 gap

        // The Evil 24H union across the shipped profiles USED TO BE 1..7, 9, 10 — CBlock3.2-E has 1, CBlock4
        // has 2..5, CBlock5 has 6..7, and FinalEvil24hCBlock opened at 9. Nothing supplied 8, and eligibility
        // needs cur + 1, so FinalEvil24hCBlock was dead on arrival: 24HR-9 wanted cur == 8 and cur was stuck
        // at 7. FinalEvil24hCBlock now runs 24HR-8..10 in both trees and the union is contiguous
        // (CampaignTablesTests derives that from the files). Both rotations are kept below because the
        // PROPERTY is what this test owns — a gap kills a rotation silently — and the repaired shape is worth
        // pinning against a later edit that trims the 8 back off.
        [Fact]
        public void Evil_24h_chain_stalls_at_seven_without_ordinal_8_and_runs_with_it()
        {
            var stalled = new Sim("24HR-9", "24HR-10").With("24HR", 7);
            Assert.False(stalled.AnyValid());
            Assert.Equal(new string[] { null, null, null, null, null }, stalled.Run(5));
            Assert.Equal(7, stalled.Completions("24HR"));   // never moves, however long the block runs

            // THE FIX: supply the missing ordinal. Nothing else about the schema has to change.
            var fixedUp = new Sim("24HR-8", "24HR-9", "24HR-10").With("24HR", 7);
            Assert.Equal(new[] { "24HR", "24HR", "24HR", null }, fixedUp.Run(4));
            Assert.Equal(10, fixedUp.Completions("24HR"));

            // The full range works identically and is portable to a save at ANY completion count, which is
            // why ExpandTarget writes it. Same three runs from 7; from 0 it would run all ten.
            var full = new Sim(Enumerable.Range(1, 10).Select(i => "24HR-" + i).ToArray()).With("24HR", 7);
            Assert.Equal(new[] { "24HR", "24HR", "24HR", null }, full.Run(4));
            Assert.Equal(10, full.Completions("24HR"));
        }

        // A gap anywhere strands everything above it, in every later profile, silently. Stated generally so
        // the property is not read as a fact about 24HR.
        [Fact]
        public void A_missing_ordinal_strands_every_entry_above_it()
        {
            var sim = new Sim("NOTM-1", "NOTM-2", "NOTM-4", "NOTM-5").With("NOTM", 0);
            Assert.Equal(new[] { "NOTM", "NOTM", null, null }, sim.Run(4));
            Assert.Equal(2, sim.Completions("NOTM"));       // 4 and 5 are unreachable forever
        }

        // ------------------------------------------------------------------ the rejected proposal

        // PROPOSAL (evaluated 2026-07-27, REJECTED): redefine ordinal 0 as "run this challenge until its
        // cap", i.e. `index == 0 ? cur < max : <the current rule>`, so a chain could cross a gap like the
        // Evil 24HR-8 one without naming the ordinals. Ordinal 0 really is free — no shipped profile's
        // Challenges array uses it (the "-0" entries elsewhere in those files, NGU-0 / HACK-0 / AT-0 /
        // WISH-0, are allocation Priorities, a different grammar) — and it really does clear the gap.
        //
        // It was rejected because it buys NO expressive power (see above: enumerating the ordinals already
        // says "run to the cap", from any starting count) while adding failure modes that the ordinal form
        // does not have. Each is pinned below against the ACTUAL rule, so the facts survive without the
        // mechanism. `Greedy` models what the sentinel WOULD have done.
        private static bool Greedy(int cur, int max) => cur < max;

        [Fact]
        public void RejectedGreedySentinel_ordinal_0_is_inert_today_which_is_what_made_it_look_free()
        {
            for (int cur = 0; cur <= 10; cur++)
                Assert.False(BreakpointEditor.ChallengeEligible(0, cur, 10));

            // ...and the editor clamps it away rather than writing a dead entry back to disk.
            Assert.Equal(new List<string> { "24HR-1" }, BreakpointEditor.CanonChallenges(new[] { "24HR-0" }));
            Assert.True(BreakpointEditor.TryParseChallenge("24HR-0", out var it));
            Assert.Equal(1, it.Index);
        }

        // WHY IT CANNOT MEAN "ONE MORE": eligibility is recomputed from live completions every rebirth and
        // nothing records that an entry fired, so a predicate that is true after a completion lands is true
        // again on the next rebirth. "CODE-0" is unavoidably "run to the cap".
        [Fact]
        public void RejectedGreedySentinel_would_repeat_until_the_cap_never_once()
        {
            int cur = 7, fired = 0;
            while (Greedy(cur, 10)) { cur++; fired++; }
            Assert.Equal(3, fired);                        // 8, 9, 10 — clears the gap, as pitched...
            Assert.False(Greedy(cur, 10));

            // ...but the same expression in a block that wants ONE completion runs ten. Chapter 2's
            // Micro-CBlock ("5 Basic and 1 24H") and Chapter 5's Evil entry ("don't do more than 1" Basic)
            // both need exactly-N, so both must keep explicit ordinals.
            int one = 0; for (int c = 0; Greedy(c, 10); c++) one++;
            Assert.Equal(10, one);
            var correct = new Sim("BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5", "24HR-1");
            correct.Run(20);
            Assert.Equal(1, correct.Completions("24HR"));  // what "24HR-1" guarantees, forever
        }

        // ORDER IS MEANING: BaseRebirth engages FirstOrDefault(ChallengeValid). An always-eligible entry
        // starves everything behind it. Demonstrated with the ordinal form's equivalent — a run of ordinals
        // that stays eligible for many consecutive rebirths — because that is the same scan.
        [Fact]
        public void RejectedGreedySentinel_first_eligible_wins_so_a_greedy_entry_would_starve_the_block()
        {
            // A ten-deep run placed first takes ten consecutive rebirths before anything else moves. A
            // sentinel behaves identically and cannot be bounded to fewer.
            var front = new Sim("NORB-1", "NORB-2", "NORB-3", "NOEC-1", "TC-1");
            Assert.Equal(new[] { "NORB", "NORB", "NORB", "NOEC", "TC" }, front.Run(5));
            Assert.Equal(0, front.Completions("NOEC") - 1); // NOEC only started once NORB ran dry

            // And "just put it last" is not a sufficient guard: a stranded entry ahead of it is skipped by
            // FirstOrDefault, so a rotation with any OTHER gap hands control straight to the greedy entry —
            // the exact situation this proposal was meant to rescue.
            var stranded = new Sim("TC-3", "NOEC-1");       // TC is at 0, so TC-3 can never fire
            Assert.Equal(new[] { "NOEC" }, stranded.Run(1));
            Assert.Equal(0, stranded.Completions("TC"));
        }

        // AnyChallengesValid short-circuits TimeRebirth's timer (`challenges` => rebirth NOW, ignoring the
        // profile's Time/Number target). So an always-eligible entry does not merely add challenge runs; it
        // suppresses the profile's whole rebirth plan until it is exhausted.
        [Fact]
        public void RejectedGreedySentinel_would_hold_the_rebirth_timer_open_for_its_whole_run()
        {
            var sim = new Sim("24HR-8", "24HR-9", "24HR-10").With("24HR", 7);
            for (int i = 0; i < 3; i++) { Assert.True(sim.AnyValid()); sim.Rebirth(); }
            Assert.False(sim.AnyValid());                  // a bounded run releases the timer at a known point
            // A sentinel releases it only at the cap, which no profile states and no reader can see.
            Assert.True(Greedy(0, 10));
        }

        // "DO TWO MORE" IS INEXPRESSIBLE, twice over: CanonChallenges dedupes on CODE+ORDINAL so two
        // "CODE-0" entries collapse into one, and even uncollapsed the first is always the one
        // FirstOrDefault picks and it already runs to the cap.
        [Fact]
        public void RejectedGreedySentinel_two_entries_could_not_mean_two_more()
        {
            Assert.Equal(new List<string> { "24HR-1" },
                         BreakpointEditor.CanonChallenges(new[] { "24HR-0", "24HR-0" }));
            // The ordinal form says it exactly, and says it in the file.
            var two = new Sim("24HR-8", "24HR-9").With("24HR", 7);
            two.Run(5);
            Assert.Equal(9, two.Completions("24HR"));
        }

        // ------------------------------------------------------------------ editor round-trip

        // Admitting 0 through the editor would have needed the [1, cap] clamp relaxed, and the clamp is load
        // bearing in both directions. These pin the grammar the companion depends on.
        [Fact]
        public void The_editor_grammar_keeps_zero_and_negative_ordinals_out_of_the_file()
        {
            Assert.Equal(new List<string> { "24HR-1" }, BreakpointEditor.CanonChallenges(new[] { "24HR-0" }));
            Assert.Equal(new List<string> { "24HR-1" }, BreakpointEditor.CanonChallenges(new[] { "24HR--4" }));
            Assert.Equal(new List<string> { "24HR-1" }, BreakpointEditor.CanonChallenges(new[] { "24HR-0..0" }));
            Assert.Equal(new List<string> { "24HR-1", "24HR-2", "24HR-3" }, BreakpointEditor.CanonChallenges(new[] { "24HR-0..3" }));
            // The ordinary grammar is untouched by all of the above.
            Assert.Equal(new List<string> { "24HR-1", "24HR-2", "24HR-3" }, BreakpointEditor.CanonChallenges(new[] { "24HR-3" }));
            Assert.Equal(new List<string> { "24HR-1", "24HR-2", "24HR-3" }, BreakpointEditor.CanonChallenges(new[] { "24HR*3" }));
            Assert.Equal(new List<string> { "24HR-2", "24HR-3" }, BreakpointEditor.CanonChallenges(new[] { "24HR-2..3" }));
            Assert.Equal(new List<string> { "24HR-8", "24HR-9", "24HR-10" }, BreakpointEditor.CanonChallenges(new[] { "24HR-8..10" }));
        }

        // The rotations the fix touches, through display -> save. FinalEvil24hCBlock's current (stalled) form
        // and its repaired form must both survive the editor byte-identical, or the follow-up profile pass
        // would be undone the first time the Challenges page is opened.
        [Theory]
        [InlineData("24HR-9 24HR-10")]                                                   // FinalEvil24hCBlock, before
        [InlineData("24HR-8 24HR-9 24HR-10")]                                            // FinalEvil24hCBlock, as shipped
        [InlineData("NOTM-6 NOTM-7 NOTM-8 NOTM-9 NOTM-10 24HR-7 24HR-8 24HR-9 24HR-10")] // CBlock3.0-N
        [InlineData("BASIC-1")]                                                          // EvilStart
        [InlineData("BLIND-1 24HR-2")]                                                   // C-Miniblock2.4-Finish
        public void Rotations_touched_by_the_fix_survive_a_display_then_save(string rotation)
        {
            var raw = rotation.Split(' ');
            var entries = BreakpointEditor.GroupChallenges(raw).Select(g => g.Entry).ToList();
            Assert.Equal(raw, BreakpointEditor.CanonChallenges(entries).ToArray());
        }

        // The repaired rotation reads as ONE friendly row, "24 Hour x 10" starting partway in — the shape the
        // Challenges page already renders for every chained C-block, so the fix needs no UI work.
        [Fact]
        public void The_repaired_rotation_reads_as_one_partial_row()
        {
            var rows = BreakpointEditor.GroupChallenges(new[] { "24HR-8", "24HR-9", "24HR-10" });
            Assert.Single(rows);
            Assert.Equal(8, rows[0].From);
            Assert.Equal(10, rows[0].Target);
            Assert.True(rows[0].Partial);
            Assert.False(rows[0].Single);
            Assert.False(rows[0].Suspect);                 // not the old editor's damage shape
            Assert.Equal("24HR-8..10", rows[0].Entry);
        }
    }
}
