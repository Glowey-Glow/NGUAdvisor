using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // audit/40 §3 — the six items that were still live after the zone-phases campaign closed item 3.
    //
    // The document's own framing is the specification: "Winning layer 1 does not mean being
    // adventured. Most of what is written in layer 1 is discarded, silently, by layer 2." Item 3 was
    // written as a PREDICTION of what that costs, then happened exactly, and cost a full run of
    // farming that never left the ITOPOD (audit/41 §2.1). These tests pin the SURFACE — the chain
    // itself is deliberately unchanged, so the wording and the silence rules are the whole fix.
    //
    // Cause = the enum row; the audit/40 §2 row id is in each test name where it matters.
    //
    // Collection: the latch tests below drive ZoneRouting's process-wide statics. See TestCollections.cs.
    [Collection(TestCollections.ZoneRoutingState)]
    public class ZoneRoutingTests
    {
        private static string Say(ZoneRouting.Cause now, int intended, int routed,
                                 ZoneRouting.Cause previous = ZoneRouting.Cause.None,
                                 bool previousSpoke = true)
            => ZoneRouting.Describe(previous, previousSpoke, now,
                                    intended, intended < 0 ? null : Name(intended),
                                    routed, routed < 0 ? null : Name(routed));

        // Stands in for ZonePhaseReader.ZoneName, which reaches ZoneHelpers.ZoneList (Unity-side).
        private static string Name(int zone) => zone >= 1000 ? "ITOPOD" : "Zone " + zone;

        // ---- §3 item 1: R3-R8 discard every advisor write without a word --------------------

        // The exact live shape of item 3, generalised: the advisor wrote 20 and logged
        // "rare farm -> Chocolate World"; the resolver routed elsewhere and said nothing.
        [Theory]
        [InlineData(ZoneRouting.Cause.GoldSnipe)]      // R5
        [InlineData(ZoneRouting.Cause.Titan)]          // R6
        [InlineData(ZoneRouting.Cause.Quest)]          // R7
        [InlineData(ZoneRouting.Cause.CBlockGold)]     // R8
        [InlineData(ZoneRouting.Cause.TimeMachineEmpty)] // R4
        public void An_owner_that_routes_elsewhere_names_both_zones(ZoneRouting.Cause c)
        {
            var line = Say(c, intended: 20, routed: 33);

            Assert.NotNull(line);
            Assert.Contains(ZoneRouting.Owner(c), line);
            Assert.Contains("Zone 33", line);   // where we actually are
            Assert.Contains("Zone 20", line);   // what was written and is not being adventured
            Assert.Contains("not being adventured", line);
        }

        // R3. The pit run alternates zone 0 and the ITOPOD WITHIN one hold (NeedsGold flips), so the
        // resolver passes routed = -1: naming either would re-emit the line on every flip, and a
        // line per flip on a per-frame path is the same as no line at all.
        [Fact]
        public void The_pit_run_reports_the_hold_without_naming_a_zone_it_keeps_changing()
        {
            var line = Say(ZoneRouting.Cause.PitRun, intended: 20, routed: -1);

            Assert.NotNull(line);
            Assert.Contains("the money-pit run", line);
            Assert.Contains("Zone 20", line);
            Assert.DoesNotContain("->", line);
        }

        // SILENCE IS THE CORRECT ANSWER TWICE, and both matter because this is a per-frame path.
        [Fact]
        public void An_unset_target_is_not_a_contention()
        {
            // -1 is the SavedSettings sentinel (_snipeZone = -1). Nothing was displaced.
            Assert.Null(Say(ZoneRouting.Cause.Quest, intended: -1, routed: 33));
        }

        [Fact]
        public void An_owner_that_agrees_with_the_target_is_not_a_contention()
        {
            Assert.Null(Say(ZoneRouting.Cause.Titan, intended: 33, routed: 33));
        }

        // ---- §3 item 2: ApplyZones stands down for only TWO of the SIX contenders -----------
        //
        // AdvisorApply.cs returns on GoldCBlockMode || MoneyPitRunMode only, so the advisor writes
        // and ANNOUNCES a farm zone while four of the six own routing.
        //
        // ⚠ ONE CORRECTION TO §3 item 2, measured 2026-08-06. It names "R3 pit-run readiness" as one
        // of the four. R3's condition begins `MoneyPitManager.ShockwaveTier() <= 1e18`, and
        // ShockwaveTier returns a `double?` that is NULL whenever MoneyPitRunMode is off
        // (MoneyPitManager.cs:32-33) — a lifted `null <= 1e18` is FALSE, so R3 cannot fire without
        // the very toggle ApplyZones already stands down for. The genuinely uncovered row §3 did not
        // list is R4, the empty Time Machine. The count is unchanged: two covered, four not.
        [Theory]
        [InlineData(ZoneRouting.Cause.TimeMachineEmpty)]   // R4 — the one §3 missed
        [InlineData(ZoneRouting.Cause.GoldSnipe)]          // R5
        [InlineData(ZoneRouting.Cause.Titan)]              // R6
        [InlineData(ZoneRouting.Cause.Quest)]              // R7
        public void Each_contender_ApplyZones_does_not_stand_down_for_is_reported(ZoneRouting.Cause c)
        {
            Assert.NotNull(Say(c, intended: 20, routed: 33));
        }

        // R3 and R8 ARE covered by a stand-down, and are still reported — the stand-down stops the
        // advisor WRITING, it does not stop a manual dropdown target or a stale write from losing.
        [Theory]
        [InlineData(ZoneRouting.Cause.PitRun)]
        [InlineData(ZoneRouting.Cause.CBlockGold)]
        public void The_two_contenders_it_does_stand_down_for_are_still_reported(ZoneRouting.Cause c)
        {
            Assert.NotNull(Say(c, intended: 20, routed: c == ZoneRouting.Cause.PitRun ? -1 : 33));
        }

        // ---- §3 item 3's SURVIVING HALF: R10's Target ITOPOD toggle -------------------------
        //
        // 271f5f8 rescued the advisor's own drop farm from the toggle. audit/40 §6.1 recorded that
        // NOTHING ELSE was rescued, deliberately — "Target ITOPOD keeps its meaning everywhere else,
        // including for the boost farm, a wider change than the defect justified". So the boost farm
        // still writes a zone, still logs "Advisor: farm zone -> X" (AdvisorApply.cs:1175-1179), and
        // R10 still discards it. That precedence is a design decision and is NOT changed; the
        // silence is the defect, and it is the exact shape that cost a full run (audit/41 §2.1).
        [Fact]
        public void The_toggle_names_the_zone_target_it_discarded()
        {
            var line = Say(ZoneRouting.Cause.TargetItopod, intended: 20, routed: 1000);

            Assert.NotNull(line);
            Assert.Contains("Target ITOPOD", line);
            Assert.Contains("Zone 20", line);          // the boost/dropdown zone that was written
            Assert.Contains("ITOPOD is being adventured", line);
        }

        // Both silences of the owner rows apply here too, and they matter more: this state persists
        // for as long as the toggle is on, so a line per frame would be a stream, and a line about a
        // target nobody set would be noise on every ITOPOD run.
        [Fact]
        public void The_toggle_is_silent_when_it_discarded_nothing()
        {
            // -1 is the SavedSettings sentinel; Main.ResolveIntentZone never reports it as displaced.
            Assert.Null(Say(ZoneRouting.Cause.TargetItopod, intended: -1, routed: 1000));
            // An explicit ITOPOD target agreeing with the toggle is not a contention.
            Assert.Null(Say(ZoneRouting.Cause.TargetItopod, intended: 1000, routed: 1000));
        }

        // It stops holding either because the operator switched it off or because a gear hunt or a
        // drop farm took the row above it (audit/40 §6.1). Naming the zone that routes now is what
        // tells those apart, and it is the sentence §6.3 asks every override to end with.
        [Fact]
        public void The_toggle_names_what_routes_when_it_stops_holding()
        {
            var line = Say(ZoneRouting.Cause.None, intended: 20, routed: 20,
                           previous: ZoneRouting.Cause.TargetItopod);

            Assert.NotNull(line);
            Assert.Contains("Target ITOPOD", line);
            Assert.Contains("Zone 20", line);
            // It never "owned" the chain — it won a row inside it — so it must not claim it did.
            Assert.DoesNotContain("released adventure routing", line);
        }

        // The reason must not read as a fault: losing to the toggle is the documented behaviour for
        // every writer except a gear hunt and a drop farm, so it names what WOULD beat it.
        [Fact]
        public void The_toggle_reason_says_what_outranks_it()
        {
            var why = ZoneRouting.Reason(ZoneRouting.Cause.None, ZoneRouting.Cause.TargetItopod);
            Assert.Contains("gear hunt", why);
            Assert.Contains("drop farm", why);
        }

        // ---- §3 item 4: R11 rewrites the zone silently --------------------------------------

        [Fact]
        public void A_locked_target_names_what_replaced_it()
        {
            // AllowZoneFallback on -> the max reachable zone.
            var fallback = Say(ZoneRouting.Cause.UnlockFallback, intended: 43, routed: 37);
            Assert.NotNull(fallback);
            Assert.Contains("Zone 43", fallback);
            Assert.Contains("locked", fallback);
            Assert.Contains("Zone 37", fallback);

            // AllowZoneFallback off -> the ITOPOD. Same rewrite, and it must not read as an ITOPOD
            // preference: the target was locked, nobody chose the ITOPOD.
            var itopod = Say(ZoneRouting.Cause.UnlockFallback, intended: 43, routed: 1000);
            Assert.NotNull(itopod);
            Assert.Contains("Zone 43", itopod);
            Assert.Contains("ITOPOD", itopod);
        }

        // ---- §3 item 5: R12/R13 silently override any ITOPOD routing ------------------------
        //
        // Anything resolving to >= 1000 — INCLUDING an explicit ITOPOD choice — is redirected.
        // Both have sound reasons recorded in comments; neither reached the operator.
        [Theory]
        [InlineData(ZoneRouting.Cause.EvilClimb)]
        [InlineData(ZoneRouting.Cause.GoldStarvedAugs)]
        public void An_ITOPOD_override_says_it_is_overriding_the_ITOPOD(ZoneRouting.Cause c)
        {
            var line = Say(c, intended: 1000, routed: 33);

            Assert.NotNull(line);
            Assert.Contains(ZoneRouting.Owner(c), line);
            Assert.Contains("ITOPOD", line);
            Assert.Contains("Zone 33", line);
        }

        [Fact]
        public void The_ITOPOD_overrides_carry_the_reason_the_comments_already_held()
        {
            Assert.Contains("bosses and gold",
                ZoneRouting.Reason(ZoneRouting.Cause.None, ZoneRouting.Cause.EvilClimb));
            Assert.Contains("no gold",
                ZoneRouting.Reason(ZoneRouting.Cause.None, ZoneRouting.Cause.GoldStarvedAugs));
        }

        // ---- §3 item 6: the fall-through-on-_furthestZone-<-0 paths are invisible ------------
        //
        // Each is a deliberate "nothing fightable yet, use normal routing" case. §3 listed three
        // (R5, R8, R12); R13 has the same shape and had neither a comment nor a line.
        [Theory]
        [InlineData(ZoneRouting.Cause.GoldSnipeNoZone)]
        [InlineData(ZoneRouting.Cause.CBlockNoZone)]
        [InlineData(ZoneRouting.Cause.EvilClimbNoZone)]
        [InlineData(ZoneRouting.Cause.GoldStarvedNoZone)]
        public void A_decline_says_so_and_says_it_is_a_decline(ZoneRouting.Cause c)
        {
            Assert.True(ZoneRouting.IsHandover(c));

            var line = Say(c, intended: -1, routed: 1000);
            Assert.NotNull(line);
            Assert.Contains(ZoneRouting.Owner(c), line);
            Assert.Contains("no clearable zone", line);

            // The feed's "why" must not read as a fault: this is the state the gate was written for.
            Assert.Contains("decline, not a fault", ZoneRouting.Reason(ZoneRouting.Cause.None, c));
        }

        // A hand-back has nothing left to hand back, so it gets no second line when it clears —
        // it already said normal routing continues.
        [Fact]
        public void A_decline_does_not_also_announce_a_resume()
        {
            Assert.Null(Say(ZoneRouting.Cause.None, intended: 1000, routed: 1000,
                            previous: ZoneRouting.Cause.GoldSnipeNoZone));
        }

        // ---- what resumes afterwards (audit/40 §6.3's rule, applied here) --------------------

        [Theory]
        [InlineData(ZoneRouting.Cause.Quest)]
        [InlineData(ZoneRouting.Cause.Titan)]
        [InlineData(ZoneRouting.Cause.PitRun)]
        [InlineData(ZoneRouting.Cause.EvilClimb)]
        public void An_owner_that_lets_go_names_what_resumes(ZoneRouting.Cause previous)
        {
            Assert.True(ZoneRouting.IsOwner(previous));

            var line = Say(ZoneRouting.Cause.None, intended: 20, routed: 20, previous: previous);
            Assert.NotNull(line);
            Assert.Contains(ZoneRouting.Owner(previous), line);
            Assert.Contains("released", line);
            Assert.Contains("Zone 20", line);
        }

        [Fact]
        public void Steady_normal_routing_says_nothing_at_all()
        {
            Assert.Null(Say(ZoneRouting.Cause.None, intended: 20, routed: 20,
                            previous: ZoneRouting.Cause.None, previousSpoke: false));
        }

        // ⚠ A HOLD THAT DISPLACED NOTHING GETS NO RELEASE LINE. A quest that happened to agree with
        // the zone target (or ran while the target was unset) is not contention, and announcing its
        // release would be a line about an event the operator never saw begin. Minor quests are
        // frequent enough that this is the difference between a report and a stream.
        [Fact]
        public void A_hold_that_was_never_reported_is_not_reported_when_it_releases()
        {
            Assert.Null(Say(ZoneRouting.Cause.None, intended: 33, routed: 33,
                            previous: ZoneRouting.Cause.Quest, previousSpoke: false));
        }

        // The full round trip through the latch: a silent hold must stay silent at BOTH ends, and
        // the latch is what remembers which it was.
        [Fact]
        public void The_latch_remembers_whether_the_hold_was_ever_spoken()
        {
            ZoneRouting.Reset();

            // A quest that agrees with the target: transition happens, no line, nothing to release.
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 33, 33, out var p1, out var s1));
            Assert.False(s1);
            var held = Say(ZoneRouting.Cause.Quest, 33, 33, p1, s1);
            Assert.Null(held);
            ZoneRouting.Spoke(held != null);

            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.None, 33, 33, out var p2, out var s2));
            Assert.Equal(ZoneRouting.Cause.Quest, p2);
            Assert.False(s2);
            Assert.Null(Say(ZoneRouting.Cause.None, 33, 33, p2, s2));
        }

        [Fact]
        public void A_hold_that_WAS_reported_is_reported_when_it_releases()
        {
            ZoneRouting.Reset();

            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out var p1, out var s1));
            var held = Say(ZoneRouting.Cause.Quest, 20, 33, p1, s1);
            Assert.NotNull(held);
            ZoneRouting.Spoke(true);

            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.None, 20, 20, out var p2, out var s2));
            Assert.True(s2);
            Assert.NotNull(Say(ZoneRouting.Cause.None, 20, 20, p2, s2));
        }

        // ---- the latch: this runs from LateUpdate, i.e. ~60x/second --------------------------

        [Fact]
        public void A_held_state_surfaces_once_and_then_never_again()
        {
            ZoneRouting.Reset();

            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out var p0, out _));
            Assert.Equal(ZoneRouting.Cause.None, p0);

            for (int frame = 0; frame < 1000; frame++)
                Assert.False(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out _, out _));
        }

        [Fact]
        public void A_change_of_owner_zone_or_target_is_a_new_fact()
        {
            ZoneRouting.Reset();

            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out _, out _));
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 37, out _, out _));   // new zone
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 21, 37, out _, out _));   // new target
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Titan, 21, 37, out _, out _));   // new owner
        }

        [Fact]
        public void The_latch_hands_the_previous_cause_out_so_the_release_can_be_named()
        {
            ZoneRouting.Reset();

            ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out _, out _);
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.None, 20, 20, out var previous, out _));
            Assert.Equal(ZoneRouting.Cause.Quest, previous);
        }

        // A displacement, its release, and then the SAME displacement again must all be reported —
        // a latch that swallowed the second occurrence would recreate the defect for every quest
        // after the first.
        [Fact]
        public void The_same_contention_reported_again_after_it_clears()
        {
            ZoneRouting.Reset();

            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out _, out _));
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.None, 20, 20, out _, out _));
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Quest, 20, 33, out _, out _));
        }

        // ---- the classification itself -------------------------------------------------------
        //
        // IsOwner drives whether a release is announced, IsHandover whether a decline is. Every row
        // must be exactly one of owner / hand-back / rewrite / none, or a cause silently falls
        // through Describe and reports nothing — which is the defect, not the fix.
        [Fact]
        public void Every_cause_is_classified_and_every_owner_has_a_name()
        {
            foreach (ZoneRouting.Cause c in System.Enum.GetValues(typeof(ZoneRouting.Cause)))
            {
                Assert.False(ZoneRouting.IsOwner(c) && ZoneRouting.IsHandover(c));

                if (c == ZoneRouting.Cause.None || c == ZoneRouting.Cause.UnlockFallback)
                {
                    Assert.False(ZoneRouting.IsOwner(c));
                    Assert.False(ZoneRouting.IsHandover(c));
                    continue;
                }

                Assert.True(ZoneRouting.IsOwner(c) || ZoneRouting.IsHandover(c));
                Assert.False(string.IsNullOrEmpty(ZoneRouting.Owner(c)));
                Assert.False(string.IsNullOrEmpty(ZoneRouting.Reason(ZoneRouting.Cause.None, c)));
            }
        }
    }
}
