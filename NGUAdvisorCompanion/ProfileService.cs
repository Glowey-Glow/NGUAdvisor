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
    private static string PathFor(string dir, string name) => Path.Combine(dir, name + ".json");

    /// <summary>Build the display-ready timelines message ({type:"timelines", profile, systems:{...}}).</summary>
    public static string BuildTimelinesJson(string dir, string name)
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

    private static JSONArray ChallengeList(List<string> entries)
    {
        var arr = new JSONArray();
        if (entries != null)
            foreach (var e in entries)
                if (BreakpointEditor.TryParseChallenge(e, out var it))
                {
                    var o = new JSONObject();
                    o["code"] = it.Code;
                    o["count"] = it.Count;
                    string label = it.Code;
                    foreach (var info in SystemCatalog.Challenges)
                        if (info.Code == it.Code) { label = info.Label; break; }
                    o["label"] = label;
                    arr.Add(o);
                }
        return arr;
    }

    /// <summary>
    /// Write the profile's challenge rotation (flat "CODE-count" list). Canonicalized by BreakpointEditor
    /// (valid codes, clamped counts, deduped); companion-local write like the timeline editor.
    /// </summary>
    public static string SetChallenges(string dir, string name, string[] entries)
    {
        var path = PathFor(dir, name);
        var model = ProfileModel.Load(File.ReadAllText(path));
        model.Challenges = BreakpointEditor.CanonChallenges(entries);
        return SaveAndReload(dir, name, path, model);
    }

    /// <summary>
    /// M3.4 WRITE: change one breakpoint's time. Loads via ProfileModel (preserves Extras/passthrough),
    /// mutates only TimeSeconds, validates, writes in place; the injector's AllocationWatcher reloads.
    /// Returns the fresh timelines message (re-read from disk to confirm the write).
    /// </summary>
    public static string EditBreakpointTime(string dir, string name, string systemKey, int index, int sec)
    {
        if (sec < 0) sec = 0;
        var path = PathFor(dir, name);
        var model = ProfileModel.Load(File.ReadAllText(path));
        if (!model.SetTimeSeconds(systemKey, index, sec))
            throw new System.Exception("No breakpoint " + systemKey + "[" + index + "]");
        return SaveAndReload(dir, name, path, model);
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

    /// <summary>
    /// M11 slice: EDIT a breakpoint's time + content + challenge/target in one write. Content is validated
    /// semantically by BreakpointEditor (valid tokens/indices/codes) before the structural JSON check.
    /// </summary>
    public static string EditBreakpoint(string dir, string name, string systemKey, int index, int sec,
                                        string payload, string challenge, string target)
    {
        if (sec < 0) sec = 0;
        var path = PathFor(dir, name);
        var model = ProfileModel.Load(File.ReadAllText(path));
        var r = BreakpointEditor.Apply(model, systemKey, index, sec, payload, challenge, target);
        if (!r.Ok)
            throw new System.Exception(r.Error);
        return SaveAndReload(dir, name, path, model);
    }

    // Serialize, structural-validate, write in place, then re-read from disk to confirm the round-trip.
    private static string SaveAndReload(string dir, string name, string path, ProfileModel model)
    {
        var json = model.ToJson();
        var v = ProfileValidator.Validate(json);
        if (!v.Ok)
            throw new System.Exception("Refusing to save invalid profile (" + v.Line + ":" + v.Col + " " + v.Message + ")");
        File.WriteAllText(path, json);
        return BuildTimelinesJson(dir, name);
    }

    /// <summary>
    /// M11 slice: DELETE a breakpoint. Loads via ProfileModel (preserves passthrough/extras), removes the
    /// indexed breakpoint, validates, writes in place; the injector's AllocationWatcher reloads. Returns
    /// the fresh timelines message (re-read from disk to confirm).
    /// </summary>
    public static string DeleteBreakpoint(string dir, string name, string systemKey, int index)
    {
        var path = PathFor(dir, name);
        var model = ProfileModel.Load(File.ReadAllText(path));
        if (!model.RemoveBreakpoint(systemKey, index))
            throw new System.Exception("No breakpoint " + systemKey + "[" + index + "]");
        return SaveAndReload(dir, name, path, model);
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

    private static JSONArray Gear(List<ProfileModel.ListBreakpoint> list)
    {
        var arr = new JSONArray();
        foreach (var b in list)
        {
            string payload, summary;
            if (!string.IsNullOrEmpty(b.Objective))
            {
                payload = (b.ForceRespawn ? "Optimize+Respawn: " : "Optimize: ") + b.Objective;
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
