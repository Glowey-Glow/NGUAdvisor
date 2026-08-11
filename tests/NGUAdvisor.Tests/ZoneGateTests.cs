using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The one-shot gate, extracted from BoostFarmAdvisor/GearFarmAdvisor so it is single-sourced and
    // testable (the same treatment BossScale got for the unlock gate).
    //
    // The behaviour under test is the fail-CLOSED rule. The inline version this replaces had three
    // silent fail-open paths — table null, row missing, OPower <= 0 — each of which skipped the gate
    // entirely and let a zone be ranked as one-shottable at ANY attack. Zone 43 hit the middle one in
    // production: no row in ZoneStatHelper.Defaults, so it won the Sadistic ranking outright.
    // audit/01 §"Why not keep" row 2; audit/09 BoostFarm §8 MED, GearFarm §8 MED.
    public class ZoneGateTests
    {
        [Fact]
        public void Unknown_zone_is_not_one_shottable_however_large_the_attack()
        {
            var d = ZoneGate.Evaluate(tableLoaded: true, rowFound: false, oPower: 0, attack: 1e300);

            Assert.False(d.OneShottable);
            Assert.False(d.Known);
            Assert.Equal("no row in the zone stat table", d.Reason);
        }

        [Fact]
        public void Missing_table_is_not_one_shottable_however_large_the_attack()
        {
            var d = ZoneGate.Evaluate(tableLoaded: false, rowFound: false, oPower: 0, attack: 1e300);

            Assert.False(d.OneShottable);
            Assert.False(d.Known);
            Assert.Equal("zone table not loaded", d.Reason);
        }

        // A zoneOverride.json with a misspelled key ("Opower") parses to AsDouble == 0. That used to
        // make the zone maximally permissive; it must now make it maximally restrictive.
        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Non_positive_OPower_is_not_one_shottable(double oPower)
        {
            var d = ZoneGate.Evaluate(tableLoaded: true, rowFound: true, oPower: oPower, attack: 1e300);

            Assert.False(d.OneShottable);
            Assert.False(d.Known);
            Assert.Equal("row has a non-positive OPower", d.Reason);
        }

        [Fact]
        public void Known_row_admits_the_zone_only_at_or_above_its_OPower()
        {
            // Zone 43's derived threshold.
            const double oPower = 7.026e35;

            Assert.False(ZoneGate.Evaluate(true, true, oPower, oPower * 0.999).OneShottable);
            Assert.True(ZoneGate.Evaluate(true, true, oPower, oPower).OneShottable);
            Assert.True(ZoneGate.Evaluate(true, true, oPower, oPower * 1.001).OneShottable);
        }

        [Fact]
        public void A_known_row_is_reported_as_known_with_no_reason()
        {
            var d = ZoneGate.Evaluate(true, true, 100, 50);

            Assert.False(d.OneShottable);   // below threshold...
            Assert.True(d.Known);           // ...but not because we lacked data
            Assert.Null(d.Reason);
        }

        // Known == false must ALWAYS imply OneShottable == false. That single implication is the whole
        // fail-closed contract; if a future edit breaks it, the fail-open class of bug is back.
        [Theory]
        [InlineData(false, false, 0.0)]
        [InlineData(false, true, 1.0)]
        [InlineData(true, false, 0.0)]
        [InlineData(true, true, 0.0)]
        [InlineData(true, true, -5.0)]
        public void Unknown_never_means_one_shottable(bool tableLoaded, bool rowFound, double oPower)
        {
            var d = ZoneGate.Evaluate(tableLoaded, rowFound, oPower, attack: double.MaxValue);

            if (!d.Known)
            {
                Assert.False(d.OneShottable);
                Assert.NotNull(d.Reason);
            }
        }

        // The farm advisors run on a 1 Hz display path, so the miss must be logged once, not 86,400
        // times a day per zone.
        [Fact]
        public void A_missing_zone_announces_once_per_caller_and_zone()
        {
            ZoneGate.ResetAnnouncements();

            Assert.True(ZoneGate.ShouldAnnounce("BoostFarm", 43));
            Assert.False(ZoneGate.ShouldAnnounce("BoostFarm", 43));

            // A different advisor still gets to say it once.
            Assert.True(ZoneGate.ShouldAnnounce("GearFarm", 43));
            Assert.False(ZoneGate.ShouldAnnounce("GearFarm", 43));

            // A different zone is a different fact.
            Assert.True(ZoneGate.ShouldAnnounce("BoostFarm", 44));
        }

        // ---- the fight-type read, resolved to the same rule (audit/40 §4, B2) -----------------
        //
        // ZoneStatHelper.ZoneFightType returned 2 — the BEST fight type, "so unknown zones never
        // block progress" — for a zone with no row. Same table, same file, same fail-OPEN shape the
        // gate above exists to close. These pin the resolution: an unknown zone is NOT clearable.

        [Fact]
        public void Unknown_zone_is_not_clearable_at_any_stats()
        {
            var d = ZoneGate.EvaluateFightType(tableLoaded: true, rowFound: false, rowFightType: 2);

            Assert.Equal(0, d.FightType);
            Assert.False(d.Known);
            Assert.Equal("no row in the zone stat table", d.Reason);
        }

        [Fact]
        public void Missing_table_is_not_clearable_at_any_stats()
        {
            var d = ZoneGate.EvaluateFightType(tableLoaded: false, rowFound: false, rowFightType: 2);

            Assert.Equal(0, d.FightType);
            Assert.False(d.Known);
            Assert.Equal("zone table not loaded", d.Reason);
        }

        // The row is the authority when there IS one — fail-closed must not become "always 0".
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void A_known_row_answers_for_itself(int rowFightType)
        {
            var d = ZoneGate.EvaluateFightType(tableLoaded: true, rowFound: true, rowFightType: rowFightType);

            Assert.Equal(rowFightType, d.FightType);
            Assert.True(d.Known);
            Assert.Null(d.Reason);
        }

        // The same single implication that is the whole contract above: unknown never means
        // clearable. 2 is the value the old code returned for exactly these inputs.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void Unknown_never_means_clearable(bool tableLoaded, bool rowFound)
        {
            var d = ZoneGate.EvaluateFightType(tableLoaded, rowFound, rowFightType: 2);

            Assert.False(d.Known);
            Assert.Equal(0, d.FightType);
            Assert.NotNull(d.Reason);
        }

        // ZoneFightType shares the announce latch with the one-shot gate, under its own caller key,
        // so a missing row cannot be reported twice for the same zone by the same reader — and the
        // one-shot gate's own announcement is not consumed by it.
        [Fact]
        public void The_fight_type_reader_announces_under_its_own_caller_key()
        {
            ZoneGate.ResetAnnouncements();

            Assert.True(ZoneGate.ShouldAnnounce("ZoneFightType", 43));
            Assert.False(ZoneGate.ShouldAnnounce("ZoneFightType", 43));
            Assert.True(ZoneGate.ShouldAnnounce("GearFarm", 43));
        }
    }
}
