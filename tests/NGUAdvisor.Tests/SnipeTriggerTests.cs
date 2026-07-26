using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless guard for the gold-snipe re-trigger decision (audit M5 migration). Locks the "one arm per
    // zone" latch (the fix for a completed snipe being wiped every second), the reload-seed path, and the
    // new-zone-over-timer priority.
    public class SnipeTriggerTests
    {
        // helper: the common "armed, has a best zone" call with the rest defaulted to no-timer
        private static SnipeResult NewZone(int best, int furthest, int lastArmed, bool complete = true)
            => SnipeTrigger.Decide(armNewZone: true, hasBest: true, bestZone: best,
                furthestZone: furthest, lastNewZoneTrigger: lastArmed, snipeComplete: complete,
                allowTimer: false, timerHit: false);

        [Fact]
        public void New_zone_fires_once_when_best_exceeds_baseline_and_last_armed()
        {
            var r = NewZone(best: 8, furthest: 5, lastArmed: 0);
            Assert.Equal("new zone fightable", r.Trigger);
            Assert.Equal(8, r.NewZone);
            Assert.False(r.SeedBaseline);
        }

        [Fact]
        public void New_zone_does_not_re_fire_for_a_zone_already_armed()
        {
            // The latch: best==8 but zone 8 already armed -> no trigger (this is the wiped-every-second fix).
            Assert.Null(NewZone(best: 8, furthest: 5, lastArmed: 8).Trigger);
        }

        [Fact]
        public void New_zone_does_not_fire_when_best_is_not_beyond_the_baseline()
        {
            Assert.Null(NewZone(best: 5, furthest: 5, lastArmed: 0).Trigger);
        }

        [Fact]
        public void Reload_seeds_the_baseline_silently_when_unknown_and_complete()
        {
            var r = NewZone(best: 12, furthest: -1, lastArmed: 0, complete: true);
            Assert.True(r.SeedBaseline);
            Assert.Null(r.Trigger);           // seeding is silent, not a snipe
        }

        [Fact]
        public void Unknown_baseline_but_not_complete_does_nothing()
        {
            var r = NewZone(best: 12, furthest: -1, lastArmed: 0, complete: false);
            Assert.False(r.SeedBaseline);
            Assert.Null(r.Trigger);
        }

        [Fact]
        public void Seeding_does_not_suppress_the_timer()
        {
            // Reload-seed AND a manual timer hit in the same pass: both happen (the original falls through).
            var r = SnipeTrigger.Decide(armNewZone: true, hasBest: true, bestZone: 12,
                furthestZone: -1, lastNewZoneTrigger: 0, snipeComplete: true,
                allowTimer: true, timerHit: true);
            Assert.True(r.SeedBaseline);
            Assert.Equal("timer", r.Trigger);
        }

        [Fact]
        public void New_zone_takes_priority_over_the_timer()
        {
            var r = SnipeTrigger.Decide(armNewZone: true, hasBest: true, bestZone: 8,
                furthestZone: 5, lastNewZoneTrigger: 0, snipeComplete: true,
                allowTimer: true, timerHit: true);
            Assert.Equal("new zone fightable", r.Trigger);   // not "timer"
        }

        [Fact]
        public void Timer_fires_when_armed_and_hit_with_no_new_zone()
        {
            var r = SnipeTrigger.Decide(armNewZone: false, hasBest: false, bestZone: -1,
                furthestZone: 5, lastNewZoneTrigger: 0, snipeComplete: true,
                allowTimer: true, timerHit: true);
            Assert.Equal("timer", r.Trigger);
        }

        [Fact]
        public void Nothing_fires_when_disarmed_and_no_timer()
        {
            var r = SnipeTrigger.Decide(false, false, -1, 5, 0, true, false, false);
            Assert.Null(r.Trigger);
            Assert.False(r.SeedBaseline);
        }
    }
}
