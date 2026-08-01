using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The hash behind the "re-optimize when new gear drops" trigger. Its invariants are what stop the
    // trigger firing on the advisor's own gear swaps — get one wrong and the feature becomes a loop
    // that re-equips forever, and every equip zeroes the player's energy/magic/R3 allocation.
    public class GearSignatureTests
    {
        [Fact]
        public void OrderDoesNotMatter()
        {
            // Slot order shuffles constantly and means nothing.
            Assert.Equal(GearSignature.Compute(new[] { 1, 2, 3 }),
                         GearSignature.Compute(new[] { 3, 1, 2 }));
        }

        [Fact]
        public void MovingAnItemBetweenCollections_DoesNotChangeTheSignature()
        {
            // THE LOAD-BEARING ONE. ChangeGear only MOVES items between equipped / inventory / daycare.
            // Callers hash the UNION, so a swap is just a permutation and must be invisible here —
            // otherwise the advisor's own equip re-triggers the watch and it never settles.
            var equippedThenBag = new[] { 10, 20 }.Concat(new[] { 30, 40, 0, 0 });
            var afterSwap = new[] { 30, 20 }.Concat(new[] { 10, 40, 0, 0 });   // 10 <-> 30 swapped
            Assert.Equal(GearSignature.Compute(equippedThenBag), GearSignature.Compute(afterSwap));
        }

        [Fact]
        public void ANewItemChangesTheSignature()
        {
            Assert.NotEqual(GearSignature.Compute(new[] { 1, 2, 3 }),
                            GearSignature.Compute(new[] { 1, 2, 3, 4 }));
        }

        [Fact]
        public void RemovingAnItemChangesTheSignature()
        {
            Assert.NotEqual(GearSignature.Compute(new[] { 1, 2, 3 }),
                            GearSignature.Compute(new[] { 1, 2 }));
        }

        [Fact]
        public void AMergeIsSeen_EvenThoughTheIdSetIsUnchanged()
        {
            // Two copies of item 7 merge into one. The SET {7,9} is identical before and after, so a
            // set-only hash would miss the upgrade users notice most. The count term catches it.
            Assert.NotEqual(GearSignature.Compute(new[] { 7, 7, 9 }),
                            GearSignature.Compute(new[] { 7, 9 }));
        }

        [Fact]
        public void EmptySlotsAreIgnored()
        {
            // id 0 is the game's "nothing here".
            Assert.Equal(GearSignature.Compute(new[] { 5, 6 }),
                         GearSignature.Compute(new[] { 0, 5, 0, 6, 0 }));
        }

        [Fact]
        public void EmptyAndNullAreStableAndEqual()
        {
            Assert.Equal(GearSignature.Compute(new int[0]), GearSignature.Compute(null));
        }

        [Fact]
        public void LevelsAreNotPartOfTheInput()
        {
            // Documenting the contract at the boundary: the signature takes IDs only. The advisor boosts
            // gear continuously, so if a caller ever folded .level in, this hash would change every few
            // seconds forever and the trigger would degenerate into a busy loop.
            var ids = new List<int> { 100, 200, 300 };
            var again = new List<int> { 100, 200, 300 };
            Assert.Equal(GearSignature.Compute(ids), GearSignature.Compute(again));
        }

        [Fact]
        public void DistinctInventoriesGetDistinctSignatures()
        {
            // Not a cryptographic claim — just that ordinary NGU-sized inventories don't collide.
            var seen = new Dictionary<ulong, string>();
            for (int a = 1; a <= 40; a++)
            for (int b = a + 1; b <= 40; b++)
            for (int c = b + 1; c <= 40; c++)
            {
                var sig = GearSignature.Compute(new[] { a, b, c });
                var key = $"{a},{b},{c}";
                Assert.False(seen.ContainsKey(sig), $"collision: {key} vs {(seen.ContainsKey(sig) ? seen[sig] : "")}");
                seen[sig] = key;
            }
        }
    }
}
