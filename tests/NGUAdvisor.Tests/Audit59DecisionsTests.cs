using System.Linq;
using Xunit;
using NGUAdvisor.Managers;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// audit/59 "Product decisions outstanding" — decisions 4, 5 and 6, resolved 2026-08-18.
    ///
    /// 4. Segment lifetime  — LIVE for deciders, PROVENANCE for reconstruction. Two readers wanted two
    ///                        different things, which is a missing field, not a conflict.
    /// 5. MarkStale         — WIRED. LedgerEntry.Segment's own comment already named the mechanism
    ///                        ("the run phase it was written in, the usual reason a write goes stale");
    ///                        nothing ever compared it to the live phase.
    /// 6. Wish selection    — a VALUE mode added alongside the three speed/price modes.
    /// </summary>
    // ⚠ WriteLedger's entry list is a process-wide static. WriteLedgerTests already declares this
    // collection for that reason; xUnit runs DIFFERENT collections in parallel, so without this
    // attribute these two classes race over one list and the Snapshot().Single() assertions below
    // fail intermittently. TestCollections.cs documents the trap; this class was missing from it.
    [Collection(TestCollections.WriteLedgerState)]
    public class Audit59DecisionsTests
    {
        // ── 5. MarkStale ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_write_justified_by_a_segment_that_ended_goes_stale()
        {
            WriteLedger.Reset();
            WriteLedger.Record("at.block", "1,234", "purpose floor for the segment", "NGU MARATHON");

            Assert.Equal(WriteState.Active, WriteLedger.Snapshot().Single().State);

            int n = WriteLedger.MarkStaleOutsideSegment("EVIL NGU");

            Assert.Equal(1, n);
            var e = WriteLedger.Snapshot().Single();
            Assert.Equal(WriteState.Stale, e.State);
            Assert.Contains("NGU MARATHON segment that justified this has ended", e.Why);
        }

        [Fact]
        public void A_write_whose_segment_still_runs_is_left_alone()
        {
            WriteLedger.Reset();
            WriteLedger.Record("at.block", "1,234", "purpose floor", "NGU MARATHON");

            Assert.Equal(0, WriteLedger.MarkStaleOutsideSegment("NGU MARATHON"));
            Assert.Equal(WriteState.Active, WriteLedger.Snapshot().Single().State);
        }

        [Fact]
        public void A_write_that_no_segment_justified_never_goes_stale_this_way()
        {
            // An empty stamp means the write was not made under a segment at all, so a segment
            // ending says nothing about it. Staling these would flip most of the ledger on the first
            // segment change for no reason.
            WriteLedger.Reset();
            WriteLedger.Record("at.block", "1,234", "purpose floor", "");

            Assert.Equal(0, WriteLedger.MarkStaleOutsideSegment("EVIL NGU"));
            Assert.Equal(WriteState.Active, WriteLedger.Snapshot().Single().State);
        }

        [Fact]
        public void Reverted_is_terminal_and_Contested_outranks_stale()
        {
            // Reverted: the field is already back in the operator's hands — a segment ending cannot
            // make that less true.
            WriteLedger.Reset();
            WriteLedger.Record("at.block", "1,234", "purpose floor", "NGU MARATHON");
            WriteLedger.MarkReverted("at.block");
            Assert.Equal(0, WriteLedger.MarkStaleOutsideSegment("EVIL NGU"));
            Assert.Equal(WriteState.Reverted, WriteLedger.Snapshot().Single().State);

            // Contested is a statement about ARBITRATION between two writers and outranks a statement
            // about timing. Flattening it to Stale would hide the more serious fact.
            WriteLedger.Reset();
            var contested = WriteLedger.Registry.FirstOrDefault(w => w.AlsoWrittenBy != null && w.AlsoWrittenBy.Length > 0);
            if (contested != null)
            {
                WriteLedger.Record(contested.Id, "x", "why", "NGU MARATHON");
                Assert.Equal(WriteState.Contested, WriteLedger.Snapshot().Single().State);
                Assert.Equal(0, WriteLedger.MarkStaleOutsideSegment("EVIL NGU"));
                Assert.Equal(WriteState.Contested, WriteLedger.Snapshot().Single().State);
            }
        }

        // ── 6. Wish value ranking ────────────────────────────────────────────────────────────────

        // Reproduces the game's own hackBonus so the cancellation can be OBSERVED rather than
        // asserted: hackBonus(L) = (1 + L*b) * m^floor(L/T)  ([DECOMP] HacksController.cs:415-428).
        private static double HackBonus(double b, double m, long T, long L)
            => (1 + L * b) * System.Math.Pow(m, L / T);

        [Fact]
        public void The_milestone_factor_really_does_cancel_out_of_the_rate()
        {
            // ⚠ THIS TEST USED TO BE `Assert.Equal(f(x), f(x))` WITH IDENTICAL ARGUMENTS. It passed
            // and proved nothing: MarginalDensity takes no milestone parameter, so the claim it
            // named was not reachable through it at all. The property is about hackBonus, so the
            // test has to compute hackBonus.
            const double b = 0.002, m = 1.02;
            const long T = 50;

            // Two levels with DIFFERENT banked milestone counts: L=10 has 0, L=110 has 2.
            foreach (var L in new long[] { 10, 110, 260 })
            {
                double here = HackBonus(b, m, T, L);
                double next = HackBonus(b, m, T, L + 1);
                // Skip the level that CROSSES a milestone — there the step is real, not the rate.
                if ((L + 1) / T != L / T) continue;

                double observed = (next - here) / here;
                Assert.Equal(HackMath.RelativeGainPerLevel(b, L), observed, 9);
            }

            // And the rate is genuinely independent of the banked count: same b, same level, wildly
            // different milestone multipliers -> identical relative gain.
            //
            // (The line that used to sit here was `Assert.Equal(f(b,10), f(b,10))` - the very
            // tautology this test was written to replace, left inside the replacement. Removed.)
            double small = (HackBonus(b, 1.001, T, 10 + 1) - HackBonus(b, 1.001, T, 10)) / HackBonus(b, 1.001, T, 10);
            double large = (HackBonus(b, 1.900, T, 10 + 1) - HackBonus(b, 1.900, T, 10)) / HackBonus(b, 1.900, T, 10);
            Assert.Equal(small, large, 12);
        }

        [Fact]
        public void Hacks_and_wishes_provably_share_one_law()
        {
            // hackBonus  = (1 + L*b) * m^floor(L/T)  -> d/bonus = b/(1+L*b), the m^k cancelling
            // wishEffect = 1 + L*e                   -> d/bonus = e/(1+L*e), no milestone term at all
            // Same expression. MarginalDensity divides it by a cost; WishValueRate multiplies by a rate.
            const double b = 0.05;
            Assert.Equal(b / 1.0, HackMath.RelativeGainPerLevel(b, 0), 12);
            Assert.Equal(b / 3.0, HackMath.RelativeGainPerLevel(b, 40), 12);

            Assert.Equal(HackMath.RelativeGainPerLevel(b, 40) / 1000.0,
                         HackMath.MarginalDensity(b, 40, 1000L), 15);

            Assert.Equal(HackMath.RelativeGainPerLevel(b, 40) * 0.25,
                         HackMath.WishValueRate(b, 40, 0.25), 15);
        }

        // ── THE ONE WISH THAT SUBTRACTS ──────────────────────────────────────────────────────────

        [Fact]
        public void The_reducer_wish_is_not_ranked_with_the_additive_law()
        {
            // respawn1() = 1 - L*e, floored at 0.9 ([DECOMP] WishesController.cs:1373-1377). Wish 46
            // is the only one; verified by sweeping the controller for `1f - ... level * properties`.
            const double e = 0.01;

            // Below the floor it still pays, and the magnitude uses 1 - L*e, not 1 + L*e.
            Assert.Equal(e / (1 - 5 * e), HackMath.ReducerGainPerLevel(e, 5), 12);
            Assert.NotEqual(HackMath.RelativeGainPerLevel(e, 5), HackMath.ReducerGainPerLevel(e, 5), 6);

            // A reducer ACCELERATES as it goes (the denominator shrinks) - the opposite of the
            // additive law, which decays. Getting the sign wrong inverts the whole ranking.
            Assert.True(HackMath.ReducerGainPerLevel(e, 9) > HackMath.ReducerGainPerLevel(e, 1));
            Assert.True(HackMath.RelativeGainPerLevel(e, 9) < HackMath.RelativeGainPerLevel(e, 1));
        }

        [Fact]
        public void The_respawn_floor_is_NOT_modelled_because_it_cannot_be_reached()
        {
            // audit/16 §F2 ruled this explicitly: "code that special-cases 'wish 46 hits the respawn
            // floor' ... is modelling a transition that does not occur". effectPerLevel[46] is
            // ~0.01 and maxLevel[46] is 10, and GetValidWishes already filters level < maxLevel - so
            // the level is at most 9, the multiplier never drops below ~0.91, and the game's own
            // clamp is a strict `<`. A guard here would be dead code pretending to be a safeguard,
            // the same shape as the minimumWishTime() flaw the same audit told us not to guard.
            //
            // So ReducerGainPerLevel models 1 - L*e and nothing else. At every REACHABLE level it
            // is positive and finite:
            const double e = 0.00999999978;      // the captured float, not a tidy decimal
            for (long L = 0; L <= 9; L++)
                Assert.True(HackMath.ReducerGainPerLevel(e, L) > 0, "level " + L + " should still pay");

            // Only a mathematically degenerate input returns zero - e.g. a multiplier at or past 0.
            Assert.Equal(0.0, HackMath.ReducerGainPerLevel(1.0, 1));
            Assert.Equal(0.0, HackMath.ReducerGainPerLevel(e, -1));
        }

    }
}
