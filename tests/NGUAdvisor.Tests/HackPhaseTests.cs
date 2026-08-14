using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE GUIDE CH.5 HACK-PHASE GATE (audit/10 §A1.1 rows 1-3, amendment 09 §1's sequencing read).
    //
    // What broke, and what these tests pin so it cannot come back silently: the auto profile's R3
    // token line was a bare ALLHACK — hacks 0..14 in index order — and R3Breakpoints is an
    // order-not-share waterfill whose head lane self-limits only at saturation, which at this stage
    // exceeds the whole pool by five orders of magnitude. So HACK-0 drank everything, forever, at
    // every chapter. The guide's own switch ("A/D until completing CBlock 3, then Adventure; first
    // milestone on Hacks 3-7") existed only as prose.
    public class HackPhaseTests
    {
        // ---------------- the token lists ----------------

        [Fact]
        public void Pre_phase_is_AD_hack_alone()
        {
            Assert.Equal(new[] { "HACK-0" }, HackPhase.R3Tokens(postCBlock3: false));
        }

        // THE INDEX TRAP (audit/10 §A1.0): the guide's "Hacks 3-7" is 1-BASED — the parenthetical
        // "(TM-mNGU)" resolves it to decomp ids 2..6. A 0-based transcription (ids 3..7, DC-Blood)
        // is the documented wrong reading; this test is the place it fails loudly.
        [Fact]
        public void Post_phase_is_the_first_milestone_sweep_ids_2_to_6_then_adventure()
        {
            Assert.Equal(
                new[] { "MILEHACK-2", "MILEHACK-3", "MILEHACK-4", "MILEHACK-5", "MILEHACK-6", "HACK-1" },
                HackPhase.R3Tokens(postCBlock3: true));
        }

        // The waterfill hands the pool to the FIRST non-terminal lane. The sweep lanes are the
        // bounded ones (MileHackBP stops at the first milestone) and Adventure is unbounded
        // (hitTarget never true with no target set) — so every bounded lane must sit ahead of the
        // unbounded default or the sweep never receives a unit. Order is the allocation.
        [Fact]
        public void Post_phase_puts_every_bounded_lane_ahead_of_the_unbounded_default()
        {
            var tokens = HackPhase.R3Tokens(postCBlock3: true);
            int firstUnbounded = Array.FindIndex(tokens, t => !t.StartsWith("MILEHACK", StringComparison.Ordinal));
            Assert.Equal(tokens.Length - 1, firstUnbounded);   // exactly one, and it is last
            Assert.Equal("HACK-1", tokens[tokens.Length - 1]);
        }

        // Every token both phases emit must survive the real parser's grammar — a token that parses
        // to nothing is a silent no-op (the WISH-0 lesson). The mirror is pinned to the parser
        // source by ProfileTokenCorpusTests, so Emitted here means the runtime seats the lane.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_phase_token_parses_and_seats_exactly_one_lane(bool post)
        {
            foreach (var token in HackPhase.R3Tokens(post))
            {
                var lanes = new List<BreakpointParseMirror.Lane>();
                var outcome = BreakpointParseMirror.Expand(
                    token, BreakpointParseMirror.Pool.R3, syncTraining: false, lanes);
                Assert.Equal(BreakpointParseMirror.Outcome.Emitted, outcome);
                Assert.Single(lanes);
                Assert.Equal(token.StartsWith("MILEHACK", StringComparison.Ordinal) ? "MileHackBP" : "HackBP",
                             lanes[0].Family);
            }
        }

        // ---------------- the completion predicate ----------------

        // The switch condition is cblock3's EVIL leg, read from the campaign table itself — one
        // source of truth with Status(). Pinned to the guide's own list (ch.5): five 100LCs, five
        // NoAugs, five Basics, one Troll (TC-2 is optional and must NOT appear), one NoNGU, one
        // NoTM, six NoRBs, one Blind. The Normal return-trip leg is a different difficulty and must
        // not leak in — its counters are unreadable from Evil.
        [Fact]
        public void Cblock3_evil_leg_requirements_derive_from_the_campaign_table()
        {
            var req = CampaignTables.LegRequirements(HackPhase.BlockId, CampaignTables.Evil);
            var expected = new Dictionary<string, int>
            {
                ["100LC"] = 5, ["NOAUG"] = 5, ["BASIC"] = 5, ["TC"] = 1,
                ["NONGU"] = 1, ["NOTM"] = 1, ["NORB"] = 6, ["BLIND"] = 1,
            };
            Assert.Equal(expected.OrderBy(k => k.Key, StringComparer.Ordinal),
                         req.OrderBy(k => k.Key, StringComparer.Ordinal));
        }

        [Fact]
        public void Cblock3_normal_leg_is_the_return_trip_not_the_gate()
        {
            var req = CampaignTables.LegRequirements(HackPhase.BlockId, CampaignTables.Normal);
            Assert.Equal(new[] { "24HR", "NOTM" },
                         req.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(10, req["24HR"]);
            Assert.Equal(10, req["NOTM"]);
        }

        private static Dictionary<string, int> EvilLegDone() => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["100LC"] = 5, ["NOAUG"] = 5, ["BASIC"] = 5, ["TC"] = 2,
            ["NONGU"] = 1, ["NOTM"] = 1, ["NORB"] = 6, ["BLIND"] = 1,
            ["24HR"] = 1, ["NOEC"] = 0, ["LSC"] = 0,   // extra codes are fine; requirements drive the walk
        };

        [Fact]
        public void ChainSatisfied_is_true_when_every_required_ordinal_is_done()
        {
            var req = CampaignTables.LegRequirements(HackPhase.BlockId, CampaignTables.Evil);
            Assert.True(HackPhase.ChainSatisfied(req, EvilLegDone()));
        }

        [Theory]
        [InlineData("NORB", 5)]    // one short on the longest chain
        [InlineData("TC", 0)]      // the single mandatory Troll missing (TC-2 being optional changes nothing)
        [InlineData("BLIND", 0)]   // the single Blind missing
        public void ChainSatisfied_is_false_while_any_required_ordinal_is_short(string code, int cur)
        {
            var req = CampaignTables.LegRequirements(HackPhase.BlockId, CampaignTables.Evil);
            var live = EvilLegDone();
            live[code] = cur;
            Assert.False(HackPhase.ChainSatisfied(req, live));
        }

        // FAIL CLOSED, both ways. A code the live read did not supply is unverifiable, not done;
        // an empty requirements dict means the table gave us nothing to verify against (wrong block
        // id, wrong difficulty) and must not read as "no requirements, therefore satisfied" — the
        // gate falls back to the pre-CBlock3 list that was also the status quo ante.
        [Fact]
        public void ChainSatisfied_fails_closed_on_missing_data()
        {
            var req = CampaignTables.LegRequirements(HackPhase.BlockId, CampaignTables.Evil);
            var live = EvilLegDone();
            live.Remove("NORB");
            Assert.False(HackPhase.ChainSatisfied(req, live));

            Assert.False(HackPhase.ChainSatisfied(new Dictionary<string, int>(), EvilLegDone()));
            Assert.False(HackPhase.ChainSatisfied(null, EvilLegDone()));
            Assert.False(HackPhase.ChainSatisfied(req, null));
            Assert.False(HackPhase.ChainSatisfied(
                CampaignTables.LegRequirements("no-such-block", CampaignTables.Evil), EvilLegDone()));
        }

        // ---------------- the milestone stop ----------------

        // MileHackBP's stop, at the values the guide publishes for the sweep's own hacks
        // (audit/10 §A3.2): first milestones at 50/40/20/30/30 levels, reducers only ever lowering
        // them. Terminal must stay terminal as reducers land: 45 >= 50 is false, but once past, a
        // LOWER threshold cannot resurrect the lane.
        [Theory]
        [InlineData(0, 50, false)]
        [InlineData(49, 50, false)]
        [InlineData(50, 50, true)]
        [InlineData(51, 45, true)]    // threshold reduced after the milestone was passed — stays done
        [InlineData(20, 20, true)]    // Augment Speed's unreduced first milestone
        [InlineData(0, 0, true)]      // unreachable per the capture ruling; if it happens, fail SAFE (done)
        public void FirstMilestoneMet_matches_the_guides_milestone_arithmetic(long level, long threshold, bool met)
        {
            Assert.Equal(met, HackMath.FirstMilestoneMet(level, threshold));
        }
    }
}
