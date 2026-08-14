using System.Collections.Generic;
using System.IO;
using NGUAdvisor.Managers;   // ProfileModel, SystemCatalog (linked shared sources)
using SimpleJSON;

namespace NGUAdvisorCompanion;

/// <summary>
/// Reads/writes allocation-profile breakpoints for the Timeline Editor using the SAME ProfileModel the
/// injector uses (linked shared source), so the load->edit->save round-trip is identical and lossless.
/// Build methods produce a display-ready "timelines" message for the web UI; edit methods (M3.4) apply a
/// targeted mutation to the loaded model and write it back in place (the injector's AllocationWatcher reloads).
/// </summary>
public static class ProfileService
{
    // C10 (counter-audit): every profile command arrives on its own bare Task.Run (MainForm.TryHandleLocal),
    // and every public method here is a read-modify-write against ONE file. With nothing serialising them,
    // two commands in flight interleave: both read the same bytes, both write, and the second erases the
    // first. Measured on the real code before this gate existed: 40/40 simultaneous deletes lost one of the
    // two (the loser hitting a Windows sharing violation, because File.ReadAllText/File.WriteAllText are
    // mutually exclusive), and 4/6 staggered deletes lost one SILENTLY - both calls returned a fresh
    // timelines message and the row was simply not on disk.
    //
    // The gate lives here rather than in MainForm because ProfileService owns the file and must hold the
    // invariant for EVERY caller, present and future (MainForm is not the only one - the test project links
    // this file directly). Monitor, not SemaphoreSlim: the critical section is short (~1 ms on a real
    // profile; 37 ms on a synthetic 4000-breakpoint one) and entirely synchronous, there is no await to
    // suspend across, and every caller is already off the UI thread inside Task.Run - so nothing the UI
    // thread does can block on this.
    //
    // In-process only, by design: nothing else writes these files (the injector's AllocationWatcher only
    // reads them), so a cross-process Mutex would buy nothing. A hand-edit in the profile folder while the
    // editor is open remains possible - that is C3's residual window, not this one.
    private static readonly object _fileGate = new object();

    private static string PathFor(string dir, string name) => Path.Combine(dir, name + ".json");

    /// <summary>Build the display-ready timelines message ({type:"timelines", profile, systems:{...}}).</summary>
    public static string BuildTimelinesJson(string dir, string name)
    {
        // Reads are gated too: a bare read that lands mid-write throws a sharing violation, which is exactly
        // how the Timeline Editor's "load" used to fail while another command was saving.
        lock (_fileGate) { return BuildTimelinesJsonCore(dir, name); }
    }

    // Caller MUST hold _fileGate. Split out so SaveAndReload can re-read without depending on Monitor
    // reentrancy (which would silently become a deadlock if this ever moved to SemaphoreSlim).
    private static string BuildTimelinesJsonCore(string dir, string name)
    {
        var model = ProfileModel.Load(File.ReadAllText(PathFor(dir, name)));
        var sys = new JSONObject();
        sys["energy"] = Priorities(model.Energy);
        sys["magic"] = Priorities(model.Magic);
        sys["r3"] = Priorities(model.R3);
        sys["gear"] = Gear(model.Gear);
        sys["diggers"] = IntList(model.Diggers, SystemCatalog.Diggers);
        sys["beards"] = IntList(model.Beards, SystemCatalog.Beards);
        sys["wandoos"] = Value(model.Wandoos, SystemCatalog.WandoosOS);
        sys["ngudiff"] = Value(model.NGUDiff, SystemCatalog.Difficulty);
        sys["consumables"] = Consumables(model.Consumables);
        sys["rebirth"] = Rebirth(model.Rebirth);

        var root = new JSONObject();
        root["type"] = "timelines";
        root["profile"] = name;
        root["systems"] = sys;
        root["challenges"] = ChallengeList(model.Challenges);   // flat rotation (not time-based)
        return root.ToString();
    }

    // The profile stores COMPLETION ORDINALS ("BASIC-1".."BASIC-5"), the Challenges page shows friendly
    // "x N" rows. BreakpointEditor.GroupChallenges does the collapse (one row per contiguous run, target =
    // the run's highest ordinal); this just serialises a row. Every row carries:
    //   code/label       - the challenge
    //   target (+count)  - the friendly "x N"; `count` is the legacy alias, same value
    //   from             - lowest ordinal in the run (> 1 => the run starts partway in)
    //   ordinals         - the exact ordinals, so nothing about the row is lossy
    //   entry            - the token to send back in setChallenges to leave the row EXACTLY as it is
    //   cap              - the challenge's completion ceiling
    //   single/partial   - shape flags (from == target / from > 1)
    //   suspect + repair - present only for the old editor's damage shape (a lone ordinal above 1); the UI
    //                      must confirm against live completions before offering `repair`, since chained
    //                      C-block profiles author that shape on purpose.
    // Live progress is NOT read here (this process has no game): the UI already receives
    // state.challengeState.queued[{code,cur,max}] and merges it — done = min(cur,target),
    // "will run" = ordinals where ordinal > cur, met = cur >= target, stuck = cur + 1 < from.
    private static JSONArray ChallengeList(List<string> entries)
    {
        var arr = new JSONArray();
        foreach (var g in BreakpointEditor.GroupChallenges(entries))
        {
            var o = new JSONObject();
            o["code"] = g.Code;
            o["label"] = g.Label;
            o["from"] = g.From;
            o["target"] = g.Target;
            o["count"] = g.Target;             // legacy alias of target (pre-fix field name)
            o["cap"] = g.Cap;
            o["entry"] = g.Entry;
            var ordinals = new JSONArray();
            foreach (var n in g.Ordinals) ordinals.Add(n);
            o["ordinals"] = ordinals;
            o["single"] = g.Single;
            o["partial"] = g.Partial;
            if (g.Suspect) { o["suspect"] = true; o["repair"] = g.Repair; }
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>
    /// Write the profile's challenge rotation. `entries` is the EDITOR's vocabulary — "CODE-N"/"CODE*N" for a
    /// friendly target (expanded to the full CODE-1..CODE-N ordinal range the runtime actually matches on) and
    /// "CODE-A..B" for an untouched/advanced row, which is what each row's `entry` field is. Canonicalized by
    /// BreakpointEditor (valid codes, clamped ordinals, deduped on CODE+ORDINAL); companion-local write like
    /// the timeline editor.
    /// </summary>
    public static string SetChallenges(string dir, string name, string[] entries)
    {
        lock (_fileGate)
        {
            var path = PathFor(dir, name);
            var model = ProfileModel.Load(File.ReadAllText(path));
            model.Challenges = BreakpointEditor.CanonChallenges(entries);
            return SaveAndReload(dir, name, path, model);
        }
    }

    /// <summary>
    /// M3.4 WRITE: change one breakpoint's time. Loads via ProfileModel (preserves Extras/passthrough),
    /// mutates only TimeSeconds, validates, writes in place; the injector's AllocationWatcher reloads.
    /// Returns the fresh timelines message (re-read from disk to confirm the write).
    /// </summary>
    public static string EditBreakpointTime(string dir, string name, string systemKey, int index, int sec)
    {
        if (sec < 0) sec = 0;
        lock (_fileGate)
        {
            var path = PathFor(dir, name);
            var model = ProfileModel.Load(File.ReadAllText(path));
            if (!model.SetTimeSeconds(systemKey, index, sec))
                throw new System.Exception("No breakpoint " + systemKey + "[" + index + "]");
            return SaveAndReload(dir, name, path, model);
        }
    }

    /// <summary>
    /// M11 slice: ADD a breakpoint (blank of the right type, at time <paramref name="sec"/>) and immediately
    /// apply the editor payload/challenge/target to it, so add-with-content is one atomic write. Loads via
    /// ProfileModel (preserves passthrough/extras), validates the payload SEMANTICALLY (BreakpointEditor) and
    /// the whole file STRUCTURALLY (ProfileValidator) before writing; the injector's AllocationWatcher reloads.
    /// </summary>
    public static string AddBreakpoint(string dir, string name, string systemKey, int sec,
                                       string payload, string challenge, string target)
    {
        if (sec < 0) sec = 0;
        lock (_fileGate)
        {
            var path = PathFor(dir, name);
            var model = ProfileModel.Load(File.ReadAllText(path));
            int index = model.AddBreakpoint(systemKey, sec);
            if (index < 0)
                throw new System.Exception("Unknown system '" + systemKey + "'.");
            var r = BreakpointEditor.Apply(model, systemKey, index, sec, payload, challenge, target);
            if (!r.Ok)
                throw new System.Exception(r.Error);
            return SaveAndReload(dir, name, path, model);
        }
    }

    /// <summary>
    /// M11 slice: EDIT a breakpoint's time + content + challenge/target in one write. Content is validated
    /// semantically by BreakpointEditor (valid tokens/indices/codes) before the structural JSON check.
    /// </summary>
    public static string EditBreakpoint(string dir, string name, string systemKey, int index, int sec,
                                        string payload, string challenge, string target)
    {
        if (sec < 0) sec = 0;
        lock (_fileGate)
        {
            var path = PathFor(dir, name);
            var model = ProfileModel.Load(File.ReadAllText(path));
            var r = BreakpointEditor.Apply(model, systemKey, index, sec, payload, challenge, target);
            if (!r.Ok)
                throw new System.Exception(r.Error);
            return SaveAndReload(dir, name, path, model);
        }
    }

    // Serialize, structural-validate, write in place, then re-read from disk to confirm the round-trip.
    // Caller MUST hold _fileGate (every call site is inside a lock (_fileGate) block).
    private static string SaveAndReload(string dir, string name, string path, ProfileModel model)
    {
        var json = model.ToJson();
        var v = ProfileValidator.Validate(json);
        if (!v.Ok)
            throw new System.Exception("Refusing to save invalid profile (" + v.Line + ":" + v.Col + " " + v.Message + ")");
        File.WriteAllText(path, json);
        return BuildTimelinesJsonCore(dir, name);
    }

    /// <summary>
    /// M11 slice: DELETE a breakpoint. Loads via ProfileModel (preserves passthrough/extras), removes the
    /// indexed breakpoint, validates, writes in place; the injector's AllocationWatcher reloads. Returns
    /// the fresh timelines message (re-read from disk to confirm).
    /// </summary>
    public static string DeleteBreakpoint(string dir, string name, string systemKey, int index)
    {
        lock (_fileGate)
        {
            var path = PathFor(dir, name);
            var model = ProfileModel.Load(File.ReadAllText(path));
            if (!model.RemoveBreakpoint(systemKey, index))
                throw new System.Exception("No breakpoint " + systemKey + "[" + index + "]");
            return SaveAndReload(dir, name, path, model);
        }
    }

    // A display row: sec + human `summary` (labels) + `payload` (the canonical editable text the editor
    // pre-fills and sends back, so display and edit round-trip) + optional challenge tag.
    private static JSONObject Row(int sec, string summary, string payload, string challenge)
    {
        var o = new JSONObject();
        o["sec"] = sec;
        o["summary"] = summary;
        o["payload"] = payload ?? "";
        if (!string.IsNullOrEmpty(challenge)) o["challenge"] = challenge;
        return o;
    }

    private static JSONArray Priorities(List<ProfileModel.PriorityBreakpoint> list)
    {
        var arr = new JSONArray();
        foreach (var b in list)
        {
            string payload = string.Join(", ", b.Priorities);
            arr.Add(Row(b.TimeSeconds, b.Priorities.Count == 0 ? "(none)" : payload, payload, b.Challenge));
        }
        return arr;
    }

    // Three shapes now, not two. GEAR LOCK is the both-at-once row — locked IDs and an objective —
    // and its payload has to round-trip through BreakpointEditor.ApplyGear unchanged, which is why
    // the canonical text here is exactly the grammar that parses there.
    private static JSONArray Gear(List<ProfileModel.ListBreakpoint> list)
    {
        var arr = new JSONArray();
        foreach (var b in list)
        {
            bool hasObjective = !string.IsNullOrEmpty(b.Objective);
            bool hasItems = b.Items.Count > 0;
            string payload, summary;
            string optimize = (b.ForceRespawn ? "Optimize+Respawn: " : "Optimize: ") + b.Objective;
            if (hasObjective && hasItems)
            {
                payload = "Lock: " + string.Join(", ", b.Items) + "; " + optimize;
                summary = "Optimize: " + b.Objective + (b.ForceRespawn ? " (+Respawn)" : "")
                        + " · " + b.Items.Count + (b.Items.Count == 1 ? " item locked" : " items locked");
            }
            else if (hasObjective)
            {
                payload = optimize;
                summary = "Optimize: " + b.Objective + (b.ForceRespawn ? " (+Respawn)" : "");
            }
            else
            {
                payload = string.Join(", ", b.Items);
                summary = b.Items.Count + (b.Items.Count == 1 ? " item" : " items");
            }
            arr.Add(Row(b.TimeSeconds, summary, payload, b.Challenge));
        }
        return arr;
    }

    private static JSONArray IntList(List<ProfileModel.ListBreakpoint> list, IReadOnlyList<KeyValuePair<int, string>> names)
    {
        var arr = new JSONArray();
        foreach (var b in list)
        {
            var parts = new List<string>();
            foreach (var i in b.Items) parts.Add(SystemCatalog.NameOf(names, i));
            string payload = string.Join(", ", b.Items);   // raw indices, for the editor
            arr.Add(Row(b.TimeSeconds, parts.Count == 0 ? "(none)" : string.Join(", ", parts), payload, b.Challenge));
        }
        return arr;
    }

    private static JSONArray Value(List<ProfileModel.ValueBreakpoint> list, IReadOnlyList<KeyValuePair<int, string>> names)
    {
        var arr = new JSONArray();
        foreach (var b in list)
            arr.Add(Row(b.TimeSeconds, SystemCatalog.NameOf(names, b.Value), b.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), b.Challenge));
        return arr;
    }

    private static JSONArray Consumables(List<ProfileModel.StringListBreakpoint> list)
    {
        var arr = new JSONArray();
        foreach (var b in list)
        {
            var parts = new List<string>();
            foreach (var it in b.Items)
            {
                string code = it, amt = "";
                int c = it.IndexOf(':');
                if (c >= 0) { code = it.Substring(0, c); amt = " x" + it.Substring(c + 1); }
                parts.Add(SystemCatalog.LabelOf(SystemCatalog.Consumables, code) + amt);
            }
            string payload = string.Join(", ", b.Items);   // raw CODE[:amount], for the editor
            arr.Add(Row(b.TimeSeconds, parts.Count == 0 ? "(none)" : string.Join(", ", parts), payload, b.Challenge));
        }
        return arr;
    }

    private static JSONArray Rebirth(List<ProfileModel.RebirthEntry> list)
    {
        var arr = new JSONArray();
        foreach (var b in list)
        {
            string label = SystemCatalog.LabelOf(SystemCatalog.RebirthTypes, b.Type);
            if (b.Target.HasValue) label += " -> " + b.Target.Value;
            // Rebirth's editor uses Type (payload) + a separate numeric Target field.
            var o = Row(b.TimeSeconds, label, b.Type, "");
            o["target"] = b.Target.HasValue ? b.Target.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "";
            arr.Add(o);
        }
        return arr;
    }
}
