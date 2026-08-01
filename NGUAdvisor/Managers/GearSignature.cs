using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // The pure half of GearWatch (same split as WandoosMath/WandoosAdvisor and DiggerMath/DiggerManager):
    // a hash of "which items do I own", with no Unity dependency so the invariants the drop trigger
    // relies on are actually tested rather than asserted in a comment.
    //
    // The invariants, and why each one matters:
    //  - ORDER-INDEPENDENT. Slot order shuffles constantly and carries no meaning here.
    //  - PERMUTATION-INVARIANT, which is the load-bearing one: LoadoutManager.ChangeGear only MOVES
    //    items between equipped / inventory / daycare, so as long as the caller hashes the UNION, the
    //    advisor's own swaps cannot change the signature and therefore cannot re-trigger themselves.
    //  - COUNT-SENSITIVE, so a merge ({a,a,b} -> {a,b}) is seen even though the id SET is unchanged.
    //  - ID-ONLY. Levels are deliberately not hashed: the advisor boosts gear continuously and
    //    GameGearAdapter scales stats by level, so hashing levels would change every few seconds
    //    forever and the "trigger" would be a busy loop.
    public static class GearSignature
    {
        private const ulong MixId = 0x9E3779B97F4A7C15UL;   // golden-ratio odd constant (Fibonacci hashing)
        private const ulong MixCount = 0xC2B2AE3D27D4EB4FUL;

        // Avalanche one id before it joins the sum. This is NOT decoration: summing `id * constant`
        // is linear (M*a + M*b == M*(a+b)), so every inventory with the same id-total would collide —
        // {1,2,6} and {1,3,5} would be indistinguishable. Mixing each id first breaks that while
        // keeping the sum's order-independence, which is the property the trigger actually needs.
        // (splitmix64's finalizer.)
        private static ulong Avalanche(ulong x)
        {
            unchecked
            {
                x *= MixId;
                x ^= x >> 29;
                x *= 0xBF58476D1CE4E5B9UL;
                x ^= x >> 32;
                x *= 0x94D049BB133111EBUL;
                x ^= x >> 31;
                return x;
            }
        }

        // id 0 means "empty slot" in the game's collections and is ignored.
        public static ulong Compute(IEnumerable<int> ids)
        {
            unchecked
            {
                ulong h = 0;
                int n = 0;
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        if (id == 0) continue;
                        h += Avalanche((ulong)id);
                        n++;
                    }
                }
                return h ^ ((ulong)n * MixCount);
            }
        }
    }
}
