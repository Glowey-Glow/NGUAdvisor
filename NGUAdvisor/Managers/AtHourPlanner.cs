using System;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // AT HOUR extension (user feature): near the stock end of AT HOUR, forecast where the AT
    // Power/Toughness levels land if the feed keeps running, and extend the segment — never past
    // the run's 4h mark, so NGU MARATHON still starts on time — when the projection crosses the
    // next titan kill-ladder stage or makes a new zone idle-farmable. Decided ONCE per run near
    // the boundary (the segment engine is time-anchored by law: bounded windows, no
    // re-litigation) and logged either way, so the feed always says what was weighed.
    //
    // Forecast math (the game's own formulas, reference/decomp-full/AdvancedTrainingController.cs):
    //   level speed  dL/dt = R/(L+1) with R = progressPerTick*50*(L+1)
    //     -> closed form L(t) = sqrt((L0+1)^2 + 2Rt) - 1  (levelTarget caps it; -1 pauses it)
    //   totalAdvAttack/Defense carry a (1 + 0.1*L^0.4) AT multiplier (slots 1/0), so
    //     projected stat = reference stat * (1 + 0.1*L(t)^0.4) / (1 + 0.1*L0^0.4).
    //
    // Reference stats (user decision): unbuffed P/T projected onto the optimizer's best
    // Power/Toughness gear (OptimizationAdvisor.ProjectedBestGear) — AT HOUR wears AT-speed
    // gear, and thresholds are met in the kill loadout, not in what happens to be equipped.
    public static class AtHourPlanner
    {
        private const double NormalEnd = 7200;               // stock boundary: 2h into the run
        private const double MaxEnd = 14400;                 // hard cap: the 4h mark
        private const double DecideFrom = NormalEnd - 300;   // decide in the segment's last 5 min
        private const double DecideUntil = NormalEnd + 300;  // …or just past it (TM refill detours)

        private static bool _decided;
        private static double _plannedEnd = NormalEnd;
        private static double _decidedRunSec = double.MaxValue;

        // The AT HOUR end for this run, in rebirth seconds. Cheap after the one-shot decision.
        public static double EndSec(Character c, double runSec)
        {
            if (_decided && runSec < _decidedRunSec)   // rebirth (or quickload) re-arms
            {
                _decided = false;
                _plannedEnd = NormalEnd;
            }
            if (_decided) return _plannedEnd;
            if (runSec < DecideFrom) return NormalEnd;
            // The auto profile owns AT feeding — without it an extended segment allocates nothing.
            if (Main.Settings == null || !Main.Settings.AutoProfile) return NormalEnd;

            _decided = true;
            _decidedRunSec = runSec;
            if (runSec > DecideUntil)
            {
                // Advisor reloaded well past the boundary: keep the stock shape rather than
                // surprise-flipping RECOVERY/MARATHON back into AT HOUR mid-run.
                _plannedEnd = NormalEnd;
                return _plannedEnd;
            }
            try { _plannedEnd = Decide(c, runSec); }
            catch (Exception e)
            {
                Main.LogDebug($"AT-hour planner: {e.Message}");
                _plannedEnd = NormalEnd;
            }
            return _plannedEnd;
        }

        private static double Decide(Character c, double runSec)
        {
            double window = MaxEnd - runSec;
            if (window <= 60) return NormalEnd;

            var tough = ReadSlot(c, 0);   // slot 0 -> adventure Toughness (defense)
            var power = ReadSlot(c, 1);   // slot 1 -> adventure Power (attack)
            if (power.R <= 0 && tough.R <= 0)
            {
                Rec("AT hour ends on time", "AT Power/Toughness aren't leveling (no energy or paused targets)");
                return NormalEnd;
            }

            double beast = 1;
            try { beast = c.adventureController.beastModeBonus(); } catch { }
            if (double.IsNaN(beast) || beast < 1) beast = 1;
            OptimizationAdvisor.ProjectedBestGear(out var atkMult, out var defMult);
            double refAtk = c.totalAdvAttack() / beast * atkMult;
            double refDef = c.totalAdvDefense() * defMult;
            if (double.IsNaN(refAtk) || double.IsNaN(refDef) || refAtk <= 0 || refDef <= 0)
            {
                Rec("AT hour ends on time", "no usable reference stats");
                return NormalEnd;
            }

            double bestT = double.MaxValue;
            string bestLabel = null;
            string missLabel = null;      // nearest out-of-reach candidate, for the honest "no" line
            double missNeed = double.MaxValue;
            string blocked = null;

            void Consider(double t, string label, double needPct)
            {
                if (t <= window) { if (t < bestT) { bestT = t; bestLabel = label; } }
                else if (needPct < missNeed) { missLabel = label; missNeed = needPct; }
            }

            // -- Titan kill-ladder stage, staged against the reference stats. --
            try
            {
                var obj = OptimizationAdvisor.NextObjective();
                if (obj.Known)
                {
                    OptimizationAdvisor.StagedRequirementFor(obj.Index, obj.Version, refAtk, refDef,
                        out var reqA, out var reqD, out var reqR, out var stage);
                    string name = TitanName(obj.Index, obj.Version);
                    if (refAtk >= reqA && refDef >= reqD)
                    {
                        // Stage already met in best gear — the kill happens without AT's help.
                    }
                    else if (reqR > 0 && Regen(c) < reqR)
                    {
                        blocked = $"{name} {stage} is regen-gated (AT can't raise regen)";
                    }
                    else
                    {
                        double need = Math.Max(reqA / refAtk, reqD / refDef);
                        double t = Solve(power, tough, reqA / refAtk, reqD / refDef, window);
                        Consider(t, $"{name} {stage} ({ExpBalancer.Fmt(reqA)}/{ExpBalancer.Fmt(reqD)})", (need - 1) * 100);
                    }
                }
            }
            catch (Exception e) { Main.LogDebug($"AT-hour titan check: {e.Message}"); }

            // -- Next farm zone: the lowest reachable zone the best gear can't idle (FightType 2). --
            try
            {
                var zones = ZoneStatHelper.UserOverrides ?? ZoneStatHelper.Defaults;
                int maxReach = ZoneHelpers.GetMaxReachableZone(false);
                foreach (var kvp in zones.OrderBy(z => z.Key))
                {
                    if (kvp.Key > maxReach) break;
                    var st = kvp.Value;
                    if (st.FightType((float)refAtk, (float)refDef) == 2) continue;

                    // Idle-farmable via one-shot power (attack alone beats OPower) or the I pair.
                    double tOne = Solve(power, tough, st.OPower * 1.0001 / refAtk, 0, window);
                    double tPair = Solve(power, tough, st.IPower / refAtk, st.IToughness / refDef, window);
                    double t = Math.Min(tOne, tPair);
                    double need = Math.Min(st.OPower * 1.0001 / refAtk,
                        Math.Max(st.IPower / refAtk, st.IToughness / refDef));
                    string name = ZoneHelpers.ZoneList.TryGetValue(kvp.Key, out var zn) ? zn : $"zone {kvp.Key}";
                    Consider(t, $"{name} idle-farm ({ExpBalancer.Fmt(st.IPower)}/{ExpBalancer.Fmt(st.IToughness)})", (need - 1) * 100);
                    break;   // only the NEXT zone is an unlock; higher ones follow on later runs
                }
            }
            catch (Exception e) { Main.LogDebug($"AT-hour zone check: {e.Message}"); }

            if (bestLabel != null)
            {
                // 10% schedule buffer: the forecast holds R constant, but allocation gaps and cap
                // changes nudge it. The MaxEnd clamp keeps any overshoot inside the 4h law.
                double end = Math.Min(MaxEnd, runSec + bestT * 1.1);
                double pPct = (Ratio(power, bestT) - 1) * 100;
                double tPct = (Ratio(tough, bestT) - 1) * 100;
                Rec($"AT hour extended to {end / 3600.0:0.0}h",
                    $"projected +{pPct:0}% P / +{tPct:0}% T crosses {bestLabel} around {(runSec + bestT) / 3600.0:0.0}h");
                return end;
            }

            string why;
            if (missLabel != null)
                why = $"{missLabel} needs +{missNeed:0}%; 4h of AT projects +{(Ratio(power, window) - 1) * 100:0}% P / +{(Ratio(tough, window) - 1) * 100:0}% T";
            else if (blocked != null)
                why = blocked;
            else
                why = "no titan stage or farm zone within AT's reach";
            Rec("AT hour ends on time", why);
            return NormalEnd;
        }

        // ---- forecast primitives ----

        private struct Slot
        {
            public double L0;    // current level
            public double R;     // level-speed numerator: levels/sec * (L+1); 0 = not growing
            public long Cap;     // levelTarget: 0 = uncapped, >0 = hard stop
        }

        private static Slot ReadSlot(Character c, int id)
        {
            var s = new Slot();
            try
            {
                s.L0 = c.advancedTraining.level[id];
                double ppt = c.advancedTrainingController.getProgressPerTick(id);
                if (double.IsNaN(ppt) || ppt < 0) ppt = 0;
                s.R = ppt * 50.0 * (s.L0 + 1.0);
                s.Cap = c.advancedTraining.levelTarget[id];
                if (s.Cap == -1) s.R = 0;   // -1 = the game treats the slot as paused
            }
            catch { s.R = 0; }
            return s;
        }

        private static double LevelAt(Slot s, double t)
        {
            if (s.R <= 0 || t <= 0) return s.L0;
            double l = Math.Sqrt((s.L0 + 1.0) * (s.L0 + 1.0) + 2.0 * s.R * t) - 1.0;
            if (s.Cap > 0 && l > s.Cap) l = s.Cap;
            return l;
        }

        // Projected stat multiplier of a slot after t more seconds of feed.
        private static double Ratio(Slot s, double t)
        {
            double b0 = s.L0 > 0 ? 0.1 * Math.Pow(s.L0, 0.4) : 0;
            double bt = 0.1 * Math.Pow(Math.Max(LevelAt(s, t), 0), 0.4);
            return (1.0 + bt) / (1.0 + b0);
        }

        // Smallest t (60s steps) where both projected ratios clear their needs; MaxValue if never
        // inside the window. Needs <= 1 are already met. Ratios are monotone, so first hit wins.
        private static double Solve(Slot power, Slot tough, double needAtk, double needDef, double window)
        {
            if (needAtk <= 1 && needDef <= 1) return 0;
            for (double t = 60; t <= window; t += 60)
                if (Ratio(power, t) >= needAtk && Ratio(tough, t) >= needDef)
                    return t;
            return double.MaxValue;
        }

        // ---- small helpers ----

        private static double Regen(Character c)
        {
            try { return c.totalAdvHPRegen(); } catch { return 0; }
        }

        private static string TitanName(int i, int v)
        {
            string name = i >= 0 && i < TitansPanel.Abbrev.Length ? TitansPanel.Abbrev[i] : $"T{i + 1}";
            return OptimizationAdvisor.AkVersionCount(i) > 1 ? $"{name} v{v}" : name;
        }

        private static void Rec(string action, string reason) => ChallengeOverlay.Record("AT HOUR", action, reason);
    }
}
