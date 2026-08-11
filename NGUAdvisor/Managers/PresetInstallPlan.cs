using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NGUAdvisor.Managers
{
    // THE OVERWRITE POLICY for the embedded Presets\*.json, as pure data + pure functions.
    //
    // WHY THIS EXISTS AT ALL. PresetInstaller used to be `if (File.Exists(dest)) continue;` — install once,
    // never again. That is safe and it is also a one-way valve: every fix we make to a shipped preset stops
    // at the repo. Audit 36 §5 measured the cost on this machine. `Presets\24hr-EarlyEvil.json` was repaired
    // (audit 01 finding #11: `ALLNGU` listed twice in two Priorities arrays, which counts nine NGU lanes as
    // eighteen and halves every other lane's share), and the file the game actually loads still carries the
    // defect, because it already existed and so was skipped forever. It is the ONLY file in a six-corpus
    // sweep whose token program differs from the repo's, and no release could ever have fixed it.
    //
    // WHY NOT JUST OVERWRITE. A profile in the profiles dir is USER DATA. The companion's Timeline editor
    // writes in place, and the operator hand-edits gear IDs and times. Overwriting unconditionally would
    // silently destroy that, which is a worse defect than the one being fixed.
    //
    // THE POLICY: OVERWRITE ONLY WHAT WE WROTE, AND NEVER WITHOUT A COPY.
    // The installer keeps a manifest beside the profiles recording the hash of the text IT last wrote for
    // each preset. That single fact separates "our file, untouched" from "the user's file now".
    //
    //   on disk missing                        -> Install
    //   on disk == shipped                     -> AlreadyCurrent      (nothing written; hash recorded)
    //   no manifest entry, content differs      -> BackupThenInstall   (the one-time migration)
    //   manifest entry == on disk, shipped moved-> UpdateInPlace       (our file, untouched: deliver the fix)
    //   manifest entry != on disk              -> PreserveUserEdit    (hand-edited: the user wins, forever)
    //
    // BackupThenInstall is the only branch that can move a file the user might have authored, and it is
    // reachable exactly once per file — the run that first writes a manifest entry. It copies the existing
    // file into <profiles>\_backup\<name>.<timestamp>.json BEFORE writing, and logs the backup path at
    // Main.Log level (not LogDebug), so the replacement is recoverable and announced. On this machine that
    // is four files (24hr-EarlyEvil, EvilStart, Normal-24hr, Normal-LRB); the other 22 hash-match the repo
    // and take AlreadyCurrent.
    //
    // PreserveUserEdit is deliberately permanent. Once a user has edited a preset it stops tracking the
    // shipped version for good; deleting the file is the documented way to opt back in. The alternative —
    // re-prompting every launch — has no UI to prompt from at this point in startup (this runs before the
    // companion exists) and would train the operator to ignore the log line.
    //
    // UNITY-FREE ON PURPOSE. The decision, the hash and the manifest format link into the headless test
    // project; PresetInstaller keeps only the file IO, the resource enumeration and the logging.
    public static class PresetInstallPlan
    {
        // NOT a *.json name. The runtime's profile list is Directory.GetFiles(dir, "*.json") with no
        // AllDirectories (UiBridge.cs:2063), so this file and the _backup folder are both invisible to the
        // dropdown. Naming the manifest *.json would have put it in the profile picker.
        public const string ManifestFileName = "_installed-presets.manifest";
        public const string BackupFolderName = "_backup";

        public enum Action
        {
            Install,
            AlreadyCurrent,
            UpdateInPlace,
            BackupThenInstall,
            PreserveUserEdit
        }

        // destHash / recordedHash may be null: null destHash means "no file", null recordedHash means
        // "this installer has no record of ever writing that name".
        //
        // RAW-HASH OVERLOAD. Kept because the four-argument shape is the policy in its simplest form and
        // the characterisation tests are written against it. The live installer calls the five-argument
        // overload below; this one is that overload with normalisation switched off.
        public static Action Decide(bool destExists, string destHash, string shippedHash, string recordedHash)
        {
            return Decide(destExists, destHash, destHash, shippedHash, recordedHash);
        }

        // THE COMPARISON THE INSTALLER ACTUALLY USES, and the fix for the freeze.
        //
        // THE DEFECT. The editor's save path is not the identity function. ProfileModel.Load(text).ToJson()
        // performs THREE transformations on every write, measured 2026-08-07 across all 30 shipped presets:
        //
        //   1. documentation keys are DROPPED by name (ProfileModel:322-335 IsCommentKey). 21 of 30 carry
        //      at least one.
        //   2. inline arrays are REFLOWED to one element per line, and "Key": becomes "Key" : . All 30.
        //   3. keys that were ABSENT are ADDED, because the writer always emits a payload key - Gear[n].ID
        //      and Rebirth[n].Time. This one is not cosmetic and is the reason a preset carrying no
        //      documentation key at all still diverged.
        //
        // So OPENING A SHIPPED PRESET AND SAVING IT WITHOUT CHANGING ANYTHING produced a file that matched
        // neither its shipped text nor its manifest record, and Decide correctly, permanently, silently
        // refused to update it again. Measured: 0 of 30 survived a no-op save by raw hash. The operator
        // never decided anything; the editor did it to them.
        //
        // THE FIX. Compare the CANONICAL FORM - both sides put through the editor's own writer - so a
        // difference only counts when it survives the transformation the editor would have applied anyway.
        // Verified idempotent on all 30 presets and all 31 live profiles: Normalize(Normalize(x)) ==
        // Normalize(x), which is what makes "no-op save" land back on AlreadyCurrent (30 of 30) rather than
        // drifting one step per save.
        //
        // WHAT THIS CAN NO LONGER DETECT, stated plainly because it is a real cost. An edit consisting
        // ONLY of things the writer folds away - a Comment/Note/Thresholds/DiggerOptions line, whitespace,
        // array layout, or an omitted Gear ID / Rebirth Time - is now invisible to the comparison. Such a
        // file reads as "ours, untouched". It is NOT overwritten while the shipped text is unchanged
        // (AlreadyCurrent writes nothing), but when a future release does change that preset it takes
        // UpdateInPlace and the user's comments go with it. The operator authorised exactly this:
        // "i honestly dont care if they get deleted from the profiles". To keep 86d639a's promise literal,
        // PresetInstaller still takes a timestamped backup on that path whenever the bytes on disk are not
        // the bytes we shipped - so nothing is destroyed without a copy, even when it is only a comment.
        //
        // A GENUINE FIELD EDIT IS STILL CAUGHT. Verified against the operator's live machine: the installed
        // 24hr-EarlyEvil.json differs from the shipped one in Diggers[1].List, Energy, Magic, Beards and
        // Gear content - not just documentation - and still returns PreserveUserEdit under this comparison.
        //
        // recordedHash is matched against the NORMALISED hash first and the RAW hash second. The second is
        // the migration path: manifests written before this change hold raw hashes, and without it every
        // one of them would read as an edit on the first launch after the fix.
        public static Action Decide(bool destExists, string destHashRaw, string destHashNormalized,
                                    string shippedHashNormalized, string recordedHash)
        {
            if (!destExists) return Action.Install;
            if (Same(destHashNormalized, shippedHashNormalized)) return Action.AlreadyCurrent;
            if (recordedHash == null) return Action.BackupThenInstall;
            if (Same(destHashNormalized, recordedHash) || Same(destHashRaw, recordedHash))
                return Action.UpdateInPlace;
            return Action.PreserveUserEdit;
        }

        // The profile text as the companion's editor would write it. Returns the INPUT UNCHANGED whenever
        // that canonical form cannot be trusted, so a file this does not understand is compared by raw
        // bytes exactly as before.
        //
        // Three refusals, and the first is the dangerous one:
        //   * DEGENERATE. Anything that is not a profile - a stray .json, a truncated file - loads to an
        //     empty model and writes back as an empty Breakpoints object. Two DIFFERENT such files would
        //     then normalise to the same text, hash equal, and take AlreadyCurrent. Refusing an empty
        //     Breakpoints keeps them comparing by raw bytes.
        //   * NOT IDEMPOTENT. If a second pass moves the text again, the canonical form is not a fixed
        //     point and comparing against it would drift. Checked per file rather than assumed.
        //   * THREW. SimpleJSON is lenient but not total.
        public static string Normalize(string text)
        {
            if (text == null) return null;
            try
            {
                var once = ProfileModel.Load(text).ToJson();
                if (string.IsNullOrEmpty(once)) return text;

                var root = SimpleJSON.JSON.Parse(once);
                var bps = root == null ? null : root["Breakpoints"];
                if (bps == null || bps.Count == 0) return text;

                if (!string.Equals(ProfileModel.Load(once).ToJson(), once, StringComparison.Ordinal))
                    return text;

                return once;
            }
            catch
            {
                return text;
            }
        }

        public static string HashNormalized(string text) => Hash(Normalize(text));

        public static bool Writes(Action a) =>
            a == Action.Install || a == Action.UpdateInPlace || a == Action.BackupThenInstall;

        private static bool Same(string a, string b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // SHA-256 over the TEXT, with CRLF folded to LF first.
        //
        // Text and not bytes because the installer reads the embedded resource through a StreamReader (BOM
        // stripped) and writes with File.WriteAllText (UTF-8, no BOM): hashing raw bytes would call a file
        // "edited" over an encoding detail neither side chose. CRLF folded because git checkout, the
        // packager and a text editor all rewrite line endings without anyone intending an edit — and the
        // cost of a false "edited" verdict is that the file never receives another fix.
        public static string Hash(string text)
        {
            if (text == null) return null;
            var normalized = text.Replace("\r\n", "\n");
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string BackupName(string fileName, DateTime whenLocal)
        {
            var stem = fileName ?? "";
            if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - 5);
            return stem + "." + whenLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        }

        // One record per line: "<file name><TAB><hash>". Comment lines start with '#'. Deliberately not JSON
        // — nothing should be tempted to open it in the profile editor, and a corrupt line must degrade to
        // "no record for that name" (which costs one backup) rather than to an exception during startup.
        public static string SerializeManifest(IDictionary<string, string> records)
        {
            var sb = new StringBuilder();
            sb.Append("# NGUAdvisor preset install record. Each line is the SHA-256 of the text this\n");
            sb.Append("# installer last wrote for that preset. Delete a line to let the shipped version\n");
            sb.Append("# overwrite your copy on the next launch; delete the file to re-adopt everything.\n");
            if (records != null)
            {
                var names = new List<string>(records.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    if (string.IsNullOrEmpty(name) || name.IndexOf('\t') >= 0) continue;
                    var hash = records[name];
                    if (string.IsNullOrEmpty(hash)) continue;
                    sb.Append(name).Append('\t').Append(hash).Append('\n');
                }
            }
            return sb.ToString();
        }

        public static Dictionary<string, string> ParseManifest(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1) continue;
                var name = line.Substring(0, tab).Trim();
                var hash = line.Substring(tab + 1).Trim();
                if (name.Length == 0 || hash.Length == 0) continue;
                map[name] = hash;
            }
            return map;
        }
    }
}
