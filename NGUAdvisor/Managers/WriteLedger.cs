using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // WHAT THE ADVISOR HAS SET ON YOUR BEHALF, AND WHETHER IT IS STILL TRUE.
    //
    // The defect that produced this file: for twelve hours the advisor wrote an inflated level target
    // onto both Wandoos AT slots once per rebirth, and the product never said a word. It was found by
    // noticing the boxes in the game and then reading debug.log. Three of those steps happen outside
    // the product.
    //
    // ⚠ THIS LEDGER DOES NOT COVER EVERY WRITE, AND SAYS SO ON THE SCREEN. A census of the tree found
    // 129 write sites across 48 files. Promising all of them would be a lie, because five resist
    // interception outright — the rebirth trigger is a reflective call whose whole effect happens
    // inside the game's compiled controller; the transform climb is a non-atomic delete-then-recreate
    // with a rollback; the adventure event log is mutated by index assignment on a reflected list;
    // several reflection sites build their target name from a loop variable; and every energy/magic
    // AMOUNT is staged as a string into a UI text field and committed later on a different object.
    //
    // So the 129 are split three ways by the two questions the census already answers about each site
    // — is it persistent, and does the advisor revert it:
    //
    //   PERSISTENT STATE  (this ledger)  — survives a reload, can go stale, undoing one is meaningful.
    //   IRREVERSIBLE ACTIONS (Activity)  — happened once; there is no field to inspect and nothing to
    //                                      undo, only a record that it occurred.
    //   PER-TICK ALLOCATION (pool board) — reclaimed and re-committed every pass BY DESIGN. A row per
    //                                      tick would be ~86,400 rows a day that all say the same thing.
    //
    // A ledger with unmarked holes is worse than one with marked holes: it grants confidence it has not
    // earned. Hence Registry below — the declared scope is DATA, the UI renders the count from it, and
    // a test asserts that every declared writer is actually instrumented. A gap becomes a failing test
    // rather than a silent omission.
    public enum WriteState
    {
        Active,      // the advisor stands behind it and the live field still agrees
        Stale,       // the live field still holds it, but the reason that justified it has passed
        Reverted,    // the live field no longer holds it — the advisor withdrew, or something else won
        Contested    // more than one writer targets this field, with no arbitration between them
    }

    // One declared writer. The census is the source of this list; nothing is instrumented that is not
    // named here, and nothing named here may go uninstrumented.
    public sealed class WriterSpec
    {
        public string Id;         // stable key for the registry and the tests — NEVER shown to anyone
        public string System;     // what the operator calls the system

        // WHAT THIS IS CALLED IN THE GAME, and the only name the Ledger leads with.
        //
        // Operator feedback 2026-08-11, and correct: a screen that reports what the advisor did to your
        // save has to name things the way the save does. "advancedTraining.levelTarget[2]" is where the
        // value lives in the code; "Advanced Training · Block target" is the box you can go and look at.
        // The first is a debugging aid and belongs in the expanded detail; the second is the row.
        //
        // The Wandoos AT slots are "Energy Dump" and "Magic Dump" because that is what the game's own
        // tooltip calls them ([DECOMP] AdvancedTrainingController.bonusText, ids 3 and 4 — "the levelling
        // speed of Energy Dump in the Wandoos feature"). Slot 2 is Block by the same source.
        public string Game;

        public string Field;      // where the value actually lives — shown only in the expanded detail
        public string Rule;       // the advisor code that owns the decision
        public string Authority;  // where the number comes from: a guide section, an operator ruling, a derivation
        public string[] AlsoWrittenBy;  // other writers of the same field — non-empty means Contested

        // DECLARED BUT NOT YET WIRED. The registry is the SCOPE; this is how much of it is LIVE.
        // Instrumenting eighteen call sites across a dozen files is not one commit, and the alternative
        // to saying so is a screen that silently under-reports — which is the exact failure this feature
        // exists to end. The UI prints "N of 18 · M pending" from these flags, and a test asserts that
        // every NON-pending writer really does have a Record call in the source.
        public bool Pending;
    }

    public sealed class LedgerEntry
    {
        public string WriterId;
        public DateTime At;
        public string Value;        // as written, already formatted for a human
        public string Why;          // the one-sentence reason, in the operator's terms
        public string Segment;      // the run phase it was written in — the usual reason a write goes stale
        public WriteState State;
        public string[] Chain;      // the causal steps, newest last
    }

    public static class WriteLedger
    {
        // ---- the declared scope -------------------------------------------------------------------
        // Ordered roughly by how much trouble a wrong value here has actually caused.
        public static readonly WriterSpec[] Registry =
        {
            new WriterSpec { Id="at.block", System="Advanced Training", Game="Advanced Training · Block target", Field="advancedTraining.levelTarget[2]",
                Rule="LevelPlanner.ApplyPurposeFloor", Authority="operator ruling 2026-08-07", AlsoWrittenBy=new string[0] },
            new WriterSpec { Id="at.wandoos.reclaim", System="Advanced Training", Game="Advanced Training · Energy Dump & Magic Dump targets", Field="advancedTraining.levelTarget[3..4]",
                Rule="LevelPlanner one-shot reclaim", Authority="operator ruling 2026-08-10", AlsoWrittenBy=new string[0] },

            // Two writers, no arbitration beyond LevelPlanner's own deferral. Both hit the same field.
            new WriterSpec { Id="ngu.track.planner", System="NGU", Game="NGU difficulty — Normal / Evil / Sadistic", Field="settings.nguLevelTrack",
                Rule="LevelPlanner.TickNguTrack", Authority="guide ch.5 Evil tail",
                AlsoWrittenBy=new[]{"ngu.track.profile"} },
            new WriterSpec { Id="ngu.track.profile", System="NGU", Game="NGU difficulty — Normal / Evil / Sadistic", Field="settings.nguLevelTrack",
                Rule="NGUDiffBreakpoints.Swap", Authority="your profile's NGUDiff timeline",
                AlsoWrittenBy=new[]{"ngu.track.planner"} },

            // Two writers on very different clocks — one every 60s, one every 0.5s.
            new WriterSpec { Id="blood.spells.advisor", System="Blood Magic", Game="Blood Magic · auto-cast Rebirth, Loot and Gold spells", Field="rebirth/loot/gold AutoSpell",
                Rule="AdvisorApply (60s)", Authority="advisor",
                AlsoWrittenBy=new[]{"blood.spells.quick"} },
            new WriterSpec { Id="blood.spells.quick", System="Blood Magic", Game="Blood Magic · auto-cast Rebirth, Loot and Gold spells", Field="rebirth/loot/gold AutoSpell",
                Rule="Main.QuickStuff (0.5s)", Authority="advisor",
                AlsoWrittenBy=new[]{"blood.spells.advisor"} },

            // Two writers, and one of them wipes the Wandoos dump levels on the way through.
            new WriterSpec { Id="wandoos.os.advisor", System="Wandoos", Game="Wandoos · OS version (98 / Meh / XL)", Field="wandoos98 OS type",
                Rule="AdvisorApply.ApplyWandoosOs", Authority="advisor OS ranking",
                AlsoWrittenBy=new[]{"wandoos.os.profile"} },
            new WriterSpec { Id="wandoos.os.profile", System="Wandoos", Game="Wandoos · OS version (98 / Meh / XL)", Field="wandoos98 OS type",
                Rule="WandoosBreakpoints (reflection)", Authority="your profile's Wandoos breakpoints",
                AlsoWrittenBy=new[]{"wandoos.os.advisor"} },

            new WriterSpec { Id="gear.equipped", System="Gear", Game="Equipped gear", Field="equipped loadout",
                Rule="LoadoutManager.ChangeGear", Authority="advisor objective / lock owner", AlsoWrittenBy=new string[0] },
            // Four advisor paths save over a slot the operator owns, and nothing has ever said so.
            new WriterSpec { Id="gear.slot0", System="Gear", Game="Your saved gear loadout (first slot)", Field="saved loadout slot 0",
                Rule="assignCurrentEquipToLoadout(0)", Authority="advisor, undeclared",
                AlsoWrittenBy=new string[0] },

            new WriterSpec { Id="diggers.active", System="Diggers", Game="Equipped diggers", Field="active digger set",
                Rule="DiggerManager.EquipDiggers", Authority="advisor value ranking",
                AlsoWrittenBy=new string[0] },
            new WriterSpec { Id="beards.active", System="Beards", Game="Equipped beards", Field="active beard set",
                Rule="BeardManager.EquipBeards", Authority="advisor / challenge rule",
                AlsoWrittenBy=new string[0] },

            new WriterSpec { Id="adventure.zone", System="Adventure", Game="Adventure zone", Field="adventure zone",
                Rule="CombatManager.MoveToZone", Authority="routing precedence chain",
                AlsoWrittenBy=new string[0] },
            new WriterSpec { Id="adventure.itopod", System="Adventure", Game="ITOPOD floor range", Field="itopodStart / itopodEnd",
                Rule="ITOPODManager", Authority="advisor floor selection",
                AlsoWrittenBy=new string[0] },

            // No reclaim path anywhere: it outlives the run, the rebirth and the session.
            new WriterSpec { Id="inventory.lootfilter", System="Inventory", Game="Loot filter", Field="itemList.itemFiltered[id]",
                Rule="InventoryManager", Authority="advisor loot filter",
                AlsoWrittenBy=new string[0] },
            new WriterSpec { Id="inventory.cubetarget", System="Inventory", Game="Infinity Cube · auto-convert target", Field="cube conversion target",
                Rule="InventoryManager.selectAuto*Transform", Authority="advisor",
                AlsoWrittenBy=new string[0] },

            new WriterSpec { Id="exp.amounts", System="EXP", Game="EXP shop · custom purchase amounts", Field="settings.custom*Amount ×6",
                Rule="ExpBalancer", Authority="advisor ratio walk",
                AlsoWrittenBy=new string[0] },

            // Reflection with a computed member name — instrumented by hand at the one call site,
            // because nothing keyed on member name could ever find it.
            new WriterSpec { Id="titan.version", System="Titans", Game="Titan difficulty — V1 to V4", Field="adventure.titan{n}Version",
                Rule="ZoneHelpers (reflection)", Authority="advisor kill ladder",
                AlsoWrittenBy=new string[0] }
        };

        public static int LiveCount
        {
            get { int n = 0; foreach (var w in Registry) if (!w.Pending) n++; return n; }
        }

        public static int DeclaredCount { get { return Registry.Length; } }

        public static WriterSpec Spec(string id)
        {
            for (int i = 0; i < Registry.Length; i++) if (Registry[i].Id == id) return Registry[i];
            return null;
        }

        // ---- the live ledger ----------------------------------------------------------------------
        // Current run only (operator choice): rebirth is the natural boundary, most of these reset there
        // anyway, and in-memory means there is no storage format to keep correct. Cleared by Reset().
        private const int MaxEntries = 200;
        private static readonly List<LedgerEntry> _entries = new List<LedgerEntry>();
        private static readonly object _gate = new object();

        public static void Reset()
        {
            lock (_gate) _entries.Clear();
        }

        // One field, one live row. A writer that re-asserts the same value every tick must not produce a
        // row per tick — this is a LEDGER OF STATE, not a log of assignments, and the distinction is the
        // whole reason it is readable. A genuinely new value replaces the row and restarts its clock.
        public static void Record(string writerId, string value, string why, string segment, params string[] chain)
        {
            if (string.IsNullOrEmpty(writerId)) return;
            var spec = Spec(writerId);
            if (spec == null) return;   // undeclared writers are dropped, not silently admitted

            lock (_gate)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].WriterId != writerId) continue;
                    if (_entries[i].Value == value && _entries[i].State != WriteState.Reverted) return;  // unchanged
                    _entries.RemoveAt(i);
                    break;
                }

                _entries.Add(new LedgerEntry
                {
                    WriterId = writerId,
                    At = DateTime.UtcNow,
                    Value = value ?? "",
                    Why = why ?? "",
                    Segment = segment ?? "",
                    State = spec.AlsoWrittenBy.Length > 0 ? WriteState.Contested : WriteState.Active,
                    Chain = chain ?? new string[0]
                });

                while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
            }
        }

        // The advisor withdrew its own value. Distinct from Stale: reverted means the field is back to
        // something the operator owns, which is the outcome an undo would have produced.
        public static void MarkReverted(string writerId)
        {
            lock (_gate)
                foreach (var e in _entries)
                    if (e.WriterId == writerId) e.State = WriteState.Reverted;
        }

        // The value still stands but the reason has passed — the Wandoos AT case exactly. This is the
        // state the whole feature exists to make visible, and nothing else in the product can express it.
        public static void MarkStale(string writerId, string why)
        {
            lock (_gate)
                foreach (var e in _entries)
                    if (e.WriterId == writerId && e.State != WriteState.Reverted)
                    {
                        e.State = WriteState.Stale;
                        if (!string.IsNullOrEmpty(why)) e.Why = why;
                    }
        }

        public static List<LedgerEntry> Snapshot()
        {
            lock (_gate) return new List<LedgerEntry>(_entries);
        }

        public static int CountIn(WriteState s)
        {
            int n = 0;
            lock (_gate) foreach (var e in _entries) if (e.State == s) n++;
            return n;
        }

        public static string StateName(WriteState s)
        {
            switch (s)
            {
                case WriteState.Active:    return "active";
                case WriteState.Stale:     return "stale";
                case WriteState.Reverted:  return "reverted";
                default:                   return "contested";
            }
        }
    }
}
