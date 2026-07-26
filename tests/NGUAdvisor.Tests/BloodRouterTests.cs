using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless guard for the single-sink blood routing priority (audit M5 migration). Locks the ladder
    // that shipped bugs — the "Number >=" FLOOR outranking the in-run sinks (it was treated as a ceiling),
    // NUMBER as the default sink, idle only when ineligible — plus the lazy evaluation the caller relies
    // on (window read only when needed, loot probed only after gold declines).
    public class BloodRouterTests
    {
        private static Func<bool> Yes(Action onCall = null) => () => { onCall?.Invoke(); return true; };
        private static Func<bool> No(Action onCall = null) => () => { onCall?.Invoke(); return false; };

        [Fact]
        public void NumberFloor_outranks_everything_and_short_circuits_the_sinks()
        {
            bool windowTouched = false, goldTouched = false, lootTouched = false;
            var route = BloodRouter.DecideRoute(
                numberEligible: true, numberFloor: 100, rebirthPower: 50,
                windowOpen: Yes(() => windowTouched = true),
                goldAvailable: Yes(() => goldTouched = true),
                lootAvailable: Yes(() => lootTouched = true));
            Assert.Equal(BloodRoute.NumberFloor, route);
            Assert.False(windowTouched);   // floor is decided without touching the window or the sinks
            Assert.False(goldTouched);
            Assert.False(lootTouched);
        }

        [Fact]
        public void Floor_knob_off_when_target_is_zero()
        {
            // numberFloor == 0 means the knob is off -> fall through to the in-run sinks.
            var route = BloodRouter.DecideRoute(true, numberFloor: 0, rebirthPower: 1, Yes(), Yes(), No());
            Assert.Equal(BloodRoute.Gold, route);
        }

        [Fact]
        public void At_or_above_floor_gold_wins_when_window_open()
        {
            var route = BloodRouter.DecideRoute(true, 100, rebirthPower: 150, Yes(), Yes(), Yes());
            Assert.Equal(BloodRoute.Gold, route);
        }

        [Fact]
        public void Loot_is_only_probed_after_gold_declines()
        {
            bool lootTouched = false;
            var route = BloodRouter.DecideRoute(true, 100, 150, Yes(), No(), Yes(() => lootTouched = true));
            Assert.Equal(BloodRoute.Loot, route);
            Assert.True(lootTouched);
        }

        [Fact]
        public void Window_closed_short_circuits_both_sinks_to_the_default()
        {
            bool goldTouched = false, lootTouched = false;
            var route = BloodRouter.DecideRoute(true, 100, 150,
                No(), Yes(() => goldTouched = true), Yes(() => lootTouched = true));
            Assert.Equal(BloodRoute.NumberDefault, route);   // eligible, window shut -> default NUMBER sink
            Assert.False(goldTouched);
            Assert.False(lootTouched);
        }

        [Fact]
        public void Default_sink_is_NUMBER_when_eligible_with_no_in_run_sink()
        {
            var route = BloodRouter.DecideRoute(true, 100, 150, Yes(), No(), No());
            Assert.Equal(BloodRoute.NumberDefault, route);
        }

        [Fact]
        public void Idle_only_when_not_eligible_and_no_sink()
        {
            // Not eligible (NORB / no rebirth): window forced open, but no gold/loot demand -> idle.
            var route = BloodRouter.DecideRoute(false, 100, 150, Yes(), No(), No());
            Assert.Equal(BloodRoute.Idle, route);
        }

        [Fact]
        public void Not_eligible_can_still_take_an_in_run_sink()
        {
            var route = BloodRouter.DecideRoute(false, 100, 150, Yes(), Yes(), No());
            Assert.Equal(BloodRoute.Gold, route);
        }
    }
}
