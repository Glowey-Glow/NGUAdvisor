using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // GEAR LOCK — "wear THESE, and optimise everything else around them".
    //
    // The old model made a loadout EITHER a list of item IDs OR an optimiser objective, and the
    // request that produced this feature is the one thing neither half could say: "2 respawn + a
    // doll, and optimise the rest for the Time Machine."
    //
    // What is under test here is the RESOLUTION — which locked id takes which slot, and what is said
    // about the ones that cannot. That second half is the point. Every way a lock can fail (an id you
    // don't own, five accessories against four slots, an id the item table doesn't define, a repeat)
    // produces a set that is simply different from the one asked for, and a solver that drops them in
    // silence is indistinguishable from one that made its own call. So the refusals are asserted with
    // their reasons, not just their counts.
    public class GearLockTests
    {
        // A tiny stand-in item table. Ids are grouped by hundreds purely so a test reads at a glance:
        // 1xx weapons, 2xx head, 3xx chest, 4xx legs, 5xx boots, 6xx accessories.
        private static readonly Dictionary<int, GearLockSlot> Catalog = new Dictionary<int, GearLockSlot>
        {
            { 101, GearLockSlot.Weapon }, { 102, GearLockSlot.Weapon }, { 103, GearLockSlot.Weapon },
            { 201, GearLockSlot.Head },   { 202, GearLockSlot.Head },
            { 301, GearLockSlot.Chest },  { 401, GearLockSlot.Legs },   { 501, GearLockSlot.Boots },
            { 601, GearLockSlot.Accessory }, { 602, GearLockSlot.Accessory }, { 603, GearLockSlot.Accessory },
            { 604, GearLockSlot.Accessory }, { 605, GearLockSlot.Accessory }
        };
        // Known to the game but NOT in the bag — the "you don't own one" case.
        private static readonly HashSet<int> Unowned = new HashSet<int> { 103, 605 };

        private static GearLockItem Look(int id)
        {
            GearLockSlot slot;
            if (!Catalog.TryGetValue(id, out slot)) return GearLockItem.Missing();
            return Unowned.Contains(id)
                ? GearLockItem.NotOwned(slot, "Item " + id)
                : GearLockItem.Have(slot, "Item " + id);
        }

        private static GearLockPlan Plan(int[] ids, bool twoWeapons = true, int accessories = 4)
            => GearLockPlan.Resolve(ids, Look, GearLockCapacity.Of(twoWeapons, accessories));

        private static string Kinds(GearLockPlan p) => string.Join(",", p.Issues.Select(i => i.Kind));

        // ── THE FEATURE ITSELF ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Locked_items_take_their_own_slots_and_leave_the_rest_free()
        {
            var p = Plan(new[] { 601, 602, 201 });
            Assert.Equal(new List<int> { 601, 602 }, p.Accessories);
            Assert.Equal(201, p.Head);
            Assert.Equal(3, p.Applied);
            Assert.Equal(3, p.SlotsHeld);
            Assert.False(p.HasIssues);
            Assert.Null(p.Message);              // nothing to report is REPORTED AS NOTHING
            Assert.Equal(0, p.Chest);            // untouched slots stay free for the optimiser
        }

        [Fact]
        public void An_empty_lock_holds_nothing_which_is_the_whole_of_the_old_behaviour()
        {
            var p = Plan(new int[0]);
            Assert.True(p.IsEmpty);
            Assert.Equal(0, p.SlotsHeld);
            Assert.Null(p.Message);
        }

        // ORDER IS THE PRIORITY ORDER, and it has to be, because it is also the slot order:
        // LoadoutManager.ChangeGear equips in list order, so the first locked accessory takes
        // accessory slot 0 and the first locked weapon takes the mainhand.
        [Fact]
        public void Order_is_preserved_because_order_decides_which_slot_each_item_takes()
        {
            var p = Plan(new[] { 603, 601, 602 });
            Assert.Equal(new List<int> { 603, 601, 602 }, p.Accessories);
        }

        // ── OVER-LOCKING: the case the user is most likely to hit ─────────────────────────────────

        [Fact]
        public void Five_locked_accessories_against_four_slots_drops_the_LAST_one_and_says_so()
        {
            // A local lookup where all five are owned, so this isolates OVER-LOCKING from ownership.
            var five = GearLockPlan.Resolve(new[] { 601, 602, 603, 604, 605 },
                id => GearLockItem.Have(GearLockSlot.Accessory, "Item " + id),
                GearLockCapacity.Of(true, 4));
            Assert.Equal(new List<int> { 601, 602, 603, 604 }, five.Accessories);
            Assert.Equal(4, five.Applied);
            Assert.Single(five.Issues);
            Assert.Equal(GearLockIssue.NoRoom, five.Issues[0].Kind);
            Assert.Equal(605, five.Issues[0].Id);
            // The user must be able to act on this, so the message names the item AND the reason.
            Assert.Contains("605", five.Message);
            Assert.Contains("4 accessory slots", five.Message);
            Assert.Contains("4 of 5", five.Message);
        }

        [Fact]
        public void Two_locked_heads_is_over_locking_too_and_the_second_is_named()
        {
            var p = Plan(new[] { 201, 202 });
            Assert.Equal(201, p.Head);
            Assert.Single(p.Issues);
            Assert.Equal(GearLockIssue.NoRoom, p.Issues[0].Kind);
            Assert.Contains("head slot is already locked", p.Message);
        }

        // ── THE OFFHAND ───────────────────────────────────────────────────────────────────────────
        // weapon2Unlocked() is wish 28's level ([DECOMP] InventoryController.cs:2746), so before that
        // wish there is exactly ONE weapon slot. A second locked weapon then has nowhere to go, and
        // the message has to say WHY rather than just "doesn't fit" — the fix is a wish, not an edit.

        [Fact]
        public void With_the_offhand_locked_behind_a_wish_only_one_weapon_can_be_pinned()
        {
            var p = Plan(new[] { 101, 102 }, twoWeapons: false);
            Assert.Equal(new List<int> { 101 }, p.Weapons);
            Assert.Single(p.Issues);
            Assert.Contains("wish 28", p.Message);
        }

        [Fact]
        public void With_the_offhand_unlocked_both_weapons_are_pinned_mainhand_first()
        {
            var p = Plan(new[] { 101, 102 }, twoWeapons: true);
            Assert.Equal(new List<int> { 101, 102 }, p.Weapons);
            Assert.False(p.HasIssues);
        }

        [Fact]
        public void A_third_weapon_never_fits_even_with_the_offhand_unlocked()
        {
            var p = GearLockPlan.Resolve(new[] { 101, 102, 103 },
                id => GearLockItem.Have(GearLockSlot.Weapon, "Item " + id),
                GearLockCapacity.Of(true, 4));
            Assert.Equal(2, p.Weapons.Count);
            Assert.Contains("both weapon slots are already locked", p.Message);
        }

        // ── IDS THAT CANNOT BE HONOURED ───────────────────────────────────────────────────────────

        [Fact]
        public void An_item_you_do_not_own_is_named_not_silently_skipped()
        {
            var p = Plan(new[] { 601, 605 });
            Assert.Equal(new List<int> { 601 }, p.Accessories);
            Assert.Single(p.Issues);
            Assert.Equal(GearLockIssue.NotOwned, p.Issues[0].Kind);
            Assert.Contains("isn't in your inventory", p.Message);
            Assert.Contains("605", p.Message);
        }

        [Fact]
        public void An_id_no_wearable_item_carries_is_refused_as_unknown_not_as_unowned()
        {
            var p = Plan(new[] { 9999 });
            Assert.Equal(0, p.Applied);
            Assert.Single(p.Issues);
            Assert.Equal(GearLockIssue.Unknown, p.Issues[0].Kind);
            Assert.Contains("no wearable item with ID 9999", p.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Zero_and_negatives_are_not_item_ids(int id)
        {
            var p = Plan(new[] { id });
            Assert.Equal(0, p.Applied);
            Assert.Equal(GearLockIssue.Invalid, p.Issues[0].Kind);
        }

        // The game equips ONE copy of a given item at a time even when you own several, so a repeat
        // cannot take a second slot. Said out loud rather than deduped in silence — a user who typed
        // the same id twice meant to type two different ones.
        [Fact]
        public void A_repeated_id_cannot_take_a_second_slot_and_is_told_so()
        {
            var p = Plan(new[] { 601, 601 });
            Assert.Single(p.Accessories);
            Assert.Equal(GearLockIssue.Duplicate, p.Issues[0].Kind);
            Assert.Contains("listed twice", p.Message);
        }

        [Fact]
        public void Several_different_refusals_are_all_reported_not_just_the_first()
        {
            var p = Plan(new[] { 601, 605, 9999, 601, 0 });
            Assert.Equal(1, p.Applied);
            Assert.Equal(4, p.Issues.Count);
            Assert.Equal("notowned,unknown,duplicate,invalid", Kinds(p));
            Assert.Contains("1 of 5", p.Message);
        }

        // ── DEGENERATE, AND IT MUST STILL WORK ────────────────────────────────────────────────────

        [Fact]
        public void Locking_every_slot_is_legal_and_leaves_nothing_to_optimise()
        {
            var cap = GearLockCapacity.Of(true, 2);
            var p = GearLockPlan.Resolve(new[] { 101, 102, 201, 301, 401, 501, 601, 602 }, Look, cap);
            Assert.False(p.HasIssues);
            Assert.Equal(8, p.Applied);
            Assert.True(p.FillsEverySlot(cap));
        }

        [Fact]
        public void A_partial_lock_does_not_claim_to_fill_every_slot()
        {
            var cap = GearLockCapacity.Of(true, 4);
            Assert.False(Plan(new[] { 601, 201 }).FillsEverySlot(cap));
        }

        // ── SLOT COUNTS MOVE, AND NOTHING IS CACHED ───────────────────────────────────────────────
        // accessorySpaces() grows as purchases / arbitrary unlocks land ([DECOMP]
        // InventoryController.cs:180). Resolve is called fresh on every solve with the LIVE count, so
        // a lock that did not fit yesterday fits the moment the slot unlocks — with no state to
        // invalidate and nothing to go stale.

        [Fact]
        public void The_same_lock_list_resolves_differently_as_accessory_slots_unlock()
        {
            var ids = new[] { 601, 602, 603 };
            var before = Plan(ids, accessories: 2);
            var after = Plan(ids, accessories: 3);

            Assert.Equal(2, before.Accessories.Count);
            Assert.Single(before.Issues);
            Assert.Equal(3, after.Accessories.Count);
            Assert.False(after.HasIssues);
        }

        [Fact]
        public void With_no_accessory_slots_at_all_the_message_says_that_rather_than_a_count()
        {
            var p = Plan(new[] { 601 }, accessories: 0);
            Assert.Empty(p.Accessories);
            Assert.Contains("no accessory slots", p.Message);
        }

        // ── HOLDS: the respawn pin asks this, and a wrong answer double-equips ─────────────────────

        [Fact]
        public void Holds_answers_for_every_slot_kind()
        {
            var p = Plan(new[] { 101, 102, 201, 301, 401, 501, 601 });
            foreach (var id in new[] { 101, 102, 201, 301, 401, 501, 601 })
                Assert.True(p.Holds(id), "should hold " + id);
            Assert.False(p.Holds(602));
            Assert.False(p.Holds(0));
        }

        // ── THE SET ITSELF ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void An_empty_or_null_lock_set_is_null_so_callers_can_test_it_one_way()
        {
            Assert.Null(GearLockSet.Of(null));
            Assert.Null(GearLockSet.Of(new int[0]));
            Assert.NotNull(GearLockSet.Of(new[] { 601 }));
        }

        // requireAccessoryId used to be its own parameter on Optimize. It is a lock, and this is the
        // whole of the conversion — the Required flag is what preserves "a required mechanic item
        // beats a respawn preference", which the old code got from an early return.
        [Fact]
        public void A_required_item_is_a_lock_that_knows_it_is_not_a_preference()
        {
            var set = GearLockSet.RequiredItem(135);
            Assert.NotNull(set);
            Assert.True(set.Required);
            Assert.Equal(new List<int> { 135 }, set.Ids);

            Assert.Null(GearLockSet.RequiredItem(0));            // "no required item" stays null
            Assert.False(GearLockSet.Of(new[] { 601 }).Required); // a user's lock is a preference
        }

        [Theory]
        [InlineData("326, 100", new[] { 326, 100 })]
        [InlineData("Lock: 326, 100", new[] { 326, 100 })]
        [InlineData("lock:326", new[] { 326 })]
        [InlineData(" 326 ,  100 ", new[] { 326, 100 })]
        public void Lock_text_reads_the_way_a_profile_writes_it(string text, int[] expected)
        {
            GearLockSet set; string err;
            Assert.True(GearLockSet.TryParse(text, out set, out err), text + " -> " + err);
            Assert.Equal(expected, set.Ids.ToArray());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Lock")]                 // no colon
        [InlineData("Lock:")]                // nothing after it
        [InlineData("326, banana")]
        [InlineData("-3")]
        public void Malformed_lock_text_is_refused_with_a_reason(string text)
        {
            GearLockSet set; string err;
            Assert.False(GearLockSet.TryParse(text, out set, out err));
            Assert.Null(set);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void It_reads_back_in_the_form_the_profile_stores()
        {
            var set = GearLockSet.Of(new[] { 326, 100 });
            Assert.Equal("326, 100", set.Format());
            Assert.Equal("2 items locked", set.Describe());
            Assert.Equal("1 item locked", GearLockSet.Of(new[] { 326 }).Describe());
            Assert.Equal("nothing locked", new GearLockSet().Describe());
        }

        // ── COMPOSITION WITH FLOORS ───────────────────────────────────────────────────────────────
        // A lock fixes a SLOT; a floor constrains a TOTAL. They are siblings and they compose — and
        // when a locked set cannot reach a floor, the operator has to be told that the lock was
        // holding slots, or the sentence points them at the wrong number to change.

        [Fact]
        public void An_infeasible_floor_says_how_many_slots_the_lock_was_holding()
        {
            GearFloorSet floors; string err;
            Assert.True(GearFloorSet.TryParse("Power >= 1T", out floors, out err));
            var have = new Dictionary<string, double> { { "Power", 2e9 } };

            var withLock = FloorVerdict.Infeasible(floors.Unmet(have), have, 4);
            Assert.False(withLock.Feasible);
            Assert.Contains("out of reach", withLock.Message);
            Assert.Contains("Gear Lock is holding 4 slots", withLock.Message);

            var one = FloorVerdict.Infeasible(floors.Unmet(have), have, 1);
            Assert.Contains("holding 1 slot,", one.Message);
        }

        // The no-lock message must be BYTE-IDENTICAL to what it was before Gear Lock existed —
        // GearFloorsTests asserts it independently, and this pins the new parameter's default.
        [Fact]
        public void With_no_lock_the_infeasible_message_is_unchanged()
        {
            GearFloorSet floors; string err;
            Assert.True(GearFloorSet.TryParse("Power >= 1T", out floors, out err));
            var have = new Dictionary<string, double> { { "Power", 2e9 } };
            Assert.DoesNotContain("Gear Lock", FloorVerdict.Infeasible(floors.Unmet(have), have).Message);
            Assert.DoesNotContain("Gear Lock", FloorVerdict.Infeasible(floors.Unmet(have), have, 0).Message);
        }
    }
}
