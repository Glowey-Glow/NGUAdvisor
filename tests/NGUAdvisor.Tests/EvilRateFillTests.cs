using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE EVIL RATE-LANE FILL, from the live 2026-08-03 defect (audit/25-evil-ngu-zero.md,
    // amendment 19): a 2-hour, both-pools, all-lanes zero that was invisible in every log. These
    // tests pin the two fixes — the SURFACING line that names the state (amendment 19 §4), and the
    // two-regime rate fill that ends it (amendment 19 §3). The scenario numbers are the live ones:
    // pool ~590B magic, evil id-0 capacity 10x-1000x that, id 6 hardCap-clamped
    // ([DECOMP] AllNGUController.cs:1426-1470 clamps to Character.hardCap() = 9e18,
    // [DECOMP] Character.cs:918-921 — the long-overflow guard, not the player's cap).
    public class EvilRateFillTests
    {
        private const long LivePool = 590_000_000_000;          // 25 §2: BR-0's reconstruction, ~590B
        private const long CheapestEvilCapacity = 5_900_000_000_000;   // 10x the pool — evil id 0
        private const long HardCap = 9_000_000_000_000_000_000;        // Character.hardCap()

        private static BudgetPass.BudgetState NoBudgetPressure() =>
            new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 };

        private static ConstraintLayer.LaneSpec RateLane(string label, long capacity)
            => new ConstraintLayer.LaneSpec
            {
                Name = "NGUBP",
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = capacity,
                RateLane = true,
            };

        private static ConstraintLayer.LaneSpec Sink(string label = "CAPWAN")
            => new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP",
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
                SurplusSink = true,
            };

        // An ordinary fill lane behind the rate group — the shape amendment 27's defect starved.
        private static ConstraintLayer.LaneSpec Lane(string name, string label, long capacity)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = capacity,
                WantsMore = true,
            };

        // The magic h:20 list shape at 79207s: NGU-0..6 ahead of the sink, every capacity a
        // multiple of the whole pool (25 §3's dividers put evil id 0 at 10x-1000x, id 6 at the
        // 9e18 hardCap clamp).
        private static List<ConstraintLayer.LaneSpec> LiveMagicLanes()
            => new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGU-0", CheapestEvilCapacity),          // 10x the pool
                RateLane("NGU-1", 59_000_000_000_000),            // 100x
                RateLane("NGU-2", 590_000_000_000_000),           // 1000x
                RateLane("NGU-3", 590_000_000_000_000),
                RateLane("NGU-4", 5_900_000_000_000_000),
                RateLane("NGU-5", 5_900_000_000_000_000),
                RateLane("NGU-6", HardCap),                       // clamped, rescues nothing
                Sink(),
            };

        // ---- the surfacing line (amendment 19 §4) — fix 1 -----------------------------------------

        [Fact]
        public void The_tally_still_names_a_total_zero_state_when_one_can_still_happen()
        {
            // ⚠ UPDATED BY AMENDMENT 28. This test used to reconstruct amendment 18's all-or-
            // nothing rule (a FillSession built WITHOUT the lane context, so every rate lane filled
            // regime-A style) and assert the tally named the 79207s two-hour zero. THAT STATE IS
            // NOW UNREACHABLE — with the denominator unconditional, a rate lane dearer than the
            // pool chunks rather than refusing, which is what The_live_zero_state_now_chunks_
            // every_lane_none_zero pins. The amendment 19 §4 surfacing machinery still has to name
            // the zero state that CAN still occur: a pool with less than one whole unit for each
            // destination still waiting. Same line, same inequality, same tally.
            var plan = ConstraintLayer.Compose(1, NoBudgetPressure(), LiveMagicLanes());

            foreach (var d in plan.Lanes.Where(l => l.RateLane))
            {
                Assert.True(d.Seated);
                Assert.Equal(0, d.Allocation);
                Assert.Contains("pool exhausted", d.Reason);
            }

            Assert.Equal(7, plan.RateLanesSkipped);
            Assert.Equal(CheapestEvilCapacity, plan.RateSkipCheapest);
            Assert.Equal(1, plan.RateSkipPool);
            Assert.True(plan.RateSkipPool < plan.RateSkipCheapest,
                "the line's inequality must be literally true");

            // Nothing was allocated, so spec §8 still holds: the sink takes the lot.
            Assert.Equal(1, plan.SinkAllocation);
        }

        // ---- the two-regime rate fill (amendment 19 §3) — fix 2 -----------------------------------

        // THE REGRESSION TEST for the live defect: the 79207s lane set through Compose. Against the
        // single-regime code every Evil NGU came out at literal zero for two hours, both pools,
        // with the whole pool draining through the sink as apparent normal operation. Under
        // regime B every lane chunks at roughly its share of the pool — the old path's (and the
        // guide's human's) behaviour when BB is unaffordable.
        [Fact]
        public void The_live_zero_state_now_chunks_every_lane_none_zero()
        {
            var plan = ConstraintLayer.Compose(LivePool, NoBudgetPressure(), LiveMagicLanes());

            long rateTotal = 0;
            foreach (var d in plan.Lanes.Where(l => l.RateLane))
            {
                Assert.True(d.Seated);
                Assert.True(d.Allocation > 0, $"{d.Label} must not be zero under regime B");
                // A chunk, never the unaffordable full capacity — and never past the equal share
                // by more than the game's own x1.00000202655792 fudge. Eight destinations here,
                // not seven: the sink holds a slot in the divisor (amendment 27 §4.2/§6.2).
                Assert.True(d.Allocation < d.Capacity);
                Assert.True(d.Allocation > LivePool / 8 / 2, $"{d.Label} chunk far below its share");
                Assert.True(d.Allocation <= LivePool / 8 + LivePool / 8 / 2, $"{d.Label} chunk far above its share");
                rateTotal += d.Allocation;
            }

            Assert.True(rateTotal <= LivePool);
            // The NGUs drink seven eighths of the pool. The sink gets the eighth the divisor
            // reserved for it — neither the whole pool the NGUs refused (the 79207s state) nor
            // the literal zero the rate-lane divisor left it (the 14:33 state).
            Assert.True(plan.SinkAllocation > 0,
                "spec §8: there is always a destination that accepts everything");
            Assert.True(plan.SinkAllocation < LivePool / 4,
                "the sink must no longer absorb the pool the NGUs refused");
            Assert.Equal(LivePool, rateTotal + plan.SinkAllocation);

            // The skip state is over — the surface line falls silent and the all-clear can fire.
            Assert.Equal(0, plan.RateLanesSkipped);
            Assert.Equal("", ConstraintLayer.RateSkipSignature(plan));
        }

        // ---- amendment 27: the denominator is ALL SEATED LANES, not the rate lanes ---------------

        // THE 14:33 ENERGY BLOCK, verbatim from debug.log (2026-08-04, 62963s) — the first
        // per-lane offered/taken read the constraint layer has ever had. Regime B is engaged
        // (the cheapest capacity, NGU-2's 1.09e13, is ~12x the pool), the split across the group
        // is even and correct — and dividing the REMAINING pool by the RATE LANES hands the group
        // 100% of it: nine lanes at 11.028%-11.168%, then AdvancedTraining-0/1/3/4, the surplus
        // sink and Augment-2/3 all at literal zero, remainder=0.
        private const long LiveEnergyPool = 926_504_309_183;

        // The nine live `cap=` figures, in list order. Every one of them is 12x-12000x the pool.
        private static readonly long[] LiveEnergyRateCaps =
        {
            14_808_281_714_411,      // NGU-0
            14_459_904_784_375,      // NGU-1
            10_906_007_660_430,      // NGU-2 — the cheapest, and still ~12x the pool
            46_148_632_290_559,      // NGU-3
            109_037_458_654_222,     // NGU-4
            443_388_810_461_694,     // NGU-5
            1_085_850_223_183_459,   // NGU-6
            3_393_281_822_258_306,   // NGU-7
            11_424_049_243_940_980,  // NGU-8
        };

        // Live capacities for the starved lanes, read off the SAME tick's normal-track block
        // (62983s), where the same four AT lanes and Augment-2 did receive an offer and
        // self-limited below it. The sink sits at index 13 in the live list, with both augments
        // behind it — it is filled last regardless, which is what makes "the remainder reaches
        // the sink" a statement about the fill and not about list order.
        private const long LiveAtCapacity = 29_038_864_600;
        private const long LiveAugmentCapacity = 2_885_710_834;

        private static List<ConstraintLayer.LaneSpec> LiveEnergyLanes()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>();
            for (int i = 0; i < LiveEnergyRateCaps.Length; i++)
                lanes.Add(RateLane("NGU-" + i, LiveEnergyRateCaps[i]));
            foreach (var id in new[] { 0, 1, 3, 4 })
                lanes.Add(Lane("AdvancedTrainingBP", "AdvancedTraining-" + id, LiveAtCapacity));
            lanes.Add(Sink("CAPWandoos-0"));
            lanes.Add(Lane("AugmentBP", "Augment-2", LiveAugmentCapacity));
            lanes.Add(Lane("AugmentBP", "Augment-3", LiveAugmentCapacity));
            return lanes;
        }

        [Fact]
        public void The_rate_group_no_longer_owns_the_pool_the_lanes_behind_it_are_offered_something()
        {
            var plan = ConstraintLayer.Compose(LiveEnergyPool, NoBudgetPressure(), LiveEnergyLanes());

            Assert.Equal(16, plan.Lanes.Length);
            Assert.All(plan.Lanes, d => Assert.True(d.Seated, d.Label + " must seat"));

            // Regime B is engaged: nothing can be blanked, so nothing is skipped.
            Assert.Equal(0, plan.RateLanesSkipped);
            foreach (var d in plan.Lanes.Where(l => l.RateLane))
            {
                Assert.True(d.Allocation > 0, $"{d.Label} must not be zero under regime B");
                Assert.True(d.Allocation < d.Capacity, $"{d.Label} must chunk, never blank");
            }

            // THE DEFECT: the nine-lane group took 100% of the pool. Under amendment 27 §4.2 the
            // divisor is every seated lane not yet offered — sixteen of them — so the group takes
            // nine sixteenths and leaves the rest standing.
            //
            // ⚠ UPDATED BY AMENDMENT 29 (the waterfill). The bound was 5/8 and the group now takes
            // 78.2%, so the NUMBER had to move — and the PROPERTY it was standing in for did not.
            // What 5/8 was really testing is "no lane behind the group is starved for the benefit of
            // one ahead of it", and that is now asserted DIRECTLY, below, on every lane. The reason
            // the group's share rose is not list position: six of the sixteen lanes are saturated
            // (four AdvancedTraining at 29,038,864,600 and two Augments at 2,885,710,834, every one
            // funded to its FULL capacity), so 121.9 B of the pool has nowhere else to go, and the
            // 804.6 B that remains is split among the ten lanes that can still absorb — the nine
            // rate lanes and the sink — at 80.4-80.5 B EACH.
            //
            // ⚠ THAT IS THE DISCRIMINATION AGAINST AMENDMENT 28's DEFECT, and it is worth stating
            // because "the rate group takes 78%" is the same headline number 28 was written to kill.
            // 28's 78% went to ONE lane, chosen by list order, with Augment-3 and the sink at LITERAL
            // ZERO. Here every one of the ten unsaturated lanes gets the same 8.68%, the sink
            // included, and reversing the list cannot change it.
            long rateTotal = plan.Lanes.Where(l => l.RateLane).Sum(l => l.Allocation);
            Assert.True(rateTotal > LiveEnergyPool / 2,
                "nine of sixteen lanes is still most of the pool — this is a denominator fix, not a cap");

            // Every lane BEHIND the group is offered something, and takes its whole capacity.
            foreach (var d in plan.Lanes.Where(l => !l.RateLane && !l.SurplusSink))
            {
                Assert.True(d.Allocation > 0,
                    $"{d.Label} was offered nothing — the pool was exhausted by the lanes ahead of it");
                Assert.Equal(d.Capacity, d.Allocation);
            }

            // THE ANTI-STARVATION PROPERTY, asserted directly rather than through a 5/8 proxy: every
            // lane that is not saturated ends within 1% of every other, sink included. A residual
            // allocation cannot produce this — that is the whole content of amendment 28 §1.
            var unsaturated = plan.Lanes
                .Where(l => l.Allocation != l.Capacity || l.SurplusSink)
                .Select(l => l.Allocation).ToList();
            Assert.Equal(10, unsaturated.Count);       // nine rate lanes + the sink
            Assert.True(unsaturated.Max() - unsaturated.Min() < unsaturated.Max() / 100,
                $"the unsaturated lanes must land together: {unsaturated.Min()}..{unsaturated.Max()}");

            // And the remainder reaches the sink: constraint-layer-spec §8's always-a-destination
            // guarantee, which amendment 27 §3 recorded as currently FAILING, holds again — and the
            // sink is now a PEER of the lanes it used to be handed the residue by.
            Assert.True(plan.SinkAllocation > 0,
                "spec §8: there is always a destination that accepts everything");
            Assert.Contains(plan.SinkAllocation, unsaturated);
            Assert.Equal(0, plan.Unallocated);
            Assert.Equal(LiveEnergyPool, plan.Lanes.Sum(l => l.Allocation));
        }

        [Fact]
        public void The_even_split_within_the_rate_group_survives_the_chunks_are_only_smaller()
        {
            // ⚠ The even split is RIGHT and is not what amendment 27 changes. The nine lanes still
            // come out within a hair of each other — at ~1/16 of the pool apiece instead of ~1/9.
            var plan = ConstraintLayer.Compose(LiveEnergyPool, NoBudgetPressure(), LiveEnergyLanes());
            var chunks = plan.Lanes.Where(l => l.RateLane).Select(l => l.Allocation).ToList();

            long min = chunks.Min(), max = chunks.Max();
            Assert.True(max - min < max / 100,
                $"the group's split must stay even: {min}..{max}");

            // ⚠ UPDATED BY AMENDMENT 29 (the waterfill). The share is no longer 1/16 of the pool,
            // because six of the sixteen lanes SATURATE inside round 1 and stop being claimants. The
            // right denominator for what is left is the number of lanes that can still absorb it —
            // ten, the nine rate lanes and the sink — over the pool MINUS the saturated capacities.
            // That is the same 1/n it always was, recomputed once the roster is known rather than
            // guessed once at the start.
            long saturated = 4 * LiveAtCapacity + 2 * LiveAugmentCapacity;
            long share = (LiveEnergyPool - saturated) / 10;
            Assert.All(chunks, a => Assert.True(a > share - share / 20 && a < share + share / 20,
                $"chunk {a} is not the ten-claimant share {share}"));

            // The live figures were 11.028%-11.168% of the pool; they are still under that, and
            // still the SAME for every lane in the group — the property amendment 27 §4.2 fixed.
            Assert.All(chunks, a => Assert.True(a * 100.0 / LiveEnergyPool < 11.0,
                "every chunk must be smaller than the nine-lane share it used to get"));
        }

        // ---- amendment 28: the denominator applies UNCONDITIONALLY ------------------------------

        // THE 65401s ENERGY BLOCK (2026-08-04, Normal track) — the regime-A half of the same
        // defect, which amendment 27 §4.4 recorded and left open at §6.1. Twenty seconds apart on
        // one account, one ~927 B pool: regime A gave NGU-8 78.22% with Augment-3 at zero, regime B
        // gave NGU-8 6.257% with every augment funded. A 12x discontinuity on identical inputs.
        //
        // Live figures used here:
        //   pool                   909,161,174,670       verbatim
        //   NGU-8 take             79.749% of the pool   verbatim -> its capacity, since regime A
        //                                                funded it at exactly cap
        //   AdvancedTraining take  2.56% of the pool each, verbatim — these lanes are SELF-LIMITING
        //                                                on the live path, so the number is what
        //                                                their own Allocate absorbed, not a cap
        //   Augment-3              zero                  verbatim
        //   NGU-0..7               their 62963s capacities, every one 12x-12000x this pool, which
        //                          is why regime A refused them and reached NGU-8 with the pool
        //                          still whole
        //
        // Driven through FillSession the way ConstraintLayerBridge drives it — NOT through
        // Compose's own fill — because the live starvation needs SELF-LIMITING lanes: what made
        // Augment-3 zero is that Augment-2 was offered the entire remainder and absorbed it.
        private const long Live65401Pool = 909_161_174_670;
        private const long Live65401Ngu8Capacity = 725_046_945_188;    // 79.749% of the pool
        private const long Live65401AtSelfLimit = 23_274_526_072;      // 2.56% of the pool

        private static ConstraintLayer.LaneSpec SelfLimitingLane(string name, string label)
            => new ConstraintLayer.LaneSpec
            {
                Name = name,
                Label = label,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                WantsMore = true,
            };

        // The live sixteen, in live list order, shaped as the BRIDGE shapes them: rate lanes carry
        // the game's cap helper number up front, every other lane is self-limiting.
        private static List<ConstraintLayer.LaneSpec> Live65401Lanes()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>();
            for (int i = 0; i < 8; i++)
                lanes.Add(RateLane("NGU-" + i, LiveEnergyRateCaps[i]));
            lanes.Add(RateLane("NGU-8", Live65401Ngu8Capacity));
            foreach (var id in new[] { 0, 1, 3, 4 })
                lanes.Add(SelfLimitingLane("AdvancedTrainingBP", "AdvancedTraining-" + id));
            lanes.Add(Sink("CAPWandoos-0"));
            lanes.Add(SelfLimitingLane("AugmentBP", "Augment-2"));
            lanes.Add(SelfLimitingLane("AugmentBP", "Augment-3"));
            return lanes;
        }

        // What each lane's own Allocate() absorbs out of the offer it is handed. The AT lanes
        // stair-snap at 2.56% of the pool; the augments have the vacuous CAPBESTAUG bound and
        // absorb whatever they are given — which is exactly why Augment-3 saw literal zero.
        private static long Live65401SelfLimit(string label, long offer)
            => label != null && label.StartsWith("AdvancedTraining", StringComparison.Ordinal)
                ? Math.Min(offer, Live65401AtSelfLimit)
                : offer;

        // The bridge's loop (ConstraintLayerBridge:119-165), verbatim in shape.
        private static long[] DriveLiveFill(long pool, List<ConstraintLayer.LaneSpec> specs)
        {
            var plan = ConstraintLayer.Compose(pool, NoBudgetPressure(), specs);
            Assert.False(plan.CapacitiesKnown);   // self-limiting lanes: the executor drives the fill
            Assert.All(plan.Lanes, d => Assert.True(d.Seated, d.Label + " must seat"));

            var session = new ConstraintLayer.FillSession(pool, plan.Lanes);
            var takes = new long[plan.Lanes.Length];
            for (int i = 0; i < plan.Lanes.Length; i++)
            {
                if (i == plan.SinkIndex)
                    continue;
                string skip;
                var offer = session.Offer(plan.Lanes[i], out skip);
                takes[i] = Live65401SelfLimit(plan.Lanes[i].Label, offer);
                session.Commit(takes[i]);
            }
            takes[plan.SinkIndex] = session.TakeRemainder();
            return takes;
        }

        private static double Pct(long take, long pool) => take * 100.0 / pool;

        [Fact]
        public void The_65401s_block_NGU8_no_longer_takes_four_fifths_of_the_pool()
        {
            var takes = DriveLiveFill(Live65401Pool, Live65401Lanes());

            // ⚠ AGAINST THE OLD CODE this lane set reproduces the live block exactly, and every
            // assertion below fails:
            //
            //   NGU-0..7      0                 regime A: "partial funding refused", every one
            //   NGU-8         725,046,945,188   79.749% of the pool, funded at exactly cap=self
            //   AT-0,1,3,4    23,274,526,072    2.56% each — self-limited on the 184 B left over
            //   sink          0                 nothing remained (spec §8 failing)
            //   Augment-2     91,016,125,194    offered the whole remainder, absorbed it
            //   Augment-3     0                 the pool was gone
            //
            // and with the denominator unconditional it becomes:
            //
            //   NGU-0..7      ~56.8 B each      ~6.24% — the sixteen-lane share
            //   NGU-8         55,772,954,965    6.134%, down from 79.749%
            //   AT-0,1,3,4    23,274,526,072    UNCHANGED: the lane's own stair-snap is the bound,
            //                                   not the walk — its share was wider than its snap
            //   sink          101,956,857,906   the reserved slot, paid
            //   Augment-2     101,956,857,905
            //   Augment-3     101,956,857,905   from literal zero
            long share = Live65401Pool / 16;

            // NGU-8 takes roughly its sixteenth, not four fifths of the pool.
            Assert.Equal(55_772_954_965, takes[8]);
            Assert.True(takes[8] < share * 6 / 5 && takes[8] > share * 4 / 5,
                $"NGU-8 took {takes[8]} ({Pct(takes[8], Live65401Pool):F3}%) — the sixteen-lane " +
                $"share is {share}");
            Assert.True(Pct(takes[8], Live65401Pool) < 10.0,
                "the 79.749% concentration must be gone");

            // Every AdvancedTraining lane is funded, and at the 2.56% its own Allocate stops at —
            // the share it is offered is wider than its stair-snap, so the bound is the lane's, not
            // the walk's.
            for (int i = 9; i <= 12; i++)
                Assert.Equal(Live65401AtSelfLimit, takes[i]);

            // Both augments are funded. Under the old code the pool was gone before the second one.
            Assert.Equal(101_956_857_905, takes[14]);
            Assert.Equal(101_956_857_905, takes[15]);   // was literal zero

            // And the remainder reaches the sink: constraint-layer-spec §8's always-a-destination
            // guarantee, which amendment 27 §3 recorded as FAILING, holds in regime A too.
            Assert.Equal(101_956_857_906, takes[13]);   // was literal zero

            Assert.Equal(Live65401Pool, takes.Sum());
        }

        [Fact]
        public void The_65401s_block_no_lane_ahead_of_NGU8_is_blanked_out_of_the_walk()
        {
            var takes = DriveLiveFill(Live65401Pool, Live65401Lanes());

            // NGU-0..7 are 12x-12000x the pool. Under regime A every one of them was refused with
            // "partial funding refused" and contributed nothing; each now chunks at its share.
            // Each chunk lands a hair either side of the sixteen-lane share: NguCap divides the
            // level cost into equal pieces rather than taking the share flat, so a lane takes
            // slightly less than its share and the residue lifts the share of the lane behind it.
            long share = Live65401Pool / 16;
            for (int i = 0; i < 8; i++)
                Assert.True(takes[i] > share * 4 / 5 && takes[i] < share * 6 / 5,
                    $"NGU-{i} took {takes[i]}, share {share}");

            // The nine-lane group holds a little over nine sixteenths — no ceiling, just the count.
            long rateTotal = takes.Take(9).Sum();
            Assert.True(rateTotal < Live65401Pool * 5 / 8 && rateTotal > Live65401Pool / 2,
                $"the rate group took {rateTotal} of {Live65401Pool}");
        }

        // THE DISCONTINUITY, pinned. One lane set, one pool, ONE capacity moved across the old
        // regime boundary: NGU-8 dearer than the pool (nothing blankable — old regime B) versus
        // NGU-8 inside the pool (old regime A). Nothing else differs. Under the old code this flip
        // moved NGU-8 from 11.03% to 79.75% and moved every lane behind it from funded to zero.
        [Fact]
        public void The_same_lane_set_either_side_of_the_old_regime_boundary_allocates_the_same()
        {
            var dearer = Live65401Lanes();
            dearer[8] = RateLane("NGU-8", 11_424_049_243_940_980);   // its 62963s capacity

            var takesA = DriveLiveFill(Live65401Pool, Live65401Lanes());   // old regime A
            var takesB = DriveLiveFill(Live65401Pool, dearer);             // old regime B

            for (int i = 0; i < takesA.Length; i++)
            {
                Assert.True(takesA[i] > 0 && takesB[i] > 0,
                    $"lane {i} must be funded under both: {takesA[i]} vs {takesB[i]}");
                var ratio = (double)Math.Max(takesA[i], takesB[i]) / Math.Min(takesA[i], takesB[i]);
                Assert.True(ratio < 1.25,
                    $"lane {i} moved {ratio:F2}x across the old regime boundary: " +
                    $"{takesA[i]} vs {takesB[i]} — the flip must not decide the split");
            }

            // The one that used to swing 12x: within a couple of percent of itself now.
            Assert.True(Math.Abs(Pct(takesA[8], Live65401Pool) - Pct(takesB[8], Live65401Pool)) < 0.5,
                $"NGU-8: {Pct(takesA[8], Live65401Pool):F3}% vs {Pct(takesB[8], Live65401Pool):F3}%");

            Assert.Equal(Live65401Pool, takesA.Sum());
            Assert.Equal(Live65401Pool, takesB.Sum());
        }

        [Fact]
        public void A_pool_of_rate_lanes_only_is_unchanged_the_denominator_is_the_same_set()
        {
            // Nothing but rate lanes: "all seated lanes" and "the rate lanes" are the same set, so
            // the change is a no-op here. Pinned against NguCap's shipped arithmetic, exact.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGU-0", 600),
                RateLane("NGU-1", 5000),
                RateLane("NGU-2", 7000),
            };

            // ⚠ UPDATED BY AMENDMENT 29 (the waterfill): 151/218/226 with 4 left idle -> 153/220/226
            // with NOTHING left idle. This is the sinkless shape the operator reported at scale — no
            // Wandoos in the set, so every unit the walk fails to place goes IDLE — and it is the
            // shape the waterfill exists for. Four units is not much; 1,464,028,202,041 was.
            var plan = ConstraintLayer.Compose(599, NoBudgetPressure(), lanes);

            Assert.Equal(153, plan.Lanes[0].Allocation);
            Assert.Equal(220, plan.Lanes[1].Allocation);
            Assert.Equal(226, plan.Lanes[2].Allocation);
            Assert.Equal(0, plan.RateLanesSkipped);
            Assert.Equal(599, plan.Lanes.Sum(l => l.Allocation));

            // No sink in the set — and with the waterfill there is no residue left to surface.
            Assert.Equal(0, plan.Unallocated);
            Assert.Null(plan.UnallocatedReason);
        }

        [Fact]
        public void A_refused_lane_never_enters_the_denominator()
        {
            // THE SEAT RULE, unaffected by amendment 27 and required to stay so: the divisor counts
            // SEATED lanes, taken from the plan AFTER the passes. A lane refused at Pass 1 is not a
            // destination and must not shrink anyone's share — the RIT-7 divisor-inflation class,
            // four recorded instances, stays unrepresentable.
            var none = new FeasibilityPass.ExternalConstraints();

            ConstraintLayer.LaneSpec Ngu(ConstraintLayer.FocusSet f, string label, long cap)
                => new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP",
                    Label = label,
                    Feasibility = FeasibilityPass.ExternalGate(
                        ConstraintLayer.WithFocus(none, f, label, isSurplusSink: false)),
                    Capacity = cap,
                    RateLane = true,
                };

            List<ConstraintLayer.LaneSpec> Lanes(ConstraintLayer.FocusSet f) =>
                new List<ConstraintLayer.LaneSpec>
                {
                    Ngu(f, "NGU-0", 10_000_000),
                    Ngu(f, "NGU-1", 20_000_000),
                    Ngu(f, "NGU-2", 10_000_000),
                    Sink(),
                };

            var all = ConstraintLayer.Compose(1_000_000, NoBudgetPressure(), Lanes(null));
            var refused = ConstraintLayer.Compose(1_000_000, NoBudgetPressure(),
                Lanes(new ConstraintLayer.FocusSet("PAWG", new[] { "NGU-0", "NGU-1" })));

            Assert.False(refused.Lanes[2].Seated);
            Assert.Equal("not in declared focus: PAWG", refused.Lanes[2].Reason);

            // Four destinations when NGU-2 seats, three when it is refused — so the surviving
            // lanes get MORE, not the same. If the refused lane leaked into the divisor the two
            // shares would be identical.
            Assert.True(refused.Lanes[0].Allocation > all.Lanes[0].Allocation,
                "a refused lane must not count in the regime B divisor");
        }

        [Fact]
        public void The_old_regime_boundary_no_longer_flips_anything_the_shares_are_continuous()
        {
            // ⚠ UPDATED BY AMENDMENT 28. The regime-B half is UNCHANGED — the denominator was
            // already all seated lanes there. The regime-A half used to read 600/0/0/0: one unit
            // of pool crossed the cheapest capacity and the whole allocation changed shape.
            List<ConstraintLayer.LaneSpec> Lanes() => new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGU-0", 600),
                RateLane("NGU-1", 5000),
                RateLane("NGU-2", 7000),
                Sink(),
            };

            // Pool one unit BELOW the cheapest capacity. NguCap's shipped arithmetic, exact, over
            // FOUR destinations — three rate lanes and the sink (amendment 27 §4.2): 121 =
            // ceil(600/ceil(600/149) x 1.00000202655792), then 157 over share 159, then 160 over
            // share 160, and the sink keeps the 161 that reserving its slot left standing.
            //
            // ⚠ UPDATED BY AMENDMENT 29 (the waterfill): 121/157/160/161 -> 124/160/164/151. One
            // pass left 161 of 599 with the sink because the walk ended on it; a second round puts
            // the part of that the rate lanes will actually convert back in front of them, and the
            // sink keeps one round's share. THE POINT OF THIS TEST — that ONE UNIT of pool across
            // the old regime boundary does not change the SHAPE of the allocation — is strengthened,
            // not weakened: see the a/b comparison below.
            var b = ConstraintLayer.Compose(599, NoBudgetPressure(), Lanes());
            Assert.Equal(124, b.Lanes[0].Allocation);
            Assert.Equal(160, b.Lanes[1].Allocation);
            Assert.Equal(164, b.Lanes[2].Allocation);
            Assert.Equal(151, b.SinkAllocation);
            Assert.Equal(0, b.RateLanesSkipped);

            // Pool AT the cheapest capacity — ONE unit more. Nothing flips: NGU-0's capacity still
            // exceeds its share (600 > 150), so it still chunks, and every lane behind it keeps its
            // share instead of being refused for the benefit of a lane that could be blanked.
            // ⚠ AMENDMENT 29 moved lane 2 and the sink by two units each (149/152 -> 151/150) and
            // left lanes 0 and 1 exactly where they were: at 600 the round-1 residue is already down
            // to a single share, so there is next to nothing for a second round to re-offer.
            var a = ConstraintLayer.Compose(600, NoBudgetPressure(), Lanes());
            Assert.Equal(151, a.Lanes[0].Allocation);
            Assert.Equal(148, a.Lanes[1].Allocation);
            Assert.Equal(151, a.Lanes[2].Allocation);
            Assert.Equal(150, a.SinkAllocation);
            Assert.Equal(0, a.RateLanesSkipped);

            // One unit of pool moves no lane by more than 5% of the pool, and no lane falls out of
            // the walk. Under the old code lane 0 moved by 479 — four fifths of the pool — and
            // lanes 1 and 2 both went to literal zero.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(a.Lanes[i].Allocation > 0 && b.Lanes[i].Allocation > 0);
                Assert.True(Math.Abs(a.Lanes[i].Allocation - b.Lanes[i].Allocation) < 600 / 10,
                    $"lane {i}: {b.Lanes[i].Allocation} -> {a.Lanes[i].Allocation}");
            }
        }

        [Fact]
        public void The_fill_self_heals_with_the_pool_and_holds_no_state_across_ticks()
        {
            // Tick 1: the live short pool — everything chunks.
            var shortPool = ConstraintLayer.Compose(LivePool, NoBudgetPressure(), LiveMagicLanes());
            Assert.All(shortPool.Lanes.Where(l => l.RateLane), d => Assert.True(d.Allocation > 0));

            // Tick 2: the pool has grown until NGU-0's SHARE — not the raw pool — covers its
            // capacity. It blanks at exactly capacity, which is amendment 18 §1.2's BB end state
            // arriving one lane at a time; everything dearer keeps chunking rather than waiting.
            // ⚠ UPDATED BY AMENDMENT 28: this used to fire at pool == CheapestEvilCapacity, where
            // NGU-0 took the whole pool and the other six took literal zero.
            const long EightShares = CheapestEvilCapacity * 8;   // eight seated destinations
            var grown = ConstraintLayer.Compose(EightShares, NoBudgetPressure(), LiveMagicLanes());
            Assert.Equal(CheapestEvilCapacity, grown.Lanes[0].Allocation);
            Assert.All(grown.Lanes.Where(l => l.RateLane && l.Label != "NGU-0"),
                d => Assert.True(d.Allocation > 0, d.Label + " must keep chunking"));
            Assert.True(grown.SinkAllocation > 0);

            // Tick 3: the pool shrank again — straight back to the short-pool numbers, nothing
            // cached (spec §4.5: re-evaluation, not caching).
            var shrunk = ConstraintLayer.Compose(LivePool, NoBudgetPressure(), LiveMagicLanes());
            for (int i = 0; i < shrunk.Lanes.Length; i++)
                Assert.Equal(shortPool.Lanes[i].Allocation, shrunk.Lanes[i].Allocation);
        }

        [Fact]
        public void A_declared_focus_restricts_the_regime_B_group_and_surplus_still_reaches_the_sink()
        {
            // Amendment 18 §2 stands under amendments 19 and 27: focus is a Pass 1 predicate.
            // Under regime B the focused lanes chunk, the unfocused lane refuses with the focus
            // reason, and — because the divisor counts SEATED lanes only — the excluded lane does
            // not shrink anyone's share: the divisor is three (two focused lanes and the sink),
            // not the four it would be if a refused lane could reach it.
            var focus = new ConstraintLayer.FocusSet("PAWG", new[] { "NGU-0", "NGU-1" });
            var none = new FeasibilityPass.ExternalConstraints();

            ConstraintLayer.LaneSpec Ngu(string label, long cap)
                => new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP",
                    Label = label,
                    Feasibility = FeasibilityPass.ExternalGate(
                        ConstraintLayer.WithFocus(none, focus, label, isSurplusSink: false)),
                    Capacity = cap,
                    RateLane = true,
                };

            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                Ngu("NGU-0", 10_000_000),
                Ngu("NGU-1", 20_000_000),
                Ngu("NGU-2", 10_000_000),   // not in focus
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1_000_000, NoBudgetPressure(), lanes);

            // Three-way split of 1,000,000 through NguCap: 322_582 then 333_335 over what remains,
            // and the sink keeps the rest.
            //
            // ⚠ UPDATED BY AMENDMENT 29 (the waterfill): 322_582/333_335/344_083 -> 326_165/336_918/
            // 336_917. The three claimants are now within 3.3% of each other instead of 6.7%: the
            // sink no longer collects the chunking residue on top of its own share, so the FOCUSED
            // lanes get it. The property the test is named for — a focus-refused lane does not
            // count in the divisor — is unchanged and asserted below.
            Assert.Equal(326_165, plan.Lanes[0].Allocation);
            Assert.Equal(336_918, plan.Lanes[1].Allocation);

            Assert.False(plan.Lanes[2].Seated);
            Assert.Equal(0, plan.Lanes[2].Allocation);
            Assert.Equal("not in declared focus: PAWG", plan.Lanes[2].Reason);

            Assert.Equal(336_917, plan.SinkAllocation);
            Assert.Equal(0, plan.Unallocated);
        }

        [Fact]
        public void RateChunk_is_NguCaps_shipped_chunking_verbatim()
        {
            // Amendment 19 §7.1: regime B reuses NguValueMath.NguCap — never re-derives. Pin the
            // routing: RateChunk(capacity, remaining, k) must equal NguCap with num3 synthesised
            // to the capacity and MaxAllocation to the lane's share.
            foreach (var (capacity, remaining, lanesLeft) in new[]
            {
                (5_900_000_000_000L, 590_000_000_000L, 7),
                (600L, 599L, 3),
                (9_000_000_000_000_000_000L, 590_000_000_000L, 1),
            })
            {
                string skip;
                var chunk = ConstraintLayer.RateChunk(capacity, remaining, lanesLeft, out skip);
                var expected = NguValueMath.NguCap(new NguValueMath.NguCapInputs
                {
                    LevelPlusOnePlusOffset = 1f,
                    Num2 = 1.0,
                    SpeedDivider = capacity,
                    MaxAllocation = remaining / lanesLeft,
                    IdlePool = remaining,
                }).Num;

                Assert.Equal(expected, chunk);
                Assert.True(chunk > 0);
                Assert.Null(skip);
            }

            // A pool too small to hand every lane a whole unit refuses with a reason — the zero
            // still surfaces (spec §10).
            string reason;
            Assert.Equal(0, ConstraintLayer.RateChunk(1000, 3, 7, out reason));
            Assert.Contains("pool exhausted", reason);
        }

        [Fact]
        public void The_skip_line_fires_on_entry_never_spams_and_refreshes_on_the_long_interval()
        {
            const string sig = "NGU-0|NGU-1";

            // Entry: first occurrence fires immediately (lastAt = MinValue -> huge elapsed).
            Assert.Equal(ConstraintLayer.RateSkipEmit.Skips,
                ConstraintLayer.RateSkipSurfaceDecision(sig, null, double.MaxValue));

            // Unchanged state: silent inside the refresh interval, re-emits at it.
            Assert.Equal(ConstraintLayer.RateSkipEmit.Silent,
                ConstraintLayer.RateSkipSurfaceDecision(sig, sig, 10));
            Assert.Equal(ConstraintLayer.RateSkipEmit.Silent,
                ConstraintLayer.RateSkipSurfaceDecision(sig, sig, 599));
            Assert.Equal(ConstraintLayer.RateSkipEmit.Skips,
                ConstraintLayer.RateSkipSurfaceDecision(sig, sig, 600));

            // A changed SET emits, but never inside the 30s hard floor (flap guard).
            Assert.Equal(ConstraintLayer.RateSkipEmit.Skips,
                ConstraintLayer.RateSkipSurfaceDecision("NGU-0", sig, 31));
            Assert.Equal(ConstraintLayer.RateSkipEmit.Silent,
                ConstraintLayer.RateSkipSurfaceDecision("NGU-0", sig, 10));

            // Leaving the state announces the all-clear — behind the same floor.
            Assert.Equal(ConstraintLayer.RateSkipEmit.Cleared,
                ConstraintLayer.RateSkipSurfaceDecision("", sig, 31));
            Assert.Equal(ConstraintLayer.RateSkipEmit.Silent,
                ConstraintLayer.RateSkipSurfaceDecision("", sig, 10));

            // No skips and none before: nothing to say, ever.
            Assert.Equal(ConstraintLayer.RateSkipEmit.Silent,
                ConstraintLayer.RateSkipSurfaceDecision("", "", double.MaxValue));
            Assert.Equal(ConstraintLayer.RateSkipEmit.Silent,
                ConstraintLayer.RateSkipSurfaceDecision("", null, double.MaxValue));
        }

        [Fact]
        public void A_partial_skip_is_tallied_against_the_pool_it_was_actually_refused()
        {
            // The line must stay TRUE outside the total-zero state too: here the walk funds the
            // lanes ahead, so the skip was refused against the REMAINDER, not the tick pool — and
            // that is the number the tally must carry.
            // ⚠ UPDATED BY AMENDMENT 28: the old scenario (pool 1000, caps 400/900/700) produced
            // its partial skip through regime A's "capacity or nothing" and now funds all three.
            // A rate lane can still reach zero — when what is left will not give each destination
            // still waiting one whole unit — so the scenario is now a pool of 5.
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGU-0", 400),
                RateLane("NGU-1", 900),
                RateLane("NGU-2", 700),   // reached with 1 left across 2 destinations
                Sink(),
            };

            var plan = ConstraintLayer.Compose(5, NoBudgetPressure(), lanes);

            Assert.Equal(2, plan.Lanes[0].Allocation);
            Assert.Equal(2, plan.Lanes[1].Allocation);
            Assert.Equal(0, plan.Lanes[2].Allocation);
            Assert.Equal(1, plan.RateLanesSkipped);
            Assert.Equal(700, plan.RateSkipCheapest);
            Assert.Equal(1, plan.RateSkipPool);       // NOT the tick pool of 5
            Assert.True(plan.RateSkipPool < plan.RateSkipCheapest);
            Assert.Equal("NGU-2", ConstraintLayer.RateSkipSignature(plan));
            Assert.Equal(1, plan.SinkAllocation);
        }

        [Fact]
        public void No_rate_skips_means_an_empty_signature_and_a_zero_tally()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>
            {
                RateLane("NGU-0", 400),
                Sink(),
            };

            var plan = ConstraintLayer.Compose(1000, NoBudgetPressure(), lanes);

            Assert.Equal(0, plan.RateLanesSkipped);
            Assert.Equal("", ConstraintLayer.RateSkipSignature(plan));
        }
    }
}
