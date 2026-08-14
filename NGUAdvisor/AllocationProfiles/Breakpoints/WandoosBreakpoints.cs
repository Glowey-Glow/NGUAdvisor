using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class WandoosBreakpoints : BaseBreakpoints<int>
    {
        public WandoosBreakpoints() : base() { }

        public WandoosBreakpoints(JSONNode bps) : base(bps, (bp) => bp["OS"].AsInt) { }

        protected override bool PerformSwap(Breakpoint bp)
        {
            if (_character.wandoos98.OSlevel <= 0)
                return false;

            int id = bp.priorities;

            if (id == (int)_character.wandoos98.os)
                return true;

            if (id == 1 && !_character.inventory.itemList.jakeComplete)
                return false;
            if (id == 2 && _character.wandoos98.XLLevels <= 0)
                return false;

            var controller = Main.Character.wandoos98Controller;
            controller.SetFieldValue("nextOS", id);
            controller.CallMethod("setOSType");

            _character.wandoos98Controller.refreshMenu();

            // The profile's claim on the OS. AdvisorApply.ApplyWandoosOs writes the same thing from its
            // own ranking, so the pair reads Contested — and this is the expensive one to get wrong:
            // changing the OS WIPES the Wandoos dump levels, which CustomAllocation records as a
            // user-reported incident ("hours of progress gone").
            var osNames = new[] { "Wandoos 98", "Wandoos Meh", "Wandoos XL" };
            Managers.WriteLedger.Record("wandoos.os.profile",
                id >= 0 && id < osNames.Length ? osNames[id] : ("OS " + id),
                "your profile's Wandoos breakpoint reached this step",
                Managers.ChallengeOverlay.Segment,
                "Written by reflection: nextOS, then setOSType",
                "The advisor's own OS ranking writes this field too",
                "⚠ Any OS change wipes the Wandoos energy and magic dump levels");

            return true;
        }
    }
}
