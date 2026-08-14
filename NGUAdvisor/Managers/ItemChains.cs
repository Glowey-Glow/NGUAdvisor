using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE TWO CROSS-ZONE ITEM CHAINS, and the rarity split that keeps them from distorting a zone's
    // gear-farm rating. Unity-free — plain data plus predicates, linked into the test project.
    //
    // ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
    // [OPERATOR], 2026-08-05: "the only gear set that I currently have that isn't maxxed and
    // completely boosted is Pretty Pink Princess Land. So I'm curious as to why the GearFarmAdvisor
    // even considers Chocolate World or Evil Verse."
    //
    // Because a zone's DROP TABLE is not its SET. [DECOMP] ItemList.cs:577-584 defines Chocolate
    // World's set as exactly {221,222,223,224,225}, but GearFarmAdvisor's zone-20 table also carries
    // 226, 227, 444 and 142 — none of which are in maxxedChoco(). Analyze counted any un-maxxed
    // equipment id (GearFarmAdvisor.cs:378-386), so a zone whose set was finished still reported
    // "uncapped" on the strength of a shared pendant.
    //
    // Worse, HoursToCap takes the zone's WORST item (GearFarmAdvisor.cs:341, Math.Max), so one
    // cross-zone ultra-rare set the rating for the whole zone. That is what produced "fastest is
    // Chocolate World (~54.6h)" for a zone whose own set was already complete, and it is what pushed
    // all three candidates past the 3h budget and silenced the phase machine.
    //
    // ── THE CHAINS ARE EXACTLY THESE TWO, AND THAT IS A MEASURED RESULT ───────────────────────────
    // Every equipment id that appears in more than one zone's drop table is a Looty or a Pendant —
    // checked across all 32 zones of GearFarmAdvisor.Table against [DECOMP] ItemNameDesc.cs
    // constructItemInfo() slot types. There is no third chain. Ids 169 and 170 drop in zone 31 only,
    // so the cross-zone test alone misses them; they are chain members by name, description and
    // effect, and are listed here on that basis.
    //
    // ── WHAT THEY DO, AND WHY THEY DESERVE THEIR OWN TRACK ────────────────────────────────────────
    // [DECOMP] ItemNameDesc.cs constructItemInfo():
    //   Looty 67, 128            specType.Looting
    //   Looty 169, 230, 296      specType.Looting2 + AllPower
    //   Pendant 76, 94, 142      specType.Looting + GoldDropAmount
    //   Pendant 170              specType.QuestDrop + AllCap
    //   Pendant 229              specType.NGU2 + AllCap
    //   Pendant 295              specType.Res3Cap + Res3Power
    // Most of both chains is DROP CHANCE, which compounds: every merge makes all later farming
    // faster. Note the pendant chain CHANGES CHARACTER at 170 — it stops being a Looting item — so
    // "the pendant chain" is not one homogeneous reward. Item 53 has no spec at all ("What a useless
    // piece of junk!"), and is the chain's base.
    public static class ItemChains
    {
        public const string Pendant = "Pendant";
        public const string Looty = "Looty";

        // Ordered by tier. Index + 1 is the tier number.
        private static readonly Dictionary<string, int[]> Chains = new Dictionary<string, int[]>
        {
            // Forest Pendant -> Ascended -> Ascended^2 -> Ascended^3 -> x4 -> x5 -> x6
            { Pendant, new[] { 53, 76, 94, 142, 170, 229, 295 } },
            // Looty McLootFace -> Sir Looty McLootington III -> King -> Emperor -> GALACTIC HERALD
            { Looty, new[] { 67, 128, 169, 230, 296 } },
        };

        public static bool IsChainItem(int id) => ChainOf(id) != null;

        public static string ChainOf(int id)
        {
            foreach (var kv in Chains)
                for (int i = 0; i < kv.Value.Length; i++)
                    if (kv.Value[i] == id) return kv.Key;
            return null;
        }

        // 1-based position in its chain; 0 when the id is not a chain item.
        public static int TierOf(int id)
        {
            foreach (var kv in Chains)
                for (int i = 0; i < kv.Value.Length; i++)
                    if (kv.Value[i] == id) return i + 1;
            return 0;
        }

        public static int ChainLength(string chain)
            => chain != null && Chains.TryGetValue(chain, out var ids) ? ids.Length : 0;

        // "Pendant 4/7" — for surfacing, so a rare target reads as chain progress rather than a
        // bare item id.
        public static string Label(int id)
        {
            var c = ChainOf(id);
            return c == null ? null : $"{c} {TierOf(id)}/{ChainLength(c)}";
        }

        public static IEnumerable<int> All()
        {
            foreach (var kv in Chains)
                foreach (var id in kv.Value)
                    yield return id;
        }

        // ── THE RARITY SPLIT ──────────────────────────────────────────────────────────────────────
        // An item is held out of a zone's SET RATING when it is a chain item, or when its drop rate
        // is far below the zone's ordinary rolls.
        //
        // THE 20x FACTOR IS NOT NEW AND NOT ARBITRARY. It is the rule ZoneStatHelper already applies
        // to the same tables (ZoneStatHelper.cs:77-81): the recommended drop chance counts "rolls
        // within 20x of the zone's most common roll", and excludes "ultra-rare specials like the 0.8%
        // Ring of Apathy" because "capping those takes far more and is a choice, not a baseline".
        // The gear farm's per-zone rating is exactly the same kind of baseline, so it takes the same
        // exclusion rather than inventing a second threshold.
        public const double RareFactor = 20.0;

        // rate: this item's drops/hour in the zone. bestRate: the fastest item's drops/hour there.
        // A non-positive best rate makes the question meaningless, so nothing is rare by default —
        // fail OPEN here on purpose: wrongly calling an item rare would drop it out of the set
        // rating silently, which is the distortion this whole file exists to remove.
        public static bool IsRareInZone(double rate, double bestRate)
        {
            if (bestRate <= 0) return false;
            if (rate <= 0) return true;   // unobtainable at this DC: certainly not a baseline item
            return bestRate / rate > RareFactor;
        }
    }
}
