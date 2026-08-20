using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // WHERE THE R3 POOL WENT — the third pool's answer to the question the constraint layer answers
    // for Energy and Magic, produced beside the R3 allocator instead of inside it.
    //
    // WHY THIS FILE EXISTS RATHER THAN A ROUTING CHANGE. ConstraintLayerBridge.cs:19-23 excludes
    // R3Breakpoints from the constraint layer on purpose, and the exclusion did not weaken with
    // amendment 28 — it hardened. The layer now offers every seated lane
    // `min(capacity, remaining / seated destinations not yet offered)` (ConstraintLayerBridge.cs:15-17),
    // i.e. a divisor, and BOTH R3 files forbid one by name: R3Breakpoints.cs:19-27 ("prioCount stays 1
    // on purpose … an even fifteen-way split of a modest pool can push every share under the float
    // stall floor") and HackPhase.cs:36-38 ("⚠ NEVER split the pool instead. prioCount stays 1 in the
    // R3 lane by law"). A CAPALLHACK line is fifteen lanes; under the layer's denominator round one
    // would hand each of them a fifteenth of the pool, which is precisely the 2^-25 state
    // HackMath.StallFloor documents — a hack that accumulates literally nothing forever while the
    // game's own tooltip shows a finite countdown. So routing R3 would change live allocation on
    // every save to do the one thing the code says not to do, and this file changes nothing but what
    // is displayed.
    //
    // ⚠ IT EMITS THE LAYER'S OWN SHAPE, NOT A PARALLEL ONE. Compose returns a real
    // ConstraintLayer.Plan, so UiBridge's `emit` lambda, AllocTelemetry.Signature's change gate and
    // the companion's renderPools all consume R3 through the identical contract they already use for
    // Energy and Magic. If R3 is ever routed for real, LastR3Plan replaces Compose and NOTHING
    // downstream moves.
    //
    // THE POOL HAS TWO CONSUMERS AND THEY DO NOT BEHAVE ALIKE. Hacks are funded by R3Breakpoints,
    // which reclaims (hacksController.removeAllR3) and refills the whole list every pass; wishes are
    // funded once afterwards by WishManager.Allocate, which releases and re-takes its own holdings
    // and is invisible to every lane reclaim. Nothing else in the game holds R3 — the only writers of
    // res3.idleRes3 in the decompile are HacksController (:171-204), WishesController (:647, :1026,
    // :1095) and the bar's own regen (Character.cs:2404, Resource3Display.cs:62-74) — so
    // `hacks + wishes + idle` is the complete account, and this board states it as such.
    //
    // WISHES ARE MODELLED AS THE SURPLUS SINK, which is what R3Breakpoints.cs:28-32 already calls
    // them ("wishes are the other R3 sink"). That maps them onto the seat the E/M board gives
    // Wandoos, so the same tag renders for the same role with no new vocabulary.
    public static class R3PoolView
    {
        // ---- what the allocator records, at the two instants only it can see -----------------------

        // One R3 timeline entry as the swap saw it. Seated/Reason are decided where the verdict is
        // made — R3Breakpoints, with the live character in hand — because re-deriving a refusal at
        // snapshot time is a SECOND opinion that can disagree with the one that actually defunded the
        // lane, and a board whose reason contradicts its own allocation is worse than no reason.
        public struct LaneRecord
        {
            public string Label;      // the profile token verbatim ("CAPHACK-1"), as E/M lanes carry
            public int HackId;
            public bool Seated;       // survived IsValid() + the challenge overlay and was offered a budget
            public string Reason;     // why it was not, when it was not
            public long Offered;      // ResourceBreakpoint.Budget after UpdateMaxAllocation — the whole
                                      // remaining idle at this lane's turn, since prioCount stays 1
        }

        public sealed class SwapRecord
        {
            public LaneRecord[] Lanes;
            public long Pool;         // idleRes3 immediately after removeAllR3 — what this pass divided
            public bool Reclaimed;    // FALSE = PerformSwap returned at R3Breakpoints.cs:41 before
                                      // RemoveR3(), so the PREVIOUS pass's allocation is still standing
                                      // and nothing was reconsidered. Indistinguishable from a healthy
                                      // pass in every other channel, which is why it is recorded.
            public string HeldReason;
        }

        // The wish share is a number only WishManager.Allocate can know: it is a percentage of the
        // idle pool AT THAT MOMENT — after the hacks have taken their fill and after wishes have
        // released last tick's holdings — and that instant is gone by the time anything else looks.
        public sealed class WishRecord
        {
            public bool Ran;
            public double SharePercent;
            public long Offered;
        }

        // ---- the live tail, read at snapshot time --------------------------------------------------

        // What a hack is holding right now, and whether that holding is doing anything. Live rather
        // than recorded so the three summands of the account — hacks, wishes, idle — all come from ONE
        // instant and therefore balance exactly; a mix of recorded and live numbers cannot, because
        // the R3 bar regenerates into idle between the swap and the snapshot.
        public struct HackHolding
        {
            public int Id;
            public long Held;              // hacks.hacks[id].res3
            public double ProgressPerTick; // hacksController.progressPerTick(id)
        }

        public struct Inputs
        {
            public SwapRecord Swap;
            public WishRecord Wish;
            public IList<HackHolding> Hacks;   // every hack, not only the rostered ones — see Compose
            public long WishHeld;              // Σ wishes.wishes[i].res3
            public long Idle;                  // res3.idleRes3
            public bool R3Managed;             // Settings.ManageR3
            public bool WishesManaged;         // Settings.ManageWishes
        }

        // ---- why a timeline entry was not funded ---------------------------------------------------

        // The facts R3Breakpoints reads off the live character when a token fails IsValid(). Separated
        // from the reading so the SENTENCES can be asserted headlessly: a refusal reason is the entire
        // product of an unseated row, and the E/M path's own spec §10 makes "zero with no reason" the
        // defect rather than the omission.
        public struct RefusalFacts
        {
            public int HackId;
            public bool HacksUnlocked;        // buttons.hacks.interactable — which IS hacks.hacksOn
            public long Level;
            public long HardCapLevel;
            public bool TargetMet;            // hacksController.hitTarget(id)
            public bool MilestoneLane;        // a MILEHACK token, which carries the guide's extra stop
            public long MilestoneThreshold;   // hacksController.milestoneThreshold(id)
            public bool DroppedByOverlay;     // passed IsValid() and the challenge overlay removed it
        }

        // ⚠ THE ORDER MIRRORS HackBP's OWN GATES, and it has to: Unlocked() tests index, then the
        // hacks button, then the hard cap; TargetMet() tests hitTarget and — on MileHackBP only — the
        // first milestone. Reporting them in any other order would name a cause the lane had not
        // reached yet.
        public static string HackRefusal(RefusalFacts f)
        {
            var inv = CultureInfo.InvariantCulture;

            if (f.HackId < 0 || f.HackId > 14)
                return "hack id " + f.HackId.ToString(inv) + " does not exist — the game has fifteen " +
                       "hacks (0-14), so this token can never fund anything";

            if (!f.HacksUnlocked)
                return "hacks are not unlocked on this save yet";

            if (f.Level >= f.HardCapLevel)
                return "at its hard cap, level " + f.HardCapLevel.ToString(inv) +
                       " — past the cap updateAllHacks still burns the progress bar but skips the " +
                       "level++, so R3 put here would return nothing at all";

            if (f.TargetMet)
                return "at the target level you set in the game (hacksController.hitTarget) — the " +
                       "advisor honours the game's own -1 \"never fund this\" marker through the same test";

            if (f.MilestoneLane && HackMath.FirstMilestoneMet(f.Level, f.MilestoneThreshold))
                return "first milestone reached at level " + f.MilestoneThreshold.ToString(inv) +
                       " — the guide's ch.5 rule is \"get the first milestone, move on\" (HackPhase), " +
                       "so this lane is done and the R3 goes to the ones behind it";

            if (f.DroppedByOverlay)
                return "removed for this challenge by the challenge overlay, which rewrites the " +
                       "timeline when a challenge disables the system a token names";

            return "refused by IsValid() and no more specific cause could be read from live state";
        }

        // ---- the recorders -------------------------------------------------------------------------
        //
        // Plain statics holding plain data, exactly as ConstraintLayerBridge.LastEnergyPlan does. No
        // Unity type is named anywhere in this file, so it links into the headless test project.

        public static SwapRecord LastSwap { get; private set; }
        public static WishRecord LastWishShare { get; private set; }

        public static void RecordSwap(SwapRecord record) { LastSwap = record; }

        public static void RecordWishShare(double sharePercent, long offered)
        {
            LastWishShare = new WishRecord
            {
                Ran = true,
                SharePercent = sharePercent,
                Offered = offered < 0 ? 0 : offered,
            };
        }

        // Rebirth / profile reload drops both records rather than letting a previous run's roster
        // outlive the timeline that produced it — the same reason ConstraintLayerBridge holds no
        // state across ticks (spec §4.5).
        public static void Reset()
        {
            LastSwap = null;
            LastWishShare = null;
        }

        // ---- the composition -----------------------------------------------------------------------

        // Null when there is genuinely nothing to say — no pass has run and no R3 is held anywhere.
        // `emit` already treats a null plan as "no node this tick", so an R3-less save costs the wire
        // nothing and the page keeps showing two pools.
        public static ConstraintLayer.Plan Compose(Inputs input)
        {
            long hackHeldTotal = 0;
            if (input.Hacks != null)
                for (int i = 0; i < input.Hacks.Count; i++)
                    hackHeldTotal += Positive(input.Hacks[i].Held);
            long wishHeld = Positive(input.WishHeld);
            long idle = Positive(input.Idle);

            var roster = input.Swap != null && input.Swap.Lanes != null
                ? input.Swap.Lanes
                : new LaneRecord[0];

            if (roster.Length == 0 && hackHeldTotal == 0 && wishHeld == 0)
                return null;

            var lanes = new List<ConstraintLayer.LaneDecision>(roster.Length + 16);
            var claimed = new HashSet<int>();
            bool reclaimed = input.Swap == null || input.Swap.Reclaimed;

            for (int i = 0; i < roster.Length; i++)
            {
                var r = roster[i];
                // ⚠ THE SAME HACK CANNOT BE FUNDED TWICE, so its holding is reported ONCE. A timeline
                // is free to name HACK-1 twice; attributing the live holding to both rows would
                // double-count it and the three summands would no longer add to the pool, which is
                // the one property this board is built on.
                bool duplicate = !claimed.Add(r.HackId);
                var live = FindHolding(input.Hacks, r.HackId);
                long took = duplicate ? 0 : Positive(live.Held);

                lanes.Add(new ConstraintLayer.LaneDecision
                {
                    Name = "HackBP",
                    Label = r.Label,
                    Seated = r.Seated,
                    EliminatedBy = r.Seated ? ConstraintLayer.PassId.None : ConstraintLayer.PassId.Feasibility,
                    Allocation = took,
                    Offered = Positive(r.Offered),
                    Capacity = ConstraintLayer.SelfLimiting,
                    Reason = LaneReason(r, took, live, duplicate),
                });
            }

            // HACKS HOLDING R3 THAT NO TOKEN NAMES. removeAllR3 empties every hack, so after a pass
            // that reclaimed, this set is empty by construction — it is non-empty exactly when R3
            // management is off, when the pass returned before its reclaim, or when the timeline was
            // edited under a standing allocation. That R3 is stranded: nothing reclaims it until a
            // token names the hack again, and without a row for it the board's own arithmetic would
            // silently lose it.
            if (input.Hacks != null)
            {
                for (int i = 0; i < input.Hacks.Count; i++)
                {
                    var h = input.Hacks[i];
                    long held = Positive(h.Held);
                    if (held <= 0 || claimed.Contains(h.Id)) continue;
                    claimed.Add(h.Id);
                    lanes.Add(new ConstraintLayer.LaneDecision
                    {
                        Name = "HackBP",
                        Label = "HACK-" + h.Id.ToString(CultureInfo.InvariantCulture),
                        Seated = false,
                        EliminatedBy = ConstraintLayer.PassId.Feasibility,
                        Allocation = held,
                        Offered = 0,
                        Capacity = ConstraintLayer.SelfLimiting,
                        Reason = StrandedReason(held, input.R3Managed),
                    });
                }
            }

            // THE WISH LANE, ALWAYS LAST AND ALWAYS PRESENT. Last because that is when it is funded
            // (CustomAllocation's "Wishes (share of remaining idle)" step runs after the R3 swap);
            // always, because "the sliders took nothing" and "wishes are not a destination" are
            // different facts and a lane that disappears when it takes zero cannot tell them apart.
            // CAN ANY HACK STILL TAKE R3? This is the flag the two most consequential sentences on the
            // board turn on, and it cannot be read off a lane count: a fifteen-row roster in which
            // every row is refused is the state the bench save is actually in (all fifteen hacks at
            // hardCapLevel, so CAPALLHACK expands to fifteen lanes and IsValid() kills all of them).
            bool anyHackSeated = false;
            for (int i = 0; i < lanes.Count; i++)
                if (lanes[i].Seated) { anyHackSeated = true; break; }

            int sinkIndex = lanes.Count;
            bool wishSeated = input.WishesManaged && input.Wish != null && input.Wish.Ran
                              && input.Wish.SharePercent > 0;
            string wishRefusal = wishSeated ? null : WishRefusal(input);
            lanes.Add(new ConstraintLayer.LaneDecision
            {
                Name = "WishManager",
                Label = "Wishes",
                Seated = wishSeated,
                EliminatedBy = wishSeated ? ConstraintLayer.PassId.None : ConstraintLayer.PassId.Feasibility,
                SurplusSink = true,
                Allocation = wishHeld,
                Offered = input.Wish != null ? Positive(input.Wish.Offered) : 0,
                Capacity = ConstraintLayer.SelfLimiting,
                Reason = wishSeated ? WishZeroReason(input, wishHeld, anyHackSeated) : wishRefusal,
            });

            long allocated = 0;
            for (int i = 0; i < lanes.Count; i++) allocated += lanes[i].Allocation;

            var plan = new ConstraintLayer.Plan
            {
                Lanes = lanes.ToArray(),
                // ⚠ THE POOL IS THE ACCOUNT, NOT THE SWAP'S DENOMINATOR. Energy and Magic report the
                // idle pool their swap divided, because wishes are not lanes there. Here wishes ARE a
                // lane, and the only denominator against which hacks + wishes + idle all sum to 100%
                // is the whole of R3 held anywhere. It equals res3.curRes3 by the decompile's own
                // arithmetic (curRes3 = idleRes3 + Σ allocations), but it is DERIVED rather than read
                // so the three shares can never fail to close.
                Pool = allocated + idle,
                SinkIndex = sinkIndex,
                SinkSeated = wishSeated,
                SinkRefusalReason = wishRefusal,
                SinkAllocation = wishHeld,
                Unallocated = idle,
                UnallocatedReason = idle > 0 ? IdleReason(input, wishSeated, anyHackSeated) : null,
                BudgetMessage = reclaimed ? null : HeldMessage(input.Swap),
                CapacitiesKnown = false,
            };
            return plan;
        }

        // ---- reasons -------------------------------------------------------------------------------

        // Non-null whenever this lane's number needs a sentence: a refusal, a zero it did not choose,
        // or — the case unique to R3 — a holding that is provably doing nothing.
        //
        // A held pass (R3Breakpoints returning before its reclaim) reaches this method with EVERY row
        // unseated by construction, so the refusal branch below carries that case too; the pass-level
        // fact rides on Plan.BudgetMessage rather than being repeated on fifteen rows.
        private static string LaneReason(LaneRecord r, long took, HackHolding live, bool duplicate)
        {
            if (duplicate)
                return "the timeline names this hack more than once — a hack holds one allocation, so " +
                       "its R3 is reported on the first row and this one takes nothing";

            if (!r.Seated)
                return r.Reason ??
                       "refused by the R3 timeline with NO recorded reason — that omission is itself " +
                       "the defect the E/M path's spec §10 forbids";

            if (took <= 0)
                return r.Offered <= 0
                    ? "offered nothing: the hacks ahead of it in the timeline absorbed the pool — the " +
                      "R3 lane is an ORDER, not a share (prioCount stays 1), so each lane is offered " +
                      "the whole remaining idle at its turn and the ones behind get what it leaves"
                    : "offered " + Abbrev(r.Offered) + " and took none of it: HackBP stops at the " +
                      "allocation that saturates one level per tick and this hack is already past it, " +
                      "or its own gate refused inside Allocate()";

            return StallReason(took, live.ProgressPerTick);
        }

        // THE FLOAT STALL FLOOR, named on the lane that is parked on it. `progress` is a float whose
        // ULP across [0.5,1) is 2^-24, so round-to-nearest swallows any increment below half of that
        // and the bar sticks forever — HackMath.StallFloor, and the reason R3Breakpoints refuses to
        // split the pool. A stalled hack is not idle and not refused: it is holding a real share of
        // the pool and converting none of it, which is the one state this board would otherwise
        // render as a healthy coloured segment.
        private static string StallReason(long took, double ppt)
        {
            if (took <= 0) return null;
            if (ppt <= 0 || double.IsNaN(ppt))
                return "holding " + Abbrev(took) + " at an unreadable rate — progressPerTick returns " +
                       "nothing, so whether this allocation converts at all cannot be established";
            if (!HackMath.WillStall(ppt)) return null;
            return "holding " + Abbrev(took) + " at " + ppt.ToString("0.###e0", CultureInfo.InvariantCulture) +
                   " progress/tick, below the 2^-25 float stall floor (HackMath.StallFloor): progress " +
                   "is a float and round-to-nearest swallows an increment this small, so the bar can " +
                   "never reach 1 and this R3 buys nothing until the rate rises";
        }

        private static string StrandedReason(long held, bool r3Managed)
        {
            return "holding " + Abbrev(held) + " with no token naming it" +
                   (r3Managed
                       ? " — the timeline was edited under a standing allocation, or the last pass " +
                         "returned before its reclaim. Nothing reclaims this until a HACK token names " +
                         "the hack again."
                       : " — R3 allocation is off, so this is whatever was left standing when it was " +
                         "switched off. Nothing will reclaim it while it stays off.");
        }

        private static string WishRefusal(Inputs input)
        {
            if (!input.WishesManaged)
                return "wish funding is off, so nothing is offered to wishes and the hacks' leftover " +
                       "stays idle";
            if (input.Wish == null || !input.Wish.Ran)
                return "the wish pass has not run since this page connected — its share is a percentage " +
                       "of the idle pool at that moment, which nothing else can reconstruct";
            return "the Wish R3 slider is at 0%: since the overCap spare pass was removed the sliders " +
                   "are authoritative downward, so 0% really allocates nothing and the leftover stays idle";
        }

        // ⚠ A WISH WITH ZERO R3 MAKES NO PROGRESS AT ALL, whatever energy and magic it holds.
        // progressPerTick is energyFactor * magicFactor * res3Factor ([DECOMP] WishesController.cs:705)
        // and res3Factor is pow(res3Power * wish.res3, 0.17) (:831-843) — a fixed non-zero bias, so a
        // zero R3 term zeroes the product. That makes "the hacks took the whole pool" a wish OUTAGE
        // rather than a slower wish, and it is the failure mode of an index-ordered CAPALLHACK line
        // whose first fundable hack absorbs everything.
        private static string WishZeroReason(Inputs input, long wishHeld, bool anyHackSeated)
        {
            if (wishHeld > 0) return null;
            if (input.Wish != null && input.Wish.Offered > 0)
                return "offered " + Abbrev(input.Wish.Offered) + " and took none: no wish slot was free, " +
                       "or every unblacklisted wish is already at max level for this difficulty";
            return anyHackSeated
                ? "the hacks ahead of it left nothing to share — the R3 timeline is walked in ORDER and " +
                  "each lane is offered the whole remaining idle, so one fundable hack at the head of a " +
                  "CAPALLHACK line can take the pool. Every wish then holds zero R3, and progressPerTick " +
                  "multiplies a res3 term with bias 0.17 ([DECOMP] WishesController.cs:705, :831-843), " +
                  "so NO wish advances at all — this is an outage, not a slowdown"
                : "the slider's share of what was left rounds to nothing this pass";
        }

        private static string IdleReason(Inputs input, bool wishSeated, bool anyHackSeated)
        {
            if (!input.R3Managed)
                return "R3 allocation is off — nothing claims this pool until the R3 section is " +
                       "switched back on";

            // ⚠ WITH NO SEATED HACK LANE THIS R3 IS STRANDED, NOT QUEUED. WishManager.cs:67-76 states
            // the equilibrium it relies on: wishes release everything each tick and re-take their
            // percentage, "and the next swap reabsorbs it into the lanes". When every hack token is
            // refused there is no lane to reabsorb it, so the swap returns before its own reclaim and
            // the un-taken share simply sits there — forever, at (100 - slider)% of the pool. Saying
            // "reclaimed on the next swap" here would be the board's one outright false sentence.
            if (!anyHackSeated)
                return wishSeated
                    ? "STRANDED: no hack token in the timeline can take R3, so nothing reabsorbs what " +
                      "the Wish R3 slider leaves. At " +
                      input.Wish.SharePercent.ToString("0.#", CultureInfo.InvariantCulture) +
                      "% the pool settles with the other " +
                      (100.0 - input.Wish.SharePercent).ToString("0.#", CultureInfo.InvariantCulture) +
                      "% permanently idle — raise the slider to 100% to put all of it to work"
                    : "STRANDED: no hack token in the timeline can take R3 and wishes are taking none " +
                      "of it either, so this pool has no destination at all and will stay exactly where " +
                      "it is";

            if (wishSeated)
                return "what the hacks could not use, minus the Wish R3 slider's " +
                       input.Wish.SharePercent.ToString("0.#", CultureInfo.InvariantCulture) +
                       "% cut of it — the remainder is reclaimed and re-offered on the next swap";
            return "every hack in the timeline is already at the most R3 it can use, and wishes are " +
                   "taking none of the leftover — it is reclaimed and re-offered on the next swap";
        }

        private static string HeldMessage(SwapRecord swap)
        {
            var why = swap != null ? swap.HeldReason : null;
            return "no R3 was allocated this pass: " +
                   (string.IsNullOrEmpty(why)
                       ? "every token in the timeline was invalid"
                       : why) +
                   ", so R3Breakpoints returned before its reclaim and the previous allocation is " +
                   "still standing — a board showing lanes here is showing the last pass, not this one";
        }

        // ---- helpers -------------------------------------------------------------------------------

        private static HackHolding FindHolding(IList<HackHolding> hacks, int id)
        {
            if (hacks != null)
                for (int i = 0; i < hacks.Count; i++)
                    if (hacks[i].Id == id) return hacks[i];
            return new HackHolding { Id = id };
        }

        private static long Positive(long v) { return v > 0 ? v : 0; }

        private static string Abbrev(long v) { return NumberFormatter.Abbrev(v); }
    }
}
