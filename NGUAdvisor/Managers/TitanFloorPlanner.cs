using System;

namespace NGUAdvisor.Managers
{
    // WHICH TITAN VERSION CAN ACTUALLY BE KILLED RIGHT NOW.
    //
    // The spawn selector used to walk down to the highest version with AutokillAvailable(i, v) — but
    // that reads [DECOMP] autokillTitan{N}V{v}Achieved, a RECORD OF PAST ACHIEVEMENT, not a test of
    // capability. A version you can beat but have never auto-killed reads false, so the walk skipped it
    // and parked lower.
    //
    // That closes a loop the player cannot escape: achieving v2's auto-kill requires fighting v2, and
    // parking on v1 means v2 is never fought. Field symptom: "100% ready for T7 v2 but killing T7 v1"
    // while the v2 floor had 2.4x headroom.
    //
    // So the question is asked properly here: convert the version's STAGED requirement into a gear
    // floor and ask the solver whether any loadout in the inventory clears it. Feasible = killable.
    public static class TitanFloorPlanner
    {
        // A solve is not free and this sits on a 30s tick, so the answer is cached until the objective
        // moves or the throttle expires. The walk stops at the FIRST feasible version, so the common
        // case is one or two solves, not one per version.
        private const double RecheckSeconds = 60.0;
        private static string _key = "";
        private static DateTime _at = DateTime.MinValue;
        private static int _cached;
        private static string _why = "";

        // The highest version <= maxVersion whose staged requirement a loadout in the inventory can
        // actually meet. 0 = could not be determined, and callers MUST treat that as "no opinion" and
        // keep their existing behaviour — never as "nothing is killable", which would park at the bottom.
        public static int HighestKillable(int titanIndex, int maxVersion, out string why)
        {
            why = "";
            if (titanIndex < 0 || maxVersion < 1) return 0;

            string key = titanIndex + ":" + maxVersion;
            DateTime now = DateTime.UtcNow;
            if (_key == key && (now - _at).TotalSeconds < RecheckSeconds) { why = _why; return _cached; }

            int found = 0;
            string reason = "";
            try
            {
                var atk = AdventureFloorReader.Attack();
                var def = AdventureFloorReader.Defence();
                if (!atk.Known && !def.Known) return 0;      // no reading, no opinion

                var adventure = GearOptimizer.FindObjective("Adventure");
                if (adventure == null) return 0;

                for (int v = maxVersion; v >= 1; v--)
                {
                    double reqA, reqD, reqR; string stage;
                    OptimizationAdvisor.StagedRequirementFor(titanIndex, v, out reqA, out reqD, out reqR, out stage);

                    var floors = BuildFloors(reqA, reqD, atk, def);
                    if (floors.IsEmpty) continue;            // nothing expressible: no opinion on this version

                    var res = GearOptimizer.Optimize(adventure, false, null, floors);
                    if (res != null && res.Floors.Feasible)
                    {
                        found = v;
                        reason = "v" + v + " " + stage + " is within reach in best gear";
                        break;
                    }
                }
            }
            catch (Exception e) { Main.LogDebug($"HighestKillable: {e.Message}"); return 0; }

            _key = key; _at = now; _cached = found; _why = reason;
            why = reason;
            return found;
        }

        // HOW MUCH LOOT FITS INSIDE A WINNABLE FIGHT.
        //
        // A live titan fight used to force the "Adventure" objective outright, throwing the loot
        // objective away. That maximises survivability — but survival is a THRESHOLD, not a quantity.
        // Every point of Power above what the kill needs buys nothing, and could have been Drop Chance.
        // Which is exactly a constrained maximisation: maximise loot SUBJECT TO clearing the floor.
        //
        // ⚠ THIS IS THE PATH THAT PRODUCED TWO REPORTED DEATH LOOPS (empty loadout, then drop gear on a
        // live T6v2). Re-admitting a loot objective here is re-admitting the shape of that bug, and the
        // only thing standing between them is whether the floor is right. Hence:
        //   - the UNATTENDED requirement, not the manual one (nobody is at the keyboard);
        //   - a margin on top of it;
        //   - feasibility REQUIRED — infeasible falls back to Adventure, i.e. today's behaviour;
        //   - and a failure to read anything at all also falls back.
        // Survival always wins the tie. Loot is only ever spent from PROVEN surplus.
        public const double SurvivalMargin = 1.20;

        // The requirement an UNATTENDED kill has to clear. Passing zero stats to the staged ladder is
        // deliberate and load-bearing: with atk/def = 0 the ladder can never take its "auto-kill" branch,
        // so a killed version yields the IDLE requirement and an unkilled one the FIRST-KILL requirement.
        // That is the correct bar for a fight nobody is playing — the manual number assumes a player at
        // the keyboard dodging, and these fights happen while the user is away.
        public static GearFloorSet SurvivalFloor(int titanIndex, int version, out string detail)
        {
            detail = "";
            var set = new GearFloorSet();
            try
            {
                var atk = AdventureFloorReader.Attack();
                var def = AdventureFloorReader.Defence();
                if (!atk.Known && !def.Known) return set;    // no reading -> no floor -> caller falls back

                double reqA, reqD, reqR; string stage;
                OptimizationAdvisor.StagedRequirementFor(titanIndex, version, 0, 0,
                                                         out reqA, out reqD, out reqR, out stage);
                set = BuildFloors(reqA * SurvivalMargin, reqD * SurvivalMargin, atk, def);
                detail = $"v{version} {stage} needs {reqA:0.#e0} atk / {reqD:0.#e0} def, +{(SurvivalMargin - 1) * 100:0}% margin";
            }
            catch (Exception e) { Main.LogDebug($"SurvivalFloor: {e.Message}"); return new GearFloorSet(); }
            return set;
        }

        // Requirements are ADVENTURE stats; the solver measures the BRACKET (GearOptimizer.FloorStats
        // scores WornList, which already appends the cube and the nude base). NaN means the conversion
        // cannot be expressed, which must DROP the floor — a zero floor every set clears would read as a
        // satisfied constraint rather than an absent one, and would call an unkillable version killable.
        private static GearFloorSet BuildFloors(double reqA, double reqD,
                                                AdventureFloor.Reading atk, AdventureFloor.Reading def)
        {
            var set = new GearFloorSet();
            if (atk.Known && reqA > 0)
            {
                double need = AdventureFloor.RequiredBracket(reqA, atk.Multiplier);
                if (!double.IsNaN(need)) set.Floors.Add(new GearFloor { Stat = GearObjectives.Stat.Power, Value = need });
            }
            if (def.Known && reqD > 0)
            {
                double need = AdventureFloor.RequiredBracket(reqD, def.Multiplier);
                if (!double.IsNaN(need)) set.Floors.Add(new GearFloor { Stat = GearObjectives.Stat.Toughness, Value = need });
            }
            return set;
        }
    }
}
