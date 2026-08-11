using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE DC/PP DIGGER VENUE LAW.
    //
    // Diggers 0 "Drops" and 8 "PP" are a swap pair chosen by venue. The law was written as
    // `if (titanWindow || hunting) DC; else if (itopod) PP;` with no else, where `hunting` is the
    // MANUAL Gear Hunt toggle — so a farm the ADVISOR chose was not a case at all.
    //
    // ⚠ THAT HOLE INVERTED IN PRACTICE. While routing sat in the ITOPOD, `itopod` was true and the PP
    // branch pushed digger 0 to the TAIL, where Take(slots) cuts it. Observed live: equipped set
    // 3,1,2,8,4,5,6,7,11,10,9 — every digger except 0. Routing then moved to zone 20 for a ~54h drop
    // farm, `itopod` went false, `hunting` was false, neither branch fired, and digger 0 stayed cut
    // by a decision made for a venue the character had already left.
    public class FarmVenueTests
    {
        // A real adventure zone, standing in for "wherever the character actually is".
        private const int FarmZone = 20;

        [Fact]
        public void The_advisors_own_drop_farm_wants_drop_chance()
        {
            var p = FarmVenue.Decide(titanWindow: false, gearHunt: false, dropFarm: true, currentZone: FarmZone);
            Assert.Equal(FarmVenue.Pays.DropChance, p);
            Assert.Equal(0, FarmVenue.Promote(p));
            Assert.Equal(8, FarmVenue.Bench(p));
        }

        // THE REGRESSION. Both false is the state that produced the live bug: no reorder at all, so
        // whatever the previous venue decided stood — and the previous venue had benched digger 0.
        [Fact]
        public void A_drop_farm_outranks_a_stale_itopod_read()
        {
            // The farm's demand wins even when the character has not physically left the ITOPOD yet
            // — one digger tick of lag is expected by construction (FarmVenue's own header), and it
            // is exactly why the gear hunt already outranks the ITOPOD term.
            var p = FarmVenue.Decide(titanWindow: false, gearHunt: false, dropFarm: true,
                                     currentZone: ZonePhase.ItopodZone);
            Assert.Equal(FarmVenue.Pays.DropChance, p);
        }

        [Fact]
        public void The_itopod_still_wants_perk_points()
        {
            var p = FarmVenue.Decide(titanWindow: false, gearHunt: false, dropFarm: false,
                                     currentZone: ZonePhase.ItopodZone);
            Assert.Equal(FarmVenue.Pays.PerkPoints, p);
            Assert.Equal(8, FarmVenue.Promote(p));
            Assert.Equal(0, FarmVenue.Bench(p));
        }

        // Titan and manual gear hunt are deliberate events and keep their precedence unchanged.
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Titan_and_gear_hunt_still_win(bool titan, bool hunt)
        {
            Assert.Equal(FarmVenue.Pays.DropChance,
                FarmVenue.Decide(titan, hunt, dropFarm: false, currentZone: ZonePhase.ItopodZone));
        }

        // ⚠ NO SIGNAL IS NOT THE ITOPOD. Returning PerkPoints here would re-create the original bug
        // from the other side: an unknown venue would bench the drop digger.
        [Fact]
        public void No_venue_signal_reorders_nothing()
        {
            var p = FarmVenue.Decide(false, false, false, FarmZone);
            Assert.Equal(FarmVenue.Pays.Unknown, p);
            Assert.Equal(-1, FarmVenue.Promote(p));
            Assert.Equal(-1, FarmVenue.Bench(p));
        }

        // ── audit/40 §3 item 7: THE VENUE IS A ZONE, AND ONLY A ZONE ──────────────────────────────
        //
        // The fourth parameter used to be `bool itopod`, filled by OptimizationAdvisor from
        // `!hunting && (AdventureTargetITOPOD || SnipeZone >= 1000)` — layer-1 INTENT fields. audit/40
        // §0: layer 2 decides what is actually adventured and discards most of layer 1 silently, so
        // whenever any of R3-R8 or R11-R13 fired, this law ran for a venue the character had already
        // left. That is the same shape as the live inversion in this file's header, one level up.
        //
        // ⚠ THE GUARD IS THE SIGNATURE, NOT THIS ASSERTION. C# has no bool-to-int conversion, so the
        // old expression cannot be handed to Decide again without a deliberate rewrite — reverting
        // OptimizationAdvisor's line does not compile. These tests pin what the zone MEANS.
        [Fact]
        public void Perk_points_are_only_ever_earned_at_the_itopod_zone()
        {
            // Every zone the character can stand in below the ITOPOD, including the Safe Zone.
            for (int zone = -1; zone < ZonePhase.ItopodZone; zone++)
                Assert.NotEqual(FarmVenue.Pays.PerkPoints,
                    FarmVenue.Decide(titanWindow: false, gearHunt: false, dropFarm: false, currentZone: zone));

            Assert.Equal(FarmVenue.Pays.PerkPoints,
                FarmVenue.Decide(titanWindow: false, gearHunt: false, dropFarm: false,
                                 currentZone: ZonePhase.ItopodZone));
            Assert.True(FarmVenue.AtItopod(ZonePhase.ItopodZone));
            Assert.False(FarmVenue.AtItopod(ZonePhase.ItopodZone - 1));
        }

        // The rows §3 item 7 is about: layer 2 routing a REAL zone while the layer-1 toggles still
        // say ITOPOD. The old term read the toggles and benched digger 0 through every one of these.
        [Theory]
        [InlineData(0)]     // R4, the empty Time Machine (audit/40 §7 — the row §3 item 2 omitted)
        [InlineData(20)]    // R5 gold snipe / R7 quest zone / R12 EVIL CLIMB / R13 gold-starved augs
        [InlineData(35)]    // R6, a spawning titan zone
        public void A_layer_two_override_into_a_real_zone_never_reads_as_the_itopod(int routedZone)
        {
            Assert.False(FarmVenue.AtItopod(routedZone));
            Assert.NotEqual(FarmVenue.Pays.PerkPoints,
                FarmVenue.Decide(titanWindow: false, gearHunt: false, dropFarm: false, currentZone: routedZone));
        }

        // ⚠ AN UNREADABLE ZONE IS NOT THE ITOPOD EITHER. The live read is wrapped in a try/catch, and
        // "we don't know" must reorder nothing — the same rule the Unknown case above exists for.
        [Fact]
        public void An_unreadable_zone_reorders_nothing()
        {
            Assert.False(FarmVenue.AtItopod(FarmVenue.UnknownZone));
            Assert.Equal(FarmVenue.Pays.Unknown,
                FarmVenue.Decide(false, false, false, FarmVenue.UnknownZone));
        }

        // ⚠ THE MANUAL-POOL CASE. With AutoProfile OFF the profile's digger list becomes both the
        // pool and a hard filter. 24hr-EarlyEvil's three breakpoints are [2,3,6,7,8,10,11] /
        // [1,2,3,4,5,6,7,8,11,10,9] / [2,3,4,5,8,9,11] — NONE contains digger 0, so the demand was
        // unsatisfiable and looked like a DC/PP flip the moment AutoProfile was switched off.
        // [OPERATOR] approved seating digger 0 anyway, as the ONE exception to the no-filler rule.
        [Theory]
        [InlineData(new[] { 2, 3, 6, 7, 8, 10, 11 })]
        [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 10, 9 })]
        [InlineData(new[] { 2, 3, 4, 5, 8, 9, 11 })]
        public void The_shipped_profile_pools_all_omit_the_drop_chance_digger(int[] pool)
        {
            Assert.DoesNotContain(FarmVenue.DropChanceDigger, pool);
            // ...and every one of them DOES carry the digger the farm wants benched, so the swap has
            // somewhere to take its slot from.
            Assert.Contains(FarmVenue.PerkPointDigger, pool);
        }

        [Fact]
        public void The_pair_is_diggers_zero_and_eight()
        {
            // OptimizationAdvisor.DiggerNames: index 0 "Drops", index 8 "PP".
            Assert.Equal(0, FarmVenue.DropChanceDigger);
            Assert.Equal(8, FarmVenue.PerkPointDigger);
        }

        // Promote and Bench are always the two members of the pair, never the same digger.
        [Theory]
        [InlineData(FarmVenue.Pays.DropChance)]
        [InlineData(FarmVenue.Pays.PerkPoints)]
        public void Promote_and_bench_are_always_opposite_members(FarmVenue.Pays p)
        {
            Assert.NotEqual(FarmVenue.Promote(p), FarmVenue.Bench(p));
            Assert.Contains(FarmVenue.Promote(p), new[] { 0, 8 });
            Assert.Contains(FarmVenue.Bench(p), new[] { 0, 8 });
        }
    }
}
