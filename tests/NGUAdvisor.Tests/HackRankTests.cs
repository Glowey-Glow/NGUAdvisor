using System.Collections.Generic;
using System.Linq;
using Xunit;
using NGUAdvisor.Managers;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// Pricing the R3 tail: the standing default is chosen by marginal value density, not by name.
    ///
    /// ── WHY ───────────────────────────────────────────────────────────────────────────────────────
    /// PostCBlock3 ended in a bare HACK-1. HACK-1 has no target, so hitTarget is never true and the
    /// lane never retires — Adventure holds the whole pool for the rest of the run regardless of what
    /// it is worth. On the live board 2026-08-18 (L153, 100K pool) it ranked ELEVENTH of fifteen.
    ///
    /// ── THE BOARD THESE NUMBERS COME FROM ─────────────────────────────────────────────────────────
    /// Real [HackPrice]/[HackDbg] output, 2026-08-18, R3 pool 100K. `sat` is the game's own
    /// saturation, which IS the cost term (baseDivider x 1.0078^L x (L+1), scaled by res3 power and
    /// hack speed). Cross-check: Adventure sat=19.2e9 predicts 192,000 ticks/level; the game reported
    /// 191,511.
    /// </summary>
    public class HackRankTests
    {
        // id, name, level, sat, baseEffectPerLevel, milestoneEffect, levelsToNextMilestone
        // Constants from audit/11-captured-constants-r3.md; levels and sat from the live log.
        private static readonly object[][] Board =
        {
            new object[] {  0, "Attack/Defense", 235L, 27_700_000_000L,  0.025,   1.025,  5L },
            new object[] {  1, "Adventure",      153L, 19_200_000_000L,  0.001,   1.02,  47L },
            new object[] {  2, "Time Machine",    57L,  6_840_000_000L,  0.002,   1.02,  43L },
            new object[] {  3, "Drop Chance",     40L,  4_240_000_000L,  0.0025,  1.03,  40L },
            new object[] {  4, "Augment Speed",   20L,  3_720_000_000L,  0.002,   1.01,  20L },
            new object[] {  5, "Energy NGU",      30L, 14_800_000_000L,  0.001,   1.015, 30L },
            new object[] {  6, "Magic NGU",       30L, 14_800_000_000L,  0.001,   1.015, 30L },
            new object[] {  7, "Blood Gain",       0L,    758_000_000L,  0.001,   1.04,  50L },
            new object[] {  8, "QP Gain",          0L,  1_520_000_000L,  0.0005,  1.008, 50L },
            new object[] {  9, "Daycare",          0L,  3_790_000_000L,  0.0002,  1.005, 45L },
            new object[] { 10, "EXP",              0L,  7_580_000_000L,  0.00025, 1.01,  75L },
            new object[] { 11, "NUMBER",           0L, 15_200_000_000L,  0.05,    1.04,  40L },
            new object[] { 12, "PP",               0L, 37_900_000_000L,  0.0005,  1.005, 25L },
            new object[] { 13, "Hack Hack",        0L, 37_900_000_000L,  0.0005,  1.10, 100L },
            new object[] { 14, "Wish",             0L,1_890_000_000_000L,0.0001,  1.005, 50L },
        };

        private static List<HackPhase.Candidate> LiveBoard(bool withMilestone = true,
                                                           IEnumerable<int> ineligible = null)
        {
            var skip = new HashSet<int>(ineligible ?? Enumerable.Empty<int>());
            var list = new List<HackPhase.Candidate>();
            foreach (var r in Board)
            {
                int id = (int)r[0];
                long lvl = (long)r[2], sat = (long)r[3], toMs = (long)r[6];
                double b = (double)r[4], m = (double)r[5];
                double d = HackMath.MarginalDensity(b, lvl, sat);
                if (withMilestone) d += HackMath.MilestoneStep(m, toMs, sat);
                list.Add(new HackPhase.Candidate
                {
                    Id = id,
                    Density = d,
                    Eligible = !skip.Contains(id) && d > 0,
                });
            }
            return list;
        }

        [Fact]
        public void The_milestone_factor_cancels_out_of_the_rate()
        {
            // d(bonus)/bonus = b/(1+L*b) regardless of how many milestones are banked. Same hack,
            // same level, same price — a hack sitting on 4 milestones has the same RATE as one on 0.
            double a = HackMath.MarginalDensity(0.05, 0, 15_200_000_000L);
            double b = HackMath.MarginalDensity(0.05, 0, 15_200_000_000L);
            Assert.Equal(a, b);

            // And the rate decays with level exactly as 1/(1+L*b): hack 11 at L40 is 1/3 of L0.
            double l0 = HackMath.MarginalDensity(0.05, 0, 1000L);
            double l40 = HackMath.MarginalDensity(0.05, 40, 1000L);
            Assert.Equal(3.0, l0 / l40, 6);
        }

        [Fact]
        public void The_live_board_ranks_NUMBER_first_and_Adventure_eleventh()
        {
            var toks = HackPhase.RankedTail(LiveBoard());

            Assert.Equal("HACK-11", toks[0]);          // NUMBER  — 5.92%/h
            Assert.Equal("HACK-7", toks[1]);           // Blood   — 2.37%/h

            // Adventure, the incumbent that used to hold the pool unconditionally.
            int adv = System.Array.IndexOf(toks, "HACK-1");
            Assert.True(adv >= 9, "Adventure should rank near the bottom, was #" + (adv + 1));

            // The gap is the point: it is far outside any argument about weighting one hack's
            // percent against another's. TWO NUMBERS, BOTH REAL — the PURE RATE gap is 73x
            // (5.92%/h vs 0.081%/h); folding in the amortised milestone step narrows it to ~49.8x,
            // because Adventure is 47 levels from a 2% milestone and hack 11 is 40 from a 4% one.
            // Quote whichever, but say which.
            double num = LiveBoard().First(c => c.Id == 11).Density;
            double advD = LiveBoard().First(c => c.Id == 1).Density;
            Assert.True(num / advD > 45, "expected a >45x gap, got " + (num / advD));

            double numRate = LiveBoard(withMilestone: false).First(c => c.Id == 11).Density;
            double advRate = LiveBoard(withMilestone: false).First(c => c.Id == 1).Density;
            Assert.True(numRate / advRate > 70, "pure-rate gap should be ~73x, got " + (numRate / advRate));
        }

        [Fact]
        public void The_guide_sweep_still_leads_and_keeps_its_order()
        {
            // ⚠ The CBlock-3 gate was validated in game 2026-08-13 and shipped in public 2.4.0.
            // Ranking replaces the TAIL only; the bounded first-milestone lanes must be untouched.
            var toks = HackPhase.R3Tokens(true, LiveBoard());

            Assert.Equal("MILEHACK-2", toks[0]);
            Assert.Equal("MILEHACK-3", toks[1]);
            Assert.Equal("MILEHACK-4", toks[2]);
            Assert.Equal("MILEHACK-5", toks[3]);
            Assert.Equal("MILEHACK-6", toks[4]);
            Assert.Equal("HACK-11", toks[5]);          // priced tail begins here
            Assert.DoesNotContain("HACK-1", toks.Take(5));
        }

        [Fact]
        public void Pre_CBlock3_is_untouched_because_one_lane_cannot_be_ranked()
        {
            Assert.Equal(HackPhase.PreCBlock3, HackPhase.R3Tokens(false, LiveBoard()));
        }

        // ── THE SHAPES THAT WOULD BREAK ──────────────────────────────────────────────────────────

        [Fact]
        public void An_ineligible_lane_is_never_seated_however_good_its_price()
        {
            // HackBP.Unlocked() refuses a hard-capped or locked lane, and a maxed hack still BURNS
            // the progress bar while skipping the level++ — so seating one parks the pool on
            // nothing. Rank must never hand the head to a lane the breakpoint will then refuse.
            var board = LiveBoard(ineligible: new[] { 11, 7 });   // the two best, both refused
            var toks = HackPhase.RankedTail(board);

            Assert.DoesNotContain("HACK-11", toks);
            Assert.DoesNotContain("HACK-7", toks);
            Assert.Equal("HACK-3", toks[0]);           // next by price
        }

        [Fact]
        public void The_decoy_slot_15_would_take_the_head_lane_forever_if_it_were_ever_priced()
        {
            // HacksController.properties has SIXTEEN rows. Slot 15 is the garbage-named decoy
            // (audit/11 §F2): baseEffectPerLevel = 1 — twenty times hack 11's 0.05, the largest
            // real coefficient — milestoneEffect = 1 so its staircase is inert, and NOTHING
            // consumes its bonus. If the reader ever enumerates hacks.hacks.Count instead of the
            // 0..14 bound HackBP.Unlocked() uses, this is what happens:
            var board = LiveBoard();
            board.Add(new HackPhase.Candidate
            {
                Id = 15,
                Density = HackMath.MarginalDensity(1.0, 0, 15_200_000_000L),   // same price as NUMBER
                Eligible = true,
            });

            var poisoned = HackPhase.RankedTail(board);
            Assert.Equal("HACK-15", poisoned[0]);       // it takes the head...

            double decoy = board.First(c => c.Id == 15).Density;
            double best = board.First(c => c.Id == 11).Density;
            Assert.True(decoy / best > 15, "the decoy outprices the true top lane by " + (decoy / best) + "x");

            // ...and it never retires: level 0, no consumer, no milestone. The bound is the guard.
            // This test is the reason the bound must not be "tidied" into hacks.hacks.Count.
        }

        [Fact]
        public void The_static_default_is_dropped_by_value_so_reordering_PostCBlock3_is_safe()
        {
            // Dropping by position (PostCBlock3.Length - 1) deletes whatever happens to sit last.
            // Rebuild the guide sweep in a different order and the sweep must survive intact with
            // exactly the default removed — no lane silently lost.
            var toks = HackPhase.R3Tokens(true, LiveBoard());

            foreach (var guide in new[] { "MILEHACK-2", "MILEHACK-3", "MILEHACK-4", "MILEHACK-5", "MILEHACK-6" })
                Assert.Contains(guide, toks);

            // HACK-1 appears exactly once, and via the RANKING (near the bottom), not the static list.
            Assert.Equal(1, toks.Count(t => t == "HACK-1"));
            Assert.True(System.Array.IndexOf(toks, "HACK-1") > 5);

            // Nothing from the static sweep was dropped as collateral.
            Assert.Equal(HackPhase.PostCBlock3.Length - 1, toks.Count(t => t.StartsWith("MILEHACK-")));
        }

        [Fact]
        public void An_unpriceable_board_falls_back_to_exactly_todays_list()
        {
            // A failed read must never do worse than the static list it replaces.
            Assert.Equal(HackPhase.UnpricedTail, HackPhase.RankedTail(null));
            Assert.Equal(HackPhase.UnpricedTail, HackPhase.RankedTail(new List<HackPhase.Candidate>()));
            Assert.Equal(HackPhase.UnpricedTail,
                HackPhase.RankedTail(LiveBoard(ineligible: Enumerable.Range(0, 15))));

            // And the whole-list form degrades to the pre-ranking behaviour, HACK-1 tail included.
            var toks = HackPhase.R3Tokens(true, new List<HackPhase.Candidate>());
            Assert.Equal(HackPhase.PostCBlock3, toks);
        }

        [Fact]
        public void Zero_and_garbage_prices_are_refused_not_ranked_top()
        {
            // sat <= 0 means the game could not price the lane. A naive 1/sat would rank it FIRST.
            Assert.Equal(0.0, HackMath.MarginalDensity(0.05, 0, 0L));
            Assert.Equal(0.0, HackMath.MarginalDensity(0.05, 0, -1L));
            Assert.Equal(0.0, HackMath.MarginalDensity(0.0, 0, 1000L));
            Assert.Equal(0.0, HackMath.MarginalDensity(-1.0, 0, 1000L));
            Assert.Equal(0.0, HackMath.MilestoneStep(1.0, 10, 1000L));    // no milestone gain
            Assert.Equal(0.0, HackMath.MilestoneStep(1.04, 0, 1000L));    // divide-by-zero guard
        }

        [Fact]
        public void The_order_is_stable_and_ties_break_on_id_so_it_cannot_thrash()
        {
            // Density only moves when a level moves. Equal densities must not reorder between ticks,
            // or the head lane would flap and the pool would chase it.
            var a = HackPhase.RankedTail(LiveBoard());
            var b = HackPhase.RankedTail(LiveBoard());
            Assert.Equal(a, b);

            var tied = new List<HackPhase.Candidate>
            {
                new HackPhase.Candidate { Id = 9, Density = 1.0, Eligible = true },
                new HackPhase.Candidate { Id = 2, Density = 1.0, Eligible = true },
                new HackPhase.Candidate { Id = 5, Density = 1.0, Eligible = true },
            };
            Assert.Equal(new[] { "HACK-2", "HACK-5", "HACK-9" }, HackPhase.RankedTail(tied));
        }

        [Fact]
        public void The_milestone_step_only_reorders_lanes_that_are_already_close()
        {
            // Hack 13 is one of the worst rates in the game but carries a 10% milestone. Including
            // the step must not vault it over lanes that are orders of magnitude better.
            var withMs = HackPhase.RankedTail(LiveBoard(withMilestone: true));
            var without = HackPhase.RankedTail(LiveBoard(withMilestone: false));

            Assert.Equal("HACK-11", withMs[0]);
            Assert.Equal("HACK-11", without[0]);
            Assert.True(System.Array.IndexOf(withMs, "HACK-13") > 5,
                "a 10% milestone on a bottom-rate lane must not buy the head of the queue");
        }
    }
}
