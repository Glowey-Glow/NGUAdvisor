using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // The one-shot gate, single-sourced so the boost and gear farm advisors cannot drift apart.
    //
    // WHY THIS EXISTS. Both advisors used to write the gate inline as:
    //
    //     if (ZoneStatHelper.UserOverrides != null &&
    //         ZoneStatHelper.UserOverrides.TryGetValue(zone, out var st))
    //         if (st.OPower > 0 && attack < st.OPower) continue;
    //
    // which has THREE separate fail-OPEN paths, all silent:
    //   1. UserOverrides == null   (zone file not loaded yet)      -> gate skipped entirely
    //   2. TryGetValue misses      (no row for that zone)          -> gate skipped entirely
    //   3. st.OPower <= 0          (typo'd/absent key in the JSON
    //                               override -> AsDouble gives 0)  -> gate skipped entirely
    // In each case the zone is then treated as one-shottable at ANY attack and ranked on a per-kill
    // value model that assumes a cadence the player cannot actually sustain. Zone 43 hit path 2 for
    // real: it had no row in ZoneStatHelper.Defaults, so it won the Sadistic ranking the moment its
    // boss gate opened. audit/01 §"Why not keep" row 2; audit/09 BoostFarm §8 MED, GearFarm §8 MED.
    //
    // An unknown zone is now NOT one-shottable, and says so once per zone per session.
    public static class ZoneGate
    {
        public struct Decision
        {
            // True only when a real row was found AND the attack clears it.
            public bool OneShottable;

            // False when the table or the row was missing, or the row's OPower was non-positive.
            // Known == false always implies OneShottable == false — that IS the fail-closed rule.
            public bool Known;

            // Non-null exactly when Known is false; ready to log verbatim.
            public string Reason;
        }

        // tableLoaded: the zone table exists at all (ZoneStatHelper.UserOverrides != null).
        // rowFound:    a row for this zone was present in it.
        // oPower:      that row's OPower (ignored when !rowFound).
        // attack:      ZoneStatHelper.EffectiveAdvAttack(), i.e. beast-mode divided out.
        //
        // >= matches the farm advisors' historical "attack < OPower -> skip" sense. Note that
        // CombatManager.ZoneEntryHpThreshold uses strict > against the same table (audit/09 LOW);
        // that divergence is untouched here and is immaterial in practice because the table rounds
        // OPower UP, so no real attack value lands exactly on a threshold.
        public static Decision Evaluate(bool tableLoaded, bool rowFound, double oPower, double attack)
        {
            if (!tableLoaded)
                return new Decision { OneShottable = false, Known = false, Reason = "zone table not loaded" };

            if (!rowFound)
                return new Decision { OneShottable = false, Known = false, Reason = "no row in the zone stat table" };

            if (oPower <= 0)
                return new Decision { OneShottable = false, Known = false, Reason = "row has a non-positive OPower" };

            return new Decision { OneShottable = attack >= oPower, Known = true, Reason = null };
        }

        // ---- the SAME rule, for the fight-type read ------------------------------------------
        //
        // audit/40 §4 recorded the divergence and deliberately did not fix it: ZoneStatHelper
        // .ZoneFightType returned 2 — the BEST fight type — for a zone with no row, "so unknown
        // zones never block progress". That is fail-OPEN, out of the same table, in the same file,
        // for the same reason path 2 above exists. Zone 43 is the proof that it is not theoretical:
        // it had no row, so it read as clearable at any attack and won the Sadistic ranking.
        //
        // Resolved to ZoneGate's rule so the two cannot drift apart again: a zone with no usable row
        // is NOT clearable. FightType 0 is the game's own "we can't do the zone" (ZoneStatHelper
        // .ZoneStats.FightType), so the fail-closed answer needs no new vocabulary.
        public struct FightTypeDecision
        {
            // 0 whenever Known is false — that IS the fail-closed rule, stated in the type.
            public int FightType;

            // False when the table or the row was missing.
            public bool Known;

            // Non-null exactly when Known is false; ready to log verbatim.
            public string Reason;
        }

        // rowFightType is the row's own answer at current stats (ZoneStats.FightType); it is only
        // read when rowFound, exactly as oPower is above.
        public static FightTypeDecision EvaluateFightType(bool tableLoaded, bool rowFound, int rowFightType)
        {
            if (!tableLoaded)
                return new FightTypeDecision { FightType = 0, Known = false, Reason = "zone table not loaded" };

            if (!rowFound)
                return new FightTypeDecision { FightType = 0, Known = false, Reason = "no row in the zone stat table" };

            return new FightTypeDecision { FightType = rowFightType, Known = true, Reason = null };
        }

        // These advisors run on a 1 Hz display path (audit/09: BoostFarmAdvisor.Analyze executes
        // ~86,400x/day), so an unconditional log on a missing row would emit a line per zone per
        // second. First occurrence per (caller, zone) only.
        private static readonly HashSet<string> Announced = new HashSet<string>();

        public static bool ShouldAnnounce(string caller, int zone)
        {
            lock (Announced)
                return Announced.Add(caller + "#" + zone);
        }

        // Test seam: the announce latch is process-wide, so a test that asserts first-time-only
        // behaviour needs to be able to clear it.
        public static void ResetAnnouncements()
        {
            lock (Announced)
                Announced.Clear();
        }
    }
}
