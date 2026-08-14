using System;

namespace NGUAdvisor.Managers
{
    // The game side of "until done". UntilCondition decides; this reads.
    //
    // Kept apart from the clause itself on purpose: the decision has to stay testable without a game
    // build, and the moment a Character read leaks into it that stops being true. Everything here is a
    // read — nothing in this file may write to the save.
    public static class UntilFactsProvider
    {
        // One read per tick, shared by every breakpoint list that asks. Six timelines each calling
        // totalAdvAttack() on the same frame is six identical walks of the gear table for one answer.
        private static UntilFacts _cached;
        private static double _cachedAt = -1;

        public static UntilFacts Read()
        {
            try
            {
                var c = Main.Character;
                if (c == null) return default(UntilFacts);

                double now = c.rebirthTime.totalseconds;
                if (_cachedAt == now) return _cached;

                var f = new UntilFacts();
                f.RunSeconds = now;
                try { f.Gold = c.realGold; } catch { }
                try { f.Attack = c.totalAdvAttack(); } catch { }
                try { f.Defence = c.totalAdvDefense(); } catch { }
                try { f.Energy = c.totalCapEnergy(); } catch { }
                try { f.Magic = c.totalCapMagic(); } catch { }
                // Bestiary-backed, not the difficulty selector — the selector is what the advisor is
                // CHASING, and a condition written about progress must read progress.
                try
                {
                    var obj = OptimizationAdvisor.NextObjective();
                    if (obj.Known) f.TitanVersions = ZoneHelpers.VersionsDefeatedByKills(obj.Index);
                }
                catch { }

                _cached = f;
                _cachedAt = now;
                return f;
            }
            catch { return default(UntilFacts); }
        }

        // ---- narration -----------------------------------------------------------------------------
        // A held step is INDISTINGUISHABLE from a healthy slow run unless something says so. That is the
        // one real hazard this feature introduces, so the hold announces itself once and then goes quiet
        // rather than repeating every tick.
        private static string _lastHeld;
        private static DateTime _lastHeldAt = DateTime.MinValue;
        private static readonly TimeSpan Reannounce = TimeSpan.FromMinutes(30);

        public static string HeldBy { get; private set; }   // null when nothing is holding

        public static void NoteHold(string text, UntilCondition cond)
        {
            HeldBy = text;
            bool isNew = _lastHeld != text;
            bool stale = (DateTime.UtcNow - _lastHeldAt) > Reannounce;
            if (!isNew && !stale) return;
            _lastHeld = text;
            _lastHeldAt = DateTime.UtcNow;
            try
            {
                Main.Log($"Advisor: holding this step — {cond.Describe()} (waiting, not stalled)");
                Activity.Queued("Step is waiting on a condition", cond.Describe());
            }
            catch { }
        }

        public static void NoteMet(string text, UntilClause met)
        {
            if (_lastHeld == null) return;      // never announced a hold, so nothing to close out
            HeldBy = null;
            _lastHeld = null;
            try
            {
                Main.Log($"Advisor: step condition met — {(met != null ? met.Describe() : "done")}; advancing");
                Activity.Completed("Step condition met", met != null ? met.Describe() : "advancing");
            }
            catch { }
        }
    }
}
