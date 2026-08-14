using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE TWO CROSS-ZONE ITEM CHAINS.
    //
    // [OPERATOR] 2026-08-05: "the only gear set that I currently have that isn't maxxed and completely
    // boosted is Pretty Pink Princess Land. So I'm curious as to why the GearFarmAdvisor even
    // considers Chocolate World or Evil Verse."
    //
    // Because a zone's DROP TABLE is not its SET. [DECOMP] ItemList.cs:577-584 makes Chocolate World's
    // set exactly {221..225}; GearFarmAdvisor's zone-20 table also carries 226, 227, 444 and 142.
    // HoursToCap takes the WORST item (GearFarmAdvisor.cs:341), so one shared pendant rated a zone
    // whose own set was already finished at 54.6h — and pushed every candidate past the 3h budget.
    public class ItemChainsTests
    {
        // Ids verified against [DECOMP] ItemNameDesc.cs constructItemInfo() names and descriptions.
        private static readonly int[] PendantChain = { 53, 76, 94, 142, 170, 229, 295 };
        private static readonly int[] LootyChain = { 67, 128, 169, 230, 296 };

        [Fact]
        public void The_pendant_chain_is_seven_links_in_ascension_order()
        {
            for (int i = 0; i < PendantChain.Length; i++)
            {
                Assert.Equal(ItemChains.Pendant, ItemChains.ChainOf(PendantChain[i]));
                Assert.Equal(i + 1, ItemChains.TierOf(PendantChain[i]));
            }
            Assert.Equal(7, ItemChains.ChainLength(ItemChains.Pendant));
        }

        [Fact]
        public void The_looty_chain_is_five_links_in_ascension_order()
        {
            for (int i = 0; i < LootyChain.Length; i++)
            {
                Assert.Equal(ItemChains.Looty, ItemChains.ChainOf(LootyChain[i]));
                Assert.Equal(i + 1, ItemChains.TierOf(LootyChain[i]));
            }
            Assert.Equal(5, ItemChains.ChainLength(ItemChains.Looty));
        }

        // The zone SETS must never be mistaken for chain items — they are exactly what the gear farm
        // is supposed to rank on. [DECOMP] ItemList.cs:577-584 (Choco) and :587-593 (PPP).
        [Theory]
        [InlineData(221)] [InlineData(222)] [InlineData(223)] [InlineData(224)] [InlineData(225)]
        [InlineData(231)] [InlineData(232)] [InlineData(233)] [InlineData(234)] [InlineData(235)] [InlineData(236)]
        [InlineData(226)] [InlineData(227)]
        public void Zone_set_items_are_not_chain_items(int id)
        {
            Assert.False(ItemChains.IsChainItem(id));
            Assert.Null(ItemChains.ChainOf(id));
            Assert.Equal(0, ItemChains.TierOf(id));
        }

        // The specific id that caused the report. It is in the drop table of zones 20, 21, 22, 24, 25,
        // 27, 28 and 29 — so leaving it in the set rating flags eight zones as "uncapped" on one item.
        [Fact]
        public void Item_142_is_a_chain_item_and_is_labelled_as_chain_progress()
        {
            Assert.True(ItemChains.IsChainItem(142));
            Assert.Equal(ItemChains.Pendant, ItemChains.ChainOf(142));
            Assert.Equal("Pendant 4/7", ItemChains.Label(142));
        }

        [Fact]
        public void Sir_looty_is_the_second_looty_link()
        {
            Assert.Equal("Looty 2/5", ItemChains.Label(128));
        }

        [Fact]
        public void A_non_chain_id_has_no_label()
        {
            Assert.Null(ItemChains.Label(221));
            Assert.Null(ItemChains.Label(444));
        }

        [Fact]
        public void The_two_chains_do_not_overlap_and_have_no_duplicates()
        {
            var all = ItemChains.All().ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Equal(12, all.Count);
            Assert.Empty(PendantChain.Intersect(LootyChain));
        }

        // ── the 20x rarity yardstick ──────────────────────────────────────────────────────────────
        // Not a new threshold: ZoneStatHelper.cs:77-81 already counts "rolls within 20x of the zone's
        // most common roll" and excludes "ultra-rare specials like the 0.8% Ring of Apathy".

        [Theory]
        [InlineData(10.0, 100.0, false)]   // 10x  — inside the baseline
        [InlineData(5.0, 100.0, false)]    // 20x  — the boundary itself is still baseline (> not >=)
        [InlineData(4.9, 100.0, true)]     // just past 20x
        [InlineData(1.0, 100.0, true)]     // 100x — plainly rare
        [InlineData(100.0, 100.0, false)]  // the fastest item itself
        public void An_item_far_below_the_zones_common_rate_is_rare(double rate, double best, bool rare)
            => Assert.Equal(rare, ItemChains.IsRareInZone(rate, best));

        [Fact]
        public void Exactly_twenty_times_slower_is_not_yet_rare()
            => Assert.False(ItemChains.IsRareInZone(5.0, 100.0));

        // An unobtainable item is certainly not a baseline item.
        [Fact]
        public void A_zero_rate_item_is_rare()
            => Assert.True(ItemChains.IsRareInZone(0, 100.0));

        // ⚠ FAIL OPEN, deliberately. Wrongly calling an item rare drops it out of the set rating
        // silently — the same class of distortion this file exists to remove, in the other direction.
        [Fact]
        public void With_no_yardstick_nothing_is_rare()
        {
            Assert.False(ItemChains.IsRareInZone(0, 0));
            Assert.False(ItemChains.IsRareInZone(1.0, -1.0));
        }
    }
}
