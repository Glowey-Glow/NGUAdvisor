using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using NGUAdvisor.Managers;
using SimpleJSON;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    // A gear breakpoint is either a manual item-ID list ("ID") or an optimizer objective ("Objective").
    // When an objective is set, the native gear optimizer computes the best loadout live (route C3) instead
    // of using a fixed ID list - so gear stays optimal as it improves. Optimization runs in PerformSwap,
    // which BaseBreakpoints only invokes when the active breakpoint changes (naturally throttled).
    public class GearSpec
    {
        public int[] Ids;
        public string Objective;
        public bool ForceRespawn;
    }

    // ActiveObjective/ActiveForceRespawn mirror the objective of the last-applied gear breakpoint
    // (null when the active breakpoint is a manual ID list) so AdvisorApply can periodically
    // re-optimize the same objective as drops improve (Phase C gear auto-refresh).
    public class GearBreakpoints : BaseBreakpoints<GearSpec>
    {
        // ActiveObjective is a STATIC, but a profile load builds a NEW GearBreakpoints
        // (BreakpointWrapper's parsing ctor) — so without clearing here the previous profile's
        // objective survived the switch and the advisor kept re-equipping, every 120s, a set the new
        // profile never asked for. Clearing in the ctor makes "a new timeline is in charge" mean
        // "nothing is in force until it says so".
        public GearBreakpoints() : base() { ClearActive(); }

        public GearBreakpoints(JSONNode bps) : base(bps, ParseSpec) { ClearActive(); }

        private static void ClearActive()
        {
            ActiveObjective = null;
            ActiveForceRespawn = false;
        }

        private static GearSpec ParseSpec(JSONNode bp)
        {
            var spec = new GearSpec();
            var obj = bp["Objective"];
            if (obj != null && !string.IsNullOrEmpty(obj.Value))
                spec.Objective = obj.Value;
            var resp = bp["TopRespawn"];
            if (resp != null)
                spec.ForceRespawn = resp.AsBool;
            var id = bp["ID"];
            if (id != null && id.IsArray)
                spec.Ids = id.AsArray.Children.Select(x => x.AsInt).ToArray();
            return spec;
        }

        public static string ActiveObjective { get; private set; }
        public static bool ActiveForceRespawn { get; private set; }

        // Rebirth (CustomAllocation calls Reset on every lane) and "the timeline has nothing to say
        // yet". Both used to leave the previous value standing: at t=0 of a new run GetCurrentBreakpoint
        // returns null, so PerformSwap never ran, so the whole first stretch of every run was driven by
        // the PREVIOUS run's final objective.
        public override void Reset()
        {
            base.Reset();
            ClearActive();
        }

        protected override void OnNoBreakpoint() => ClearActive();

        protected override bool PerformSwap(Breakpoint bp)
        {
            if (!LockManager.CanSwap())
                return false;

            string objectiveName = bp.priorities.Objective;
            bool forceRespawn = bp.priorities.ForceRespawn;

            // Smart default: if this breakpoint has no explicit objective and isn't itself challenge-tagged,
            // but a challenge is active, optimize for the built-in objective for that challenge (if any).
            if (string.IsNullOrEmpty(objectiveName) && string.IsNullOrEmpty(bp.challenge))
            {
                var ch = Managers.ChallengeDetector.Current();
                if (ch != null)
                {
                    var def = Managers.ChallengeDetector.DefaultGear(ch);
                    if (def != null) { objectiveName = def.Objective; forceRespawn = def.ForceRespawn; }
                }
            }

            int[] ids;
            if (!string.IsNullOrEmpty(objectiveName))
            {
                var objective = GearOptimizer.FindObjective(objectiveName);
                if (objective == null)
                {
                    // A typo'd objective is accepted by the profile editor (it only rejects an EMPTY
                    // one), so this is reachable from ordinary use. Returning false without clearing
                    // left the PREVIOUS objective active for the rest of the session, which is worse
                    // than doing nothing: the advisor keeps optimizing for a set the profile no longer
                    // asks for, and the only trace is this debug line. Clear, so the resolver falls
                    // through to the user's standing pick instead.
                    Main.LogDebug($"Gear breakpoint objective '{objectiveName}' not recognized.");
                    ClearActive();
                    return false;
                }
                ids = GearOptimizer.OptimizeIds(objective, forceRespawn);
                if (ids.Length == 0)
                    return false;
                Main.Log($"Optimized gear for '{objective.Name}'{(forceRespawn ? " (+top respawn)" : "")}: {ids.Length} items.");
                ActiveObjective = objectiveName;
                ActiveForceRespawn = forceRespawn;
            }
            else
            {
                ids = bp.priorities.Ids ?? new int[0];
                ActiveObjective = null;
                ActiveForceRespawn = false;
            }

            current = bp;
            LoadoutManager.ChangeGear(ids);
            Main.InventoryController.assignCurrentEquipToLoadout(0);

            return true;
        }
    }
}
