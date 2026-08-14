using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // NGUAdvisors' and NGUBP's Unity-free decision core (audit 01 §3.4, extraction E2 — the name
    // `NguValueMath` is the one report 03 §10 asked for).
    //
    // PARITY ONLY. Everything here is a verbatim move out of Managers/NGUAdvisors.cs and
    // AllocationProfiles/.../NGUBP.cs, with the live `Character` reads lifted into the callers.
    //
    // THREE KNOWN DEFECTS ARE PRESERVED ON PURPOSE, each pinned by a [QUIRK] test in
    // NguValueMathTests.cs. They are listed in 05 §5 and the task brief as explicitly out of scope,
    // because every one of them changes what the allocator funds:
    //
    //   Q1  ValueRatio prices an NGU LINEARLY — (1 + f(L+dL)) / (1 + fL) — where the game switches to a
    //       power curve above a per-NGU break level. Above the break the rating is wrong, and it is
    //       wrong in the direction that OVERVALUES a deep NGU.
    //   Q2  Pick's share model is `pool / keep.Count`, an equal split over the surviving candidates.
    //       That is not the budget the real allocator hands the lane (ResourceBreakpoint.
    //       UpdateMaxAllocation divides by prioCount, a seat count over the whole token list, and CAP
    //       lanes bypass the split entirely). Report 03 measures the divergence at up to 1590x.
    //   Q3  Rating is computed at the FULL pool while Ratio is computed at the share, and the final
    //       ordering is by Rating — so the number that decides the ranking is not the number the prune
    //       predicate tested.
    public static class NguValueMath
    {
        public static readonly string[] ENames = { "Augs", "Wandoos", "Respawn", "Gold", "Adv-α", "Power-α", "DropCh", "Magic", "PP" };
        public static readonly string[] MNames = { "Ygg", "EXP", "Power-β", "Number", "TM", "Energy", "Adv-β" };

        // The prune bar and the tie-hysteresis tolerance, kept as named constants rather than inlined
        // so a later tuning pass has one place to look. Values unchanged.
        public const double HotRatioBar = 1.05;
        public const double TieTolerance = 0.005;      // 0.5% relative
        public const double SurplusFloor = 1.0001;     // above this counts as "worth otherwise-idle energy"
        public const int MaxPruneIterations = 12;
        public const int NothingHotFallbackCount = 2;  // the Take(2) that became the primary path

        public class Entry
        {
            public int Id;
            public string Name;
            public long Level;
            public double Rating;     // x/hr with the FULL pool — the GO-site-comparable score
            public double Ratio;      // x/hr at the equal share it actually gets when running
            public double Lph;        // levels/hr at that share (GrowthPanel's predicted rate)
            public double LphPerUnit; // levels/hr per allocated unit (internal to the prune loop)
        }

        // One NGU as the value model sees it. The caller resolves every field from the live
        // NGUController/NGU.skills before calling; the core never reads game state.
        public struct NguCandidate
        {
            public int Id;
            public long Level;        // level on the track currently being levelled
            public double Divider;    // energySpeedDivider(id) / magicSpeedDivider(id)
            public double Factor;     // boostFactor for the track being levelled; 0 = unreadable
            public bool IsRespawn;    // energy id 2 — the one nonlinear, hard-floored curve
            public bool NormalTrack;  // settings.nguLevelTrack == difficulty.normal (Respawn only)
        }

        // ---- the value model -------------------------------------------------------------------

        // Bonus-multiplier ratio for dL more levels: (1 + f(L+dL)) / (1 + fL) — exact for every NGU
        // except Respawn, which has its own capped time-reduction curve.
        //
        // [Q1] This is the LINEAR pricing. The game applies a power curve above a per-NGU break level
        // and this expression does not. NOT fixed here — see the class comment.
        public static double ValueRatio(in NguCandidate n, double level, double dL)
        {
            if (dL <= 0) return 1.0;
            if (n.IsRespawn) return RespawnRatio(n, level, dL);
            double f = n.Factor;
            if (f > 0) return (1.0 + f * (level + dL)) / (1.0 + f * level);
            return (level + dL + 1.0) / (level + 1.0);
        }

        // Respawn value = respawnTime(old)/respawnTime(new), from the game's exact curve
        // (decomp AllNGUController.respawnBonusNormal/Evil): Normal <=400 linear floored at 0.8,
        // then an asymptote to 0.6; Evil/Sadistic tracks <=10000 floored at 0.925, then to 0.9.
        // At a floor the ratio is 1.0 — a capped Respawn never earns a lane.
        public static double RespawnRatio(in NguCandidate n, double level, double dL)
        {
            double f = n.Factor;
            bool normalTrack = n.NormalTrack;
            double RF(double lvl)
            {
                if (normalTrack)
                {
                    if (lvl <= 400) return Math.Max(0.8, 1.0 - f * lvl);
                    return Math.Max(0.6, 1.0 - (lvl / (lvl * 5.0 + 200000.0) + 0.2));
                }
                if (lvl <= 10000) return Math.Max(0.925, 1.0 - f * lvl);
                return Math.Max(0.9, 1.0 - (lvl / (lvl * 20.0 + 200000.0) + 0.05));
            }
            double now = RF(level), after = RF(level + dL);
            return after > 0 && now > after ? now / after : 1.0;
        }

        // levels/hr per allocated unit: progressPerTick = power / divider x allocated x mult / (level+1),
        // at 50 ticks/s. Returns 0 for an unreadable divider so the caller can skip the candidate.
        public static double LevelsPerHourPerUnit(double power, double divider, double mult, long level) =>
            divider <= 0 ? 0 : power / divider * mult / (level + 1) * 50.0 * 3600.0;

        // NGUAdvisors.Build. `pool` is the FULL resource pool, so Rating is the full-pool score.
        // [Q3] Rating is what the final sort uses; Ratio (the share-based score) is only what the prune
        // predicate tests. The two are different numbers and the ranking never re-reads the second.
        public static List<Entry> Build(IEnumerable<NguCandidate> candidates, bool magic, double power, double mult, double pool)
        {
            var into = new List<Entry>();
            if (candidates == null) return into;
            var names = magic ? MNames : ENames;
            foreach (var n in candidates)
            {
                if (n.Id < 0 || n.Id >= names.Length) continue;
                if (n.Divider <= 0) continue;
                double lphPerUnit = LevelsPerHourPerUnit(power, n.Divider, mult, n.Level);
                double rating = ValueRatio(n, n.Level, lphPerUnit * pool);
                into.Add(new Entry
                {
                    Id = n.Id,
                    Name = names[n.Id],
                    Level = n.Level,
                    Rating = rating,
                    Ratio = rating,   // refined to the actual share in Pick()
                    LphPerUnit = lphPerUnit
                });
            }
            into.Sort((a, b) => b.Rating.CompareTo(a.Rating));
            return into;
        }

        // Rating exactly 1.0 = a capped curve (Respawn at its floor): genuinely worthless even for
        // otherwise-idle energy. Everything else with positive value beats idling.
        public static int[] Surplus(List<Entry> list, int[] targets) =>
            list.Where(x => !targets.Contains(x.Id) && x.Rating > SurplusFloor)
                .OrderByDescending(x => x.Rating).Select(x => x.Id).ToArray();

        // Equal-share prune: each pass splits the pool over the keepers and drops anyone under
        // 1.05x/hr AT THAT SHARE — survivors' shares grow, so the loop is monotone and terminates.
        // Prune-only by design (re-admitting on the larger share would oscillate). Nothing hot:
        // deepen the top two by rating.
        //
        // WHAT ACTUALLY HAPPENS AT SCALE (measured in-game 2026-07-16, Normal, ~5.5M NGU levels):
        // the prune is a PREDICATE, so it assumes lanes differ enough that some clear the bar at the
        // shared rate. They don't — the allocator drives every lane to EQUAL MARGINAL VALUE and then
        // sits there. Measured: a 233x spread in level and a 1680x spread in lph/u collapsed to a 0.04%
        // spread in Rating. Everything is tied, so the "nothing hot" fallback is the ONLY branch that
        // runs at that scale and Take(2) is a magic number for an edge case that became the primary
        // path. It is NOT dead everywhere: a lower NGU level track (Evil retracks levels) spreads the
        // ratings again and the designed prune wakes up. Both paths must stay correct — see
        // [[ngu-marathon-convergence]].
        //
        // [Q2] `pool / keep.Count` is NOT the budget UpdateMaxAllocation will hand these lanes. That
        // divisor is prioCount — a seat count over the WHOLE token list, including lanes this function
        // has never heard of — and CAP tokens skip the split entirely. NOT fixed here.
        public static int[] Pick(List<Entry> list, IReadOnlyDictionary<int, NguCandidate> byId, double pool)
        {
            if (list.Count == 0) return new int[0];
            var keep = new List<Entry>(list);
            for (int iter = 0; iter < MaxPruneIterations && keep.Count > 0; iter++)
            {
                double share = pool / keep.Count;
                foreach (var e in keep)
                {
                    e.Lph = e.LphPerUnit * share;
                    e.Ratio = RatioOf(byId, e, e.Lph);
                }
                var hot = keep.Where(x => x.Ratio >= HotRatioBar).ToList();
                if (hot.Count == keep.Count) break;
                if (hot.Count == 0)
                {
                    keep = keep.OrderByDescending(x => x.Rating).Take(NothingHotFallbackCount).ToList();
                    double s2 = pool / keep.Count;
                    foreach (var e in keep)
                    {
                        e.Lph = e.LphPerUnit * s2;
                        e.Ratio = RatioOf(byId, e, e.Lph);
                    }
                    break;
                }
                keep = hot;
            }

            return keep.OrderByDescending(x => x.Rating).Select(x => x.Id).ToArray();
        }

        private static double RatioOf(IReadOnlyDictionary<int, NguCandidate> byId, Entry e, double dL)
        {
            if (byId != null && byId.TryGetValue(e.Id, out var n)) return ValueRatio(n, e.Level, dL);
            return 1.0;
        }

        // Tie hysteresis. At convergence the ranking is decided on the 5th-6th decimal of Rating — pure
        // jitter from levels ticking up between the 30s Compute refreshes — so a plain sort reshuffled
        // the hot set every refresh. That churned the emitted profile, re-parsed every breakpoint object
        // behind it, and buried the overlay log, all to swap between lanes that are interchangeable
        // anyway. Keep the incumbent while it is still statistically tied with the fresh pick.
        //
        // Deliberately NOT a "ratings within X%" sort comparer: that predicate is not transitive, and
        // List.Sort throws on an inconsistent comparer. This compares the two candidate SETS instead.
        public static int[] Stabilize(List<Entry> all, int[] fresh, int[] incumbent)
        {
            try
            {
                if (incumbent == null || incumbent.Length == 0 || incumbent.Length != fresh.Length) return fresh;
                double RatingOf(int id)
                {
                    foreach (var e in all) if (e.Id == id) return e.Rating;
                    return double.NaN;   // incumbent is no longer a live candidate
                }
                double worstInc = double.MaxValue, worstFresh = double.MaxValue;
                foreach (var id in incumbent)
                {
                    double r = RatingOf(id);
                    if (double.IsNaN(r)) return fresh;
                    if (r < worstInc) worstInc = r;
                }
                foreach (var id in fresh)
                {
                    double r = RatingOf(id);
                    if (double.IsNaN(r)) return fresh;
                    if (r < worstFresh) worstFresh = r;
                }
                // fresh is the top-N, so worstFresh >= worstInc always; keep the incumbent unless the
                // fresh pick is better by more than the tolerance.
                return worstInc >= worstFresh * (1.0 - TieTolerance) ? incumbent : fresh;
            }
            catch { return fresh; }
        }

        // ---------------------------------------------------------------------------------------
        // NGUBP's own surface: the per-NGU target predicate and the stair-snap cap arithmetic.
        // ---------------------------------------------------------------------------------------

        // NGUBP.TargetMet(). A negative target is the game's explicit "never fund this" marker and
        // reports DONE; zero means "no target" and the lane never reports done.
        public static bool NguTargetMet(long target, long level)
        {
            if (target < 0) return true;
            return target > 0 && level >= target;
        }

        // `index >= 0`: ParseBreakpointArray yields Index = -1 for a malformed NGU token.
        // -1 <= 8 passes, the lane reports in range, and ngus[-1] then throws — killing the
        // lane until profile reload. Fourth instance of this sentinel class after HACK-x
        // (audit 09 §5), RIT-x (advisors/02:718) and AT-5. Both pools share this guard.
        public static bool NguIndexInRange(bool magic, int index) => index >= 0 && (magic ? index <= 6 : index <= 8);

        // Everything GetNGU{Energy,Magic}CapCalc needs.
        //
        // Num2 is passed in ALREADY MULTIPLIED, deliberately. It is `power x the whole bonus stack`, and
        // the lane assembles that product in a specific left-to-right order across five statements.
        // Floating-point multiplication is not associative, so re-associating it here — even into the
        // obviously-equivalent `power * stack` — could shift the last bits of a number that then goes
        // through two Math.Ceiling calls, where a 1-ulp difference is a whole allocation unit. Keeping
        // the assembly at the call site makes this extraction exact rather than merely very close.
        public struct NguCapInputs
        {
            public float LevelPlusOnePlusOffset;   // FLOAT, exactly as the game's num1 is — see [QUIRK]
            public double Num2;                    // power x the assembled multiplier stack
            public double SpeedDivider;            // energySpeedDivider(Index) / magicSpeedDivider(Index)
            public long MaxAllocation;
            public long IdlePool;                  // idleEnergy / magic.idleMagic
        }

        public struct NguCapResult
        {
            public long Num;
            public double PPT;
            public int Offset => (int)Math.Floor(PPT * 50 * 10);
        }

        // Verbatim NGUBP cap arithmetic (the energy and magic copies are identical once the reads are
        // resolved, so this replaces both). The two-pass wrapper stays in the lane.
        public static NguCapResult NguCap(in NguCapInputs a)
        {
            var num3 = Math.Ceiling(a.SpeedDivider * (double)a.LevelPlusOnePlusOffset / a.Num2);
            if (num3 < 1.0)
                num3 = 1.0;

            var num4 = Math.Ceiling(num3 / Math.Ceiling(num3 / a.MaxAllocation) * 1.00000202655792);
            long num = num4 > a.IdlePool ? a.IdlePool : (long)num4;

            return new NguCapResult { Num = num, PPT = num4 / num3 };
        }
    }
}
