using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The verdicts behind the Profiles page's live/parked badges.
    //
    // Worth pinning rather than eyeballing because the rules they encode are NOT in one place in the
    // product — they are spread across CustomAllocation's per-step guards, ChallengeOverlay's priority
    // substitution, AdvisorApply's separate gear-refresh check, and three independent Advisor* toggles.
    // A readout that gets this wrong is worse than no readout: it would tell an operator their profile
    // is driving something the advisor has quietly taken over, which is the exact confusion it exists
    // to end.
    public class ProfileSectionsTests
    {
        // Everything on, auto profile off: the profile is genuinely driving.
        private static SectionInputs AllOn() => new SectionInputs
        {
            GlobalEnabled = true, AutoProfile = false,
            ManageEnergy = true, ManageMagic = true, ManageR3 = true,
            ManageGear = true, AdvisorGearRefresh = false,
            ManageWandoos = true, AdvisorWandoosOS = false,
            ManageDiggers = true, AdvisorDiggers = false,
            ManageBeards = true, AdvisorBeards = false,
            ManageNGUDiff = true, ManageConsumables = true, AutoRebirth = true,
            NguTrackOverrideActive = false
        };

        private static SectionVerdict Get(SectionInputs i, string key)
            => ProfileSections.Evaluate(i).Single(v => v.Key == key);

        [Fact]
        public void Every_section_is_reported_exactly_once()
        {
            var v = ProfileSections.Evaluate(AllOn());
            Assert.Equal(10, v.Count);
            Assert.Equal(v.Count, v.Select(x => x.Key).Distinct().Count());
            Assert.All(v, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
            Assert.All(v, x => Assert.False(string.IsNullOrWhiteSpace(x.Label)));
        }

        [Fact]
        public void With_auto_profile_off_the_profile_drives_everything_it_manages()
        {
            var v = ProfileSections.Evaluate(AllOn());
            Assert.All(v, x => Assert.Equal(SectionDriver.Profile, x.Driver));
        }

        // THE HEADLINE: auto profile does NOT mean "the advisor has everything".
        [Fact]
        public void Auto_profile_takes_the_pools_and_gear_but_not_wandoos_diggers_or_beards()
        {
            var i = AllOn(); i.AutoProfile = true; i.AdvisorGearRefresh = true;

            Assert.Equal(SectionDriver.Advisor, Get(i, "energy").Driver);
            Assert.Equal(SectionDriver.Advisor, Get(i, "magic").Driver);
            Assert.Equal(SectionDriver.Advisor, Get(i, "r3").Driver);
            Assert.Equal(SectionDriver.Advisor, Get(i, "gear").Driver);

            // These three answer to their own toggles and ignore AutoProfile entirely.
            Assert.Equal(SectionDriver.Profile, Get(i, "wandoos").Driver);
            Assert.Equal(SectionDriver.Profile, Get(i, "diggers").Driver);
            Assert.Equal(SectionDriver.Profile, Get(i, "beards").Driver);

            // And these are never touched by it at all.
            Assert.Equal(SectionDriver.Profile, Get(i, "ngudiff").Driver);
            Assert.Equal(SectionDriver.Profile, Get(i, "consumables").Driver);
            Assert.Equal(SectionDriver.Profile, Get(i, "rebirth").Driver);
        }

        // THE GAP. Auto profile hard-blocks the profile's gear path; AdvisorGearRefresh is what replaces
        // it; nothing couples the two. With one on and the other off, gear is driven by NOBODY — and the
        // readout has to say that rather than defaulting to a comfortable answer.
        [Fact]
        public void Auto_profile_on_with_gear_refresh_off_leaves_gear_driven_by_nobody()
        {
            var i = AllOn(); i.AutoProfile = true; i.AdvisorGearRefresh = false;
            var gear = Get(i, "gear");
            Assert.Equal(SectionDriver.Nobody, gear.Driver);
            Assert.Contains("Nothing is driving this", gear.Reason);
        }

        [Fact]
        public void The_gap_needs_both_flags_to_appear()
        {
            var a = AllOn(); a.AutoProfile = true; a.AdvisorGearRefresh = true;
            Assert.NotEqual(SectionDriver.Nobody, Get(a, "gear").Driver);

            var b = AllOn(); b.AutoProfile = false; b.AdvisorGearRefresh = false;
            Assert.Equal(SectionDriver.Profile, Get(b, "gear").Driver);
        }

        [Fact]
        public void Turning_a_system_off_reads_as_off_not_as_parked()
        {
            var i = AllOn(); i.AutoProfile = true;
            i.ManageEnergy = false; i.ManageGear = false; i.ManageDiggers = false;

            Assert.Equal(SectionDriver.Off, Get(i, "energy").Driver);
            Assert.Equal(SectionDriver.Off, Get(i, "gear").Driver);
            Assert.Equal(SectionDriver.Off, Get(i, "diggers").Driver);
            // Off is not the gap: an operator who switched gear off is not missing anything.
            Assert.DoesNotContain("Nothing is driving this", Get(i, "gear").Reason);
        }

        [Fact]
        public void Master_switch_off_reports_everything_off_and_blames_nothing_else()
        {
            var i = AllOn(); i.GlobalEnabled = false; i.AutoProfile = true;
            var v = ProfileSections.Evaluate(i);
            Assert.Equal(10, v.Count);
            Assert.All(v, x => Assert.Equal(SectionDriver.Off, x.Driver));
            Assert.All(v, x => Assert.Contains("Automation is off", x.Reason));
        }

        [Fact]
        public void The_all_off_path_reports_the_same_sections_as_the_normal_path()
        {
            var on = ProfileSections.Evaluate(AllOn()).Select(x => x.Key + "|" + x.Label).OrderBy(x => x);
            var off = AllOn(); off.GlobalEnabled = false;
            var offKeys = ProfileSections.Evaluate(off).Select(x => x.Key + "|" + x.Label).OrderBy(x => x);
            Assert.Equal(on, offKeys);   // the early return must not drift from the main list
        }

        // Two writers on one field. The profile's timeline owns it, but LevelPlanner also steers it
        // inside the Evil ch.5 window, and neither knows about the other.
        [Fact]
        public void Ngu_track_reports_the_advisor_only_while_the_override_window_is_open()
        {
            var closed = AllOn();
            Assert.Equal(SectionDriver.Profile, Get(closed, "ngudiff").Driver);

            var open = AllOn(); open.NguTrackOverrideActive = true;
            var v = Get(open, "ngudiff");
            Assert.Equal(SectionDriver.Advisor, v.Driver);
            Assert.Contains("both write it", v.Reason);
        }

        [Fact]
        public void Independent_toggles_are_reported_independently_of_auto_profile()
        {
            var i = AllOn(); i.AutoProfile = true;
            i.AdvisorDiggers = true;      // advisor takes diggers
            i.AdvisorBeards = false;      // profile keeps beards
            Assert.Equal(SectionDriver.Advisor, Get(i, "diggers").Driver);
            Assert.Equal(SectionDriver.Profile, Get(i, "beards").Driver);
            Assert.Contains("Auto Profile does not affect this", Get(i, "beards").Reason);
        }

        [Fact]
        public void Pool_reasons_say_replaced_rather_than_stopped()
        {
            var i = AllOn(); i.AutoProfile = true;
            var e = Get(i, "energy");
            // The lanes ARE funded under auto profile — just not from the operator's list. "Off" would
            // be a materially wrong thing to imply here.
            Assert.Contains("generating", e.Reason);
            Assert.Contains("empty", e.Reason);
        }

        [Theory]
        [InlineData(SectionDriver.Profile, "profile")]
        [InlineData(SectionDriver.Advisor, "advisor")]
        [InlineData(SectionDriver.Nobody, "nobody")]
        [InlineData(SectionDriver.Off, "off")]
        public void Driver_names_are_stable_wire_values(SectionDriver d, string expected)
            => Assert.Equal(expected, ProfileSections.DriverName(d));
    }
}
