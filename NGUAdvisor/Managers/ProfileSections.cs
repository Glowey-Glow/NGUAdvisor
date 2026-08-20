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
    //   NOT GOVERNED AT ALL (Wandoos OS / Beards) — these ignore AutoProfile completely and answer to
    //     their own Advisor* toggles. An operator can have Auto Profile on and still be running the
    //     profile's raw beard breakpoints.
    //
    //   MEMBERSHIP SOURCE (Diggers) — the fourth mechanism, and the one this file got WRONG until a
    //     live differential caught it (audit/59 §A, 2026-08-18). Diggers were listed above as "not
    //     governed at all" and the readout said so in as many words. They are: AutoProfile decides
    //     whether the digger ADVISOR is confined to the profile's named list or free to build its
    //     own set. See Diggers() below for the measurement and the one line that does it.
    //
    //   REACHED ONLY FROM THE REBIRTH PATH (Challenges) — the fifth, added at the same time. The
    //     profile's challenge list is not a system with a toggle; it is consulted by
    //     BaseRebirth.TryStartChallenge, under two gates that can each silence twenty authored
    //     entries without a word. See Challenges() below.
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

        // The challenge rotation's three facts. Kept as plain booleans like everything else here: the
        // COUNT of authored entries belongs to the load banner (CustomAllocation.BuildAllocationString),
        // which is the surface that can quote the profile file; what this file answers is whether the
        // list can be reached at all.
        public bool RebirthArmed;         // CustomAllocation.RebirthIsArmed() — the profile schedules a rebirth
        public bool ProfileHasChallenges; // BaseRebirth.ChallengeCount > 0 — entries survived parsing
        public bool AdvisorChallenges;    // the advisor may enter LSC on its own (BaseRebirth.cs:141)
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
            Add(outp, "diggers",  "Diggers",    Diggers(i.ManageDiggers, i.AdvisorDiggers, i.AutoProfile));
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

            Add(outp, "rebirth", "Rebirth", !i.AutoRebirth
                ? V(SectionDriver.Off, "Auto rebirth is off.")
                : i.RebirthArmed
                    ? V(SectionDriver.Profile, "Your profile's rebirth trigger is running. Auto Profile does not touch this.")
                    : V(SectionDriver.Off,
                        "Auto rebirth is on, but this profile arms no rebirth: every entry parsed to a " +
                        "negative time, which DoRebirth filters out before it does anything " +
                        "(CustomAllocation.cs:407-409). Nothing will rebirth this run."));

            Add(outp, "challenges", "Challenges",
                Challenges(i.AutoRebirth, i.RebirthArmed, i.ProfileHasChallenges, i.AdvisorChallenges));

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

        // Wandoos OS / Beards: Auto Profile is irrelevant to these, which is the single most surprising
        // thing on this screen and therefore the thing the sentence has to say out loud.
        //
        // ⚠ DIGGERS USED TO BE ROUTED HERE AND ARE NOT INDEPENDENT — see Diggers() below. Adding a
        // system to this helper is a claim that AutoProfile appears nowhere in its path; check that
        // before reusing it, because the digger claim was made by inspection and was false for a year.
        private static SectionVerdict Independent(bool manage, bool advisor, string what)
        {
            if (!manage) return V(SectionDriver.Off, "This system is not being managed.");
            return advisor
                ? V(SectionDriver.Advisor, "The advisor is choosing your " + what + " set. Auto Profile does not affect this.")
                : V(SectionDriver.Profile, "Your profile's " + what + " breakpoints are running — Auto Profile does not affect this one.");
        }

        // DIGGERS: Auto Profile decides the advisor's MEMBERSHIP SOURCE.
        //
        // This readout claimed "The advisor is choosing your digger set. Auto Profile does not affect
        // this" until a live differential refuted it (audit/59 §A, measured 2026-08-18 on two games run
        // side by side): with Auto Profile OFF the equipped set was exactly the profile's list; with it
        // ON, two diggers the profile never named were running.
        //
        // One line does it — OptimizationAdvisor.CurrentDiggerSet() reads the profile's digger list
        // only inside `if (Main.Settings != null && !Main.Settings.AutoProfile)` (:875-876), and the
        // comment above it states the intent: "AutoProfile has no digger breakpoints, so it always
        // falls through to the advisor's own goal-aware fill-every-slot set (guarded on !AutoProfile so
        // a stale manual profile left loaded while AutoProfile drives can't leak its list in)". The
        // three live cases:
        //
        //   advisor OFF                  -> the profile's timeline runs. CustomAllocation.cs:256 gates
        //                                   that step on ManageDiggers && !AdvisorDiggers and carries no
        //                                   AutoProfile term, so here — and only here — "Auto Profile
        //                                   does not affect this" is a true sentence.
        //   advisor ON, auto profile OFF -> HYBRID: the profile's list is the candidate POOL and the
        //                                   advisor reorders and levels within it, adding nothing
        //                                   (poolFilter, OptimizationAdvisor.cs:870-884).
        //   advisor ON, auto profile ON  -> the advisor builds the whole set and the profile's digger
        //                                   list is never read.
        //
        // Undoing this puts the readout back to telling an operator their authored digger list is in
        // force while diggers they never named are equipped — which is the failure it exists to prevent.
        private static SectionVerdict Diggers(bool manage, bool advisor, bool autoProfile)
        {
            if (!manage) return V(SectionDriver.Off, "This system is not being managed.");
            if (!advisor)
                return V(SectionDriver.Profile,
                    "Your profile's digger breakpoints are running — Auto Profile does not affect this one.");
            return autoProfile
                ? V(SectionDriver.Advisor,
                    "The advisor is choosing your digger set, and with Auto Profile on it chooses the WHOLE " +
                    "set: your profile's digger list is not read, so diggers you never named can be equipped.")
                : V(SectionDriver.Advisor,
                    "The advisor is choosing your digger set from YOUR list: it reorders and levels within the " +
                    "diggers your profile names for this step and adds none. Turning Auto Profile on removes " +
                    "that limit — the advisor then picks the whole set itself. (If your profile names no " +
                    "diggers for the current step, the advisor builds its own set either way.)");
        }

        // CHALLENGES: not a system, a list consulted from one place.
        //
        // Until audit/59 §A this section had no surface anywhere in the product — no load-banner count,
        // no key in AllKeys(), nothing in the companion. A submitted profile authored TWENTY entries and
        // nothing acknowledged they existed.
        //
        // The only path that engages one is the rebirth path: CustomAllocation.DoRebirth() ->
        // TimeRebirth.DoRebirth() -> BaseRebirth.TryStartChallenge() (TimeRebirth.cs:77,
        // BaseRebirth.cs:176). Two gates above it are each capable of silencing the whole list without a
        // word — BaseRebirth.RebirthAvailable() opens with `if (!Settings.AutoRebirth) return false`
        // (:157-158), and DoRebirth returns before reaching it when no entry is armed
        // (CustomAllocation.cs:407-409). Order matters below: report the gate that fires FIRST, because
        // that is the one an operator has to clear.
        //
        // The advisor's own LSC opportunity (TryStartAdvisorLsc, BaseRebirth.cs:137-154) sits BELOW the
        // profile's list — it runs only when no authored entry is eligible — so it is a fallback here,
        // never a competing driver, and it is behind the same two gates.
        private static SectionVerdict Challenges(bool autoRebirth, bool rebirthArmed,
            bool hasChallenges, bool advisorChallenges)
        {
            if (!autoRebirth)
                return V(SectionDriver.Off,
                    "Auto rebirth is off. A challenge is only ever entered from the rebirth path, so nothing " +
                    "will start one — any challenge entries in this profile are inert until auto rebirth is on.");

            if (!rebirthArmed)
                return V(SectionDriver.Off,
                    "This profile arms no rebirth, and a challenge is only ever entered from the rebirth path — " +
                    "so nothing will start one, however many entries the profile lists.");

            if (hasChallenges)
                return V(SectionDriver.Profile,
                    "Your profile's challenge list is the rotation: at the next scheduled rebirth the first entry " +
                    "whose ordinal is still incomplete engages, and an eligible entry pulls the rebirth in ahead " +
                    "of its timer.");

            return advisorChallenges
                ? V(SectionDriver.Advisor,
                    "This profile names no challenges. The advisor may still enter Laser Sword on its own when it " +
                    "judges the window worth it — it never pulls a rebirth in early to do so.")
                : V(SectionDriver.Off,
                    "This profile names no challenges and the challenge advisor is off, so scheduled rebirths will " +
                    "be plain runs.");
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
            yield return new SectionVerdict { Key = "challenges",  Label = "Challenges" };
        }

        // ---- `:percent`: where it is read and where it is not ---------------------------------------

        // THE LOAD-TIME NOTICE for tokens that parse and then do nothing. Lives here, beside the
        // section verdicts, for the same two reasons ProfileSections itself does: it answers "is this
        // part of my profile actually driving the game", and its VALUE IS ENTIRELY IN IT BEING RIGHT.
        //
        // CapPercent has exactly one reader — ResourceBreakpoint.UpdateMaxAllocation (:71-79) — and the
        // constraint path never calls it; ConstraintLayerBridge hands each lane a budget through
        // OfferBudget (:96-102) instead. So on Energy and Magic, with the constraint allocator on (the
        // default), an authored percentage does nothing: `CAPTM:10` was measured taking 12.5-19.3% of
        // the pool on a live save (audit/59 §A, 2026-08-18).
        //
        // ⚠ IT IS NOT DEAD EVERYWHERE, AND SAYING SO WOULD BE THE SAME LIE POINTED THE OTHER WAY.
        // R3 is deliberately outside the constraint layer, so R3Breakpoints.cs:66 calls
        // UpdateMaxAllocation itself and the percentage is honoured as written; and the legacy share
        // loop behind the ConstraintAllocator kill switch calls it too (EnergyBreakpoints.cs:51,
        // MagicBreakpoints.cs:45). 15 shipped presets and 57 sample profiles use the syntax, so a
        // blanket "`:percent` does nothing" would mislabel every one of them.
        //
        // ── SETTLED 2026-08-18 (operator ruling), replacing audit/59 §1's three open options ──────
        //
        // The ADVISOR owns percentages. It sizes every lane to the best optimal outcome from live
        // state, and an authored `:percent` is read as the operator saying "not this system, I will
        // drive it". So the token is neither ignored nor refused: THE SYSTEM IT NAMES IS FORCED INTO
        // MANUAL MODE. ConstraintLayerBridge partitions it out of the optimiser and runs it literally
        // — UpdateMaxAllocation then Allocate against the freshly reclaimed pool — and the advisor
        // optimises whatever is left.
        //
        // That is why this notice must WARN and not merely describe. Authoring a percent now costs
        // the advisor on that system, which is a real trade the operator has to make knowingly; the
        // old wording ("IGNORED... switching the constraint allocator off restores it") described a
        // behaviour that no longer exists and would now be actively misleading.
        //
        // Returns the lines to print, or an EMPTY list when the profile authors no percent at all —
        // never a line saying "no percentages found", which is noise on the majority of loads.
        public static List<string> PercentNotice(bool constraintAllocatorOn,
            IList<string> energyMagicTokens, IList<string> r3Tokens, IList<string> droppedTokens)
        {
            var outp = new List<string>();
            int em = energyMagicTokens == null ? 0 : energyMagicTokens.Count;
            int r3 = r3Tokens == null ? 0 : r3Tokens.Count;
            int bad = droppedTokens == null ? 0 : droppedTokens.Count;
            if (em == 0 && r3 == 0 && bad == 0)
                return outp;

            outp.Add("⚠ \":percent\" FORCES MANUAL MODE. The advisor sizes every lane it owns to the " +
                     "best outcome it can compute from live state. A lane you give an explicit " +
                     "percentage is one you are driving yourself, so the advisor stops optimising it:");

            if (em > 0)
                outp.Add("   Energy/Magic: " + Join(energyMagicTokens) + " — MANUAL. " +
                         (constraintAllocatorOn
                            ? "Each of these claims its authored share off the top, before the " +
                              "advisor allocates, and is excluded from the optimiser. "
                            : "The constraint allocator is off, so the original share loop runs and " +
                              "reads these as written. ") +
                         "Remove the \":percent\" to hand the system back to the advisor.");

            if (r3 > 0)
                outp.Add("   R3: " + Join(r3Tokens) + " — MANUAL. R3 is not routed through the " +
                         "constraint allocator, so the percentage means what it says. Note the R3 " +
                         "tail is otherwise priced by the advisor now (marginal value density), and " +
                         "a percent here opts that lane out of the pricing.");

            if (bad > 0)
                outp.Add("   Dropped at parse: " + Join(droppedTokens) + " — the text after ':' is not " +
                         "a whole number, and a malformed percent skips the WHOLE token rather than " +
                         "defaulting to 100%. Those lanes are not in your timeline at all.");

            return outp;
        }

        private static string Join(IList<string> tokens)
        {
            var arr = new string[tokens.Count];
            tokens.CopyTo(arr, 0);
            return string.Join(", ", arr);
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
