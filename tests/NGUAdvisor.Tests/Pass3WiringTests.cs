using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // PASS 3, WIRED (37 §S5 A1) — and the wish stall floor it shares a binade argument with
    // (37 §S5 A3). Two jobs, opposite obligations:
    //
    //   C1 must change NOTHING. ConstraintLayerBridge.cs hardcoded `WantsMore = true`, so TargetPass
    //   — compiled, guarded, ledgered, fully tested — was called by nothing. Wiring it in while no
    //   target table exists (37 §S5 A2) must reproduce that literal `true` exactly, on every lane of
    //   every system on every track. The proof is in two layers: the PASS can't produce a stop
    //   without rows, and the COMPOSITION is field-for-field identical when it doesn't.
    //
    //   C2 must change something SPECIFIC. The shipped wish guard divided by progress, which is a
    //   wrong floor, and a wrong floor is worse than no floor because it looks handled.
    //
    // ⚠ ConstraintLayerBridge reads Main.Character and is deliberately not linkable into this
    // assembly (see the csproj note). What IS linked is every decision it makes: TargetPass's wiring
    // map and row selection, ConstraintLayer.WantFromAnswer, and the composition. BridgeWant below
    // mirrors the bridge's three-shape guard verbatim so the pass-level and composition-level proofs
    // exercise the same predicate the live path runs.
    public class Pass3WiringTests
    {
        // ---- the live shape of a lane set, as (system, index) pairs -------------------------------
        // Energy NGUs 0-8 and magic NGUs 0-6 are the parser's own bounds (ALLNGU yields top = 9 / 7);
        // AT is 0-4 (AllAdvancedTraining.length == 5); augments are 7 pairs × 2 halves = 14 slots
        // (23 §7.1 S1's "14 slots"); TM is one lane per pool.
        private static IEnumerable<KeyValuePair<string, int>> LiveSlots()
        {
            for (int i = 0; i <= 8; i++) yield return Slot(TargetPass.SysNguEnergy, i);
            for (int i = 0; i <= 6; i++) yield return Slot(TargetPass.SysNguMagic, i);
            for (int i = 0; i <= 4; i++) yield return Slot(TargetPass.SysAt, i);
            for (int i = 0; i <= 13; i++) yield return Slot(TargetPass.SysAugments, i);
            yield return Slot(TargetPass.SysTmSpeed, 0);
            yield return Slot(TargetPass.SysTmGoldMulti, 0);
            yield return Slot(TargetPass.SysWandoos, 0);
        }

        private static KeyValuePair<string, int> Slot(string system, int index) =>
            new KeyValuePair<string, int>(system, index);

        private static readonly TargetPass.Track[] AllTracks =
        {
            TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
        };

        // Levels chosen to straddle every comparison Evaluate can make: the game's unset sentinel,
        // one past it, the sole standing terminal's value and one either side of it, the NGU
        // hardcap, and saturation.
        private static readonly long[] Levels = { 0L, 1L, 400L, 401L, 402L, 500L, TargetPass.NguHardCap, long.MaxValue };

        // ---- C1, layer 1: the PASS cannot stop a lane when no rows exist --------------------------

        // The structural claim the whole no-change proof rests on: WriteTarget is the ONLY
        // disposition Evaluate attaches a Satisfaction to, and it is reachable only through a row
        // that routed to Write. No rows, no WriteTarget; no WriteTarget, no Satisfied; no Satisfied,
        // WantFromAnswer returns true. Asserted by exhaustion rather than by reading the code, over
        // every live slot × every track × every interesting level.
        [Fact]
        public void With_no_target_table_no_lane_on_any_track_at_any_level_can_be_stopped()
        {
            int checkedLanes = 0;

            foreach (var slot in LiveSlots())
            foreach (var track in AllTracks)
            foreach (var level in Levels)
            {
                var lane = new TargetPass.LaneState
                {
                    System = slot.Key,
                    Index = slot.Value,
                    ActiveTrack = track,
                    LevelOnTrack = level,
                };

                // Exactly what the bridge hands Evaluate: RowsFor over a null table.
                var rows = TargetPass.RowsFor(null, lane.System, lane.Index);
                Assert.Null(rows);

                var answer = TargetPass.Evaluate(lane, rows, FeasibilityPass.Verdict.Seat());

                Assert.NotEqual(TargetPass.Disposition.WriteTarget, answer.Disposition);
                Assert.NotEqual(TargetPass.Satisfaction.Satisfied, answer.Satisfaction);

                string reason;
                Assert.True(ConstraintLayer.WantFromAnswer(answer, out reason),
                    slot.Key + "-" + slot.Value + " on " + track + " at level " + level +
                    " was stopped with no target table supplied");
                Assert.Null(reason);

                checkedLanes++;
            }

            // 38 slots (9 energy NGUs + 7 magic + 5 AT + 14 augment halves + 2 TM + Wandoos)
            // × 3 tracks × 8 levels — a real sweep, not three spot checks.
            Assert.Equal(38 * 3 * Levels.Length, checkedLanes);
        }

        // An EMPTY table is the same standalone state as no table at all — the bridge's field starts
        // null and an objective layer that produces zero rows must not read as "everything is done".
        [Fact]
        public void An_empty_table_is_standalone_mode_not_a_universal_stop()
        {
            var empty = new List<TargetPass.TargetRow>();

            foreach (var slot in LiveSlots())
            {
                var rows = TargetPass.RowsFor(empty, slot.Key, slot.Value);
                Assert.Null(rows);

                var lane = new TargetPass.LaneState
                {
                    System = slot.Key,
                    Index = slot.Value,
                    ActiveTrack = TargetPass.Track.Normal,
                    LevelOnTrack = 1_000_000L,
                };
                string reason;
                Assert.True(ConstraintLayer.WantFromAnswer(
                    TargetPass.Evaluate(lane, rows, FeasibilityPass.Verdict.Seat()), out reason));
            }
        }

        // Silence is what a table-less lane answers, and it must SURFACE a reason rather than default
        // (23 §7). The want is true either way — this pins that the wiring is producing a real
        // answer with real provenance, not an early return dressed up as one.
        [Fact]
        public void A_table_less_lane_answers_a_surfaced_silence_not_a_default()
        {
            var lane = new TargetPass.LaneState
            {
                System = TargetPass.SysNguMagic,
                Index = 3,
                ActiveTrack = TargetPass.Track.Normal,
                LevelOnTrack = 0,
            };

            var answer = TargetPass.Evaluate(lane, TargetPass.RowsFor(null, lane.System, lane.Index),
                FeasibilityPass.Verdict.Seat());

            Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Contains("never named", answer.Reason);
            Assert.Empty(answer.RowErrors);
        }

        // NGUs and TM are excluded from the target table BY DECISION, not by omission (amendment 21
        // §1: "NGUs never take a target level. On any track, in any pool, at any chapter";
        // amendment 24 §6 for TM). A silence for them is the CORRECT answer, and the ledger says so
        // in its own words — this is the assertion that stops a future reader filing them as gaps.
        [Fact]
        public void The_ngu_and_tm_silences_are_a_decision_and_the_ledger_says_so()
        {
            TargetPass.SilenceSpec spec;

            Assert.True(TargetPass.FindSilence(TargetPass.SysNguEnergy, 4, TargetPass.Track.Evil, out spec));
            Assert.Contains("no level exists", spec.Reason);
            Assert.Equal(TargetPass.SilenceClass.NonLevel, spec.Class);

            Assert.True(TargetPass.FindSilence(TargetPass.SysNguMagic, 5, TargetPass.Track.Evil, out spec));
            Assert.Contains("no level exists", spec.Reason);

            Assert.True(TargetPass.FindSilence(TargetPass.SysTmSpeed, 0, TargetPass.Track.Normal, out spec));
            Assert.Contains("no standing level", spec.Reason);

            Assert.True(TargetPass.FindSilence(TargetPass.SysTmGoldMulti, 0, TargetPass.Track.Normal, out spec));
            Assert.Equal(TargetPass.SilenceClass.Silent, spec.Class);
        }

        // ---- C1, layer 2: the wiring map ----------------------------------------------------------

        [Fact]
        public void The_wiring_map_names_six_lane_families_and_splits_two_of_them_by_pool()
        {
            Assert.Equal(TargetPass.SysNguEnergy, TargetPass.SystemForLane("NGUBP", true));
            Assert.Equal(TargetPass.SysNguMagic, TargetPass.SystemForLane("NGUBP", false));

            // ONE class, TWO systems: energy funds levelSpeed, magic funds levelGoldMulti.
            Assert.Equal(TargetPass.SysTmSpeed, TargetPass.SystemForLane("TimeMachineBP", true));
            Assert.Equal(TargetPass.SysTmGoldMulti, TargetPass.SystemForLane("TimeMachineBP", false));

            Assert.Equal(TargetPass.SysAt, TargetPass.SystemForLane("AdvancedTrainingBP", true));
            Assert.Equal(TargetPass.SysAugments, TargetPass.SystemForLane("AugmentBP", true));
            Assert.Equal(TargetPass.SysWandoos, TargetPass.SystemForLane("WandoosBP", true));
        }

        // The five families with no system in 23's schema. ⚠ BestAug is the one that looks like an
        // omission and is not: its augment pair is chosen INSIDE Allocate(), so at Pass 3 time it has
        // no slot, and naming it (augments, LaneIndex) would let an AUG-0 row speak for it. Its
        // terminator is the game's own hitAugmentTarget/hitUpgradeTarget, applied by IsValid()
        // upstream of this pass entirely (spec §7's "wired to the game's own signal").
        [Fact]
        public void Lanes_with_no_schema_system_including_BestAug_are_unnamed_deliberately()
        {
            Assert.Null(TargetPass.SystemForLane("BestAug", true));
            Assert.Null(TargetPass.SystemForLane("BasicTrainingBP", true));
            Assert.Null(TargetPass.SystemForLane("RitualBP", true));
            Assert.Null(TargetPass.SystemForLane("BR", true));
            Assert.Null(TargetPass.SystemForLane("HackBP", false));
            Assert.Null(TargetPass.SystemForLane(null, true));
            Assert.Null(TargetPass.SystemForLane("SomeLaneAddedLater", true));
        }

        // Evaluate filters by TRACK and asserts nothing about system or index, so the caller owns
        // that selection. If RowsFor leaked a neighbour's row, one lane's terminal would stop
        // another's — the failure mode this function exists to prevent.
        [Fact]
        public void RowsFor_selects_by_system_and_index_and_leaks_neither()
        {
            var table = new List<TargetPass.TargetRow>
            {
                Row(TargetPass.SysNguEnergy, 2, 401),
                Row(TargetPass.SysNguEnergy, 3, 999),
                Row(TargetPass.SysNguMagic, 2, 777),
                Row(TargetPass.SysAt, 2, 55),
                Row(TargetPass.SysNguEnergy, 2, 402),   // a second row on the SAME slot
            };

            var hit = TargetPass.RowsFor(table, TargetPass.SysNguEnergy, 2);
            Assert.Equal(2, hit.Count);
            Assert.All(hit, r => Assert.Equal(TargetPass.SysNguEnergy, r.System));
            Assert.All(hit, r => Assert.Equal(2, r.Index));

            Assert.Null(TargetPass.RowsFor(table, TargetPass.SysNguEnergy, 7));
            Assert.Null(TargetPass.RowsFor(table, TargetPass.SysWandoos, 2));
            Assert.Null(TargetPass.RowsFor(table, null, 2));
            Assert.Null(TargetPass.RowsFor(null, TargetPass.SysNguEnergy, 2));
        }

        private static TargetPass.TargetRow Row(string system, int index, long value) =>
            new TargetPass.TargetRow
            {
                System = system,
                Index = index,
                Track = TargetPass.Track.Normal,
                Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = value,
                ValueHigh = value,
                Cite = "test",
            };

        // ---- C1, layer 3: the COMPOSITION is field-for-field identical ----------------------------

        // The bridge's Pass 3 guard, verbatim, minus the live reads: the three shapes that never
        // reach Pass 3, then Evaluate, then WantFromAnswer.
        private static bool BridgeWant(ConstraintLayer.LaneSpec spec, string system, int index,
            TargetPass.Track track, long level, IList<TargetPass.TargetRow> table, out string reason)
        {
            reason = null;
            if (spec.SurplusSink || spec.RateLane || !spec.Feasibility.Seated)
                return true;
            if (system == null)
                return true;

            var lane = new TargetPass.LaneState
            {
                System = system, Index = index, ActiveTrack = track, LevelOnTrack = level,
            };
            var answer = TargetPass.Evaluate(lane, TargetPass.RowsFor(table, system, index),
                spec.Feasibility);
            return ConstraintLayer.WantFromAnswer(answer, out reason);
        }

        // A lane set shaped like a real energy pool: a gold-stalled TM (Pass 1 out), nine energy
        // NGUs, the five ATs, both augment halves of a pair, a saturated BasicTraining (Pass 2 out),
        // a beard (NoAllocation), a rate lane, an unnamed lane (BR), and the Wandoos sink.
        //
        // `track` and `atBlockLevel` exist for ONE caller — the live-wire negative control below.
        // The only standing terminal in the reference rows is AT Block on the EVIL track (Respawn
        // 401 was removed by [OPERATOR] 2026-08-07), so a control that has to be STOPPED by the
        // real table has to run a lane there and past the cap. The C1 proof passes a null table, so
        // neither parameter can reach it: with no rows, no level and no track changes any answer.
        private static List<ConstraintLayer.LaneSpec> LiveShapedSet(IList<TargetPass.TargetRow> table,
            bool usePass3, out List<string> systems, out List<int> indices,
            TargetPass.Track track = TargetPass.Track.Normal, long atBlockLevel = 32)
        {
            var lanes = new List<ConstraintLayer.LaneSpec>();
            var sys = new List<string>();
            var idx = new List<int>();

            Action<ConstraintLayer.LaneSpec, string, int, long> add = (spec, system, index, level) =>
            {
                if (usePass3)
                {
                    string why;
                    spec.WantsMore = BridgeWant(spec, system, index, track, level,
                        table, out why);
                    spec.WantReason = why;
                }
                else
                {
                    spec.WantsMore = true;      // the shipped literal
                    spec.WantReason = null;
                }
                lanes.Add(spec);
                sys.Add(system);
                idx.Add(index);
            };

            add(new ConstraintLayer.LaneSpec
            {
                Name = "TimeMachineBP", Label = "TM",
                Feasibility = FeasibilityPass.Verdict.Refuse("gold stall: bar unstarted and realGold 0 < cost 5"),
                Capacity = 5000,
            }, TargetPass.SysTmSpeed, 0, 12);

            for (int i = 0; i <= 8; i++)
                add(new ConstraintLayer.LaneSpec
                {
                    Name = "NGUBP", Label = "NGU-" + i,
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 100 + i * 37,
                }, TargetPass.SysNguEnergy, i, 250 + i * 90);

            for (int i = 0; i <= 4; i++)
                add(new ConstraintLayer.LaneSpec
                {
                    Name = "AdvancedTrainingBP", Label = "AT-" + i,
                    Feasibility = FeasibilityPass.Verdict.Seat(),
                    Capacity = 640 - i * 55,
                }, TargetPass.SysAt, i, i == 2 ? atBlockLevel : 30 + i);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "AugmentBP", Label = "AUG-4",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = 2200,
            }, TargetPass.SysAugments, 4, 8000);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "AugmentBP", Label = "AUG-5",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = 1900,
            }, TargetPass.SysAugments, 5, 7400);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "BasicTrainingBP", Label = "BT-3",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = 0,                       // saturated — Pass 2 eliminates it
            }, null, 3, 0);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "BR", Label = "BR",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = 700,
            }, null, 0, 0);                         // no schema system: never addressable by a row

            add(new ConstraintLayer.LaneSpec
            {
                Name = "Beards", Label = "BEARD",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                NoAllocation = true,
            }, null, 0, 0);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "NGUBP", Label = "CAPNGU-6",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = 3100,
                RateLane = true,                    // Evil-track NGU: Pass 3 never sees it
            }, TargetPass.SysNguEnergy, 6, 44);

            add(new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP", Label = "WAN",
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting,
                SurplusSink = true,
            }, TargetPass.SysWandoos, 0, 0);

            systems = sys;
            indices = idx;
            return lanes;
        }

        // THE C1 PROOF. Compose the same pool over the same live-shaped lane set twice — once with
        // the shipped hardcoded `WantsMore = true`, once with the wired Pass 3 and no target table —
        // and assert the two plans agree on every field that can move a unit of the pool, plus every
        // field that can change what the operator is told. Run over a range of pool sizes so the
        // fill's divisor, its rate-lane skip and its remainder all get exercised.
        [Fact]
        public void Wiring_pass_3_with_no_target_table_changes_no_allocation_anywhere()
        {
            var pools = new long[] { 0, 1, 97, 1000, 12_345, 250_000, 9_000_000, long.MaxValue / 4 };
            var budgets = new[]
            {
                new BudgetPass.BudgetState { InLevelChallenge = false, RebirthLevels = 0 },
                new BudgetPass.BudgetState { InLevelChallenge = true, RebirthLevels = 100 },
            };

            foreach (var pool in pools)
            foreach (var budget in budgets)
            {
                List<string> systems;
                List<int> indices;
                var oldLanes = LiveShapedSet(null, false, out systems, out indices);
                var newLanes = LiveShapedSet(null, true, out systems, out indices);

                // Pass 3 is genuinely running on the new set — the guard below would otherwise pass
                // vacuously if BridgeWant had short-circuited every lane.
                Assert.Equal(oldLanes.Count, newLanes.Count);

                var oldPlan = ConstraintLayer.Compose(pool, budget, oldLanes);
                var newPlan = ConstraintLayer.Compose(pool, budget, newLanes);

                Assert.Equal(oldPlan.Pool, newPlan.Pool);
                Assert.Equal(oldPlan.CapacitiesKnown, newPlan.CapacitiesKnown);
                Assert.Equal(oldPlan.SinkIndex, newPlan.SinkIndex);
                Assert.Equal(oldPlan.SinkSeated, newPlan.SinkSeated);
                Assert.Equal(oldPlan.SinkAllocation, newPlan.SinkAllocation);
                Assert.Equal(oldPlan.SinkRefusalReason, newPlan.SinkRefusalReason);
                Assert.Equal(oldPlan.Unallocated, newPlan.Unallocated);
                Assert.Equal(oldPlan.UnallocatedReason, newPlan.UnallocatedReason);
                Assert.Equal(oldPlan.BudgetExhausted, newPlan.BudgetExhausted);
                Assert.Equal(oldPlan.BudgetMessage, newPlan.BudgetMessage);
                Assert.Equal(oldPlan.RateLanesSkipped, newPlan.RateLanesSkipped);
                Assert.Equal(oldPlan.RateSkipCheapest, newPlan.RateSkipCheapest);
                Assert.Equal(oldPlan.RateSkipPool, newPlan.RateSkipPool);
                Assert.Equal(oldPlan.Vacuity.Vacuous, newPlan.Vacuity.Vacuous);
                Assert.Equal(oldPlan.Vacuity.TotalCapacity, newPlan.Vacuity.TotalCapacity);
                Assert.Equal(oldPlan.Vacuity.Surplus, newPlan.Vacuity.Surplus);

                Assert.Equal(oldPlan.Lanes.Length, newPlan.Lanes.Length);
                for (int i = 0; i < oldPlan.Lanes.Length; i++)
                {
                    var o = oldPlan.Lanes[i];
                    var n = newPlan.Lanes[i];
                    var where = "pool " + pool + " lane " + (o.Label ?? o.Name);

                    Assert.Equal(o.Allocation, n.Allocation);
                    Assert.True(o.Seated == n.Seated, where + ": seat differs");
                    Assert.Equal(o.EliminatedBy, n.EliminatedBy);
                    Assert.Equal(o.Reason, n.Reason);
                    Assert.Equal(o.Capacity, n.Capacity);
                    Assert.Equal(o.RateLane, n.RateLane);
                    Assert.Equal(o.NoAllocation, n.NoAllocation);
                    Assert.Equal(o.SurplusSink, n.SurplusSink);

                    // The one that matters: NOTHING was eliminated at Pass 3.
                    Assert.NotEqual(ConstraintLayer.PassId.Target, n.EliminatedBy);
                }

                // The pool is conserved identically on both sides — the arithmetic statement of
                // "no allocation changed", independent of the field-by-field comparison above.
                Assert.Equal(oldPlan.Lanes.Sum(l => l.Allocation), newPlan.Lanes.Sum(l => l.Allocation));
            }
        }

        // ---- C1, layer 4: the wire is LIVE, not dead ----------------------------------------------

        // The negative control for the proof above. If Pass 3 could not stop a lane at all, "changes
        // nothing" would be true for an uninteresting reason. Supply the real table — the reference
        // rows' sole standing terminal — to a lane past it, and the same wiring eliminates it at
        // Pass 3 with a surfaced reason.
        //
        // ⚠ THAT TERMINAL IS NOW AT BLOCK AT 100,000 ON THE EVIL TRACK, not Respawn 401 on Normal.
        // [OPERATOR] removed Respawn 2026-08-07 and made Block AT a hard cap; the control follows
        // the row rather than keeping a number the tables no longer carry. The claim it proves is
        // unchanged and so are its assertions — only the lane it points at moved.
        [Fact]
        public void The_same_wiring_does_stop_a_lane_once_a_met_terminal_row_is_supplied()
        {
            var table = TargetPass.GuideRows;

            List<string> systems;
            List<int> indices;
            var lanes = LiveShapedSet(table, true, out systems, out indices,
                TargetPass.Track.Evil, atBlockLevel: 150_000);

            // AT-2 in the live-shaped set sits at 150,000, past the 100,000 hard cap.
            int blockAt = Enumerable.Range(0, lanes.Count)
                .First(i => systems[i] == TargetPass.SysAt && indices[i] == 2);

            Assert.False(lanes[blockAt].WantsMore);
            Assert.Contains("target met", lanes[blockAt].WantReason);
            Assert.Contains("100000", lanes[blockAt].WantReason);

            var plan = ConstraintLayer.Compose(100_000, new BudgetPass.BudgetState(), lanes);
            Assert.Equal(ConstraintLayer.PassId.Target, plan.Lanes[blockAt].EliminatedBy);
            Assert.False(plan.Lanes[blockAt].Seated);
            Assert.Equal(0, plan.Lanes[blockAt].Allocation);

            // EXACTLY ONE lane is stopped. Without this the control could pass while the table
            // quietly terminated half the pool — which is the failure a standing terminal causes.
            var stopped = Enumerable.Range(0, lanes.Count)
                .Where(i => plan.Lanes[i].EliminatedBy == ConstraintLayer.PassId.Target)
                .ToList();
            Assert.Single(stopped);

            // ...and the rate lane beside it is NOT stopped by the same table (amendment 18 §1.2:
            // Pass 3 never sees a rate lane), nor is the sink.
            int rate = Enumerable.Range(0, lanes.Count).First(i => lanes[i].RateLane);
            Assert.True(lanes[rate].WantsMore);
            Assert.True(plan.Lanes[rate].Seated);
            Assert.True(plan.SinkSeated);
        }

        // A terminal row that is NOT yet met leaves the lane funded — the other half of the live
        // wire, and the one that would silently defund a whole system if WantFromAnswer inverted.
        [Fact]
        public void An_unmet_terminal_row_keeps_the_lane_funded()
        {
            // RowsFor hands over BOTH at-2 rows — the ch.3 Normal 5,000 precondition and the ch.5
            // Evil 100,000 terminal — and Evaluate selects by the lane's ACTIVE TRACK.
            var rows = TargetPass.RowsFor(TargetPass.GuideRows, TargetPass.SysAt, 2);
            Assert.NotNull(rows);

            var lane = new TargetPass.LaneState
            {
                System = TargetPass.SysAt,
                Index = 2,
                ActiveTrack = TargetPass.Track.Evil,
                LevelOnTrack = 99_999,                 // one short of the 100,000 hard cap
            };
            var answer = TargetPass.Evaluate(lane, rows, FeasibilityPass.Verdict.Seat());

            Assert.Equal(TargetPass.Disposition.WriteTarget, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.Unsatisfied, answer.Satisfaction);

            string reason;
            Assert.True(ConstraintLayer.WantFromAnswer(answer, out reason));
            Assert.Null(reason);
        }

        // ---- C2: the wish stall floor -------------------------------------------------------------

        // WishManager is Character-welded and not linkable here; these two expressions are its guard
        // before and after, quoted exactly, so the comparison below is between the real predicates.
        private static bool ShippedGuard(float ppt, float progress) =>
            ppt / progress <= 2.9802322E-8f;

        private static bool WiredGuard(float ppt) =>
            CapacityPass.CannotCompleteLevel(ppt);

        // The game's own zero-floor, mirrored from [DECOMP] WishesController.cs:754 — strict `<`,
        // compared as a double. Everything at or above 1e-8 is a LIVE rate as far as the game is
        // concerned.
        private static bool GameZeroFloor(float ppt) => (double)ppt < 1E-08;

        // ⚠ THE BINADE BOUNDARY, ASSERTED EMPIRICALLY RATHER THAN ARGUED. In [0.5, 1) a float's ULP
        // is 2^-24, so half an ULP is 2^-25 = 2.98e-8 — and 2e-8 is below it. The addition the game
        // performs every tick (Wish.progress += ppt, [DECOMP] WishesController.cs:278) is therefore
        // a NO-OP at exactly 0.5. This is the failure, in one line.
        [Fact]
        public void A_wish_at_2e_minus_8_cannot_move_the_bar_off_0_point_5()
        {
            Assert.Equal(0.5f, 0.5f + 2e-8f);
            Assert.True(0.5f + 2e-8f == 0.5f);

            // Not a fluke of 2e-8: the whole binade is frozen at that rate, and 499 ticks of it —
            // the lookahead the old guard used — do not move the bar either.
            float bar = 0.5f;
            for (int i = 0; i < 499; i++)
                bar += 2e-8f;
            Assert.Equal(0.5f, bar);

            // The floor is a property of the BINADE, not of 0.5: the same rate is frozen at the top
            // of it too, and the floor does not scale with progress inside it.
            Assert.Equal(0.75f, 0.75f + 2e-8f);
            Assert.Equal(0.99999994f, 0.99999994f + 2e-8f);
            Assert.Equal(CapacityPass.FinalBinadeFloor, CapacityPass.StallFloorAt(0.5f));
            Assert.Equal(CapacityPass.FinalBinadeFloor, CapacityPass.StallFloorAt(0.99999994f));
        }

        // THE GAP. The game returns a nonzero rate from 1e-8 up; the bar stops moving from 2^-25
        // down. Everything in [1e-8, 2.98e-8) is therefore a wish the game reports as progressing
        // and that can never finish — and the shipped guard, which is `ppt <= progress × 2^-25`,
        // waves the entire gap through at progress 0.5.
        [Fact]
        public void The_field_stall_gap_between_1e_minus_8_and_2_pow_minus_25_is_closed()
        {
            // ⚠ THE GAP'S LOWER EDGE IS NOT THE LITERAL 1e-8f. The game compares a FLOAT against a
            // DOUBLE — `if ((double)num < 1E-08)`, [DECOMP] WishesController.cs:754 — and 1e-8 has
            // no float representation: the nearest one rounds DOWN, so `1e-8f` promoted back to
            // double is BELOW 1e-8 and the game's own floor zeroes it. The gap opens at the next
            // representable float above that. Worth pinning because "the floor is 1e-8" reads as if
            // 1e-8 itself were on the live side of it, and it is not.
            Assert.True(GameZeroFloor(1e-8f));
            float gapFloor = 1e-8f + CapacityPass.FloatUlp(1e-8f);
            Assert.False(GameZeroFloor(gapFloor));

            var inTheGap = new[] { gapFloor, 1.2e-8f, 1.49e-8f, 1.5e-8f, 2e-8f, 2.5e-8f, 2.9e-8f, 2.98023216e-8f };

            foreach (var ppt in inTheGap)
            {
                Assert.False(GameZeroFloor(ppt));               // the game calls it a live rate
                Assert.Equal(0.5f, 0.5f + ppt);                 // ...and the bar does not move
                Assert.True(WiredGuard(ppt));                   // the new guard zeroes it
            }

            // The shipped guard misses the top of the gap outright — everything above 1.49e-8.
            foreach (var ppt in new[] { 1.5e-8f, 2e-8f, 2.5e-8f, 2.9e-8f })
                Assert.False(ShippedGuard(ppt, 0.5f));

            // And 2^-25 itself is a stall, not an advance: at exactly half an ULP, round-to-nearest
            // -even parks the bar on 0.5's even mantissa. Hence `<=`, not `<`.
            Assert.Equal(0.5f, 0.5f + (float)CapacityPass.FinalBinadeFloor);
            Assert.True(WiredGuard((float)CapacityPass.FinalBinadeFloor));
        }

        // The new guard must not be greedy either — a wrong floor in the other direction defunds
        // wishes that were working. Just above 2^-25 the bar moves, and the guard lets it.
        [Fact]
        public void Just_above_the_floor_the_bar_moves_and_the_guard_stays_out_of_the_way()
        {
            foreach (var ppt in new[] { 3e-8f, 4e-8f, 6e-8f, 1e-7f })
            {
                Assert.False(WiredGuard(ppt));
                Assert.NotEqual(0.5f, 0.5f + ppt);
            }
        }

        // The new guard is a strict SUPERSET of the shipped one, at every progress in every binade:
        // it zeroes everything the old test zeroed and more. Nothing that used to be defunded is
        // now funded — the change is one-directional, which is what makes it safe to ship.
        [Fact]
        public void The_new_floor_only_ever_catches_more_never_less()
        {
            var progresses = new[] { 0.001f, 0.0625f, 0.1f, 0.25f, 0.4999f, 0.5f, 0.75f, 0.9999999f, 1f };
            var rates = new[] { 1e-8f, 1.4e-8f, 1.49e-8f, 2e-8f, 2.98023216e-8f, 3e-8f, 5e-8f, 1e-7f, 1e-6f };

            foreach (var progress in progresses)
            foreach (var ppt in rates)
            {
                if (ShippedGuard(ppt, progress))
                    Assert.True(WiredGuard(ppt),
                        "ppt " + ppt + " at progress " + progress + " was zeroed before and is not now");
            }
        }

        // ⚠ WHY THE 499-TICK PROJECTION IS NOT THE ARGUMENT, pinned so it is not "simplified" back.
        // The old code clamped the projection to 1f, and 1f is the first value of the NEXT binade up
        // — ULP 2^-23, half-ULP 2^-24, twice the floor below it. Asking "is it stalled at the
        // projected progress" would therefore zero a wish at 4e-8 precisely BECAUSE its bar is about
        // to reach 1f, even though 4e-8 moves every float below 1f. The capacity question is
        // absolute; the local one is not usable here.
        [Fact]
        public void The_clamped_projection_would_defund_a_healthy_wish_which_is_why_it_is_gone()
        {
            const float healthy = 4e-8f;

            Assert.True(CapacityPass.StalledNow(healthy, 1f));      // the trap the clamp walks into
            Assert.False(WiredGuard(healthy));                       // the guard that shipped instead
            Assert.NotEqual(0.5f, 0.5f + healthy);                   // ...and it is genuinely moving

            // A rate below the final binade's floor moves NOW at low progress and still completes
            // nothing — the reason the absolute test is the correct one, not merely the convenient
            // one (CapacityPass's own framing: "moves now but never completes").
            Assert.False(CapacityPass.StalledNow(2e-8, 0.25f));
            Assert.True(WiredGuard(2e-8f));
        }
    }
}
