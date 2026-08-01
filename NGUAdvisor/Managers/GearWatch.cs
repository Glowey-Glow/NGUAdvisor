using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // "Did what I own change?" — the signal the advisor never had.
    //
    // Before this, a newly dropped or merged item could sit unused for up to two minutes, because the
    // 120s poll in AdvisorApply.ApplyGearRefresh was the ONLY thing that ever asked the optimizer to
    // look again. This re-arms that clock the moment the inventory actually changes.
    //
    // WHAT IS HASHED, AND WHY IT IS SPECIFICALLY THIS:
    //  - IDs only, never levels. GameGearAdapter.CalcCap scales an item's stats by .level, and the
    //    advisor boosts gear continuously, so a hash over (id, level) would change every few seconds
    //    forever — it would be a busy-wait wearing a trigger's clothes.
    //  - The UNION of equipped + inventory + daycare. ChangeGear only MOVES items between those
    //    collections (including MoveFromDaycareToInventory on the shockwave path), so the union is
    //    invariant under the advisor's own swaps. That is what makes this incapable of self-triggering;
    //    an equipped-only or inventory-only hash would fire on every swap and feed back into itself.
    //  - Order-independent (sum of a mixed id, plus a mixed count). Slot order shuffles constantly and
    //    means nothing here; a count term keeps {a,a,b} distinguishable from {a,b}.
    //
    // It therefore fires on: a drop, a merge (two copies become one, so the count moves), a trash, and
    // a transform climb. Those are exactly "new gear obtained".
    //
    // MAIN THREAD ONLY (reads live game collections). Called from AdvisorApply.Tick, which is already
    // 30s-throttled — so the cost is one allocation-free integer pass over the inventory, twice a minute.
    public static class GearWatch
    {
        public static ulong Signature { get; private set; }
        // Bumped on every observed change. Callers that want "has anything changed since I looked"
        // compare this rather than the raw hash.
        public static int Seq { get; private set; }
        public static bool Primed { get; private set; }

        // Equipment, not "anything in a bag slot". Same test LoadoutManager uses before equipping.
        private static bool IsGear(Equipment e)
        {
            if (e == null || e.id == 0) return false;
            try { return e.isEquipment(); } catch { return false; }
        }

        private static IEnumerable<int> LiveIds()
        {
            var inv = Main.Character.inventory;
            var ic = Main.InventoryController;

            if (inv.weapon != null) yield return inv.weapon.id;
            // If this read fails we must NOT quietly drop weapon2 from the union — ChangeGear can still
            // fill that slot, so an absent weapon2 would make our own swap look like a new item and
            // self-trigger. Assume unlocked on failure: including a slot that turns out to be locked is
            // harmless (its id is a stable constant either way), dropping one is not.
            bool two = true;
            try { two = ic.weapon2Unlocked(); } catch { }
            if (two && inv.weapon2 != null) yield return inv.weapon2.id;
            if (inv.head != null) yield return inv.head.id;
            if (inv.chest != null) yield return inv.chest.id;
            if (inv.legs != null) yield return inv.legs.id;
            if (inv.boots != null) yield return inv.boots.id;

            if (inv.accs != null)
                foreach (var a in inv.accs) if (a != null) yield return a.id;

            // EQUIPMENT ONLY out of the bulk collections. `inventory` is not just gear — it also holds
            // boosts (ids 1..39), quest items, cooking ingredients and convertibles, and the advisor
            // churns all of them constantly: MergeBoosts, ManageQuestItems and ManageConvertibles add,
            // merge and delete on their own schedules, and boosts drop on nearly every kill. Hashing
            // those made Poll() return true on effectively every tick for an actively-played character,
            // which would have turned the 120s gear throttle into a permanent 30s one — a 4x increase in
            // the heaviest step in the tick, for a trigger that is supposed to fire on new GEAR.
            // isEquipment() is the same test LoadoutManager uses, so this stays in step with what
            // ChangeGear can actually move.
            if (inv.inventory != null)
                foreach (var e in inv.inventory) if (IsGear(e)) yield return e.id;
            // Daycare is part of the union ON PURPOSE — not because the optimizer can use those items
            // (it can't), but because ChangeGear can move an item OUT of daycare, and leaving daycare
            // unhashed would make that look like a brand-new item and re-trigger on our own swap.
            if (inv.daycare != null)
                foreach (var d in inv.daycare) if (IsGear(d)) yield return d.id;
            // Same reason as daycare: ManageFavoredMacguffin moves items between `macguffins` and
            // `inventory` in both directions, so leaving this side out would let one of OUR moves look
            // like a brand-new item.
            if (inv.macguffins != null)
                foreach (var mg in inv.macguffins) if (IsGear(mg)) yield return mg.id;
        }

        // Returns true when the owned-item set changed since the last call. The FIRST call only primes
        // the baseline and returns false: at that point every item is "new", and re-optimizing on load
        // is already handled (and done better) by AdvisorApply's _gearAsserted startup assert.
        public static bool Poll()
        {
            try
            {
                ulong sig = GearSignature.Compute(LiveIds());
                if (!Primed)
                {
                    Signature = sig;
                    Primed = true;
                    return false;
                }
                if (sig == Signature) return false;
                Signature = sig;
                Seq++;
                return true;
            }
            catch (Exception e)
            {
                Main.LogDebug($"GearWatch: {e.Message}");
                return false;
            }
        }

        // A payload reload wipes statics anyway, but be explicit: after a reload the baseline is
        // meaningless and must be re-primed rather than compared against.
        public static void Reset()
        {
            Signature = 0;
            Primed = false;
        }
    }
}
