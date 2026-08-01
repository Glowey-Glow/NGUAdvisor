using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using NGUAdvisor.Managers;
using SimpleJSON;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class DiggerBreakpoints : BaseBreakpoints<DiggerBreakpoints.DiggerSpec>
    {
        // The most recently constructed instance = the loaded profile's digger timeline (only one profile
        // is live at a time; a reload rebuilds the wrapper and re-points this). The advisor's Hybrid
        // membership reads the active list through here without needing a path to the private wrapper.
        public static DiggerBreakpoints Current { get; private set; }

        // A digger breakpoint is an ordered slot list PLUS an optional active count. The two are not the
        // same thing: the list is a priority order and may be shorter than the count, so "3, 8" with a
        // count of 4 means "these two first, four active, advisor picks the rest".
        public class DiggerSpec
        {
            public int[] Ids = new int[0];
            public int Count;            // 0 = unset -> as many as the unlocked slots allow (old behaviour)
        }

        public DiggerBreakpoints() : base() { Current = this; }

        public DiggerBreakpoints(JSONNode bps) : base(bps, ParseSpec) { Current = this; }

        private static DiggerSpec ParseSpec(JSONNode bp)
        {
            var spec = new DiggerSpec();
            try { spec.Ids = bp["List"].AsArray.Children.Select(x => x.AsInt).Where(x => x >= 0 && x <= 11).ToArray(); }
            catch { }
            // Absent, zero or negative all mean "unset". Profiles written before this key existed parse
            // to 0 and behave exactly as they always did.
            try { var n = bp["Count"]; if (n != null && n.IsNumber && n.AsInt > 0) spec.Count = n.AsInt; }
            catch { }
            return spec;
        }

        // The profile's digger list for the current rebirth time / active challenge, or null when the
        // profile names no diggers (empty timeline) — in which case the advisor generates its own set.
        public static int[] ActiveProfileDiggers()
        {
            try { var s = Current?.GetCurrentBreakpoint()?.priorities; return s != null && s.Ids.Length > 0 ? s.Ids : null; }
            catch { return null; }
        }

        // How many diggers the active breakpoint wants running, or 0 for "as many as unlocked".
        public static int ActiveProfileDiggerCount()
        {
            try { return Current?.GetCurrentBreakpoint()?.priorities?.Count ?? 0; }
            catch { return 0; }
        }

        protected override bool PerformSwap(Breakpoint bp)
        {
            if (!LockManager.CanSwap())
                return false;

            // Advisor auto-apply (Phase B): while enabled (and not in a challenge), the advisor's
            // goal-aware set replaces the profile's list at every swap.
            var target = bp.priorities?.Ids ?? new int[0];
            if (Main.Settings != null && Main.Settings.AdvisorDiggers)
                target = OptimizationAdvisor.CurrentDiggerSet() ?? target;

            // Honour the breakpoint's active count, and never ask for more than the game has slots for.
            // Truncating here (rather than letting EquipDiggers' own .Take swallow it) keeps the list a
            // PRIORITY ORDER: the entries past the cut are simply the ones that lose, and they start
            // running by themselves as slots unlock.
            try
            {
                int want = bp.priorities?.Count ?? 0;
                int slots = Main.Character.allDiggers.maxDiggerSlots();
                int cap = want > 0 ? System.Math.Min(want, slots) : slots;
                if (cap > 0 && target.Length > cap)
                {
                    Main.LogDebug($"Diggers: {target.Length} listed, running the top {cap}" +
                                  (want > 0 ? $" (breakpoint asks for {want})" : " (unlocked slots)") + ".");
                    target = target.Take(cap).ToArray();
                }
            }
            catch { }

            if (DiggerManager.EquipDiggers(target))
            {
                _lastFailKey = null;
                Main.Log($"Equipping Diggers: {string.Join(", ", target)}");
                current = bp;
                return true;
            }

            // Retrying is right (gold income may simply not exist yet after rebirth), but log the
            // failure once per distinct set — the old per-pass line spammed every 10s all run.
            var key = string.Join(",", target);
            if (key != _lastFailKey)
            {
                _lastFailKey = key;
                Main.Log($"Diggers {key} can't run yet (no gold income or nothing usable) — retrying quietly.");
            }

            return false;
        }

        private static string _lastFailKey;
    }
}
