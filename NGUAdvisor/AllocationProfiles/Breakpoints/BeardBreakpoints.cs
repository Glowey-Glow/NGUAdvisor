using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using NGUAdvisor.Managers;
using SimpleJSON;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class BeardBreakpoints : BaseBreakpoints<int[]>
    {
        private readonly DiggerBreakpoints diggerbp;

        public BeardBreakpoints(DiggerBreakpoints diggerbp) : base()
        {
            this.diggerbp = diggerbp;
        }

        public BeardBreakpoints(JSONNode bps, DiggerBreakpoints diggerbp) :
            base(bps, (bp) => bp["List"].AsArray.Children.Select(x => x.AsInt).Where(x => x <= 6).ToArray())
        {
            this.diggerbp = diggerbp;
        }

        protected override bool PerformSwap(Breakpoint bp)
        {
            if (!LockManager.CanSwap())
                return false;

            // Advisor auto-apply (Phase B): while enabled (and not in a challenge), the advisor's
            // goal-aware set replaces the profile's list at every swap.
            var target = bp.priorities;
            if (Main.Settings != null && Main.Settings.AdvisorBeards)
                target = OptimizationAdvisor.CurrentBeardSet() ?? target;

            // TRUNCATE TO THE SLOTS THAT EXIST, and treat the list as a PRIORITY ORDER rather than a
            // set. EquipBeards resizes anything longer than capBeards() and returns false to say so —
            // and a false here means `current` is never assigned, so this breakpoint re-fires on every
            // allocation tick, clearing and re-equipping beards forever with a "Failed to equip" line
            // each pass. That triggers on exactly the ordinary case of a profile written for seven
            // slots being run before all seven are unlocked. Cutting here makes the equip succeed, and
            // the entries past the cut simply activate as slots unlock — which is what an ordered
            // priority list should do.
            try
            {
                int cap = Main.Character.allBeards.capBeards();
                if (target != null && cap > 0 && target.Length > cap)
                {
                    var kept = target.Take(cap).ToArray();
                    Main.LogDebug($"Beards: {target.Length} listed, {cap} slot(s) unlocked — running the top {cap}.");
                    target = kept;
                }
            }
            catch { }

            if (BeardManager.EquipBeards(target))
            {
                Main.Log($"Equipping Beards: {string.Join(", ", target)}");
                current = bp;
                diggerbp.Reset(); // Diggers could turn off due to a deactivation of the Golden Beard
                return true;
            }
            else
            {
                Main.Log($"Failed to equip Beards: {string.Join(", ", target)}");
            }

            return false;
        }
    }
}
