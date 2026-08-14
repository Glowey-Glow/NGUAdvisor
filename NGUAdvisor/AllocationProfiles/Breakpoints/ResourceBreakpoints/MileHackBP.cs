using NGUAdvisor.Managers;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    // HackBP that stops at the hack's FIRST MILESTONE — the MILEHACK-n token.
    //
    // The guide's ch.5 hack rows are targets with completion predicates, not a standing order
    // (decision record amendment 09 §3): "get the first milestone on Hacks 3-7 (TM-mNGU)" is done
    // the moment each of ids 2-6 crosses its first milestone. Plain HACK-n cannot express that stop —
    // hitTarget() reads the game's per-hack target field, which is 0 (the "no target — never done"
    // sentinel) unless the player typed one — so a sweep written with HACK tokens never terminates
    // and the R3 waterfill parks on its head forever.
    //
    // The stop is READ, never written. nextMilestoneTarget()/setToNextMilestone() were rejected in
    // the hacks-lane campaign: nextMilestoneTarget computes from `target` rather than `level`, so
    // wiring it to a tick ratchets the target past the hard cap and turns the hack into a permanent
    // R3 sink. milestoneThreshold(id) is the first milestone's LEVEL (serialized spacing minus the
    // perk/quirk reducers), already maintained by the game and safe to compare against every pass.
    //
    // hitTarget() is still honoured alongside the milestone stop so the game's own -1 "never fund
    // this" marker keeps working exactly as it does for HACK-n and ALLHACK.
    public class MileHackBP : HackBP
    {
        protected override bool TargetMet() =>
            _character.hacksController.hitTarget(Index)
            || HackMath.FirstMilestoneMet(
                   _character.hacks.hacks[Index].level,
                   _character.hacksController.milestoneThreshold(Index));
    }
}
