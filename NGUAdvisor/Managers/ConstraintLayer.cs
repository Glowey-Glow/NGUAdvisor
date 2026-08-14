using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // THE CONSTRAINT LAYER — the composition of the four passes
    // (audit/decisions/constraint-layer-spec.md §2), Unity-free. This is the allocator that REPLACES
    // the prioCount share model: four passes in order, every tick, then fill seated lanes to
    // min(capacity, want) in list order, remainder to the surplus sink. The seat rule (spec §4.1,
    // four recorded instances of the divisor-inflation defect class) is enforced by SeatRoster — a
    // refused lane has no path into a count. The ONE divisor in this file is the fill's share
    // (amendment 19 §3, corrected by amendment 27 §4.2 and made UNCONDITIONAL by amendment 28), and
    // it counts every SEATED DESTINATION lane, taken from the plan AFTER the passes ran — a refused
    // lane cannot reach it, which is the seat rule's actual content. No other divisor may be added,
    // and no share CONSTANT may be added beside it (amendment 27 §4.2 rejected one; `35` has
    // catalogued ten fitted constants already).
    //
    // ORDERING IS NOT ARBITRARY (spec §2). Budget precedes feasibility because a budget-exhausted
    // lane may be perfectly feasible; capacity precedes target because a lane can want more than it
    // can absorb. A lane eliminated by an earlier pass is not considered by later ones.
    //
    // NO CACHING ACROSS TICKS (spec §4.5): Compose holds nothing between calls — every input arrives
    // fresh per call, every Plan is built from scratch. ResourceBreakpoint froze hack ordering at
    // profile-parse time and ChallengeOverlay cached its parsed list for a whole session; a lane must
    // not stay dead after its blocker lifts, so the layer is stateless by construction.
    //
    // THE SURPLUS SINK IS WANDOOS (spec §8; 20 §2.8, verified by exhaustive search — the string
    // "target" does not occur in Wandoos98Controller.cs or Wandoos98.cs). The only unterminated
    // consumer in the pool and the only flat-cost lane, so its share shrinks with progression
    // automatically. The sink is filled LAST with the whole remainder — it is not a mid-list fill —
    // and no Wandoos target is ever synthesised (TargetPass refuses one on every path).
    public static class ConstraintLayer
    {
        // Capacity sentinel: the lane's own Allocate() math is the capacity function, discovered at
        // fill time (CapacityPass.Table's Advisor rows — LaneCapMath / AugmentMath / RitualMath /
        // NguValueMath stair-snaps all take a budget and self-limit below it). The live path uses
        // this for every non-rate lane; tests use known capacities.
        public const long SelfLimiting = -1L;

        public enum PassId
        {
            None = -1,     // not eliminated
            Budget = 0,
            Feasibility = 1,
            Capacity = 2,
            Target = 3
        }

        // One lane as the composition sees it, assembled fresh each tick by the caller.
        public struct LaneSpec
        {
            public string Name;        // class name — BudgetPass.Counts key ("AugmentBP", "BR", …)
            public string Label;       // surfacing label ("CAPNGU-5"); null falls back to Name
            public FeasibilityPass.Verdict Feasibility;   // Pass 1, computed fresh this tick
            public bool NoAllocation;  // CapacityPass.CapSource.None (beards): seats, never fills
            public long Capacity;      // Pass 2 amount; SelfLimiting = discovered at fill time
            public bool WantsMore;     // Pass 3; ignored for RateLane and SurplusSink
            public string WantReason;  // surfaced when eliminated at Pass 3
            public bool RateLane;      // amendment 18 §1: fund to capacity, chunked into its share
                                       // when capacity exceeds it; Pass 3 never sees it
            public bool SurplusSink;   // Wandoos — receives the remainder, never a mid-list fill

            // ⚠ MAY THE WATERFILL OFFER THIS LANE A SECOND TIME? null = ask ReofferableLane(Name),
            // which is DEFAULT CLOSED. Set explicitly only by a caller that owns a lane shape the
            // table cannot name. See the ReofferTable header — this is a safety property of the
            // lane's Allocate(), not a preference.
            public bool? Reofferable;
        }

        public struct LaneDecision
        {
            public string Name;
            public string Label;
            public bool Seated;
            public PassId EliminatedBy;   // None when seated
            public string Reason;         // non-null for every lane receiving zero (spec §10) —
                                          // including a seated rate lane skipped for pool shortness
            public bool NoAllocation;
            public bool RateLane;
            public long Capacity;
            public bool SurplusSink;
            public bool Reofferable;      // resolved from LaneSpec.Reofferable ?? ReofferableLane(Name)
            public long Allocation;       // filled by Compose only when CapacitiesKnown

            // WHAT THE LANE WAS OFFERED, as opposed to what it took. The gap between the two is the
            // single most diagnostic figure on this path — a self-limiting lane offered the whole pool
            // and absorbing one unit of it looks identical to a healthy one in every other channel —
            // and it already existed as a local array in PerformSwap, handed to the log renderer and
            // then dropped. Nothing outside that method could read it, so the companion could show
            // "took 0.04% of pool" without being able to say whether that was refusal or starvation.
            // Cumulative across fill rounds, like Allocation.
            public long Offered;
        }

        public sealed class Plan
        {
            public LaneDecision[] Lanes;
            public long Pool;
            public int SinkIndex = -1;            // -1 = no sink lane in the set
            public bool SinkSeated;
            public string SinkRefusalReason;      // why the sink cannot absorb, when it cannot
            public bool BudgetExhausted;
            public string BudgetMessage;          // BudgetPass.SurfaceMessage when any lane idled
            public SeatRoster Roster;             // the seat record — the ONLY source of any count
            public bool CapacitiesKnown;          // every seated fill lane carried a known capacity
            public CapacityPass.VacuityResult Vacuity;   // meaningful only when CapacitiesKnown
            public long SinkAllocation;           // filled when CapacitiesKnown
            public long Unallocated;              // nonzero ONLY when the sink refused or is absent —
            public string UnallocatedReason;      // surfaced, never silently idle

            // The rate-skip tally (amendment 19 §4). A seated rate lane receiving zero is invisible
            // to parity (equal rows drop), to the sink-refused surface (the sink still seats) and to
            // debug.log (nothing throws) — the 79207s two-hour both-pools zero hid in ALL of them.
            // These three numbers are what Surface() turns into the one state-change line that would
            // have named it: "N rate lanes skipped, pool X < cheapest capacity Y".
            public int RateLanesSkipped;          // seated rate lanes that received zero this tick
            public long RateSkipCheapest;         // capacity of the cheapest such lane
            public long RateSkipPool;             // what remained when that cheapest lane was refused
        }

        // ---- the composition ---------------------------------------------------------------------

        public static Plan Compose(long pool, in BudgetPass.BudgetState budget, IList<LaneSpec> lanes)
        {
            var count = lanes?.Count ?? 0;
            var plan = new Plan
            {
                Lanes = new LaneDecision[count],
                Pool = pool > 0 ? pool : 0,
                Roster = new SeatRoster(),
                BudgetExhausted = BudgetPass.Exhausted(budget),
            };

            int budgetRefusals = 0;

            for (int i = 0; i < count; i++)
            {
                var spec = lanes[i];
                var d = new LaneDecision
                {
                    Name = spec.Name,
                    Label = spec.Label ?? spec.Name,
                    NoAllocation = spec.NoAllocation,
                    RateLane = spec.RateLane,
                    Capacity = spec.Capacity,
                    SurplusSink = spec.SurplusSink,
                    Reofferable = spec.Reofferable ?? ReofferableLane(spec.Name),
                    EliminatedBy = PassId.None,
                };

                // PASS 0 — budget. Runs FIRST: a budget-exhausted lane is feasible, under capacity
                // and target-unmet, so Passes 1-3 cannot detect it (spec §3).
                var v = BudgetPass.Evaluate(spec.Name, budget);
                if (!v.Seated)
                {
                    Eliminate(ref d, PassId.Budget, v.Reason);
                    plan.Roster.Add(d.Label, v);
                    budgetRefusals++;
                }

                // PASS 1 — feasibility, the caller's fresh verdict (external constraints first,
                // then the game predicates — FeasibilityPass owns the order within the verdict).
                if (d.EliminatedBy == PassId.None && !spec.Feasibility.Seated)
                {
                    Eliminate(ref d, PassId.Feasibility, spec.Feasibility.Reason);
                    plan.Roster.Add(d.Label, spec.Feasibility);
                }

                // Sink bookkeeping runs whether or not the sink seated: a sink refused at Pass 0
                // (Wandoos holds two counting sites) or Pass 1 (trolled off) is still THE sink —
                // its refusal is what the unallocated remainder must surface. One sink only: a
                // second sink lane would make "the remainder" ambiguous.
                if (spec.SurplusSink)
                {
                    if (plan.SinkIndex >= 0)
                    {
                        if (d.EliminatedBy == PassId.None)
                        {
                            var dup = FeasibilityPass.Verdict.Refuse(
                                "duplicate surplus sink: the remainder already flows to " +
                                plan.Lanes[plan.SinkIndex].Label);
                            Eliminate(ref d, PassId.Feasibility, dup.Reason);
                            plan.Roster.Add(d.Label, dup);
                        }
                    }
                    else
                    {
                        plan.SinkIndex = i;
                        if (d.EliminatedBy == PassId.None)
                        {
                            // The sink is unterminated BY DEFINITION (spec §8): Passes 2 and 3
                            // do not apply to it.
                            plan.SinkSeated = true;
                            plan.Roster.Add(d.Label, FeasibilityPass.Verdict.Seat());
                        }
                    }
                }
                else if (d.EliminatedBy == PassId.None)
                {
                    if (spec.NoAllocation)
                    {
                        // The beard shape (spec §6): a P1 claimant with zero allocation cost. It
                        // SEATS — the Campaign Advisor's ranking needs it — but Passes 2-3 do not
                        // apply and the fill never offers it a unit.
                        plan.Roster.Add(d.Label, FeasibilityPass.Verdict.Seat());
                    }
                    else
                    {
                        // PASS 2 — capacity. "How much can it absorb before the marginal unit is
                        // provably wasted?" Zero means the lane is saturated NOW — the
                        // BasicTrainingBP-at-cap defect class (7791969): a saturated lane must not
                        // hold a fill slot.
                        if (spec.RateLane && spec.Capacity == SelfLimiting)
                        {
                            // The chunk needs the capacity BEFORE the fill (it is NguCap's num3) —
                            // a rate lane cannot be self-limiting. Caller error, refused.
                            var rv = FeasibilityPass.Verdict.Refuse(
                                "rate lane without a known capacity: amendment 18 §1's " +
                                "fund-to-capacity needs the game's cap helper number up front");
                            Eliminate(ref d, PassId.Capacity, rv.Reason);
                            plan.Roster.Add(d.Label, rv);
                        }
                        else if (spec.Capacity == 0)
                        {
                            var cv = FeasibilityPass.Verdict.Refuse(
                                "at capacity: the marginal unit is provably wasted (spec §5)");
                            Eliminate(ref d, PassId.Capacity, cv.Reason);
                            plan.Roster.Add(d.Label, cv);
                        }
                        // PASS 3 — targets. Rate lanes never reach it (amendment 18 §1.2: consumed
                        // entirely by Pass 2 — "blank the bar" IS "funded to capacity").
                        else if (!spec.RateLane && !spec.WantsMore)
                        {
                            var tv = FeasibilityPass.Verdict.Refuse(
                                spec.WantReason ?? "target met");
                            Eliminate(ref d, PassId.Target, tv.Reason);
                            plan.Roster.Add(d.Label, tv);
                        }
                        else
                        {
                            plan.Roster.Add(d.Label, FeasibilityPass.Verdict.Seat());
                        }
                    }
                }

                d.Seated = d.EliminatedBy == PassId.None;
                plan.Lanes[i] = d;
            }

            if (plan.BudgetExhausted && budgetRefusals > 0)
                plan.BudgetMessage = BudgetPass.SurfaceMessage(budget.RebirthLevels, budgetRefusals);

            if (plan.SinkIndex >= 0 && !plan.SinkSeated)
                plan.SinkRefusalReason = plan.Lanes[plan.SinkIndex].Reason;

            // Capacities known → Compose can run the fill itself (the test / planning mode) and the
            // vacuity test is meaningful. Any self-limiting lane → the live executor drives the same
            // FillSession lane by lane instead.
            plan.CapacitiesKnown = true;
            var caps = new List<long>();
            for (int i = 0; i < count; i++)
            {
                var d = plan.Lanes[i];
                if (!d.Seated || d.SurplusSink || d.NoAllocation)
                    continue;
                if (d.Capacity == SelfLimiting)
                {
                    plan.CapacitiesKnown = false;
                    break;
                }
                caps.Add(d.Capacity);
            }

            if (plan.CapacitiesKnown)
            {
                // The vacuity test (spec §5.2): Σ capacity < pool means the allocation question is
                // vacuous — no destination is better than any other; fill everything and pass the
                // remainder to the sink. The Σ excludes the sink (its absorption IS the remainder).
                plan.Vacuity = CapacityPass.Vacuity(plan.Pool, caps);

                // THE WATERFILL (amendment 36). Round 1 is the shipped single pass unchanged; the
                // loop re-offers what it left to the lanes that proved appetite in it. See the
                // Waterfill header for the amendment-28 tension this declares and for the
                // termination proof.
                //
                // ⚠ THIS AND SEVEN SIBLING CITES SAID "amendment 29" UNTIL 2026-08-08, AND THAT NUMBER
                // WAS ALREADY TAKEN. They were forward references written while this work was in
                // flight, but G1-D3-V9-amendment-29.md is "the honest total, and the second
                // allocator", committed 2026-08-04 on an unrelated subject — so every one of them
                // sent a reader to the wrong document, and one was even dated 2026-08-08. Renumbered
                // to 36, which is the record of the [OPERATOR] surplus ruling this fill implements.
                // ⚠ AMENDMENT 36 IS A DRAFT (`-DRAFT` suffix) UNTIL THE OPERATOR RATIFIES IT; if it
                // is renumbered on ratification, these eight cites move with it. The other seven are
                // in this file (:570, :653, :829, :1152) and in AllocTelemetry.cs, ChallengeOverlay.cs
                // and ConstraintLayerBridge.cs.
                var fill = new Waterfill(plan.Pool, plan.Lanes, plan.SinkIndex);
                FillSession session;
                FillSession firstRound = null;
                while ((session = fill.BeginRound()) != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (i == plan.SinkIndex || !fill.IsLive(i))
                            continue;
                        string skipReason;
                        var offer = session.Offer(fill.LaneForRound(i), out skipReason);
                        // The reasons and the rate-skip tally stay ROUND 1's, so the surfaced
                        // numbers mean exactly what they meant before this commit.
                        if (firstRound == null && skipReason != null)
                            plan.Lanes[i].Reason = skipReason;
                        // Known capacity: the lane takes its whole offer (offer is already
                        // min(residual capacity, share), or the rate lane's chunk of that share).
                        session.Commit(offer);
                        fill.Record(i, offer, offer);
                    }
                    fill.EndRound();
                    if (firstRound == null)
                        firstRound = session;
                }

                for (int i = 0; i < count; i++)
                    if (i != plan.SinkIndex)
                        plan.Lanes[i].Allocation = fill.TotalTaken(i);

                // The sink is handed its BANKED reserve plus whatever the last round could not place —
                // still filled last, still with everything that is left, still never a mid-list fill.
                if (plan.SinkSeated)
                {
                    plan.SinkAllocation = fill.SinkTotal;
                    plan.Lanes[plan.SinkIndex].Allocation = fill.SinkTotal;
                }
                else
                {
                    // No sink: nothing was banked, so this is the whole leftover.
                    RecordUnallocated(plan, fill.Remaining);
                }

                RecordRateSkips(plan, firstRound);
            }

            return plan;
        }

        private static void Eliminate(ref LaneDecision d, PassId pass, string reason)
        {
            d.Seated = false;
            d.EliminatedBy = pass;
            d.Reason = reason;
        }

        private static void RecordUnallocated(Plan plan, long remainder)
        {
            plan.Unallocated = remainder;
            if (remainder <= 0)
                return;
            plan.UnallocatedReason = plan.SinkIndex >= 0
                ? "surplus sink refused: " + plan.SinkRefusalReason
                : "no surplus sink in this lane set — the remainder stays idle (reclaimed next tick); " +
                  "spec §8's always-a-destination guarantee holds only when the set includes Wandoos";
        }

        // Executor-side counterpart of the Compose fill, for the live path where the remainder is
        // only known after the sink question is settled. Mutates the plan's surfacing fields the
        // same way Compose's own fill does.
        public static void RecordExecutedRemainder(Plan plan, long remainder)
        {
            if (plan == null)
                return;
            if (plan.SinkSeated)
                plan.SinkAllocation = remainder;
            else
                RecordUnallocated(plan, remainder);
        }

        // Copies the session's rate-skip tally onto the plan — called by both fills (Compose's own
        // and the bridge's live loop) so the surfaced numbers always come from the fill that ran.
        public static void RecordRateSkips(Plan plan, FillSession session)
        {
            if (plan == null || session == null)
                return;
            plan.RateLanesSkipped = session.RateSkips;
            plan.RateSkipCheapest = session.RateSkipCheapest;
            plan.RateSkipPool = session.RateSkipPool;
        }

        // ---- the rate-skip surface (amendment 19 §4) ---------------------------------------------

        // The skip STATE, amount-insensitive: which seated rate lanes ended the tick at zero with a
        // stored fill-skip reason. Same principle as ConstraintParity.Signature — the SET carries
        // the signal, the amounts move every tick. Empty string = no skips.
        public static string RateSkipSignature(Plan plan)
        {
            if (plan?.Lanes == null || plan.RateLanesSkipped == 0)
                return "";
            var keys = new List<string>();
            foreach (var d in plan.Lanes)
                if (d.Seated && d.RateLane && d.Allocation == 0 && d.Reason != null)
                    keys.Add(d.Label ?? d.Name ?? "?");
            keys.Sort(StringComparer.Ordinal);
            return string.Join("|", keys.ToArray());
        }

        public enum RateSkipEmit
        {
            Silent,
            Skips,     // emit "N rate lanes skipped, pool X < cheapest capacity Y"
            Cleared    // emit the all-clear — the skip state ended
        }

        // The latch decision, pure for tests; the caller (ConstraintLayerBridge.Surface) holds the
        // per-pool state. Throttling is ConstraintParity's established precedent verbatim: a
        // signature CHANGE emits behind the 30s hard floor, an unchanged signature refreshes on the
        // 600s interval, and the clear transition respects the same floor so a flapping boundary
        // cannot alternate two lines every tick.
        public static RateSkipEmit RateSkipSurfaceDecision(string signature, string lastSignature,
            double secondsSinceLastEmit)
        {
            if (!string.IsNullOrEmpty(signature))
                return ConstraintParity.ShouldEmit(signature, lastSignature, secondsSinceLastEmit)
                    ? RateSkipEmit.Skips
                    : RateSkipEmit.Silent;
            if (string.IsNullOrEmpty(lastSignature) ||
                secondsSinceLastEmit < ConstraintParity.MinIntervalSeconds)
                return RateSkipEmit.Silent;
            return RateSkipEmit.Cleared;
        }

        // ---- the fill ----------------------------------------------------------------------------

        // THE FILL IS ONE RULE (amendment 28, closing amendment 27 §4.4/§6.1). Every seated
        // destination is offered
        //
        //     min( capacity , remaining / seated destinations not yet offered )
        //
        // whatever kind of lane it is and whatever the rate group's capacities look like. THE TWO
        // REGIMES ARE GONE.
        //
        // What they were: amendment 19 §3 split the rate fill on "can ANY lane in the group be
        // funded to its FULL capacity" — regime A all-or-nothing down the list, regime B chunked
        // across the group. Amendment 27 §4.2 fixed regime B's divisor (it had been the rate lanes,
        // which handed the group 100% of the pool by construction) and left regime A open at §6.1.
        //
        // WHY THE TEST HAD TO GO. It is a statement about CAPACITY — "is anything blankable" — and
        // it was deciding the SPLIT. Live [AllocDbg], 2026-08-04, one account, twenty seconds
        // apart, the same ~927 B energy pool: regime A gave NGU-8 78.22% with Augment-3 at zero;
        // regime B gave NGU-8 6.257% with every augment funded. A 12x discontinuity on identical
        // inputs, triggered by a track flip. Three consecutive regime-A samples the same run swung
        // 79.01% / 49.12% / 79.75% as cap=self recomputed with each lane's next-level cost.
        // ⚠ cap=self is a WASTE bound, not a value bound: it answers "how much can this lane absorb
        // without discarding overflow", and for a high-divider lane that number exceeds the pool,
        // so min(capacity, remaining) = remaining. THE HIGHEST-DIVIDER LANE SITS LAST AND EATS WHAT
        // IS LEFT — reverse the list and Augments take 78%. Allocation was a residual, not a
        // decision.
        //
        // WHY NOTHING IS LOST BY REMOVING IT. Once the share bound applies, RateChunk clamped to
        // capacity IS RateFill wherever RateFill was defensible: when capacity <= share the chunk
        // divisor ceil(capacity/share) is 1, so the lane is funded at exactly its capacity — the
        // full blank, all-or-nothing, amendment 18 §1.2's BB end state unchanged. The A branch's
        // only remaining behaviour was the two cases the share bound exists to stop: take the whole
        // remaining pool when capacity > share (the 78% defect), or take ZERO when capacity >
        // remaining. Zero is strictly worse than a chunk by amendment 19 §3's own argument — there
        // is nothing to steal from, and the only alternative destination is the surplus sink, which
        // is the sink precisely because it is the LOWEST-VALUE destination. And the "steal" the
        // all-or-nothing rule was written to prevent cannot happen under a reserved share: a lane
        // taking its own share leaves every lane behind it exactly the share it would have had.
        //
        // ⚠ NO SHARE CONSTANT AND NO GROUP CEILING (amendment 27 §4.2, restated). The share falls
        // out of how many lanes are seated, which the roster already knows. `35` catalogued ten
        // fitted constants in this layer; an eleventh is not licensed here.

        // Every lane's chunk of the remaining pool. The chunk arithmetic is NguValueMath.NguCap's
        // SHIPPED per-tick chunking (NguValueMath.cs:283-284,
        // num4 = ceil(num3 / ceil(num3 / MaxAllocation) x 1.00000202655792), clamped to idle) —
        // reused verbatim per amendment 19 §7.1, never re-derived; it is what the old path has
        // always done and what the guide's human does when BB is unaffordable (25 §6). The lane's
        // share of the pool is remaining / (seated DESTINATION lanes not yet offered) — the
        // divisor the file header licenses: seated lanes only, so a refused lane cannot inflate
        // it. On the live path NGUBP.Allocate then runs the same NguCap with this offer as its
        // budget and self-limits below it, exactly as it did under the old allocator.
        //
        // ⚠ THE DIVISOR IS NOT THE RATE GROUP (amendment 27 §4.2, correcting amendment 19 §3).
        // Dividing by the rate lanes answers "how do we split among lanes that cannot be blanked"
        // and never answers "how much of the pool should that group get in the first place" — so
        // the group consumed 100% of it by construction, and every lane behind it received
        // literal zero. Live, 2026-08-04 14:33: nine Evil NGU lanes at 11.028%-11.168% of a
        // 926.5B energy pool, then AdvancedTraining-0/1/3/4, the surplus sink and Augment-2/3 all
        // at zero, remainder=0 — with constraint-layer-spec §8's always-a-destination guarantee
        // failing in both pools. Counting all sixteen seated lanes gives each rate lane ~1/16 and
        // leaves the rest standing.
        //
        // ⚠ AND IT IS NOT CONDITIONAL (amendment 28). This is the ONLY rate-lane fill; there is no
        // longer a branch that hands a lane more than its share. Offer clamps the result to the
        // lane's capacity — NguCap's x1.00000202655792 fudge overshoots by design, and a lane must
        // never be offered past what it can absorb (spec §2's min(capacity, want)).
        public static long RateChunk(long capacity, long remaining, int lanesLeft, out string skipReason)
        {
            if (capacity <= 0)
            {
                skipReason = "rate lane with no capacity";
                return 0;
            }
            if (lanesLeft < 1)
                lanesLeft = 1;
            long maxAllocation = remaining / lanesLeft;
            if (maxAllocation < 1)
            {
                skipReason = string.Format(CultureInfo.InvariantCulture,
                    "pool exhausted: remaining {0} across {1} seated destination(s) still waiting " +
                    "leaves no whole unit for this lane (amendment 28)", remaining, lanesLeft);
                return 0;
            }

            var r = NguValueMath.NguCap(new NguValueMath.NguCapInputs
            {
                // Synthesised so the game's num3 IS this lane's one-level cost:
                // num3 = ceil(capacity x 1 / 1) = capacity. The chunk line then runs on it
                // untouched.
                LevelPlusOnePlusOffset = 1f,
                Num2 = 1.0,
                SpeedDivider = capacity,
                MaxAllocation = maxAllocation,
                IdlePool = remaining,
            });
            skipReason = r.Num > 0 ? null : string.Format(CultureInfo.InvariantCulture,
                "chunk rounded to zero: capacity {0} over share {1} (amendment 19 §3.1's chunking)",
                capacity, maxAllocation);
            return r.Num;
        }

        // One tick's fill, shared by Compose (known capacities) and the live executor (self-limiting
        // lanes, real takes observed from the pool). Both drive the SAME arithmetic so the tested
        // fill and the live fill cannot diverge: Offer answers "what may this lane take at its
        // turn", Commit deducts what it actually took.
        public sealed class FillSession
        {
            private long _remaining;
            private int _lanesLeft;            // seated DESTINATION lanes not yet offered — THE
                                               // divisor (amendment 27 §4.2, unconditional from 28)

            // No lane context: the divisor degenerates to ONE destination, so the first lane is
            // offered the whole remaining pool. That is a degenerate mode for a caller that has no
            // plan to hand over — Compose and the bridge both use the lanes overload, and any
            // caller that does not is asking for allocation-as-a-residual back.
            public FillSession(long pool) : this(pool, null)
            {
            }

            // ONE count, from ONE question, taken from the plan AFTER the passes ran so a refused
            // lane can never reach it (the seat rule, spec §4.1). A session is built fresh per pool
            // per tick, so it is recomputed like every other predicate (spec §4.5) — no state
            // survives a tick — and it applies PER POOL (amendment 19 §7.2): energy and magic are
            // separate pools with separate lists and separate sessions.
            //
            // THE DIVISOR (amendment 27 §4.2) asks "how many destinations are still waiting for
            // this pool", so it counts every seated DESTINATION: a lane that can receive a unit at
            // all. That includes the surplus SINK, which is seated, is the destination for the
            // remainder, and is never handed to Offer — so its slot is never spent and a share
            // survives the walk for it (amendment 27 §6.2, resolved IN: it is the sink's reserved
            // slot that restores spec §8's always-a-destination guarantee in a pool whose only
            // other lanes are rate lanes). It excludes a NoAllocation lane: the beard shape
            // (spec §6) seats for the Campaign Advisor's ranking and Offer never hands it a unit,
            // so a slot held for it would be a share reserved for nothing — divisor inflation by
            // another name.
            //
            // ⚠ THERE IS NO SECOND COUNT. Amendment 19 §3's regime test walked the rate group to
            // ask "can any lane be blanked" and then decided the split with the answer; amendment
            // 28 removed it (see the fill header). A rate group census has no reader left, and
            // reintroducing one is how the 12x discontinuity comes back.
            public FillSession(long pool, IList<LaneDecision> lanes)
            {
                _remaining = pool > 0 ? pool : 0;
                int destinations = 0;
                if (lanes != null)
                {
                    for (int i = 0; i < lanes.Count; i++)
                    {
                        var d = lanes[i];
                        if (!d.Seated || d.NoAllocation)
                            continue;
                        destinations++;
                    }
                }
                _lanesLeft = destinations;
            }

            public long Remaining => _remaining;

            // Seated DESTINATION lanes not yet offered — the divisor itself. Read-only, and read by
            // the waterfill's sink reserve (amendment 36): the sink's share of a round has to be the
            // same 1/n every other destination in that round was offered.
            public int LanesLeft => _lanesLeft;

            // The rate-skip tally (amendment 19 §4), accumulated at the ONLY point that knows both
            // the refusal and what remained when it happened. Session-lifetime state, which is one
            // tick of one pool — nothing survives into the next tick (spec §4.5).
            public int RateSkips { get; private set; }
            public long RateSkipCheapest { get; private set; }   // capacity of the cheapest refused rate lane
            public long RateSkipPool { get; private set; }       // what remained when IT was refused —
                                                                 // so "pool X < cheapest capacity Y" is
                                                                 // true by construction in every state

            public long Offer(in LaneDecision d, out string skipReason)
            {
                skipReason = null;
                if (!d.Seated || d.NoAllocation || d.SurplusSink)
                    return 0;

                // THE SHARE, and it is unconditional (amendment 28). The divisor counts THIS lane
                // too — it is a destination not yet offered, and it is being offered right now.
                // The sink is the one destination that never arrives here, so its slot is the one
                // that is never spent, and that is what leaves a remainder for it.
                int lanesLeft = _lanesLeft > 0 ? _lanesLeft : 1;
                long share = _remaining / lanesLeft;

                long take;
                if (d.RateLane)
                {
                    take = RateChunk(d.Capacity, _remaining, lanesLeft, out skipReason);
                    // NguCap's x1.00000202655792 overshoots capacity by design when the chunk
                    // divisor is 1; the fill never offers past what a lane can absorb (spec §2).
                    // With that clamp, capacity <= share reproduces amendment 18's full blank
                    // EXACTLY — which is why the regime test has no work left to do.
                    if (take > d.Capacity)
                        take = d.Capacity;
                    if (take == 0 && d.Capacity > 0)
                    {
                        RateSkips++;
                        if (RateSkips == 1 || d.Capacity < RateSkipCheapest)
                        {
                            RateSkipCheapest = d.Capacity;
                            RateSkipPool = _remaining;
                        }
                    }
                }
                else if (d.Capacity == SelfLimiting)
                {
                    // The lane's own stair-snap math bounds the take BELOW the offer; the share
                    // bounds the offer. Handing it _remaining is what let one self-limiting lane
                    // drink a pool it merely happened to reach first (amendment 27 §1.1).
                    take = share;
                }
                else
                {
                    take = Math.Min(d.Capacity, share);
                }

                // Offered: this lane leaves the count for every lane behind it, whatever kind it
                // was. Only the sink never arrives here — both fills skip it and hand it the
                // remainder last — so its slot is the one that is never spent.
                if (_lanesLeft > 0)
                    _lanesLeft--;
                return take;
            }

            public void Commit(long actualTake)
            {
                if (actualTake <= 0)
                    return;
                _remaining -= Math.Min(actualTake, _remaining);
            }

            // The sink's share: everything left. Zeroes the session so a second call cannot
            // double-count.
            public long TakeRemainder()
            {
                var r = _remaining;
                _remaining = 0;
                return r;
            }
        }

        // ---- re-offer safety: WHICH LANES MAY BE OFFERED TWICE (amendment 36 §2) -------------------

        // ⚠ A SECOND OFFER IS NOT ALWAYS A WASTE. FOR ONE LANE IT IS A WITHDRAWAL, and that is what
        // rolled this branch back out of the operator's live game.
        //
        // MEASURED, like-for-like, [AllocDbg] 8/7/2026, the SAME six-lane magic block 150 seconds
        // apart — identical membership, identical order, indistinguishable pool:
        //
        //   (9120s) single pass   BR-30 offered=1009899134963 took=1009899134562  remainder=401
        //   (9270s) waterfill     BR-30 offered=1009942040007 took=1009942039231  remainder=1009942039619
        //
        // The take did not move. The REMAINDER went from 401 to the whole take. Round 1 offered BR
        // 1,009,942,039,619 and it absorbed all but 388 of it; round 2 offered it that 388, and
        // BR.CastRituals — walking EVERY unlocked ritual with the new, tiny `allocationLeft` — priced
        // every one of them as unable to finish inside its `secondsToRun` window, took the
        // SkipAndDrain branch, and ran
        //
        //     if (_character.bloodMagic.ritual[i].magic > 0)
        //         _character.bloodMagicController.bloodMagics[i].removeAllMagic();
        //
        // on each. [DECOMP] BloodMagicController.cs:230-236 is `idleMagic += magic; ritual.magic -=
        // magic` — round 1's placement, handed straight back. The executor then read
        // `long remainder = Idle(c, type)` at the end of the fill and correctly saw all of it idle.
        // The bridge clamps a negative take to 0, which is why the LOGGED take still reads as round
        // 1's while the pool says otherwise: `offered=` is cumulative (1,009,942,039,619 + 388) and
        // reproduces the defect to the unit.
        //
        // ⚠ THE PREMISE THE WATERFILL WAS WRITTEN ON — "the algorithm discovers appetite by offering
        // and measuring take" — IS FALSE FOR BR. AppetiteProven's rules A and B are theorems about
        // the game's STAIR ARITHMETIC and they are sound; what they cannot see is that calling one
        // lane's Allocate() a second time RUNS A DIFFERENT PROGRAM. Rule A passed BR correctly (it
        // took 99.99996% of its offer). The fill was right about the appetite and wrong about the
        // lane.
        //
        // SO RE-OFFERING IS A PER-LANE PROPERTY, DEFAULT CLOSED. A lane is re-offerable only if a
        // second Allocate() with a smaller budget provably cannot return resource the first placed.
        // A lane absent from this table is NOT re-offerable — the same enforcement-by-omission
        // BudgetPass.Allowlist uses, pointed the safe way round.
        //
        // THE PROOF OBLIGATION, and it is the same one for every row: read the lane's Allocate() for
        // any `removeAll*` / `cap()` / reset / recompute-from-level on ANY path, then confirm the
        // GAME method it ends in is additive. All eight game entry points were re-read in
        // reference/decomp-full for this commit and every `add*` is literally
        // `target += num; idlePool -= num` with num clamped to idle — no reset, no recompute, and
        // the only `= 0` in any of them zeroes the SOURCE pool after it was fully drained. The
        // withdrawal methods are separate and are named per row where a lane can reach one.
        public struct ReofferRow
        {
            public string Lane;          // LaneSpec.Name — the advisor class name
            public bool Reofferable;
            public string AdvisorShape;  // what the advisor's Allocate() does with a smaller budget
            public string GameCall;      // the game method it ends in, with its decomp line
            public string Proof;         // why a second offer cannot withdraw — or why it can
        }

        public static readonly ReofferRow[] ReofferTable =
        {
            // ---- NOT RE-OFFERABLE ----------------------------------------------------------------
            new ReofferRow { Lane = "BR", Reofferable = false,
                AdvisorShape = "CastRituals(Index) re-walks EVERY unlocked ritual against the NEW " +
                    "allocationLeft; RitualMath.RitualDecide prices each one with " +
                    "RitualTimeLeft(id, allocationLeft), so a smaller budget makes every ritual too " +
                    "slow and returns SkipAndDrain",
                GameCall = "BloodMagicController.removeAllMagic() ([DECOMP] :230-236) on the drain " +
                    "path; .add() (:124-155) on the fund path",
                Proof = "DESTRUCTIVE, MEASURED. BR.cs:92-98 drains on every non-Fund verdict, and the " +
                    "verdict is a FUNCTION OF THE OFFER — tLeft scales as 1/allocationLeft " +
                    "(RitualMath.ProgressPerTick's Remaining term), so round 2's residue budget " +
                    "guarantees the drain. 9270s: remainder 401 -> 1,009,942,039,619." },

            new ReofferRow { Lane = "RitualBP", Reofferable = false,
                AdvisorShape = "RitualBP.cs:66-72 — the gold gate drains before returning false",
                GameCall = "BloodMagicController.removeAllMagic() ([DECOMP] :230-236)",
                Proof = "DEFAULT CLOSED, and deliberately not argued open. The gate is " +
                    "RitualMath.RitualGoldGateBlocks(goldCost, realGold, progress) — it does NOT read " +
                    "the budget, so a lane that funded in round 1 would gate the same way in round 2 " +
                    "and the drain would be unreachable. That argument is CONTINGENT on realGold and " +
                    "ritual progress being invariant across rounds inside one PerformSwap, which this " +
                    "layer neither owns nor checks, and the same drain is what BR's regression was. " +
                    "An unproven lane is closed (this file's own rule); the cost is one lane's second " +
                    "helping, the risk is BR's defect a second time." },

            // ---- RE-OFFERABLE --------------------------------------------------------------------
            new ReofferRow { Lane = "BestAug", Reofferable = true,
                AdvisorShape = "BestAug.AllocatePairs() re-ranks the seven pairs against the new " +
                    "MaxAllocation and funds the winner's live halves; the only early exit " +
                    "(BestAug.cs:77 MoneyPitRunMode) is a bare `return false`",
                GameCall = "AugmentController.addEnergyAug() / addEnergyUpgrade() ([DECOMP] :511, :535)",
                Proof = "SAFE. No removeAll*, no cap(), no reset on any path in BestAug.cs or its " +
                    "AugmentBP base — the whole body is CalculateAugCap -> SetInput -> add. The game " +
                    "call is `character.augments.augs[id].addEnergyAug(num); idleEnergy -= num` over " +
                    "Aug.cs:97-105's `augEnergy += energy`, so a second smaller offer ADDS a second " +
                    "smaller amount rather than resizing to it. This is the lane the waterfill was " +
                    "built for and the one it was measured on: 14.285% -> 99.707% of a 1.728 T pool " +
                    "([AllocDbg] 9270s), with the augment's own stair arithmetic bounding the take." },

            new ReofferRow { Lane = "NGUBP", Reofferable = true,
                AdvisorShape = "CalculateNGU{Energy,Magic}Cap -> SetInput -> add; no branches",
                GameCall = "NGUController.add() ([DECOMP] :290-304) / NGUMagicController.add() (:253-267)",
                Proof = "SAFE, AND THE WASTE IS ALREADY BOUNDED BY RULE A. The game call is " +
                    "`NGU.skills[id].energy += num; idleEnergy -= num` — accumulating, so a repeat " +
                    "offer is accepted, never returned. It CAN be wasted (updateNGU zeroes progress " +
                    "on level-up, 07 §8), which is exactly the case AppetiteProven's rule A retires: " +
                    "an NGU past its one-level price takes 81 M of a 244 B offer and never sees a " +
                    "second round. A lane that DOES survive rule A took more than half its offer, " +
                    "i.e. it is still chunking below one level, so its extra helping converts. The " +
                    "destructive siblings — cap() (:306), removeAll() (:445) — are not on this path." },

            new ReofferRow { Lane = "BasicTrainingBP", Reofferable = true,
                AdvisorShape = "LaneCapMath.BasicTrainingAllocation(cap, MaxAllocation) -> SetInput " +
                    "-> addEnergy; no branches",
                GameCall = "OffenseTraining.addEnergy() ([DECOMP] :175-201) / DefenseTraining.addEnergy() (:162-188)",
                Proof = "SAFE. `training.attackEnergy[id] += num; idleEnergy -= num`, no reset. ⚠ ONE " +
                    "SIDE EFFECT NAMED: with settings.syncTraining on, the offense call also pours " +
                    "the same num into the mirrored defense slot and halves num against idle — that " +
                    "is ADDITIVE too and it is the shipped single-pass behaviour, unchanged by a " +
                    "second offer. In practice these lanes are cap-1 and rule A retires them on the " +
                    "round that discovers them (`offered=100796380579 took=1`)." },

            new ReofferRow { Lane = "TimeMachineBP", Reofferable = true,
                AdvisorShape = "CalculateTM{Energy,Magic}Cap -> SetInput -> addEnergy/addMagic; no branches",
                GameCall = "TimeMachineController.addEnergy() ([DECOMP] :406-425) / addMagic() (:448-467)",
                Proof = "SAFE. `machine.speedEnergy += num; idleEnergy -= num`. removeEnergy() (:427), " +
                    "reset() (:490) and removeAllMagic() (:763) are separate methods this lane never " +
                    "calls." },

            new ReofferRow { Lane = "AugmentBP", Reofferable = true,
                AdvisorShape = "CalculateAugCap -> SetInput -> the gold estimate -> addEnergyAug/Upgrade. " +
                    "The gold gate (AugmentBP.cs:86-87) is a bare `return false`",
                GameCall = "AugmentController.addEnergyAug() / addEnergyUpgrade() ([DECOMP] :511, :535)",
                Proof = "SAFE. Same body and same game call as BestAug; the one extra branch REFUSES " +
                    "without touching what is already placed. Note the game gate " +
                    "`bossID > augBossRequired` makes an under-boss call a silent no-op, not a " +
                    "withdrawal." },

            new ReofferRow { Lane = "AdvancedTrainingBP", Reofferable = true,
                AdvisorShape = "CalculateATCap -> SetInput -> ControllerFor(Index).addEnergy(). The " +
                    "wish-190 branch (AdvancedTrainingBP.cs:79) returns true having allocated nothing",
                GameCall = "AdvancedTrainingController.addEnergy() ([DECOMP] :187-205)",
                Proof = "SAFE. `advancedTraining.energy[id] += num; idleEnergy -= num`. The wish-190 " +
                    "no-op takes zero, so AppetiteProven retires the lane on the spot rather than " +
                    "spinning on it. ⚠ THE OLD GroupShare WATERFILL IS GONE (see the class header) — " +
                    "there is no per-group state left that a second call could reset." },

            new ReofferRow { Lane = "WandoosBP", Reofferable = true,
                AdvisorShape = "RecordShare -> LaneCapMath.WandoosCap -> SetInput -> addEnergy/addMagic",
                GameCall = "Wandoos98Controller.addEnergy() ([DECOMP] :564-569) / addMagic() (:611-616)",
                Proof = "SAFE, AND MOOT ON THIS PATH: WandoosBP is the surplus sink, the sink is never " +
                    "handed to Offer in any round, and it is funded once after the loop. Classified " +
                    "anyway so the table is total. `wandoos98.wandoosEnergy += num; idleEnergy -= num`. " +
                    "⚠ addCapEnergy() (:571) and addCapMagic() (:591) ARE destructive — both open by " +
                    "dumping the lane back into idle — and this lane calls NEITHER. The only " +
                    "cross-call state is the LastShareEnergy/Magic diagnostic, which steers nothing." },

            new ReofferRow { Lane = "HackBP", Reofferable = true,
                AdvisorShape = "hacksController.addR3(Index, CalculateHackCap()); no branches",
                GameCall = "HacksController.addR3(id, amount) ([DECOMP] :160-179)",
                Proof = "SAFE, AND NOT ROUTED HERE: R3Breakpoints does not enter this layer (see the " +
                    "ConstraintLayerBridge header), so this row can only ever be read by a future " +
                    "caller. `hacks[id].res3 += amount; idleRes3 -= amount`, with hitTarget(id) " +
                    "making a satisfied hack a no-op rather than a withdrawal." },

            new ReofferRow { Lane = "MileHackBP", Reofferable = true,
                AdvisorShape = "HackBP's Allocate() verbatim; only TargetMet() differs (first-milestone stop)",
                GameCall = "HacksController.addR3(id, amount) ([DECOMP] :160-179)",
                Proof = "SAFE BY INHERITANCE: the class overrides no allocation code — Allocate() and " +
                    "CalculateHackCap() are HackBP's own, so the HackBP row's proof is this row's " +
                    "proof. The milestone stop only changes IsValid(), i.e. whether the lane is " +
                    "offered at all, never what an offer does. Same not-routed caveat as HackBP." },
        };

        // DEFAULT CLOSED. An unrecognised lane name — a new lane type, a synthetic test name, a
        // typo — is NOT re-offerable. That is the whole safety property: the failure mode of a
        // missing row is one lost second helping, and the failure mode of a wrong `true` is BR.
        public static bool ReofferableLane(string laneName)
        {
            if (string.IsNullOrEmpty(laneName))
                return false;
            for (int i = 0; i < ReofferTable.Length; i++)
                if (string.Equals(ReofferTable[i].Lane, laneName, StringComparison.Ordinal))
                    return ReofferTable[i].Reofferable;
            return false;
        }

        // ---- the waterfill (amendment 36) ---------------------------------------------------------

        // THE FILL IS NO LONGER SINGLE-PASS. One ROUND is exactly the pass above, byte for byte; the
        // loop re-offers what the round left over to the lanes that PROVED, in that round, that they
        // would convert more. [OPERATOR] 2026-08-07:
        //
        //   "All of the systems that can take a resource should be in consideration for the sink, not
        //    just wandoos. where they could take a fraction and it spread across multiple systems for
        //    constant gain as well as wandoos."
        //
        // WHAT IT IS FOR, from the operator's own log ([AllocDbg] 8/7/2026-10:47 PM, 5379s, energy
        // pool 1,713,961,926,335, seven seated lanes, no Wandoos): six NGU lanes were offered
        // 244 B-1.47 T and absorbed 81 M-4.7 B, BestAug-0 was offered 244,851,703,762 and absorbed
        // 244,794,962,329 — and the 1,464,028,202,041 the NGUs declined, 85.418% of the pool, was
        // never re-offered to the one lane in the set that was still chunking. It fell out as idle
        // on EVERY swap, not intermittently.
        //
        // ⚠ AMENDMENT 28 TENSION, DECLARED. Amendment 28 deleted residual-shaped allocation: "cap=self
        // is a WASTE bound, not a value bound … THE HIGHEST-DIVIDER LANE SITS LAST AND EATS WHAT IS
        // LEFT — reverse the list and Augments take 78%. Allocation was a residual, not a decision."
        // A second round is residual-shaped, so this reopens exactly one thing and nothing else:
        //
        //   REOPENED — a lane may now receive MORE than one share of the pool in a tick.
        //   STILL HOLDS — the SPLIT is never a residual. Every round's offer is still
        //     min(capacity, remaining / seated destinations not yet offered), unconditional, with the
        //     denominator taken from the seat count after the passes ran; there is no regime test, no
        //     share constant, no group ceiling, and no lane is handed `remaining` because it happens
        //     to sit last. Amendment 28's 12x track-flip discontinuity cannot come back through here:
        //     round r+1 offers the SAME 1/n to every survivor, so list order decides who is offered
        //     first, never who is offered more.
        //   STILL HOLDS — the SINK KEEPS ITS RESERVED SLOT IN EVERY ROUND (28 §2). It is counted as a
        //     destination and never handed to Offer, in round 1 and in round 9 alike, so spec §8's
        //     always-a-destination guarantee survives the loop structurally rather than by luck.
        //
        // ⚠ AND THE ROUND FLOOR IS ONE UNIT, NOT A FLOAT STALL FLOOR. CapacityPass's 2^-25 /
        // ulp(progress)/2 floors (spec §5.3) answer "will `progress += ppt` move a FLOAT BAR at all".
        // The pool is a `long`. The smallest motion that exists in this domain is 1, and the shipped
        // fill already tests for it — RateChunk refuses when `remaining / lanesLeft < 1` ("leaves no
        // whole unit"). Importing 2^-25 here would be WishManager's mistake in the other direction: a
        // floor borrowed from a domain it does not belong to (CapacityPass.StallFloorAt's header).

        // WHY A LANE MAY BE OFFERED AGAIN — a THEOREM about the shipped stair arithmetic, not a
        // tuned threshold.
        //
        // Every self-limiting energy/magic lane resolves its take with the game's own expression,
        //
        //     take = ceil( cost / ceil(cost / offer) x 1.00000202655792 )
        //
        // where `cost` is that lane's ONE-LEVEL-PER-TICK price (NguValueMath.NguCap's num3,
        // AugmentMath.AugCap's num1, and the same shape in RitualMath / WandoosMath). Write
        // k = ceil(cost / offer):
        //
        //   k == 1  ⟺  cost <= offer  ⟺  THE LANE IS AT ITS CEILING. The take is ceil(cost x F) — a
        //             number that does not move when the offer moves — and everything past it is
        //             DISCARDED by the game: [DECOMP] NGUController.updateNGU:56-59 sets
        //             `progress = 0f` on level-up and never subtracts 1 (07 §8).
        //   k >= 2  ⟹  cost > (k-1) x offer  ⟹  take = cost/k > offer x (k-1)/k >= offer / 2.
        //
        // RULE A is the contrapositive of the second line: `2 x take <= offer` PROVES k == 1, i.e.
        // proves the lane is at its ceiling. Exact, and nothing is fitted — the 2 is k_min, the
        // smallest chunk count that means "still chunking". It retires the ten cap-1 BasicTraining
        // lanes (take 1 of 100 B), all six NGUs (take 81 M of 244 B) and every saturated lane, in the
        // round that discovers them.
        //
        // RULE B closes the gap rule A leaves: a k == 1 lane whose cost sits ABOVE offer/2 passes rule
        // A, and returns THE SAME take every round however much it is offered. So a lane must also
        // IMPROVE on its own previous take to keep its seat. A ceiling take is constant, so rule B
        // retires such a lane on the very next round — one extra chunk, never a loop.
        //
        // ⚠ WHAT THIS COSTS, STATED. The game's stair math is stateless within a tick: round r+1
        // recomputes `cost` from the lane's LEVEL, not from what round r already gave it. So a lane
        // whose cost falls between round r's offer and round r+1's offer is funded to a full `cost`
        // on top of round r's `cost/k`, and the game converts only one level either way. The overshoot
        // is bounded — at most one extra chunk per lane, at most cost/2 wasted, and only for lanes
        // with cost in (offer_r, offer_{r+1}] — and it is spent on resource that is otherwise 100%
        // IDLE, which is what makes it never worse than the state it replaces. It is NOT free when a
        // sink is present, which is why the sink's slot stays reserved every round.
        public static bool AppetiteProven(long offer, long take, long previousTake, bool firstRound)
        {
            if (offer <= 0 || take <= 0)
                return false;                       // nothing offered, or nothing absorbed
            // Rule A, written as `take > offer - take` so neither side can overflow a long.
            if (take <= offer - take)
                return false;
            // Rule B — only meaningful once there IS a previous round to improve on.
            if (!firstRound && take <= previousTake)
                return false;
            return true;
        }

        // TERMINATION, and it is proved rather than bounded by a counter.
        //
        // Consider the LAST live lane of any round. The divisor is "seated destinations not yet
        // offered", so by its turn that divisor is 1 (or 2 with the sink's reserved slot) and it is
        // offered the whole remaining pool R (or R/2). Then exactly one of:
        //
        //   * it does NOT keep its seat — the live set strictly shrinks, and the live set is finite;
        //   * it keeps its seat — rule A says it took more than half of what it was offered, so what
        //     remains after the round is under R/2 (under 3R/4 with a sink), and the pool is a
        //     non-negative long.
        //
        // So EVERY round either shrinks the live set or shrinks the remaining pool geometrically, and
        // both are well-founded. The pathological case where every lane declines terminates in ONE
        // round: every lane fails rule A, the next BeginRound finds no live lane and returns null.
        // In practice the loop runs 2-4 rounds, because the stair arithmetic converges quadratically
        // — a chunking lane offered R leaves about R^2/cost behind.
        public sealed class Waterfill
        {
            private readonly LaneDecision[] _lanes;
            private readonly int _sinkIndex;
            private readonly bool[] _live;
            private readonly bool[] _reofferable;   // resolved once per tick, from the plan
            private readonly long[] _lastTake;
            private readonly long[] _total;
            private readonly long[] _offered;
            private readonly long[] _residual;   // known capacities only; SelfLimiting keeps its sentinel
            private long _remaining;
            private long _sinkBank;              // the sink's banked reserve — OUT of the waterfill
            private long _sinkReserveThisRound;
            private bool _sinkSeated;
            private int _round;
            private FillSession _session;

            public Waterfill(long pool, LaneDecision[] lanes, int sinkIndex)
            {
                _lanes = lanes ?? new LaneDecision[0];
                _sinkIndex = sinkIndex;
                var n = _lanes.Length;
                _live = new bool[n];
                _reofferable = new bool[n];
                _lastTake = new long[n];
                _total = new long[n];
                _offered = new long[n];
                _residual = new long[n];
                for (int i = 0; i < n; i++)
                {
                    var d = _lanes[i];
                    _live[i] = d.Seated && !d.NoAllocation && i != sinkIndex;
                    _reofferable[i] = d.Reofferable;
                    _residual[i] = d.Capacity;
                }
                _sinkSeated = sinkIndex >= 0 && sinkIndex < n
                              && _lanes[sinkIndex].Seated && !_lanes[sinkIndex].NoAllocation;
                _remaining = pool > 0 ? pool : 0;
            }

            public long Remaining => _remaining;              // what the NEXT round may re-offer
            public long SinkBank => _sinkBank;                // reserved for the sink, never re-offered
            public long SinkTotal => _sinkBank + _remaining;  // what the sink is handed at the end
            public int Round => _round;                       // 0 before the first BeginRound
            public long TotalTaken(int i) => _total[i];
            public long TotalOffered(int i) => _offered[i];
            public bool IsLive(int i) => _live[i];

            public int LiveCount
            {
                get { var n = 0; for (int i = 0; i < _live.Length; i++) if (_live[i]) n++; return n; }
            }

            // ⚠ THE PROOF'S OWN BOUND, WRITTEN DOWN — not a tuning knob and not a floor. Each round
            // either retires a lane (at most `_lanes.Length` of those) or at least halves the
            // remaining pool (at most 64 of those, because that is how many times a positive `long`
            // can be halved). A correct execution can never reach it — the live sets converge in
            // 2-4 rounds — but this loop runs on Unity's MAIN THREAD inside PerformSwap, where a
            // state nobody predicted must degrade into a stale allocation rather than a frozen game.
            private int MaxRounds => _lanes.Length + 64;

            // Opens the next round, or returns null when the fill is over. The roster it builds is
            // what the round's divisor counts: the lanes still live, PLUS the sink's reserved slot.
            public FillSession BeginRound()
            {
                if (_remaining <= 0 || _round >= MaxRounds)
                    return null;

                var roster = new List<LaneDecision>();
                var anyLive = false;
                for (int i = 0; i < _lanes.Length; i++)
                {
                    if (i == _sinkIndex)
                    {
                        // Amendment 28 §2, unchanged and now per ROUND: the sink is a seated
                        // destination that is never handed to Offer, so its slot is the one that is
                        // never spent — and that reserved slot is what leaves it a remainder.
                        if (_lanes[i].Seated && !_lanes[i].NoAllocation)
                            roster.Add(_lanes[i]);
                        continue;
                    }
                    if (!_live[i])
                        continue;
                    roster.Add(LaneForRound(i));
                    anyLive = true;
                }
                if (!anyLive)
                    return null;

                // ⚠ THE SINK'S SHARE IS BANKED, NOT RE-DIVIDED. Without this the waterfill STARVES
                // Wandoos: the sink collects the residue, so re-offering the residue re-offers the
                // sink's own money, and after k rounds it holds pool/n^k — measured, not feared (the
                // 62963s sixteen-lane block took the sink from 30.9% of the pool to under 0.1%
                // before this reserve went in). The operator ruled the surplus is spread "as well as
                // wandoos", not instead of it.
                //
                // The reserve is amendment 28's OWN share formula pointed at the sink: what the
                // round had to give, over the round's roster — the same 1/n the first lane in the
                // round was offered, and INDEPENDENT OF LIST POSITION, which is the property
                // amendment 28 exists to defend. Round 1 is unaffected: the reserve is bookkeeping
                // taken at the END of the round, the session is untouched, so every offer inside the
                // round is byte-for-byte what it was before this commit.
                _sinkReserveThisRound = _sinkSeated ? _remaining / roster.Count : 0;

                _round++;
                _session = new FillSession(_remaining, roster);
                return _session;
            }

            // The lane as THIS round sees it. A KNOWN capacity is reduced by what the lane has already
            // absorbed — that is exact information and it beats any inference. A SELF-LIMITING lane
            // keeps its sentinel and is bounded by its own Allocate(), exactly as in round 1.
            public LaneDecision LaneForRound(int i)
            {
                var d = _lanes[i];
                if (d.Capacity != SelfLimiting)
                    d.Capacity = _residual[i] > 0 ? _residual[i] : 0;
                return d;
            }

            // What the lane was offered and what it actually absorbed. Decides its seat for the next
            // round: arithmetic when the capacity is known, AppetiteProven when it is not — and then
            // the re-offer gate, which OVERRIDES both.
            public void Record(int i, long offer, long take)
            {
                if (take < 0)
                    take = 0;
                _offered[i] += offer;
                _total[i] += take;

                if (_residual[i] == SelfLimiting)
                {
                    _live[i] = AppetiteProven(offer, take, _lastTake[i], _round <= 1);
                }
                else
                {
                    _residual[i] = _residual[i] > take ? _residual[i] - take : 0;
                    _live[i] = _residual[i] > 0 && offer > 0 && take > 0;
                }

                // ⚠ THE RE-OFFER GATE, AND IT IS LAST BECAUSE IT OVERRULES THE APPETITE. AppetiteProven
                // answers "would this lane convert more" — a theorem about the game's stair arithmetic
                // — and for BR it answered YES, correctly, right before round 2 handed the whole take
                // back (ReofferTable's header carries the measurement). Appetite is not permission: a
                // lane that is not PROVEN safe to call twice is retired after its one offer whatever
                // it took, so the fill degrades to the shipped single pass for that lane exactly and
                // for no other. Round 1 is untouched — this can only ever remove a later offer.
                if (!_reofferable[i])
                    _live[i] = false;

                _lastTake[i] = take;
            }

            // Closes the round on the session's own arithmetic (Compose's mode). The session was
            // opened on the RE-OFFERABLE pool, so the bank has to be added back before the one
            // formula below takes it off again — both callers hand EndRound the same thing, the
            // WHOLE unspent pool.
            public void EndRound() =>
                EndRound((_session != null ? _session.Remaining : _remaining) + _sinkBank);

            // Closes the round on a LIVE pool read (the executor's mode) — the game is the authority
            // on what is actually left, not the session's running subtraction. The sink's reserve for
            // this round comes off the top; only what the lanes DECLINED carries into the next.
            //
            // ⚠ `remaining` IS THE WHOLE UNSPENT POOL, bank included. The live pool read cannot see a
            // bank — nothing has been handed to the sink yet, the reservation is bookkeeping — so
            // subtracting what is already banked is what stops the reserve compounding against
            // itself. (It did, before this line: the sink came out at 530,809,727,271 of a
            // 926,504,309,183 pool while the executor's own idle read said 299,183,649,976.)
            public void EndRound(long remaining)
            {
                var left = remaining > 0 ? remaining : 0;
                left -= _sinkBank;
                if (left < 0)
                    left = 0;
                var reserve = _sinkReserveThisRound < left ? _sinkReserveThisRound : left;
                if (reserve > 0)
                    _sinkBank += reserve;
                _sinkReserveThisRound = 0;
                _remaining = left - reserve;
            }
        }

        // ---- declared focus (amendment 18 §2) ----------------------------------------------------

        // "These lanes are in; everything else waits." The sets are defined in the data (§2.3:
        // PAWG, Adv/DC, All); this type is deliberately generic over lane keys so those sets and
        // user-defined ones (open item §4.2) share one shape.
        public sealed class FocusSet
        {
            public readonly string Name;
            private readonly HashSet<string> _lanes;

            public FocusSet(string name, IEnumerable<string> laneKeys)
            {
                Name = name ?? "focus";
                _lanes = new HashSet<string>(laneKeys ?? new string[0], StringComparer.Ordinal);
            }

            public bool Contains(string laneKey) => laneKey != null && _lanes.Contains(laneKey);
        }

        // The ONE door through which a focus reaches a lane's constraints. The sink is exempt BY
        // CONSTRUCTION (amendment 18 §2.4): Wandoos remains the surplus sink under any focus, so a
        // focused-but-saturated state cannot read as "nothing to allocate" — the surplus still
        // reaches the sink. The focus therefore runs BEFORE Pass 2's vacuity test simply by being
        // part of the lane's Pass 1 verdict.
        public static FeasibilityPass.ExternalConstraints WithFocus(
            FeasibilityPass.ExternalConstraints constraints,
            FocusSet focus, string laneKey, bool isSurplusSink)
        {
            if (focus == null || isSurplusSink || focus.Contains(laneKey))
                return constraints;
            constraints.FocusExcluded = focus.Name;
            return constraints;
        }

        // ---- THE ANCHOR-ABSENT SURPLUS SINK (amendment 36 §3.1) ------------------------------------

        // ⚠ THIS IS THE CONDITIONAL SINK c828f06 LEFT OPEN, AND IT IS CONDITIONAL FOR A MEASURED
        // REASON. Read that commit before widening this: seating Wandoos in AUGMENTATION
        // UNCONDITIONALLY was proposed, measured and refused, and the measurement reproduces here
        // (AugmentationSinkTests.Seating_the_sink_unconditionally_halves_the_augment).
        //
        // ⚠ AND THE HOPE THAT MOTIVATED RE-OPENING IT IS FALSE: THE SINK IS INSIDE THE DIVISOR.
        // FillSession's constructor counts "every seated DESTINATION", and the sink IS one — it is
        // skipped by Offer, not by the count, which is the whole of amendment 28 §2 ("its slot is
        // never spent, and that is what leaves a remainder for it"). So a sink costs every other lane
        // one slot of the share, and on top of that Waterfill.BeginRound BANKS it `_remaining /
        // roster.Count` per ROUND, out of the loop's reach. It is not free. Live proof, [AllocDbg]
        // 8/8/2026-12:37 AM (11959s), fifteen seated lanes of which Wandoos-0 is the sink:
        //
        //     1,758,030,099,891 / 15 = 117,202,006,659 = TimeMachine-0's `offered=`, exactly.
        //
        // Fifteen, not fourteen. Had the sink been outside the divisor the first lane would have been
        // offered 125,573,578,563 and the log would say so.
        //
        // SO THE ONLY FREE SINK IS ONE SEATED WHEN THE ANCHOR IS NOT THERE TO PAY FOR IT. The
        // condition is a statement about MEMBERSHIP and nothing else:
        //
        //     seat Wandoos  ⟺  the segment is on the table below
        //                  AND its ANCHOR lane is absent from the membership
        //                  AND no Wandoos is in the membership already.
        //
        // "ANCHOR ABSENT" IS NOT A NEW LIVE READ. The membership handed to this layer has already
        // been filtered by ResourceBreakpoint.IsValid() = correctResourceType && Unlocked() &&
        // !TargetMet() (ConstraintLayerBridge.cs:98, ChallengeOverlay.ParsedList:421), so a BestAug
        // stopped by the No Augs challenge (BestAug.Unlocked) or by all seven pairs reaching target
        // (BestAug.TargetMet -> AugmentMath.BestAugTargetMet) is ALREADY GONE from the list. Absence
        // IS the refusal, computed once, by the code that already computes it.
        //
        // ⚠ WHY IT CANNOT FLAP, which is the property the operator has been bitten by before (audit
        // 31: the BT flip-flop was a membership-before-global-reclaim race). THE CONDITION READS
        // NOTHING THE ALLOCATION WRITES — that is the proof, and it is structural rather than a
        // damping constant:
        //   · energy — BestAug's absence is `noAugsChallenge.inChallenge` (a run-level flag) or all
        //     seven pairs locked-or-at-target. Neither is moved by giving Wandoos energy; and a pair
        //     at target cannot come back un-met inside a rebirth, because the lane that would raise
        //     its level is the one that just stopped being funded.
        //   · magic — BR's absence is BloodPlanner.BloodMatters(), which is the SAME boolean that
        //     decides whether the BR-30 token is emitted at all, and it is already latched and
        //     surfaced once per transition (ChallengeOverlay._lastBloodMatters). So Wandoos and the
        //     ritual are ANTI-CORRELATED by construction: exactly one of them is in the magic
        //     membership, the destination count never changes, and there is no state in which both
        //     appear and compete.
        // A sink seated on this rule therefore stays seated until the game state that removed the
        // anchor changes — never because of what the sink itself absorbed.
        public struct SinkAnchorRow
        {
            public string Segment;         // ChallengeOverlay.Segment, exact
            public string EnergyAnchor;    // lane class name (bp.GetType().Name), as BuildSpec keys it
            public string MagicAnchor;
            public string Why;
        }

        // AN ALLOWLIST, and deliberately one segment long — the same enforcement-by-omission
        // BudgetPass.Allowlist and ReofferTable use. AUGMENTATION is the ONLY auto-profile segment
        // that emits no Wandoos token at all: EVIL CLIMB / TM HOUR / RECOVERY / AT HOUR emit
        // CAPWAN:40, NGU MARATHON emits CAPWAN:60, NGU+AT and EVIL NGU emit bare WAN. In every one of
        // those the sink is already seated and this rule is a no-op by its own second clause, so
        // generalising the table would buy nothing and would put six untested segments behind one
        // predicate.
        public static readonly SinkAnchorRow[] AnchorAbsentSinkTable =
        {
            new SinkAnchorRow { Segment = "AUGMENTATION", EnergyAnchor = "BestAug", MagicAnchor = "BR",
                Why = "guide ch5 phase 2 is a SINGLE-ANCHOR segment by design — energy is the best " +
                      "augment, magic is the blood that feeds the Counterfeit -> Blood Number spells — " +
                      "and every other lane in it is a per-tick CEILING (six NGU lanes absorbing " +
                      "5.07 B of a 1.728 T pool, five absorbing 16.4 B of 1.026 T). Lose the anchor " +
                      "and ~99% of the pool has no destination at all." },
        };

        // The anchor lane's name for this segment and pool, or null when the segment is not on the
        // table. One table read by both sides, so the membership test and the surfaced reason cannot
        // name different lanes.
        public static string SinkAnchorFor(string segment, bool energy)
        {
            if (string.IsNullOrEmpty(segment))
                return null;
            for (int i = 0; i < AnchorAbsentSinkTable.Length; i++)
                if (string.Equals(AnchorAbsentSinkTable[i].Segment, segment, StringComparison.Ordinal))
                    return energy ? AnchorAbsentSinkTable[i].EnergyAnchor
                                  : AnchorAbsentSinkTable[i].MagicAnchor;
            return null;
        }

        public struct SinkSeating
        {
            public bool Seat;
            public string Reason;   // non-null WHENEVER Seat is true — spec §10's surfaced reason,
                                    // produced by the same call that made the decision so the log
                                    // line and the behaviour cannot disagree
        }

        // The decision, pure. `anchorSeated` / `sinkSeated` are membership facts the caller reads off
        // the SAME list it is about to hand the layer — no live game read of any kind happens here.
        public static SinkSeating AnchorAbsentSink(string segment, bool energy,
            bool anchorSeated, bool sinkSeated)
        {
            var anchor = SinkAnchorFor(segment, energy);
            if (anchor == null || anchorSeated || sinkSeated)
                return new SinkSeating { Seat = false, Reason = null };
            return new SinkSeating
            {
                Seat = true,
                Reason = string.Format(CultureInfo.InvariantCulture,
                    "{0} has no {1} lane this tick and no surplus sink — seating Wandoos so the " +
                    "{2} pool has a destination (spec §8); it costs the anchor nothing because the " +
                    "anchor is not there",
                    segment, anchor, energy ? "energy" : "magic"),
            };
        }

        // ---- Pass 3 want, from a target-table answer ---------------------------------------------

        // The bridge from TargetPass to the composition: "does this lane still want more?" ONLY a
        // written terminal target that the game's own comparator reads as met stops a lane.
        // Preconditions never terminate (writing one abandons the lane — 23 §0.4), silences never
        // default to satisfied OR to a synthetic stop (23 §7), and an unresolved operator decision
        // must not stop a lane the operator has not finished deciding.
        public static bool WantFromAnswer(in TargetPass.LaneAnswer answer, out string reason)
        {
            if (answer.Disposition == TargetPass.Disposition.WriteTarget &&
                answer.Satisfaction == TargetPass.Satisfaction.Satisfied)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "target met: level >= {0} by the game's own comparator (spec §7)",
                    answer.TargetToWrite);
                return false;
            }
            reason = null;
            return true;
        }
    }
}
