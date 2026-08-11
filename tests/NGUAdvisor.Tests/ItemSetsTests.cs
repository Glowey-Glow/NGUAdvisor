using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE GAME'S OWN ITEM-LIST SETS — now the discriminator for what rates a gear-farm zone.
    //
    // [OPERATOR] 2026-08-05: "the only gear set I have that isn't maxxed is Pretty Pink Princess
    // Land, so why does GearFarmAdvisor even consider Chocolate World or Evil Verse."
    //
    // Measured live, after the per-item breakdown was finally made to print:
    //   Chocolate World  Energy Bar Bar x42 ~54.5h, Magic Bar Bar x27 ~35h   (set 221-225 COMPLETE)
    //   The Evilverse    BOTH Edgy Boots x99 ~642h                           (set COMPLETE)
    //   PPP              the whole 231-236 set + Creepy Doll                 (genuinely unfinished)
    //
    // Two earlier discriminators failed on that same data and both are pinned below so they cannot
    // quietly come back: chain membership (142 was already maxxed, so it excluded nothing) and a 20x
    // rate bar (220 is only ~5.9x slower than the Evilverse set rolls).
    public class ItemSetsTests
    {
        // ── the three ids that caused the report ──────────────────────────────────────────────────

        [Theory]
        [InlineData(220)]   // BOTH Edgy Boots  — Evilverse
        [InlineData(226)]   // Energy Bar Bar   — Chocolate World
        [InlineData(227)]   // Magic Bar Bar    — Chocolate World
        public void The_set_less_strays_belong_to_no_set(int id)
        {
            Assert.False(ItemSets.IsSetMember(id));
            Assert.Null(ItemSets.SetOf(id));
        }

        // ...and the sets those zones DO have are entirely separate ids, which is why the operator
        // could correctly call them complete while the advisor called the zone uncapped.
        [Fact]
        public void Chocolate_worlds_set_is_221_to_225_and_excludes_the_bar_bars()
        {
            Assert.Equal(new[] { 221, 222, 223, 224, 225 }, ItemSets.MembersOf("maxxedChoco"));
            foreach (var id in new[] { 221, 222, 223, 224, 225 })
                Assert.Equal("maxxedChoco", ItemSets.SetOf(id));
            Assert.DoesNotContain(226, ItemSets.MembersOf("maxxedChoco"));
            Assert.DoesNotContain(227, ItemSets.MembersOf("maxxedChoco"));
        }

        // [DECOMP] ItemList.cs:511-513 — note it tests 213,214,215,217,218 and NOT 216 or 219.
        [Fact]
        public void The_evilverse_set_skips_216_and_219_which_form_their_own_set()
        {
            Assert.Equal(new[] { 213, 214, 215, 217, 218 }, ItemSets.MembersOf("maxxedEdgy"));
            // ⚠ A SET IS NOT A ZONE: this two-item set sits inside the Evilverse's drop table.
            Assert.Equal(new[] { 216, 219 }, ItemSets.MembersOf("maxxedEdgyBoots"));
            Assert.True(ItemSets.IsSetMember(216));
            Assert.True(ItemSets.IsSetMember(219));
            // ...but "BOTH Edgy Boots" is in neither.
            Assert.False(ItemSets.IsSetMember(220));
        }

        [Fact]
        public void PPPs_set_is_the_one_that_is_genuinely_unfinished()
        {
            Assert.Equal(new[] { 231, 232, 233, 234, 235, 236 }, ItemSets.MembersOf("maxxedPretty"));
        }

        // ── the two failed discriminators, pinned ─────────────────────────────────────────────────

        // Item 142 is a chain link and belongs to no set. It was already maxxed on the operator's
        // save, so the chain split excluded nothing and the estimate moved 54.6h -> 54.5h.
        [Fact]
        public void Chain_membership_alone_would_not_have_caught_the_strays()
        {
            Assert.True(ItemChains.IsChainItem(142));
            foreach (var id in new[] { 220, 226, 227 })
                Assert.False(ItemChains.IsChainItem(id));   // the chain list misses all three
        }

        // 220 is ~5.9x slower than the Evilverse set rolls — comfortably inside the 20x bar, so a
        // rate heuristic classifies it as ordinary set gear. Rate does not separate these.
        [Fact]
        public void The_twenty_times_rate_bar_would_not_have_caught_220()
            => Assert.False(ItemChains.IsRareInZone(rate: 0.154, bestRate: 0.916));

        // ── table integrity ───────────────────────────────────────────────────────────────────────
        // Generated from [DECOMP] ItemList.cs, not transcribed — a mistyped id silently moves an item
        // between "rank the zone on it" and "do not", which is the entire defect being fixed.

        [Fact]
        public void The_table_matches_the_decomp_extraction()
        {
            Assert.Equal(69, ItemSets.SetCount);
            Assert.Equal(326, ItemSets.MemberCount);
        }

        [Fact]
        public void No_id_belongs_to_two_sets()
        {
            var seen = new System.Collections.Generic.Dictionary<int, string>();
            foreach (var s in ItemSets.AllSets())
                foreach (var id in ItemSets.MembersOf(s))
                {
                    Assert.False(seen.ContainsKey(id),
                        $"item {id} is in both {(seen.TryGetValue(id, out var f) ? f : "?")} and {s}");
                    seen[id] = s;
                }
        }

        [Fact]
        public void Every_member_id_is_a_plausible_item_id()
        {
            foreach (var s in ItemSets.AllSets())
            {
                Assert.NotEmpty(ItemSets.MembersOf(s));
                foreach (var id in ItemSets.MembersOf(s))
                {
                    Assert.True(id > 0);
                    // 514 == Consts.MAX_GEAR_ID (Consts.cs:8); inlined because Consts is not linked here.
                    Assert.True(id <= 514, $"{s} carries id {id} past MAX_GEAR_ID");
                }
            }
        }

        [Fact]
        public void An_unknown_set_name_yields_an_empty_list_rather_than_throwing()
        {
            Assert.Empty(ItemSets.MembersOf("maxxedNotAThing"));
            Assert.Empty(ItemSets.MembersOf(null));
        }

        // Single-item sets are real and must not be pruned as degenerate — maxxedWandoos is item 66
        // alone, which is also one of the Misc ids QuestManager.cs:32-39 records as un-maxxable.
        [Fact]
        public void Single_item_sets_exist()
        {
            Assert.Equal(new[] { 66 }, ItemSets.MembersOf("maxxedWandoos"));
            Assert.True(ItemSets.AllSets().Count(s => ItemSets.MembersOf(s).Length == 1) > 10);
        }
    }
}
