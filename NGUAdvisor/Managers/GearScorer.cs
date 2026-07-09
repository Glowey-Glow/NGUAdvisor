using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // Phase 1 of the native gear optimizer (route C3): a faithful C# port of the gear-optimizer's SCORING
    // math (score_vals / get_raw_vals / hardcap from its util.js). Pure and game-independent so it can be
    // validated against the website's JS as an oracle before it is fed live game item data.
    //
    // An "objective" (their "factor") is a list of stat names + optional exponents. The score is the product
    // of each stat's multiplier (raw total / 100), each raised to its exponent. Higher = better.
    public static class GearScorer
    {
        // One equipped item: its per-stat bonus values, and whether it occupies a weapon slot (offhand math).
        public class Item
        {
            public Dictionary<string, double> Stats;
            public bool IsWeapon;
            public Item() { Stats = new Dictionary<string, double>(StringComparer.Ordinal); }
        }

        // Stats that accumulate from 0 (the item bonuses ARE the whole multiplier); everything else from 100%.
        private static bool BaseZero(string stat) => stat == "Respawn" || stat == "Power" || stat == "Toughness";

        // Port of get_raw_vals. `equip` is in slot order (weapons first: 1st weapon = mainhand, 2nd = offhand).
        // offhandPercent is the offhand weapon's contribution (0..100).
        public static double[] GetRawVals(IReadOnlyList<Item> equip, IReadOnlyList<string> stats, double offhandPercent)
        {
            var vals = new double[stats.Count];
            for (int i = 0; i < stats.Count; i++)
            {
                var stat = stats[i];
                vals[i] = BaseZero(stat) ? 0.0 : 100.0;
                bool mainhand = true;
                foreach (var item in equip)
                {
                    if (item?.Stats == null || !item.Stats.TryGetValue(stat, out var val)) continue;
                    if (item.IsWeapon)
                    {
                        if (mainhand) mainhand = false;
                        else val *= offhandPercent / 100.0;
                    }
                    if (double.IsNaN(val)) continue;
                    vals[i] += val;
                }
            }
            return vals;
        }

        // Port of hardcap. caps holds "<stat> Cap" (hard cap) and "Nude <stat>" (nude total) entries.
        public static double[] HardCap(double[] vals, IReadOnlyList<string> stats, IReadOnlyDictionary<string, double> caps)
        {
            var res = new double[vals.Length];
            for (int i = 0; i < vals.Length; i++)
            {
                if (caps == null || !caps.TryGetValue(stats[i] + " Cap", out var hardcap))
                {
                    res[i] = vals[i];
                    continue;
                }
                double total = 1.0;
                if (caps.TryGetValue("Nude " + stats[i], out var nude)) total = Math.Max(1.0, nude);
                double maxVal = 100.0 * Math.Max(1.0, hardcap / total);
                res[i] = Math.Min(vals[i], maxVal);
            }
            return res;
        }

        // Port of score_vals: product of (val/100)^exponent. exponents may be null (all weight 1).
        public static double ScoreVals(double[] vals, IReadOnlyList<double> exponents)
        {
            double res = 1.0;
            for (int i = 0; i < vals.Length; i++)
            {
                double v = vals[i] / 100.0;
                if (exponents != null && exponents.Count > i)
                    v = Math.Pow(v, exponents[i]);
                res *= v;
            }
            return res;
        }

        public static double ScoreRaw(IReadOnlyList<Item> equip, IReadOnlyList<string> stats, IReadOnlyList<double> exponents, double offhandPercent)
            => ScoreVals(GetRawVals(equip, stats, offhandPercent), exponents);

        public static double Score(IReadOnlyList<Item> equip, IReadOnlyList<string> stats, IReadOnlyList<double> exponents, double offhandPercent, IReadOnlyDictionary<string, double> caps)
            => ScoreVals(HardCap(GetRawVals(equip, stats, offhandPercent), stats, caps), exponents);
    }
}
