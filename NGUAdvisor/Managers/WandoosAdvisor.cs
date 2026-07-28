using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using System;

namespace NGUAdvisor.Managers
{
    // Route C3 Phase A: EXACT Wandoos OS comparator, mirroring the game's own math
    // (decompiled Wandoos98Controller, verified against reference/decomp/Wandoos98Controller.cs).
    // The arithmetic lives in WandoosMath (Unity-free, headless-tested); this file does the live
    // reads and the policy around them.
    //
    // The projection assumes: allocation = whole current E/M cap (how CAPWAN behaves when it can),
    // current live speed (includes OS level + bootup + gear/AT/beard/digger bonuses — identical for
    // all three OS types), the CURRENT OS keeping its already-banked levels while the other two OSs
    // start from level 0 (switching to them wipes their levels), over a fixed time window.
    //
    // ADVANCED TRAINING INSIDE THE WINDOW. totalWandoos{Energy,Magic}Speed() both carry a
    // (1 + advancedTrainingController.wandoos{Energy,Magic}.trainingBonus(0)) factor, so AT levels
    // landing mid-window raise Wandoos speed while the projection is running. Sampling the speed
    // once and holding it flat across up to four hours is not a neutral simplification: an OS whose
    // progress-per-tick is already saturated (min(p,1) == 1) does not respond to speed at all, so
    // ignoring the ramp understates exactly the unsaturated (more expensive) OSs and systematically
    // under-recommends a swap. The AT levels are forecast with AtHourPlanner's own forecaster
    // (AtHourPlanner.TryProjectLevels) and turned into a per-sub-interval speed multiplier curve,
    // which WandoosMath integrates with the clamp INSIDE the sum. Not being fed AT is an exact
    // no-op: the curve comes back null and the projection takes its original closed form.
    //
    // NOT modelled, deliberately: (1) the bootup ramp — the projection is taken at FULL boot on
    // purpose, see below; (2) the profile's real Wandoos share, i.e. whether the active breakpoint
    // is CAPWAN or CAPWAN:50. Both are uniform speed/allocation factors and therefore have the same
    // non-uniform effect across the three OSs as AT does; the full-cap assumption is exact for any
    // OS that ends up saturated (a CAPWAN breakpoint hands Wandoos min(budget, capAmount) and
    // capAmount is exactly p = 1) and overstates only the unsaturated ones.
    public static class WandoosAdvisor
    {
        public struct OsCase
        {
            public int Os;
            public string Name;
            public bool Unlocked;
            public double Bonus;    // projected A/D multiplier after the window
            public double LevelsE;
            public double LevelsM;
        }

        public struct Verdict
        {
            public bool Known;
            public int CurrentOs;
            public int BestOs;
            public string CurrentName;
            public string BestName;
            public double Advantage;   // best projected bonus / current-OS projected bonus
            public OsCase[] Cases;
        }

        private static readonly string[] Names = { "98", "MEH", "XL" };

        // Projection window matched to the RUN, not a fixed hour: remaining time to the profile's
        // time-based rebirth target (clamped 10m-4h); 120m when rebirth is off/unset (NORB, LRB).
        public static int RunHorizonMinutes()
        {
            try
            {
                double target = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1;
                if (target <= 0) return 120;
                double remainingMin = (target - Main.Character.rebirthTime.totalseconds) / 60.0;
                return (int)Math.Min(Math.Max(remainingMin, 10), 240);
            }
            catch { return 120; }
        }

        public static Verdict Compare(int minutes)
        {
            var v = new Verdict { Known = false };
            try
            {
                var c = Main.Character;
                if (c == null) return v;

                bool evil = (int)c.settings.rebirthDifficulty >= 1;
                double[] baseTimes = evil
                    ? new[] { 1e21, 1e27, 1e33 }
                    : new[] { 1e9, 1e12, 1e15 };

                bool[] unlocked = { true, false, false };
                try { unlocked[1] = c.inventory.itemList.jakeComplete; } catch { }
                try { unlocked[2] = c.wandoos98.XLLevels > 0; } catch { }

                // Project at FULL-BOOT speed: right after a rebirth the bootup factor is ~0, which
                // would zero every OS's projection and make the comparison garbage (and silently
                // block the auto-switch at exactly the moment switching is free). Divide the live
                // speed by the current bootup factor; if wandoos has barely booted the numbers are
                // unstable, so report unknown and let the next tick (a minute later) decide.
                double boot = 1.0;
                try { boot = c.wandoos98Controller.bootupSpeedFactor(); } catch { }
                if (boot < 0.02) return v;
                double speedE = c.totalWandoosEnergySpeed() / boot;
                double speedM = c.totalWandoosMagicSpeed() / boot;
                // Project at the profile's REAL Wandoos share, not the whole cap. The full-cap assumption is
                // exact for any OS that saturates (the allocator hands Wandoos min(budget, capAmount), and
                // capAmount is exactly p = 1) but overstates every unsaturated OS by 1/share -- and the
                // presets run CAPWAN:50 / :30 / :20 / :10, so share is routinely 0.1-0.5. Because a saturated
                // OS is unaffected and an unsaturated one is not, the error does NOT cancel in the ratio: it
                // lands on the advantage number by share^-2.0 or share^-2.1 in the mixed regime, which is the
                // regime where the recommendation flips. Falls back to the full cap when no WandoosBP has run
                // (Wandoos not in the active priority list), which is the behaviour this replaced.
                double shareE = WandoosBP.LastShareEnergy;
                double shareM = WandoosBP.LastShareMagic;
                double capE = c.totalCapEnergy() * (shareE > 0 ? shareE : 1.0);
                double capM = c.totalCapMagic() * (shareM > 0 ? shareM : 1.0);
                double seconds = minutes * 60.0;

                int curOs = (int)c.wandoos98.os;
                if (curOs < 0 || curOs > 2) curOs = 0;

                var scenario = new WandoosMath.Scenario
                {
                    BaseTimes = baseTimes,
                    CapE = capE,
                    CapM = capM,
                    SpeedE = speedE,
                    SpeedM = speedM,
                    Seconds = seconds,
                    CurOs = curOs,
                    BankedE = c.wandoos98.energyLevel,
                    BankedM = c.wandoos98.magicLevel,
                    CurveE = AtSpeedCurve(c, energy: true, seconds: seconds),
                    CurveM = AtSpeedCurve(c, energy: false, seconds: seconds)
                };

                var lE = new double[3];
                var lM = new double[3];
                var bonus = new double[3];
                WandoosMath.Project(scenario, lE, lM, bonus);

                var cases = new OsCase[3];
                for (int os = 0; os < 3; os++)
                    cases[os] = new OsCase
                    {
                        Os = os,
                        Name = Names[os],
                        Unlocked = unlocked[os],
                        LevelsE = lE[os],
                        LevelsM = lM[os],
                        Bonus = bonus[os]
                    };

                int cur = curOs;
                int best = WandoosMath.BestOs(bonus, unlocked);

                v.Known = true;
                v.CurrentOs = cur;
                v.BestOs = best;
                v.CurrentName = Names[cur];
                v.BestName = Names[best];
                v.Advantage = cases[cur].Bonus > 0 ? cases[best].Bonus / cases[cur].Bonus : 1.0;
                v.Cases = cases;
            }
            catch (Exception e) { Main.LogDebug($"WandoosAdvisor: {e.Message}"); }
            return v;
        }

        // Speed multiplier curve contributed by the Advanced Training slot that feeds this dump
        // (slot 3 = Energy Dump, slot 4 = Magic Dump). null when AT can't move the speed inside the
        // window — not unlocked, not being fed, paused, or already at its level target — and null
        // routes the projection back to its exact constant-speed closed form.
        private static double[] AtSpeedCurve(Character c, bool energy, double seconds)
        {
            try
            {
                // AdvancedTrainingController.updateAdvancedTraining() refuses to level anything until
                // basic training tier 5 is done, which is exactly what gates the AT button. Without
                // this a locked account would forecast levels it cannot earn.
                if (!c.buttons.advancedTraining.interactable) return null;

                var ctl = energy ? c.advancedTrainingController.wandoosEnergy
                                 : c.advancedTrainingController.wandoosMagic;
                if (ctl == null) return null;

                int id = energy ? 3 : 4;
                var times = WandoosMath.SampleTimes(seconds);
                var levels = new double[times.Length];
                if (!AtHourPlanner.TryProjectLevels(c, id, times, levels)) return null;

                // trainingBonus(0) is the levelFactor*L0 term ALREADY inside the speed we sampled, so
                // it is the denominator that makes the curve a pure relative correction (= 1 at t=0).
                return WandoosMath.AtSpeedCurve(levels, ctl.levelFactor, ctl.trainingBonus(0),
                                                c.advancedTraining.level[id]);
            }
            catch (Exception e) { Main.LogDebug($"WandoosAdvisor AT curve: {e.Message}"); return null; }
        }

        public static string FmtX(double ratio)
        {
            if (ratio >= 1e6) return (ratio / 1e6).ToString("0.#") + "M×";
            if (ratio >= 1000) return (ratio / 1000).ToString("0.#") + "K×";
            if (ratio >= 10) return ratio.ToString("0") + "×";
            return ratio.ToString("0.0") + "×";
        }
    }
}
