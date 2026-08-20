using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // WHERE THE R3 POOL WENT (R3PoolView).
    //
    // R3 had NO runtime telemetry of any kind. Measured over a 25-minute bench session against the
    // end-game save: [AllocDbg] Energy 42, [AllocDbg] Magic 36, [AllocDbg] R3 0, [HackDbg] 0,
    // "Hacks:" 0. Two independent silences produce that:
    //
    //   * R3Breakpoints.PerformSwap returns when every token fails IsValid() — BEFORE its own
    //     RemoveR3(), so the previous allocation is left standing and nothing is logged. On the bench
    //     save all fifteen hacks sit at hardCapLevel, so CAPALLHACK expands to fifteen lanes and
    //     every one of them fails HackBP.Unlocked(). A dead R3 lane and a healthy one are
    //     byte-identical in every channel.
    //   * [HackDbg] is unreachable exactly when it is needed: LogHacks is called after a
    //     `funded == 0` early return, and hard-capped hacks never increment `funded`.
    //
    // So the assertions below are not about a display. They are about the only record that will exist
    // of what the third pool did — which is why every one of them is about a SENTENCE as much as a
    // number: a lane that received zero and cannot say why is the defect the E/M path's spec §10
    // names, and a lane that says something FALSE (idle R3 described as "reclaimed on the next swap"
    // when no lane can reclaim it) is worse than the silence it replaced.
    public class R3PoolViewTests
    {
        // ---- builders ------------------------------------------------------------------------------

        private static R3PoolView.LaneRecord Seated(string label, int id, long offered)
            => new R3PoolView.LaneRecord { Label = label, HackId = id, Seated = true, Offered = offered };

        private static R3PoolView.LaneRecord Refused(string label, int id, string why)
            => new R3PoolView.LaneRecord { Label = label, HackId = id, Seated = false, Reason = why };

        private static R3PoolView.HackHolding Holds(int id, long held, double ppt = 0.25)
            => new R3PoolView.HackHolding { Id = id, Held = held, ProgressPerTick = held > 0 ? ppt : 0 };

        private static R3PoolView.Inputs Inputs(R3PoolView.LaneRecord[] roster, bool reclaimed,
            IEnumerable<R3PoolView.HackHolding> hacks, long wishHeld, long idle,
            double wishPct = 100, long wishOffered = 0, bool wishRan = true,
            bool r3Managed = true, bool wishesManaged = true)
            => new R3PoolView.Inputs
            {
                Swap = roster == null ? null : new R3PoolView.SwapRecord
                {
                    Lanes = roster,
                    Pool = idle + wishHeld,
                    Reclaimed = reclaimed,
                    HeldReason = reclaimed ? null : "all 15 hack token(s) in the timeline failed IsValid()",
                },
                Wish = wishRan
                    ? new R3PoolView.WishRecord { Ran = true, SharePercent = wishPct, Offered = wishOffered }
                    : null,
                Hacks = hacks == null ? null : hacks.ToList(),
                WishHeld = wishHeld,
                Idle = idle,
                R3Managed = r3Managed,
                WishesManaged = wishesManaged,
            };

        private static ConstraintLayer.LaneDecision Lane(ConstraintLayer.Plan p, string label)
            => p.Lanes.Single(l => l.Label == label);

        // ---- the account closes --------------------------------------------------------------------

        // THE PROPERTY THE WHOLE BOARD RESTS ON. Hacks, wishes and idle are the complete set of R3
        // destinations — the only writers of res3.idleRes3 in the decompile are HacksController
        // (:171-204), WishesController (:647, :1026, :1095) and the bar's own regen — so the three
        // shares must sum to the pool exactly. If they do not, the percentages the board draws are
        // shares of a number that means nothing.
        [Fact]
        public void LaneTotalsPlusIdleEqualThePool()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 900), Seated("CAPHACK-1", 1, 400) },
                reclaimed: true,
                hacks: new[] { Holds(0, 500), Holds(1, 300) },
                wishHeld: 150, idle: 50, wishOffered: 200));

            Assert.Equal(1000, plan.Pool);
            Assert.Equal(500 + 300 + 150 + 50, plan.Pool);
            Assert.Equal(50, plan.Unallocated);
            Assert.Equal(1000 - 50, plan.Lanes.Sum(l => l.Allocation));
        }

        [Fact]
        public void SeatedLanesCarryWhatTheyWereOfferedAndWhatTheyHold()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 900) },
                reclaimed: true,
                hacks: new[] { Holds(0, 500) },
                wishHeld: 0, idle: 400, wishOffered: 400));

            var lane = Lane(plan, "CAPHACK-0");
            Assert.True(lane.Seated);
            Assert.Equal(900, lane.Offered);
            Assert.Equal(500, lane.Allocation);
        }

        // Wishes take the surplus-sink seat Wandoos holds on E/M, which is what R3Breakpoints already
        // calls them ("wishes are the other R3 sink"). The UI renders SinkIndex with its own tag, so
        // getting this wrong labels the pool's second consumer as an ordinary lane.
        [Fact]
        public void WishesAreTheSinkAndAlwaysPresent()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 900) },
                reclaimed: true, hacks: new[] { Holds(0, 1000) },
                wishHeld: 0, idle: 0, wishOffered: 0));

            Assert.Equal(plan.Lanes.Length - 1, plan.SinkIndex);
            Assert.True(plan.Lanes[plan.SinkIndex].SurplusSink);
            Assert.Equal("Wishes", plan.Lanes[plan.SinkIndex].Label);
        }

        // ---- every zero carries its reason ----------------------------------------------------------

        [Fact]
        public void RefusedLaneKeepsTheReasonTheAllocatorRecorded()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Refused("CAPHACK-3", 3, "at its hard cap, level 6600") },
                reclaimed: true, hacks: new[] { Holds(3, 0) },
                wishHeld: 10, idle: 0));

            var lane = Lane(plan, "CAPHACK-3");
            Assert.False(lane.Seated);
            Assert.Equal("at its hard cap, level 6600", lane.Reason);
        }

        // A refusal with no recorded cause is itself the defect, so the composer refuses to be silent
        // about the silence rather than emitting a bare zero.
        [Fact]
        public void RefusedLaneWithNoRecordedReasonSaysSo()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Refused("CAPHACK-3", 3, null) },
                reclaimed: true, hacks: new[] { Holds(3, 0) },
                wishHeld: 10, idle: 0));

            Assert.Contains("NO recorded reason", Lane(plan, "CAPHACK-3").Reason);
        }

        // The R3 lane is an ORDER, not a share: prioCount stays 1, so each lane is offered the whole
        // remaining idle at its turn and the lanes behind it get what it leaves. A lane that never got
        // its turn must not read the same as one that refused its offer.
        [Fact]
        public void SeatedLaneStarvedByTheOrderSaysItWasOfferedNothing()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 1000), Seated("CAPHACK-1", 1, 0) },
                reclaimed: true,
                hacks: new[] { Holds(0, 1000), Holds(1, 0) },
                wishHeld: 0, idle: 0));

            var starved = Lane(plan, "CAPHACK-1");
            Assert.True(starved.Seated);
            Assert.Equal(0, starved.Allocation);
            Assert.Contains("offered nothing", starved.Reason);
            Assert.Contains("ORDER", starved.Reason);
        }

        [Fact]
        public void SeatedLaneThatRefusedItsOfferSaysThatInstead()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-1", 1, 5000) },
                reclaimed: true, hacks: new[] { Holds(1, 0) },
                wishHeld: 0, idle: 5000, wishOffered: 5000));

            Assert.Contains("took none of it", Lane(plan, "CAPHACK-1").Reason);
        }

        // THE FLOAT STALL FLOOR. A hack below 2^-25 progress per tick holds a real share of the pool
        // and converts none of it, forever — progress is a float whose ULP across [0.5,1) is 2^-24, so
        // round-to-nearest swallows the increment. It is the one state this board would otherwise draw
        // as a healthy coloured segment, and it is why R3Breakpoints refuses to split the pool.
        [Fact]
        public void LaneParkedOnTheStallFloorSaysSoRatherThanLookingHealthy()
        {
            double belowFloor = HackMath.StallFloor / 2;
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-9", 9, 1000) },
                reclaimed: true,
                hacks: new[] { Holds(9, 800, belowFloor) },
                wishHeld: 0, idle: 200, wishOffered: 200));

            var lane = Lane(plan, "CAPHACK-9");
            Assert.Equal(800, lane.Allocation);
            Assert.Contains("stall floor", lane.Reason);
        }

        [Fact]
        public void HealthyLaneCarriesNoReason()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-9", 9, 1000) },
                reclaimed: true,
                hacks: new[] { Holds(9, 800, 0.5) },
                wishHeld: 0, idle: 200, wishOffered: 200));

            Assert.Null(Lane(plan, "CAPHACK-9").Reason);
        }

        // ---- the held pass: the bench save's actual state ---------------------------------------------

        // Every hack hard-capped => valid.Count == 0 => PerformSwap returns before RemoveR3(). The
        // board must not render this as an empty panel: the lane list draws nothing (the page keeps
        // seated lanes and non-zero takes), so the pool-level message is the ONLY thing that can say
        // what happened.
        [Fact]
        public void HeldPassStatesItselfOnThePoolRatherThanRenderingBlank()
        {
            var roster = Enumerable.Range(0, 15)
                .Select(i => Refused("CAPHACK-" + i, i, "at its hard cap, level 6600"))
                .ToArray();

            var plan = R3PoolView.Compose(Inputs(roster, reclaimed: false,
                hacks: Enumerable.Range(0, 15).Select(i => Holds(i, 0)),
                wishHeld: 482273592059025L, idle: 0, wishOffered: 482273592059025L));

            Assert.NotNull(plan.BudgetMessage);
            Assert.Contains("no R3 was allocated this pass", plan.BudgetMessage);
            Assert.Equal(482273592059025L, plan.Pool);
            Assert.Equal(482273592059025L, plan.Lanes[plan.SinkIndex].Allocation);
            Assert.All(plan.Lanes.Where(l => l.Label.StartsWith("CAPHACK")),
                l => Assert.Equal("at its hard cap, level 6600", l.Reason));
        }

        // ⚠ THE FALSE SENTENCE THIS TEST EXISTS TO FORBID. WishManager's own header relies on the next
        // swap reabsorbing what the sliders left. With no fundable hack lane there is no next
        // absorption: the un-taken share sits there permanently at (100 - slider)% of the pool.
        // "Reclaimed and re-offered on the next swap" would be a lie in exactly the state the bench
        // save is in.
        [Fact]
        public void IdleIsStrandedNotQueuedWhenNoHackLaneCanTakeIt()
        {
            var roster = Enumerable.Range(0, 15)
                .Select(i => Refused("CAPHACK-" + i, i, "at its hard cap, level 6600"))
                .ToArray();

            var plan = R3PoolView.Compose(Inputs(roster, reclaimed: false,
                hacks: Enumerable.Range(0, 15).Select(i => Holds(i, 0)),
                wishHeld: 400, idle: 600, wishPct: 40, wishOffered: 400));

            Assert.Contains("STRANDED", plan.UnallocatedReason);
            Assert.Contains("40", plan.UnallocatedReason);
            Assert.Contains("60", plan.UnallocatedReason);
            Assert.DoesNotContain("next swap", plan.UnallocatedReason);
        }

        [Fact]
        public void IdleIsQueuedWhenAHackLaneCanStillReclaimIt()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-1", 1, 1000) },
                reclaimed: true, hacks: new[] { Holds(1, 400) },
                wishHeld: 200, idle: 400, wishPct: 40, wishOffered: 200));

            Assert.Contains("next swap", plan.UnallocatedReason);
            Assert.DoesNotContain("STRANDED", plan.UnallocatedReason);
        }

        // ---- the opposite failure: hacks starve the wishes ---------------------------------------------

        // An index-ordered CAPALLHACK line whose head hack is still fundable takes the whole pool, so
        // every wish holds zero R3 — and progressPerTick multiplies a res3 term with a fixed 0.17 bias
        // ([DECOMP] WishesController.cs:705, :831-843), so NO wish advances at all. That is an outage,
        // and a bare "0.0%" on the wish row would read as a rounding artefact.
        [Fact]
        public void WishesStarvedByTheHackOrderReportAnOutage()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 1000) },
                reclaimed: true, hacks: new[] { Holds(0, 1000) },
                wishHeld: 0, idle: 0, wishOffered: 0));

            var wishes = plan.Lanes[plan.SinkIndex];
            Assert.True(wishes.Seated);
            Assert.Equal(0, wishes.Allocation);
            Assert.Contains("NO wish advances at all", wishes.Reason);
            Assert.Contains("outage, not a slowdown", wishes.Reason);
        }

        [Fact]
        public void ZeroPercentSliderIsARefusalNotAnOutage()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 1000) },
                reclaimed: true, hacks: new[] { Holds(0, 600) },
                wishHeld: 0, idle: 400, wishPct: 0, wishOffered: 0));

            var wishes = plan.Lanes[plan.SinkIndex];
            Assert.False(wishes.Seated);
            Assert.Contains("0%", wishes.Reason);
            Assert.Equal(wishes.Reason, plan.SinkRefusalReason);
        }

        [Fact]
        public void WishesOffAreRefusedWithTheirOwnReason()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 1000) },
                reclaimed: true, hacks: new[] { Holds(0, 600) },
                wishHeld: 0, idle: 400, wishesManaged: false));

            Assert.False(plan.SinkSeated);
            Assert.Contains("wish funding is off", plan.SinkRefusalReason);
        }

        // ---- R3 held where no token names it -----------------------------------------------------------

        // removeAllR3 empties every hack, so a hack holding R3 that no token names exists exactly when
        // R3 management is off, when the pass returned before its reclaim, or when the timeline was
        // edited under a standing allocation. Without a row for it the pool arithmetic would lose it.
        [Fact]
        public void HackHoldingR3WithNoTokenGetsItsOwnRowAndStaysInTheAccount()
        {
            var plan = R3PoolView.Compose(Inputs(
                new R3PoolView.LaneRecord[0], reclaimed: true,
                hacks: new[] { Holds(7, 250) },
                wishHeld: 0, idle: 750, r3Managed: false));

            var orphan = Lane(plan, "HACK-7");
            Assert.False(orphan.Seated);
            Assert.Equal(250, orphan.Allocation);
            Assert.Contains("no token naming it", orphan.Reason);
            Assert.Equal(1000, plan.Pool);
        }

        // A timeline may name the same hack twice. The hack holds ONE allocation, so attributing it to
        // both rows would double-count it and the account would stop closing.
        [Fact]
        public void DuplicateTokenIsReportedOnceAndSaysWhy()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("HACK-1", 1, 1000), Seated("HACK-1", 1, 0) },
                reclaimed: true, hacks: new[] { Holds(1, 800) },
                wishHeld: 0, idle: 200, wishOffered: 200));

            Assert.Equal(1000, plan.Pool);
            Assert.Equal(800, plan.Lanes[0].Allocation);
            Assert.Equal(0, plan.Lanes[1].Allocation);
            Assert.Contains("more than once", plan.Lanes[1].Reason);
        }

        // ---- nothing to say --------------------------------------------------------------------------

        // `emit` treats a null plan as "no node this tick", so an R3-less save costs the wire nothing
        // and the page keeps showing two pools rather than a third empty one.
        [Fact]
        public void NoPassAndNoHoldingsComposesNothing()
        {
            Assert.Null(R3PoolView.Compose(Inputs(null, reclaimed: true,
                hacks: Enumerable.Range(0, 15).Select(i => Holds(i, 0)),
                wishHeld: 0, idle: 0, wishRan: false)));
        }

        // ---- the refusal sentences ---------------------------------------------------------------------

        // ⚠ ORDER MIRRORS HackBP's OWN GATES: index, then the hacks button, then the hard cap, then
        // hitTarget, then — on MILEHACK only — the first milestone. Reporting them in any other order
        // names a cause the lane had not reached yet.
        [Fact]
        public void RefusalOutOfRangeIsNamedFirst()
        {
            Assert.Contains("does not exist", R3PoolView.HackRefusal(new R3PoolView.RefusalFacts
            {
                HackId = 99, HacksUnlocked = true, Level = 0, HardCapLevel = 100,
            }));
        }

        [Fact]
        public void RefusalLockedHacksAreNamedBeforeTheCap()
        {
            Assert.Contains("not unlocked", R3PoolView.HackRefusal(new R3PoolView.RefusalFacts
            {
                HackId = 3, HacksUnlocked = false, Level = 900, HardCapLevel = 100,
            }));
        }

        [Fact]
        public void RefusalHardCapNamesTheLevelAndWhyR3ThereIsWasted()
        {
            var why = R3PoolView.HackRefusal(new R3PoolView.RefusalFacts
            {
                HackId = 3, HacksUnlocked = true, Level = 6600, HardCapLevel = 6600, TargetMet = true,
            });
            Assert.Contains("hard cap, level 6600", why);
            Assert.Contains("skips the level", why);
        }

        [Fact]
        public void RefusalTargetIsNamedWhenBelowTheCap()
        {
            Assert.Contains("hitTarget", R3PoolView.HackRefusal(new R3PoolView.RefusalFacts
            {
                HackId = 3, HacksUnlocked = true, Level = 50, HardCapLevel = 6600, TargetMet = true,
            }));
        }

        // MileHackBP's extra stop is the guide's ch.5 rule, and it is reported as such rather than as
        // a generic target: a lane that finished its objective and one the player parked are different
        // facts about the run.
        [Fact]
        public void RefusalMilestoneIsNamedOnlyForMilestoneLanes()
        {
            var facts = new R3PoolView.RefusalFacts
            {
                HackId = 4, HacksUnlocked = true, Level = 40, HardCapLevel = 6600,
                MilestoneLane = true, MilestoneThreshold = 25,
            };
            Assert.Contains("first milestone reached at level 25", R3PoolView.HackRefusal(facts));

            facts.MilestoneLane = false;
            Assert.DoesNotContain("milestone", R3PoolView.HackRefusal(facts));
        }

        [Fact]
        public void RefusalOverlayDropIsNamedLast()
        {
            Assert.Contains("challenge overlay", R3PoolView.HackRefusal(new R3PoolView.RefusalFacts
            {
                HackId = 4, HacksUnlocked = true, Level = 40, HardCapLevel = 6600,
                DroppedByOverlay = true,
            }));
        }

        // ---- the plan is the layer's own shape ----------------------------------------------------------

        // The point of composing a real ConstraintLayer.Plan is that UiBridge's emit lambda,
        // AllocTelemetry's change gate and the companion's renderPools consume R3 through the exact
        // contract they already use for Energy and Magic — so if R3 is ever routed for real, only the
        // producer swaps. A signature that throws or reads empty would silently disable the gate.
        [Fact]
        public void ComposedPlanFeedsTheExistingTelemetrySignature()
        {
            var plan = R3PoolView.Compose(Inputs(
                new[] { Seated("CAPHACK-0", 0, 900), Refused("CAPHACK-1", 1, "at its hard cap, level 6600") },
                reclaimed: true, hacks: new[] { Holds(0, 500), Holds(1, 0) },
                wishHeld: 150, idle: 350, wishOffered: 350));

            var sig = AllocTelemetry.Signature(plan, null);
            Assert.Contains("CAPHACK-0@funded", sig);
            Assert.Contains("#sink=seated", sig);

            // And it renders, so the same block an operator reads for E/M is available for R3.
            Assert.Contains("[AllocDbg] R3", AllocTelemetry.Render("R3", plan, null));
        }
    }
}
