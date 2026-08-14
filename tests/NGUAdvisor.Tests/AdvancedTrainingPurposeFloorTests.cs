using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // LevelPlanner's Block purpose cap (slot 2), as a floor.
    //
    // Operator-reported 2026-08-07: "it's capping fields in AT again concerning Block below the 100,000
    // threshold that was set." TickPurposeTargets runs every tick while the auto profile is on and wrote
    // levelTarget[2] = ceil(49 / block.levelFactor) unconditionally, so a hand-typed 100,000 was gone on
    // the next tick. The 99% arithmetic was never wrong — verified against [DECOMP]
    // AdvancedTrainingController.blockBonus, `50f / (1 + levelFactor * level) / 100f`, which reaches 1%
    // remaining damage at levelFactor*level >= 49. What was missing was any way for the operator to ask
    // for more.
    //
    // LevelPlanner itself reads Main.Character and cannot link here; this is the decision it defers to.
    public class AdvancedTrainingPurposeFloorTests
    {
        [Theory]
        // an operator target ABOVE the stop is kept, which is the whole point
        [InlineData(100000, 98000, 100000)]
        [InlineData(1000000, 98000, 1000000)]
        // exactly at the stop: kept, and identical either way
        [InlineData(98000, 98000, 98000)]
        // below the stop: raised to it, so a fresh run still reaches 99% reduction
        [InlineData(50000, 98000, 98000)]
        [InlineData(1, 98000, 98000)]
        // 0 is the game's UNSET sentinel -> take the stop
        [InlineData(0, 98000, 98000)]
        // negative reads as "met" to AdvancedTrainingTargetMet; not a number to preserve
        [InlineData(-1, 98000, 98000)]
        public void Floor_keeps_the_larger_of_the_two(long current, long stop, long expected) =>
            Assert.Equal(expected, LaneTargets.AdvancedTrainingPurposeFloor(current, stop));

        [Fact]
        public void The_operators_100k_survives_a_thousand_ticks()
        {
            // The defect was per-tick, so one application proving nothing is exactly how it hid. The old
            // behaviour was `targets[2] = stop` — this pins that repetition cannot walk it back down.
            const long stop = 98000;
            long target = 100000;
            for (int i = 0; i < 1000; i++)
                target = LaneTargets.AdvancedTrainingPurposeFloor(target, stop);
            Assert.Equal(100000, target);
        }

        [Fact]
        public void An_unset_slot_still_gets_the_purpose_cap()
        {
            // The floor must not become "never write anything" — a fresh install has 0 here and the
            // purpose rule still owns it.
            long target = 0;
            target = LaneTargets.AdvancedTrainingPurposeFloor(target, 98000);
            Assert.Equal(98000, target);

            // and having taken it, it is now stable
            Assert.Equal(98000, LaneTargets.AdvancedTrainingPurposeFloor(target, 98000));
        }

        [Fact]
        public void A_lowered_stop_never_drags_an_existing_target_down()
        {
            // levelFactor does not move within a run, but if it ever did, the floor must still not lower
            // what is already there — that is the failure the operator saw.
            Assert.Equal(100000, LaneTargets.AdvancedTrainingPurposeFloor(100000, 40000));
            Assert.Equal(100000, LaneTargets.AdvancedTrainingPurposeFloor(100000, 1));
        }
    }
}
