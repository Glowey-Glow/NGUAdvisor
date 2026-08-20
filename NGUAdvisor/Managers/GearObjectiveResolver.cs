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
            public const string DropFarm = "dropfarm";   // the advisor's own farm, same Loot Hunter set
            public const string Segment = "segment";     // the auto-profile's segment gear
            public const string Profile = "profile";     // the active profile's gear timeline
            public const string ProfileManual = "profilemanual"; // a profile row listing item IDs — MANUAL MODE
            public const string Pin = "pin";             // the user's standing pick (Loadouts -> Main)
            public const string None = "none";
        }

        public sealed class Inputs
        {
            public bool Noec;                  // ChallengeDetector.Current() == "NOEC"
            public bool ChallengeActive;       // ChallengeDetector.Current() != null
            public bool HuntActive;            // GearHunter.Active — the MANUAL gear-hunt toggle
            // FarmVenue.DropFarmActive — the ADVISOR's own gear/rare/IDLE farm is standing in a zone
            // for its drops. Kept separate from HuntActive: it ranks lower (see Resolve) and it needs
            // its own sentence, because "you armed this" and "the advisor chose this" are different
            // things to tell someone whose gear just changed.
            public bool DropFarmActive;
            public string Override;            // ChallengeOverlay.GearObjectiveOverride
            public bool OverrideIsSegment;     // true when the override is segment gear, not a challenge rotation
            public string ProfileObjective;    // GearBreakpoints.ActiveObjective
            public bool ProfileRespawn;        // GearBreakpoints.ActiveForceRespawn
            // GearBreakpoints.ActiveLocks — the item IDs the profile's gear row pins (Gear Lock).
            // Null/empty for every profile written before the feature existed.
            public int[] ProfileLocks;
            // GearBreakpoints.ActiveManualIds — item IDs from a profile row that names no objective.
            // Non-empty means the operator is driving gear by hand for this row (audit/59 §A P0).
            public int[] ProfileManualIds;
            public string Pin;                 // Settings.GearObjective ("" = follow the profile)
            public bool PinRespawn;            // Settings.GearObjectiveRespawn
        }

        public sealed class Result
        {
            public string Name;            // null when nothing is in force
            public bool ForceRespawn;
            // The Gear Lock in force, or null. Set ONLY on the profile row — see Resolve.
            public int[] Locks;
            // The manual item IDs in force, or null. Set ONLY on a ProfileManual result — the caller
            // equips these directly rather than solving an objective.
            public int[] ManualIds;
            public bool IsManual => ManualIds != null && ManualIds.Length > 0;
            public string Source;          // one of Src.*
            public string Sentence;        // one line for the companion, already human-readable
            public bool Resolved => !string.IsNullOrEmpty(Name);
            public int LockCount => Locks == null ? 0 : Locks.Length;
        }

        private static string LockNote(int[] locks)
        {
            int n = locks == null ? 0 : locks.Length;
            if (n == 0) return "";
            return n == 1 ? " One item is locked into the set."
                          : " " + n + " items are locked into the set.";
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

            // THE ADVISOR'S OWN DROP FARM gets the SAME Loot Hunter set, for the same reason: while
            // routing is parked in a zone waiting on drops, DC and Respawn multiply exactly what that
            // farm produces, and a stats/NGU objective contributes nothing to it. This is the gear
            // half of the drop-chance demand that already moves the DC digger (FarmVenue).
            //
            // ⚠ ABOVE THE OVERRIDE, AND THE FIRST ATTEMPT PUT IT BELOW — which made it dead code.
            // The reasoning for "below" was that a challenge rotation must not be re-geared around.
            // That is already guaranteed by this row's OWN !ChallengeActive guard, so ranking below
            // the override bought nothing and cost everything: with AutoProfile on and no challenge
            // running, ChallengeOverlay.cs:186-189 sets the override to SegmentGear() on EVERY tick,
            // so `Has(Override)` is permanently true and the farm row was never reached. Observed
            // live: DropFarmActive true (the DC digger moved on it), the farm routing zone 20, and
            // gear still on "NGUs" for the whole run.
            //
            // What the override actually is here is a PHASE plan. A drop farm is a concrete activity
            // the advisor has committed the next several hours to, and it is the automatic twin of
            // the manual gear hunt — which already sits above the override for exactly this reason.
            // During a challenge the guard below hands the rotation back untouched.
            if (!i.ChallengeActive && i.DropFarmActive)
                return new Result
                {
                    Name = LootHunter,
                    Source = Src.DropFarm,
                    Sentence = "Running the Loot Hunter set — the advisor is farming a zone for drops."
                        + (Has(i.Override) ? " \"" + i.Override + "\" resumes when the farm ends."
                         : Has(i.ProfileObjective) ? " \"" + i.ProfileObjective + "\" resumes when the farm ends."
                         : "")
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

            // GEAR LOCK RIDES WITH THE PROFILE ROW AND NOWHERE ELSE.
            //
            // The segment / challenge rows above pair the override's NAME with the profile
            // breakpoint's respawn FLAG — a pre-existing quirk this file already documents and
            // deliberately preserves. It is NOT extended to locks. A lock names concrete items chosen
            // for one row's plan; pairing it with an objective that came from somewhere else would
            // pin, say, a doll set into a challenge-rotation push loadout the user never associated it
            // with. When the override wins, the lock stands down with the row it belonged to.
            if (Has(i.ProfileObjective))
                return new Result
                {
                    Name = i.ProfileObjective,
                    ForceRespawn = i.ProfileRespawn,
                    Locks = i.ProfileLocks != null && i.ProfileLocks.Length > 0 ? i.ProfileLocks : null,
                    Source = Src.Profile,
                    Sentence = "Running \"" + i.ProfileObjective + "\" — from the profile's gear timeline."
                        + LockNote(i.ProfileLocks)
                        + (Has(i.Pin) ? " Your pick applies when the timeline has nothing to say." : "")
                };

            // MANUAL GEAR ROW — ABOVE THE PIN, AND THAT ORDER IS THE WHOLE FIX.
            //
            // A row listing item IDs is an explicit instruction about THIS moment in the timeline;
            // the pin is a standing default for when the timeline has nothing to say. Ranked below
            // the pin (which is what "no branch at all" amounted to) the authored row lost every
            // time and could not even re-assert, because BaseBreakpoints latches `swapped` once the
            // pin has equipped. Ranked above it, the row wins while it is current and the pin
            // resumes the moment the timeline moves on — which is what both features already claim
            // to do.
            //
            // Below the profile's NAMED objective on purpose: an objective row and an ID row cannot
            // both be current (GearBreakpoints sets one and nulls the other), so the order between
            // them never actually binds — but if that ever changes, a named objective is the more
            // specific statement and should keep winning.
            if (i.ProfileManualIds != null && i.ProfileManualIds.Length > 0)
                return new Result
                {
                    Name = null,                       // an ID row names no objective — there is nothing to solve
                    ManualIds = i.ProfileManualIds,
                    ForceRespawn = i.ProfileRespawn,
                    Source = Src.ProfileManual,
                    Sentence = "Gear is in MANUAL MODE — your profile's timeline lists "
                        + i.ProfileManualIds.Length + (i.ProfileManualIds.Length == 1 ? " item" : " items")
                        + " for this row, so the advisor is not choosing your loadout."
                        + (Has(i.Pin) ? " Your standing pick resumes when the timeline has nothing to say." : "")
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
