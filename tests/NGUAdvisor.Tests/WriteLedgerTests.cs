using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The ledger's promise is narrower than "every write", and the value of a narrow promise is
    // entirely in it being KEPT. These pin the two things that make it keepable: the declared scope is
    // data (so the UI's coverage claim is derived, never typed), and a field is one row rather than one
    // row per tick (so a re-asserting writer cannot bury the one entry that matters).
    [Collection(TestCollections.WriteLedgerState)]
    public class WriteLedgerTests
    {
        public WriteLedgerTests() => WriteLedger.Reset();

        [Fact]
        public void Every_declared_writer_is_unique_and_fully_described()
        {
            var r = WriteLedger.Registry;
            Assert.NotEmpty(r);
            Assert.Equal(r.Length, r.Select(x => x.Id).Distinct().Count());
            Assert.All(r, w =>
            {
                Assert.False(string.IsNullOrWhiteSpace(w.Id));
                Assert.False(string.IsNullOrWhiteSpace(w.System));
                Assert.False(string.IsNullOrWhiteSpace(w.Field));
                Assert.False(string.IsNullOrWhiteSpace(w.Rule));
                Assert.False(string.IsNullOrWhiteSpace(w.Authority));
                Assert.NotNull(w.AlsoWrittenBy);
            });
        }

        // The row's headline is the IN-GAME name, and it has to actually be one. Operator feedback
        // 2026-08-11: a screen reporting what the advisor did to your save must name things the way the
        // save does — "Advanced Training · Block target", not "advancedTraining.levelTarget[2]".
        [Fact]
        public void Every_writer_has_an_in_game_name()
            => Assert.All(WriteLedger.Registry, w => Assert.False(string.IsNullOrWhiteSpace(w.Game), w.Id + " has no Game name"));

        // A cheap guard against someone pasting the code path in. Real in-game names do not carry
        // array subscripts, member dots or camelCase run-ons; the Field column is where those belong.
        [Fact]
        public void The_in_game_name_is_not_a_code_identifier()
        {
            foreach (var w in WriteLedger.Registry)
            {
                Assert.False(w.Game.Contains("[") || w.Game.Contains("]"),
                    w.Id + " game name looks like a field subscript: " + w.Game);
                Assert.False(w.Game == w.Field, w.Id + " game name is just the field path");
                Assert.False(w.Game.Contains("settings.") || w.Game.Contains("advancedTraining.")
                          || w.Game.Contains("itemList.") || w.Game.Contains("adventure."),
                    w.Id + " game name carries a code path: " + w.Game);
                Assert.True(w.Game.Contains(" "), w.Id + " game name is not prose: " + w.Game);
            }
        }

        // The id is a key, not a label. Nothing that reaches a person should ever be built from it.
        [Fact]
        public void Ids_are_keys_and_never_double_as_display_text()
            => Assert.All(WriteLedger.Registry, w => Assert.NotEqual(w.Id, w.Game));

        // The screen says "N of N instrumented". N must come from the registry, never from a literal,
        // or the coverage claim drifts from the coverage the moment a writer is added.
        [Fact]
        public void Declared_count_is_derived_from_the_registry()
            => Assert.Equal(WriteLedger.Registry.Length, WriteLedger.DeclaredCount);

        // Contested is symmetric by construction: if A names B, B must name A. An asymmetric pair would
        // show one writer as contested and its twin as active, which is worse than showing neither.
        [Fact]
        public void Contested_writers_name_each_other_both_ways()
        {
            foreach (var w in WriteLedger.Registry)
                foreach (var otherId in w.AlsoWrittenBy)
                {
                    var other = WriteLedger.Spec(otherId);
                    Assert.True(other != null, w.Id + " names unknown writer " + otherId);
                    Assert.Contains(w.Id, other.AlsoWrittenBy);
                }
        }

        [Fact]
        public void Writers_that_share_a_field_are_all_marked_contested()
        {
            foreach (var grp in WriteLedger.Registry.GroupBy(x => x.System + "|" + x.Field).Where(g => g.Count() > 1))
                Assert.All(grp, w => Assert.NotEmpty(w.AlsoWrittenBy));
        }

        [Fact]
        public void An_undeclared_writer_is_dropped_rather_than_admitted()
        {
            WriteLedger.Record("not.a.real.writer", "9", "why", "SEG");
            Assert.Empty(WriteLedger.Snapshot());
        }

        // THE READABILITY RULE. LevelPlanner re-asserts the Block floor on every tick the auto profile
        // runs; at one row per assignment that single field would be the entire ledger within a minute.
        [Fact]
        public void Re_asserting_the_same_value_does_not_add_a_row()
        {
            for (int i = 0; i < 50; i++)
                WriteLedger.Record("at.block", "100,000", "Block hard cap", "NGU MARATHON");

            var rows = WriteLedger.Snapshot();
            Assert.Single(rows);
            Assert.Equal("100,000", rows[0].Value);
        }

        [Fact]
        public void A_new_value_replaces_the_row_and_restarts_its_clock()
        {
            WriteLedger.Record("at.block", "5,000", "old derivation", "NGU MARATHON");
            var first = WriteLedger.Snapshot().Single().At;
            WriteLedger.Record("at.block", "100,000", "ruled constant", "NGU MARATHON");

            var rows = WriteLedger.Snapshot();
            Assert.Single(rows);
            Assert.Equal("100,000", rows[0].Value);
            Assert.True(rows[0].At >= first);
        }

        [Fact]
        public void A_contested_field_records_as_contested_not_active()
        {
            WriteLedger.Record("ngu.track.planner", "Normal", "ch.5 Evil tail", "AUGMENTATION");
            Assert.Equal(WriteState.Contested, WriteLedger.Snapshot().Single().State);
        }

        [Fact]
        public void An_uncontested_field_records_as_active()
        {
            WriteLedger.Record("diggers.active", "3, 1, 2", "value ranked", "AUGMENTATION");
            Assert.Equal(WriteState.Active, WriteLedger.Snapshot().Single().State);
        }

        // The Wandoos AT case: the value still stands, but the segment that justified it has ended.
        // Nothing else in the product can express this, which is why the ledger exists.
        [Fact]
        public void Stale_means_the_value_holds_but_its_reason_has_passed()
        {
            WriteLedger.Record("at.wandoos.reclaim", "2,847,391", "1% dump cost", "AUGMENTATION");
            WriteLedger.MarkStale("at.wandoos.reclaim", "AUGMENTATION ended and the write was never withdrawn");

            var e = WriteLedger.Snapshot().Single();
            Assert.Equal(WriteState.Stale, e.State);
            Assert.Contains("never withdrawn", e.Why);
            Assert.Equal("2,847,391", e.Value);   // stale is not cleared — the number is still in the save
        }

        [Fact]
        public void Reverted_outranks_stale_and_is_not_downgraded()
        {
            WriteLedger.Record("gear.equipped", "Gold · 10 items", "gold snipe", "AUGMENTATION");
            WriteLedger.MarkReverted("gear.equipped");
            WriteLedger.MarkStale("gear.equipped", "should not apply");

            var e = WriteLedger.Snapshot().Single();
            Assert.Equal(WriteState.Reverted, e.State);
            Assert.DoesNotContain("should not apply", e.Why);
        }

        // A field the advisor withdrew and then set again is a genuinely new write, not a resurrection
        // of the reverted row.
        [Fact]
        public void Writing_again_after_a_revert_starts_a_fresh_active_row()
        {
            WriteLedger.Record("gear.equipped", "Gold", "snipe", "AUGMENTATION");
            WriteLedger.MarkReverted("gear.equipped");
            WriteLedger.Record("gear.equipped", "NGUs", "objective change", "NGU MARATHON");

            var e = WriteLedger.Snapshot().Single();
            Assert.Equal("NGUs", e.Value);
            Assert.Equal(WriteState.Active, e.State);
        }

        [Fact]
        public void Counts_are_reported_per_state()
        {
            WriteLedger.Record("at.block", "100,000", "floor", "NGU MARATHON");
            WriteLedger.Record("diggers.active", "3, 1", "ranked", "NGU MARATHON");
            WriteLedger.Record("ngu.track.planner", "Normal", "tail", "NGU MARATHON");
            WriteLedger.MarkStale("diggers.active", "window closed");

            Assert.Equal(1, WriteLedger.CountIn(WriteState.Active));
            Assert.Equal(1, WriteLedger.CountIn(WriteState.Stale));
            Assert.Equal(1, WriteLedger.CountIn(WriteState.Contested));
            Assert.Equal(0, WriteLedger.CountIn(WriteState.Reverted));
        }

        [Fact]
        public void Reset_clears_the_run()
        {
            WriteLedger.Record("at.block", "100,000", "floor", "NGU MARATHON");
            WriteLedger.Reset();
            Assert.Empty(WriteLedger.Snapshot());
        }

        // THE CLAIM THE SCREEN MAKES, ENFORCED. The Ledger prints "N of 18 instrumented · M pending"
        // straight from these flags, so the numbers cannot drift from reality by being typed. What they
        // COULD do is drift by a writer being marked live and never wired — which is precisely the
        // silent hole this whole feature exists to end. So: read the source, and prove it.
        [Fact]
        public void Every_writer_marked_live_actually_has_a_Record_call_in_the_source()
        {
            var root = RepoRoot();
            var sources = System.IO.Directory
                .GetFiles(System.IO.Path.Combine(root, "NGUAdvisor"), "*.cs", System.IO.SearchOption.AllDirectories)
                .Where(p => p.IndexOf("\\obj\\", System.StringComparison.OrdinalIgnoreCase) < 0
                         && p.IndexOf("\\bin\\", System.StringComparison.OrdinalIgnoreCase) < 0)
                .Select(System.IO.File.ReadAllText)
                .ToArray();

            var missing = WriteLedger.Registry
                .Where(w => !w.Pending)
                .Where(w => !sources.Any(s => s.Contains("WriteLedger.Record(\"" + w.Id + "\"")))
                .Select(w => w.Id)
                .ToArray();

            Assert.True(missing.Length == 0,
                "declared live but never recorded: " + string.Join(", ", missing));
        }

        // The inverse: a writer that IS wired must not still be flagged pending, or the screen
        // under-reports its own coverage and the operator trusts it less than they should.
        [Fact]
        public void No_pending_writer_is_already_wired()
        {
            var root = RepoRoot();
            var sources = System.IO.Directory
                .GetFiles(System.IO.Path.Combine(root, "NGUAdvisor"), "*.cs", System.IO.SearchOption.AllDirectories)
                .Where(p => p.IndexOf("\\obj\\", System.StringComparison.OrdinalIgnoreCase) < 0
                         && p.IndexOf("\\bin\\", System.StringComparison.OrdinalIgnoreCase) < 0)
                .Select(System.IO.File.ReadAllText)
                .ToArray();

            var wired = WriteLedger.Registry
                .Where(w => w.Pending)
                .Where(w => sources.Any(s => s.Contains("WriteLedger.Record(\"" + w.Id + "\"")))
                .Select(w => w.Id)
                .ToArray();

            Assert.True(wired.Length == 0,
                "wired but still marked Pending — clear the flag: " + string.Join(", ", wired));
        }

        [Fact]
        public void Live_and_pending_partition_the_registry()
            => Assert.Equal(WriteLedger.DeclaredCount,
                            WriteLedger.LiveCount + WriteLedger.Registry.Count(w => w.Pending));

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = null)
        {
            var dir = System.IO.Path.GetDirectoryName(here);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "NGUAdvisor", "Managers")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir;
        }

        [Theory]
        [InlineData(WriteState.Active, "active")]
        [InlineData(WriteState.Stale, "stale")]
        [InlineData(WriteState.Reverted, "reverted")]
        [InlineData(WriteState.Contested, "contested")]
        public void State_names_are_stable_wire_values(WriteState s, string expected)
            => Assert.Equal(expected, WriteLedger.StateName(s));
    }
}
