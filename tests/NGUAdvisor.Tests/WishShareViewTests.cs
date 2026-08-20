using Xunit;
using NGUAdvisor.Managers;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// The wish share record that puts a Wishes lane on the Energy and Magic boards.
    ///
    /// SCOPE. This covers the record and its sentences. It does NOT cover the snapshot wiring in
    /// UiBridge (appending the lane, reducing `unallocated`, folding the take into the change
    /// signature) — that reads live plans and cannot compile here. Per this project's own rule, a
    /// green suite here says nothing about whether the lane reaches the page.
    /// </summary>
    public class WishShareViewTests
    {
        public WishShareViewTests() { WishShareView.Reset(); }

        [Fact]
        public void Nothing_is_recorded_until_a_pass_runs()
        {
            // The distinction the board depends on: "the wish pass took nothing" must not look the
            // same as "the wish pass has never run", or a fresh session draws a zero bar it invented.
            Assert.False(WishShareView.Energy.Recorded);
            Assert.False(WishShareView.Magic.Recorded);
        }

        /// <summary>
        /// THE ~300% BUG. The take is a share of the pool AFTER removeAllResources() handed last tick's
        /// wish holdings back to idle; the plan's pool was measured during the swap, while those
        /// holdings were still held. Reported live on an end-game save: lanes working from ~1e16 while
        /// the wish slots sat on ~8e18. The board must widen its denominator, not scale the bar.
        /// </summary>
        [Fact]
        public void The_board_pool_includes_the_holdings_the_wish_pass_released()
        {
            const long laneAllocated = 10_000_000_000_000_000L;      // ~1e16 committed by the lanes
            const long idleAtPass = 8_100_000_000_000_000_000L;      // ~8.1e18 back in idle after release
            WishShareView.Record(offeredEnergy: idleAtPass, takenEnergy: idleAtPass, idleEnergyAtPass: idleAtPass,
                                 offeredMagic: 0, takenMagic: 0, idleMagicAtPass: 0);

            var pool = WishShareView.Energy.BoardPool(laneAllocated, planPool: laneAllocated);

            Assert.Equal(laneAllocated + idleAtPass, pool);
            // The share that used to read ~300% is now at or below 100%.
            Assert.True(WishShareView.Energy.Taken <= pool,
                "the wish take must never exceed the pool it is drawn against");
        }

        [Fact]
        public void Lanes_plus_wishes_plus_idle_close_against_the_board_pool()
        {
            const long laneAllocated = 250L;
            const long idleAtPass = 1000L;
            WishShareView.Record(offeredEnergy: 800, takenEnergy: 600, idleEnergyAtPass: idleAtPass,
                                 offeredMagic: 0, takenMagic: 0, idleMagicAtPass: 0);

            var e = WishShareView.Energy;
            var pool = e.BoardPool(laneAllocated, planPool: laneAllocated);

            // 250 lanes + 600 wishes + 400 idle == 1250. The board's three shares must account for
            // everything; a gap is what let the largest consumer hide in the first place.
            Assert.Equal(1250, pool);
            Assert.Equal(400, idleAtPass - e.Taken);
            Assert.Equal(pool, laneAllocated + e.Taken + (idleAtPass - e.Taken));
        }

        [Fact]
        public void A_negative_lane_allocation_cannot_shrink_the_pool()
        {
            WishShareView.Record(10, 5, 10, 0, 0, 0);
            Assert.Equal(10, WishShareView.Energy.BoardPool(-999, planPool: 0));
        }

        [Fact]
        public void An_offer_larger_than_the_measured_idle_widens_the_pool_rather_than_overflowing_it()
        {
            // The two are read a moment apart; the offer is a percentage OF the idle pool, so if it
            // reads larger the pool is the stale number. Trust the offer.
            WishShareView.Record(offeredEnergy: 900, takenEnergy: 900, idleEnergyAtPass: 100,
                                 offeredMagic: 0, takenMagic: 0, idleMagicAtPass: 0);

            Assert.Equal(900, WishShareView.Energy.IdleAtPass);
            Assert.True(WishShareView.Energy.Taken <= WishShareView.Energy.BoardPool(0, planPool: 0));
        }

        [Fact]
        public void A_pass_that_took_nothing_still_records()
        {
            WishShareView.Record(offeredEnergy: 500, takenEnergy: 0, idleEnergyAtPass: 500,
                                 offeredMagic: 300, takenMagic: 0, idleMagicAtPass: 300);

            Assert.True(WishShareView.Energy.Recorded);
            Assert.Equal(0, WishShareView.Energy.Taken);
            Assert.Equal(500, WishShareView.Energy.Offered);
            Assert.Equal(500, WishShareView.Energy.Untaken);
        }

        [Fact]
        public void Offer_and_take_are_kept_apart()
        {
            // The whole point: the plan knows the OFFER, only this knows the TAKE, and a board built
            // on the offer cannot tell 100% from 0%.
            WishShareView.Record(offeredEnergy: 1000, takenEnergy: 1000, idleEnergyAtPass: 1000,
                                 offeredMagic: 1000, takenMagic: 0, idleMagicAtPass: 1000);

            Assert.Equal(1000, WishShareView.Energy.Taken);
            Assert.Equal(0, WishShareView.Energy.Untaken);
            Assert.Equal(0, WishShareView.Magic.Taken);
            Assert.Equal(1000, WishShareView.Magic.Untaken);
        }

        [Fact]
        public void Energy_and_magic_are_independent()
        {
            WishShareView.Record(offeredEnergy: 10, takenEnergy: 4, idleEnergyAtPass: 10,
                                 offeredMagic: 900, takenMagic: 900, idleMagicAtPass: 900);

            Assert.Equal(4, WishShareView.Energy.Taken);
            Assert.Equal(900, WishShareView.Magic.Taken);
        }

        [Theory]
        [InlineData(100, 103)]   // slot rounding: remaining/slots + Sign(remaining%slots) can overshoot
        [InlineData(0, 5)]
        public void A_take_wider_than_the_offer_is_clamped(long offered, long taken)
        {
            // AllocateToWish rounds each slot UP, so four slots can sum to a few units past the offer.
            // A lane drawn wider than the residue it came from reads as a board bug.
            WishShareView.Record(offered, taken, offered, offered, taken, offered);

            Assert.Equal(offered, WishShareView.Energy.Taken);
            Assert.Equal(0, WishShareView.Energy.Untaken);
        }

        [Fact]
        public void Negative_inputs_are_floored()
        {
            WishShareView.Record(offeredEnergy: -5, takenEnergy: -9, idleEnergyAtPass: -3,
                                 offeredMagic: -1, takenMagic: 0, idleMagicAtPass: -1);

            Assert.Equal(0, WishShareView.Energy.Offered);
            Assert.Equal(0, WishShareView.Energy.Taken);
            Assert.Equal(0, WishShareView.Energy.Untaken);
        }

        [Fact]
        public void End_game_magnitudes_survive_the_record()
        {
            // The save this was built for: wishes holding ~8.1e18 energy against a 9e18 cap. Both are
            // inside long, and the subtraction in Untaken must not overflow or lose units.
            const long offered = 8_999_999_999_999_999_988L;
            const long taken = 8_117_790_657_517_421_599L;
            WishShareView.Record(offered, taken, offered, offered, taken, offered);

            Assert.Equal(taken, WishShareView.Energy.Taken);
            Assert.Equal(offered - taken, WishShareView.Energy.Untaken);
        }

        [Fact]
        public void The_lane_sentence_never_repeats_the_reclaimed_next_swap_claim()
        {
            // The one word that made a compounding claim on the pool look like normal operation.
            // Reclaim() releases wandoos/augments/TM/AT/NGU/BT and never wishesController.
            var took = WishShareView.LaneWhy(tookAnything: true);
            Assert.Contains("NOT reclaimed", took);
            Assert.DoesNotContain("reclaimed next swap", took);
        }

        [Fact]
        public void The_took_nothing_sentence_names_both_reasons_it_can_happen()
        {
            var none = WishShareView.LaneWhy(tookAnything: false);
            Assert.Contains("AND R3", none);      // a wish needs all three resources non-zero
            Assert.Contains("0", none);           // or the slider is zero
        }

        // ---- sink vs priority: how much the wish pass may claim ---------------------------------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(50.0)]
        [InlineData(100.0)]
        public void Sink_mode_takes_the_whole_remainder_and_ignores_the_slider(double slider)
        {
            // The point of the toggle: the role is an ORDERING, not a number. In sink mode the lanes
            // have already capped out against a pool that included last tick's holdings, so whatever is
            // still idle is spare by construction — and no slider value can express that (0 starves
            // wishes, 100 starves the lanes).
            Assert.Equal(1000, WishShareView.Offer(sink: true, idle: 1000, sliderPercent: slider));
        }

        [Theory]
        [InlineData(100.0, 1000)]
        [InlineData(85.0, 850)]
        [InlineData(50.0, 500)]
        [InlineData(1.0, 10)]
        public void Priority_mode_takes_the_slider_share(double slider, long expected)
        {
            Assert.Equal(expected, WishShareView.Offer(sink: false, idle: 1000, sliderPercent: slider));
        }

        [Fact]
        public void A_zero_slider_in_priority_mode_really_allocates_nothing()
        {
            // 2.4.0 made 0% authoritative downward; sink mode must not quietly resurrect the old
            // "drink all residue regardless of the sliders" behaviour for priority users.
            Assert.Equal(0, WishShareView.Offer(sink: false, idle: 1_000_000, sliderPercent: 0));
        }

        [Fact]
        public void An_empty_pool_offers_nothing_in_either_role()
        {
            Assert.Equal(0, WishShareView.Offer(sink: true, idle: 0, sliderPercent: 100));
            Assert.Equal(0, WishShareView.Offer(sink: false, idle: 0, sliderPercent: 100));
            Assert.Equal(0, WishShareView.Offer(sink: true, idle: -5, sliderPercent: 100));
        }

        [Fact]
        public void The_offer_never_exceeds_the_pool_at_end_game_magnitudes()
        {
            // Above 2^53 the double product loses exactness and Ceiling can land one unit past the pool.
            // Pools legitimately exceed 1e18 under potions, so the clamp is load-bearing, not defensive.
            const long idle = 8_999_999_999_999_999_988L;
            Assert.True(WishShareView.Offer(sink: false, idle: idle, sliderPercent: 100.0) <= idle);
            Assert.True(WishShareView.Offer(sink: false, idle: idle, sliderPercent: 99.999) <= idle);
            Assert.Equal(idle, WishShareView.Offer(sink: true, idle: idle, sliderPercent: 0));
        }

        /// <summary>
        /// THE ~150% BUG. laneAllocated comes from the swap and IdleAtPass from the wish pass; the
        /// snapshot pairing them runs on its own timer, so a swap landing between the two reads pairs a
        /// fresh plan with a stale wish record and their sum falls BELOW a lane's own allocation. The
        /// denominator must bound every constraint lane too, not just the wish lane.
        /// </summary>
        [Fact]
        public void The_board_pool_is_never_smaller_than_the_plan_pool()
        {
            // A stale record: the wish pass saw almost nothing idle...
            WishShareView.Record(offeredEnergy: 10, takenEnergy: 10, idleEnergyAtPass: 10,
                                 offeredMagic: 0, takenMagic: 0, idleMagicAtPass: 0);

            // ...while the plan it is paired with allocated from 9e18 and one lane took 3.3e18.
            const long planPool = 8_999_999_999_999_999_978L;
            const long laneAllocated = 2_200_000_000_000_000_000L;   // an under-reporting tick
            const long oneLaneTook = 3_300_454_254_841_539_072L;     // CAPNGU-8, measured live

            var pool = WishShareView.Energy.BoardPool(laneAllocated, planPool);

            Assert.Equal(planPool, pool);
            Assert.True(oneLaneTook <= pool,
                "a constraint lane must never exceed the pool the board draws it against");
        }

        [Fact]
        public void The_board_pool_still_widens_past_the_plan_pool_when_wishes_held_more()
        {
            // The 300% case must keep working: in priority mode the plan pool EXCLUDES the holdings
            // the wish pass released, so the sum is the larger and correct denominator.
            const long planPool = 10_000_000_000_000_000L;           // ~1e16, the collapsed pool
            const long idleAtPass = 8_100_000_000_000_000_000L;      // ~8.1e18 released back
            WishShareView.Record(idleAtPass, idleAtPass, idleAtPass, 0, 0, 0);

            var pool = WishShareView.Energy.BoardPool(planPool, planPool);

            Assert.True(pool > planPool);
            Assert.True(WishShareView.Energy.Taken <= pool);
        }

        [Fact]
        public void Reset_forgets_both_pools()
        {
            WishShareView.Record(1, 1, 1, 1, 1, 1);
            WishShareView.Reset();

            Assert.False(WishShareView.Energy.Recorded);
            Assert.False(WishShareView.Magic.Recorded);
        }
    }
}
