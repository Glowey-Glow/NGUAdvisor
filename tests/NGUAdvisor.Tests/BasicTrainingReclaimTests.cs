using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE BT RECLAIM RACE (operator-reported 2026-08-07: "the auto-profile is removing BT energy
    // sometimes"; the flip-flop half of audit/31, which named it a membership-before-reclaim race).
    //
    // ConstraintLayerBridge seats lanes from IsValid(), which excludes a BT slot sitting at its cap.
    // It then reclaims by calling allOffenseController/allDefenseController.removeAllEnergy(), and
    // [DECOMP] AllOffenseTraining.cs:53-61 empties EVERY slot regardless. The fill refills only what it
    // seated, so the excluded-for-being-full slots end the pass at zero.
    //
    // The bridge itself reads Main.Character and cannot link here. This is the arithmetic it defers to:
    // given what each slot held before the reclaim and which slots the fill will reseat, how much has to
    // go back.
    public class BasicTrainingReclaimTests
    {
        private static long[] E(params long[] v) => v;
        private static bool[] S(params bool[] v) => v;

        [Fact]
        public void The_mixed_pass_is_the_one_that_lost_energy()
        {
            // Slot 0 full (excluded from the seating list), slots 1-5 still filling (seated).
            // This is the ONLY shape that loses anything, and it is why it will not reproduce on demand.
            var before = E(5000, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var seated = S(false, true, true, true, true, true, false, false, false, false, false, false);

            var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);

            Assert.Equal(5000, restore[0]);          // the saturated slot gets its energy back
            Assert.Equal(0, restore[1]);             // seated: the fill funds it, restoring would double-count
            Assert.Equal(5000, restore.Sum());
        }

        [Fact]
        public void Nothing_is_restored_when_every_slot_is_seated()
        {
            var before = E(10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120);
            var seated = Enumerable.Repeat(true, 12).ToArray();

            Assert.Equal(0, LaneCapMath.BasicTrainingReclaimRestore(before, seated).Sum());
        }

        [Fact]
        public void A_slot_no_lane_covers_is_also_restored()
        {
            // The wider hole: a BT-0-only profile still triggers the controller-wide reclaim, so slots
            // 1-11 are emptied and nothing ever reseats them. Restoring by SEAT rather than by
            // saturation covers this with the same rule.
            var before = E(100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1200);
            var seated = S(true, false, false, false, false, false, false, false, false, false, false, false);

            var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);

            Assert.Equal(0, restore[0]);
            Assert.Equal(before.Sum() - 100, restore.Sum());
        }

        [Fact]
        public void Defense_slots_use_the_upper_half_of_the_index_space()
        {
            // BasicTrainingBP indexes 0-5 attack, 6-11 defense (BasicTrainingSlot is index % 6), so a
            // restore that confused the halves would refund the wrong controller.
            var before = E(0, 0, 0, 0, 0, 0, 42, 0, 0, 0, 0, 7);
            var seated = Enumerable.Repeat(false, 12).ToArray();

            var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);

            Assert.Equal(42, restore[6]);
            Assert.Equal(7, restore[11]);
            Assert.Equal(49, restore.Sum());
        }

        [Fact]
        public void An_empty_slot_restores_nothing_so_idle_never_goes_up()
        {
            // The restore subtracts its total from idleEnergy. Refunding a slot that held nothing would
            // still be a no-op, but a NEGATIVE or phantom amount would silently create energy.
            var before = E(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var seated = Enumerable.Repeat(false, 12).ToArray();

            var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);
            Assert.Equal(0, restore.Sum());
            Assert.All(restore, r => Assert.Equal(0, r));
        }

        [Fact]
        public void The_restore_never_exceeds_what_was_taken()
        {
            // The safety property that makes this fix conservative: it can only ever hand back energy
            // that was on the slot a moment earlier, so it cannot invent allocation out of the pool.
            var before = E(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            var seated = S(false, true, false, true, false, true, false, true, false, true, false, true);

            var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);

            for (int i = 0; i < 12; i++)
                Assert.True(restore[i] <= before[i], "slot " + i + " restored more than it held");
            Assert.True(restore.Sum() <= before.Sum());
        }

        [Fact]
        public void Malformed_input_is_survivable()
        {
            Assert.Equal(0, LaneCapMath.BasicTrainingReclaimRestore(null, S(true)).Sum());
            Assert.Equal(0, LaneCapMath.BasicTrainingReclaimRestore(E(1, 2), null).Sum());
            // shorter arrays than 12: stop where the data stops rather than throwing
            Assert.Equal(3, LaneCapMath.BasicTrainingReclaimRestore(E(3), S(false)).Sum());
            Assert.Equal(12, LaneCapMath.BasicTrainingReclaimRestore(E(1, 2), null).Length);
        }

        [Fact]
        public void Saturation_and_seating_are_different_questions()
        {
            // Pins the diagnosis itself: BasicTrainingSaturated is what EXCLUDES a slot from the seating
            // list, and an excluded slot is exactly the one the total reclaim strips and the fill skips.
            Assert.True(LaneCapMath.BasicTrainingSaturated(5000, 5000));
            Assert.False(LaneCapMath.BasicTrainingSaturated(4999, 5000));

            var before = E(5000, 4999, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var seated = S(!LaneCapMath.BasicTrainingSaturated(before[0], 5000),
                           !LaneCapMath.BasicTrainingSaturated(before[1], 5000),
                           false, false, false, false, false, false, false, false, false, false);

            var restore = LaneCapMath.BasicTrainingReclaimRestore(before, seated);
            Assert.Equal(5000, restore[0]);   // saturated -> unseated -> would have been lost
            Assert.Equal(0, restore[1]);      // still filling -> seated -> the fill funds it
        }
    }
}
