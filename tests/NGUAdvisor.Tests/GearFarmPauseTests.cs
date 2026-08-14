using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE GEAR-FARM CHALLENGE PAUSE (decision record amendment 25 §5).
    //
    // These are not tests on a future feature. Amendment 25 §2 recorded GearFarmAdvisor's output as
    // reaching "a text string and nothing else"; amendment 26 §3 checked and found that FALSE —
    // AdvisorApply.ApplyZones writes Settings.SnipeZone from GearFarmAdvisor.Analyze() and always has.
    // The pause is a MISSING GUARD ON LIVE BEHAVIOUR, which is why the decision it encodes gets a test
    // under it rather than a comment.
    //
    // The eleven challenge codes below are ChallengeDetector.Current()'s vocabulary verbatim
    // (ChallengeDetector.cs:29-39, matching the profile "Challenges" list / BaseRebirth.RCTarget).
    // ChallengeDetector itself reads Main.Character and cannot link here; only the codes it emits
    // cross this boundary, and those are what the pause decides on.
    public class GearFarmPauseTests
    {
        // --- B1: pause on any challenge EXCEPT Laser Sword ------------------------------------

        [Theory]
        [InlineData("NORB")]
        [InlineData("NOTM")]
        [InlineData("NOAUG")]
        [InlineData("NONGU")]
        [InlineData("BLIND")]
        [InlineData("TC")]
        [InlineData("NOEC")]
        [InlineData("100LC")]
        [InlineData("24HR")]
        [InlineData("BASIC")]
        public void Every_challenge_except_laser_sword_pauses_the_farm(string code)
        {
            Assert.True(GearFarmPause.IsPaused(code));
            Assert.Equal(code, GearFarmPause.Signature(code));
        }

        // THE SOLE EXCEPTION, and the reason is in the game's code, not in taste. audit/21 §C4: LSC is
        // the only one of the eleven whose Update() does not test bossID and the only one with no
        // targetBoss() at all — completion is augs[6].augLevel AND .upgradeLevel >= completions + 2
        // ([DECOMP] LaserSwordChallengeController.cs:37-40, :79-92), the only non-boss challenge goal.
        // Its restrictions are "Absolutely nothing!" and it resets nothing, so it does not contend with
        // gear farming for the adventure zone.
        [Fact]
        public void Laser_sword_does_not_pause_the_farm()
        {
            Assert.False(GearFarmPause.IsPaused("LSC"));
            Assert.Null(GearFarmPause.Signature("LSC"));
            Assert.Equal("LSC", GearFarmPause.LaserSword);
        }

        // ChallengeDetector.Current() returns null outside a challenge — the overwhelmingly common case.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_challenge_does_not_pause_the_farm(string code)
        {
            Assert.False(GearFarmPause.IsPaused(code));
            Assert.Null(GearFarmPause.Signature(code));
        }

        // The codes are compared exactly. A lowercase or padded value is not the Laser Sword code and
        // must not be granted its exemption — fail CLOSED, the ZoneGate rule.
        [Theory]
        [InlineData("lsc")]
        [InlineData("Lsc")]
        [InlineData(" LSC")]
        public void Only_the_exact_laser_sword_code_is_exempt(string code)
        {
            Assert.True(GearFarmPause.IsPaused(code));
        }

        // --- B2: the surfaced line fires on state change and does not spam --------------------

        [Fact]
        public void Entering_a_challenge_surfaces_once()
        {
            string latch = null;                              // farm running, nothing said yet

            var sig = GearFarmPause.Signature("TC");
            Assert.True(GearFarmPause.ShouldSurface(sig, latch));
            latch = sig;

            // ApplyZones' gear branch re-evaluates on every 10-minute tick for the whole challenge.
            for (int tick = 0; tick < 50; tick++)
                Assert.False(GearFarmPause.ShouldSurface(GearFarmPause.Signature("TC"), latch));
        }

        [Fact]
        public void A_running_farm_says_nothing_at_all()
        {
            string latch = null;
            for (int tick = 0; tick < 50; tick++)
                Assert.False(GearFarmPause.ShouldSurface(GearFarmPause.Signature(null), latch));
        }

        [Fact]
        public void Laser_sword_never_surfaces_a_pause_from_a_running_farm()
        {
            string latch = null;
            Assert.False(GearFarmPause.ShouldSurface(GearFarmPause.Signature("LSC"), latch));
        }

        [Fact]
        public void The_challenge_clearing_surfaces_the_resume()
        {
            string latch = "24HR";                            // paused, already announced

            var sig = GearFarmPause.Signature(null);
            Assert.Null(sig);
            Assert.True(GearFarmPause.ShouldSurface(sig, latch));
            Assert.Equal(GearFarmPause.ResumeMessage, GearFarmPause.Message(sig));
        }

        // LSC is not a pause, so arriving at it from a real one is a RESUME. Folding "no challenge"
        // and "Laser Sword" onto the same null signature is what makes that fall out.
        [Fact]
        public void Moving_from_a_paused_challenge_into_laser_sword_surfaces_the_resume()
        {
            string latch = "NOEC";

            var sig = GearFarmPause.Signature("LSC");
            Assert.Null(sig);
            Assert.True(GearFarmPause.ShouldSurface(sig, latch));
        }

        // Back-to-back challenges inside one 10-minute window: the farm is paused throughout, but by a
        // different thing, and the operator is told which.
        [Fact]
        public void Swapping_one_challenge_for_another_surfaces_the_new_one()
        {
            string latch = "BASIC";

            var sig = GearFarmPause.Signature("100LC");
            Assert.True(GearFarmPause.ShouldSurface(sig, latch));
            Assert.Contains("100LC", GearFarmPause.Message(sig));
        }

        [Fact]
        public void A_full_pause_and_resume_cycle_surfaces_exactly_twice()
        {
            // One tick per element: no challenge, then a challenge held for three ticks, then clear.
            var ticks = new[] { null, "TC", "TC", "TC", null, null };
            string latch = null;
            int surfaced = 0;

            foreach (var code in ticks)
            {
                var sig = GearFarmPause.Signature(code);
                if (GearFarmPause.ShouldSurface(sig, latch))
                {
                    surfaced++;
                    latch = sig;
                }
            }

            Assert.Equal(2, surfaced);                        // the pause and the resume, nothing else
            Assert.Null(latch);
        }

        // --- the strings ----------------------------------------------------------------------

        // Amendment 25 §5's "Surfaced as" column, verbatim. The challenge code is appended so the
        // operator does not have to go looking for which one; the specified text is the stem.
        [Fact]
        public void The_pause_line_carries_the_specified_text_and_names_the_challenge()
        {
            Assert.Equal("Pausing Farm due to Active Challenge", GearFarmPause.PauseMessage);

            var line = GearFarmPause.Message(GearFarmPause.Signature("NONGU"));
            Assert.StartsWith(GearFarmPause.PauseMessage, line);
            Assert.Contains("NONGU", line);
        }

        [Fact]
        public void Each_transition_carries_a_reason_for_the_feed()
        {
            Assert.Equal("challenge cleared", GearFarmPause.Reason(null));
            Assert.Equal("gear farming does not run inside a challenge", GearFarmPause.Reason("BLIND"));
        }
    }
}
