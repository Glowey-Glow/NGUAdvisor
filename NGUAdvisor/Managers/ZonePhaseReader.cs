using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // THE LIVE BRIDGE for ZonePhase. Everything Unity-bound lives here; the decision lives in
    // ZonePhase and is linked into the net9.0 test project. Same split as ConstraintLayerBridge /
    // ConstraintLayer, and the same reason: a Release build of NGUAdvisor.csproj auto-deploys, so the
    // part that can be tested headlessly must not be welded to Main.Character.
    //
    // WHAT IT READS, and where each read is already established:
    //   drop list      GearFarmAdvisor.DroppableIds  (the table extracted from LootDrop.zoneNDrop)
    //   unlock         BossScale.IsZoneUnlocked      (GearFarmAdvisor.cs:366, single-sourced, audit M5)
    //   one-hit        ZoneGate.Evaluate             (GearFarmAdvisor.cs:369-375, fail-closed)
    //   idle P/T       ZoneStats.IPower/IToughness   (MEASURED, not derived — ZoneStatHelper.cs:197-203)
    //   slot type      itemInfo.type[id]             ([DECOMP] ItemNameDesc.cs:92 constructItemInfo)
    //   filtered       itemList.itemFiltered[id]     (GearFarmAdvisor.cs:383-384)
    //   maxxed         itemList.itemMaxxed[id]
    //   held           equips + inventory + daycare  (see HeldIds)
    internal static class ZonePhaseReader
    {
        // Every id the character currently holds a copy of, built with ONE inventory pass.
        //
        // LoadoutManager.FindItemSlot (:390-415) answers the same question, but it re-materialises
        // GetConvertedEquips().Concat(GetConvertedInventory()) on every call — ~320 rebuilds per pass
        // across 32 zones. The set is built once instead. It is the same source
        // (LoadoutManager.cs:395), plus daycare: an accessory parked in the daycare has plainly been
        // collected, and FindItemSlot only consults daycare on the shockwave path (:417-428), which
        // would leave IDLE pinned open on an item the player owns.
        private static HashSet<int> HeldIds()
        {
            var held = new HashSet<int>();
            var inv = Main.Character.inventory;
            try { foreach (var x in inv.GetConvertedEquips()) held.Add(x.id); } catch { }
            try { foreach (var x in inv.GetConvertedInventory()) held.Add(x.id); } catch { }
            try { foreach (var e in inv.daycare) if (e != null && e.id != 0) held.Add(e.id); } catch { }
            return held;
        }

        // One ZonePhase.Input per farmable, unlocked, non-titan zone. Zones that throw are skipped
        // rather than defaulted — an absent candidate cannot route anywhere, which is the fail-closed
        // side, whereas a zeroed one would claim idle thresholds of 0 and look enterable at any stat.
        public static List<ZonePhase.Input> Candidates()
        {
            var list = new List<ZonePhase.Input>();
            var c = Main.Character;
            if (c == null) return list;

            double attack = ZoneStatHelper.EffectiveAdvAttack();
            double defense = c.totalAdvDefense();
            var table = ZoneStatHelper.UserOverrides;
            var il = c.inventory.itemList;
            var held = HeldIds();

            foreach (var zone in GearFarmAdvisor.FarmZones)
            {
                try
                {
                    if (ZoneHelpers.ZoneIsTitan(zone)) continue;
                    if (!BossScale.IsZoneUnlocked(c.effectiveBossID(), zone, ZoneHelpers.ZoneUnlocks)) continue;

                    ZoneStats st = null;
                    bool rowFound = table != null && table.TryGetValue(zone, out st);

                    // The SAME fail-closed accessor the gear farm uses; never read the table directly.
                    var gate = ZoneGate.Evaluate(table != null, rowFound, rowFound ? st.OPower : 0, attack);
                    if (!gate.Known && ZoneGate.ShouldAnnounce("ZonePhase", zone))
                        Main.LogDebug($"ZonePhase: zone {zone} treated as NOT one-shottable — {gate.Reason}");

                    // The idle bar is a DIFFERENT and LOWER gate than one-hit, on different
                    // provenance: OPower is derived from the decomp, IPower/IToughness are measured
                    // wiki values (ZoneStatHelper.cs:197-203). zoneOverride.json is user-editable and
                    // SimpleJSON's AsDouble yields 0 for an absent key, so a non-positive value is
                    // "unknown", never "zero and therefore always met" — the fail-OPEN shape
                    // ZoneGate.cs:16-18 exists to close.
                    bool idleKnown = rowFound && st.IPower > 0 && st.IToughness > 0;

                    var items = new List<ZonePhase.ItemFact>();
                    foreach (var id in GearFarmAdvisor.DroppableIds(zone))
                    {
                        int slot;
                        try { slot = (int)c.itemInfo.type[id]; }
                        catch { continue; }   // out of range: not a fact we have, so not a fact we assert

                        items.Add(new ZonePhase.ItemFact
                        {
                            Id = id,
                            Slot = slot,
                            Filtered = SafeFlag(il.itemFiltered, id),
                            Maxxed = SafeFlag(il.itemMaxxed, id),
                            Held = held.Contains(id)
                        });
                    }

                    list.Add(new ZonePhase.Input
                    {
                        Zone = zone,
                        ZoneUnlocked = true,
                        OneShotKnown = gate.Known,
                        OneShottable = gate.OneShottable,
                        OPower = rowFound ? st.OPower : 0,
                        OneShotReason = gate.Reason,
                        IdleThresholdKnown = idleKnown,
                        IPower = idleKnown ? st.IPower : 0,
                        IToughness = idleKnown ? st.IToughness : 0,
                        Attack = attack,
                        Defense = defense,
                        Items = items
                    });
                }
                catch { }
            }

            return list;
        }

        private static bool SafeFlag(IList<bool> flags, int id)
        {
            try { return flags != null && id >= 0 && id < flags.Count && flags[id]; }
            catch { return false; }
        }

        public static string ZoneName(int zone)
            => zone >= ZonePhase.ItopodZone
                ? "ITOPOD"
                : ZoneHelpers.ZoneList.TryGetValue(zone, out var n) ? n : $"Zone {zone}";
    }
}
