using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE THREE-PHASE ZONE RULE — IDLE -> ITOPOD -> FARM.
    //
    // [OPERATOR], stating the guide's rule: "the user would idle in the zone until they collect at
    // least one copy of the accessories. after that it would go back to itopod farming until the user
    // could one-hit for faster farming."
    //
    // AdvisorApply.ApplyZones jumped straight to FARM: GearFarmAdvisor.Analyze discards every zone
    // that is not already one-shottable (GearFarmAdvisor.cs:375, `if (!gate.OneShottable) continue;`)
    // and ApplyZones writes g.Best.Zone (AdvisorApply.cs:788-793). IDLE and the return to ITOPOD did
    // not exist. These tests pin the machine that adds them.
    //
    // Slot ordinals are [DECOMP] part.cs:1-15 — Head 0, Chest 1, Legs 2, Boots 3, Weapon 4,
    // Accessory 5, atkBoost 6, defBoost 7, specBoost 8, None 9, Misc 10, MacGuffin 11.
    public class ZonePhaseTests
    {
        private const int Head = 0, Weapon = 4, Accessory = 5, Misc = 10, MacGuffin = 11;

        private static ZonePhase.ItemFact Item(
            int id, int slot, bool held = false, bool maxxed = false, bool filtered = false)
            => new ZonePhase.ItemFact { Id = id, Slot = slot, Held = held, Maxxed = maxxed, Filtered = filtered };

        // A zone that is idleable and NOT one-shottable — the IDLE/ITOPOD regime. Attack sits above
        // the measured idle bar and below the derived OPower, which is the ordinary early-zone shape.
        private static ZonePhase.Input Zone(
            IEnumerable<ZonePhase.ItemFact> items,
            bool oneShot = false,
            bool oneShotKnown = true,
            double attack = 500,
            double defense = 500,
            bool idleKnown = true,
            double iPower = 100,
            double iToughness = 100,
            bool unlocked = true,
            double oPower = 1000)
            => new ZonePhase.Input
            {
                Zone = 20,
                ZoneUnlocked = unlocked,
                OneShotKnown = oneShotKnown,
                OneShottable = oneShot,
                OPower = oPower,
                OneShotReason = oneShotKnown ? null : "no row in the zone stat table",
                IdleThresholdKnown = idleKnown,
                IPower = iPower,
                IToughness = iToughness,
                Attack = attack,
                Defense = defense,
                Items = items.ToList()
            };

        // ── P1a: what "accessory" means ───────────────────────────────────────────────────────────
        // [DECOMP] part.cs:8 Accessory is ordinal 5; [DECOMP] Equipment.cs:570-577 isEquipment() is
        // ordinals 0..5, which is why GearFarmAdvisor.cs:296 tests `(int)type[id] <= 5`.

        [Theory]
        [InlineData(0, true)]   // Head
        [InlineData(1, true)]   // Chest
        [InlineData(2, true)]   // Legs
        [InlineData(3, true)]   // Boots
        [InlineData(4, true)]   // Weapon
        [InlineData(5, true)]   // Accessory
        [InlineData(6, false)]  // atkBoost
        [InlineData(7, false)]  // defBoost
        [InlineData(8, false)]  // specBoost
        [InlineData(9, false)]  // None
        [InlineData(10, false)] // Misc
        [InlineData(11, false)] // MacGuffin
        public void Equipment_is_exactly_the_first_six_slots(int slot, bool isEquipment)
            => Assert.Equal(isEquipment, ZonePhase.IsEquipment(slot));

        [Fact]
        public void Accessory_is_slot_five_and_nothing_else()
        {
            Assert.True(ZonePhase.IsAccessory(ZonePhase.SlotAccessory));
            Assert.Equal(5, ZonePhase.SlotAccessory);
            for (int s = 0; s <= 11; s++)
                if (s != 5) Assert.False(ZonePhase.IsAccessory(s));
        }

        // ── THE QUESTMANAGER TRAP ─────────────────────────────────────────────────────────────────
        // QuestManager.cs:32-39: Misc ids are not equipment, so Equipment.level never leaves 0 and
        // markItemAsMaxxed needs level >= 100 ([DECOMP] AllItemListController.cs:144-153) — so
        // itemMaxxed[id] can NEVER become true for a Misc id, and three cooking ids pinned a 3-hour
        // CapstoneHold open forever. A Misc id must not be able to hold the IDLE phase open the same way.

        [Theory]
        [InlineData(Misc)]
        [InlineData(MacGuffin)]
        [InlineData(6)]  // atkBoost
        [InlineData(9)]  // None
        public void A_non_equipment_id_can_never_be_an_accessory(int slot)
        {
            // Un-held and un-maxxable — the exact shape that pinned the capstone hold.
            var items = new[] { Item(100, Weapon, maxxed: true), Item(367, slot) };
            Assert.Empty(ZonePhase.Accessories(items));

            // ...and it therefore cannot hold IDLE open: the accessory set is empty and complete.
            var d = ZonePhase.Decide(Zone(items));
            Assert.NotEqual(ZonePhase.Phase.Idle, d.Phase);
            Assert.Equal(0, d.AccessoryCount);
        }

        [Fact]
        public void A_misc_id_does_not_hold_the_set_open_for_farming_either()
        {
            // Every EQUIPMENT id capped, one un-maxxable Misc id present. SetMaxxed must be true.
            var items = new[] { Item(221, Weapon, maxxed: true), Item(226, Accessory, maxxed: true), Item(369, Misc) };
            Assert.True(ZonePhase.SetMaxxed(items));
            Assert.Equal(ZonePhase.Phase.None, ZonePhase.Decide(Zone(items, oneShot: true)).Phase);
        }

        // ── P1a: what "held" means ────────────────────────────────────────────────────────────────
        // NOT itemDropped: [DECOMP] ItemNameDesc.cs:9915 marks an id dropped BEFORE the loot-filter
        // check at :9919, so a filtered drop sets the flag for an item that never entered inventory.

        [Fact]
        public void Held_means_a_copy_is_in_inventory_or_maxxed()
        {
            Assert.True(ZonePhase.Collected(Item(1, Accessory, held: true)));
            Assert.True(ZonePhase.Collected(Item(1, Accessory, maxxed: true)));
            Assert.False(ZonePhase.Collected(Item(1, Accessory)));
        }

        // A loot-filtered accessory is destroyed on creation (GearFarmAdvisor.cs:383-384, "a
        // loot-filtered item never drops"). If it counted, IDLE would never leave.
        [Fact]
        public void A_filtered_accessory_is_excluded_rather_than_pinning_idle_forever()
        {
            var items = new[] { Item(221, Weapon), Item(444, Accessory, filtered: true) };
            Assert.Empty(ZonePhase.Accessories(items));

            var d = ZonePhase.Decide(Zone(items));
            Assert.Equal(ZonePhase.Phase.Itopod, d.Phase);   // set vacuously complete, not stuck in IDLE
            Assert.Equal(0, d.AccessoryCount);
        }

        // ── IDLE: entered and left on its stated condition ────────────────────────────────────────

        [Fact]
        public void Idle_is_entered_when_an_accessory_is_missing_and_idle_pt_is_met()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory), Item(227, Accessory, held: true) };
            var d = ZonePhase.Decide(Zone(items));

            Assert.Equal(ZonePhase.Phase.Idle, d.Phase);
            Assert.Equal(20, d.TargetZone);           // IDLE writes the zone, not 1000
            Assert.Equal(1, d.AccessoriesHeld);
            Assert.Equal(2, d.AccessoryCount);
            Assert.Contains("226", d.Reason);         // names the accessory that is missing
        }

        [Fact]
        public void Idle_is_left_the_moment_the_last_accessory_is_held()
        {
            var missing = new[] { Item(221, Weapon), Item(226, Accessory, held: true), Item(227, Accessory) };
            Assert.Equal(ZonePhase.Phase.Idle, ZonePhase.Decide(Zone(missing)).Phase);

            var complete = new[] { Item(221, Weapon), Item(226, Accessory, held: true), Item(227, Accessory, held: true) };
            Assert.Equal(ZonePhase.Phase.Itopod, ZonePhase.Decide(Zone(complete)).Phase);
        }

        // ── WHICH PHASE RAISES THE DROP-FARM DEMAND ───────────────────────────────────────────────
        //
        // FarmVenue.DropFarmActive drives three subsystems (audit/41 §1.1), and one of them is
        // ROUTING: it is the term that beats Settings.AdventureTargetITOPOD in Main.ResolveIntentZone
        // (audit/40 §6.1, shipped at 271f5f8). §6.1 also states the rule that comes with it — "§3's
        // whole point is that silent contention is the defect; a fix that overrode silently would
        // just invert it" — and the set, rare and FARM lines all carry the override note.
        //
        // IDLE was added by the same campaign, raises the same demand, and carried NOTHING. So the
        // one phase whose entire purpose is to stand in a real zone overrode the operator's own
        // toggle without a word. AdvisorApply now asks this predicate for BOTH the demand and the
        // note, so the announcement cannot drift from the thing it announces.
        [Fact]
        public void Only_the_idle_phase_raises_the_drop_farm_demand()
        {
            var missing = new[] { Item(226, Accessory), Item(227, Accessory) };
            var idle = ZonePhase.Decide(Zone(missing));
            Assert.Equal(ZonePhase.Phase.Idle, idle.Phase);
            Assert.True(ZonePhase.RaisesDropFarmDemand(idle));
            Assert.True(idle.TargetZone < ZonePhase.ItopodZone);   // a zone the character stands in

            // ITOPOD is the opposite venue — it IS zone 1000, so there is nothing to override.
            var held = new[] { Item(226, Accessory, held: true), Item(227, Accessory, held: true) };
            var itopod = ZonePhase.Decide(Zone(held));
            Assert.Equal(ZonePhase.Phase.Itopod, itopod.Phase);
            Assert.False(ZonePhase.RaisesDropFarmDemand(itopod));

            // FARM's demand is raised by ApplyZones on the gear farm's own target, not by the
            // machine; the machine never returns FARM as a route (Plan/Explain ignore it).
            Assert.False(ZonePhase.RaisesDropFarmDemand(ZonePhase.Decide(Zone(held, oneShot: true))));

            // A decline takes no routing at all, so it cannot be standing anywhere.
            var declined = ZonePhase.Decide(Zone(missing, unlocked: false));
            Assert.Equal(ZonePhase.Phase.None, declined.Phase);
            Assert.False(ZonePhase.RaisesDropFarmDemand(declined));
        }

        // Held is per-accessory, not a count: two copies of one accessory do not satisfy two.
        [Fact]
        public void Idle_requires_one_copy_of_each_accessory_not_a_total()
        {
            var items = new[] { Item(226, Accessory, held: true, maxxed: true), Item(227, Accessory) };
            var d = ZonePhase.Decide(Zone(items));
            Assert.Equal(ZonePhase.Phase.Idle, d.Phase);
            Assert.Equal(1, d.AccessoriesHeld);
        }

        // "IDLE enter: zone unlocked AND idle P/T met." Both halves.
        [Fact]
        public void Idle_is_not_entered_when_the_zone_is_locked()
        {
            var d = ZonePhase.Decide(Zone(new[] { Item(226, Accessory) }, unlocked: false));
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.Equal(-1, d.TargetZone);
            Assert.Contains("not unlocked", d.Reason);
        }

        [Theory]
        [InlineData(50, 500)]    // power short
        [InlineData(500, 50)]    // toughness short
        [InlineData(50, 50)]     // both short
        public void Idle_is_not_entered_when_idle_power_or_toughness_is_short(double atk, double def)
        {
            var items = new[] { Item(226, Accessory) };
            var d = ZonePhase.Decide(Zone(items, attack: atk, defense: def, iPower: 100, iToughness: 100));
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.Contains("idle Power/Toughness not met", d.Reason);
        }

        // P1c. IPower/IToughness are MEASURED wiki values, not derived — ZoneStatHelper.cs:197-203,
        // "ABSENT from the decomp — do not 'derive' them". zoneOverride.json is user-editable and
        // AsDouble on an absent key yields 0, which is the exact fail-OPEN shape ZoneGate closed.
        [Theory]
        [InlineData(0, 100)]
        [InlineData(100, 0)]
        [InlineData(-1, 100)]
        public void An_unknown_idle_threshold_fails_closed(double ip, double it)
        {
            var items = new[] { Item(226, Accessory) };
            var d = ZonePhase.Decide(Zone(items, idleKnown: false, iPower: ip, iToughness: it));
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.Contains("idle threshold unknown", d.Reason);
        }

        // ── ITOPOD: entered and left on its stated condition ──────────────────────────────────────

        [Fact]
        public void Itopod_is_entered_when_accessories_are_held_and_one_hit_is_not_met()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            var d = ZonePhase.Decide(Zone(items, oneShot: false));

            Assert.Equal(ZonePhase.Phase.Itopod, d.Phase);
            Assert.Equal(ZonePhase.ItopodZone, d.TargetZone);
            Assert.Equal(1000, ZonePhase.ItopodZone);   // Main.cs:1400/:1444 — ITOPOD is zone 1000
        }

        [Fact]
        public void Itopod_is_left_the_moment_attack_reaches_opower()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            Assert.Equal(ZonePhase.Phase.Itopod, ZonePhase.Decide(Zone(items, oneShot: false)).Phase);
            Assert.Equal(ZonePhase.Phase.Farm, ZonePhase.Decide(Zone(items, oneShot: true)).Phase);
        }

        // ── FARM: entered and left on its stated condition ────────────────────────────────────────

        [Fact]
        public void Farm_is_entered_when_one_hit_is_met_and_the_set_is_not_capped()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            var d = ZonePhase.Decide(Zone(items, oneShot: true));

            Assert.Equal(ZonePhase.Phase.Farm, d.Phase);
            Assert.Equal(20, d.TargetZone);   // FARM writes the zone, not 1000
            Assert.False(d.Parked);
        }

        [Fact]
        public void Farm_is_left_when_every_zone_item_is_maxxed()
        {
            var items = new[] { Item(221, Weapon, maxxed: true), Item(226, Accessory, maxxed: true) };
            var d = ZonePhase.Decide(Zone(items, oneShot: true));

            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.Contains("is capped", d.Reason);
        }

        // One un-capped id anywhere in the zone keeps FARM open — SetMaxxed is an AND over the set.
        [Fact]
        public void One_uncapped_item_keeps_farm_open()
        {
            var items = new[] { Item(221, Weapon, maxxed: true), Item(226, Accessory, held: true) };
            Assert.Equal(ZonePhase.Phase.Farm, ZonePhase.Decide(Zone(items, oneShot: true)).Phase);
        }

        // The one overlap the operator's rule leaves open: at one-hit, IDLE's stay-condition and
        // FARM's enter-condition are both satisfiable. Both write the SAME zone number, so the tie is
        // immaterial to routing; FARM is the label, and the line says why.
        [Fact]
        public void One_hit_with_accessories_missing_farms_rather_than_idles()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory) };
            var d = ZonePhase.Decide(Zone(items, oneShot: true));

            Assert.Equal(ZonePhase.Phase.Farm, d.Phase);
            Assert.Equal(20, d.TargetZone);
            Assert.Contains("farming outpaces idling", d.Reason);
        }

        // ── FAIL CLOSED via ZoneGate ──────────────────────────────────────────────────────────────
        // ZoneGate.cs:7-23 — an unknown zone is NOT one-shottable. Zone 43 hit this for real: it had
        // no row and won the Sadistic ranking at any attack.

        [Fact]
        public void An_unknown_zone_is_not_one_shottable_and_never_farms()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            // OneShottable TRUE but OneShotKnown FALSE — the fail-open shape must not route to FARM.
            var d = ZonePhase.Decide(Zone(items, oneShot: true, oneShotKnown: false));

            Assert.Equal(ZonePhase.Phase.Itopod, d.Phase);
            Assert.Contains("one-hit unknown", d.Reason);
            Assert.Contains("no row in the zone stat table", d.Reason);   // ZoneGate's verbatim reason
        }

        [Fact]
        public void ZoneGate_supplies_the_unknown_verdict_this_machine_consumes()
        {
            // The three fail-open paths ZoneGate closes (ZoneGate.cs:14-19), fed straight in.
            foreach (var g in new[]
            {
                ZoneGate.Evaluate(tableLoaded: false, rowFound: false, oPower: 0, attack: 1e9),
                ZoneGate.Evaluate(tableLoaded: true,  rowFound: false, oPower: 0, attack: 1e9),
                ZoneGate.Evaluate(tableLoaded: true,  rowFound: true,  oPower: 0, attack: 1e9),
            })
            {
                Assert.False(g.Known);
                Assert.False(g.OneShottable);

                var items = new[] { Item(226, Accessory, held: true) };
                var z = Zone(items);
                z.OneShotKnown = g.Known;
                z.OneShottable = g.OneShottable;
                z.OneShotReason = g.Reason;

                Assert.Equal(ZonePhase.Phase.Itopod, ZonePhase.Decide(z).Phase);
            }
        }

        // ── P3b: the parked case must surface ─────────────────────────────────────────────────────
        // "If the accessories are held but one-hit is unreachable, the machine parks in ITOPOD
        // indefinitely. That is correct behaviour and it must be visible — otherwise it is
        // indistinguishable from a stuck advisor." (amendment 25 §4, found at two hours' cost.)

        [Fact]
        public void Parked_in_itopod_is_flagged_and_the_line_says_so()
        {
            var items = new[] { Item(226, Accessory, held: true) };
            var d = ZonePhase.Decide(Zone(items, oneShot: false, attack: 500, oPower: 1000));

            Assert.True(d.Parked);
            var line = ZonePhase.Message(d, "ITOPOD");
            Assert.Contains("ITOPOD", line);
            Assert.Contains("parked", line);
        }

        // P3c: the gap, in the same units ZoneGate compares — adventure attack with beast mode
        // divided out (ZoneGate.cs:42, ZoneStatHelper.cs:94-98).
        [Fact]
        public void The_line_carries_the_one_hit_gap_in_attack_units()
        {
            var items = new[] { Item(226, Accessory, held: true) };
            var d = ZonePhase.Decide(Zone(items, oneShot: false, attack: 2.5e12, oPower: 2.724e12));

            Assert.Equal(2.5e12, d.Attack);
            Assert.Equal(2.724e12, d.OPower);
            Assert.Equal(2.724e12 - 2.5e12, d.OneHitGap, 3);
            Assert.Contains("short", ZonePhase.GapText(d));
        }

        // An unknown OPower must not render as a zero gap — "0 away from one-hit" would read as
        // one-hit MET, which is the fail-open sentence ZoneGate exists to prevent.
        [Fact]
        public void An_unknown_opower_says_unknown_rather_than_showing_a_zero_gap()
        {
            var items = new[] { Item(226, Accessory, held: true) };
            var d = ZonePhase.Decide(Zone(items, oneShotKnown: false));

            Assert.True(d.Parked);
            Assert.Equal(0, d.OPower);
            Assert.Contains("unknown", ZonePhase.GapText(d));
            Assert.DoesNotContain("one-hit met", ZonePhase.GapText(d));
        }

        // ── P3a: one line per transition, and only per transition ─────────────────────────────────

        [Fact]
        public void Every_phase_change_surfaces_once()
        {
            var idle = ZonePhase.Decide(Zone(new[] { Item(226, Accessory) }));
            var itopod = ZonePhase.Decide(Zone(new[] { Item(226, Accessory, held: true) }));
            var farm = ZonePhase.Decide(Zone(new[] { Item(226, Accessory, held: true) }, oneShot: true));

            string last = null;
            foreach (var d in new[] { idle, itopod, farm })
            {
                var sig = ZonePhase.Signature(d);
                Assert.True(ZonePhase.ShouldSurface(sig, last));
                Assert.False(ZonePhase.ShouldSurface(sig, sig));   // ...and not twice
                last = sig;
            }
        }

        // Moving the farm between zones in the SAME phase is a transition worth a line: the zone is
        // what the operator sees.
        [Fact]
        public void Changing_zone_within_a_phase_is_a_transition()
        {
            var a = ZonePhase.Decide(Zone(new[] { Item(226, Accessory) }));
            var z = Zone(new[] { Item(226, Accessory) });
            z.Zone = 21;
            var b = ZonePhase.Decide(z);

            Assert.Equal(a.Phase, b.Phase);
            Assert.True(ZonePhase.ShouldSurface(ZonePhase.Signature(b), ZonePhase.Signature(a)));
        }

        // ⚠ THE TRANSITION THAT HAS NO ZONE CHANGE. IDLE on zone N becomes FARM on zone N the moment
        // one-hit is reached: same phase machine, same zone number, different phase. AdvisorApply's
        // farm line used to be gated on `SnipeZone != g.Best.Zone`, so this transition emitted
        // nothing at all — a phase change with no line, the 25 §4 shape. The signature carries the
        // phase, not just the zone, which is what makes it surface.
        [Fact]
        public void Idle_to_farm_on_the_same_zone_is_still_a_transition()
        {
            var items = new[] { Item(226, Accessory) };
            var idle = ZonePhase.Decide(Zone(items, oneShot: false));
            var farm = ZonePhase.Decide(Zone(items, oneShot: true));

            Assert.Equal(ZonePhase.Phase.Idle, idle.Phase);
            Assert.Equal(ZonePhase.Phase.Farm, farm.Phase);
            Assert.Equal(idle.TargetZone, farm.TargetZone);   // the zone did NOT move
            Assert.True(ZonePhase.ShouldSurface(ZonePhase.Signature(farm), ZonePhase.Signature(idle)));
        }

        [Fact]
        public void A_declined_decision_has_no_signature_and_no_zone()
        {
            var d = ZonePhase.Decide(Zone(new[] { Item(226, Accessory) }, unlocked: false));
            Assert.Null(ZonePhase.Signature(d));
            Assert.Equal(-1, d.TargetZone);
        }

        // Every phase names itself, the zone and the condition that fired (P3a).
        [Fact]
        public void Each_transition_line_names_the_phase_the_zone_and_the_condition()
        {
            var idle = ZonePhase.Decide(Zone(new[] { Item(226, Accessory) }));
            var line = ZonePhase.Message(idle, "Chocolate World");
            Assert.Contains("IDLE", line);
            Assert.Contains("Chocolate World", line);
            Assert.Contains("accessor", line);

            var farm = ZonePhase.Decide(Zone(new[] { Item(226, Accessory, held: true) }, oneShot: true));
            var fline = ZonePhase.Message(farm, "Chocolate World");
            Assert.Contains("FARM", fline);
            Assert.Contains("one-hit met", fline);
        }

        // ── P2e: AdvisorZones off — computes, writes nothing ──────────────────────────────────────
        // SavedSettings.cs:2171-2173 is the advise/drive switch and it already exists.

        [Fact]
        public void With_advisor_zones_off_the_machine_decides_but_writes_nothing()
        {
            foreach (var d in new[]
            {
                ZonePhase.Decide(Zone(new[] { Item(226, Accessory) })),                              // IDLE
                ZonePhase.Decide(Zone(new[] { Item(226, Accessory, held: true) })),                  // ITOPOD
                ZonePhase.Decide(Zone(new[] { Item(226, Accessory, held: true) }, oneShot: true)),   // FARM
            })
            {
                Assert.NotEqual(ZonePhase.Phase.None, d.Phase);         // it still computes
                Assert.False(ZonePhase.WritesZone(d, advisorZones: false));
                Assert.True(ZonePhase.WritesZone(d, advisorZones: true));
            }
        }

        [Fact]
        public void A_declined_decision_never_writes_even_with_the_toggle_on()
        {
            var d = ZonePhase.Decide(Zone(new[] { Item(226, Accessory) }, unlocked: false));
            Assert.False(ZonePhase.WritesZone(d, advisorZones: true));
        }

        // ── fail-closed defaults ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_default_decision_is_a_decline_not_zone_zero()
        {
            var d = default(ZonePhase.Decision);
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.Equal(0, d.TargetZone);              // the struct's zero, never written
            Assert.Equal("not evaluated", d.Reason);
            Assert.False(ZonePhase.WritesZone(d, advisorZones: true));
        }

        [Fact]
        public void A_zone_with_no_farmable_equipment_is_not_a_phase_target()
        {
            // Without the guard, an empty accessory set reads as "all held" and parks in ITOPOD.
            var d = ZonePhase.Decide(Zone(new[] { Item(369, Misc), Item(444, Accessory, filtered: true) }));
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.Contains("no farmable equipment", d.Reason);
        }

        [Fact]
        public void A_null_item_list_declines_rather_than_throwing()
        {
            var z = Zone(new ZonePhase.ItemFact[0]);
            z.Items = null;
            var d = ZonePhase.Decide(z);
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
        }

        // ── THE CANDIDATE ZONE ────────────────────────────────────────────────────────────────────
        // [OPERATOR] 2026-08-05: the HIGHEST unlocked zone that meets idle P/T and still has an
        // un-held accessory. IDLE needs a zone GearFarmAdvisor structurally cannot name — Analyze
        // drops everything not already one-shottable (GearFarmAdvisor.cs:375), which is exactly the
        // set IDLE and ITOPOD are about.

        private static ZonePhase.Input At(int zone, ZonePhase.Input z)
        {
            z.Zone = zone;
            return z;
        }

        [Fact]
        public void Plan_idles_the_highest_zone_with_a_missing_accessory()
        {
            var missing = new[] { Item(226, Accessory) };
            var complete = new[] { Item(226, Accessory, held: true) };

            var d = ZonePhase.Plan(new[]
            {
                At(12, Zone(missing)),
                At(25, Zone(missing)),    // highest with something outstanding — this one
                At(20, Zone(missing)),
                At(29, Zone(complete)),   // higher, but nothing left to collect
            });

            Assert.Equal(ZonePhase.Phase.Idle, d.Phase);
            Assert.Equal(25, d.TargetZone);
        }

        // A zone the character cannot idle is not a candidate however high it sits.
        [Fact]
        public void Plan_skips_zones_whose_idle_pt_is_not_met()
        {
            var missing = new[] { Item(226, Accessory) };
            var d = ZonePhase.Plan(new[]
            {
                At(12, Zone(missing, iPower: 100, iToughness: 100)),
                At(29, Zone(missing, iPower: 1e9, iToughness: 1e9)),   // out of reach
            });

            Assert.Equal(ZonePhase.Phase.Idle, d.Phase);
            Assert.Equal(12, d.TargetZone);
        }

        [Fact]
        public void Plan_skips_locked_zones()
        {
            var missing = new[] { Item(226, Accessory) };
            var d = ZonePhase.Plan(new[]
            {
                At(12, Zone(missing)),
                At(29, Zone(missing, unlocked: false)),
            });

            Assert.Equal(12, d.TargetZone);
        }

        // IDLE outranks ITOPOD: collecting is progress the character controls, parking is what is
        // left when there is nothing to collect.
        [Fact]
        public void Plan_prefers_idling_over_parking()
        {
            var d = ZonePhase.Plan(new[]
            {
                At(29, Zone(new[] { Item(226, Accessory, held: true) })),   // would park
                At(12, Zone(new[] { Item(226, Accessory) })),               // has something to collect
            });

            Assert.Equal(ZonePhase.Phase.Idle, d.Phase);
            Assert.Equal(12, d.TargetZone);
        }

        [Fact]
        public void Plan_parks_in_itopod_when_every_idleable_zone_is_collected()
        {
            var complete = new[] { Item(226, Accessory, held: true) };
            var d = ZonePhase.Plan(new[]
            {
                At(12, Zone(complete, attack: 500, oPower: 1000)),
                At(25, Zone(complete, attack: 500, oPower: 9000)),
            });

            Assert.Equal(ZonePhase.Phase.Itopod, d.Phase);
            Assert.Equal(ZonePhase.ItopodZone, d.TargetZone);
            Assert.True(d.Parked);
            // The highest such zone is the one the ladder is climbing toward, so its gap is reported.
            Assert.Equal(9000, d.OPower);
        }

        // The fall-through discipline (P2d). A machine with no ladder must hand routing back to the
        // boost/ITOPOD path rather than freezing SnipeZone — the same rule the challenge pause
        // follows at AdvisorApply.cs:767-772.
        [Fact]
        public void Plan_declines_when_there_is_no_ladder_to_climb()
        {
            var capped = new[] { Item(221, Weapon, maxxed: true), Item(226, Accessory, maxxed: true) };
            var d = ZonePhase.Plan(new[] { At(12, Zone(capped, oneShot: true)) });

            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.False(ZonePhase.WritesZone(d, advisorZones: true));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Plan_declines_on_an_empty_or_null_candidate_list(int mode)
        {
            var d = ZonePhase.Plan(mode == 0 ? new ZonePhase.Input[0] : null);
            Assert.Equal(ZonePhase.Phase.None, d.Phase);
            Assert.False(ZonePhase.WritesZone(d, advisorZones: true));
        }

        // Plan must never hand back a FARM zone: GearFarmAdvisor ranks farm targets by HoursToCap
        // against a time budget, and picking one by height would contradict that ranking.
        [Fact]
        public void Plan_never_chooses_a_farm_zone()
        {
            var d = ZonePhase.Plan(new[]
            {
                At(29, Zone(new[] { Item(226, Accessory, held: true) }, oneShot: true)),   // FARM
                At(12, Zone(new[] { Item(226, Accessory) })),                              // IDLE
            });

            Assert.Equal(ZonePhase.Phase.Idle, d.Phase);
            Assert.NotEqual(ZonePhase.Phase.Farm, d.Phase);
        }

        // ── WHY THE MACHINE DID NOTHING ───────────────────────────────────────────────────────────
        // A decline emits no routing and no line, so "correctly declining" and "silently broken" are
        // indistinguishable in the log — the 25 §4 shape one level up. Observed live: the machine ran
        // 3.5 h across two builds and never emitted a line, and nothing could say whether that was
        // right. Explain() carries the counts that make it legible.

        [Fact]
        public void A_save_where_every_zone_is_farm_ready_declines_and_says_how_many()
        {
            // The live shape: one-hit met everywhere, so every candidate resolves to FARM. Plan never
            // chooses FARM (GearFarmAdvisor owns that ranking), so the machine declines — correctly —
            // and the boost farm keeps routing.
            var items = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            var r = ZonePhase.Explain(new[]
            {
                At(12, Zone(items, oneShot: true)),
                At(20, Zone(items, oneShot: true)),
                At(25, Zone(items, oneShot: true)),
            });

            Assert.Equal(ZonePhase.Phase.None, r.Chosen.Phase);
            Assert.Equal(3, r.Candidates);
            Assert.Equal(3, r.FarmReady);
            Assert.Equal(0, r.Idle);
            Assert.Equal(0, r.Parked);
            Assert.Contains("3 farm-ready", r.Summary());
        }

        [Fact]
        public void The_report_separates_the_four_outcomes()
        {
            var missing = new[] { Item(221, Weapon), Item(226, Accessory) };
            var complete = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            var capped = new[] { Item(221, Weapon, maxxed: true), Item(226, Accessory, maxxed: true) };

            var r = ZonePhase.Explain(new[]
            {
                At(10, Zone(complete, oneShot: true)),    // FARM
                At(12, Zone(missing)),                    // IDLE
                At(20, Zone(complete)),                   // ITOPOD
                At(25, Zone(capped, oneShot: true)),      // declined - capped
            });

            Assert.Equal(4, r.Candidates);
            Assert.Equal(1, r.FarmReady);
            Assert.Equal(1, r.Idle);
            Assert.Equal(1, r.Parked);
            Assert.Equal(1, r.Declined);
            Assert.Contains("is capped", r.TopDeclineReason);
            Assert.Equal(ZonePhase.Phase.Idle, r.Chosen.Phase);   // IDLE still wins
        }

        [Fact]
        public void Explain_and_plan_agree()
        {
            var sets = new[]
            {
                new[] { At(12, Zone(new[] { Item(226, Accessory) })) },
                new[] { At(12, Zone(new[] { Item(226, Accessory, held: true) })) },
                new[] { At(12, Zone(new[] { Item(226, Accessory, maxxed: true) }, oneShot: true)) },
            };
            foreach (var s in sets)
                Assert.Equal(ZonePhase.Plan(s).Phase, ZonePhase.Explain(s).Chosen.Phase);
        }

        // The counts are the latch key at the call site, so a steady state must produce a stable
        // summary — otherwise the "once per change" line becomes a line every pass.
        [Fact]
        public void The_summary_is_stable_for_an_unchanged_save()
        {
            var items = new[] { Item(221, Weapon), Item(226, Accessory, held: true) };
            var a = ZonePhase.Explain(new[] { At(12, Zone(items, oneShot: true)) });
            var b = ZonePhase.Explain(new[] { At(12, Zone(items, oneShot: true)) });
            Assert.Equal(a.Summary(), b.Summary());
        }

        [Fact]
        public void An_empty_candidate_list_reports_zero_rather_than_throwing()
        {
            var r = ZonePhase.Explain(new ZonePhase.Input[0]);
            Assert.Equal(0, r.Candidates);
            Assert.Equal(ZonePhase.Phase.None, r.Chosen.Phase);
            Assert.Contains("0 candidate", r.Summary());
        }

        // ── the real drop table ───────────────────────────────────────────────────────────────────
        // Zone 20 as GearFarmAdvisor actually carries it (GearFarmAdvisor.cs:137-144), with the slot
        // types from [DECOMP] ItemNameDesc.cs constructItemInfo(): 221-225 are the boss-set armour,
        // 226/227 and 444 (Candy Corn Necklace) are accessories, and 142 (Ascended Ascended Ascended
        // Pendant) is a cross-zone accessory. Four accessories, five non-accessory gear ids — the
        // rule names a strictly smaller set than "the zone's gear".
        [Fact]
        public void Zone_20_accessories_are_a_strict_subset_of_its_gear()
        {
            var items = new[]
            {
                Item(221, Head), Item(222, Head), Item(223, Head), Item(224, Head), Item(225, Head),
                Item(226, Accessory), Item(227, Accessory), Item(444, Accessory), Item(142, Accessory),
            };
            var acc = ZonePhase.Accessories(items);
            Assert.Equal(4, acc.Count);
            Assert.Equal(new[] { 142, 226, 227, 444 }, acc.Select(a => a.Id).OrderBy(i => i).ToArray());
            Assert.True(acc.Count < items.Length);
        }
    }
}
