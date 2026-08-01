namespace NGUAdvisor.Managers
{
    // WHICH gear objective is in force, and WHY — one definition, shared by the equip path
    // (AdvisorApply.ApplyGearRefresh), the manual button (AdvisorApply.ForceGearReoptimize) and the
    // companion's Loadouts readout.
    //
    // The same four-way ternary was written out twice and had to be kept in step by hand. Worse, when
    // it resolved to nothing the user was simply told "No gear objective is active right now" with no
    // way to fix it from the UI — there was no control anywhere that set one.
    //
    // KNOWN, PRE-EXISTING, DELIBERATELY UNCHANGED: the challenge-rotation and segment rows pair their
    // objective NAME with the profile breakpoint's respawn FLAG (ProfileRespawn). That is what the old
    // code did on both paths, so it is preserved here rather than quietly "fixed" — changing it would
    // alter which set gets equipped during a challenge and needs its own in-game validation. The pin is
    // the only source that carries a respawn flag of its own.
    //
    // Deliberately PURE: no Unity, no game reads, no statics. Every input is passed in, so the whole
    // precedence table is unit-testable and the "empty pin behaves exactly like before" guarantee is a
    // test rather than a claim. Managers/GearObjectiveApply.cs does the live reading.
    public static class GearObjectiveResolver
    {
        // Where the winning objective came from. The UI turns this into a sentence, so a new source
        // must also get a sentence below.
        public static class Src
        {
            public const string Noec = "noec";           // No-Equipment Challenge: nothing may be worn
            public const string Challenge = "challenge"; // the challenge overlay's push/growth rotation
            public const string Hunt = "hunt";           // gear hunt's hybrid Loot Hunter set
            public const string Segment = "segment";     // the auto-profile's segment gear
            public const string Profile = "profile";     // the active profile's gear timeline
            public const string Pin = "pin";             // the user's standing pick (Loadouts -> Main)
            public const string None = "none";
        }

        public sealed class Inputs
        {
            public bool Noec;                  // ChallengeDetector.Current() == "NOEC"
            public bool ChallengeActive;       // ChallengeDetector.Current() != null
            public bool HuntActive;            // GearHunter.Active
            public string Override;            // ChallengeOverlay.GearObjectiveOverride
            public bool OverrideIsSegment;     // true when the override is segment gear, not a challenge rotation
            public string ProfileObjective;    // GearBreakpoints.ActiveObjective
            public bool ProfileRespawn;        // GearBreakpoints.ActiveForceRespawn
            public string Pin;                 // Settings.GearObjective ("" = follow the profile)
            public bool PinRespawn;            // Settings.GearObjectiveRespawn
        }

        public sealed class Result
        {
            public string Name;            // null when nothing is in force
            public bool ForceRespawn;
            public string Source;          // one of Src.*
            public string Sentence;        // one line for the companion, already human-readable
            public bool Resolved => !string.IsNullOrEmpty(Name);
        }

        // The sentinel GearHunter uses instead of a real objective — its set is a hybrid (curated
        // accessory pool + the optimiser's best P/T), so it never goes through FindObjective.
        public const string LootHunter = "LOOT HUNTER";

        private static bool Has(string s) => !string.IsNullOrEmpty(s);

        // Sentences are PLAIN TEXT — deliberately no markup. The companion escapes whatever arrives here
        // before rendering it, and it must: an objective name can reach this from a hand-edited profile
        // JSON or settings.json, not just from the fixed preset list. Emphasis is the page's job.

        // Precedence, highest first. Everything except the PIN reproduces the pre-existing expression at
        // AdvisorApply.ApplyGearRefresh exactly:
        //     NOEC -> nothing;  (!challenge && hunt) ? LOOT HUNTER : (override ?? profileObjective)
        // The PIN is appended LAST, so it can only ever fill a hole. That is deliberate: placed any
        // higher it would silently outrank the auto-profile's own segment plan, and a user who pinned
        // "Adventure" once would quietly disable the advisor's gear strategy for the rest of the run.
        //
        // Note the override is consulted in BOTH the challenge and non-challenge branches — that is the
        // old `override ?? profile` — and only its LABEL depends on OverrideIsSegment. The two must not
        // be conflated: the override outlives the challenge that set it by up to one advisor tick, so
        // deciding "is this segment gear?" from whether a challenge is running right now would mislabel
        // a stale rotation value (and, with AutoProfile off, claim an auto profile chose it).
        public static Result Resolve(Inputs i)
        {
            if (i == null) return new Result { Source = Src.None, Sentence = "Nothing is set." };

            if (i.Noec)
                return new Result
                {
                    Source = Src.Noec,
                    Sentence = "No-Equipment Challenge is active — no gear can be worn."
                };

            // Gear hunt is an explicit, user-armed mode: while it is camping a stage its hybrid set wins
            // — but never during a challenge, whose rotation is the advisor reacting to a run the user
            // cannot re-gear their way out of. Checked BEFORE the override rather than after, because
            // the override is non-null for every auto-profile user, so `override ?? hunt` would never
            // fall through (a real past bug).
            if (!i.ChallengeActive && i.HuntActive)
                return new Result
                {
                    Name = LootHunter,
                    Source = Src.Hunt,
                    Sentence = "Running the Loot Hunter set — gear hunt is camping a stage."
                };

            if (Has(i.Override))
                return i.OverrideIsSegment
                    ? new Result
                    {
                        Name = i.Override,
                        ForceRespawn = i.ProfileRespawn,
                        Source = Src.Segment,
                        Sentence = "Running \"" + i.Override + "\" — chosen by the auto profile for this segment."
                            + (Has(i.Pin) ? " Your pick applies when the auto profile is off." : "")
                    }
                    : new Result
                    {
                        Name = i.Override,
                        ForceRespawn = i.ProfileRespawn,
                        Source = Src.Challenge,
                        Sentence = "Running \"" + i.Override + "\" — from the challenge rotation."
                    };

            if (Has(i.ProfileObjective))
                return new Result
                {
                    Name = i.ProfileObjective,
                    ForceRespawn = i.ProfileRespawn,
                    Source = Src.Profile,
                    Sentence = "Running \"" + i.ProfileObjective + "\" — from the profile's gear timeline."
                        + (Has(i.Pin) ? " Your pick applies when the timeline has nothing to say." : "")
                };

            if (Has(i.Pin))
                return new Result
                {
                    Name = i.Pin,
                    ForceRespawn = i.PinRespawn,
                    Source = Src.Pin,
                    Sentence = "Running \"" + i.Pin + "\" — your standing pick."
                };

            return new Result
            {
                Source = Src.None,
                Sentence = "No objective — the advisor won't change your gear. "
                         + "Pick one above, or add a gear breakpoint to your profile."
            };
        }
    }
}
