using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The bestiary id table that replaced the difficulty selector as the source of "which versions have
    // I beaten". These are TRANSCRIPTION tests, not derivation tests, and that distinction is the point:
    // the id bases are irregular (339 follows 337, 365 follows 347), so nothing here may be computed from
    // a pattern. Every number below is pinned to the [DECOMP] addKills call site it came from.
    public class TitanBestiaryIdTests
    {
        // [DECOMP] AdventureController.cs — the autokill branches, one addKills per version.
        //   T6  :958 =312   :944 =313   :927 =314   :909 =315
        //   T7  :1021=334   :1008=335   :992 =336   :975 =337
        //   T8  :1083=339   :1070=340   :1054=341   :1037=342
        //   T9  :1145=344   :1132=345   :1116=346   :1099=347
        //   T10 :1207=365   :1194=366   :1178=367   :1161=368
        //   T11 :1269=369   :1256=370   :1240=371   :1223=372
        //   T12       =373        =374        =375   :1285=376
        [Theory]
        [InlineData(5, 1, 312)] [InlineData(5, 4, 315)]
        [InlineData(6, 1, 334)] [InlineData(6, 2, 335)] [InlineData(6, 3, 336)] [InlineData(6, 4, 337)]
        [InlineData(7, 1, 339)] [InlineData(7, 4, 342)]
        [InlineData(8, 1, 344)] [InlineData(8, 4, 347)]
        [InlineData(9, 1, 365)] [InlineData(9, 4, 368)]
        [InlineData(10, 1, 369)] [InlineData(10, 4, 372)]
        [InlineData(11, 1, 373)] [InlineData(11, 4, 376)]
        public void Version_ids_match_the_decompiled_addKills_sites(int titan, int version, int expected)
            => Assert.Equal(expected, TitanTables.BestiaryId(titan, version));

        // The gaps are real. If someone ever "simplifies" this to base + 5*index, this fails.
        [Fact]
        public void The_bases_are_not_evenly_spaced_and_must_not_be_derived()
        {
            Assert.Equal(334, TitanTables.BestiaryId(6, 1));
            Assert.Equal(339, TitanTables.BestiaryId(7, 1));   // +5 after T7's four ids end at 337
            Assert.Equal(344, TitanTables.BestiaryId(8, 1));   // +5
            Assert.Equal(365, TitanTables.BestiaryId(9, 1));   // +21 — the irregular one
            Assert.Equal(369, TitanTables.BestiaryId(10, 1));  // +4
        }

        // -1 is "no record", and callers are required to treat it differently from zero. A titan with no
        // per-version entry must never be reported as "never killed" — that is the exact failure the
        // selector proxy produced.
        [Theory]
        [InlineData(0)] [InlineData(4)] [InlineData(12)] [InlineData(13)]
        public void Unversioned_titans_have_no_record(int titan)
        {
            Assert.False(TitanTables.HasVersionKillRecord(titan));
            Assert.Equal(-1, TitanTables.BestiaryId(titan, 1));
        }

        [Theory]
        [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
        [InlineData(9)] [InlineData(10)] [InlineData(11)]
        public void Versioned_titans_have_a_record(int titan)
            => Assert.True(TitanTables.HasVersionKillRecord(titan));

        [Theory]
        [InlineData(6, 0)] [InlineData(6, 5)] [InlineData(6, -1)]
        [InlineData(-1, 1)] [InlineData(99, 1)]
        public void Out_of_range_asks_return_no_record(int titan, int version)
            => Assert.Equal(-1, TitanTables.BestiaryId(titan, version));

        // The table is indexed by titan index and must stay aligned with the 14-titan world the rest of
        // the code assumes (TitanTables.Abbrev, the bool[14] target arrays).
        [Fact]
        public void The_table_covers_every_titan_index()
            => Assert.Equal(14, TitanTables.BestiaryV1Id.Length);

        // The old conversion is kept only as a fallback for the indices with no record. Pinning it stops
        // anyone "cleaning up" the deprecated helper while those callers still need it.
        [Theory]
        [InlineData(1, 0)] [InlineData(2, 1)] [InlineData(4, 3)] [InlineData(0, 0)]
        public void The_deprecated_selector_conversion_still_behaves(int humanVersion, int expected)
            => Assert.Equal(expected, TitanTables.VersionsDefeated(humanVersion));

        // ---- chase hysteresis ---------------------------------------------------------------------
        // The band exists because a bare threshold alternated across the line every 60s pass, and each
        // flip to "not ready" dropped adventure routing mid-spawn and gave the game a free lower-version
        // kill. Observed v2 -> v1 -> v2 inside two minutes.

        [Fact]
        public void A_cold_start_needs_the_full_bar()
        {
            Assert.False(TitanTables.ChaseReady(false, 0.99));
            Assert.True(TitanTables.ChaseReady(false, 1.00));
        }

        [Fact]
        public void Once_committed_it_holds_through_the_band()
        {
            Assert.True(TitanTables.ChaseReady(true, 0.95));   // would have parked before
            Assert.True(TitanTables.ChaseReady(true, 0.90));   // the floor itself still holds
            Assert.False(TitanTables.ChaseReady(true, 0.89));  // and below it, abandon
        }

        // The actual defect: a value hovering on the commit line must not alternate.
        [Fact]
        public void A_value_jittering_around_the_commit_line_does_not_flap()
        {
            bool chasing = TitanTables.ChaseReady(false, 1.01);
            Assert.True(chasing);
            foreach (var r in new[] { 0.995, 1.004, 0.97, 1.02, 0.93, 0.99 })
            {
                chasing = TitanTables.ChaseReady(chasing, r);
                Assert.True(chasing, "jitter at " + r + " abandoned a committed chase");
            }
        }

        // The asymmetry is the design, not an accident: starting a doomed fight is cheap, abandoning a
        // winnable one mid-window costs the whole spawn cycle.
        [Fact]
        public void The_band_is_asymmetric_and_abandon_is_the_lower_edge()
        {
            Assert.True(TitanTables.ChaseAbandonRatio < TitanTables.ChaseCommitRatio);
            Assert.True(TitanTables.ChaseReady(true,  TitanTables.ChaseAbandonRatio));
            Assert.False(TitanTables.ChaseReady(false, TitanTables.ChaseAbandonRatio));
        }

        [Fact]
        public void Hopeless_stats_are_still_refused_from_either_state()
        {
            Assert.False(TitanTables.ChaseReady(false, 0.31));
            Assert.False(TitanTables.ChaseReady(true, 0.31));
        }
    }
}
