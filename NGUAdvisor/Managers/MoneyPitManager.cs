using System;
using System.Collections.Generic;
using UnityEngine;
using static NGUAdvisor.Main;
using Random = UnityEngine.Random;

namespace NGUAdvisor.Managers
{
    public static class MoneyPitManager
    {
        public enum Outcomes
        {
            None,
            IronPill,
            Worn,
            Exp,
            Pomegranate,
            Daycare
        }

        private static readonly Character _character = Main.Character;

        public static readonly List<double> moneyPitThresholds = new List<double>
            { 1e5, 1e7, 1e9, 1e11, 1e13, 1e15, 1e18, 1e21, 1e24, 1e27, 1e30, 1e50, 1e55, 1e60, 1e65, 1e70 };

        public static float TimeUntilReady() => Mathf.Max(0f, _character.pitController.currentPitTime() - (float)_character.pit.pitTime.totalseconds);

        public static bool MoneyPitReady() => TimeUntilReady() <= 0f;

        public static double? ShockwaveTier()
        {
            if (!Settings.MoneyPitRunMode)
                return null;

            Outcomes outcome = PredictMoneyPit(1e50);
            if (outcome == Outcomes.Worn || outcome == Outcomes.Daycare)
                return 1e50;

            outcome = PredictMoneyPit(1e18);
            if (outcome == Outcomes.Worn || outcome == Outcomes.Daycare)
                return 1e18;
            if (PredictMoneyPit(1e15) == Outcomes.Worn)
                return 1e15;
            if (PredictMoneyPit(1e13) == Outcomes.Worn)
                return 1e13;

            return null;
        }

        public static bool NeedsLowerTier()
        {
            if (!Settings.MoneyPitRunMode)
                return false;

            if (!MoneyPitReady())
                return false;

            var tier = ShockwaveTier();
            double gold = _character.realGold;
            switch (tier)
            {
                case 1e18 when gold >= 1e50:
                case 1e15 when gold >= 1e18:
                case 1e13 when gold >= 1e15:
                    return true;
            }

            return false;
        }

        public static bool NeedsGold()
        {
            if (!Settings.MoneyPitRunMode)
                return false;

            if (NeedsRebirth())
                return false;

            if (_character.machine.realBaseGold > 0.0)
                return false;

            var tier = ShockwaveTier();
            double gold = _character.realGold;
            var needGold = gold < tier;
            if (tier == 1e15)
                needGold |= gold % 8e16 < 1e15;
            else if (tier == 1e13)
                needGold |= gold % 4e14 < 1e13;
            return needGold;
        }

        public static bool NeedsRebirth()
        {
            if (!Settings.MoneyPitRunMode)
                return false;

            if (_character.machine.realBaseGold <= 0.0)
                return false;

            return NeedsLowerTier();
        }

        public static void CheckMoneyPit()
        {
            if (!MoneyPitReady())
                return;

            var predictionEnabled = Settings.PredictMoneyPit || Settings.MoneyPitRunMode;
            if (!predictionEnabled && _character.realGold < Settings.MoneyPitThreshold)
                return;

            double gold = _character.realGold;
            if (gold < 1e5)
                return;

            if (Settings.MoneyPitRunMode)
            {
                if (NeedsRebirth() && _character.rebirthTime.totalseconds < 300.0)
                    return;
                if (NeedsGold())
                    return;
            }

            if (predictionEnabled)
            {
                switch (PredictMoneyPit())
                {
                    case Outcomes.IronPill:
                        if (gold < Settings.MoneyPitThreshold)
                            return;

                        if (!LockManager.TryMoneyPitSwap(null, new int[] { 10 }))
                            return;

                        if (Settings.ManageMagic)
                        {
                            _character.removeMostMagic();
                            _character.bloodMagicController.capAllRituals();
                        }

                        break;
                    case Outcomes.Worn:
                        LoadoutManager.SaveDaycare();
                        if (!LockManager.TryMoneyPitSwap(Settings.Shockwave, null, true))
                            return;

                        break;
                    case Outcomes.Exp:
                        if (gold < Settings.MoneyPitThreshold)
                            return;

                        if (!LockManager.TryMoneyPitSwap(null, new int[] { 11 }))
                            return;

                        break;
                    case Outcomes.Pomegranate:
                        if (gold < Settings.MoneyPitThreshold)
                            return;

                        if (!LockManager.TryMoneyPitSwap(Settings.YggdrasilLoadout))
                            return;

                        break;
                    case Outcomes.Daycare:
                        LoadoutManager.SaveDaycare();

                        if (!LockManager.TryMoneyPitSwap())
                            return;

                        LoadoutManager.FillDaycare();

                        break;
                    default:
                        if (gold >= Settings.MoneyPitThreshold)
                            DoMoneyPit();

                        return;
                }
            }
            else
            {
                if (gold < Settings.MoneyPitThreshold)
                    return;

                if (gold >= 1e50 && _character.wishes.wishes[4].level > 0)
                {
                    if (!LockManager.TryMoneyPitSwap(Settings.Shockwave, new[] { 11, 10 }))
                        return;

                    if (Settings.ManageMagic)
                    {
                        _character.removeMostMagic();
                        _character.bloodMagicController.capAllRituals();
                    }
                }
            }

            DoMoneyPit();

            LoadoutManager.RestoreDaycare();
            if (LockManager.HasMoneyPitLock())
                LockManager.TryMoneyPitSwap();
        }

        // Panel chip + advisor policy read the upcoming outcome.
        public static Outcomes PredictNext() => PredictMoneyPit();

        // The advisor throw policy — shared by ApplyPit (acts on it) and the PIT panel's THROW PLAN
        // chip (displays it), so the UI can never disagree with the behavior.
        public struct PitPlan
        {
            public bool Throw;
            public string Verdict;
            public string Detail;
        }

        public static PitPlan AdvisorPlan()
        {
            var p = new PitPlan();
            try
            {
                var c = _character;
                double gold = c.realGold;

                if (!MoneyPitReady())
                {
                    float t = TimeUntilReady();
                    p.Verdict = t > 3600 ? $"COOLDOWN {t / 3600:0.#}h" : $"COOLDOWN {t / 60:0}m";
                    p.Detail = "PIT NOT READY";
                    return p;
                }
                if (c.machine.realBaseGold <= 0.0)
                {
                    p.Verdict = "HOLD";
                    p.Detail = "TM UNFUNDED — GOLD NEEDED";
                    return p;
                }
                if (OptimizationAdvisor.GoldStarvedForAugs(c, 1.0))
                {
                    p.Verdict = "HOLD";
                    p.Detail = "PROTECTS AUG SPENDING";
                    return p;
                }
                if (gold < 1e13)
                {
                    p.Verdict = "WAIT — below 1e13";
                    p.Detail = "OUTCOME TIERS START AT 1E13";
                    return p;
                }

                // Tier-ETA: hold when the next log10 reward tier is within 15 minutes of net gps.
                double curTier = 0, nextTier = 0;
                foreach (var t in moneyPitThresholds)
                {
                    if (gold >= t) curTier = t;
                    else { nextTier = t; break; }
                }
                double net = 0;
                try { net = c.goldPerSecond(); } catch { }
                if (nextTier > 0 && net > 0)
                {
                    double eta = (nextTier - gold) / net;
                    if (eta >= 0 && eta <= 900)
                    {
                        p.Verdict = $"WAIT — {TierName(nextTier)} in ~{Math.Max(1, eta / 60):0}m";
                        p.Detail = "TIER UP: REWARDS JUMP";
                        return p;
                    }
                }

                p.Throw = true;
                p.Verdict = $"THROW at {TierName(curTier)}";
                p.Detail = "PREDICT + PREP READY";
                return p;
            }
            catch { p.Verdict = "…"; p.Detail = ""; return p; }
        }

        public static string TierName(double t)
        {
            int exp = (int)Math.Round(Math.Log10(t));
            return $"1e{exp}";
        }

        // Advisor throw: policy already decided (AdvisorApply.ApplyPit) — run the predict/prep/throw
        // path ignoring the manual threshold. Game minimum (1e5) still applies inside.
        public static void AdvisorThrow()
        {
            var savedThreshold = Settings.MoneyPitThreshold;
            var savedPredict = Settings.PredictMoneyPit;
            try
            {
                Settings.MoneyPitThreshold = 1e5;
                Settings.PredictMoneyPit = true;
                CheckMoneyPit();
            }
            finally
            {
                Settings.MoneyPitThreshold = savedThreshold;
                Settings.PredictMoneyPit = savedPredict;
            }
        }

        private static Outcomes PredictMoneyPit(double gold = -1.0)
        {
            if (gold < 0.0)
                gold = Main.Character.realGold;
            if (gold >= 1e50 && _character.wishes.wishes[4].level > 0)
            {
                var tempState = Random.state;
                Random.state = _character.pit.pitState;
                int num = Random.Range(1, 6);
                Random.state = tempState;
                return (Outcomes)num;
            }
            else if (gold >= 1e13)
            {
                int num;
                var tempState = Random.state;
                Random.state = _character.pit.pitState;
                if (gold >= 1e18)
                    num = Random.Range(1, 13);
                else if (gold >= 1e15)
                    num = Random.Range(1, 12);
                else
                    num = Random.Range(1, 11);
                Random.state = tempState;
                switch (num)
                {
                    case 4:
                        return Outcomes.Worn;
                    case 12:
                        return Outcomes.Daycare;
                }
            }
            return Outcomes.None;
        }

        private static void DoMoneyPit()
        {
            _character.pitController.CallMethod("engage");
            LogPitSpin($"Money Pit Reward: {_character.pitController.pitText.text}");
        }

        public static void DoDailySpin()
        {
            var controller = _character.dailyController;
            if (_character.daily.spinTime.totalseconds < controller.targetSpinTime())
                return;

            controller.startNoBullshitSpin();
            string result = controller.outcomeText.text;
            LogPitSpin($"Daily Spin Reward: {result}");
        }
    }
}
