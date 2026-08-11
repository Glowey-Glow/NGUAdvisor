using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE GAME'S OWN ITEM-LIST SETS. Unity-free data, linked into the test project.
    //
    // ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
    // [OPERATOR] 2026-08-05: "the only gear set that I currently have that isn't maxxed and completely
    // boosted is Pretty Pink Princess Land. So I'm curious as to why the GearFarmAdvisor even
    // considers Chocolate World or Evil Verse."
    //
    // They were right, and it took two wrong guesses to find out why. GearFarmAdvisor rated a zone by
    // every un-maxxed equipment id in its DROP TABLE, and HoursToCap takes the WORST of them
    // (GearFarmAdvisor.cs:341). Measured live:
    //   Chocolate World  Energy Bar Bar x42 ~54.5h, Magic Bar Bar x27 ~35h   (set 221-225 COMPLETE)
    //   The Evilverse    BOTH Edgy Boots x99 ~642h                           (set complete)
    //   PPP              the whole 231-236 set + Creepy Doll                 (genuinely unfinished)
    // Items 220, 226 and 227 belong to NO set at all. The zone sets were finished exactly as the
    // operator said; the zones were being rated by set-less strays.
    //
    // TWO EARLIER DISCRIMINATORS WERE TRIED AND BOTH FAILED, which is why this one reads the game:
    //   - "is it a cross-zone chain item" (ItemChains): 142 was already maxxed on that save, so the
    //     split correctly excluded nothing and the estimate moved 54.6h -> 54.5h.
    //   - "is its rate 20x below the zone's common roll": 220 runs only ~5.9x slower than the
    //     Evilverse set rolls, well inside the bar. Rate does not separate these.
    // Set membership does, and the game states it directly rather than leaving it to be inferred.
    //
    // ── PROVENANCE ────────────────────────────────────────────────────────────────────────────────
    // Generated from [DECOMP] ItemList.cs by extracting every `public bool maxxedXxx()` function and
    // taking the itemMaxxed[] indices it tests. 69 functions, 326 distinct member ids. NOT
    // transcribed by hand — the table below is script output, because a mistyped id here silently
    // moves an item between "rank this zone on it" and "do not", which is the whole defect.
    //
    // ⚠ A SET IS NOT A ZONE. Sets and zones do not correspond one-to-one: maxxedEdgyBoots is its own
    // two-item set {216, 219} sitting inside the Evilverse's drop table, maxxedNormalBonusAcc spans
    // thirteen ids across a dozen zones, and several sets are a single item. Do not derive a zone
    // from a set or vice versa.
    public static class ItemSets
    {
        // setName -> the itemMaxxed[] indices that function tests, in source order.
        private static readonly Dictionary<string, int[]> Sets = new Dictionary<string, int[]>
        {            { "maxxedTraining", new[] { 62, 63, 64, 65, 75 } },
            { "maxxedSewers", new[] { 40, 41, 42, 43, 44, 45, 46 } },
            { "maxxedForest", new[] { 47, 48, 49, 50, 51, 52, 53 } },
            { "maxxedCave", new[] { 54, 55, 56, 57, 58, 59, 60, 61 } },
            { "maxxedHSB", new[] { 68, 69, 70, 71, 72, 73, 74 } },
            { "maxxedGRB", new[] { 78, 79, 80, 81, 82, 83, 84 } },
            { "maxxedClock", new[] { 85, 86, 87, 88, 89, 90, 91 } },
            { "maxxed2D", new[] { 95, 96, 97, 98, 99, 100, 101 } },
            { "maxxedGhost", new[] { 103, 104, 105, 106, 107, 108, 109 } },
            { "maxxedJake", new[] { 111, 112, 113, 114, 115, 116, 117 } },
            { "maxxedGaudy", new[] { 122, 123, 124, 125, 126 } },
            { "maxxedMega", new[] { 130, 131, 132, 133, 134 } },
            { "maxxedWandoos", new[] { 66 } },
            { "maxxedNumber", new[] { 102 } },
            { "maxxedTutorialCube", new[] { 77 } },
            { "maxxedFlubber", new[] { 121 } },
            { "maxxedSeed", new[] { 92 } },
            { "maxxedUUG", new[] { 141 } },
            { "maxxedRingUUG", new[] { 136, 137, 138, 139, 140 } },
            { "maxxedBeardverse", new[] { 143, 144, 145, 146, 147 } },
            { "maxxedRedLiquid", new[] { 93 } },
            { "maxxedWaldo", new[] { 150, 151, 152, 153 } },
            { "maxxedAntiWaldo", new[] { 155, 156, 157, 158 } },
            { "maxxedBadlyDrawn", new[] { 164, 165, 166, 167, 168 } },
            { "maxxedStealth", new[] { 173, 174, 175, 176, 177 } },
            { "maxxedBeast1", new[] { 184, 185, 186, 187, 188 } },
            { "maxxedEdgy", new[] { 213, 214, 215, 217, 218 } },
            { "maxxedEdgyBoots", new[] { 216, 219 } },
            { "maxxedBrownHeart", new[] { 162 } },
            { "maxxedXL", new[] { 163 } },
            { "maxxedGreenHeart", new[] { 171 } },
            { "maxxedItopodKey", new[] { 172 } },
            { "maxxedPurpleLiquid", new[] { 191 } },
            { "maxxedBlueHeart", new[] { 196 } },
            { "maxxedJakeNote", new[] { 197 } },
            { "maxxedPurpleHeart", new[] { 212 } },
            { "maxxedGreyHeart", new[] { 297 } },
            { "maxxedPinkHeart", new[] { 344 } },
            { "maxxedChoco", new[] { 221, 222, 223, 224, 225 } },
            { "maxxedPretty", new[] { 231, 232, 233, 234, 235, 236 } },
            { "maxxedNerd", new[] { 237, 238, 239, 240, 241 } },
            { "maxxedMeta", new[] { 251, 252, 253, 254, 255, 256, 257 } },
            { "maxxedParty", new[] { 258, 259, 260, 261, 262, 263, 264 } },
            { "maxxedGodmother", new[] { 265, 266, 267, 268, 269, 270, 271 } },
            { "maxxedOrangeHeart", new[] { 293 } },
            { "maxxedHeroicSigil", new[] { 292 } },
            { "maxxedEvidence", new[] { 294 } },
            { "maxxedSeveredHead", new[] { 343 } },
            { "maxxedTypo", new[] { 301, 302, 303, 304, 305, 306, 307 } },
            { "maxxedFad", new[] { 308, 309, 310, 311, 312, 313, 314 } },
            { "maxxedJRPG", new[] { 315, 316, 317, 318, 319, 320, 321 } },
            { "maxxedExile", new[] { 322, 323, 324, 325, 326 } },
            { "maxxedRad", new[] { 345, 346, 347, 348, 349, 350, 351 } },
            { "maxxedSchool", new[] { 352, 353, 354, 355, 356, 357, 358 } },
            { "maxxedWestern", new[] { 359, 360, 361, 362, 363, 364, 365 } },
            { "maxxedSpace", new[] { 373, 374, 375, 376, 377, 378, 379 } },
            { "maxxedRainbowHeart", new[] { 390 } },
            { "maxxedBeatingHeart", new[] { 391 } },
            { "maxxedBread", new[] { 392, 393, 394, 395, 396, 397, 398, 399 } },
            { "maxxed70sZone", new[] { 400, 401, 402, 403, 404, 405, 406, 407 } },
            { "maxxedHalloweenies", new[] { 408, 409, 410, 411, 412, 413, 414, 415 } },
            { "maxxedRockLobster", new[] { 416, 417, 418, 419, 420, 421, 422, 423 } },
            { "maxxedConstruction", new[] { 453, 454, 455, 456, 457, 458, 459, 460 } },
            { "maxxedDuck", new[] { 496, 497, 498, 499, 500, 501, 502, 503 } },
            { "maxxedNether", new[] { 461, 462, 463, 464, 465, 466, 467, 468 } },
            { "maxxedAmalgamate", new[] { 469, 470, 471, 472, 473, 474, 475, 476 } },
            { "maxxedPirate", new[] { 507, 508, 509, 510, 511, 512, 513, 514 } },
            { "maxxedNormalBonusAcc", new[] { 432, 433, 434, 435, 436, 437, 438, 439, 440, 441, 442, 443, 444 } },
            { "maxxedEvilBonusAcc", new[] { 445, 446, 447, 448, 449, 450, 451, 452 } },

        };

        private static readonly HashSet<int> Members = BuildMembers();

        private static HashSet<int> BuildMembers()
        {
            var h = new HashSet<int>();
            foreach (var kv in Sets)
                foreach (var id in kv.Value)
                    h.Add(id);
            return h;
        }

        // The discriminator. True when the game counts this id toward some item-list set completion.
        public static bool IsSetMember(int id) => Members.Contains(id);

        // The set this id belongs to, or null. First match wins; no id is in two sets (asserted).
        public static string SetOf(int id)
        {
            foreach (var kv in Sets)
                foreach (var m in kv.Value)
                    if (m == id) return kv.Key;
            return null;
        }

        public static int[] MembersOf(string set)
            => set != null && Sets.TryGetValue(set, out var ids) ? ids : new int[0];

        public static IEnumerable<string> AllSets()
        {
            foreach (var kv in Sets) yield return kv.Key;
        }

        public static int SetCount => Sets.Count;

        public static int MemberCount => Members.Count;
    }
}
