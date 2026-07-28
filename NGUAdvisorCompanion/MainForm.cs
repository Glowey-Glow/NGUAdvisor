using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NGUAdvisorCompanion;

/// <summary>
/// The companion window: a WebView2 filling the form that renders the Focus web UI, plus a pipe client
/// that relays injector snapshots into the page and page commands back out.
///
///   pipe line (JSON snapshot)  ->  PostWebMessageAsJson  ->  page window message
///   page postMessage (command) ->  WebMessageReceived    ->  pipe line
/// </summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _web = new();
    private readonly PipeClient _pipe = new("NGUAdvisorUI");
    private volatile bool _webReady;
    private volatile string _latestLine;   // last snapshot, flushed once the page is ready

    public MainForm()
    {
        Text = "NGU Advisor";
        // Window + taskbar icon: build it from the embedded PNG via GetHicon. (new Icon(stream) on a
        // PNG-encoded .ico is unreliable in GDI+, which is why the window icon didn't take before.)
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("appicon.png");
            if (s != null)
            {
                using var bmp = new System.Drawing.Bitmap(s);
                Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch { /* icon is cosmetic */ }
        Width = 1280;
        Height = 860;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _pipe.LineReceived += OnPipeLine;
        _pipe.ConnectionChanged += OnConnectionChanged;

        Load += async (_, _) => await InitWebAsync();
        FormClosed += (_, _) => _pipe.Dispose();
    }

    private async Task InitWebAsync()
    {
        try
        {
            // Keep WebView2's user-data folder out of the game/injector directories.
            var userData = Path.Combine(Path.GetTempPath(), "NGUAdvisorCompanion");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;

            // Serve wwwroot over a virtual https origin so window messaging works with a real origin
            // and the same index.html still opens standalone in a browser for design iteration.
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "nguadvisor.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            // Only post after the page (and its message listener) has loaded; flush the last snapshot
            // so the first frame is never dropped while the page was still parsing.
            core.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                var buffered = _latestLine;
                if (buffered != null) { try { core.PostWebMessageAsJson(buffered); } catch { } }
            };
            core.Settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Navigate("https://nguadvisor.local/index.html");

            _pipe.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Failed to initialize WebView2.\n\n" + ex.Message +
                "\n\nThe WebView2 runtime ships with Windows 11; on older systems install the Evergreen runtime.",
                "NGU Advisor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- injector -> page ----

    private void OnPipeLine(string line)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _latestLine = line;                        // buffer even before the page is ready
                var core = _web.CoreWebView2;
                if (core == null || !_webReady) return;
                try { core.PostWebMessageAsJson(line); }   // line is compact JSON from the injector
                catch { /* malformed line — skip it, keep the stream alive */ }
            });
        }
        catch { /* form closing mid-post */ }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!_webReady || IsDisposed) return;
        var payload = "{\"type\":\"status\",\"connected\":" + (connected ? "true" : "false") + "}";
        try
        {
            BeginInvoke(() =>
            {
                var core = _web.CoreWebView2;
                if (core == null) return;
                try { core.PostWebMessageAsJson(payload); } catch { }
            });
        }
        catch { }
    }

    // ---- page -> commands ----

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The page posts commands as a STRING (postMessage(JSON.stringify(obj))), so read the raw
        // string first; WebMessageAsJson would re-quote it into a double-encoded JSON literal.
        string msg = null;
        try { msg = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrWhiteSpace(msg))
        {
            try { msg = e.WebMessageAsJson; } catch { }
        }
        if (string.IsNullOrWhiteSpace(msg) || msg == "null") return;
        if (TryHandleLocal(msg)) return;   // Timeline-Editor commands are handled in-process (profile files)
        _pipe.Send(msg);                    // everything else -> injector
    }

    // ---- companion-local commands (Timeline Editor reads/writes the profile file directly) ----

    private bool TryHandleLocal(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("cmd", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String) return false;
            switch (cmdEl.GetString())
            {
                case "loadTimelines":
                    PostTimelines(GetStr(root, "dir"), GetStr(root, "name"));
                    return true;
                case "editBreakpointTime":
                    EditBreakpointTime(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                       GetInt(root, "index", -1), GetInt(root, "sec", -1));
                    return true;
                case "deleteBreakpoint":
                    DeleteBreakpoint(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                     GetInt(root, "index", -1));
                    return true;
                case "addBreakpoint":
                    AddBreakpoint(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                  GetInt(root, "sec", -1), GetStr(root, "payload"), GetStr(root, "challenge"),
                                  GetStr(root, "target"));
                    return true;
                case "editBreakpoint":
                    EditBreakpoint(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                   GetInt(root, "index", -1), GetInt(root, "sec", -1), GetStr(root, "payload"),
                                   GetStr(root, "challenge"), GetStr(root, "target"));
                    return true;
                case "setChallenges":
                    SetChallenges(GetStr(root, "dir"), GetStr(root, "name"), GetStrArray(root, "entries"));
                    return true;
                case "loadLog":
                    LoadLog(GetStr(root, "dir"), GetStr(root, "type"));
                    return true;
                case "openProfileFolder":
                    OpenProfileFolder(GetStr(root, "dir"), GetStr(root, "name"));
                    return true;
                case "focusWindow":
                    FocusWindow();
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private static string GetStr(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement o, string k, int dflt) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : dflt;

    private static string[] GetStrArray(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return new string[0];
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString());
        return list.ToArray();
    }

    private void EditBreakpointTime(string dir, string name, string system, int index, int sec)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || index < 0 || sec < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string payload;
            try { payload = ProfileService.EditBreakpointTime(dir, name, system, index, sec); }
            catch (Exception ex) { payload = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(payload);
        });
    }

    private void DeleteBreakpoint(string dir, string name, string system, int index)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || index < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string payload;
            try { payload = ProfileService.DeleteBreakpoint(dir, name, system, index); }
            catch (Exception ex) { payload = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(payload);
        });
    }

    private void AddBreakpoint(string dir, string name, string system, int sec, string payload, string challenge, string target)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || sec < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string result;
            try { result = ProfileService.AddBreakpoint(dir, name, system, sec, payload, challenge, target); }
            catch (Exception ex) { result = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(result);
        });
    }

    private void EditBreakpoint(string dir, string name, string system, int index, int sec, string payload, string challenge, string target)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || index < 0 || sec < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string result;
            try { result = ProfileService.EditBreakpoint(dir, name, system, index, sec, payload, challenge, target); }
            catch (Exception ex) { result = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(result);
        });
    }

    // The advisor's log files (allow-list — blocks path traversal via the `type` field).
    private static readonly HashSet<string> _logTypes = new HashSet<string> { "inject", "debug", "loot", "combat", "cards", "pitspin" };

    private void LoadLog(string dir, string type)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(type) || !_logTypes.Contains(type)) return;
        Task.Run(() =>
        {
            string text;
            try
            {
                var path = Path.Combine(dir, type + ".log");
                if (!File.Exists(path)) { text = "(no " + type + ".log yet)"; }
                else
                {
                    // Shared read — the advisor holds these open for writing; tail the last ~64KB.
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        const long cap = 64 * 1024;
                        if (fs.Length > cap) fs.Seek(-cap, SeekOrigin.End);
                        using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                    }
                    if (text.Length == 0) text = "(" + type + ".log is empty)";
                }
            }
            catch (Exception ex) { text = "(could not read " + type + ".log: " + ex.Message + ")"; }
            PostToWeb(JsonSerializer.Serialize(new { type = "log", name = type, text }));
        });
    }

    private void SetChallenges(string dir, string name, string[] entries)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string result;
            try { result = ProfileService.SetChallenges(dir, name, entries); }
            catch (Exception ex) { result = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(result);
        });
    }

    private void PostTimelines(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string payload;
            try
            {
                if (!File.Exists(Path.Combine(dir, name + ".json"))) return;
                payload = ProfileService.BuildTimelinesJson(dir, name);
            }
            catch (Exception ex)
            {
                payload = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message });
            }
            PostToWeb(payload);
        });
    }

    // Open the profiles folder in Explorer, selecting the named profile's file when it exists. Companion-local
    // (the injector has no business shelling out for a UI affordance). Outcome is posted back so the page can
    // show a message instead of the click appearing to do nothing.
    private void OpenProfileFolder(string dir, string name)
    {
        string error = null;
        try
        {
            if (string.IsNullOrEmpty(dir)) error = "No profile folder is known yet.";
            else if (!Directory.Exists(dir)) error = "Profile folder not found: " + dir;
            else
            {
                // MUST normalise first. The advisor builds its data dir from "%userprofile%/AppData/LocalLow",
                // so the path it reports mixes separators (C:\Users\x/AppData/LocalLow\NGUAdvisor\profiles).
                // Every file API tolerates that, but explorer.exe's /select does NOT — it silently gives up and
                // opens the Desktop instead. GetFullPath canonicalises the separators.
                dir = Path.GetFullPath(dir);
                string file = null;
                if (!string.IsNullOrEmpty(name) && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && !name.Contains(".."))
                {
                    var cand = Path.Combine(dir, name + ".json");
                    if (File.Exists(cand)) file = cand;
                }
                // /select needs explorer.exe directly; a bare folder opens through the shell.
                var psi = file != null
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"")
                    : new ProcessStartInfo(dir) { UseShellExecute = true };
                Process.Start(psi);
            }
        }
        catch (Exception ex) { error = ex.Message; }
        PostToWeb(JsonSerializer.Serialize(new { type = "profileFolder", ok = error == null, error }));
    }

    // Bring the companion forward when an in-game hotkey asks for a view (F9). Windows may refuse to steal
    // focus from the foreground game, in which case the taskbar button flashes instead — still a signal.
    private void FocusWindow()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Activate();
                    BringToFront();
                }
                catch { }
            });
        }
        catch { }
    }

    private void PostToWeb(string json)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                var core = _web.CoreWebView2;
                if (core == null || !_webReady) return;
                try { core.PostWebMessageAsJson(json); } catch { }
            });
        }
        catch { }
    }
}
