using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class NGUDiffBreakpoints : BaseBreakpoints<int>
    {
        public NGUDiffBreakpoints() : base() { }

        public NGUDiffBreakpoints(JSONNode bps) : base(bps, (bp) => bp["Diff"].AsInt) { }

        protected override bool PerformSwap(Breakpoint bp)
        {
            var setDifficulty = (difficulty)bp.priorities;
            if (_character.settings.rebirthDifficulty < setDifficulty)
                return false;

            _character.settings.nguLevelTrack = setDifficulty;
            _character.NGUController.refreshMenu();

            // The profile's own claim on this field. LevelPlanner writes it too, from a different rule
            // on a different clock, with no arbitration beyond its ProfileOwnsNguTrack deferral — so
            // both writers land in the ledger and the field reads Contested even while they agree.
            // Agreeing is exactly when a contested field looks healthy and is not.
            Managers.WriteLedger.Record("ngu.track.profile", setDifficulty.ToString(),
                "your profile's NGUDiff timeline reached this breakpoint",
                Managers.ChallengeOverlay.Segment,
                "Set from the profile's own timeline, not from the advisor's end-of-run rule",
                "LevelPlanner also writes this field inside its Evil ch.5 window",
                "Whichever of the two runs last in a tick is the one that sticks");
            return true;
        }
    }
}
