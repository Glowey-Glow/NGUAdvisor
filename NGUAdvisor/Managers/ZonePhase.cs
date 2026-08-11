using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE THREE-PHASE ZONE RULE. Unity-free by construction — plain-old data in, a phase out, no
    // Main.Character, no file IO, no writes. The ZoneGate / GearFarmPause / FeasibilityPass pattern:
    // the DECISION is extracted so it can be linked into the net9.0 test project, while the live
    // reads stay in AdvisorApply (the writer that already owns Settings.SnipeZone).
    //
    // [OPERATOR], stating the guide's rule: "the user would idle in the zone until they collect at
    // least one copy of the accessories. after that it would go back to itopod farming until the user
    // could one-hit for faster farming."
    //
    //   IDLE    enter: zone unlocked AND idle P/T met      leave: one copy of EACH accessory held
    //   ITOPOD  enter: accessories held AND one-hit NOT met leave: attack >= the zone's OPower
    //   FARM    enter: one-hit met AND the set not maxed    leave: itemMaxxed on every zone item
    //
    // WHAT WAS MISSING. AdvisorApply.ApplyZones jumped straight to FARM: GearFarmAdvisor.Analyze
    // gates on one-shottable (GearFarmAdvisor.cs:368-375) and ApplyZones writes g.Best.Zone
    // (AdvisorApply.cs:788-793). The IDLE phase and the return-to-ITOPOD phase did not exist.
    //
    // THIS IS A CLASSIFIER, NOT A LATCHED FSM. Every condition above is a predicate on CURRENT state,
    // so the phase is a pure function of the tick and nothing is remembered between calls. That is
    // deliberate and it is the same rule FeasibilityPass §4.5 states: this codebase has already
    // shipped both failure modes of remembering (ResourceBreakpoint froze hack ordering at parse
    // time; ChallengeOverlay cached its parsed list for a session). A phase must not stay entered
    // after its condition lifts. The caller owns the one thing that IS state — the last phase
    // announced — for surfacing only.
    //
    // ── ITOPOD IS ZONE 1000, NOT A SECOND SYSTEM ──────────────────────────────────────────────────
    // Main.cs:1400 resolves gear-hunt > AdventureTargetITOPOD > Settings.SnipeZone into one tempZone,
    // and Main.cs:1444 calls ITOPODManager.Update() iff tempZone >= 1000. So all three phases write
    // the SAME channel and the machine only ever chooses a zone NUMBER. See audit/40 §0.
    //
    // ── "ACCESSORY" IS A SLOT TYPE ────────────────────────────────────────────────────────────────
    // [DECOMP] part.cs:1-15 — the enum is Head, Chest, Legs, Boots, Weapon, Accessory, atkBoost,
    // defBoost, specBoost, None, Misc, MacGuffin. Accessory is ordinal 5.
    // [DECOMP] Equipment.cs:570-577 — isEquipment() is exactly {Head, Chest, Legs, Boots, Weapon,
    // Accessory}, i.e. ordinals 0..5, which is why GearFarmAdvisor.cs:296 tests `(int)type[id] <= 5`.
    // The per-id types are NOT Unity-serialized: [DECOMP] ItemNameDesc.cs:92 constructItemInfo()
    // assigns type[id] in code for all 515 typed ids, so the mapping is readable statically as well
    // as live. VERIFIED against GearFarmAdvisor's drop table: every farmable zone except zone 0 drops
    // between 1 and 5 accessories, and every one of them also drops non-accessory gear — so the rule
    // names a strictly smaller set than "the zone's gear" and is well-formed. Zone 0 drops exactly one
    // gear id (75, "A Stick", a Weapon) and NO accessory; its IDLE phase is vacuously complete.
    //
    // ── "HELD" IS NOT "DROPPED" ───────────────────────────────────────────────────────────────────
    // ⚠ The obvious read, itemDropped[id], is WRONG. [DECOMP] ItemNameDesc.cs:9915 calls
    // markItemAsDropped(id) BEFORE the loot-filter check at :9919 — and :10003/:10004 do the same for
    // markItemAsMaxxed inside makeLevelledLoot, before its filter check at :10012. A filtered drop
    // therefore sets itemDropped (and can set itemMaxxed) for an item that never entered the
    // inventory. itemDropped is "the game rolled this for you", which is not what the operator asked
    // for. HELD means a copy is in equipment or inventory — LoadoutManager.FindItemSlot scans
    // GetConvertedEquips().Concat(GetConvertedInventory()) (LoadoutManager.cs:395), and that is the
    // read the caller supplies as ItemFact.Held.
    //
    // ── THE QUESTMANAGER TRAP ─────────────────────────────────────────────────────────────────────
    // QuestManager.cs:32-39: Misc ids are not equipment, so the advisor never merges or boosts them
    // (InventoryManager.cs:242), Equipment.level never leaves 0, and markItemAsMaxxed needs
    // level >= 100 ([DECOMP] AllItemListController.cs:144-153) — so itemMaxxed[id] can NEVER become
    // true for a Misc id, and three cooking ids pinned a 3-hour CapstoneHold open forever.
    // Here the equipment guard is structural rather than advisory: part.Misc is ordinal 10, so it
    // fails IsAccessory outright and can never enter the held-set. The guard below is still written
    // explicitly, because the trap cost three hours once and a comment is cheaper than a fourth.
    //
    // ── A FILTERED ACCESSORY CAN NEVER BE COLLECTED ───────────────────────────────────────────────
    // GearFarmAdvisor.cs:383-384 already drops loot-filtered ids from its missing list ("a
    // loot-filtered item never drops"). The same exclusion is mandatory here for a stronger reason:
    // an un-held, filtered accessory would pin IDLE open forever on a drop the game destroys on
    // creation. Excluding it also restores "maxxed implies held" for everything that remains, since
    // the only way to be maxxed without holding a copy is the filtered path above.
    public static class ZonePhase
    {
        // [DECOMP] part.cs:8 — Accessory is ordinal 5, and [DECOMP] Equipment.cs:570-577 makes 0..5
        // the equipment range. Ints rather than the enum because this file must compile without the
        // game assembly.
        public const int SlotAccessory = 5;
        public const int MaxEquipmentSlot = 5;

        // Main.cs:1400 / :1444 — the ITOPOD is zone 1000 on the same channel as every other zone.
        public const int ItopodZone = 1000;

        public enum Phase
        {
            // The machine declines: it has no opinion, and the caller must fall through to the
            // boost/ITOPOD path exactly as it would with the gear farm switched off. NOT a phase.
            None = 0,
            Idle,
            Itopod,
            Farm
        }

        // One zone-droppable item, as the caller reads it. Slot/Filtered/Maxxed/Held are four
        // independent live reads; nothing here infers one from another.
        public struct ItemFact
        {
            public int Id;
            public int Slot;        // (int)Main.Character.itemInfo.type[id]
            public bool Filtered;   // inventory.itemList.itemFiltered[id]
            public bool Maxxed;     // inventory.itemList.itemMaxxed[id]
            public bool Held;       // LoadoutManager.FindItemSlot(id) != null
        }

        public static bool IsEquipment(int slot) => slot >= 0 && slot <= MaxEquipmentSlot;

        public static bool IsAccessory(int slot) => slot == SlotAccessory;

        // The predicate nobody had written: do I hold at least one copy of this accessory?
        // Maxxed counts only because Filtered items are excluded before this is asked — see the
        // filtered note in the class comment.
        public static bool Collected(ItemFact f) => f.Held || f.Maxxed;

        public struct Input
        {
            public int Zone;
            public bool ZoneUnlocked;

            // ── the one-hit gate. Supplied from ZoneGate.Evaluate, NOT read from the table here:
            // an unknown zone is NOT one-shottable and OneShotKnown carries that (ZoneGate.cs:48-60).
            public bool OneShotKnown;
            public bool OneShottable;
            public double OPower;
            public string OneShotReason;   // ZoneGate's verbatim reason when !OneShotKnown

            // ── the idle gate. A DIFFERENT, LOWER bar than one-hit, and on DIFFERENT PROVENANCE:
            // OPower is derived from the decomp (ZoneStatHelper.cs:152-196), but IPower/IToughness
            // are MEASURED wiki values with no derivation rule — ZoneStatHelper.cs:197-203, "ABSENT
            // from the decomp — do not 'derive' them", and zone 43's four are a labelled guide
            // estimate (:584-586). IdleThresholdKnown must be false when the row is missing or either
            // value is non-positive: zoneOverride.json is user-editable and AsDouble on an absent key
            // yields 0, which is the exact fail-open shape ZoneGate was built to close (ZoneGate.cs:16-18).
            public bool IdleThresholdKnown;
            public double IPower;
            public double IToughness;

            public double Attack;    // ZoneStatHelper.EffectiveAdvAttack() — beast mode divided out
            public double Defense;   // Character.totalAdvDefense()

            // Every gear id the zone can drop, with its four live reads.
            public IList<ItemFact> Items;
        }

        // A decision that routes always carries a zone; a decision that declines always carries a
        // reason. Enforced by the constructors being the only doors, as FeasibilityPass.Verdict does.
        public struct Decision
        {
            private readonly Phase _phase;
            private readonly int _zone;
            private readonly string _reason;

            private Decision(Phase phase, int zone, string reason)
            {
                _phase = phase;
                _zone = zone;
                _reason = reason;
                AccessoriesHeld = 0;
                AccessoryCount = 0;
                Parked = false;
                OneHitGap = 0;
                Attack = 0;
                OPower = 0;
            }

            public Phase Phase => _phase;

            // The zone to write: the zone itself for IDLE and FARM, 1000 for ITOPOD, -1 for None.
            public int TargetZone => _zone;

            // default(Decision) is Phase.None with a reason, i.e. fail-CLOSED — the machine that was
            // never evaluated has no opinion, it does not route to zone 0.
            public string Reason => _reason ?? "not evaluated";

            public int AccessoriesHeld { get; private set; }
            public int AccessoryCount { get; private set; }

            // The phase is waiting on something it does not itself advance. True in ITOPOD, where the
            // machine parks until adventure Power clears the gap — indefinitely if it never does.
            // Correct behaviour, and it MUST be visible: a farm that stops without saying so is
            // indistinguishable from a farm that broke (amendment 25 §4, found at two hours' cost).
            public bool Parked { get; private set; }

            // P3c: the gap to one-hit, in the SAME units ZoneGate compares — adventure attack with
            // beast mode divided out (ZoneGate.cs:42, ZoneStatHelper.cs:94-98). Zero when one-hit is
            // met or when the threshold is unknown.
            public double OneHitGap { get; private set; }
            public double Attack { get; private set; }
            public double OPower { get; private set; }

            internal static Decision Decline(string reason) =>
                new Decision(Phase.None, -1, reason ?? "no reason given");

            internal static Decision Route(Phase phase, int zone, string reason) =>
                new Decision(phase, zone, reason ?? "no reason given");

            internal Decision With(int held, int count)
            {
                AccessoriesHeld = held;
                AccessoryCount = count;
                return this;
            }

            internal Decision WithGap(double attack, double oPower, bool parked)
            {
                Attack = attack;
                OPower = oPower;
                OneHitGap = oPower > attack ? oPower - attack : 0;
                Parked = parked;
                return this;
            }
        }

        // The zone's accessories, after both exclusions: non-equipment slots (the QuestManager trap)
        // and loot-filtered ids (destroyed on creation, so never collectable).
        public static List<ItemFact> Accessories(IList<ItemFact> items)
        {
            var acc = new List<ItemFact>();
            if (items == null) return acc;
            foreach (var f in items)
            {
                if (!IsEquipment(f.Slot)) continue;   // structural: Misc (10) can never qualify
                if (!IsAccessory(f.Slot)) continue;
                if (f.Filtered) continue;             // the game destroys the drop; it can never be held
                acc.Add(f);
            }
            return acc;
        }

        // Every equipment id the zone drops is capped — the FARM leave condition. Filtered ids are
        // excluded for the same reason they are excluded from the accessory set: GearFarmAdvisor
        // already drops them from `missing` (GearFarmAdvisor.cs:383-384), so a filtered id must not
        // be able to hold FARM open against a set the player has actually finished.
        // Any equipment id the zone can actually drop (loot-filtered ids are destroyed on creation,
        // so they are not droppable in any sense that matters here).
        public static bool HasFarmableEquipment(IList<ItemFact> items)
        {
            if (items == null) return false;
            foreach (var f in items)
                if (IsEquipment(f.Slot) && !f.Filtered) return true;
            return false;
        }

        public static bool SetMaxxed(IList<ItemFact> items)
        {
            if (items == null) return false;
            bool any = false;
            foreach (var f in items)
            {
                if (!IsEquipment(f.Slot)) continue;
                if (f.Filtered) continue;
                any = true;
                if (!f.Maxxed) return false;
            }
            return any;   // a zone with no farmable equipment is not "maxxed", it is not a farm target
        }

        public static Decision Decide(Input z)
        {
            if (!z.ZoneUnlocked)
                return Decision.Decline($"zone {z.Zone} is not unlocked");

            // A zone with no farmable equipment is not a phase target at all. Without this, an empty
            // item list would make "every accessory held" vacuously TRUE and park the machine in
            // ITOPOD on a zone that drops nothing — the accessory count and the evidence for it being
            // zero are the same number, and only one of those is a reason to move.
            if (!HasFarmableEquipment(z.Items))
                return Decision.Decline($"zone {z.Zone} drops no farmable equipment");

            var acc = Accessories(z.Items);
            int held = 0;
            string firstMissing = null;
            foreach (var f in acc)
            {
                if (Collected(f)) held++;
                else if (firstMissing == null) firstMissing = f.Id.ToString();
            }
            bool accessoriesHeld = held >= acc.Count;

            // FARM's leave condition outranks everything: a capped set is not a farm target at all,
            // and the machine must hand routing back rather than pin the zone.
            if (SetMaxxed(z.Items))
                return Decision.Decline($"every item zone {z.Zone} drops is capped").With(held, acc.Count);

            // FAIL CLOSED. An unknown zone is NOT one-shottable — the whole reason ZoneGate exists
            // (ZoneGate.cs:7-23; zone 43 hit this for real and won the Sadistic ranking at any attack).
            bool oneHit = z.OneShotKnown && z.OneShottable;

            // ── FARM ──────────────────────────────────────────────────────────────────────────────
            // One-hit met and the set not capped. This is checked BEFORE the accessory count on
            // purpose: with one-hit met, IDLE's stay-condition ("accessories not all held") and
            // FARM's enter-condition are both satisfiable at once, and the operator's rule does not
            // separate them. The tie is immaterial to routing — IDLE and FARM write the SAME zone
            // number, so only the surfaced line differs — and FARM is the truthful label, because at
            // one-hit the character is farming the zone at full cadence and collecting the missing
            // accessories FASTER than idling would. Recorded here rather than decided silently.
            if (oneHit)
            {
                var d = Decision.Route(Phase.Farm, z.Zone,
                    accessoriesHeld
                        ? "one-hit met, set not capped"
                        : $"one-hit met, set not capped (accessories {held}/{acc.Count} — farming outpaces idling)");
                return d.With(held, acc.Count).WithGap(z.Attack, z.OPower, false);
            }

            // ── ITOPOD ────────────────────────────────────────────────────────────────────────────
            // Accessories held, one-hit not met. Parks here until Power clears the gap — possibly
            // forever, which is correct and which the caller MUST surface (P3b).
            if (accessoriesHeld)
            {
                string why = z.OneShotKnown
                    ? $"accessories held, one-hit not met for zone {z.Zone}"
                    : $"accessories held, one-hit unknown for zone {z.Zone} — {z.OneShotReason ?? "no reason given"}";
                var d = Decision.Route(Phase.Itopod, ItopodZone, why);
                // The gap is only meaningful when the threshold is known; an unknown zone still parks,
                // it just cannot say how far.
                return d.With(held, acc.Count)
                        .WithGap(z.Attack, z.OneShotKnown ? z.OPower : 0, true);
            }

            // ── IDLE ──────────────────────────────────────────────────────────────────────────────
            // Accessories missing. Requires the zone be idleable at all, which is a DIFFERENT and
            // LOWER bar than one-hit, on different provenance (see Input.IdleThresholdKnown).
            if (!z.IdleThresholdKnown)
                return Decision.Decline($"zone {z.Zone} idle threshold unknown — no row, or a non-positive IPower/IToughness")
                    .With(held, acc.Count);

            if (!(z.Attack >= z.IPower && z.Defense >= z.IToughness))
                return Decision.Decline($"zone {z.Zone} idle Power/Toughness not met")
                    .With(held, acc.Count);

            return Decision.Route(Phase.Idle, z.Zone,
                    $"collecting accessories ({held}/{acc.Count} held, missing id {firstMissing})")
                .With(held, acc.Count)
                .WithGap(z.Attack, z.OneShotKnown ? z.OPower : 0, false);
        }

        // ── THE CANDIDATE ZONE ────────────────────────────────────────────────────────────────────
        // ⚠ WHY THIS EXISTS AT ALL, AND WHY IT IS NOT GearFarmAdvisor'S JOB.
        // GearFarmAdvisor.Analyze DISCARDS every zone that is not already one-shottable —
        // GearFarmAdvisor.cs:375 `if (!gate.OneShottable) continue;` sits above `plans.Add(plan)` at
        // :415, and both v.Best and v.Nearest come off `plans` (:421-423). So the gear farm can only
        // ever hand out a FARM-phase zone. Running this machine on g.Best would make Decide return
        // FARM on every call and leave IDLE and ITOPOD as unreachable code — which is precisely the
        // two phases that were missing. IDLE needs a zone the gear farm structurally cannot name.
        //
        // THE RANKING RULE IS AN [OPERATOR] DECISION, 2026-08-05: the HIGHEST unlocked zone that
        // meets idle Power/Toughness and still has an un-held accessory. That is the guide's ladder —
        // push as high as you can idle, collect that zone's accessories, drop to the ITOPOD until you
        // one-hit it, then farm it. Two alternatives were put and declined: ascending order (never
        // skip a set) and extending GearFarmAdvisor with an idle-cadence rate model (no decomp source
        // for idle kill cadence exists).
        //
        // FARM IS NOT DECIDED HERE. GearFarmAdvisor ranks farm targets by HoursToCap against a time
        // budget (:396, :421); picking a farm zone by height would contradict that ranking. ApplyZones
        // calls Plan only when the gear farm has no target, so the two never both choose.
        public static Decision Plan(IList<Input> zones) => Explain(zones).Chosen;

        // WHY THE MACHINE DID NOTHING — the counts behind a decline.
        //
        // ⚠ THIS EXISTS BECAUSE A DECLINE IS INVISIBLE. Plan returning None is a legitimate, common
        // outcome: routing falls through to the boost farm and the advisor behaves as it would with
        // Farm Gear Zones off. But it emits no line, so "correctly declining" and "silently broken"
        // look identical from the log — the 25 §4 shape the phase surfacing was built to prevent,
        // reappearing one level up. Observed live: the machine ran for 3.5 h across two builds and
        // never emitted a single line, and nothing in the log could say whether that was right.
        //
        // FarmReady is the count that matters most. Plan deliberately never chooses a FARM zone
        // (GearFarmAdvisor owns that ranking by HoursToCap), so a save where every candidate is
        // already one-shottable produces: many FarmReady, zero Idle, zero Parked, and a decline —
        // while GearFarmAdvisor separately returns no target because nothing caps inside its 3 h
        // budget. Both components are correct and the pair is silent. The counts make that legible.
        public struct PlanReport
        {
            public int Candidates;
            public int FarmReady;    // resolve to FARM; Plan ignores them by design
            public int Idle;
            public int Parked;
            public int Declined;
            public string TopDeclineReason;
            public Decision Chosen;

            public string Summary()
            {
                var s = $"{Candidates} candidate zone(s): {FarmReady} farm-ready (gear farm owns those), "
                      + $"{Idle} idle, {Parked} parked, {Declined} declined";
                if (!string.IsNullOrEmpty(TopDeclineReason)) s += $" — e.g. {TopDeclineReason}";
                return s;
            }
        }

        public static PlanReport Explain(IList<Input> zones)
        {
            var r = new PlanReport();
            if (zones == null || zones.Count == 0)
            {
                r.Chosen = Decision.Decline("no candidate zones");
                return r;
            }

            r.Candidates = zones.Count;

            var best = Decision.Decline("no zone is idleable with an accessory outstanding");
            int bestZone = int.MinValue;
            var parked = Decision.Decline("no zone is on the gear ladder");
            int parkedZone = int.MinValue;

            foreach (var z in zones)
            {
                var d = Decide(z);
                switch (d.Phase)
                {
                    case Phase.Farm:
                        r.FarmReady++;
                        break;
                    case Phase.Idle:
                        r.Idle++;
                        if (z.Zone > bestZone) { best = d; bestZone = z.Zone; }
                        break;
                    case Phase.Itopod:
                        r.Parked++;
                        // Among zones already collected but not yet one-hittable, the HIGHEST is the
                        // one the ladder is climbing toward, so its gap is the one worth reporting.
                        if (z.Zone > parkedZone) { parked = d; parkedZone = z.Zone; }
                        break;
                    default:
                        r.Declined++;
                        if (r.TopDeclineReason == null) r.TopDeclineReason = d.Reason;
                        break;
                }
            }

            // IDLE outranks ITOPOD: collecting an accessory is progress the character controls, and
            // parking is what you do when there is none left to collect.
            r.Chosen = bestZone > int.MinValue ? best : parked;
            return r;
        }

        // P2e — THE DRIVE SWITCH. AdvisorZones is the advise/drive toggle and it already exists:
        // "advisor points SnipeZone at the best boost farm; off = the manual zone dropdown owns
        // SnipeZone" (SavedSettings.cs:2171-2173). With the toggle off the machine may still be
        // evaluated and surfaced, but nothing it decides reaches Settings.SnipeZone.
        //
        // Note the live path is STRICTLY STRONGER than this gate: AdvisorApply.cs:753 returns before
        // the gear-farm branch is reached at all, so with the toggle off the machine is not even
        // evaluated (and GearFarmAdvisor.Analyze, which is not cheap, does not run either). This
        // function is the decision itself, so the rule is asserted somewhere other than a comment.
        public static bool WritesZone(Decision d, bool advisorZones)
            => advisorZones && d.Phase != Phase.None && d.TargetZone >= 0;

        // ── THE DROP-FARM DEMAND, AS A PREDICATE ──────────────────────────────────────────────────
        // IDLE is a drop farm by definition — "idle in the zone until you collect at least one copy
        // of the accessories". ITOPOD is the opposite venue and None takes no routing at all, so both
        // hand the demand back. Three subsystems read FarmVenue.DropFarmActive (audit/41 §1.1):
        // ROUTING — the demand is what beats Settings.AdventureTargetITOPOD at audit/40 R10
        // (Main.ResolveIntentZone) — the DC/PP digger law, and the Loot Hunter gear objective.
        //
        // ⚠ A PREDICATE SO THE ANNOUNCEMENT CANNOT DRIFT FROM THE DEMAND. audit/40 §6.1 made a drop
        // farm outrank Target ITOPOD and required it to SAY SO: "§3's whole point is that silent
        // contention is the defect; a fix that overrode silently would just invert it." The set, rare
        // and FARM lines all carry that note. IDLE was added by the same campaign, raises the same
        // demand, and carried nothing — so the one phase whose entire purpose is to stand in a zone
        // overrode the operator's own toggle without a word. Same fact, one expression, both uses.
        //
        // The zone bounds are belt-and-braces rather than reachable: ZonePhaseReader.Candidates()
        // is built from GearFarmAdvisor.FarmZones, which are all real zones, and WritesZone has
        // already rejected TargetZone < 0 by the time this is asked.
        public static bool RaisesDropFarmDemand(Decision d)
            => d.Phase == Phase.Idle && d.TargetZone >= 0 && d.TargetZone < 1000;

        // ── SURFACING ─────────────────────────────────────────────────────────────────────────────
        // P3a: one line per TRANSITION, naming the phase, the zone and the condition that fired.
        // Transitions are discrete and rare, so they are NOT state-change-throttled into silence
        // beyond the transition itself — the caller compares against Signature().

        // The transition identity. Phase plus zone: moving the farm from zone 20 to zone 21 in the
        // same phase IS a transition worth a line, because the zone is what the operator sees.
        public static string Signature(Decision d) =>
            d.Phase == Phase.None ? null : d.Phase + "#" + d.TargetZone;

        public static bool ShouldSurface(string signature, string lastSignature) =>
            (signature ?? "") != (lastSignature ?? "");

        public static string Message(Decision d, string zoneName)
        {
            string where = string.IsNullOrEmpty(zoneName) ? $"zone {d.TargetZone}" : zoneName;
            switch (d.Phase)
            {
                case Phase.Idle:
                    return $"zone phase -> IDLE {where} ({d.Reason})";
                case Phase.Farm:
                    return $"zone phase -> FARM {where} ({d.Reason})";
                case Phase.Itopod:
                    // P3b + P3c. The gap is stated in the line itself because this phase can park
                    // indefinitely, and a single line at hour zero followed by silence is exactly the
                    // shape that is indistinguishable from a stuck advisor.
                    return $"zone phase -> ITOPOD ({d.Reason}); {GapText(d)}";
                default:
                    return $"zone phase -> none ({d.Reason})";
            }
        }

        // The one-hit gap in ZoneGate's units. Kept separate so the caller can surface it without
        // re-deriving it, and so the "unknown" case cannot silently render as a zero gap.
        public static string GapText(Decision d)
        {
            if (d.OPower <= 0)
                return "one-hit gap unknown — no OPower for this zone";
            if (d.OneHitGap <= 0)
                return "one-hit met";
            return $"parked until Power reaches {d.OPower:0.###e+0} (have {d.Attack:0.###e+0}, "
                 + $"short {d.OneHitGap:0.###e+0} — {d.Attack / d.OPower * 100:0.#}% of the way)";
        }
    }
}
