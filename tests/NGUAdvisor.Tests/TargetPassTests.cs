using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // PASS 3 — targets (constraint-layer-spec §7; 23 §0.3-0.4, §7; amendment 18). Kind routing
    // first (one consumer, three refusals-with-reason), then terminality — the two "softcap" rows
    // that MUST come out different — then track selection, the Wandoos refusal, the Evil NGU rule,
    // the silence ledger asserted entry by entry, and the ordering contract.
    public class TargetPassTests
    {
        private static readonly FeasibilityPass.Verdict Seated = FeasibilityPass.Verdict.Seat();

        private static TargetPass.TargetRow LevelRow(string system, int index, TargetPass.Track track,
            TargetPass.Terminality terminality, long value, string cite = "test")
            => new TargetPass.TargetRow
            {
                System = system,
                Index = index,
                Track = track,
                Kind = TargetPass.RowKind.Level,
                Terminality = terminality,
                ValueLow = value,
                ValueHigh = value,
                Cite = cite,
            };

        private static TargetPass.LaneState Lane(string system, int index, TargetPass.Track track,
            long level)
            => new TargetPass.LaneState
            { System = system, Index = index, ActiveTrack = track, LevelOnTrack = level };

        // ---- KIND routing (23 §0.3): four kinds, exactly one reaches this pass -------------------

        [Fact]
        public void Level_row_routes_terminal_writes()
        {
            var row = LevelRow(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, 401);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.WriteTarget, route.Disposition);
            Assert.Equal(401L, route.TargetToWrite);
            Assert.Null(route.Reason);
        }

        // A rate row is an allocation-SUFFICIENCY condition — Pass 2 capacity content (amendment 18
        // §1.2). Reaching Pass 3 is a caller error: refused WITH A REASON, never silently ignored.
        [Fact]
        public void Rate_row_is_refused_with_a_reason_naming_pass_2()
        {
            var row = LevelRow(TargetPass.SysNguEnergy, 0, TargetPass.Track.Evil,
                TargetPass.Terminality.Unspecified, 0);
            row.Kind = TargetPass.RowKind.Rate;

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Contains("Pass 2", route.Reason);
            Assert.Contains("caller error", route.Reason);
            Assert.Equal(0L, route.TargetToWrite);
        }

        [Fact]
        public void Time_row_is_refused_with_a_reason_naming_auto_profile()
        {
            var row = LevelRow(TargetPass.SysTmSpeed, 0, TargetPass.Track.Normal,
                TargetPass.Terminality.Unspecified, 0);
            row.Kind = TargetPass.RowKind.Time;

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Contains("auto-profile", route.Reason);
        }

        // A predicate is a target SELECTOR: computed upstream, then re-emitted as kind=level. The
        // raw predicate refuses; the computed result arrives as a plain level row and routes.
        [Fact]
        public void Predicate_row_is_refused_raw_and_arrives_as_a_level_after_upstream_compute()
        {
            var raw = LevelRow(TargetPass.SysNguEnergy, 7, TargetPass.Track.Normal,
                TargetPass.Terminality.Unspecified, 0);
            raw.Kind = TargetPass.RowKind.Predicate;

            var refused = TargetPass.Route(raw);
            Assert.Equal(TargetPass.Disposition.Refused, refused.Disposition);
            Assert.Contains("computed upstream", refused.Reason);

            // The same intent after the upstream solver ran: a level row, routed normally. The
            // upstream layer owns the terminality call; here it arrived Terminal.
            var computed = LevelRow(TargetPass.SysNguEnergy, 7, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, 12345,
                cite: "computed upstream from the GO >1.05x predicate");

            var route = TargetPass.Route(computed);
            Assert.Equal(TargetPass.Disposition.WriteTarget, route.Disposition);
            Assert.Equal(12345L, route.TargetToWrite);
        }

        // default(TargetRow) is Kind=Unspecified — fail closed, routes nowhere.
        [Fact]
        public void Default_row_is_refused_not_treated_as_a_level()
        {
            var route = TargetPass.Route(default(TargetPass.TargetRow));

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.NotNull(route.Reason);
        }

        [Fact]
        public void Default_route_is_an_unevaluated_refusal()
        {
            var route = default(TargetPass.RowRoute);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Equal("unevaluated", route.Reason);
            Assert.Equal(0L, route.TargetToWrite);
        }

        // ---- TERMINALITY (23 §0.4): terminal writes, precondition does NOT, AMBIGUOUS surfaces --

        [Fact]
        public void Precondition_never_writes_a_target()
        {
            var row = LevelRow(TargetPass.SysNguEnergy, 4, TargetPass.Track.Normal,
                TargetPass.Terminality.Precondition, 3000);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Precondition, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
            Assert.Contains("never written", route.Reason);
        }

        [Fact]
        public void Ambiguous_surfaces_as_operator_decision_never_guessed()
        {
            var row = LevelRow(TargetPass.SysNguEnergy, 8, TargetPass.Track.Evil,
                TargetPass.Terminality.Ambiguous, 1000);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.OperatorDecision, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
            Assert.Contains("AMBIGUOUS", route.Reason);
        }

        // An unfilled terminality is treated exactly like AMBIGUOUS: surfaced, never written.
        [Fact]
        public void Unspecified_terminality_surfaces_like_ambiguous()
        {
            var row = LevelRow(TargetPass.SysAt, 2, TargetPass.Track.Normal,
                TargetPass.Terminality.Unspecified, 5000);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.OperatorDecision, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
        }

        // A ranged terminal ("2-3k") is not a writable stopping level — the sole standing terminal
        // is a scalar. Surfaced, not collapsed to either endpoint.
        [Fact]
        public void Ranged_terminal_surfaces_instead_of_collapsing()
        {
            var row = LevelRow(TargetPass.SysAt, 0, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, 2000);
            row.ValueHigh = 3000;

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.OperatorDecision, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
        }

        // ---- SOFTCAP IS NOT ONE CONCEPT (23 §0.5) ------------------------------------------------
        // Respawn 401 ("don't invest further yet" — post-400 branch SATURATES, AllNGUController.cs:
        // 449-458) and Adventure a softcap 1000 ("keep going" — post-1000 branch is UNBOUNDED sqrt,
        // :568-572): the same word, OPPOSITE terminality, different dispositions.
        //
        // ⚠ ONLY ONE HALF IS STILL A ROW. [OPERATOR] removed the Respawn level row 2026-08-07 — 401
        // was "the 'best' for where the user is at at the time", not a property of the curve — and
        // ObjectiveTable dropped it at 08b4344 while this table did not, which is the drift this
        // commit closes. The SATURATION IS STILL REAL; what went is the instruction to stop there.
        // The surviving half is the one that was always the harder call: a softcap that means KEEP
        // GOING. The counterpart's ABSENCE is asserted here as well, so the pair cannot half-return.

        [Fact]
        public void The_adventure_a_softcap_is_a_precondition_and_respawn_has_no_reference_row()
        {
            var adventureA = TargetPass.GuideRows.Single(r =>
                r.System == TargetPass.SysNguEnergy && r.Index == 4 &&
                r.Track == TargetPass.Track.Normal);

            Assert.Equal(TargetPass.Terminality.Precondition, adventureA.Terminality);

            var adventureRoute = TargetPass.Route(adventureA);
            Assert.Equal(TargetPass.Disposition.Precondition, adventureRoute.Disposition);
            Assert.Equal(0L, adventureRoute.TargetToWrite);

            // The counterpart is gone entirely — not demoted to precondition, not gated. Absence.
            var respawn = TargetPass.GuideRows
                .Where(r => r.System == TargetPass.SysNguEnergy && r.Index == 2)
                .ToList();
            Assert.True(respawn.Count == 0,
                "a Respawn reference row is back — [OPERATOR] removed it 2026-08-07 as situational " +
                "guide advice, not a property of the curve:\n  " +
                string.Join("\n  ", respawn.Select(r => r.ValueLow + " " + r.Objective)));
        }

        // STILL EXACTLY ONE STANDING TERMINAL, and the cardinality is what this asserts — the row
        // moved (Respawn 401 out by ruling, AT Block 100,000 promoted from Precondition by ruling)
        // and the count did not. The 100LC's TM 59/10 are terminal but campaign-scoped: RouteLevel
        // refuses those on scope before terminality is read, so without this row nothing in the
        // fixture would reach WriteTarget at all. Matches ObjectiveTable's own
        // Block_AT_is_now_the_only_standing_terminal_and_the_rest_are_campaign_scoped.
        [Fact]
        public void Block_AT_is_the_sole_standing_terminal_in_the_reference_rows()
        {
            var standingTerminals = TargetPass.GuideRows
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal &&
                            r.CampaignScope == null)
                .ToArray();

            var only = Assert.Single(standingTerminals);
            Assert.Equal(TargetPass.SysAt, only.System);
            Assert.Equal(2, only.Index);
            Assert.Equal(TargetPass.Track.Evil, only.Track);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, only.ValueLow);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, only.ValueHigh);

            // It must actually reach the write path — Terminal alone is not enough, since a scope,
            // a gate, a missing track or a ranged value would each refuse it earlier.
            var route = TargetPass.Route(only);
            Assert.Equal(TargetPass.Disposition.WriteTarget, route.Disposition);
            Assert.Equal(100000L, route.TargetToWrite);
        }

        [Fact]
        public void Campaign_scoped_terminals_refuse_to_write_as_standing()
        {
            var tm59 = TargetPass.GuideRows.Single(r =>
                r.System == TargetPass.SysTmSpeed && r.CampaignScope != null);
            var tm10 = TargetPass.GuideRows.Single(r =>
                r.System == TargetPass.SysTmGoldMulti && r.CampaignScope != null);

            foreach (var row in new[] { tm59, tm10 })
            {
                var route = TargetPass.Route(row);
                Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
                Assert.Contains("campaign-scoped", route.Reason);
                Assert.Equal(0L, route.TargetToWrite);
            }
        }

        // TM speed 49: the guide names the number and says DON'T STOP — writing speedTarget = 49
        // would implement the number and invert the advice (23 §2.5). Pinned as a precondition.
        [Fact]
        public void Tm_49_is_a_precondition_not_a_stopping_level()
        {
            var tm49 = TargetPass.GuideRows.Single(r =>
                r.System == TargetPass.SysTmSpeed && r.ValueLow == 49);

            Assert.Equal(TargetPass.Terminality.Precondition, tm49.Terminality);
            Assert.Equal(TargetPass.Disposition.Precondition, TargetPass.Route(tm49).Disposition);
        }

        // Reference-row integrity: no row without a cite (23 §0.1), no wandoos row on any track
        // (23 §2.6), no Evil NGU terminal (amendment 18 §1), no unfilled terminality on a level row.
        [Fact]
        public void Reference_rows_hold_the_transcription_invariants()
        {
            foreach (var r in TargetPass.GuideRows)
            {
                Assert.False(string.IsNullOrEmpty(r.Cite));
                Assert.NotEqual(TargetPass.SysWandoos, r.System);
                if (r.Kind == TargetPass.RowKind.Level)
                    Assert.NotEqual(TargetPass.Terminality.Unspecified, r.Terminality);
                if (TargetPass.IsNguSystem(r.System) && r.Track == TargetPass.Track.Evil)
                    Assert.NotEqual(TargetPass.Terminality.Terminal, r.Terminality);
            }
        }

        // ---- TRACK (23 §0.1): three target fields per NGU, selection by the active track ---------

        // The mandated case: a Normal-track row does NOT satisfy an Evil-track lane — whatever the
        // level. The lane falls through to the silence ledger (amendment 18's evil entry), and its
        // satisfaction is NoClaim, never Satisfied.
        [Fact]
        public void Normal_track_row_does_not_satisfy_an_evil_track_lane()
        {
            var rows = new List<TargetPass.TargetRow>
            {
                LevelRow(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal,
                    TargetPass.Terminality.Terminal, 401),
            };
            var lane = Lane(TargetPass.SysNguEnergy, 2, TargetPass.Track.Evil, level: 999_999);

            var answer = TargetPass.Evaluate(lane, rows, Seated);

            Assert.NotEqual(TargetPass.Satisfaction.Satisfied, answer.Satisfaction);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
            Assert.Equal(0L, answer.TargetToWrite);
        }

        [Fact]
        public void Row_without_a_track_is_unusable()
        {
            var row = LevelRow(TargetPass.SysNguEnergy, 2, TargetPass.Track.Unspecified,
                TargetPass.Terminality.Terminal, 401);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Contains("unusable", route.Reason);
        }

        // TM is structurally track-neutral (one speedTarget, no per-track fields — 23 §2.5): its
        // rows speak for the lane on any active track.
        [Fact]
        public void Track_neutral_tm_row_matches_any_active_track()
        {
            var tm49 = TargetPass.GuideRows.Single(r =>
                r.System == TargetPass.SysTmSpeed && r.ValueLow == 49);
            var rows = new List<TargetPass.TargetRow> { tm49 };

            foreach (var track in new[]
                { TargetPass.Track.Normal, TargetPass.Track.Evil, TargetPass.Track.Sadistic })
            {
                var answer = TargetPass.Evaluate(
                    Lane(TargetPass.SysTmSpeed, 0, track, level: 10), rows, Seated);
                Assert.Equal(TargetPass.Disposition.Precondition, answer.Disposition);
            }
        }

        // The satisfaction comparator is the game's own: level >= target, equality satisfies
        // (reachedTarget, AllNGUController.cs:1316).
        [Theory]
        [InlineData(400L, TargetPass.Satisfaction.Unsatisfied)]
        [InlineData(401L, TargetPass.Satisfaction.Satisfied)]
        [InlineData(402L, TargetPass.Satisfaction.Satisfied)]
        public void Written_target_satisfaction_mirrors_the_game_comparator(long level,
            TargetPass.Satisfaction expected)
        {
            var rows = new List<TargetPass.TargetRow>
            {
                LevelRow(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal,
                    TargetPass.Terminality.Terminal, 401),
            };

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal, level), rows, Seated);

            Assert.Equal(TargetPass.Disposition.WriteTarget, answer.Disposition);
            Assert.Equal(401L, answer.TargetToWrite);
            Assert.Equal(expected, answer.Satisfaction);
        }

        // ---- WANDOOS (23 §2.6): Pass 3 refuses to produce a target, on every path ----------------

        [Fact]
        public void Wandoos_row_is_refused_whatever_it_claims_to_be()
        {
            var row = LevelRow(TargetPass.SysWandoos, 0, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, 1000);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Contains("DO NOT SYNTHESISE", route.Reason);
            Assert.Equal(0L, route.TargetToWrite);
        }

        [Fact]
        public void Wandoos_lane_is_refused_with_or_without_rows()
        {
            var withRow = TargetPass.Evaluate(
                Lane(TargetPass.SysWandoos, 0, TargetPass.Track.Normal, 5000),
                new List<TargetPass.TargetRow>
                {
                    LevelRow(TargetPass.SysWandoos, 0, TargetPass.Track.Normal,
                        TargetPass.Terminality.Terminal, 1000),
                },
                Seated);
            var withoutRow = TargetPass.Evaluate(
                Lane(TargetPass.SysWandoos, 0, TargetPass.Track.Normal, 5000), null, Seated);

            foreach (var answer in new[] { withRow, withoutRow })
            {
                Assert.Equal(TargetPass.Disposition.Refused, answer.Disposition);
                Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
                Assert.Equal(0L, answer.TargetToWrite);
                Assert.Contains("DO NOT SYNTHESISE", answer.Reason);
            }
        }

        // ---- EVIL NGUs (amendment 18): rate-only, both pools, all ids ----------------------------

        // The mandated case: an Evil NGU lane with no level row is NOT unsatisfied-and-fundable.
        // Pass 3's answer is a surfaced silence carrying amendment 18's reason, satisfaction NoClaim.
        [Fact]
        public void Evil_ngu_lane_with_no_level_row_is_not_unsatisfied_and_fundable()
        {
            foreach (var system in new[] { TargetPass.SysNguEnergy, TargetPass.SysNguMagic })
            {
                var answer = TargetPass.Evaluate(
                    Lane(system, 0, TargetPass.Track.Evil, level: 12345), null, Seated);

                Assert.NotEqual(TargetPass.Satisfaction.Unsatisfied, answer.Satisfaction);
                Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
                Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
                Assert.Equal(0L, answer.TargetToWrite);
                Assert.Contains("amendment 18", answer.Reason);
            }
        }

        // With the BB rate rows actually present (the shape the table really has), the lane is
        // still not unsatisfied — the rows are refused with reasons routing them to Pass 2, and the
        // refusals are surfaced, not swallowed.
        [Fact]
        public void Evil_ngu_lane_with_only_rate_rows_makes_no_funding_claim()
        {
            var bb = LevelRow(TargetPass.SysNguEnergy, 0, TargetPass.Track.Evil,
                TargetPass.Terminality.Unspecified, 0);
            bb.Kind = TargetPass.RowKind.Rate;
            var rows = new List<TargetPass.TargetRow> { bb };

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 0, TargetPass.Track.Evil, 0), rows, Seated);

            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(TargetPass.Disposition.Refused, answer.Disposition);
            var error = Assert.Single(answer.RowErrors);
            Assert.Contains("Pass 2", error);
        }

        // A terminal Evil NGU level row is an operator TRANSLATING the guide, which amendment 18 §1
        // forbids: there are no Evil NGU levels.
        [Fact]
        public void Terminal_evil_ngu_row_is_refused_per_amendment_18()
        {
            var row = LevelRow(TargetPass.SysNguMagic, 0, TargetPass.Track.Evil,
                TargetPass.Terminality.Terminal, 400);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Contains("amendment 18", route.Reason);
        }

        // The §1.4 residue — Evil-track PRECONDITIONS (CBlock prep, the ch.5 softcap-then rows) —
        // remains routable: surfaced as milestones, never written.
        [Fact]
        public void Evil_ngu_precondition_rows_surface_as_milestones()
        {
            var e7 = TargetPass.GuideRows.Single(r =>
                r.System == TargetPass.SysNguEnergy && r.Index == 7 &&
                r.Track == TargetPass.Track.Evil);
            var rows = new List<TargetPass.TargetRow> { e7 };

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 7, TargetPass.Track.Evil, level: 250), rows, Seated);

            Assert.Equal(TargetPass.Disposition.Precondition, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(0L, answer.TargetToWrite);
            Assert.False(answer.MilestonesAllMet);
            Assert.Equal(1000L, answer.NextMilestone);
        }

        // ---- the SILENCE LEDGER (23 §7): every silence surfaces, none defaults -------------------

        // Representative slot per ledger entry. For the catch-alls (null System / null Ids /
        // Unspecified Track) pick a concrete member.
        public static IEnumerable<object[]> LedgerEntries()
        {
            for (int i = 0; i < TargetPass.SilenceLedger.Length; i++)
                yield return new object[] { i };
        }

        [Theory]
        [MemberData(nameof(LedgerEntries))]
        public void Every_ledger_entry_surfaces_and_never_defaults(int entryIndex)
        {
            var entry = TargetPass.SilenceLedger[entryIndex];
            var system = entry.System ?? TargetPass.SysNguEnergy;
            var id = entry.Ids != null ? entry.Ids[0] : 0;
            var track = entry.Track != TargetPass.Track.Unspecified
                ? entry.Track
                : TargetPass.Track.Normal;

            TargetPass.SilenceSpec found;
            Assert.True(TargetPass.FindSilence(system, id, track, out found));
            Assert.False(string.IsNullOrEmpty(found.Reason));
            Assert.False(string.IsNullOrEmpty(found.Cite));

            var answer = TargetPass.Evaluate(Lane(system, id, track, level: 0), null, Seated);

            // Wandoos surfaces as the standing refusal rather than a mere silence; everything else
            // surfaces as Silent. In ALL cases: no satisfaction claim, no write, a reason — an
            // unfilled slot is never a default of 0, never long.MaxValue, never
            // unsatisfied-so-keep-funding.
            if (system == TargetPass.SysWandoos)
                Assert.Equal(TargetPass.Disposition.Refused, answer.Disposition);
            else
                Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(0L, answer.TargetToWrite);
            Assert.False(string.IsNullOrEmpty(answer.Reason));
        }

        // The two big ones lead the ledger (23 §7.1) — spot-check their recorded character.
        [Fact]
        public void Augment_silence_is_principled_a_chosen_augment_not_a_level()
        {
            TargetPass.SilenceSpec s;
            Assert.True(TargetPass.FindSilence(TargetPass.SysAugments, 0, TargetPass.Track.Normal,
                out s));
            Assert.Equal(TargetPass.SilenceClass.DifferentShape, s.Class);
            Assert.Contains("A CHOSEN AUGMENT, not a level", s.Reason);
        }

        [Fact]
        public void Sadistic_is_silent_in_every_system()
        {
            foreach (var system in new[]
            {
                TargetPass.SysAugments, TargetPass.SysNguEnergy, TargetPass.SysNguMagic,
                TargetPass.SysAt, TargetPass.SysTmSpeed, TargetPass.SysTmGoldMulti,
                TargetPass.SysWandoos,
            })
            {
                TargetPass.SilenceSpec s;
                Assert.True(TargetPass.FindSilence(system, 0, TargetPass.Track.Sadistic, out s));
                Assert.False(string.IsNullOrEmpty(s.Reason));
            }
        }

        // ngu-magic 3 (Number) — never named in any chapter, on any track: the specific reason wins
        // over both the amendment-18 evil entry and the Sadistic catch-all.
        [Theory]
        [InlineData(TargetPass.Track.Normal)]
        [InlineData(TargetPass.Track.Evil)]
        [InlineData(TargetPass.Track.Sadistic)]
        public void Number_is_never_named_on_any_track(TargetPass.Track track)
        {
            TargetPass.SilenceSpec s;
            Assert.True(TargetPass.FindSilence(TargetPass.SysNguMagic, 3, track, out s));
            Assert.Contains("never named", s.Reason);
        }

        // A slot in nobody's ledger still surfaces — fail closed, with the fallback reason.
        [Fact]
        public void Unledgered_slot_still_surfaces_as_silent()
        {
            TargetPass.SilenceSpec s;
            Assert.False(TargetPass.FindSilence(TargetPass.SysAt, 0, TargetPass.Track.Normal, out s));

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysAt, 0, TargetPass.Track.Normal, 0), null, Seated);

            Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Contains("no ledger entry", answer.Reason);
        }

        // ---- a silence is not a zero (23 §7; AllNGUController.cs:1302-1339) ----------------------

        // target == 0 is the game's UNSET sentinel (reads unmet, funds forever) and -1 its
        // never-fund marker (reads met). Neither is a stopping level this pass may emit — so a
        // silence can never be smuggled out as a write of 0.
        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        [InlineData(-5L)]
        public void Game_sentinels_are_not_writable_targets(long value)
        {
            var row = LevelRow(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, value);

            var route = TargetPass.Route(row);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
        }

        // The NGU hardcap bounds every writable NGU target (23 §7.4; AllNGUController.cs:85-88;
        // clamps NGUController.cs:60-63/:78-81/:107-110). AT hosts no such clamp, so the bound is
        // NGU-scoped.
        [Fact]
        public void Ngu_target_above_the_hardcap_is_refused_at_the_cap_is_writable()
        {
            var above = LevelRow(TargetPass.SysNguEnergy, 4, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, TargetPass.NguHardCap + 1);
            var at = LevelRow(TargetPass.SysNguEnergy, 4, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, TargetPass.NguHardCap);
            var atLaneAboveCap = LevelRow(TargetPass.SysAt, 0, TargetPass.Track.Normal,
                TargetPass.Terminality.Terminal, TargetPass.NguHardCap + 1);

            Assert.Equal(TargetPass.Disposition.Refused, TargetPass.Route(above).Disposition);
            Assert.Contains("hardcap", TargetPass.Route(above).Reason);
            Assert.Equal(TargetPass.Disposition.WriteTarget, TargetPass.Route(at).Disposition);
            Assert.Equal(TargetPass.Disposition.WriteTarget,
                TargetPass.Route(atLaneAboveCap).Disposition);
        }

        // ---- ORDERING (spec §2): the contract is asserted, not re-derived ------------------------

        [Fact]
        public void Unseated_lane_is_a_contract_violation()
        {
            var rows = new List<TargetPass.TargetRow>
            {
                LevelRow(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal,
                    TargetPass.Terminality.Terminal, 401),
            };

            var refused = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal, 500),
                rows, FeasibilityPass.Verdict.Refuse("gold stall: bar unstarted"));
            var unevaluated = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 2, TargetPass.Track.Normal, 500),
                rows, default(FeasibilityPass.Verdict));

            foreach (var answer in new[] { refused, unevaluated })
            {
                Assert.Equal(TargetPass.Disposition.Refused, answer.Disposition);
                Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
                Assert.Contains("contract violation", answer.Reason);
            }
            // The upstream refusal reason travels inside the surfaced message.
            Assert.Contains("gold stall", refused.Reason);
        }

        // The eager-IsValid posture: TargetMet-shaped code runs before Allocate and even when
        // Unlocked is false, so wholly-default inputs must produce a fail-closed answer, never a
        // throw (the BestAug._useUpgrades hazard class).
        [Fact]
        public void Default_inputs_fail_closed_without_throwing()
        {
            var answer = TargetPass.Evaluate(default(TargetPass.LaneState), null, Seated);

            Assert.Equal(TargetPass.Disposition.Silent, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(0L, answer.TargetToWrite);
            Assert.NotNull(answer.RowErrors);
        }

        // ---- lane-level composition --------------------------------------------------------------

        // The PAWG ladder (four precondition rungs on one id): the lane surfaces the LOWEST unmet
        // rung, and never a satisfaction claim.
        [Theory]
        [InlineData(0L, 500L, false)]
        [InlineData(600L, 5_000L, false)]
        [InlineData(200_000L, 5_000_000L, false)]
        [InlineData(6_000_000L, 0L, true)]
        public void Pawg_ladder_surfaces_the_next_unmet_rung(long level, long expectedNext,
            bool expectedAllMet)
        {
            var rungs = TargetPass.GuideRows
                .Where(r => r.System == TargetPass.SysNguEnergy && r.Index == 0 &&
                            r.Track == TargetPass.Track.Normal)
                .ToList();
            Assert.Equal(4, rungs.Count);

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 0, TargetPass.Track.Normal, level), rungs, Seated);

            Assert.Equal(TargetPass.Disposition.Precondition, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(expectedNext, answer.NextMilestone);
            Assert.Equal(expectedAllMet, answer.MilestonesAllMet);
        }

        // A misrouted row beside a valid one is refused WITH ITS REASON and the valid row still
        // speaks for the lane — refusals are surfaced, never swallowed, never lane-fatal.
        [Fact]
        public void Misrouted_row_surfaces_beside_a_consumable_one()
        {
            var rate = LevelRow(TargetPass.SysNguEnergy, 6, TargetPass.Track.Normal,
                TargetPass.Terminality.Unspecified, 0);
            rate.Kind = TargetPass.RowKind.Rate;
            var softcap = LevelRow(TargetPass.SysNguEnergy, 6, TargetPass.Track.Normal,
                TargetPass.Terminality.Precondition, 1000);
            var rows = new List<TargetPass.TargetRow> { rate, softcap };

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 6, TargetPass.Track.Normal, 100), rows, Seated);

            Assert.Equal(TargetPass.Disposition.Precondition, answer.Disposition);
            var error = Assert.Single(answer.RowErrors);
            Assert.Contains("caller error", error);
        }

        // Two standing terminals on one track cannot both be the stopping level — surfaced, not
        // coin-tossed.
        [Fact]
        public void Conflicting_terminals_are_refused()
        {
            var rows = new List<TargetPass.TargetRow>
            {
                LevelRow(TargetPass.SysAt, 2, TargetPass.Track.Normal,
                    TargetPass.Terminality.Terminal, 5000),
                LevelRow(TargetPass.SysAt, 2, TargetPass.Track.Normal,
                    TargetPass.Terminality.Terminal, 100000),
            };

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysAt, 2, TargetPass.Track.Normal, 0), rows, Seated);

            Assert.Equal(TargetPass.Disposition.Refused, answer.Disposition);
            Assert.Contains("two terminal rows", answer.Reason);
            Assert.Equal(0L, answer.TargetToWrite);
        }

        // An unresolved AMBIGUOUS row on the lane's own track blocks a write: the operator has not
        // finished deciding this lane.
        [Fact]
        public void Ambiguous_row_blocks_a_write_on_the_same_lane()
        {
            var rows = new List<TargetPass.TargetRow>
            {
                LevelRow(TargetPass.SysNguEnergy, 8, TargetPass.Track.Evil,
                    TargetPass.Terminality.Ambiguous, 1000),
            };

            var answer = TargetPass.Evaluate(
                Lane(TargetPass.SysNguEnergy, 8, TargetPass.Track.Evil, 2000), rows, Seated);

            Assert.Equal(TargetPass.Disposition.OperatorDecision, answer.Disposition);
            Assert.Equal(TargetPass.Satisfaction.NoClaim, answer.Satisfaction);
            Assert.Equal(0L, answer.TargetToWrite);
        }
    }
}
