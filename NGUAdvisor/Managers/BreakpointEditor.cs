using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // Parses the companion Timeline-Editor's canonical payload text into a breakpoint's typed content, with
    // SEMANTIC validation the structural ProfileValidator can't do (ProfileValidator only checks JSON syntax):
    // priority tokens against PriorityCatalog, digger/beard/value indices against SystemCatalog ranges,
    // consumable codes and rebirth types against their catalogs. On success it mutates the shared ProfileModel
    // via its typed setters; ProfileService wraps this with load -> validate(JSON) -> write-in-place. Zero UI /
    // game deps so the parse+validate+apply round-trip is unit-testable headlessly.
    //
    // Canonical payload per system (what BuildTimelinesJson emits and this parses, so they round-trip):
    //   energy/magic/r3 : "NGU-3, WAN, CAPAUG-10:50"        (comma tokens, priority order)
    //   gear            : "326, 100"  OR  "Optimize: <obj>" / "Optimize+Respawn: <obj>"
    //   diggers/beards  : "2, 4, 5"                          (comma slot indices)
    //   wandoos/ngudiff : "2"                                (single index)
    //   consumables     : "LC, MUFFIN:5"                     (comma CODE[:amount])
    //   rebirth         : payload = Type code; target passed separately
    public static class BreakpointEditor
    {
        public struct Result
        {
            public bool Ok;
            public string Error;
            public static Result Success => new Result { Ok = true };
            public static Result Fail(string e) => new Result { Ok = false, Error = e };
        }

        /// <summary>
        /// Apply a full breakpoint edit (time + payload + challenge/target) at systemKey[index]. Validates the
        /// payload semantically and returns a friendly error (no mutation persisted by the caller) on failure.
        /// </summary>
        public static Result Apply(ProfileModel m, string systemKey, int index, int sec,
                                   string payload, string challenge, string target)
        {
            if (m == null) return Result.Fail("No profile loaded.");
            if (sec < 0) sec = 0;
            payload = payload ?? "";
            switch (systemKey)
            {
                case "energy":
                case "magic":
                case "r3": return ApplyPriority(m, systemKey, index, sec, payload, challenge);
                case "gear": return ApplyGear(m, index, sec, payload, challenge);
                case "diggers": return ApplyIntList(m, systemKey, index, sec, payload, challenge, 0, 11, "digger slot");
                case "beards": return ApplyIntList(m, systemKey, index, sec, payload, challenge, 0, 6, "beard slot");
                case "wandoos": return ApplyValue(m, systemKey, index, sec, payload, challenge, 0, 2, "Wandoos OS");
                case "ngudiff": return ApplyValue(m, systemKey, index, sec, payload, challenge, 0, 2, "difficulty");
                case "consumables": return ApplyConsumables(m, index, sec, payload, challenge);
                case "rebirth": return ApplyRebirth(m, index, sec, payload, target);
                default: return Result.Fail("Unknown system '" + systemKey + "'.");
            }
        }

        private static Result NoBp(string systemKey, int index) =>
            Result.Fail("No " + systemKey + " breakpoint at index " + index + ".");

        private static ResourceKind KindOf(string systemKey) =>
            systemKey == "magic" ? ResourceKind.Magic : systemKey == "r3" ? ResourceKind.R3 : ResourceKind.Energy;

        private static Result ApplyPriority(ProfileModel m, string sk, int index, int sec, string payload, string challenge)
        {
            if (!m.SetTimeSeconds(sk, index, sec)) return NoBp(sk, index);
            var kind = KindOf(sk);
            var tokens = new List<string>();
            foreach (var raw in SplitCsv(payload))
            {
                var t = PriorityCatalog.Parse(raw);
                if (!t.Recognized)
                    return Result.Fail("'" + raw + "' is not a known priority token.");
                var bt = PriorityCatalog.Find(kind, t.Base);
                if (bt == null)
                    return Result.Fail("'" + t.Base + "' is not valid for " + kind + ".");
                if (t.Index.HasValue)
                {
                    if (!bt.HasIndex)
                        return Result.Fail("'" + t.Base + "' does not take a -number.");
                    if (t.Index.Value < 0 || t.Index.Value > bt.IndexMax)
                        return Result.Fail("'" + t.Base + "-" + t.Index.Value + "' is out of range (0-" + bt.IndexMax + ").");
                }
                tokens.Add(PriorityCatalog.Build(t.Cap, t.Base, t.Index, t.Percent));
            }
            m.SetPriorities(sk, index, tokens);
            return SetChallenge(m, sk, index, challenge);
        }

        private static Result ApplyGear(ProfileModel m, int index, int sec, string payload, string challenge)
        {
            if (!m.SetTimeSeconds("gear", index, sec)) return NoBp("gear", index);
            var p = (payload ?? "").Trim();
            if (p.StartsWith("Optimize", StringComparison.OrdinalIgnoreCase))
            {
                int colon = p.IndexOf(':');
                string head = colon >= 0 ? p.Substring(0, colon) : p;
                bool respawn = head.IndexOf("Respawn", StringComparison.OrdinalIgnoreCase) >= 0;
                string obj = colon >= 0 ? p.Substring(colon + 1).Trim() : "";
                if (obj.Length == 0)
                    return Result.Fail("Gear objective is empty (use \"Optimize: <objective>\").");
                m.SetGearObjective(index, obj, respawn);
            }
            else
            {
                var ids = new List<int>();
                foreach (var raw in SplitCsv(p))
                {
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0)
                        return Result.Fail("'" + raw + "' is not a valid item ID.");
                    ids.Add(id);
                }
                m.SetItems("gear", index, ids);
            }
            return SetChallenge(m, "gear", index, challenge);
        }

        private static Result ApplyIntList(ProfileModel m, string sk, int index, int sec, string payload,
                                           string challenge, int min, int max, string what)
        {
            if (!m.SetTimeSeconds(sk, index, sec)) return NoBp(sk, index);
            var ids = new List<int>();
            foreach (var raw in SplitCsv(payload))
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    return Result.Fail("'" + raw + "' is not a number.");
                if (id < min || id > max)
                    return Result.Fail(what + " '" + id + "' is out of range (" + min + "-" + max + ").");
                ids.Add(id);
            }
            m.SetItems(sk, index, ids);
            return SetChallenge(m, sk, index, challenge);
        }

        private static Result ApplyValue(ProfileModel m, string sk, int index, int sec, string payload,
                                         string challenge, int min, int max, string what)
        {
            if (!m.SetTimeSeconds(sk, index, sec)) return NoBp(sk, index);
            var p = (payload ?? "").Trim();
            if (p.Length == 0)
                return Result.Fail("Enter a " + what + " value (" + min + "-" + max + ").");
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                return Result.Fail("'" + p + "' is not a valid " + what + " (" + min + "-" + max + ").");
            m.SetValue(sk, index, v);
            return SetChallenge(m, sk, index, challenge);
        }

        private static Result ApplyConsumables(ProfileModel m, int index, int sec, string payload, string challenge)
        {
            if (!m.SetTimeSeconds("consumables", index, sec)) return NoBp("consumables", index);
            var items = new List<string>();
            foreach (var raw in SplitCsv(payload))
            {
                string code = raw, amt = null;
                int c = raw.IndexOf(':');
                if (c >= 0) { code = raw.Substring(0, c).Trim(); amt = raw.Substring(c + 1).Trim(); }
                string canon = null;
                foreach (var kv in SystemCatalog.Consumables)
                    if (string.Equals(kv.Key, code, StringComparison.OrdinalIgnoreCase)) { canon = kv.Key; break; }
                if (canon == null)
                    return Result.Fail("'" + code + "' is not a known consumable code.");
                if (amt != null)
                {
                    if (!int.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1)
                        return Result.Fail("'" + raw + "' has an invalid amount (must be a whole number >= 1).");
                    items.Add(canon + ":" + n);
                }
                else items.Add(canon);
            }
            m.SetStringItems(index, items);
            return SetChallenge(m, "consumables", index, challenge);
        }

        private static Result ApplyRebirth(ProfileModel m, int index, int sec, string payload, string target)
        {
            if (!m.SetTimeSeconds("rebirth", index, sec)) return NoBp("rebirth", index);
            var type = (payload ?? "").Trim();
            if (type.Length == 0)
                return Result.Fail("Choose a rebirth type.");
            string canon = null;
            foreach (var kv in SystemCatalog.RebirthTypes)
                if (string.Equals(kv.Key, type, StringComparison.OrdinalIgnoreCase)) { canon = kv.Key; break; }
            if (canon == null)
                return Result.Fail("'" + type + "' is not a known rebirth type.");
            double? tgt = null;
            if (SystemCatalog.TypeTakesTarget(canon))
            {
                var ts = (target ?? "").Trim();
                if (ts.Length == 0)
                    return Result.Fail("A '" + canon + "' rebirth needs a target value.");
                if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var tv))
                    return Result.Fail("'" + ts + "' is not a number.");
                tgt = tv;
            }
            m.SetRebirth(index, canon, tgt);
            return Result.Success;
        }

        private static Result SetChallenge(ProfileModel m, string sk, int index, string challenge)
        {
            var c = (challenge ?? "").Trim();
            if (c.Length == 0) { m.SetChallenge(sk, index, ""); return Result.Success; }
            c = c.ToUpperInvariant();
            bool known = false;
            foreach (var info in SystemCatalog.Challenges)
                if (string.Equals(info.Code, c, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (!known)
                return Result.Fail("'" + c + "' is not a known challenge code.");
            m.SetChallenge(sk, index, c);
            return Result.Success;
        }

        // ----- Challenge rotation (profile "Challenges" array of "CODE-count", per BaseRebirth.ParseChallenges) -----

        public struct ChallengeItem { public string Code; public int Count; }

        /// <summary>Parse one "CODE-count" entry; validates the code against SystemCatalog and clamps count to [1, cap].</summary>
        public static bool TryParseChallenge(string raw, out ChallengeItem item)
        {
            item = new ChallengeItem();
            if (string.IsNullOrEmpty(raw)) return false;
            int dash = raw.IndexOf('-');
            if (dash <= 0 || dash >= raw.Length - 1) return false;
            var code = raw.Substring(0, dash).Trim().ToUpperInvariant();
            if (!int.TryParse(raw.Substring(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return false;
            foreach (var info in SystemCatalog.Challenges)
                if (string.Equals(info.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    item.Code = info.Code;
                    item.Count = Math.Max(1, Math.Min(info.Cap, count));
                    return true;
                }
            return false;
        }

        /// <summary>Canonicalize a raw challenge rotation: keep only valid codes, clamp counts, dedupe by code (first wins, order kept).</summary>
        public static List<string> CanonChallenges(IEnumerable<string> raw)
        {
            var seen = new HashSet<string>();
            var outList = new List<string>();
            if (raw != null)
                foreach (var r in raw)
                    if (TryParseChallenge(r, out var it) && seen.Add(it.Code))
                        outList.Add(it.Code + "-" + it.Count);
            return outList;
        }

        private static IEnumerable<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (var part in s.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }
    }
}
