namespace NGUAdvisor.Managers
{
    // "IS A REBIRTH GOING TO HAPPEN?" — as distinct from "WHEN?".
    //
    // CustomAllocation.NextRebirthTargetSeconds() answers WHEN, in seconds, and it can only answer it
    // for a target expressed in seconds. Every other consumer of that number wants exactly that (a
    // horizon to project over, a deadline to cap a segment at, a countdown to display) and its -1 is
    // an honest "no time deadline this run". BloodPlanner was the one caller asking the OTHER
    // question — is there a rebirth to cash a banked NUMBER multi into — and it asked it by testing
    // `NextRebirthTargetSeconds() > 0`, which silently answers "no" for a rebirth that is armed on
    // something other than the clock.
    //
    // ⚠ THE PARSE DETAIL THAT MAKES THE TWO QUESTIONS DIFFERENT. BreakpointWrapper's Rebirth loop
    // starts each entry at `time = 0.0` and only overwrites it when the entry carries a "Time" key
    // (CustomAllocation.cs:482-484). A NUMBER or BOSSES entry is normally written WITHOUT one — its
    // trigger is the attack multi, not the clock — so it parses to RebirthTime 0. Zero is therefore
    // "no time floor, fire as soon as the number condition is met": ARMED. Minus one is the disarm
    // sentinel, and it is the one the rest of the rebirth code already reads as such —
    // CustomAllocation.DoRebirth filters `Where(x => x.RebirthTime >= 0.0)` and
    // TimeRebirth.RebirthAvailable opens with `if (RebirthTime < 0.0) return false`. So
    // `RebirthTime >= 0` is not a new convention invented here; it is the convention the rebirth
    // path already runs on, read back out.
    //
    // Against the shipped profile corpus that split is 11 armed-but-timeless against 17 disarmed:
    //   ARMED, RebirthTime 0 (Number with no "Time")  — CBlock1, CBlock2, CBlock2.0-LSC, CBlock3.0-N,
    //     C-Miniblock2.1-100lc / 2.2-NoTM / 2.3-NoRB / 2.4-Finish, CBlock3.1-E100LC, CBlock3.2-E,
    //     EvilStart.
    //   DISARMED, RebirthTime -1 — every LRB/HackDay/RADDay profile, plus PostEND.
    // (Profiles that pair a Number entry with a Time backstop — CBlock4/5, FinalEvil24hCBlock,
    // C-Microblock1-Basics, FinalCBlock, SADCBlock1, PostEND-Challenges — were never affected: the
    // backstop is a real seconds target and NextRebirthTargetSeconds already returned it.)
    //
    // THE FIFTH TRIGGER TYPE IS NOT IN THE PROFILE AT ALL. MoneyPitRunRebirth is a static class driven
    // by Settings.MoneyPitRunMode and invoked from Main.cs:1231 as the fallback when the profile's own
    // DoRebirth declines; it has no entry in the rebirth array and no RebirthTime to read. It is
    // covered here by its own toggle rather than by anything in the profile.
    //
    // AND AutoRebirth IS THE MASTER SWITCH FOR ALL FIVE. Main.cs:1229 will not call the profile's
    // DoRebirth without it, and the money-pit fallback routes through BaseRebirth.RebirthAvailable,
    // which opens with `if (!Settings.AutoRebirth) return false`. BR.RebirthDeadline and
    // BestAug.Horizon already gate on it for the same reason.
    public static class RebirthSchedule
    {
        // Why a rebirth is or is not coming. The caller turns this into operator-facing text; keeping
        // the CAUSE typed is the point — "no rebirth scheduled" was one message covering three
        // different situations, only one of which was true.
        public enum Outlook
        {
            // A rebirth will happen this run. Something is armed and nothing is holding it off.
            Coming,
            // The No Rebirth challenge. The run cannot rebirth at all, so a banked multi is never cashed.
            NoRebirthChallenge,
            // Settings.AutoRebirth is off, so the advisor never fires one.
            AutoRebirthOff,
            // Every profile entry is disarmed (RebirthTime -1) and money-pit run mode is off.
            NothingScheduled
        }

        // One entry's arm state. -1 disarms; 0 and above are armed, 0 meaning "no time floor".
        // Deliberately NOT `> 0`: see the header — that test is what mistook eleven shipped profiles
        // for having no rebirth.
        public static bool EntryArmed(double rebirthTimeSec) => rebirthTimeSec >= 0.0;

        // ORDER IS THE MESSAGE, not the logic — every branch below returns something other than
        // Coming, so any order gives the same answer to "is a rebirth coming". They are ranked most
        // to least fundamental so the reason names the outermost cause: NORB cannot be overridden by
        // a setting, AutoRebirth cannot be overridden by a profile.
        public static Outlook Current(bool autoRebirth, bool inNoRebirthChallenge,
                                      bool profileHasArmedEntry, bool moneyPitRunMode)
        {
            if (inNoRebirthChallenge) return Outlook.NoRebirthChallenge;
            if (!autoRebirth) return Outlook.AutoRebirthOff;
            if (profileHasArmedEntry || moneyPitRunMode) return Outlook.Coming;
            return Outlook.NothingScheduled;
        }

        public static bool IsComing(bool autoRebirth, bool inNoRebirthChallenge,
                                    bool profileHasArmedEntry, bool moneyPitRunMode) =>
            Current(autoRebirth, inNoRebirthChallenge, profileHasArmedEntry, moneyPitRunMode) == Outlook.Coming;
    }
}
