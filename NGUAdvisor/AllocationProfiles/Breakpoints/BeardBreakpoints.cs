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

            // ⚠ THE ADVISOR OVERRIDE THAT USED TO SIT HERE WAS UNREACHABLE, and its comment described
            // a challenge guard that existed in neither place. PerformSwap is only ever entered from
            // CustomAllocation.cs:239, which runs the profile timeline `if (Settings.ManageBeards &&
            // !Settings.AdvisorBeards …)` — so `if (Settings.AdvisorBeards)` here could never be true.
            // The two paths are mutually exclusive, not layered: when the beards advisor is on this
            // method is not called at all and AdvisorApply.ApplyBeards owns the set instead.
            var target = bp.priorities;

            // The challenge rule applies to the profile path too, so it holds with the beards advisor
            // OFF and on a profile that never set an empty list. Applied to the INTENT so the log line
            // below says what actually happened; BeardManager.EquipBeards carries the backstop.
            target = BeardRule.Apply(ChallengeDetector.Current(), target);

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
                Main.Log(target == null || target.Length == 0
                    ? "Equipping Beards: none"
                    : $"Equipping Beards: {string.Join(", ", target)}");
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
