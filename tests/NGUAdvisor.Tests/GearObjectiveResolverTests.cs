using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The precedence table that decides which gear objective is in force. Two call sites used to write
    // it out separately and had already drifted; these tests are what keeps the single definition honest.
    //
    // The most important test here is GoldenTable_EmptyPin_ReproducesTheOldExpression: the standing pin
    // is a NEW rank, and it must be provably invisible to everyone who hasn't set one.
    public class GearObjectiveResolverTests
    {
        private static GearObjectiveResolver.Inputs In(
            bool noec = false, bool challengeActive = false, bool huntActive = false,
            string over = null, bool overIsSegment = false,
            string profile = null, bool profileRespawn = false,
            string pin = null, bool pinRespawn = false, bool dropFarm = false)
            => new GearObjectiveResolver.Inputs
            {
                Noec = noec, ChallengeActive = challengeActive, HuntActive = huntActive,
                Override = over, OverrideIsSegment = overIsSegment,
                ProfileObjective = profile, ProfileRespawn = profileRespawn,
                Pin = pin, PinRespawn = pinRespawn, DropFarmActive = dropFarm
            };

        // ── THE ADVISOR'S OWN DROP FARM ───────────────────────────────────────────────────────────
        // [OPERATOR] 2026-08-05: "we're not swapping to DC/Respawn gear either. We made the Loot
        // Hunter to handle this previously, perhaps it should absorb that into this as well."
        //
        // The Loot Hunter set was gated on GearHunter.Active — the MANUAL toggle — so a farm the
        // ADVISOR chose ran on whatever objective the profile happened to hold. Exactly the shape of
        // the DC/PP digger law, which had the same hole for the same reason (FarmVenue).

        [Fact]
        public void A_drop_farm_runs_the_loot_hunter_set()
        {
            var r = GearObjectiveResolver.Resolve(In(dropFarm: true));
            Assert.Equal(GearObjectiveResolver.LootHunter, r.Name);
            Assert.Equal(GearObjectiveResolver.Src.DropFarm, r.Source);
            Assert.True(r.Resolved);
        }

        // It outranks the standing profile timeline and the pin — background objectives with no claim
        // on the next several hours — and names what will come back.
        [Fact]
        public void A_drop_farm_outranks_the_profile_timeline_and_the_pin()
        {
            var r = GearObjectiveResolver.Resolve(In(profile: "NGUs", pin: "Adventure", dropFarm: true));
            Assert.Equal(GearObjectiveResolver.LootHunter, r.Name);
            Assert.Equal(GearObjectiveResolver.Src.DropFarm, r.Source);
            Assert.Contains("NGUs", r.Sentence);
            Assert.Contains("resumes", r.Sentence);
        }

        // A challenge rotation reacts to a run the user cannot re-gear out of, so it still wins —
        // enforced by the farm row's OWN !ChallengeActive guard, not by its rank.
        [Fact]
        public void A_challenge_rotation_outranks_a_drop_farm()
        {
            var r = GearObjectiveResolver.Resolve(
                In(challengeActive: true, over: "Boss Push", dropFarm: true));
            Assert.Equal("Boss Push", r.Name);
            Assert.Equal(GearObjectiveResolver.Src.Challenge, r.Source);
        }

        // ⚠ THE REGRESSION THAT MADE THE FIRST ATTEMPT DEAD CODE. The farm row was ranked BELOW the
        // override to protect the challenge rotation — but that is already the guard's job, and with
        // AutoProfile on and NO challenge running, ChallengeOverlay.cs:186-189 sets the override to
        // SegmentGear() every tick. So `Has(Override)` was permanently true and the farm row was
        // never reached: observed live with DropFarmActive true, the farm routing zone 20, and gear
        // still on "NGUs". Segment gear is a PHASE plan; a drop farm is the next several hours.
        [Fact]
        public void A_drop_farm_outranks_segment_gear()
        {
            var r = GearObjectiveResolver.Resolve(
                In(over: "NGUs", overIsSegment: true, dropFarm: true));
            Assert.Equal(GearObjectiveResolver.LootHunter, r.Name);
            Assert.Equal(GearObjectiveResolver.Src.DropFarm, r.Source);
            Assert.Contains("NGUs", r.Sentence);      // names what resumes
        }

        // Segment gear still wins whenever no farm is running — the common case must be untouched.
        [Fact]
        public void Segment_gear_still_wins_with_no_drop_farm()
        {
            var r = GearObjectiveResolver.Resolve(In(over: "Wandoos", overIsSegment: true));
            Assert.Equal("Wandoos", r.Name);
            Assert.Equal(GearObjectiveResolver.Src.Segment, r.Source);
        }

        [Fact]
        public void The_manual_hunt_still_outranks_everything_including_a_drop_farm()
        {
            var r = GearObjectiveResolver.Resolve(
                In(huntActive: true, over: "Wandoos", overIsSegment: true, dropFarm: true));
            Assert.Equal(GearObjectiveResolver.Src.Hunt, r.Source);
        }

        // No farm re-gears its way through a challenge — the same guard the hunt already has.
        [Fact]
        public void A_challenge_suppresses_the_drop_farm_set_even_with_no_rotation()
        {
            var r = GearObjectiveResolver.Resolve(In(challengeActive: true, profile: "NGUs", dropFarm: true));
            Assert.Equal("NGUs", r.Name);
            Assert.Equal(GearObjectiveResolver.Src.Profile, r.Source);
        }

        [Fact]
        public void No_equipment_challenge_still_beats_a_drop_farm()
        {
            var r = GearObjectiveResolver.Resolve(In(noec: true, dropFarm: true));
            Assert.Equal(GearObjectiveResolver.Src.Noec, r.Source);
            Assert.False(r.Resolved);
        }

        // Regression: with the farm off, every pre-existing row lands exactly where it did.
        [Fact]
        public void With_no_drop_farm_the_table_is_unchanged()
        {
            Assert.Equal(GearObjectiveResolver.Src.Profile,
                GearObjectiveResolver.Resolve(In(profile: "NGUs")).Source);
            Assert.Equal(GearObjectiveResolver.Src.Pin,
                GearObjectiveResolver.Resolve(In(pin: "Adventure")).Source);
            Assert.Equal(GearObjectiveResolver.Src.None,
                GearObjectiveResolver.Resolve(In()).Source);
        }

        [Fact]
        public void Noec_BeatsEverything_AndResolvesToNothing()
        {
            var r = GearObjectiveResolver.Resolve(In(
                noec: true, challengeActive: true, huntActive: true,
                over: "NGUs", profile: "Adventure", pin: "Power"));
            Assert.Equal(GearObjectiveResolver.Src.Noec, r.Source);
            Assert.False(r.Resolved);
            Assert.Null(r.Name);
        }

        [Fact]
        public void ChallengeRotation_BeatsHuntProfileAndPin()
        {
            var r = GearObjectiveResolver.Resolve(In(
                challengeActive: true, over: "NGUs", overIsSegment: false,
                huntActive: true, profile: "Adventure", pin: "Power"));
            Assert.Equal(GearObjectiveResolver.Src.Challenge, r.Source);
            Assert.Equal("NGUs", r.Name);
        }

        [Fact]
        public void AStaleRotationOverrideIsLabelledAsARotation_NotAsSegmentGear()
        {
            // ChallengeOverlay only updates on the 30s advisor tick, but the companion reads this every
            // second — so for up to 30s after a challenge ends the override still holds the rotation
            // value while no challenge is running. The label must follow the flag recorded when the
            // value was WRITTEN, not "is a challenge running right now": otherwise the UI claims the
            // auto profile chose this, which is wrong, and flatly false when AutoProfile is off.
            var r = GearObjectiveResolver.Resolve(In(challengeActive: false, over: "NGUs", overIsSegment: false));
            Assert.Equal(GearObjectiveResolver.Src.Challenge, r.Source);
            Assert.Equal("NGUs", r.Name);                       // the NAME is unchanged from the old code
            Assert.DoesNotContain("auto profile", r.Sentence);
        }

        [Fact]
        public void ChallengeBranchStillConsultsTheOverride_EvenIfFlaggedSegment()
        {
            // The old expression was `override ?? profile` in BOTH branches. The segment flag only ever
            // changes the wording, never which objective is chosen.
            var r = GearObjectiveResolver.Resolve(In(
                challengeActive: true, over: "Time Machine", overIsSegment: true, profile: "Adventure"));
            Assert.Equal("Time Machine", r.Name);
            Assert.Equal(GearObjectiveResolver.Src.Segment, r.Source);
        }

        [Fact]
        public void Hunt_BeatsProfileAndPin_OutsideAChallenge()
        {
            var r = GearObjectiveResolver.Resolve(In(huntActive: true, profile: "Adventure", pin: "Power"));
            Assert.Equal(GearObjectiveResolver.Src.Hunt, r.Source);
            Assert.Equal(GearObjectiveResolver.LootHunter, r.Name);
        }

        [Fact]
        public void Hunt_YieldsInsideAChallenge()
        {
            // The challenge rotation is the advisor reacting to a run the user can't re-gear out of.
            var r = GearObjectiveResolver.Resolve(In(
                challengeActive: true, huntActive: true, over: "Adventure", overIsSegment: false));
            Assert.Equal(GearObjectiveResolver.Src.Challenge, r.Source);
        }

        [Fact]
        public void Hunt_IsCheckedBeforeTheSegmentOverride()
        {
            // Regression: the override is non-null for EVERY auto-profile user (it carries segment gear
            // outside challenges), so an `override ?? hunt` ordering never fell through and the Loot
            // Hunter set was never equipped. Hunt must win here.
            var r = GearObjectiveResolver.Resolve(In(huntActive: true, over: "Time Machine", overIsSegment: true));
            Assert.Equal(GearObjectiveResolver.Src.Hunt, r.Source);
        }

        [Fact]
        public void SegmentGear_BeatsProfileAndPin()
        {
            var r = GearObjectiveResolver.Resolve(In(
                over: "Time Machine", overIsSegment: true, profile: "Adventure", pin: "Power"));
            Assert.Equal(GearObjectiveResolver.Src.Segment, r.Source);
            Assert.Equal("Time Machine", r.Name);
        }

        [Fact]
        public void ProfileTimeline_BeatsThePin()
        {
            // Explicit authoring in the profile outranks the standing pick, so the pin can never fight a
            // breakpoint transition — which is why GearBreakpoints.PerformSwap needed no change.
            var r = GearObjectiveResolver.Resolve(In(profile: "Adventure", pin: "Power"));
            Assert.Equal(GearObjectiveResolver.Src.Profile, r.Source);
            Assert.Equal("Adventure", r.Name);
        }

        [Fact]
        public void Pin_FillsTheHole_WhenNothingElseApplies()
        {
            var r = GearObjectiveResolver.Resolve(In(pin: "Power", pinRespawn: true));
            Assert.Equal(GearObjectiveResolver.Src.Pin, r.Source);
            Assert.Equal("Power", r.Name);
            Assert.True(r.ForceRespawn);
        }

        [Fact]
        public void NothingAnywhere_ResolvesToNone_WithAnActionableSentence()
        {
            var r = GearObjectiveResolver.Resolve(In());
            Assert.Equal(GearObjectiveResolver.Src.None, r.Source);
            Assert.False(r.Resolved);
            // This is the state that used to produce a dead end; the sentence must say what to do.
            Assert.Contains("Pick one", r.Sentence);
        }

        [Fact]
        public void RespawnFlagFollowsTheWinningSource()
        {
            // Pins the PRE-EXISTING pairing: every source except the standing pick takes the profile
            // breakpoint's respawn flag, which is what the old code did on both call sites. The pin is
            // the only one with a flag of its own, and the profile's must not leak onto it.
            var profile = GearObjectiveResolver.Resolve(In(profile: "Adventure", profileRespawn: true, pin: "Power"));
            Assert.True(profile.ForceRespawn);

            var pin = GearObjectiveResolver.Resolve(In(profileRespawn: true, pin: "Power", pinRespawn: false));
            Assert.Equal(GearObjectiveResolver.Src.Pin, pin.Source);
            Assert.False(pin.ForceRespawn);   // the profile's flag must not leak onto the pin
        }

        [Fact]
        public void EverySourceProducesANonEmptySentence()
        {
            var cases = new[]
            {
                In(noec: true),
                In(challengeActive: true, over: "NGUs"),
                In(huntActive: true),
                In(over: "Time Machine", overIsSegment: true),
                In(profile: "Adventure"),
                In(pin: "Power"),
                In()
            };
            foreach (var c in cases)
            {
                var r = GearObjectiveResolver.Resolve(c);
                Assert.False(string.IsNullOrWhiteSpace(r.Sentence));
                Assert.False(string.IsNullOrWhiteSpace(r.Source));
            }
        }

        [Fact]
        public void SentencesArePlainText_NoMarkup()
        {
            // The companion escapes this string before rendering (it must — an objective name can arrive
            // from a hand-edited profile JSON, not just the fixed preset list), so any markup here would
            // reach the user as literal "&lt;b&gt;".
            var cases = new[]
            {
                In(noec: true),
                In(challengeActive: true, over: "NGUs"),
                In(huntActive: true),
                In(over: "Time Machine", overIsSegment: true),
                In(profile: "Adventure"),
                In(pin: "Power"),
                In()
            };
            foreach (var c in cases)
            {
                var s = GearObjectiveResolver.Resolve(c).Sentence;
                Assert.DoesNotContain("<", s);
                Assert.DoesNotContain(">", s);
                Assert.DoesNotContain("&", s);
            }
        }

        [Fact]
        public void AnObjectiveNameIsCarriedVerbatimIntoTheSentence()
        {
            // Including a hostile one — proving the resolver does no escaping of its own and the page
            // must (and does) do it. If this ever "passes" by sanitising here instead, the two layers
            // have started disagreeing about who owns escaping.
            var r = GearObjectiveResolver.Resolve(In(pin: "<script>x</script>"));
            Assert.Contains("<script>x</script>", r.Sentence);
        }

        [Fact]
        public void NullInputs_DoNotThrow()
        {
            var r = GearObjectiveResolver.Resolve(null);
            Assert.Equal(GearObjectiveResolver.Src.None, r.Source);
        }

        [Fact]
        public void EmptyStringsAreTreatedAsUnset()
        {
            var r = GearObjectiveResolver.Resolve(In(over: "", profile: "", pin: ""));
            Assert.Equal(GearObjectiveResolver.Src.None, r.Source);
        }

        [Fact]
        public void GoldenTable_EmptyPin_ReproducesTheOldExpression()
        {
            // The pre-change behaviour, verbatim:
            //   NOEC                       -> nothing
            //   !challenge && hunt         -> "LOOT HUNTER"
            //   override ?? profileObjective
            // Swept over every combination of the five booleans that drove it. If this ever fails, the
            // pin has stopped being invisible to users who never set one.
            string[] overrides = { null, "SEG" };
            string[] profiles = { null, "PRO" };
            foreach (var noec in new[] { false, true })
            foreach (var chal in new[] { false, true })
            foreach (var hunt in new[] { false, true })
            foreach (var over in overrides)
            foreach (var prof in profiles)
            foreach (var seg in new[] { false, true })   // the label flag must never move the NAME
            {
                var r = GearObjectiveResolver.Resolve(In(
                    noec: noec, challengeActive: chal, huntActive: hunt,
                    over: over, overIsSegment: seg, profile: prof, pin: ""));

                string expected;
                if (noec) expected = null;
                else if (!chal && hunt) expected = GearObjectiveResolver.LootHunter;
                else expected = over ?? prof;

                Assert.True(expected == r.Name,
                    $"noec={noec} chal={chal} hunt={hunt} over={over ?? "null"} prof={prof ?? "null"} seg={seg} " +
                    $"=> expected '{expected ?? "null"}' but got '{r.Name ?? "null"}'");
            }
        }
    }
}
