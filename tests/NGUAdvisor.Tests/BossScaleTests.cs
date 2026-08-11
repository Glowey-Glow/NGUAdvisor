using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless regression guard for the boss-progression scale + zone-unlock gate (audit M5, first
    // strangler slice). This is the exact logic class behind the recurring "Evil" bugs — boost/gear
    // farm falling back to ITOPOD because a raw, difficulty-LOCAL bossID was compared against the
    // effectiveBossID-scaled ZoneUnlocks table. Because BossScale is Unity-free, these run in CI, so a
    // regression here is caught without launching the game.
    public class BossScaleTests
    {
        [Theory]
        [InlineData(GameDifficulty.Normal, 60, 60)]
        [InlineData(GameDifficulty.Evil, 60, 361)]
        [InlineData(GameDifficulty.Sadistic, 60, 662)]
        public void EffectiveBossId_applies_the_difficulty_offset(GameDifficulty diff, int raw, int expected)
        {
            Assert.Equal(expected, BossScale.EffectiveBossId(raw, diff));
        }

        [Fact]
        public void OffsetFor_matches_the_game_scale()
        {
            Assert.Equal(0, BossScale.OffsetFor(GameDifficulty.Normal));
            Assert.Equal(301, BossScale.OffsetFor(GameDifficulty.Evil));
            Assert.Equal(602, BossScale.OffsetFor(GameDifficulty.Sadistic));
        }

        [Fact]
        public void Zone_unlocks_on_the_boundary_and_not_before()
        {
            var unlocks = new[] { 0, 0, 0, 0, 0, 100 };   // zone 5 requires effective boss 100
            Assert.False(BossScale.IsZoneUnlocked(99, 5, unlocks));
            Assert.True(BossScale.IsZoneUnlocked(100, 5, unlocks));   // >= boundary is unlocked
            Assert.True(BossScale.IsZoneUnlocked(101, 5, unlocks));
        }

        [Fact]
        public void Out_of_range_or_null_reads_as_locked_never_a_crash()
        {
            var unlocks = new[] { 0, 50 };
            Assert.False(BossScale.IsZoneUnlocked(9999, 2, unlocks));    // zone past the table
            Assert.False(BossScale.IsZoneUnlocked(9999, -1, unlocks));   // negative zone
            Assert.False(BossScale.IsZoneUnlocked(9999, 0, null));       // no table
        }

        // The exact shipped bug: on Evil, gating with the RAW bossID wrongly LOCKS a zone the player has
        // actually cleared, because ZoneUnlocks is on the effectiveBossID scale. The effective id gates
        // it correctly. This test fails if a future edit reverts to raw-bossID gating.
        [Fact]
        public void Evil_zone_gate_requires_effective_boss_id_not_raw()
        {
            var unlocks = new int[22];
            unlocks[21] = 350;                 // e.g. an Evil-era zone unlocking at effective boss 350
            const int rawBoss = 60;            // the player's difficulty-local boss counter on Evil

            int effective = BossScale.EffectiveBossId(rawBoss, GameDifficulty.Evil);   // 60 + 301 = 361
            Assert.True(BossScale.IsZoneUnlocked(effective, 21, unlocks));              // correct: unlocked

            // The old raw-bossID gate (the bug) would have locked it — lock in the fix.
            Assert.False(BossScale.IsZoneUnlocked(rawBoss, 21, unlocks));
        }

        // The real shipped ZoneUnlocks table, so these pin the actual numbers the advisor runs on.
        private static readonly int[] ShippedZoneUnlocks = {
            0, 7, 17, 37, 48, 58, 58, 66, 66, 74, 82, 82, 90, 100, 100, 108, 116, 116, 124, 132, 137, // Normal
            359, 401, 426, 459, 467, 467, 475, 483, 491, 491, 501,                                    // Evil
            727, 752, 777, 810, 818, 826, 826, 834, 842, 850, 850, 871, 897, 902};                    // Sadistic

        // The ceiling is the last ZONE unlock of the era, NOT the ceiling titan's zone. The old rule
        // scanned 0..TitanZones[maxTitanIndex] and came up short in both playable eras:
        //   Evil   stopped at THE EXILE (zone 30, 491) and missed The Rad-Lands (zone 31, 501).
        //   Normal stopped at zone 19 (132) and missed Chocolate World (zone 20, 137).
        // Short ceilings make IsPastBossCeiling flip true early — "unlocks done" while a zone is ahead.
        [Theory]
        [InlineData(GameDifficulty.Normal, 137)]     // Chocolate World, not 132
        [InlineData(GameDifficulty.Evil, 501)]       // The Rad-Lands, not 491
        [InlineData(GameDifficulty.Sadistic, 902)]   // THE TRAITOR
        public void BossUnlockCeiling_is_the_last_zone_of_the_era(GameDifficulty difficulty, int expected)
        {
            Assert.Equal(expected, BossScale.BossUnlockCeiling(ShippedZoneUnlocks, difficulty));
        }

        // Each era's ceiling must sit inside that era's own band — this is what stops a Normal ceiling
        // from picking up an Evil threshold (they are all in one flat table).
        [Theory]
        [InlineData(GameDifficulty.Normal)]
        [InlineData(GameDifficulty.Evil)]
        [InlineData(GameDifficulty.Sadistic)]
        public void BossUnlockCeiling_stays_inside_its_own_difficulty_band(GameDifficulty difficulty)
        {
            int lo = BossScale.OffsetFor(difficulty);
            int ceiling = BossScale.BossUnlockCeiling(ShippedZoneUnlocks, difficulty);
            Assert.InRange(ceiling, lo, lo + BossScale.EvilStep - 1);
        }

        [Fact]
        public void BossUnlockCeiling_handles_a_missing_table()
        {
            Assert.Equal(0, BossScale.BossUnlockCeiling(null, GameDifficulty.Evil));
            Assert.Equal(0, BossScale.BossUnlockCeiling(new int[0], GameDifficulty.Evil));
        }

        [Fact]
        public void PastBossCeiling_needs_a_positive_ceiling()
        {
            // ceiling <= 0 == "no known ceiling" -> never past it (the bug the 3 unguarded sites had:
            // effectiveBossId-1 >= 0 read as "past" for almost any progress).
            Assert.False(BossScale.IsPastBossCeiling(9999, 0));
            Assert.False(BossScale.IsPastBossCeiling(9999, -5));
        }

        [Fact]
        public void PastBossCeiling_compares_highest_cleared_boss_to_the_ceiling()
        {
            Assert.False(BossScale.IsPastBossCeiling(100, 100));   // cleared boss 99, ceiling 100 -> not yet
            Assert.True(BossScale.IsPastBossCeiling(101, 100));    // cleared boss 100 -> at/past ceiling
            Assert.True(BossScale.IsPastBossCeiling(150, 100));
        }

        [Fact]
        public void PastBossCeiling_is_false_at_zero_progress_without_underflow()
        {
            // effectiveBossId 0 -> Math.Max(0, -1) == 0; must not throw or wrap, just "not past".
            Assert.False(BossScale.IsPastBossCeiling(0, 5));
        }
    }
}
