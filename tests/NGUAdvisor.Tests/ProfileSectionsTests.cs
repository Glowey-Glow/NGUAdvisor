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
            NguTrackOverrideActive = false,
            // The challenge rotation's three facts. A profile that arms a rebirth and lists challenges
            // is the case where the profile genuinely drives them — the same "everything on" premise
            // the rest of this bag encodes.
            RebirthArmed = true, ProfileHasChallenges = true, AdvisorChallenges = false
        };

        private static SectionVerdict Get(SectionInputs i, string key)
            => ProfileSections.Evaluate(i).Single(v => v.Key == key);

        [Fact]
        public void Every_section_is_reported_exactly_once()
        {
            var v = ProfileSections.Evaluate(AllOn());
            Assert.Equal(11, v.Count);
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

            // These three answer to their own toggles first — with the advisor off for each, the
            // profile's own breakpoints run even under Auto Profile. (Diggers diverge once their
            // advisor IS on; see the digger tests below.)
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
            Assert.Equal(11, v.Count);
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

        // ---- diggers: the verdict that was measurably wrong (audit/59 §A) --------------------------

        // THE DEFECT ITSELF. This readout said "The advisor is choosing your digger set. Auto Profile
        // does not affect this" in every advisor-on configuration. It does affect it:
        // OptimizationAdvisor.CurrentDiggerSet reads the profile's list only under `!AutoProfile`, and
        // the live differential measured two diggers appearing that the profile never named once Auto
        // Profile was on. The sentence that made the false claim must not come back.
        [Fact]
        public void The_digger_verdict_never_claims_auto_profile_is_irrelevant_while_the_advisor_owns_them()
        {
            var off = AllOn(); off.AdvisorDiggers = true; off.AutoProfile = false;
            var on  = AllOn(); on.AdvisorDiggers  = true; on.AutoProfile  = true;

            Assert.Equal(SectionDriver.Advisor, Get(off, "diggers").Driver);
            Assert.Equal(SectionDriver.Advisor, Get(on, "diggers").Driver);

            Assert.DoesNotContain("Auto Profile does not affect this", Get(off, "diggers").Reason);
            Assert.DoesNotContain("Auto Profile does not affect this", Get(on, "diggers").Reason);

            // And the two configurations must not read identically — the whole finding is that they
            // are different behaviours, so one sentence for both is the bug in another form.
            Assert.NotEqual(Get(off, "diggers").Reason, Get(on, "diggers").Reason);
        }

        // With Auto Profile OFF the advisor is confined to the profile's named diggers (the hybrid
        // pool). Measured: the live set was exactly the profile's list.
        [Fact]
        public void With_auto_profile_off_the_digger_advisor_is_confined_to_the_profile_list()
        {
            var i = AllOn(); i.AdvisorDiggers = true; i.AutoProfile = false;
            var v = Get(i, "diggers");
            Assert.Contains("from YOUR list", v.Reason);
            Assert.Contains("adds none", v.Reason);
        }

        // With Auto Profile ON the profile's digger list is not read at all. Measured: two diggers the
        // profile never named were equipped — so the readout has to say that can happen.
        [Fact]
        public void With_auto_profile_on_the_digger_advisor_owns_the_whole_set()
        {
            var i = AllOn(); i.AdvisorDiggers = true; i.AutoProfile = true;
            var v = Get(i, "diggers");
            Assert.Contains("WHOLE", v.Reason);
            Assert.Contains("never named", v.Reason);
        }

        // The one case where the old sentence was true, and it must stay true: with the digger advisor
        // off, CustomAllocation's digger step carries no AutoProfile term at all.
        [Fact]
        public void With_the_digger_advisor_off_auto_profile_really_is_irrelevant()
        {
            var i = AllOn(); i.AdvisorDiggers = false; i.AutoProfile = true;
            var v = Get(i, "diggers");
            Assert.Equal(SectionDriver.Profile, v.Driver);
            Assert.Contains("Auto Profile does not affect this one", v.Reason);
        }

        // ---- challenges: the section that had no surface at all ------------------------------------

        [Fact]
        public void The_challenge_rotation_is_reported_as_a_section()
        {
            var keys = ProfileSections.Evaluate(AllOn()).Select(x => x.Key).ToList();
            Assert.Contains("challenges", keys);
        }

        // Both gates above TryStartChallenge live in the rebirth path, and each one silences the whole
        // authored list. The verdict names the one that fires FIRST, because that is the one to clear.
        [Fact]
        public void Auto_rebirth_off_makes_every_authored_challenge_inert()
        {
            var i = AllOn(); i.AutoRebirth = false;
            var v = Get(i, "challenges");
            Assert.Equal(SectionDriver.Off, v.Driver);
            Assert.Contains("Auto rebirth is off", v.Reason);
        }

        [Fact]
        public void An_unarmed_profile_makes_every_authored_challenge_inert_too()
        {
            var i = AllOn(); i.AutoRebirth = true; i.RebirthArmed = false;
            var v = Get(i, "challenges");
            Assert.Equal(SectionDriver.Off, v.Driver);
            Assert.Contains("arms no rebirth", v.Reason);

            // The rebirth section has to agree — the same fact, reported on the line an operator is
            // most likely to be looking at when they count breakpoints.
            var rb = Get(i, "rebirth");
            Assert.Equal(SectionDriver.Off, rb.Driver);
            Assert.Contains("arms no rebirth", rb.Reason);
        }

        [Fact]
        public void A_reachable_challenge_list_reports_the_profile_as_the_rotation()
        {
            var v = Get(AllOn(), "challenges");
            Assert.Equal(SectionDriver.Profile, v.Driver);
            Assert.Contains("challenge list is the rotation", v.Reason);
        }

        // No authored entries: the advisor's own LSC opportunity is the only thing that can enter a
        // challenge, and it is a fallback rather than a rival — so it is reported only when it is on.
        [Fact]
        public void With_no_authored_challenges_the_advisor_is_reported_only_when_it_is_armed()
        {
            var withAdvisor = AllOn();
            withAdvisor.ProfileHasChallenges = false; withAdvisor.AdvisorChallenges = true;
            Assert.Equal(SectionDriver.Advisor, Get(withAdvisor, "challenges").Driver);
            Assert.Contains("Laser Sword", Get(withAdvisor, "challenges").Reason);

            var without = AllOn();
            without.ProfileHasChallenges = false; without.AdvisorChallenges = false;
            Assert.Equal(SectionDriver.Off, Get(without, "challenges").Driver);
            Assert.Contains("plain runs", Get(without, "challenges").Reason);
        }

        // ---- `:percent`: the notice that must not overclaim (audit/59 §A) --------------------------

        private static readonly string[] EM = { "CAPTM:10", "CAPWAN:60" };
        private static readonly string[] R3 = { "HACK-3:20" };
        private static readonly string[] Bad = { "CAPTM:ten" };
        private static readonly string[] None = new string[0];

        // Silence when there is nothing to say. The majority of profiles author no percent at all, and
        // a line reading "no percentages found" on every one of them is how a notice stops being read.
        [Fact]
        public void A_profile_with_no_percent_tokens_gets_no_notice()
        {
            Assert.Empty(ProfileSections.PercentNotice(true, None, None, None));
            Assert.Empty(ProfileSections.PercentNotice(false, None, None, None));
        }

        // THE MEASURED HALF: with the constraint allocator on, an Energy/Magic percentage does nothing.
        [Fact]
        public void Energy_and_magic_percentages_are_reported_as_manual_mode()
        {
            // ⚠ THIS TEST USED TO ASSERT "IGNORED", AND THAT WAS RIGHT UNTIL 2026-08-18. The operator
            // ruling settled audit/59 §1: the advisor owns percentages, and an authored one is read as
            // "not this system, I'll drive". So the lane is neither ignored nor refused — it is pulled
            // out of the optimiser and honoured literally. The old wording described a behaviour that
            // no longer exists, which is why the assertion moved rather than the code.
            var lines = string.Join("\n", ProfileSections.PercentNotice(true, EM, None, None));
            Assert.Contains("CAPTM:10", lines);
            Assert.Contains("MANUAL", lines);
            Assert.DoesNotContain("IGNORED", lines);
        }

        // THE WARNING IS THE POINT, not the description. Authoring a percent now COSTS the advisor on
        // that system, which is a trade the operator has to make knowingly. A notice that merely says
        // what happens, without saying what it costs, is the failure this whole surface exists to end.
        [Fact]
        public void The_notice_says_what_a_percent_costs_and_how_to_undo_it()
        {
            foreach (var on in new[] { true, false })
            {
                var lines = string.Join("\n", ProfileSections.PercentNotice(on, EM, R3, None));
                Assert.Contains("FORCES MANUAL MODE", lines);
                Assert.Contains("stops optimising", lines);
                Assert.Contains("hand the system back to the advisor", lines);
            }
        }

        // `:percent` is live on R3 and live on the legacy path. Both were already true; what changed
        // is that being live now also means being OUT of the advisor's hands, on every path.
        [Fact]
        public void Every_path_reports_manual_never_inert()
        {
            foreach (var on in new[] { true, false })
            {
                var r3 = string.Join("\n", ProfileSections.PercentNotice(on, None, R3, None));
                Assert.Contains("HACK-3:20", r3);
                Assert.Contains("MANUAL", r3);
                Assert.DoesNotContain("IGNORED", r3);

                var em = string.Join("\n", ProfileSections.PercentNotice(on, EM, None, None));
                Assert.Contains("MANUAL", em);
                Assert.DoesNotContain("IGNORED", em);
            }
        }

        // The R3 line has to name the thing it opts out of, now that the R3 tail is priced.
        [Fact]
        public void The_r3_line_says_a_percent_opts_out_of_the_advisors_pricing()
        {
            var lines = string.Join("\n", ProfileSections.PercentNotice(true, None, R3, None));
            Assert.Contains("marginal value density", lines);
            Assert.Contains("opts that lane out", lines);
        }

        // The same profile can hold both, and the two must appear side by side rather than one being
        // generalised over the other.
        [Fact]
        public void A_profile_with_both_gets_both_lines_at_once()
        {
            var lines = string.Join("\n", ProfileSections.PercentNotice(true, EM, R3, None));
            Assert.Contains("Energy/Magic:", lines);
            Assert.Contains("R3:", lines);
        }

        // Same punctuation, completely different outcome: a percent that will not parse skips the
        // WHOLE token, so the lane is not in the timeline at all — which is a lost lane, not a lost
        // percentage, and has to read differently.
        [Fact]
        public void A_malformed_percent_is_reported_as_a_dropped_token_not_an_ignored_one()
        {
            var lines = string.Join("\n", ProfileSections.PercentNotice(true, None, None, Bad));
            Assert.Contains("CAPTM:ten", lines);
            Assert.Contains("Dropped at parse", lines);
            Assert.Contains("not in your timeline at all", lines);
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
