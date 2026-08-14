using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // "FOCUS ON X UNTIL DONE" — the missing half of the profile vocabulary.
    //
    // Every instruction a profile can give today is "AT time T, do X". There is no way to say "do X
    // UNTIL condition C", so nothing in a profile can terminate on an outcome. That is the whole of the
    // gap: a run is authored as a stopwatch, and the things an operator actually wants are goals —
    // bank this much gold, clear this stat, beat this titan version — which arrive at times nobody can
    // write down in advance.
    //
    // ⚠ THIS IS DELIBERATELY NOT AN EXPRESSION LANGUAGE. A parser that accepts arbitrary arithmetic
    // invites profiles that cannot be validated, cannot be explained back to the operator in their own
    // words, and fail at 3am inside a game loop with no stack. The grammar is:
    //
    //     <subject> <op> <number>[unit]   [ or <subject> <op> <number>[unit] ]...
    //
    // Nothing else. Clauses join with OR and the FIRST met one wins, because that is what a deadline
    // is: "bank 2.4T, or give up after 45 minutes" is one intent, and the 45 minutes is the escape
    // hatch, not a second goal. AND is deliberately absent — every AND we could think of was better
    // written as two consecutive steps, and an unmeetable conjunction is a profile that silently never
    // advances, which is the exact failure mode this feature exists to remove.
    //
    // Unity-free by construction: parse is string in, struct out, and evaluation takes a plain fact
    // bag. The caller reads the game; this decides. That is what makes the whole vocabulary testable
    // without the game build, which matters more here than usual — a wrong answer does not throw, it
    // just quietly never advances the run.
    public enum UntilSubject
    {
        Run,            // seconds elapsed this run
        Gold,           // current gold
        Attack,         // adventure attack
        Defence,        // adventure defence
        Energy,         // total energy cap
        Magic,          // total magic cap
        TitanVersions   // versions of the CURRENT titan objective beaten (bestiary-backed)
    }

    public struct UntilFacts
    {
        public double RunSeconds;
        public double Gold;
        public double Attack;
        public double Defence;
        public double Energy;
        public double Magic;
        public double TitanVersions;
    }

    public sealed class UntilClause
    {
        public UntilSubject Subject;
        public bool AtLeast;        // true => ">=", false => "<="
        public double Value;
        public string RawUnit;      // preserved so Describe() can say it back the way it was written

        public double Read(UntilFacts f)
        {
            switch (Subject)
            {
                case UntilSubject.Run: return f.RunSeconds;
                case UntilSubject.Gold: return f.Gold;
                case UntilSubject.Attack: return f.Attack;
                case UntilSubject.Defence: return f.Defence;
                case UntilSubject.Energy: return f.Energy;
                case UntilSubject.Magic: return f.Magic;
                default: return f.TitanVersions;
            }
        }

        public bool IsMet(UntilFacts f)
        {
            double v = Read(f);
            return AtLeast ? v >= Value : v <= Value;
        }

        public string Describe()
        {
            string n = Subject == UntilSubject.Run
                ? FormatDuration(Value)
                : Value.ToString("0.###e0", CultureInfo.InvariantCulture);
            if (Subject == UntilSubject.TitanVersions) n = ((long)Value).ToString(CultureInfo.InvariantCulture);
            return SubjectName(Subject) + (AtLeast ? " reaches " : " falls to ") + n;
        }

        public static string SubjectName(UntilSubject s)
        {
            switch (s)
            {
                case UntilSubject.Run: return "run time";
                case UntilSubject.Gold: return "gold";
                case UntilSubject.Attack: return "adventure attack";
                case UntilSubject.Defence: return "adventure defence";
                case UntilSubject.Energy: return "energy cap";
                case UntilSubject.Magic: return "magic cap";
                default: return "titan versions beaten";
            }
        }

        private static string FormatDuration(double sec)
        {
            if (sec >= 3600) return (sec / 3600.0).ToString("0.##", CultureInfo.InvariantCulture) + "h";
            if (sec >= 60) return (sec / 60.0).ToString("0.##", CultureInfo.InvariantCulture) + "m";
            return sec.ToString("0", CultureInfo.InvariantCulture) + "s";
        }
    }

    public sealed class UntilCondition
    {
        public readonly List<UntilClause> Clauses = new List<UntilClause>();

        public bool IsMet(UntilFacts f, out UntilClause met)
        {
            met = null;
            for (int i = 0; i < Clauses.Count; i++)
                if (Clauses[i].IsMet(f)) { met = Clauses[i]; return true; }
            return false;
        }

        // Read back in the operator's words, not the profile's syntax. A step that will not advance is
        // the worst failure this feature can have, so the UI has to be able to say what it is waiting
        // for without the operator re-reading the JSON.
        public string Describe()
        {
            if (Clauses.Count == 0) return "never — this step does not end on its own";
            var parts = new string[Clauses.Count];
            for (int i = 0; i < Clauses.Count; i++) parts[i] = Clauses[i].Describe();
            return "until " + string.Join(", or ", parts);
        }

        // ---- parsing ------------------------------------------------------------------------------
        // Returns false with a REASON rather than throwing: this runs over profile files the operator
        // hand-edits, and a profile that fails to load with no explanation is worse than one that
        // refuses one clause and says which.
        public static bool TryParse(string text, out UntilCondition cond, out string error)
        {
            cond = null; error = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { error = "empty"; return false; }

            var result = new UntilCondition();
            var parts = text.Split(new[] { " or " }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var piece = raw.Trim();
                if (piece.Length == 0) continue;

                bool atLeast;
                int opAt = piece.IndexOf(">=", StringComparison.Ordinal);
                if (opAt >= 0) atLeast = true;
                else
                {
                    opAt = piece.IndexOf("<=", StringComparison.Ordinal);
                    if (opAt < 0) { error = "no >= or <= in \"" + piece + "\""; return false; }
                    atLeast = false;
                }

                string subj = piece.Substring(0, opAt).Trim();
                string valTxt = piece.Substring(opAt + 2).Trim();

                UntilSubject s;
                if (!TryParseSubject(subj, out s)) { error = "unknown subject \"" + subj + "\""; return false; }

                double v; string unit;
                if (!TryParseValue(valTxt, s, out v, out unit)) { error = "cannot read \"" + valTxt + "\" as a number"; return false; }
                if (v < 0) { error = "negative target in \"" + piece + "\""; return false; }

                result.Clauses.Add(new UntilClause { Subject = s, AtLeast = atLeast, Value = v, RawUnit = unit });
            }

            if (result.Clauses.Count == 0) { error = "no clauses"; return false; }
            cond = result;
            return true;
        }

        private static bool TryParseSubject(string s, out UntilSubject subject)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "run": case "time": case "elapsed": subject = UntilSubject.Run; return true;
                case "gold": subject = UntilSubject.Gold; return true;
                case "attack": case "atk": case "power": subject = UntilSubject.Attack; return true;
                case "defence": case "defense": case "def": subject = UntilSubject.Defence; return true;
                case "energy": subject = UntilSubject.Energy; return true;
                case "magic": subject = UntilSubject.Magic; return true;
                case "titanversions": case "versions": subject = UntilSubject.TitanVersions; return true;
                default: subject = UntilSubject.Run; return false;
            }
        }

        // Durations accept h/m/s because that is how a person writes a deadline; magnitudes accept
        // K/M/B/T because that is how this game writes every number the operator ever sees.
        private static bool TryParseValue(string txt, UntilSubject s, out double value, out string unit)
        {
            value = 0; unit = null;
            if (txt.Length == 0) return false;
            char last = char.ToLowerInvariant(txt[txt.Length - 1]);
            double mult = 1;
            bool hasUnit = true;

            if (s == UntilSubject.Run)
            {
                if (last == 'h') mult = 3600;
                else if (last == 'm') mult = 60;
                else if (last == 's') mult = 1;
                else hasUnit = false;
            }
            else
            {
                if (last == 'k') mult = 1e3;
                else if (last == 'm') mult = 1e6;
                else if (last == 'b') mult = 1e9;
                else if (last == 't') mult = 1e12;
                else hasUnit = false;
            }

            string num = hasUnit ? txt.Substring(0, txt.Length - 1).Trim() : txt;
            if (hasUnit) unit = last.ToString();

            double parsed;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return false;
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
            value = parsed * mult;
            return true;
        }
    }
}
