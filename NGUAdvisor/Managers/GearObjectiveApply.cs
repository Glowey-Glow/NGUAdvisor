using System;

namespace NGUAdvisor.Managers
{
    // The live half of GearObjectiveResolver: reads the game/settings state the resolver needs and
    // hands back the single answer. Kept separate so the precedence table itself stays Unity-free and
    // unit-testable (see GearObjectiveResolverTests).
    //
    // MAIN THREAD ONLY — ChallengeDetector and GearHunter read live game objects.
    public static class GearObjectiveApply
    {
        public static GearObjectiveResolver.Result Current()
        {
            var i = new GearObjectiveResolver.Inputs();
            try
            {
                var ch = ChallengeDetector.Current();
                i.ChallengeActive = ch != null;
                i.Noec = ch == "NOEC";
            }
            catch { }

            try { i.HuntActive = GearHunter.Active; } catch { }
            // The advisor's own drop farm — the same demand that moves the DC digger (FarmVenue).
            // A plain read: the flag is written by the zone pass on its 10-minute throttle, so it is
            // already the slow-moving side of the pair while this runs every second.
            try { i.DropFarmActive = FarmVenue.DropFarmActive; } catch { }

            // ChallengeOverlay publishes ONE field for two different things — the challenge push/growth
            // rotation and the auto profile's per-segment gear — so it also publishes which one it is,
            // recorded at assignment time. Do NOT re-derive this from ChallengeDetector: the override
            // only updates on the 30s advisor tick while this runs every second, so for up to 30s after
            // a challenge ends the field still holds the rotation value and a re-derived answer would
            // mislabel it as segment gear.
            try
            {
                i.Override = ChallengeOverlay.GearObjectiveOverride;
                i.OverrideIsSegment = ChallengeOverlay.GearObjectiveIsSegment;
            }
            catch { }

            try
            {
                i.ProfileObjective = AllocationProfiles.Breakpoints.GearBreakpoints.ActiveObjective;
                i.ProfileRespawn = AllocationProfiles.Breakpoints.GearBreakpoints.ActiveForceRespawn;
                // Gear Lock. Read from the SAME publisher as the objective and the respawn flag, so
                // the refresh pass can never re-solve an objective without the locks that came with it.
                i.ProfileLocks = AllocationProfiles.Breakpoints.GearBreakpoints.ActiveLocks;
            }
            catch { }

            try
            {
                var s = Main.Settings;
                if (s != null)
                {
                    i.Pin = s.GearObjective;
                    i.PinRespawn = s.GearObjectiveRespawn;
                }
            }
            catch { }

            try { return GearObjectiveResolver.Resolve(i); }
            catch (Exception e)
            {
                Main.LogDebug($"GearObjectiveApply: {e.Message}");
                return new GearObjectiveResolver.Result
                {
                    Source = GearObjectiveResolver.Src.None,
                    Sentence = "Couldn't work out the gear objective — check the Debug log."
                };
            }
        }
    }
}
