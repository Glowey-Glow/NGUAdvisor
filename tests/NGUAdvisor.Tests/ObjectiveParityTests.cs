using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE OBJECTIVE LAYER'S COMPARISON RUN (amendment 34 §7.1). The objective layer is an ADDITIVE
    // second membership source [OPERATOR]: computed against the same tick as the profile's
    // membership, compared, logged — and NOT APPLIED. The profile is still the sole membership
    // source, so the load-bearing test in this file is the one that proves the comparator cannot
    // move a unit of the pool.
    //
    // The four invariants, in the order they can go wrong:
    //   - NO ALLOCATION CHANGES. Composition + fill, field for field, with the comparator on and
    //     off, over 8 pool sizes x 2 budget states, plus a negative control so "changes nothing" is
    //     not vacuously true.
    //   - A SILENCE IS NOT A DROP (34 §6, C4). The table records 94 silences with reasons; every one
    //     of them renders as NO OPINION, and there is no verdict in the vocabulary that could say
    //     otherwise.
    //   - ONLY A `level` ROW IS A LANE INSTRUCTION (23 §0.3, C5). 32 of 70 rows are Level; a rate,
    //     time or predicate row can never produce an add.
    //   - CHAPTER 0 IS NOT A CHAPTER (C1). StageDetector returns Unknown on any exception, and
    //     ObjectiveTable.ChapterMatches treats a query chapter of 0 as ChapterAny — matching EVERY
    //     row. A held comparison says nothing rather than everything.
    public class ObjectiveParityTests
    {
        private static ObjectiveParity.ProfileLane Lane(string cls, string label, int index,
            bool energy = true) =>
            new ObjectiveParity.ProfileLane
            {
                ClassName = cls, Label = label, Index = index, EnergyPool = energy,
            };

        // An energy membership shaped like a real profile: a TM, five NGUs, two ATs, an augment
        // half, a BasicTraining the schema cannot name, and the Wandoos sink.
        private static List<ObjectiveParity.ProfileLane> LiveShapedMembership() =>
            new List<ObjectiveParity.ProfileLane>
            {
                Lane("TimeMachineBP", "TM", 0),
                Lane("NGUBP", "NGU-0", 0),
                Lane("NGUBP", "NGU-1", 1),
                Lane("NGUBP", "NGU-2", 2),
                Lane("NGUBP", "NGU-3", 3),
                Lane("NGUBP", "NGU-4", 4),
                Lane("AdvancedTrainingBP", "AT-0", 0),
                Lane("AdvancedTrainingBP", "AT-1", 1),
                Lane("AugmentBP", "AUG-4", 4),
                Lane("BasicTrainingBP", "BT-3", 3),
                Lane("WandoosBP", "WAN", 0),
            };

        private static ObjectiveParity.Report Run(int chapter = 3,
            TargetPass.Track track = TargetPass.Track.Normal,
            List<ObjectiveParity.ProfileLane> lanes = null, bool energy = true) =>
            ObjectiveParity.Compare(chapter, true, track, track, energy,
                lanes ?? LiveShapedMembership());

        private static IEnumerable<ObjectiveParity.Row> Of(ObjectiveParity.Report r,
            ObjectiveParity.Verdict v) => r.Rows.Where(x => x.Verdict == v);

        // ---- C1: the comparator changes no allocation ---------------------------------------------

        // A live-shaped lane set for the CONSTRAINT LAYER, index-aligned with LiveShapedMembership
        // above so the comparator is fed the membership that composed this plan.
        private static List<ConstraintLayer.LaneSpec> LiveShapedSpecs()
        {
            var lanes = new List<ConstraintLayer.LaneSpec>();
            Action<string, string, long> add = (name, label, cap) =>
                lanes.Add(new ConstraintLayer.LaneSpec
                {
                    Name = name, Label = label, WantsMore = true,
                    Feasibility = FeasibilityPass.Verdict.Seat(), Capacity = cap,
                });

            add("TimeMachineBP", "TM", 5000);
            for (int i = 0; i <= 4; i++)
                add("NGUBP", "NGU-" + i, 100 + i * 37);
            add("AdvancedTrainingBP", "AT-0", 640);
            add("AdvancedTrainingBP", "AT-1", 585);
            add("AugmentBP", "AUG-4", 2200);
            add("BasicTrainingBP", "BT-3", 0);          // saturated: Pass 2 eliminates it
            lanes.Add(new ConstraintLayer.LaneSpec
            {
                Name = "WandoosBP", Label = "WAN", WantsMore = true,
                Feasibility = FeasibilityPass.Verdict.Seat(),
                Capacity = ConstraintLayer.SelfLimiting, SurplusSink = true,
            });
            return lanes;
        }

        // The bridge's loop (ConstraintLayerBridge:127-203) in shape: compose, offer each seated lane
        // its share, commit what it absorbs, hand the remainder to the sink. `runComparator` puts the
        // comparison exactly where the live path puts it — and then, to be adversarial about it,
        // ALSO inside the fill loop, where a mutating comparator would be caught.
        private static ConstraintLayer.Plan DriveFill(long pool, BudgetPass.BudgetState budget,
            bool runComparator, out long[] takes)
        {
            var plan = ConstraintLayer.Compose(pool, budget, LiveShapedSpecs());
            var session = new ConstraintLayer.FillSession(pool, plan.Lanes);
            takes = new long[plan.Lanes.Length];

            for (int i = 0; i < plan.Lanes.Length; i++)
            {
                if (i == plan.SinkIndex)
                    continue;
                string skip;
                var offer = session.Offer(plan.Lanes[i], out skip);
                if (skip != null)
                    plan.Lanes[i].Reason = skip;
                takes[i] = offer;                       // every lane here absorbs its whole offer
                session.Commit(takes[i]);
                if (runComparator)
                    Run();
            }

            takes[plan.SinkIndex] = session.TakeRemainder();
            plan.Lanes[plan.SinkIndex].Allocation = takes[plan.SinkIndex];
            if (runComparator)
                Run();
            return plan;
        }

        // THE PROOF. Same pool, same budget, same lane set — composed and filled twice, once with the
        // comparison running and once without — asserted equal on every field that can move a unit of
        // the pool and every field that can change what the operator is told.
        //
        // The structural half of the same claim is the signature: Compare() takes value copies of
        // four facts per lane and returns a report. It is handed no Plan, no LaneSpec, no
        // ResourceBreakpoint and no Character, so there is no argument through which it could write.
        [Fact]
        public void The_comparison_run_changes_no_allocation_anywhere()
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
                long[] quietTakes, loudTakes;
                var quiet = DriveFill(pool, budget, false, out quietTakes);
                var loud = DriveFill(pool, budget, true, out loudTakes);

                var where = "pool " + pool + " lvlChal " + budget.InLevelChallenge;

                Assert.Equal(quiet.Pool, loud.Pool);
                Assert.Equal(quiet.CapacitiesKnown, loud.CapacitiesKnown);
                Assert.Equal(quiet.SinkIndex, loud.SinkIndex);
                Assert.Equal(quiet.SinkSeated, loud.SinkSeated);
                Assert.Equal(quiet.SinkRefusalReason, loud.SinkRefusalReason);
                Assert.Equal(quiet.Unallocated, loud.Unallocated);
                Assert.Equal(quiet.UnallocatedReason, loud.UnallocatedReason);
                Assert.Equal(quiet.BudgetExhausted, loud.BudgetExhausted);
                Assert.Equal(quiet.BudgetMessage, loud.BudgetMessage);
                Assert.Equal(quiet.RateLanesSkipped, loud.RateLanesSkipped);
                Assert.Equal(quiet.Vacuity.Vacuous, loud.Vacuity.Vacuous);
                Assert.Equal(quiet.Vacuity.TotalCapacity, loud.Vacuity.TotalCapacity);

                Assert.Equal(quiet.Lanes.Length, loud.Lanes.Length);
                for (int i = 0; i < quiet.Lanes.Length; i++)
                {
                    var q = quiet.Lanes[i];
                    var l = loud.Lanes[i];
                    Assert.Equal(q.Allocation, l.Allocation);
                    Assert.True(q.Seated == l.Seated, where + " " + q.Label + ": seat differs");
                    Assert.Equal(q.EliminatedBy, l.EliminatedBy);
                    Assert.Equal(q.Reason, l.Reason);
                    Assert.Equal(q.Capacity, l.Capacity);
                    Assert.Equal(q.SurplusSink, l.SurplusSink);
                    Assert.Equal(quietTakes[i], loudTakes[i]);
                }

                // The arithmetic statement of the same thing, independent of the field walk.
                Assert.Equal(quietTakes.Sum(), loudTakes.Sum());
                Assert.Equal(quiet.Lanes.Sum(d => d.Allocation), loud.Lanes.Sum(d => d.Allocation));
            }
        }

        // THE NEGATIVE CONTROL for the proof above. If the comparator produced nothing on this input,
        // "changes nothing" would be true for an uninteresting reason.
        [Fact]
        public void The_same_comparison_does_produce_a_report_so_the_proof_is_not_vacuous()
        {
            var r = Run();

            Assert.False(r.Held);
            Assert.NotEmpty(r.Rows);
            Assert.True(r.Adds > 0, "the guide adds nothing at ch.3 — the comparison would be inert");
            Assert.True(r.Agreements > 0);
            Assert.True(r.NoOpinion > 0);
            Assert.NotEqual("", ObjectiveParity.Signature(r));
            Assert.NotNull(ObjectiveParity.Format("Energy", r));
        }

        // ---- C4: a silence is a NO-OPINION, never a drop ------------------------------------------

        // The vocabulary itself is the guard. A later edit that wanted to report a drop would have to
        // ADD a verdict, which fails here — rather than quietly reusing one that already exists.
        [Fact]
        public void The_vocabulary_has_no_drop_verdict_at_all()
        {
            var names = Enum.GetNames(typeof(ObjectiveParity.Verdict));
            Assert.DoesNotContain(names, n =>
                n.IndexOf("drop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("refus", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ngu-magic id 3 is Number — 23 §7.2's "the only NGU id the guide never mentions once". A
        // profile that funds it must come back as NO OPINION carrying the ledger's reason, never as
        // anything the operator could read as "the guide wants this lane gone".
        [Fact]
        public void A_slot_the_guide_never_mentions_renders_as_no_opinion_with_its_ledger_reason()
        {
            var lanes = new List<ObjectiveParity.ProfileLane>
            {
                Lane("NGUBP", "NGU-3", 3, energy: false),
            };
            var r = ObjectiveParity.Compare(3, true, TargetPass.Track.Normal,
                TargetPass.Track.Normal, false, lanes);

            var row = r.Rows.Single(x => x.Label == "NGU-3");
            Assert.Equal(ObjectiveParity.Verdict.NoOpinionSilent, row.Verdict);
            Assert.Contains("never named in any chapter", row.Detail);
            Assert.Equal(1, r.NoOpinion);
            Assert.Equal(0, r.Agreements);

            // And the rendering says so in words, before it says anything else.
            var block = ObjectiveParity.Format("Magic", r);
            Assert.Contains("NO OPINION is not a drop", block);
            Assert.Contains("NO OPINION (silent)", block);
        }

        // 34 §6's finding, which is why the reader has TWO surfaces: the guide restates an augment
        // selector four times and still supplies no level. Losing that to the ledger would lose the
        // guidance; folding it into agreement would lose the ledger entry. It is neither.
        [Fact]
        public void Guidance_without_a_level_is_a_different_no_opinion_from_a_silence()
        {
            var lanes = new List<ObjectiveParity.ProfileLane>
            {
                Lane("AugmentBP", "AUG-4", 4),
                Lane("NGUBP", "NGU-7", 7),      // 23 §7.2: GO priority-1 predicate only, no level
            };
            var r = Run(lanes: lanes);

            var aug = r.Rows.Single(x => x.Label == "AUG-4");
            Assert.Equal(ObjectiveParity.Verdict.NoOpinionGuidanceWithoutLevel, aug.Verdict);
            Assert.Contains("the guide speaks here", aug.Detail);

            var ngu = r.Rows.Single(x => x.Label == "NGU-7");
            Assert.Equal(ObjectiveParity.Verdict.NoOpinionSilent, ngu.Verdict);

            // BOTH are no-opinion, and both are counted as such — the distinction is in the reason
            // the operator reads, not in the membership claim, because there is no membership claim.
            Assert.Equal(2, r.NoOpinion);

            // The adds are the OTHER direction and are expected here: this two-lane profile seats
            // neither NGU-0..6 nor the ATs, all of which the guide levels at ch.3. Not one of them
            // came from the two lanes above, which is the point being made.
            Assert.DoesNotContain(Of(r, ObjectiveParity.Verdict.ObjectiveAdds),
                x => x.System == TargetPass.SysAugments);
            Assert.DoesNotContain(Of(r, ObjectiveParity.Verdict.ObjectiveAdds),
                x => x.System == TargetPass.SysNguEnergy && x.Id == 7);
        }

        // The sweep: every slot, every track, every chapter. A profile that seats a slot the guide
        // has no level for lands in a no-opinion class EVERY TIME — 38 slots x 3 tracks x 8 chapters.
        [Fact]
        public void No_slot_on_any_track_in_any_chapter_is_ever_reported_as_something_to_remove()
        {
            var tracks = new[]
            {
                TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
            };
            int levelled = 0, noOpinion = 0;

            foreach (var track in tracks)
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                bool energy = system != TargetPass.SysNguMagic && system != TargetPass.SysTmGoldMulti;
                var cls = ClassFor(system);
                var r = ObjectiveParity.Compare(chapter, true, track, track, energy,
                    new List<ObjectiveParity.ProfileLane> { Lane(cls, "L", id, energy) });

                var row = r.Rows.Single(x => x.Label == "L");
                switch (row.Verdict)
                {
                    case ObjectiveParity.Verdict.BothLevel:
                    case ObjectiveParity.Verdict.CampaignScopedOnly:
                        levelled++;
                        break;
                    case ObjectiveParity.Verdict.NoOpinionSilent:
                    case ObjectiveParity.Verdict.NoOpinionGuidanceWithoutLevel:
                    case ObjectiveParity.Verdict.NoOpinionConflict:
                        noOpinion++;
                        // A no-opinion NEVER carries a RENDERED LEVEL: that is the
                        // 0-as-unset-sentinel hazard the reader is built to refuse ([DECOMP]
                        // AllNGUController.cs:1311-1314), reproduced one layer up. The ledger's own
                        // prose says "no level", which is the opposite claim and must survive.
                        Assert.DoesNotContain("[terminal]", row.Detail ?? "");
                        Assert.DoesNotContain("[PRECONDITION", row.Detail ?? "");
                        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(
                            row.Detail ?? "", @"level \d"), row.Detail);
                        break;
                    default:
                        Assert.Fail(system + " " + id + " ch" + chapter + " " + track +
                                    ": unexpected verdict " + row.Verdict);
                        break;
                }
            }

            // 38 slots x 3 tracks x 8 chapters, every one of them classified.
            Assert.Equal(38 * 3 * 8, levelled + noOpinion);
            Assert.True(levelled > 0 && noOpinion > 0);
        }

        // ⚠ NOT EVERY SILENCE IS A RECORDED ONE, and an operator reading the block needs to know
        // which kind they are looking at. The ledger is CHAPTER-AGNOSTIC (TargetPass.FindSilence
        // keys on system/id/track) while the table is CHAPTER-KEYED, so a slot the guide levels at
        // one chapter and says nothing about at another produces a silence with no ledger entry —
        // 71 of the 912 slot-queries, like ngu-energy 0-6 at ch.1 Normal.
        //
        // ⚠ THIS COMMENT USED TO SAY "ALL OF THEM CHAPTER MISSES, NOT A HOLE IN THE LEDGER". THAT
        // WAS FALSE, and ChapterMissDerivationTests measured it: the 71 are 63 chapter misses plus
        // EIGHT GENUINE HOLES. The eight are `ngu-energy 2` (Respawn) on Normal at every chapter —
        // a slot with no row on that track at ANY chapter and no ledger entry. The parenthesis
        // below records exactly when they appeared without drawing the conclusion; the count was
        // updated 70 -> 71 and the classification was not.
        // (Was 70; the Respawn slot joined them when [OPERATOR] removed its row 2026-08-07.)
        //
        // The 71 itself has NOT moved and is still the right fixture for this test's question. The
        // partition is asserted below and characterised in ChapterMissDerivationTests, which also
        // carries the ruling: the two facts are now DERIVED in ObjectiveReader rather than recorded,
        // because a chapter miss is the Chapter field of the rows that failed to match and the
        // ledger has no chapter to state it at. Adjudicating the eight — writing a reason for the
        // guide's silence about Respawn — remains the ledger owner's call.
        //
        // ObjectiveReader refuses to default either kind ("surfaced, never defaulted", 23 §7), and
        // what matters here is that BOTH render as no-opinion and NEITHER renders without a reason.
        [Fact]
        public void Both_recorded_and_unrecorded_silences_render_as_no_opinion_with_a_reason()
        {
            int recorded = 0, unrecorded = 0, levelled = 0;
            int unrecordedMisses = 0, unrecordedHoles = 0;

            foreach (var track in new[]
                     {
                         TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
                     })
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var system in ObjectiveReader.AllSystems)
            foreach (var id in Enumerable.Range(0, ObjectiveReader.IdCount(system)))
            {
                var answer = ObjectiveReader.LevelSlot(chapter, track, system, id);
                if (answer.HasLevel) { levelled++; continue; }

                bool energy = system != TargetPass.SysNguMagic && system != TargetPass.SysTmGoldMulti;
                var r = ObjectiveParity.Compare(chapter, true, track, track, energy,
                    new List<ObjectiveParity.ProfileLane> { Lane(ClassFor(system), "L", id, energy) });
                var row = r.Rows.Single(x => x.Label == "L");

                Assert.NotEqual(ObjectiveParity.Verdict.BothLevel, row.Verdict);
                Assert.NotEqual(ObjectiveParity.Verdict.ObjectiveAdds, row.Verdict);
                Assert.False(string.IsNullOrEmpty(row.Detail),
                    system + " " + id + " ch" + chapter + " " + track + " rendered without a reason");

                if (answer.SilenceKnown)
                {
                    recorded++;
                }
                else
                {
                    unrecorded++;
                    if (answer.IsChapterMiss) unrecordedMisses++; else unrecordedHoles++;
                }
            }

            Assert.Equal(38 * 3 * 8, recorded + unrecorded + levelled);
            Assert.Equal(71, unrecorded);
            Assert.True(recorded > unrecorded);

            // ⚠ THE PARTITION, so "71" can never again be read as "71 chapter misses". 63 are the
            // ledger's non-business (the guide levels the slot, at another chapter). 8 are the
            // ledger's business and it does not do it. See ChapterMissDerivationTests.
            Assert.Equal(63, unrecordedMisses);
            Assert.Equal(8, unrecordedHoles);
        }

        private static string ClassFor(string system)
        {
            if (system == TargetPass.SysNguEnergy || system == TargetPass.SysNguMagic) return "NGUBP";
            if (system == TargetPass.SysAt) return "AdvancedTrainingBP";
            if (system == TargetPass.SysAugments) return "AugmentBP";
            if (system == TargetPass.SysWandoos) return "WandoosBP";
            return "TimeMachineBP";
        }

        // ---- C5: only a `level` row is a lane instruction ------------------------------------------

        // 32 of the table's 70 rows are kind=Level. The other 38 are rate, time and predicate — a
        // predicate is a target SELECTOR computed upstream (23 §0.3), not a membership instruction.
        // Every add this comparator can produce is backed by a Level row, on every chapter and track.
        [Fact]
        public void Every_add_is_backed_by_a_level_row_and_no_predicate_row_can_produce_one()
        {
            var tracks = new[]
            {
                TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic,
            };
            int adds = 0;

            foreach (var track in tracks)
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var energy in new[] { true, false })
            {
                var r = ObjectiveParity.Compare(chapter, true, track, track, energy,
                    new List<ObjectiveParity.ProfileLane>());

                foreach (var add in Of(r, ObjectiveParity.Verdict.ObjectiveAdds))
                {
                    adds++;
                    var answer = ObjectiveReader.LevelSlot(chapter, add.Track, add.System, add.Id);
                    Assert.True(answer.HasLevel, add.Label + " added with no level row");
                    Assert.All(answer.LevelRows,
                        row => Assert.Equal(TargetPass.RowKind.Level, row.Kind));
                    // A standing add is never built out of campaign-scoped rows alone.
                    Assert.Contains(answer.LevelRows, row => row.CampaignScope == null);
                }

                // And the campaign-scoped class exists precisely so those rows do NOT become adds.
                foreach (var scoped in Of(r, ObjectiveParity.Verdict.CampaignScopedOnly))
                {
                    var answer = ObjectiveReader.LevelSlot(chapter, scoped.Track, scoped.System,
                        scoped.Id);
                    Assert.All(answer.LevelRows, row => Assert.NotNull(row.CampaignScope));
                }
            }

            Assert.True(adds > 0, "no add anywhere — the add direction would be untested");
        }

        // The two 100LC terminals (23 §4, amendment 34 §4) must never read as standing membership.
        [Fact]
        public void The_100lc_terminals_are_reported_as_campaign_scoped_and_not_as_adds()
        {
            var r = ObjectiveParity.Compare(3, true, TargetPass.Track.Normal,
                TargetPass.Track.Normal, true, new List<ObjectiveParity.ProfileLane>
                {
                    Lane("TimeMachineBP", "TM", 0),
                });

            var tm = r.Rows.Single(x => x.Label == "TM");
            Assert.Equal(ObjectiveParity.Verdict.CampaignScopedOnly, tm.Verdict);
            Assert.Contains("scope=100lc", tm.Detail);
            Assert.Equal(0, r.Agreements);

            // The slot the operator seated is NOT an add, and neither is the other 100LC terminal
            // on the magic side — the scope field is what keeps both out of the add list.
            Assert.DoesNotContain(Of(r, ObjectiveParity.Verdict.ObjectiveAdds),
                x => x.System == TargetPass.SysTmSpeed);
            var magic = ObjectiveParity.Compare(3, true, TargetPass.Track.Normal,
                TargetPass.Track.Normal, false, new List<ObjectiveParity.ProfileLane>());
            Assert.DoesNotContain(Of(magic, ObjectiveParity.Verdict.ObjectiveAdds),
                x => x.System == TargetPass.SysTmGoldMulti);
            Assert.Contains(Of(magic, ObjectiveParity.Verdict.CampaignScopedOnly),
                x => x.System == TargetPass.SysTmGoldMulti);
        }

        // A lane 23's schema cannot NAME is outside the comparison in both directions — neither an
        // agreement nor a disagreement (TargetPass.cs:147-159, the five families plus BestAug).
        [Fact]
        public void A_lane_the_schema_cannot_name_is_outside_the_comparison()
        {
            var r = Run(lanes: new List<ObjectiveParity.ProfileLane>
            {
                Lane("BasicTrainingBP", "BT-3", 3),
                Lane("BestAugmentBP", "BESTAUG", 0),
                Lane("RitualBP", "BM-2", 2),
            });

            Assert.Equal(3, r.Unnameable);
            Assert.Equal(0, r.NoOpinion);
            Assert.Equal(0, r.Agreements);

            // Every PROFILE row is unnameable and carries no system. The rows with no profile rank
            // are the add direction, which is unaffected by an unnameable lane sitting beside it —
            // an unnameable lane occupies no slot, so it neither suppresses nor creates an add.
            var seated = r.Rows.Where(x => x.ProfileRank >= 0).ToList();
            Assert.Equal(3, seated.Count);
            Assert.All(seated, row =>
            {
                Assert.Equal(ObjectiveParity.Verdict.Unnameable, row.Verdict);
                Assert.Null(row.System);
            });
        }

        // ---- C1: chapter-unknown holds, and says nothing ------------------------------------------

        // StageDetector.cs:92-96 returns Unknown — Known=false, Chapter=0 — on ANY exception, and
        // ObjectiveTable.ChapterMatches(:905-909) reads a query chapter of 0 as ChapterAny, matching
        // EVERY row in the table. Taking that at face value reports the whole guide as a divergence.
        [Fact]
        public void Chapter_unknown_produces_no_divergence_report()
        {
            var r = ObjectiveParity.Compare(0, false, TargetPass.Track.Normal,
                TargetPass.Track.Normal, true, LiveShapedMembership());

            Assert.True(r.Held);
            Assert.Empty(r.Rows);
            Assert.Equal(0, r.Adds);
            Assert.Equal(0, r.Agreements);
            Assert.Equal(0, r.NoOpinion);
            Assert.Contains("chapter unknown", r.HeldReason);

            // It cannot reach the log at all: an empty signature is refused by the throttle, and the
            // renderer returns null rather than an empty block.
            Assert.Equal("", ObjectiveParity.Signature(r));
            Assert.False(ObjectiveParity.ShouldEmit(ObjectiveParity.Signature(r), "", 100_000));
            Assert.Null(ObjectiveParity.Format("Energy", r));
        }

        // The second door. A `Known` stage carrying an out-of-range chapter — a detector edit, a new
        // difficulty band — must not reach the table either, because chapter 0 IS ChapterAny.
        [Fact]
        public void A_chapter_outside_one_to_eight_is_held_even_when_the_stage_claims_to_know_it()
        {
            foreach (var chapter in new[] { 0, -1, 9, 99 })
            {
                var r = ObjectiveParity.Compare(chapter, true, TargetPass.Track.Normal,
                    TargetPass.Track.Normal, true, LiveShapedMembership());
                Assert.True(r.Held, "chapter " + chapter + " was not held");
                Assert.Empty(r.Rows);
            }

            // ...and 1-8 are not held, so the guard is not simply refusing everything.
            foreach (var chapter in Enumerable.Range(1, 8))
                Assert.False(Run(chapter).Held, "chapter " + chapter + " was held");
        }

        // An unreadable track is the same hazard one step down: TrackMatches admits only TrackNeutral
        // and AllTracks rows against Unspecified, so every level row would vanish and every slot
        // would read as a silence — a report that says "the guide has no opinion on anything".
        [Fact]
        public void An_unreadable_track_holds_rather_than_reporting_every_slot_as_silent()
        {
            var held = ObjectiveParity.Compare(3, true, TargetPass.Track.Unspecified,
                TargetPass.Track.Normal, true, LiveShapedMembership());
            Assert.True(held.Held);
            Assert.Contains("track unreadable", held.HeldReason);

            Assert.True(ObjectiveParity.Compare(3, true, TargetPass.Track.Normal,
                TargetPass.Track.Unspecified, true, LiveShapedMembership()).Held);
        }

        // ---- C3: the throttle ----------------------------------------------------------------------

        [Fact]
        public void The_throttle_fires_on_change_and_refreshes_on_unchanged()
        {
            var a = ObjectiveParity.Signature(Run());
            var b = ObjectiveParity.Signature(Run(chapter: 5));
            Assert.NotEqual(a, b);

            // A change emits once the 30s floor has passed, and is held below it — Analyze runs
            // ~86,400x/day and a flapping signature must not become a per-tick log.
            Assert.True(ObjectiveParity.ShouldEmit(b, a, 31));
            Assert.False(ObjectiveParity.ShouldEmit(b, a, 29));

            // An unchanged signature waits for the slow refresh.
            Assert.False(ObjectiveParity.ShouldEmit(a, a, 100));
            Assert.False(ObjectiveParity.ShouldEmit(a, a, 599));
            Assert.True(ObjectiveParity.ShouldEmit(a, a, 600));

            // The constants are ConstraintParity's, not a second copy of them.
            Assert.Equal(30, ConstraintParity.MinIntervalSeconds);
            Assert.Equal(600, ConstraintParity.RefreshIntervalSeconds);
        }

        // The signature keys on the SET of (verdict, slot) pairs and on nothing else — it must move
        // when the comparison moves and stay put when it does not.
        [Fact]
        public void The_signature_moves_on_membership_and_track_and_holds_otherwise()
        {
            var baseline = ObjectiveParity.Signature(Run());

            Assert.Equal(baseline, ObjectiveParity.Signature(Run()));

            var reordered = LiveShapedMembership();
            var moved = reordered[1];
            reordered.RemoveAt(1);
            reordered.Add(moved);
            // ⚠ ORDER IS NOT IN THE SIGNATURE. The profile has an order and the objective table has
            // none (see the C6 note on Row.ProfileRank), so a reorder is not a divergence — it is
            // rendered in the block and deliberately not throttled on.
            Assert.Equal(baseline, ObjectiveParity.Signature(Run(lanes: reordered)));

            var dropped = LiveShapedMembership();
            dropped.RemoveAt(2);
            Assert.NotEqual(baseline, ObjectiveParity.Signature(Run(lanes: dropped)));

            Assert.NotEqual(baseline, ObjectiveParity.Signature(Run(track: TargetPass.Track.Evil)));
        }

        // ---- what the report says -------------------------------------------------------------------

        // The profile's ORDER is carried where it exists — it is the profile's fact, and the one the
        // operator needs to act on an add — and is simply absent on an objective-only row rather
        // than faked as a rank the table cannot supply.
        [Fact]
        public void The_profile_rank_is_carried_on_profile_lanes_and_absent_on_adds()
        {
            var r = Run();

            foreach (var row in r.Rows.Where(x => x.Verdict != ObjectiveParity.Verdict.ObjectiveAdds))
                Assert.InRange(row.ProfileRank, 0, LiveShapedMembership().Count - 1);

            foreach (var add in Of(r, ObjectiveParity.Verdict.ObjectiveAdds))
                Assert.Equal(-1, add.ProfileRank);

            // Profile lanes appear in the profile's own order, so "#3" in the block means the third
            // token in the priority list.
            var ranks = r.Rows.Where(x => x.ProfileRank >= 0).Select(x => x.ProfileRank).ToList();
            Assert.Equal(ranks.OrderBy(x => x).ToList(), ranks);

            Assert.Contains("(profile #1)", ObjectiveParity.Format("Energy", r));
        }

        // The terminality of every rendered level travels WITH it: 23 §0.4's failure is a
        // precondition read as a stopping point, and a comparison block that showed "level 3000" with
        // no qualifier is exactly how that reading gets made by a human instead of by the router.
        [Fact]
        public void Every_rendered_level_carries_its_terminality()
        {
            var r = Run();
            var block = ObjectiveParity.Format("Energy", r);

            foreach (var row in r.Rows.Where(x =>
                         x.Verdict == ObjectiveParity.Verdict.BothLevel ||
                         x.Verdict == ObjectiveParity.Verdict.ObjectiveAdds ||
                         x.Verdict == ObjectiveParity.Verdict.CampaignScopedOnly))
            {
                Assert.Contains("level ", row.Detail);
                Assert.True(
                    row.Detail.Contains("[terminal]") ||
                    row.Detail.Contains("PRECONDITION") ||
                    row.Detail.Contains("ambiguous") ||
                    row.Detail.Contains("terminality unfilled"),
                    row.Label + " rendered a level with no terminality: " + row.Detail);
            }

            // ⚠ THE TERMINAL EXEMPLAR IS NO LONGER RESPAWN 401. [OPERATOR] removed that row
            // 2026-08-07 ("the 401 was in the guide because it's the 'best' for where the user is at
            // at the time"), so the Energy block has no terminal left to render — the standing
            // terminal is now Block AT 100,000, which is an AT row and does not appear here. What
            // this test is FOR — that a rendered level never appears without its terminality — is
            // asserted over every row by the loop above and is unaffected.
            Assert.DoesNotContain("level 401", block);

            // A range that stays a range rather than being collapsed to an end (23 §0.1).
            Assert.Contains("level 2K-3K [PRECONDITION", block);
        }

        // The pool split is the lane classes' own CorrectResourceType, not an assumption: an energy
        // comparison can never propose a magic NGU, and vice versa.
        [Fact]
        public void The_add_direction_respects_the_pool_each_system_belongs_to()
        {
            var empty = new List<ObjectiveParity.ProfileLane>();

            foreach (var chapter in Enumerable.Range(1, 8))
            {
                var energy = ObjectiveParity.Compare(chapter, true, TargetPass.Track.Normal,
                    TargetPass.Track.Normal, true, empty);
                Assert.All(energy.Rows, row =>
                {
                    Assert.NotEqual(TargetPass.SysNguMagic, row.System);
                    Assert.NotEqual(TargetPass.SysTmGoldMulti, row.System);
                });

                var magic = ObjectiveParity.Compare(chapter, true, TargetPass.Track.Normal,
                    TargetPass.Track.Normal, false, empty);
                Assert.All(magic.Rows, row =>
                {
                    Assert.NotEqual(TargetPass.SysNguEnergy, row.System);
                    Assert.NotEqual(TargetPass.SysTmSpeed, row.System);
                    Assert.NotEqual(TargetPass.SysAt, row.System);        // AdvancedTrainingBP.cs:35
                    Assert.NotEqual(TargetPass.SysAugments, row.System);  // AugmentBP.cs:8
                });
            }
        }

        // The NGU lanes follow the live selector settings.nguLevelTrack and everything else follows
        // the run's difficulty — the SAME split ConstraintLayerBridge.LaneStateFor makes (:427-453),
        // read from one place rather than two.
        [Fact]
        public void The_ngu_lanes_and_the_rest_take_their_tracks_from_different_reads()
        {
            var r = ObjectiveParity.Compare(3, true, TargetPass.Track.Evil, TargetPass.Track.Normal,
                true, LiveShapedMembership());

            Assert.Equal(TargetPass.Track.Evil,
                r.Rows.Single(x => x.Label == "NGU-2").Track);
            Assert.Equal(TargetPass.Track.Normal,
                r.Rows.Single(x => x.Label == "AT-0").Track);

            Assert.Equal(TargetPass.Track.Evil,
                ObjectiveParity.TrackFor(TargetPass.SysNguMagic, TargetPass.Track.Evil,
                    TargetPass.Track.Normal));
            Assert.Equal(TargetPass.Track.Normal,
                ObjectiveParity.TrackFor(TargetPass.SysWandoos, TargetPass.Track.Evil,
                    TargetPass.Track.Normal));
        }

        // 23 §2.3's M0/M1 pair: two irreconcilable readings of the guide's own sentence, adjudicated
        // by neither the audit nor this comparator. No number is emitted on either reading, so no
        // membership claim is made — it is a no-opinion that names itself as one.
        [Fact]
        public void An_unadjudicated_conflict_is_surfaced_as_its_own_no_opinion()
        {
            var conflicted = new List<ObjectiveParity.Row>();
            foreach (var chapter in Enumerable.Range(1, 8))
            foreach (var track in new[] { TargetPass.Track.Normal, TargetPass.Track.Evil })
            {
                var r = ObjectiveParity.Compare(chapter, true, track, track, false,
                    new List<ObjectiveParity.ProfileLane>
                    {
                        Lane("NGUBP", "M0", 0, energy: false),
                        Lane("NGUBP", "M1", 1, energy: false),
                    });
                conflicted.AddRange(Of(r, ObjectiveParity.Verdict.NoOpinionConflict));
            }

            Assert.NotEmpty(conflicted);
            Assert.All(conflicted, row =>
            {
                Assert.Contains("CONFLICT", row.Detail);
                Assert.DoesNotContain("level ", row.Detail);
            });
        }
    }
}
