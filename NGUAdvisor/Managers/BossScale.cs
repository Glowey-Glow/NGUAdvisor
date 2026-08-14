using System;

namespace NGUAdvisor.Managers
{
    // Unity-free boss-progression scale + zone-unlock gate (audit M5 — first strangler slice of the
    // decision core).
    //
    // WHY THIS EXISTS — and why it is a plain static with no Character/Unity reference:
    // The recurring "Evil" bugs (boost/gear farm silently falling back to ITOPOD, ceilings misfiring)
    // were all the SAME mistake: comparing the difficulty-LOCAL raw bossID against the ZoneUnlocks
    // table, which is on the effectiveBossID scale (Normal +0 / Evil +301 / Sadistic +602 — mirrors
    // Character.effectiveBossID()). That logic lived only inside managers welded to the live Character,
    // so the ONLY way to catch a regression was to launch the game. Pulling the arithmetic and the gate
    // out here — taking primitives, referencing nothing Unity — lets NGUAdvisor.Tests exercise it
    // headlessly for Normal AND Evil in CI. In-game play is no longer the only safety net for this
    // logic. This is the template the rest of the decision core follows: pure function in, fixture in a
    // test, call site delegates to it.
    public enum GameDifficulty { Normal = 0, Evil = 1, Sadistic = 2 }

    public static class BossScale
    {
        // Added to the difficulty-local bossID to reach the global effectiveBossID the zone/titan
        // tables are indexed on. Mirrors Character.effectiveBossID(): +301 at Evil, +301 again at
        // Sadistic (so Sadistic totals +602).
        public const int EvilStep = 301;

        public static int OffsetFor(GameDifficulty difficulty)
        {
            switch (difficulty)
            {
                case GameDifficulty.Sadistic: return EvilStep * 2;
                case GameDifficulty.Evil: return EvilStep;
                default: return 0;
            }
        }

        // The global boss number the zone/titan tables compare against. Gating MUST use this, never the
        // raw difficulty-local bossID.
        public static int EffectiveBossId(int rawBossId, GameDifficulty difficulty)
            => rawBossId + OffsetFor(difficulty);

        // Single source of truth for "has the player's boss progress unlocked this zone?" — used by
        // every farm/ceiling gate. Caller passes the EFFECTIVE boss id (e.g. Character.effectiveBossID()).
        // Out-of-range or a missing table reads as locked, never a crash.
        public static bool IsZoneUnlocked(int effectiveBossId, int zone, int[] zoneUnlocks)
        {
            if (zoneUnlocks == null || zone < 0 || zone >= zoneUnlocks.Length)
                return false;
            return effectiveBossId >= zoneUnlocks[zone];
        }

        // Highest EFFECTIVE boss number that still unlocks a zone AT THIS DIFFICULTY.
        //
        // The previous rule scanned zones 0..TitanZones[DifficultyMaxTitanIndex()] — the ceiling TITAN's
        // zone — on the assumption that the last titan of an era is also its last unlock. It is not:
        //   Evil   ceiling titan 8 = THE EXILE, zone 30, threshold 491 … but The Rad-Lands is zone 31 at
        //          501, so the ceiling came out TEN bosses short.
        //   Normal ceiling titan 5 = zone 19, threshold 132 … but Chocolate World is zone 20 at 137.
        // Being short makes IsPastBossCeiling flip true early, which announces "unlocks done at this
        // difficulty" while a zone is still ahead and prematurely trips the digger/EXP-farm promotion
        // that hangs off the same flag.
        //
        // Bound by DIFFICULTY BAND instead, which is what the ZoneUnlocks table is actually built on: a
        // zone belongs to this era when its threshold falls in [offset, offset + EvilStep). That is
        // independent of where the titans happen to sit, so a future table edit cannot re-open this.
        public static int BossUnlockCeiling(int[] zoneUnlocks, GameDifficulty difficulty)
        {
            if (zoneUnlocks == null) return 0;
            int lo = OffsetFor(difficulty);
            int hi = lo + EvilStep;               // exclusive — the next era's band starts here
            int best = 0;
            for (int z = 0; z < zoneUnlocks.Length; z++)
            {
                int thr = zoneUnlocks[z];
                if (thr >= lo && thr < hi && thr > best)
                    best = thr;
            }
            return best;
        }

        // "Past the boss-unlock ceiling": the player has cleared the highest zone this difficulty
        // unlocks, so further boss progress is EXP-only (the guide's NUMBER-ritual phase). ceiling <= 0
        // means "no known ceiling" (early game, or BossUnlockCeiling() hit its catch) and is NOT past —
        // the guard matters, since without it effectiveBossId-1 >= 0 reads as "past" for almost any
        // progress. Matches OptimizationAdvisor's canonical guarded check and unifies three call sites
        // that had dropped the guard.
        public static bool IsPastBossCeiling(int effectiveBossId, int ceiling)
            => ceiling > 0 && Math.Max(0, effectiveBossId - 1) >= ceiling;
    }
}
