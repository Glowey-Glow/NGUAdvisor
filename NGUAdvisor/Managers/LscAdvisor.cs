using System;

namespace NGUAdvisor.Managers
{
    // Laser Sword Challenge opportunity (user insight): LSC does NOT reset the number, and its
    // completion condition — read live from the game — is simply leveling the Laser Sword augment
    // (augs[6]) AND its upgrade to laserSwordTarget() = completions + 2 (per difficulty). That makes
    // it finishable inside a normal run's Augs window: free challenge progress while augs get their
    // hour anyway. The advisor estimates time-to-target from the aug speed formula and recommends
    // the run when it fits comfortably inside an hour.
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

        public static Verdict Compute()
        {
            if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalSeconds < 120) return _cache;
            var v = new Verdict();
            try
            {
                var c = Main.Character;
                if (c == null) { _cache = v; return v; }
                if (!c.challenges.laserSwordChallengeUnlocked) { _cache = v; return v; }
                if (ChallengeDetector.Current() != null) { _cache = v; return v; }   // already in one

                var ch = c.challenges.laserSwordChallenge;
                int done = ch.curCompletions;
                int max = 0;
                try { max = ch.maxCompletions; } catch { }
                if (max > 0 && done >= max) { _cache = v; return v; }   // normal-difficulty LSC finished

                int target = done + 2;   // the game's laserSwordTarget() on Normal
                v.Known = true;
                v.Target = target;

                // EXACT game formula (AugmentController.getAugProgressPerTick, verbatim):
                //   progress/tick = totalEnergyPower / augSpeedDividers[id] / 50000 x allocated / (level+1)
                // at 50 ticks/s -> seconds for level L = divider x 1000 x (L+1) / (EP x energy).
                // Summed over aug + upgrade to the target; EXP-bought energy/power persist into
                // challenges, so current values are a fair basis; x2 safety for start friction + gold.
                double ep = Math.Max(1, c.totalEnergyPower());
                double energy = Math.Max(1, c.curEnergy);
                double augDiv = 1e9, upgDiv = 1e9;
                try
                {
                    var ac = c.augmentsController;
                    augDiv = ac.normalAugSpeedDividers[6];
                    upgDiv = ac.normalUpgradeSpeedDividers[6];
                }
                catch { }
                double sumLevels = target * (target + 1) / 2.0;
                double seconds = (augDiv + upgDiv) * 1000.0 * sumLevels / (ep * energy) * 2.0;

                v.EstMinutes = seconds / 60.0;
                v.Recommended = v.EstMinutes <= 48;   // fits comfortably inside the Augs hour
                v.Text = v.Recommended
                    ? $"LSC finishable in ~{Math.Max(1, v.EstMinutes):0}m (laser sword to lv {target}) — number is NOT reset; next auto-rebirth enters it"
                    : $"LSC needs ~{v.EstMinutes:0}m for lv {target} — not yet an Augs-hour freebie";
            }
            catch (Exception e) { Main.LogDebug($"LscAdvisor: {e.Message}"); }
            _cache = v;
            _cacheAt = DateTime.UtcNow;
            return v;
        }
    }
}
