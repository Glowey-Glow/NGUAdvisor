using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // LOGS section (L1, revised for the A1 rail sub-nav): the sources live as rail children —
    // the rail calls SelectSource. This panel is the reader: adaptive filter chips + the list.
    //  - ADVISOR: the ChallengeOverlay feed (the old Advisors FEED sub-view, moved here whole).
    //  - LOOT:    Main.LootFeed ring (mirrors loot.log writes; OPEN FILE for full history).
    //  - SESSION: tail of inject.log, read on demand (shared read — the writer keeps it open).
    public class LogsPanel : Panel
    {
        private static readonly string[] AdvisorCats = { "ALL", "ALLOC", "GEAR", "TITAN", "SEGMENT", "QUEST" };
        private static readonly string[] LootCats = { "ALL", "DROPS", "EXP · AP", "BOOSTS" };
        private static readonly string[] SessionCats = { "ALL" };
        private static readonly string[][] SourceFilters = { AdvisorCats, LootCats, SessionCats };

        private readonly List<Button> _chips = new List<Button>();
        private ListBox _list;
        private Button _pause;
        private Button _openFile;
        private int _active;              // 0 advisor · 1 loot · 2 session (rail children)
        private string _filter = "ALL";
        private bool _paused;
        private string _lastTop;
        private int _lastCount = -1;
        private DateTime _lastTick = DateTime.MinValue;

        public LogsPanel(int canvasW)
        {
            BackColor = UiTheme.Ground;
            Width = canvasW;

            // Filter chip strip + actions; chips rebuild per source.
            _pause = MkChip("⏸ PAUSE");
            _pause.Click += (s, e) =>
            {
                _paused = !_paused;
                UiTheme.ApplyState(_pause, _paused ? UiTheme.Energy : UiTheme.BtnFace, _paused ? Color.White : UiTheme.Ink);
                if (!_paused) Rebuild(force: true);
            };
            _openFile = MkChip("OPEN FILE");
            _openFile.Click += (s, e) =>
            {
                try
                {
                    string file = _active == 1 ? "loot.log" : _active == 2 ? "inject.log" : null;
                    System.Diagnostics.Process.Start(file == null ? GetLogDir() : Path.Combine(GetLogDir(), file));
                }
                catch (Exception ex) { LogDebug($"Logs open: {ex.Message}"); }
            };

            _list = new ListBox
            {
                Bounds = new Rectangle(0, 34, canvasW - 20, 600),
                Font = UiTheme.Ui,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = UiTheme.Surface,
                ForeColor = UiTheme.Ink,
                SelectionMode = SelectionMode.None
            };
            Controls.Add(_list);
            Controls.Add(_pause);
            Controls.Add(_openFile);

            Height = 640;
            BuildChips();
            SelectSource(0);
            VisibleChanged += (s, e) => { if (Visible) Rebuild(force: true); };
        }

        private Button MkChip(string text)
        {
            var b = new Button { Text = text, Size = new Size(UiLayout.BtnWidth(text), 24), Font = UiTheme.Chip, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = UiTheme.Border;
            UiTheme.ApplyState(b, UiTheme.BtnFace, UiTheme.Ink);
            return b;
        }

        // Chips adapt to the source: rebuild the strip, then right-align PAUSE / OPEN FILE.
        private void BuildChips()
        {
            foreach (var c in _chips) { Controls.Remove(c); c.Dispose(); }
            _chips.Clear();

            int cx = 0;
            foreach (var cat in SourceFilters[_active])
            {
                var b = MkChip(cat);
                b.Location = new Point(cx, 2);
                string captured = cat;
                b.Click += (s, e) => { _filter = captured; StyleChips(); Rebuild(force: true); };
                Controls.Add(b);
                _chips.Add(b);
                cx += b.Width + 6;
            }
            _openFile.Location = new Point(_list.Right - _openFile.Width, 2);
            _pause.Location = new Point(_openFile.Left - _pause.Width - 6, 2);
            StyleChips();
        }

        private void StyleChips()
        {
            var cats = SourceFilters[_active];
            for (int i = 0; i < _chips.Count; i++)
                UiTheme.ApplyState(_chips[i], cats[i] == _filter ? UiTheme.Accent : UiTheme.BtnFace,
                    cats[i] == _filter ? Color.White : UiTheme.Ink);
        }

        // Called by the rail's LOGS children (A1 sub-nav owns source selection).
        public void SelectSource(int idx)
        {
            _active = Math.Max(0, Math.Min(SourceFilters.Length - 1, idx));
            _filter = "ALL";
            BuildChips();
            Rebuild(force: true);
        }

        public void TickLogs()
        {
            if (!Visible) return;
            if ((DateTime.UtcNow - _lastTick).TotalSeconds < 2) return;
            _lastTick = DateTime.UtcNow;
            if (!_paused) Rebuild();
        }

        private static bool LootMatch(string line, string filter)
        {
            switch (filter)
            {
                case "EXP · AP": return line.Contains(" EXP") || line.Contains(" AP");
                case "BOOSTS": return line.IndexOf("boost", StringComparison.OrdinalIgnoreCase) >= 0;
                case "DROPS": return !(line.Contains(" EXP") || line.Contains(" AP"));
                default: return true;
            }
        }

        private List<string> CurrentLines()
        {
            switch (_active)
            {
                case 0:
                    var src = ChallengeOverlay.Feed;
                    return _filter == "ALL"
                        ? src.ToList()
                        : src.Where(l => l.StartsWith($"[{_filter}]")).ToList();
                case 1:
                    return _filter == "ALL"
                        ? Main.LootFeed.ToList()
                        : Main.LootFeed.Where(l => LootMatch(l, _filter)).ToList();
                default:
                    return SessionTail();
            }
        }

        // Read the last ~200 lines of inject.log with a shared-read stream (the writer keeps the
        // file open; FileShare.ReadWrite is required or the open throws).
        private static List<string> SessionTail()
        {
            var outLines = new List<string>();
            try
            {
                var path = Path.Combine(GetLogDir(), "inject.log");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var all = new List<string>();
                    string line;
                    while ((line = sr.ReadLine()) != null) all.Add(line);
                    for (int i = all.Count - 1; i >= 0 && outLines.Count < 200; i--)
                        outLines.Add(all[i]);
                }
            }
            catch (Exception e) { outLines.Add($"(could not read inject.log: {e.Message})"); }
            return outLines;
        }

        private void Rebuild(bool force = false)
        {
            try
            {
                var lines = CurrentLines();
                string top = lines.Count > 0 ? lines[0] : null;
                if (!force && lines.Count == _lastCount && top == _lastTop) return;
                _lastCount = lines.Count;
                _lastTop = top;

                _list.BeginUpdate();
                try
                {
                    _list.Items.Clear();
                    if (lines.Count == 0)
                        _list.Items.Add(_active == 0 ? "(no advisor actions yet this session)" : "(nothing yet this session)");
                    else
                        foreach (var l in lines) _list.Items.Add(l);
                }
                finally { _list.EndUpdate(); }
            }
            catch (Exception ex) { LogDebug($"Logs panel: {ex.Message}"); }
        }
    }
}
