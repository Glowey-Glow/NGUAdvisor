using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE COMPARISON THAT DID NOT EXIST, AND THE DEFECT IT WOULD HAVE CAUGHT.
    //
    // There are two tables of guide rows. ObjectiveTable.LaneRows is the full 23 §2 transcription
    // and is the AUTHORITY. TargetPass.GuideRows is a deliberately minimal SUBSET, kept because it
    // is the only populated TargetRow[] in the tree and is therefore what exercises Pass 3's router
    // — RowsFor / Evaluate / Route — on shipped data with no ToTargetRow translation in between.
    //
    // ⚠ NOTHING COMPARED THEM, AND THEY DIVERGED ON THE SAME DAY TWO OPERATOR RULINGS LANDED:
    //   08b4344          removed the Respawn 401 row from LaneRows. GuideRows kept shipping it,
    //                    still described as "THE sole standing terminal in the entire guide".
    //   d614347/3e9816d  made AT Block a hard TERMINAL at ObjectiveTable.AtBlockHardCapLevel.
    //                    GuideRows kept it as a Precondition on both tracks.
    // Both were silent. Every test on either side passed, because every test read one table only.
    //
    // ⚠ WHAT THIS FILE CHECKS, AND WHAT IT DELIBERATELY DOES NOT.
    //   IT CHECKS   every GuideRow is matched FIELD-FOR-FIELD by a materialised LaneRow (slot,
    //               track, kind, terminality, both value ends, campaign scope, lift gate);
    //               the two tables' TERMINAL SETS are equal in both directions;
    //               the row count, so a silent deletion here is not mistaken for agreement.
    //   IT DOES NOT check that GuideRows carries every LaneRow. Being a SUBSET is the point —
    //               LaneRows has four id-4 level rows where this table keeps one. A new NON-terminal
    //               LaneRow in a slot GuideRows already covers is therefore invisible here, by
    //               design. A new TERMINAL anywhere is not: the set equality catches it.
    //   IT DOES NOT compare prose. Objective / Cite / ValueText / GroupNote differ between the two
    //               tables on purpose, and comparing them would fire on editorial edits that move
    //               no semantics. ⚠ THE COROLLARY IS THAT COMMENTS CAN STILL GO STALE SILENTLY —
    //               which is exactly how the live routing message came to claim "the sole standing
    //               terminal (Respawn 401) is a scalar" long after that row was gone. That one
    //               string is now pinned below; arbitrary prose is not, and cannot be.
    //   IT DOES NOT check Chapter. GuideRows has no such field (37 §S5 B6), so a row moving
    //               chapters in LaneRows is not observable from here.
    //   IT CANNOT   catch an edit made to BOTH tables in the same wrong way. It proves agreement,
    //               never correctness; correctness against the rulings is OperatorRuledRowsTests.
    public class GuideRowsParityTests
    {
        // ---- the comparison key --------------------------------------------------------------

        // A level row's identity for comparison: the slot it speaks for, and everything it says
        // about that slot that can change what Pass 3 does with it. Rendered as a string so a
        // failure names the row rather than a struct hash.
        private static string Key(TargetPass.TargetRow r)
        {
            return string.Concat(
                r.System, " id", r.Index.ToString(CultureInfo.InvariantCulture),
                " track=", r.TrackNeutral ? "track-neutral" : r.Track.ToString(),
                " kind=", r.Kind.ToString(),
                " terminality=", r.Terminality.ToString(),
                " value=", r.ValueLow.ToString(CultureInfo.InvariantCulture),
                "..", r.ValueHigh.ToString(CultureInfo.InvariantCulture),
                " campaign=", r.CampaignScope ?? "-",
                " gate=", r.LiftGate ?? "-");
        }

        // Every LEVEL row of the objective table, materialised for each id it covers — the shape
        // Pass 3 consumes, produced by the table's own ToTargetRow so no second translation exists
        // here to disagree with the real one. An Ids == null row speaks for the whole system, so it
        // is expanded over that system's id count.
        private static IEnumerable<TargetPass.TargetRow> ObjectiveLevelRows()
        {
            foreach (var lane in ObjectiveTable.LaneRows)
            {
                if (lane.Kind != TargetPass.RowKind.Level)
                    continue;

                if (lane.Ids != null)
                {
                    foreach (var id in lane.Ids)
                        yield return lane.ToTargetRow(id);
                }
                else
                {
                    var count = ObjectiveReader.IdCount(lane.System);
                    for (int id = 0; id < count; id++)
                        yield return lane.ToTargetRow(id);
                }
            }
        }

        // ---- A: every reference row is backed by the authority ---------------------------------

        // THE ASSERTION THAT WOULD HAVE CAUGHT BOTH DRIFTS. A Respawn row left behind here has no
        // LaneRow to match (removed). An AT Block row still filed Precondition here has none either
        // (the authority says Terminal). Neither needs to be anticipated — any field disagreement
        // leaves an unmatched key.
        [Fact]
        public void Every_reference_row_is_matched_field_for_field_by_an_objective_table_row()
        {
            var authority = new HashSet<string>(ObjectiveLevelRows().Select(Key), StringComparer.Ordinal);

            var unmatched = TargetPass.GuideRows
                .Select(Key)
                .Where(k => !authority.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            Assert.True(unmatched.Count == 0,
                "TargetPass.GuideRows carries " + unmatched.Count + " row(s) that " +
                "ObjectiveTable.LaneRows does not back. LaneRows is the authority: either the row " +
                "was removed there and left here, or a field (terminality, value, track, scope, " +
                "gate) was ruled there and not carried over.\n  " +
                string.Join("\n  ", unmatched));
        }

        // Every GuideRow must also be a LEVEL row. The reference subset exists to pin Pass 3, and
        // Pass 3 consumes exactly one kind (23 §0.3) — a rate/time/predicate row here would be a
        // caller error shipped as data.
        [Fact]
        public void Every_reference_row_is_a_level_row_with_a_cite()
        {
            foreach (var r in TargetPass.GuideRows)
            {
                Assert.Equal(TargetPass.RowKind.Level, r.Kind);
                Assert.False(string.IsNullOrEmpty(r.Cite));
            }
        }

        // A CENSUS, so a row DELETED from GuideRows cannot pass as agreement — the check above is
        // one-directional and an empty table would satisfy it vacuously. 15 -> 14: the Respawn row
        // went, nothing else moved. ⚠ IF THIS MOVES, UPDATE IT TO THE EXACT NEW FIGURE; do not
        // widen it to a range.
        [Fact]
        public void The_reference_subset_is_fourteen_rows()
        {
            Assert.Equal(14, TargetPass.GuideRows.Length);
        }

        // ---- B: the terminal sets are equal, in both directions --------------------------------

        // The one direction A cannot see. A NEW terminal added to LaneRows in a slot this subset
        // does not carry would leave every GuideRow still matched, and "exactly one standing
        // terminal" would quietly become a claim about the fixture instead of about the system.
        // Terminals are the scarce, load-bearing rows — the only ones RouteLevel can write — so
        // they are compared as SETS rather than as a subset.
        private static List<string> TerminalKeys(IEnumerable<TargetPass.TargetRow> rows)
        {
            return rows
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal)
                .Select(Key)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }

        [Fact]
        public void The_two_tables_carry_exactly_the_same_terminal_rows()
        {
            var reference = TerminalKeys(TargetPass.GuideRows);
            var authority = TerminalKeys(ObjectiveLevelRows());

            var missingHere = authority.Except(reference, StringComparer.Ordinal).ToList();
            var extraHere = reference.Except(authority, StringComparer.Ordinal).ToList();

            Assert.True(missingHere.Count == 0 && extraHere.Count == 0,
                "the two tables disagree about which rows are TERMINAL — the only rows RouteLevel " +
                "can write.\n  in ObjectiveTable but not in GuideRows:\n    " +
                (missingHere.Count == 0 ? "(none)" : string.Join("\n    ", missingHere)) +
                "\n  in GuideRows but not in ObjectiveTable:\n    " +
                (extraHere.Count == 0 ? "(none)" : string.Join("\n    ", extraHere)));
        }

        // And the shape claim itself, asserted on BOTH tables in one place so it cannot hold on one
        // and not the other: exactly one STANDING terminal, and it is AT Block at the shared const.
        // ObjectiveLayerTests and TargetPassTests each assert their own half; this is the bridge.
        [Fact]
        public void Exactly_one_standing_terminal_exists_and_both_tables_name_the_same_row()
        {
            var referenceStanding = TargetPass.GuideRows
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal &&
                            r.CampaignScope == null)
                .ToList();
            var authorityStanding = ObjectiveLevelRows()
                .Where(r => r.Terminality == TargetPass.Terminality.Terminal &&
                            r.CampaignScope == null)
                .ToList();

            var here = Assert.Single(referenceStanding);
            var there = Assert.Single(authorityStanding);

            Assert.Equal(Key(there), Key(here));
            Assert.Equal(TargetPass.SysAt, here.System);
            Assert.Equal(2, here.Index);
            Assert.Equal(TargetPass.Track.Evil, here.Track);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, here.ValueLow);
            Assert.Equal(ObjectiveTable.AtBlockHardCapLevel, here.ValueHigh);
        }

        // ---- C: the removed number cannot come back ---------------------------------------------

        // The mirror of OperatorRuledRowsTests.The_number_401_is_not_a_level_target_anywhere, on the
        // other table. The failure mode is a later pass re-adding it from 23 §0.4 — which still
        // transcribes it — under some other id, track or chapter.
        [Fact]
        public void The_number_401_is_not_a_level_in_the_reference_rows_either()
        {
            var fourOhOne = TargetPass.GuideRows
                .Where(r => r.ValueLow == 401 || r.ValueHigh == 401)
                .ToList();

            Assert.True(fourOhOne.Count == 0,
                "401 is a reference level again — [OPERATOR] removed it 2026-08-07:\n  " +
                string.Join("\n  ", fourOhOne.Select(r => r.System + " id" + r.Index + " " + r.Objective)));
        }

        // ---- D: the value is the shared constant, not a repeated literal ------------------------

        // 3e9816d gave the hard cap ONE home so the table row and the live writer could not drift.
        // The reference row is the third reader, and it must read the const rather than restate the
        // number — a literal here would be a fourth place for 100,000 to live and go stale in.
        [Fact]
        public void The_reference_row_reads_the_hard_cap_constant_rather_than_repeating_it()
        {
            var src = CodeOnly(Source("TargetPass.cs"));

            Assert.Contains("ValueLow = ObjectiveTable.AtBlockHardCapLevel", src);
            Assert.Contains("ValueHigh = ObjectiveTable.AtBlockHardCapLevel", src);
            Assert.DoesNotContain("ValueLow = 100000", src);
            Assert.DoesNotContain("ValueHigh = 100000", src);
        }

        // ---- E: the LIVE routing message no longer asserts a fact about the table ---------------

        // ⚠ THIS IS THE HALF OF THE DRIFT NO TABLE TEST COULD REACH. RouteLevel's ranged-terminal
        // refusal used to justify itself with "the sole standing terminal (Respawn 401) is a
        // scalar" — a production string, reachable by an operator, asserting a fact about the table
        // from inside the router. It went false the day the row was removed and nothing noticed,
        // because no test reads production prose. Why a range cannot be written is a property of
        // RANGES; the message says only that now, and this pins that it stays that way.
        [Fact]
        public void The_ranged_terminal_refusal_argues_from_the_range_and_names_no_row()
        {
            var ranged = new TargetPass.TargetRow
            {
                System = TargetPass.SysNguEnergy,
                Index = 4,
                Track = TargetPass.Track.Normal,
                Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = 2000,
                ValueHigh = 3000,
                Cite = "test",
            };

            var route = TargetPass.Route(ranged);

            Assert.Equal(TargetPass.Disposition.OperatorDecision, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);

            // Still says WHAT was refused and WHY, in the terms the operator has to act on.
            Assert.Contains("2000-3000", route.Reason);
            Assert.Contains("range", route.Reason);
            Assert.Contains("operator resolution", route.Reason);

            // ...and grounds it in nothing that can be removed by a ruling.
            Assert.DoesNotContain("401", route.Reason);
            Assert.DoesNotContain("Respawn", route.Reason);
            Assert.DoesNotContain("sole standing terminal", route.Reason);
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = null)
        {
            var dir = System.IO.Path.GetDirectoryName(here);
            while (dir != null &&
                   !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "NGUAdvisor", "Managers")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir;
        }

        private static string Source(string name)
        {
            var path = System.IO.Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", name);
            Assert.True(System.IO.File.Exists(path),
                $"source not found, so nothing was measured: {path}");
            return System.IO.File.ReadAllText(path);
        }

        private static string CodeOnly(string src)
            => string.Join("\n", src.Split('\n')
                .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i < 0 ? l : l.Substring(0, i); }));
    }
}
