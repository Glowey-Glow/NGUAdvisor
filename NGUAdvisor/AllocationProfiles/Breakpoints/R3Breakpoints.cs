using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.Breakpoints
{
    public class R3Breakpoints : BaseBreakpoints<ResourceBreakpoint[]>
    {
        public R3Breakpoints() : base() { }

        public R3Breakpoints(JSONNode bps) :
            base(bps, (bp) => ResourceBreakpoint.ParseBreakpointArray(bp["Priorities"], ResourceType.R3).ToArray()) { }

        // Walk the whole list, not just the head of it.
        //
        // This used to reclaim the pool and then fund valid.FirstOrDefault(), so one hack received all of it.
        // With HackBP now stopping at the allocation that saturates a hack — past which R3 buys nothing,
        // because updateAllHacks discards the overflow — that surplus had nowhere to go and was simply
        // destroyed. Everything after the first token in a fifteen-token hack-day list was decoration.
        //
        // prioCount stays 1 on purpose. The Energy lane divides the pool by the number of non-cap priorities
        // so its systems share; R3 must NOT, for two reasons. The list is an ORDER — the author put HACK-13
        // first because they want it fed first — and an even fifteen-way split of a modest pool can push
        // every share under the float stall floor (2^-25 progress per tick), where a hack accumulates
        // literally nothing forever. Giving each entry the full remaining idle and letting it self-limit
        // yields the same waterfill without either hazard: a hack takes what it can use, the rest flows down.
        //
        // Consequence worth stating: for the first time the lane can finish with R3 left over. The wish
        // share pass (CustomAllocation, WishManager.Allocate) takes the WishR3 slider's share of remaining
        // idle, so surplus now reaches wishes instead of sitting in a saturated hack. That is the right
        // destination — wishes are the other R3 sink — but it is a behaviour change, so HackDbg reports
        // the leftover.
        protected override bool PerformSwap(Breakpoint bp)
        {
            // See EnergyBreakpoints.PerformSwap for why the null filter is here.
            var valid = bp.priorities.Where(x => x != null && x.IsValid()).ToList();
            // Challenge overlay: narrate dead-system filtering; inject fallback if the list is all-dead.
            valid = Managers.ChallengeOverlay.TransformPriorities(bp.priorities, valid, ResourceType.R3);
            if (valid.Count == 0)
            {
                // ⚠ THE SILENT PASS. This return happens BEFORE RemoveR3(), so the previous pass's
                // allocation is left standing untouched and nothing in any log distinguishes that from
                // a healthy pass — the R3 lane could be allocating nothing for hours and read
                // identically. It is recorded rather than narrated: the record is what the companion's
                // pool board turns into a stated reason, and a per-tick log line here would be spam on
                // the one path that repeats every ten seconds.
                RecordSwap(bp, null, 0, false);
                return false;
            }

            RemoveR3();
            long pool = _character.res3.idleRes3;

            // What each lane was OFFERED, kept alongside what the board reads back as taken. The R3
            // lane is an ORDER, not a share (prioCount stays 1), so a lane's offer is the whole
            // remaining idle at its turn — and the gap between that and what it absorbed is the only
            // number that separates "this hack is saturated" from "this hack was starved", which is
            // exactly the discrimination ConstraintLayer.LaneDecision.Offered exists for on E/M.
            _offers = new Dictionary<ResourceBreakpoint, long>();
            foreach (var prio in valid)
            {
                if (_character.res3.idleRes3 <= 0)
                    break;
                prio.UpdateMaxAllocation();
                _offers[prio] = prio.Budget;
                prio.Allocate();
            }

            RecordSwap(bp, valid, pool, true);
            _character.hacksController.refreshMenu();
            ReportSurplusOnce();
            return false;
        }

        // THE R3 PLAN RECORD — the third pool's answer to LastEnergyPlan / LastMagicPlan.
        //
        // R3 is deliberately not routed through the constraint layer (ConstraintLayerBridge.cs:19-23,
        // and the divisor amendment 28 introduced would violate this file's own prioCount law), so
        // there is no Plan object here to hand the companion. What there IS, and what only this method
        // can see, is the membership: which tokens the IsValid() filter and the challenge overlay left
        // standing, in the order the fill walked them. R3PoolView composes that with the live holdings
        // into the identical Plan shape the E/M board consumes.
        //
        // ⚠ HACK LANES ONLY. HackBP is the sole breakpoint whose CorrectResourceType() answers R3, so
        // any other token in an R3 timeline is refused before it can hold a unit — and rostering one
        // would be worse than dropping it, because LaneIndex would collide with a hack id and
        // misattribute that hack's R3 to a lane that never touched it.
        private void RecordSwap(Breakpoint bp, List<ResourceBreakpoint> seated, long pool, bool reclaimed)
        {
            try
            {
                var lanes = new List<Managers.R3PoolView.LaneRecord>();
                if (seated != null)
                    foreach (var prio in seated)
                        if (prio is HackBP)
                            lanes.Add(new Managers.R3PoolView.LaneRecord
                            {
                                Label = prio.Label,
                                HackId = prio.LaneIndex,
                                Seated = true,
                                Offered = OfferOf(prio),
                            });

                int refused = 0;
                foreach (var prio in bp.priorities)
                {
                    if (!(prio is HackBP)) continue;
                    if (seated != null && seated.Contains(prio)) continue;
                    refused++;
                    lanes.Add(new Managers.R3PoolView.LaneRecord
                    {
                        Label = prio.Label,
                        HackId = prio.LaneIndex,
                        Seated = false,
                        Reason = Managers.R3PoolView.HackRefusal(FactsFor(prio)),
                    });
                }

                Managers.R3PoolView.RecordSwap(new Managers.R3PoolView.SwapRecord
                {
                    Lanes = lanes.ToArray(),
                    Pool = pool,
                    Reclaimed = reclaimed,
                    HeldReason = reclaimed ? null
                        : refused > 0
                            ? "all " + refused + " hack token(s) in the timeline failed IsValid() — " +
                              "each row below names its own cause"
                            : "the R3 timeline names no hack at all: only HACK / MILEHACK tokens can " +
                              "fund this pool, so nothing in it is a destination",
                });
            }
            catch (Exception e)
            {
                // ⚠ CLEARED, NOT LEFT STANDING. A failed record must never cost an allocation — but it
                // must not leave the PREVIOUS pass's roster in place either, because the board would
                // then describe this tick using last tick's membership with no way to tell. Null reads
                // as "no pass seen yet", which is what it says before the first swap anyway.
                Managers.R3PoolView.RecordSwap(null);
                Main.LogDebug($"R3 pool record: {e.Message}");
            }
        }

        // Set by PerformSwap for the lanes it actually offered; absent means the pool ran out before
        // this lane's turn, which is a real zero and must not read as an unmeasured one.
        private Dictionary<ResourceBreakpoint, long> _offers;
        private long OfferOf(ResourceBreakpoint prio)
        {
            long v;
            return _offers != null && _offers.TryGetValue(prio, out v) ? v : 0;
        }

        // The live reads behind a refusal, taken at the instant the refusal stood. Every one of them
        // is guarded by the caller's try/catch, so an unreadable hack yields the generic sentence
        // rather than killing the record.
        private Managers.R3PoolView.RefusalFacts FactsFor(ResourceBreakpoint prio)
        {
            int id = prio.LaneIndex;
            var facts = new Managers.R3PoolView.RefusalFacts
            {
                HackId = id,
                MilestoneLane = prio is MileHackBP,
                // TransformPriorities is the ONLY thing between the IsValid() filter and the seated
                // list, so a token that still answers valid here and is missing from that list was
                // removed by the overlay. Re-asked rather than remembered: it is the same tick and the
                // same live state the filter read a few statements ago.
                DroppedByOverlay = prio.IsValid(),
            };
            if (id < 0 || id > 14) return facts;
            facts.HacksUnlocked = _character.buttons.hacks.interactable;
            facts.Level = _character.hacks.hacks[id].level;
            facts.HardCapLevel = _character.hacksController.hardCapLevel(id);
            facts.TargetMet = _character.hacksController.hitTarget(id);
            facts.MilestoneThreshold = _character.hacksController.milestoneThreshold(id);
            return facts;
        }

        // Say it the first time it happens, once per session.
        //
        // Before the saturation clamp the lane always emptied the pool into one hack, so there was never any
        // idle R3 at this point and the wish pass had nothing to pick up. Now there can be, and the wish
        // share pass takes the WishR3 slider's cut of it — 0% really allocates nothing since the overCap
        // spare pass was removed, so a leftover with the slider at 0 simply stays idle. The log names the
        // destination so leftover R3 doesn't read as a stuck allocator.
        private static bool _surplusReported;
        private void ReportSurplusOnce()
        {
            if (_surplusReported) return;
            long idle = _character.res3.idleRes3;
            if (idle <= 0) return;
            _surplusReported = true;
            Main.Log($"Hacks: every hack in this list is at the most R3 it can use; "
                   + $"{Managers.NumberFormatter.Abbrev(idle)} left over is offered to wishes "
                   + $"(Wish R3 share: {Main.Settings.WishR3:0.#}%).");
        }

        public override void Reset()
        {
            _surplusReported = false;
            // The roster is keyed to the timeline that produced it, so a rebirth or a profile reload
            // must drop it rather than let last run's tokens describe this run's pool. The board falls
            // back to "no pass seen yet" for the one allocation tick until the next swap re-records —
            // the same window LastEnergyPlan/LastMagicPlan have after a reload.
            Managers.R3PoolView.Reset();
            base.Reset();
        }

        private void RemoveR3() => _character.hacksController.removeAllR3();
    }
}
