using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using System;
using System.Collections.Generic;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    // Allocation-side telemetry for the ENERGY and MAGIC lanes.
    //
    // WHY THIS EXISTS. Until now the only allocation diagnostics were [HackDbg] and [DiggerDbg] — R3 and
    // gold — plus R3Breakpoints.ReportSurplusOnce. The two pools the auto profile actually fights over
    // emitted NOTHING. A segment could leave most of its pool idle every tick for hours and the log would
    // be indistinguishable from a healthy one; the only way anyone noticed was by eye, in game
    // (user-reported 2026-08-01: "a lot of idle energy and magic ... locked into one particular system").
    // That blind spot is the reason the fault survived, so the instrument lands before the tuning does.
    //
    // WHAT THE NUMBERS MEAN, because the obvious reading of them is wrong:
    //   committed < budget  is NORMAL and is not waste. Every system caps at ONE level per game tick
    //     (decomp NGUController.updateNGU sets progress = 0f on level-up and discards the overflow), so
    //     each lane stair-snaps to need/ceil(need/budget). Anything above that buys literally nothing.
    //     "Spend the full budget" is therefore an anti-fix — it would take resource away from lanes that
    //     could use it and hand it to one that cannot.
    //   idleEnd     is the only figure that indicates a problem. It is pool that NO lane in the list could
    //     absorb. Large and persistent idleEnd means the segment's lane list is under-specified: it is
    //     missing a sink whose need scales with the pool, not that the lanes are under-spending.
    //   prioCount   is the equal-share divisor — the count of NON-cap lanes. CAP lanes are excluded from
    //     it and are bounded by their own need instead, so moving a lane between CAP and non-CAP changes
    //     every other lane's share. Watching this drift is how the augment lane was caught falling from
    //     1/3 to 1/6 of the pool inside ten seconds as the NGU hot set grew.
    //
    // Debug-channel and throttled per resource: the allocator runs every 10s and this must not spam.
    internal sealed class AllocDiagnostic
    {
        private const double ThrottleSeconds = 60.0;
        private static readonly Dictionary<ResourceType, DateTime> _last = new Dictionary<ResourceType, DateTime>();

        private readonly ResourceType _type;
        private readonly List<string> _lanes = new List<string>();
        private int _passes;
        private int _prioCount;

        private AllocDiagnostic(ResourceType type) { _type = type; }

        // Returns null when not due, so the caller pays nothing on the other 5 ticks per minute.
        internal static AllocDiagnostic Begin(ResourceType type)
        {
            try
            {
                DateTime last;
                if (_last.TryGetValue(type, out last) && (DateTime.UtcNow - last).TotalSeconds < ThrottleSeconds)
                    return null;
                _last[type] = DateTime.UtcNow;
                return new AllocDiagnostic(type);
            }
            catch { return null; }
        }

        // Called at the top of each retry iteration. The retry loop re-runs with only the lanes that
        // succeeded, so prioCount can change between passes — record the pass count to make that visible.
        internal void Pass(int prioCount)
        {
            _passes++;
            _prioCount = prioCount;
            if (_passes > 1) _lanes.Add("| retry |");
        }

        internal void Lane(ResourceBreakpoint prio, long committed)
        {
            try { _lanes.Add($"{prio.Label}:{committed}/{prio.Budget}"); }
            catch { }
        }

        internal void Emit(long cur, long idleEnd)
        {
            try
            {
                double pct = cur > 0 ? 100.0 * idleEnd / cur : 0.0;
                Main.LogDebug($"[AllocDbg] {_type} cur={cur} idleEnd={idleEnd} ({pct:0.#}% unused) "
                            + $"prioCount={_prioCount} passes={_passes} -> {string.Join(", ", _lanes.ToArray())}");
            }
            catch { }
        }
    }
}
