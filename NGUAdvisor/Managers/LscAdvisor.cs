using System;

namespace NGUAdvisor.Managers
{
    // Laser Sword Challenge opportunity (user insight): LSC does NOT reset the number, and its
    // completion condition — read live from the game — is simply leveling the Laser Sword augment
    // (augs[6]) AND its upgrade to laserSwordTarget(). That makes it finishable inside a normal
    // run's Augs window: free challenge progress while augs get their hour anyway. The advisor
    // estimates time-to-target from the aug speed formula and recommends the run when it fits
    // comfortably inside an hour.
    //
    // All challenge state comes from the CONTROLLER (Character.allChallenges.laserSwordChallenge),
    // never from the serialized Character.challenges.laserSwordChallenge data object: that object's
    // maxCompletions is [NonSerialized] and nothing in the game ever assigns it (its only writer,
    // Challenge.setChallengeStats, has no callers), so it reads 0 forever; and its curCompletions is
    // the NORMAL-difficulty counter, unclamped. The controller's currentCompletions()/maxCompletions/
    // laserSwordTarget() are the numbers the game itself plays by, on every difficulty.
    public static class LscAdvisor
    {
        public class Verdict
        {
            public bool Known;
            public bool Recommended;
            public int Target;
            public double EstMinutes;
            public string Text;
        }

        private static Verdict _cache;
        private static DateTime _cacheAt = DateTime.MinValue;

        private static Verdict Cache(Verdict v)
        {
            _cache = v;
            _cacheAt = DateTime.UtcNow;
            return v;
        }

        // Sum of (L+1) for L = 0..n-1 — the level-cost weight of reaching level n from scratch.
        private static double LevelSum(double n) => n <= 0 ? 0 : n * (n + 1) / 2.0;

        public static Verdict Compute()
        {
            if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalSeconds < 120) return _cache;
            var v = new Verdict();
            try
            {
                var c = Main.Character;
                if (c == null) return Cache(v);
                if (!c.challenges.laserSwordChallengeUnlocked) return Cache(v);
                if (ChallengeDetector.Current() != null) return Cache(v);   // already in one

                var ctl = c.allChallenges?.laserSwordChallenge;
                if (ctl == null) return Cache(v);

                int done = ctl.currentCompletions();   // this difficulty's counter, clamped to max
                int max = ctl.maxCompletions;
                if (max > 0 && done >= max) return Cache(v);   // LSC finished on this difficulty

                int target = ctl.laserSwordTarget();
                v.Known = true;
                v.Target = target;

                // EXACT game formula (AugmentController.getAugProgressPerTick, verbatim):
                //   normal/evil: progress/tick = totalEnergyPower / speedDivider / 50000 x allocated / (level+1)
                //   sadistic:    the /50000 is absent
                // At 50 ticks/s -> seconds for level L = divider x scale x (L+1) / (EP x energy),
                // scale = 50000/50 = 1000 (normal/evil) or 1/50 = 0.02 (sadistic).
                // Summed over the levels STILL OWED on the aug and the upgrade — the sword usually
                // carries levels into the challenge, so counting from zero wildly overstates the cost.
                // EXP-bought energy/power persist into challenges, so current values are a fair basis;
                // x2 safety for start friction + gold.
                double ep = Math.Max(1, c.totalEnergyPower());
                double energy = Math.Max(1, c.curEnergy);

                var diff = c.settings.rebirthDifficulty;
                double scale = diff == difficulty.sadistic ? 0.02 : 1000.0;
                double augDiv = 1e9, upgDiv = 1e9;
                try
                {
                    var ac = c.augmentsController;
                    if (diff == difficulty.evil)
                    {
                        augDiv = ac.evilAugSpeedDividers[6];
                        upgDiv = ac.evilUpgradeSpeedDividers[6];
                    }
                    else if (diff == difficulty.sadistic)
                    {
                        augDiv = ac.sadisticAugSpeedDividers[6];
                        upgDiv = ac.sadisticUpgradeSpeedDividers[6];
                    }
                    else
                    {
                        augDiv = ac.normalAugSpeedDividers[6];
                        upgDiv = ac.normalUpgradeSpeedDividers[6];
                    }
                }
                catch { }

                double augOwed = 0, upgOwed = 0;
                try
                {
                    var a = c.augments.augs[6];
                    augOwed = LevelSum(target) - LevelSum(a.augLevel);
                    upgOwed = LevelSum(target) - LevelSum(a.upgradeLevel);
                }
                catch
                {
                    augOwed = upgOwed = LevelSum(target);
                }
                if (augOwed < 0) augOwed = 0;
                if (upgOwed < 0) upgOwed = 0;

                double seconds = (augDiv * augOwed + upgDiv * upgOwed) * scale / (ep * energy) * 2.0;

                v.EstMinutes = seconds / 60.0;
                v.Recommended = v.EstMinutes <= 48;   // fits comfortably inside the Augs hour
                v.Text = v.Recommended
                    ? $"LSC finishable in ~{Math.Max(1, v.EstMinutes):0}m (laser sword to lv {target}) — number is NOT reset; next auto-rebirth enters it"
                    : $"LSC needs ~{v.EstMinutes:0}m for lv {target} — not yet an Augs-hour freebie";
            }
            catch (Exception e) { Main.LogDebug($"LscAdvisor: {e.Message}"); }
            return Cache(v);
        }
    }
}
