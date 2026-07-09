using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // NGU value calculation (user request: the Gear Optimizer NGUs-page logic, live). NGU bonuses
    // are LINEAR in level (bonus = level x boostFactor), so an NGU's value multiplier per hour is
    // simply 1 + levelsPerHour / level — computed from the game's own speed formula:
    //   levels/hr = allocated / speedDivider(id) x multiplierStack / (level+1) x 50 ticks x 3600
    // The guide's rule: keep running an NGU while it still gains >= 1.05x/hr; below that its energy
    // is better elsewhere. Candidates (WHICH bonuses matter) stay the guide's chapter lists; this
    // ranks them and picks how many to actually run.
    public static class NGUAdvisors
    {
        public class Entry
        {
            public int Id;
            public string Name;
            public long Level;
            public double Ratio;   // projected x/hr with an equal share of current cap
        }

        public class Plan
        {
            public bool Known;
            public List<Entry> Energy = new List<Entry>();
            public List<Entry> Magic = new List<Entry>();
            public int[] EnergyTargets = new int[0];
            public int[] MagicTargets = new int[0];
            public string Summary = "";
        }

        public static readonly string[] ENames = { "Augs", "Wandoos", "Respawn", "Gold", "Adv-α", "Power-α", "DropCh", "Magic", "PP" };
        public static readonly string[] MNames = { "Ygg", "EXP", "Power-β", "Number", "TM", "Energy", "Adv-β" };

        private static Plan _cache;
        private static DateTime _cacheAt = DateTime.MinValue;

        private static double Mul(Func<double> f)
        {
            try { var v = f(); return v > 0 ? v : 1; } catch { return 1; }
        }

        public static Plan Compute(int[] energyCandidates, int[] magicCandidates)
        {
            if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalSeconds < 30) return _cache;
            var p = new Plan();
            try
            {
                var c = Main.Character;
                if (c == null || c.NGU == null) { _cache = p; return p; }

                double eMult = Mul(() => c.totalNGUSpeedBonus())
                    * Mul(() => c.adventureController.itopod.totalEnergyNGUBonus())
                    * Mul(() => c.NGUController.energyNGUBonus())
                    * Mul(() => c.allDiggers.totalEnergyNGUBonus())
                    * Mul(() => c.hacksController.totalEnergyNGUBonus());
                double mMult = Mul(() => c.totalNGUSpeedBonus())
                    * Mul(() => c.adventureController.itopod.totalMagicNGUBonus())
                    * Mul(() => c.NGUController.magicNGUBonus())
                    * Mul(() => c.allDiggers.totalMagicNGUBonus())
                    * Mul(() => c.hacksController.totalMagicNGUBonus());

                void Build(int[] cands, bool magic, List<Entry> into)
                {
                    if (cands == null || cands.Length == 0) return;
                    double pool = magic ? Math.Max(1, c.magic.curMagic) : Math.Max(1, c.curEnergy);
                    double power = magic ? Math.Max(1, c.totalMagicPower()) : Math.Max(1, c.totalEnergyPower());
                    double share = pool / cands.Length;
                    foreach (var id in cands)
                    {
                        try
                        {
                            long level = magic ? c.NGU.magicSkills[id].level : c.NGU.skills[id].level;
                            double divider = magic ? c.NGUController.magicSpeedDivider(id) : c.NGUController.energySpeedDivider(id);
                            if (divider <= 0) continue;
                            // progressPerTick = power / divider x allocated x mult / (level+1); 50 ticks/s.
                            double lph = power / divider * share * (magic ? mMult : eMult) / (level + 1) * 50.0 * 3600.0;
                            double ratio = level > 0 ? 1.0 + lph / level : 99.0;
                            var names = magic ? MNames : ENames;
                            into.Add(new Entry
                            {
                                Id = id,
                                Name = id < names.Length ? names[id] : $"#{id}",
                                Level = level,
                                Ratio = ratio
                            });
                        }
                        catch { }
                    }
                    into.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
                }

                Build(energyCandidates, false, p.Energy);
                Build(magicCandidates, true, p.Magic);

                int[] Pick(List<Entry> list)
                {
                    var keep = list.Where(x => x.Ratio >= 1.05).Select(x => x.Id).ToArray();
                    if (keep.Length >= 1) return keep;
                    return list.Take(2).Select(x => x.Id).ToArray();   // nothing hot: deepen the top two
                }
                p.EnergyTargets = Pick(p.Energy);
                p.MagicTargets = Pick(p.Magic);

                string Fmt(List<Entry> l) => l.Count == 0 ? "-"
                    : string.Join(", ", l.Take(3).Select(x => $"{x.Name} ×{Math.Min(x.Ratio, 9.99):0.00}/hr").ToArray());
                p.Summary = $"E: {Fmt(p.Energy)} · M: {Fmt(p.Magic)}";
                p.Known = p.Energy.Count > 0 || p.Magic.Count > 0;
            }
            catch (Exception e) { Main.LogDebug($"NGUAdvisors: {e.Message}"); }
            _cache = p;
            _cacheAt = DateTime.UtcNow;
            return p;
        }
    }
}
