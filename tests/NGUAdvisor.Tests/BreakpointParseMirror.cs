using System;
using System.Collections.Generic;

namespace NGUAdvisor.Tests
{
    // A FAITHFUL RE-HOST of ResourceBreakpoint.ParseBreakpointArray's string logic
    // (NGUAdvisor\AllocationProfiles\Breakpoints\ResourceBreakpoints\ResourceBreakpoint.cs:134-368).
    //
    // WHY A COPY AND NOT THE REAL PARSER. ParseBreakpointArray constructs NGUBP/BR/RitualBP/… — every one of
    // which derives from ResourceBreakpoint, whose `_character` property reads Main.Character — and the ALLBT
    // branch performs a live Unity read (`_character.settings.syncTraining`) DURING the parse. None of that
    // links into this headless assembly, which is exactly why the profile corpus has never had a parse test
    // and why audit 36 had to re-host the same logic into a throwaway console harness to sweep it.
    //
    // THE COPY IS PINNED TO THE ORIGINAL. ProfileTokenCorpusTests.Mirror_tag_table_matches_the_parser_source
    // reads ResourceBreakpoint.cs and asserts that the set of stem literals in its StartsWith/Contains chain
    // is exactly the set below. A stem added, removed or renamed in the parser without updating this table
    // fails the build rather than silently making the corpus tests test nothing.
    //
    // WHAT THIS MIRROR DELIBERATELY DOES NOT MODEL — LANE BEHAVIOUR. A Lane here carries only what the TOKEN
    // STRING determines: which family, which index, cap-or-not, and the percent. The corpus tests ask one
    // question — "does this profile produce the lane set I expect, in what order, with what outcome" — and
    // that is a property of the string, not of what the constructed breakpoint later does with a budget.
    //
    // This is not a hypothetical boundary; it was crossed once. The mirror originally carried a GroupSpread
    // field, copied from the parser's AdvancedTrainingBP construction, and the corpus asserted it true for
    // every ALLAT lane. When the token-program strip DELETED GroupSpread from the real parser, the two
    // changes merged WITHOUT TEXTUAL CONFLICT and the assertion stayed GREEN — against a mirror that no
    // longer mirrored anything. The stem pin could not catch it: the pin regexes stem LITERALS, and
    // GroupSpread was a field on the constructed lane, a different axis entirely. A guard whose whole
    // purpose is fidelity, silently green and wrong, is the CAPX:1O0 failure shape.
    //
    // ⚠ SO: DO NOT ADD DOWNSTREAM LANE BEHAVIOUR TO THIS TYPE, and do not widen the pin to chase it. The
    // fix for that class of drift is to keep the mirror's surface narrow enough that the pin can cover it.
    // (GroupSpread was moot on its own terms besides: the strip established that GroupShare's waterlevel
    // divisor counted a SUBSET of the fill's destinations over the same pool, so its bound was never tighter
    // than the offer the lane already had and Math.Min(amount, GroupShare(amount)) always returned amount.)
    //
    // Everything below is deliberately shaped like the original, including the parts that look wrong:
    //   * the two `continue`s (malformed percent / malformed index) DESTROY the token — Outcome.Skipped;
    //   * the final `else` emits a NULL, which every consumer filters out before prioCount is computed, so an
    //     unrecognised stem is equally invisible — Outcome.UnknownTag;
    //   * `ALLNGU` is matched by Contains, not StartsWith, and on an R3/Consumable pool `top` is 0, so it
    //     emits NOTHING AT ALL — not even a null — Outcome.EmptyExpansion;
    //   * BR/TM/RIT take IsCap from the RAW token (`prio`), the other eleven from the stripped one (`temp`).
    //     Equivalent for every token that parses (audit 36 §1.4); preserved so it cannot be "fixed" here.
    public static class BreakpointParseMirror
    {
        public enum Pool { Energy, Magic, R3, Consumable }

        public enum Outcome
        {
            Emitted,          // one or more lanes
            Skipped,          // ParseBreakpointArray's `continue` — malformed percent or index
            UnknownTag,       // ParseBreakpointArray's `yield return null`
            EmptyExpansion    // a recognised group tag whose expansion is empty (ALLNGU on R3)
        }

        public sealed class Lane
        {
            public string Family;         // the breakpoint class name the parser constructs
            public int Index;
            public bool IsCap;
            public double? CapPercent;

            // Family#Index, the shape audit 36 counted duplicates on.
            public override string ToString() =>
                Family + "#" + Index + (IsCap ? "(cap" : "(") +
                (CapPercent.HasValue ? ":" + (int)Math.Round(CapPercent.Value * 100) : "") + ")";
        }

        // The 15 stems, in the parser's branch order. Item1 = the literals that branch tests.
        public static readonly string[] StemLiterals =
        {
            "NGU", "CAPNGU",
            "ALLNGU",
            "CAPAT", "AT",
            "AUG", "CAPAUG",
            "BESTAUG", "CAPBESTAUG",
            "BT", "CAPBT",
            "MILEHACK", "CAPMILEHACK",
            "HACK", "CAPHACK",
            "WAN", "CAPWAN",
            "ALLBT", "CAPALLBT",
            "BR", "CAPBR",
            "TM", "CAPTM",
            "RIT", "CAPRIT",
            "ALLAT", "CAPALLAT",
            "ALLHACK", "CAPALLHACK"
        };

        public static Outcome Expand(string rawToken, Pool pool, bool syncTraining, List<Lane> into)
        {
            var prio = (rawToken ?? "").ToUpper();
            var temp = prio;

            double? cap;
            int index;

            if (temp.Contains(":"))
            {
                var split = prio.Split(':');
                temp = split[0];
                int tempCap;
                if (!int.TryParse(split[1], out tempCap))
                    return Outcome.Skipped;
                if (tempCap > 100) cap = 100;
                else if (tempCap < 0) cap = 0;
                else cap = tempCap;
                cap /= 100;
            }
            else
            {
                cap = null;
            }

            if (temp.Contains("-"))
            {
                var split = temp.Split('-');
                temp = split[0];
                if (!int.TryParse(split[1], out index))
                    return Outcome.Skipped;
            }
            else
            {
                index = 0;
            }

            var capT = temp.Contains("CAP");
            var capP = prio.Contains("CAP");
            var before = into.Count;

            if (temp.StartsWith("NGU") || temp.StartsWith("CAPNGU"))
                Add(into, "NGUBP", index, capT, cap);
            else if (temp.Contains("ALLNGU"))
            {
                var top = 0;
                if (pool == Pool.Energy) top = 9;
                else if (pool == Pool.Magic) top = 7;
                for (var i = 0; i < top; i++) Add(into, "NGUBP", i, capT, cap);
            }
            else if (temp.StartsWith("CAPAT") || temp.StartsWith("AT"))
                Add(into, "AdvancedTrainingBP", index, capT, cap);
            else if (temp.StartsWith("AUG") || temp.StartsWith("CAPAUG"))
                Add(into, "AugmentBP", index, capT, cap);
            else if (temp.StartsWith("BESTAUG") || temp.StartsWith("CAPBESTAUG"))
                Add(into, "BestAug", index, capT, cap);
            else if (temp.StartsWith("BT") || temp.StartsWith("CAPBT"))
                Add(into, "BasicTrainingBP", index, capT, cap);
            else if (temp.StartsWith("MILEHACK") || temp.StartsWith("CAPMILEHACK"))
                Add(into, "MileHackBP", index, capT, cap);
            else if (temp.StartsWith("HACK") || temp.StartsWith("CAPHACK"))
                Add(into, "HackBP", index, capT, cap);
            else if (temp.StartsWith("WAN") || temp.StartsWith("CAPWAN"))
                Add(into, "WandoosBP", index, capT, cap);
            else if (temp.StartsWith("ALLBT") || temp.StartsWith("CAPALLBT"))
            {
                var top = syncTraining ? 6 : 12;
                for (var i = 0; i < top; i++) Add(into, "BasicTrainingBP", i, capT, cap);
            }
            else if (temp.StartsWith("BR") || temp.StartsWith("CAPBR"))
                Add(into, "BR", index, capP, cap);
            else if (temp.StartsWith("TM") || temp.StartsWith("CAPTM"))
                Add(into, "TimeMachineBP", index, capP, cap);
            else if (temp.StartsWith("RIT") || temp.StartsWith("CAPRIT"))
                Add(into, "RitualBP", index, capP, cap);
            else if (temp.StartsWith("ALLAT") || temp.StartsWith("CAPALLAT"))
            {
                for (var i = 0; i < 5; i++) Add(into, "AdvancedTrainingBP", i, capT, cap);
            }
            else if (temp.StartsWith("ALLHACK") || temp.StartsWith("CAPALLHACK"))
            {
                for (var i = 0; i <= 14; i++) Add(into, "HackBP", i, capT, cap);
            }
            else
                return Outcome.UnknownTag;

            return into.Count > before ? Outcome.Emitted : Outcome.EmptyExpansion;
        }

        private static void Add(List<Lane> into, string family, int index, bool isCap, double? cap) =>
            into.Add(new Lane { Family = family, Index = index, IsCap = isCap, CapPercent = cap });
    }
}
