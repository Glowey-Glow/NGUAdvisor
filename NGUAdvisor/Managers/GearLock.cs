using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // GEAR LOCK — "wear THESE, and optimise everything else around them".
    //
    // A loadout used to be one of two things and never both: a hand-written list of item IDs, OR an
    // optimiser objective. That is a false choice. Half a dozen real setups need exactly one or two
    // fixed items and want the optimiser to do the rest of the work — two respawn items and a doll in
    // early Evil, with the remaining slots maximised for the Time Machine, is the case that prompted
    // this. Neither half of the old pair could express it: the ID list cannot improve as gear drops,
    // and the objective cannot be told "keep this one".
    //
    // THIS IS THE ONE PINNING MECHANISM. GearOptimizer already carried two ad-hoc pins before this
    // file existed — `forceTopRespawn` and `requireAccessoryId` — and a third parallel one would be the
    // mistake this codebase keeps paying for. requireAccessoryId (the titan mechanic item) IS a gear
    // lock and has been folded into it outright; see GearLockSet.RequiredItem. forceTopRespawn is NOT,
    // and deliberately: it does not name an item, it names a PREDICATE ("some item carrying Respawn")
    // whose winner is chosen by re-running the whole solve per candidate. What it does share is the
    // machinery — after this change there is exactly one place in the optimiser that holds a slot
    // against the ascent, and both features go through it.
    //
    // Unity-free by construction, in the AdventureFloor / ZoneGate shape: the live inventory read is
    // the caller's job (GearOptimizer builds the catalog from Main.Character.inventory), everything
    // here takes plain-old data. That split is not cosmetic — every bug this subsystem has shipped
    // lived on the far side of the Unity boundary, so the half that can be tested is worth keeping
    // separable from the half that cannot.
    public enum GearLockSlot
    {
        Weapon,
        Head,
        Chest,
        Legs,
        Boots,
        Accessory
    }

    // What the game knows about one id. Filled by the caller from the live inventory + the item table;
    // `Known` and `Owned` are deliberately separate answers, because "there is no item 9999" and "item
    // 412 exists but you don't have one" are different mistakes and the user needs to be told which.
    public struct GearLockItem
    {
        public bool Known;            // the game's item table defines a WEARABLE item with this id
        // It is in the inventory bag or already worn — i.e. the solver can actually seat it. That is
        // deliberately the SAME set the optimiser picks from and not a wider "do you have one
        // anywhere": an item parked in the DAYCARE is not a candidate for any set the optimiser
        // builds, so calling it owned here would produce a lock that can never be honoured and a
        // refusal that never fires. It reports as "isn't in your inventory", which is both true of
        // this solver and the actionable thing to fix.
        public bool Owned;
        public GearLockSlot Slot;
        public string Name;           // for the report; "" is tolerated

        public static GearLockItem Missing() { return new GearLockItem(); }
        public static GearLockItem NotOwned(GearLockSlot slot, string name)
        {
            var it = new GearLockItem();
            it.Known = true; it.Owned = false; it.Slot = slot; it.Name = name;
            return it;
        }
        public static GearLockItem Have(GearLockSlot slot, string name)
        {
            var it = new GearLockItem();
            it.Known = true; it.Owned = true; it.Slot = slot; it.Name = name;
            return it;
        }
    }

    // How many of each slot the character actually has RIGHT NOW. Read live on every solve and never
    // cached: accessorySpaces() moves as purchases/arbitrary unlocks land ([DECOMP]
    // InventoryController.cs:180 — base 2, +1 per hasAcc3..hasAcc9 / purchases.hasAcc5 / two Troll
    // completions) and weapon2Unlocked() is wish 28's level ([DECOMP] InventoryController.cs:2746).
    // A lock that did not fit last hour may fit this hour, and re-resolving is what makes that free.
    public struct GearLockCapacity
    {
        public int Weapons;        // 1, or 2 once the offhand is unlocked
        public int Accessories;    // accessorySpaces()

        public static GearLockCapacity Of(bool twoWeapons, int accessories)
        {
            var c = new GearLockCapacity();
            c.Weapons = twoWeapons ? 2 : 1;
            c.Accessories = accessories < 0 ? 0 : accessories;
            return c;
        }
    }

    // Why one locked id did not take a slot. Every one of these is REPORTED — an id the user typed
    // and the solver quietly dropped is the failure mode this whole type exists to prevent.
    public sealed class GearLockIssue
    {
        public const string Invalid = "invalid";     // not an item id at all
        public const string Unknown = "unknown";     // no wearable item carries this id
        public const string NotOwned = "notowned";   // a real item, but not in your inventory
        public const string Duplicate = "duplicate"; // listed twice; the game equips one copy per id
        public const string NoRoom = "noroom";       // more locks for this slot than the slot has room

        public int Id;
        public string Kind;
        public string Text;          // one operator-facing clause, sentence case, no markup
    }

    // The user's statement: these ids are pinned. Order matters — it is the order the ids take their
    // slots in, which for weapons decides mainhand vs offhand and for accessories decides which
    // physical accessory slot each lands in (LoadoutManager.ChangeGear equips in list order).
    public sealed class GearLockSet
    {
        public readonly List<int> Ids = new List<int>();

        // TRUE when the lock states a GAME MECHANIC rather than a preference — today only the titan
        // mechanic item (the Ring of Apathy, without which UUG is invincible). It is not a knob: it
        // reproduces, exactly, the pre-existing rule that a required accessory beat the respawn pin.
        // A user's Gear Lock is a preference and composes with the pin instead.
        public bool Required;

        public bool IsEmpty { get { return Ids.Count == 0; } }

        public static GearLockSet Of(IEnumerable<int> ids)
        {
            if (ids == null) return null;
            var set = new GearLockSet();
            foreach (var id in ids) set.Ids.Add(id);
            return set.IsEmpty ? null : set;
        }

        // The titan mechanic item, as a lock. This is the whole of the old `requireAccessoryId`.
        public static GearLockSet RequiredItem(int id)
        {
            if (id == 0) return null;
            var set = new GearLockSet();
            set.Ids.Add(id);
            set.Required = true;
            return set;
        }

        // "326, 100" — the canonical text the profile editor round-trips.
        public string Format()
        {
            var parts = new string[Ids.Count];
            for (int i = 0; i < Ids.Count; i++) parts[i] = Ids[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(", ", parts);
        }

        public string Describe()
        {
            if (IsEmpty) return "nothing locked";
            return Ids.Count == 1 ? "1 item locked" : Ids.Count + " items locked";
        }

        // "326, 100" (optionally with a leading "Lock:"). Refuses with a reason rather than silently
        // dropping a token — the same contract as GearFloorSet.TryParse and UntilClause, for the same
        // reason: a lock the user typed and the parser ate is a set they never asked for.
        public static bool TryParse(string text, out GearLockSet set, out string error)
        {
            set = null; error = null;
            var s = (text ?? "").Trim();
            if (s.Length == 0) { error = "empty"; return false; }
            if (s.StartsWith("Lock", StringComparison.OrdinalIgnoreCase))
            {
                int colon = s.IndexOf(':');
                if (colon < 0) { error = "no : after \"Lock\""; return false; }
                s = s.Substring(colon + 1).Trim();
            }

            var result = new GearLockSet();
            foreach (var raw in s.Split(','))
            {
                var piece = raw.Trim();
                if (piece.Length == 0) continue;
                int id;
                if (!int.TryParse(piece, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id < 0)
                { error = "\"" + piece + "\" is not an item ID"; return false; }
                result.Ids.Add(id);
            }

            if (result.IsEmpty) { error = "no item IDs"; return false; }
            set = result;
            return true;
        }
    }

    // The lock set RESOLVED against a real inventory and real slot counts: which id holds which slot,
    // and what could not be honoured. Produced fresh on every solve.
    public sealed class GearLockPlan
    {
        // Index 0 is the mainhand, index 1 the offhand. The split matters: the offhand's contribution
        // is discounted by weapon2Factor() ([DECOMP] InventoryController.cs:687 — 0 while wish 28 is
        // unlearned, else wish 28 + wish 45 progress capped at 1), so two locked weapons are not
        // interchangeable and the solver tries both orderings.
        public readonly List<int> Weapons = new List<int>();
        public int Head, Chest, Legs, Boots;
        public readonly List<int> Accessories = new List<int>();
        public readonly List<GearLockIssue> Issues = new List<GearLockIssue>();

        // How many locked ids actually took a slot. 0 with a non-empty request means every lock was
        // refused, which is the case a caller must not mistake for "no locks were asked for".
        public int Applied;

        public bool IsEmpty { get { return Applied == 0; } }
        public bool HasIssues { get { return Issues.Count > 0; } }

        // Every slot the character owns is held by a lock, so there is nothing left to optimise. NOT
        // an error: the solve still runs, still scores, and returns exactly the set that was asked for.
        public bool FillsEverySlot(GearLockCapacity cap)
        {
            return Weapons.Count >= cap.Weapons && Head != 0 && Chest != 0 && Legs != 0 && Boots != 0
                && Accessories.Count >= cap.Accessories;
        }

        // Is this exact id already seated by the lock? Asked by the respawn pin, which must not
        // "pin" an item that is already worn — that is a duplicate, not a second respawn source.
        public bool Holds(int id)
        {
            if (id == 0) return false;
            if (Head == id || Chest == id || Legs == id || Boots == id) return true;
            for (int i = 0; i < Weapons.Count; i++) if (Weapons[i] == id) return true;
            for (int i = 0; i < Accessories.Count; i++) if (Accessories[i] == id) return true;
            return false;
        }

        public int SlotsHeld
        {
            get
            {
                int n = Weapons.Count + Accessories.Count;
                if (Head != 0) n++;
                if (Chest != 0) n++;
                if (Legs != 0) n++;
                if (Boots != 0) n++;
                return n;
            }
        }

        // One line for the log / the companion, or null when everything the user asked for landed.
        // Sentence case (a report, not a label); no markup, because the companion escapes whatever
        // arrives and an item name comes from the game's own table.
        public string Message
        {
            get
            {
                if (Issues.Count == 0) return null;
                var parts = new string[Issues.Count];
                for (int i = 0; i < Issues.Count; i++) parts[i] = Issues[i].Text;
                return "Gear Lock: " + Applied + " of " + (Applied + Issues.Count) + " locked items are being worn — "
                     + string.Join("; ", parts) + ".";
            }
        }

        private void Add(string kind, int id, string text)
        {
            var issue = new GearLockIssue();
            issue.Id = id; issue.Kind = kind; issue.Text = text;
            Issues.Add(issue);
        }

        private static string Label(int id, string name)
        {
            return string.IsNullOrEmpty(name)
                ? "item " + id.ToString(CultureInfo.InvariantCulture)
                : "[" + id.ToString(CultureInfo.InvariantCulture) + "] \"" + name + "\"";
        }

        // Assign each locked id to a slot, in the order the user wrote them, refusing (with a reason)
        // anything that cannot be honoured. `lookup` is the live half and is the ONLY thing this needs
        // from the game.
        public static GearLockPlan Resolve(IEnumerable<int> ids, Func<int, GearLockItem> lookup, GearLockCapacity cap)
        {
            var plan = new GearLockPlan();
            if (ids == null || lookup == null) return plan;

            var seen = new HashSet<int>();
            foreach (var id in ids)
            {
                if (id <= 0)
                {
                    plan.Add(GearLockIssue.Invalid, id,
                        id.ToString(CultureInfo.InvariantCulture) + " is not an item ID");
                    continue;
                }
                if (!seen.Add(id))
                {
                    // The game equips ONE copy of a given id at a time even when you own several, so a
                    // repeat cannot take a second slot. Said out loud rather than deduped in silence.
                    plan.Add(GearLockIssue.Duplicate, id, Label(id, "") + " is listed twice");
                    continue;
                }

                GearLockItem it;
                try { it = lookup(id); }
                catch { it = GearLockItem.Missing(); }

                if (!it.Known)
                {
                    plan.Add(GearLockIssue.Unknown, id,
                        "there is no wearable item with ID " + id.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                if (!it.Owned)
                {
                    plan.Add(GearLockIssue.NotOwned, id, Label(id, it.Name) + " isn't in your inventory");
                    continue;
                }

                switch (it.Slot)
                {
                    case GearLockSlot.Weapon:
                        if (plan.Weapons.Count < cap.Weapons) { plan.Weapons.Add(id); plan.Applied++; }
                        else plan.Add(GearLockIssue.NoRoom, id, Label(id, it.Name) + " doesn't fit — " +
                            (cap.Weapons <= 1
                                ? "you only have one weapon slot until wish 28 unlocks the offhand"
                                : "both weapon slots are already locked"));
                        break;
                    case GearLockSlot.Head:
                        if (plan.Head == 0) { plan.Head = id; plan.Applied++; }
                        else plan.Add(GearLockIssue.NoRoom, id, Label(id, it.Name) + " doesn't fit — the head slot is already locked");
                        break;
                    case GearLockSlot.Chest:
                        if (plan.Chest == 0) { plan.Chest = id; plan.Applied++; }
                        else plan.Add(GearLockIssue.NoRoom, id, Label(id, it.Name) + " doesn't fit — the chest slot is already locked");
                        break;
                    case GearLockSlot.Legs:
                        if (plan.Legs == 0) { plan.Legs = id; plan.Applied++; }
                        else plan.Add(GearLockIssue.NoRoom, id, Label(id, it.Name) + " doesn't fit — the legs slot is already locked");
                        break;
                    case GearLockSlot.Boots:
                        if (plan.Boots == 0) { plan.Boots = id; plan.Applied++; }
                        else plan.Add(GearLockIssue.NoRoom, id, Label(id, it.Name) + " doesn't fit — the boots slot is already locked");
                        break;
                    default:
                        // OVER-LOCKING, the case the user is most likely to hit: five locked
                        // accessories against four unlocked slots. The overflow is named, and it is
                        // the LAST ones written that overflow, so the order the user typed is the
                        // priority order — the same rule the digger and boost lists already use.
                        if (plan.Accessories.Count < cap.Accessories) { plan.Accessories.Add(id); plan.Applied++; }
                        else plan.Add(GearLockIssue.NoRoom, id, Label(id, it.Name) + " doesn't fit — you have " +
                            (cap.Accessories == 0
                                ? "no accessory slots"
                                : cap.Accessories.ToString(CultureInfo.InvariantCulture) + " accessory slot"
                                  + (cap.Accessories == 1 ? "" : "s") + " and they are all locked"));
                        break;
                }
            }

            return plan;
        }
    }
}
