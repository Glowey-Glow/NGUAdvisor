using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // WHICH PARTS OF THE LOADED PROFILE ARE ACTUALLY DRIVING THE GAME.
    //
    // The profile load banner prints "4 Energy Breakpoints / 4 Magic Breakpoints / ..." — those are
    // Array.Length of what the JSON parser found, produced by BreakpointWrapper.BuildAllocationString
    // before any of the flags below even exist. It is a count of what was READ, and it reads to an
    // operator as a statement that those breakpoints are running. Frequently they are not.
    //
    // ⚠ "AUTO PROFILE" IS THREE DIFFERENT MECHANISMS WEARING ONE FLAG NAME, and that is the whole
    // reason this file exists rather than a one-line banner:
    //
    //   SUBSTITUTION (Energy / Magic / R3) — the call sites in CustomAllocation have NO AutoProfile
    //     check and always run. The swap happens a layer down, inside the shared priority builder
    //     (ChallengeOverlay.TransformPriorities): the profile's own list is replaced by a
    //     segment-derived one, and the profile's list survives only as a fallback if the generated
    //     list comes back empty — which in practice never happens.
    //
    //   HARD BLOCK (Gear) — the profile's gear path simply does not run. What replaces it is a
    //     SEPARATE flag, AdvisorGearRefresh, checked at a different call site entirely.
    //
    //   NOT GOVERNED AT ALL (Wandoos OS / Diggers / Beards) — these ignore AutoProfile completely and
    //     answer to their own Advisor* toggles. An operator can have Auto Profile on and still be
    //     running the profile's raw digger breakpoints.
    //
    // ⚠ AND THE GEAR GAP. Because the block and its replacement are two independent flags with no
    // coupling anywhere — the companion's setAutoProfile command writes only AutoProfile — the state
    // (AutoProfile ON, AdvisorGearRefresh OFF) leaves gear driven by NEITHER path. Nothing manages it
    // and nothing says so. This file reports that state as Nobody rather than guessing a winner:
    // choosing one would be a behaviour change hidden inside a readout.
    //
    // Unity-free on purpose. Everything here is a function of plain booleans, so the verdicts are
    // testable without the game build — which matters, because the value of this readout is entirely
    // in it being RIGHT, and the rules it encodes live scattered across five files.
    public enum SectionDriver
    {
        Profile,   // the loaded profile's breakpoints are what run
        Advisor,   // the advisor overrides or replaces them
        Nobody,    // neither path is armed — the gap, not a resting state
        Off        // the operator switched this system off entirely
    }

    public struct SectionVerdict
    {
        public string Key;        // stable id, matches the profile section
        public string Label;      // what the operator calls it
        public SectionDriver Driver;
        public string Reason;     // one sentence, written for the operator, not for a maintainer
    }

    // Plain-old inputs so the decision can be exercised without Main.Settings or a Character.
    public struct SectionInputs
    {
        public bool GlobalEnabled;
        public bool AutoProfile;
        public bool ManageEnergy, ManageMagic, ManageR3;
        public bool ManageGear, AdvisorGearRefresh;
        public bool ManageWandoos, AdvisorWandoosOS;
        public bool ManageDiggers, AdvisorDiggers;
        public bool ManageBeards, AdvisorBeards;
        public bool ManageNGUDiff, ManageConsumables, AutoRebirth;
        public bool NguTrackOverrideActive;   // LevelPlanner is inside its Evil ch.5 window this tick
    }

    public static class ProfileSections
    {
        public static List<SectionVerdict> Evaluate(SectionInputs i)
        {
            var outp = new List<SectionVerdict>();

            // Master switch first: with automation off nothing below is running, and saying "parked"
            // per-section would imply the advisor is holding the wheel when nobody is.
            if (!i.GlobalEnabled)
            {
                foreach (var k in AllKeys())
                    outp.Add(new SectionVerdict { Key = k.Key, Label = k.Label, Driver = SectionDriver.Off,
                        Reason = "Automation is off — nothing is being managed." });
                return outp;
            }

            Add(outp, "energy", "Energy", Pool(i.ManageEnergy, i.AutoProfile, "energy"));
            Add(outp, "magic",  "Magic",  Pool(i.ManageMagic,  i.AutoProfile, "magic"));
            Add(outp, "r3",     "R3",     Pool(i.ManageR3,     i.AutoProfile, "R3"));

            // Gear is the only section where both paths can be dark at once.
            if (!i.ManageGear)
                Add(outp, "gear", "Gear", V(SectionDriver.Off, "Gear management is off."));
            else if (!i.AutoProfile)
                Add(outp, "gear", "Gear", V(SectionDriver.Profile, "Your profile's gear breakpoints are running."));
            else if (i.AdvisorGearRefresh)
                Add(outp, "gear", "Gear", V(SectionDriver.Advisor,
                    "Auto Profile switched your gear breakpoints off; the advisor is choosing gear instead."));
            else
                Add(outp, "gear", "Gear", V(SectionDriver.Nobody,
                    "Nothing is driving this. Auto Profile switched your gear breakpoints off, and the advisor's " +
                    "own gear refresh is also off — so no gear decisions are being made at all."));

            Add(outp, "wandoos",  "Wandoos OS", Independent(i.ManageWandoos, i.AdvisorWandoosOS, "Wandoos OS"));
            Add(outp, "diggers",  "Diggers",    Independent(i.ManageDiggers, i.AdvisorDiggers,   "digger"));
            Add(outp, "beards",   "Beards",     Independent(i.ManageBeards,  i.AdvisorBeards,    "beard"));

            Add(outp, "ngudiff", "NGU difficulty", !i.ManageNGUDiff
                ? V(SectionDriver.Off, "NGU difficulty management is off.")
                : i.NguTrackOverrideActive
                    ? V(SectionDriver.Advisor,
                        "Your timeline is running, but the advisor is also steering the track inside its " +
                        "end-of-run Evil window — both write it, and the last one each tick wins.")
                    : V(SectionDriver.Profile, "Your profile's NGU difficulty timeline is running."));

            Add(outp, "consumables", "Consumables", i.ManageConsumables
                ? V(SectionDriver.Profile, "Your profile's consumable breakpoints are running. Auto Profile does not touch these.")
                : V(SectionDriver.Off, "Consumable management is off."));

            Add(outp, "rebirth", "Rebirth", i.AutoRebirth
                ? V(SectionDriver.Profile, "Your profile's rebirth trigger is running. Auto Profile does not touch this.")
                : V(SectionDriver.Off, "Auto rebirth is off."));

            return outp;
        }

        // Energy/Magic/R3: the profile's list is REPLACED, not blocked. Saying "off" here would be
        // wrong in a way that matters — the lanes are being funded, just not from your list.
        private static SectionVerdict Pool(bool manage, bool autoProfile, string what)
        {
            if (!manage) return V(SectionDriver.Off, "This pool is not being managed.");
            if (!autoProfile) return V(SectionDriver.Profile, "Your profile's " + what + " breakpoints are running.");
            return V(SectionDriver.Advisor,
                "Auto Profile is generating this segment's " + what + " list instead of using yours. " +
                "Your breakpoints are only consulted if the generated list comes back empty.");
        }

        // Wandoos OS / Diggers / Beards: Auto Profile is irrelevant to these, which is the single most
        // surprising thing on this screen and therefore the thing the sentence has to say out loud.
        private static SectionVerdict Independent(bool manage, bool advisor, string what)
        {
            if (!manage) return V(SectionDriver.Off, "This system is not being managed.");
            return advisor
                ? V(SectionDriver.Advisor, "The advisor is choosing your " + what + " set. Auto Profile does not affect this.")
                : V(SectionDriver.Profile, "Your profile's " + what + " breakpoints are running — Auto Profile does not affect this one.");
        }

        private static SectionVerdict V(SectionDriver d, string reason)
            => new SectionVerdict { Driver = d, Reason = reason };

        private static void Add(List<SectionVerdict> list, string key, string label, SectionVerdict v)
        {
            v.Key = key; v.Label = label; list.Add(v);
        }

        // One list, so the all-off early return and the normal path cannot drift apart on section names.
        private static IEnumerable<SectionVerdict> AllKeys()
        {
            yield return new SectionVerdict { Key = "energy",      Label = "Energy" };
            yield return new SectionVerdict { Key = "magic",       Label = "Magic" };
            yield return new SectionVerdict { Key = "r3",          Label = "R3" };
            yield return new SectionVerdict { Key = "gear",        Label = "Gear" };
            yield return new SectionVerdict { Key = "wandoos",     Label = "Wandoos OS" };
            yield return new SectionVerdict { Key = "diggers",     Label = "Diggers" };
            yield return new SectionVerdict { Key = "beards",      Label = "Beards" };
            yield return new SectionVerdict { Key = "ngudiff",     Label = "NGU difficulty" };
            yield return new SectionVerdict { Key = "consumables", Label = "Consumables" };
            yield return new SectionVerdict { Key = "rebirth",     Label = "Rebirth" };
        }

        public static string DriverName(SectionDriver d)
        {
            switch (d)
            {
                case SectionDriver.Profile: return "profile";
                case SectionDriver.Advisor: return "advisor";
                case SectionDriver.Nobody:  return "nobody";
                default:                    return "off";
            }
        }
    }
}
