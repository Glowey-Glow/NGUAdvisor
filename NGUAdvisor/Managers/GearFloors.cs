using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // "MAXIMISE X SUBJECT TO Y >= FLOOR" — the shape multi-objective gear actually needs.
    //
    // The obvious design is a weighted blend: Gold Drops 72%, Adventure Power 23%. It does not work
    // here, for two independent reasons, and the second one is not a tuning problem.
    //
    //   THE UNITS DO NOT COMPARE. Scoring is Π (val/100)^exponent — a product of powers, not a sum.
    //   Base-zero stats (Respawn, Power, Toughness) accumulate from 0 while everything else starts at
    //   100, so a weight of "23%" is not 23% of anything nameable. GearHunter already hit exactly this
    //   and its own comment records the outcome: reusing the product objective would let any respawn
    //   item outrank every drop-chance item. It shipped SLOT PARTITIONING instead of blending.
    //
    //   AND A WEIGHTED SUM CANNOT REACH PART OF THE ANSWER SPACE. Maximising a weighted sum maximises
    //   a linear function, so it can only ever select points on the CONVEX HULL of the Pareto
    //   frontier. Any optimal set sitting in a dent of that frontier is unreachable at every weight,
    //   at every value, permanently. Gear selection is combinatorial and its frontier is not convex.
    //   There is no number to discover.
    //
    // A floor says something a weight cannot: survival is a PRECONDITION, not a preference. Either the
    // set clears it or the answer is infeasible — and infeasible is a result the operator gets told,
    // rather than silently receiving the least-bad guess.
    //
    // The grammar deliberately mirrors UntilClause: same shape, same units, same refuse-with-a-reason
    // parsing. One vocabulary across the product beats two that nearly agree.
    public sealed class GearFloor
    {
        public string Stat;      // a GearScorer stat name, e.g. "Adventure Attack"
        public double Value;

        public bool IsMet(IDictionary<string, double> stats)
        {
            double v;
            return stats != null && stats.TryGetValue(Stat, out v) && v >= Value;
        }

        public double Shortfall(IDictionary<string, double> stats)
        {
            double v;
            if (stats == null || !stats.TryGetValue(Stat, out v)) return Value;
            return v >= Value ? 0 : Value - v;
        }

        public string Describe() => Stat + " ≥ " + Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public sealed class GearFloorSet
    {
        public readonly List<GearFloor> Floors = new List<GearFloor>();

        public bool IsEmpty { get { return Floors.Count == 0; } }

        // ALL floors must hold. This is the one place an AND is correct — and it is correct precisely
        // because it is not a schedule: an unmeetable conjunction here is reported as infeasible in the
        // same pass, not left to silently never advance the way an AND in `until` would be.
        public bool AllMet(IDictionary<string, double> stats)
        {
            for (int i = 0; i < Floors.Count; i++)
                if (!Floors[i].IsMet(stats)) return false;
            return true;
        }

        public List<GearFloor> Unmet(IDictionary<string, double> stats)
        {
            var outp = new List<GearFloor>();
            for (int i = 0; i < Floors.Count; i++)
                if (!Floors[i].IsMet(stats)) outp.Add(Floors[i]);
            return outp;
        }

        // How far the whole set is from feasible, normalised per floor so a stat measured in millions
        // does not drown one measured in percent. This is a RANKING aid for the repair pass, never a
        // score — the moment it became a score we would be back to weights.
        public double RelativeShortfall(IDictionary<string, double> stats)
        {
            double worst = 0;
            for (int i = 0; i < Floors.Count; i++)
            {
                var f = Floors[i];
                if (f.Value <= 0) continue;
                double rel = f.Shortfall(stats) / f.Value;
                if (rel > worst) worst = rel;
            }
            return worst;
        }

        public string Describe()
        {
            if (IsEmpty) return "no floors";
            var parts = new string[Floors.Count];
            for (int i = 0; i < Floors.Count; i++) parts[i] = Floors[i].Describe();
            return "subject to " + string.Join(" and ", parts);
        }

        // "Adventure Attack >= 1.2e15 and Adventure Defense >= 5e14"
        public static bool TryParse(string text, out GearFloorSet set, out string error)
        {
            set = null; error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { error = "empty"; return false; }

            var result = new GearFloorSet();
            var parts = text.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var piece = raw.Trim();
                if (piece.Length == 0) continue;

                int at = piece.IndexOf(">=", StringComparison.Ordinal);
                if (at < 0) { error = "no >= in \"" + piece + "\""; return false; }

                string stat = piece.Substring(0, at).Trim();
                string valTxt = piece.Substring(at + 2).Trim();
                if (stat.Length == 0) { error = "no stat named in \"" + piece + "\""; return false; }

                double v;
                if (!TryParseMagnitude(valTxt, out v)) { error = "cannot read \"" + valTxt + "\" as a number"; return false; }
                if (v < 0) { error = "negative floor in \"" + piece + "\""; return false; }

                result.Floors.Add(new GearFloor { Stat = stat, Value = v });
            }

            if (result.Floors.Count == 0) { error = "no floors"; return false; }
            set = result;
            return true;
        }

        // Same K/M/B/T vocabulary as an until-clause, for the same reason: it is how this game writes
        // every number an operator ever reads.
        private static bool TryParseMagnitude(string txt, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(txt)) return false;
            char last = char.ToLowerInvariant(txt[txt.Length - 1]);
            double mult = 1; bool hasUnit = true;
            if (last == 'k') mult = 1e3;
            else if (last == 'm') mult = 1e6;
            else if (last == 'b') mult = 1e9;
            else if (last == 't') mult = 1e12;
            else hasUnit = false;

            string num = hasUnit ? txt.Substring(0, txt.Length - 1).Trim() : txt;
            double parsed;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return false;
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
            value = parsed * mult;
            return true;
        }
    }

    // The outcome of a constrained solve, so "infeasible" is a reportable state rather than a silently
    // degraded set. The whole argument for floors over weights is that you find out.
    public struct FloorVerdict
    {
        public bool Feasible;
        public string Message;      // operator-facing, names the shortfall
        public int Repairs;         // slots the repair pass had to move off the pure-objective pick

        public static FloorVerdict Ok(int repairs)
            => new FloorVerdict { Feasible = true, Repairs = repairs,
                                  Message = repairs == 0 ? "the best set already clears every floor"
                                                         : repairs + " slot(s) traded away from the objective to clear the floors" };

        // `lockedSlots` is how many slots a Gear Lock was holding during the solve. It is not a second
        // cause to blame — the floor is still the thing out of reach — but it is the piece of context
        // the operator cannot see from the floor alone: a set with four accessories pinned is being
        // asked to clear a floor with far less to work with, and "the floor is out of reach" on its
        // own would send them to change the wrong number. Default 0 keeps every existing caller and
        // every existing message byte-identical.
        public static FloorVerdict Infeasible(IList<GearFloor> unmet, IDictionary<string, double> best,
                                              int lockedSlots = 0)
        {
            var parts = new string[unmet.Count];
            for (int i = 0; i < unmet.Count; i++)
            {
                double have; best.TryGetValue(unmet[i].Stat, out have);
                parts[i] = unmet[i].Stat + " " + have.ToString("0.###e0", CultureInfo.InvariantCulture)
                         + " of " + unmet[i].Value.ToString("0.###e0", CultureInfo.InvariantCulture);
            }
            return new FloorVerdict
            {
                Feasible = false,
                Repairs = 0,
                Message = "no loadout in your inventory clears " + string.Join(", ", parts)
                        + " — the floor is out of reach, not the objective"
                        + (lockedSlots > 0
                            ? " (Gear Lock is holding " + lockedSlots + " slot" + (lockedSlots == 1 ? "" : "s")
                              + ", so the solver had fewer to work with)"
                            : "")
            };
        }
    }
}
