using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Beards (constraint-layer-spec §6): the full-bar gate is Pass 1, capacity is refused, and the
    // wipe is distinguishable in the surfaced reason.
    public class BeardGateTests
    {
        private static BeardGate.BeardState State(double cur, double cap, bool disabled = false)
        {
            return new BeardGate.BeardState { CurBar = cur, CapBar = cap, Disabled = disabled };
        }

        // The game's refusal comparator is strict < (AllBeardsController.cs:60/:74): a bar exactly
        // at cap ticks, so it seats.
        [Fact]
        public void A_full_bar_seats_including_exactly_at_cap()
        {
            Assert.True(BeardGate.Feasible(State(cur: 1000, cap: 1000)).Seated);
            Assert.True(BeardGate.Feasible(State(cur: 1001, cap: 1000)).Seated);
        }

        [Fact]
        public void A_bar_below_cap_refuses_with_the_full_bar_reason()
        {
            var v = BeardGate.Feasible(State(cur: 999, cap: 1000));

            Assert.False(v.Seated);
            Assert.Contains("below cap", v.Reason);
        }

        // The wipe (TrollChallengeController.cs:738-744/:754-762) zeroes curEnergy; the reason must
        // say so, because a wiped bar and a refilling bar need different surfacing — allocation
        // cannot fix either, and only one of them is a Troll kill.
        [Fact]
        public void A_wiped_bar_names_the_wipe_in_its_reason()
        {
            var v = BeardGate.Feasible(State(cur: 0, cap: 1000));

            Assert.False(v.Seated);
            Assert.Contains("wipeEnergy", v.Reason);
        }

        [Fact]
        public void A_trolled_off_beard_refuses_before_the_bar_is_consulted()
        {
            var v = BeardGate.Feasible(State(cur: 1000, cap: 1000, disabled: true));

            Assert.False(v.Seated);
            Assert.Contains("beards.disabled", v.Reason);
        }

        // Both pool sides ride the same gate — the magic beards' :74-77 check is the same shape
        // against curMagic/totalCapMagic, so the caller just passes the other bar.
        [Fact]
        public void The_magic_side_bar_uses_the_same_gate()
        {
            Assert.True(BeardGate.Feasible(State(cur: 5e12, cap: 5e12)).Seated);
            Assert.False(BeardGate.Feasible(State(cur: 4.9e12, cap: 5e12)).Seated);
        }

        // Beards are a P1 claimant with zero allocation cost — the only system with that shape. A
        // feasible beard therefore takes an ordinary SEAT in the roster (the Campaign Advisor's
        // ranking needs it) even though Pass 2 will never produce a capacity for it.
        [Fact]
        public void A_feasible_beard_takes_a_seat_like_any_other_claimant()
        {
            var roster = new SeatRoster();
            roster.Add("BEARD-3", BeardGate.Feasible(State(cur: 1000, cap: 1000)));
            roster.Add("BEARD-4", BeardGate.Feasible(State(cur: 0, cap: 1000)));

            Assert.Equal(1, roster.SeatCount);
            Assert.Contains("BEARD-3", roster.Seated);
            Assert.Equal("BEARD-4", roster.Refusals.Single().Lane);
        }

        // Pass 2 refuses to cap beards, structurally: their table row is CapSource.None, and
        // Allocatable — the guard every capacity consumer routes through — says no. (The row's
        // content is pinned in CapacityPassTests; this is the §6 cross-check from the beard side.)
        [Fact]
        public void Pass_2_refuses_to_cap_beards()
        {
            var beard = CapacityPass.Table.Single(r => r.Lane == "Beards");

            Assert.False(CapacityPass.Allocatable(beard));
        }
    }
}
