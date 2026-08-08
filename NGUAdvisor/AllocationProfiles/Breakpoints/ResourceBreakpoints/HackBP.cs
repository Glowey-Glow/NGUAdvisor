namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class HackBP : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.R3;

        // Index >= 0 is load-bearing, not defensive. A token the parser cannot read an index out of —
        // "HACK-", "HACK-x", a stray dash — yields Index = -1 (ResourceBreakpoint.ParseBreakpointArray),
        // and -1 passes a bare `<= 14`. hitTarget(-1) returns false, so the breakpoint reports itself
        // VALID; addR3(-1, …) is then a no-op inside the game but Allocate() still returns true. Since
        // R3Breakpoints reclaims the whole pool before allocating, every hack was emptied and none refilled
        // on every pass, forever, with nothing in the log to say so.
        protected override bool Unlocked() => Index >= 0 && Index <= 14 && _character.buttons.hacks.interactable;

        protected override bool TargetMet() => _character.hacksController.hitTarget(Index);

        public override bool Allocate()
        {
            long alloc = MaxAllocation;
            _character.hacksController.addR3(Index, alloc);
            return true;
        }
    }
}
