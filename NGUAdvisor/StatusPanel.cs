using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Route C3 Phase 2: the live status strip docked to the bottom of the advisor Settings window. Built
    // from standard Label controls (NOT custom OnPaint) because labels repaint reliably in this normal
    // window under the injected Mono - the earlier custom-paint/floating-window attempts did not. Shows what
    // the automation is doing and what the player is working toward. Updated from Main.Update (main thread).
    public class StatusPanel : Panel
    {
        // Two rows of four cells (AUTO + 7 chips), so values have room in the ~690px-wide settings window.
        public const int PanelHeight = 94;
        private const int Pad = 12;
        private const int Cols = 4;
        private const int Row1CapY = 6, Row1ValY = 24, Row2CapY = 50, Row2ValY = 68;

        private class Chip { public Label Cap; public Label Val; }

        private readonly List<string> _order = new List<string>
            { "STAGE", "PROFILE", "CURRENT GOAL", "GEAR", "REBIRTH", "RESOURCES", "NEXT GOAL" };
        private readonly Dictionary<string, Chip> _chips = new Dictionary<string, Chip>();
        private readonly Panel _light;
        private readonly Label _autoCap;
        // Wrap-safe throttle. (Environment.TickCount goes NEGATIVE after ~24.9 days uptime, which made the
        // old `TickCount - last < 250` check true forever => the strip never updated. Root cause of the saga.)
        private DateTime _lastContent = DateTime.MinValue;

        public StatusPanel()
        {
            Dock = DockStyle.Bottom;
            Height = PanelHeight;
            BackColor = UiTheme.Surface;

            Controls.Add(new Panel { Dock = DockStyle.Top, Height = 3, BackColor = UiTheme.Accent });

            _autoCap = MakeCaption("AUTO");
            Controls.Add(_autoCap);
            _light = new Panel { Size = new Size(14, 14), BackColor = UiTheme.Danger };
            Controls.Add(_light);

            // Click the AUTO light (or caption) to toggle automation on/off.
            _light.Cursor = Cursors.Hand;
            _autoCap.Cursor = Cursors.Hand;
            _light.Click += ToggleAuto;
            _autoCap.Click += ToggleAuto;

            foreach (var name in _order)
            {
                var cap = MakeCaption(name);
                // AutoEllipsis=false: Mono paints NOTHING when AutoEllipsis text overflows (cause of the
                // missing dashboard AT row). Plain overflow clips instead, which always paints.
                var val = new Label { AutoSize = false, Height = 20, ForeColor = UiTheme.Ink, Font = UiTheme.Bold, BackColor = UiTheme.Surface, AutoEllipsis = false, Text = "-" };
                Controls.Add(cap);
                Controls.Add(val);
                _chips[name] = new Chip { Cap = cap, Val = val };
            }

            Resize += (s, e) => DoLayout();
            DoLayout();
        }

        private void ToggleAuto(object sender, EventArgs e)
        {
            try
            {
                if (Settings == null) return;
                Settings.GlobalEnabled = !Settings.GlobalEnabled;
                _light.BackColor = Settings.GlobalEnabled ? UiTheme.Cap : UiTheme.Danger;
            }
            catch (Exception ex) { LogDebug($"AUTO toggle failed: {ex.Message}"); }
        }

        private static Label MakeCaption(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.ColHeader,
            BackColor = UiTheme.Surface
        };

        // 8 cells (AUTO + 7 chips) laid out as 2 rows x 4 columns. Cell 0 = AUTO; cells 1..7 = the chips.
        private void DoLayout()
        {
            int colW = Math.Max(60, (Width - Pad * 2) / Cols);

            CellPos(0, colW, out int ax, out int acy, out int avy);
            _autoCap.Location = new Point(ax, acy);
            _light.Location = new Point(ax + 1, avy + 2);

            for (int i = 0; i < _order.Count; i++)
            {
                var ch = _chips[_order[i]];
                CellPos(i + 1, colW, out int x, out int capY, out int valY);
                ch.Cap.Location = new Point(x, capY);
                ch.Val.Location = new Point(x, valY);
                ch.Val.Width = Math.Max(30, colW - 10);
            }
        }

        private static void CellPos(int cell, int colW, out int x, out int capY, out int valY)
        {
            int col = cell % Cols;
            int row = cell / Cols;
            x = Pad + col * colW;
            capY = row == 0 ? Row1CapY : Row2CapY;
            valY = row == 0 ? Row1ValY : Row2ValY;
        }

        // Called each frame from Main.Update (main thread). Throttled; reads live state and updates labels.
        public void UpdateStatus()
        {
            try
            {
                if ((DateTime.UtcNow - _lastContent).TotalMilliseconds < 250) return;
                _lastContent = DateTime.UtcNow;

                var c = Main.Character;
                _light.BackColor = Settings != null && Settings.GlobalEnabled ? UiTheme.Cap : UiTheme.Danger;

                var prog = ProgressionAnalyzer.Detect();
                var challenge = ChallengeDetector.Current();

                Set("STAGE", prog.Known ? prog.Label : "-", UiTheme.Ink);
                Set("PROFILE", Settings.AutoProfile ? "AUTO (advisor)" : Settings.AllocationFile ?? "-",
                    Settings.AutoProfile ? UiTheme.Accent : UiTheme.Ink);
                Set("CURRENT GOAL", prog.Known ? prog.Activity : "-", challenge != null ? UiTheme.Accent : UiTheme.Ink);

                string gear = "Auto";
                if (challenge != null)
                {
                    var def = ChallengeDetector.DefaultGear(challenge);
                    if (def != null) gear = def.Objective;
                }
                Set("GEAR", gear, UiTheme.Ink);

                Set("REBIRTH", RebirthElapsed(c), UiTheme.Ink);
                Set("RESOURCES", Resources(c), UiTheme.Ink);
                Set("NEXT GOAL", prog.Known ? Capitalize(prog.NextGoal) : "-", UiTheme.Ink);

                Refresh(); // force a synchronous repaint (label Text changes don't always repaint on their own)
            }
            catch (Exception e) { LogDebug($"StatusPanel update failed: {e.Message}"); }
        }

        private void Set(string name, string value, Color color)
        {
            if (!_chips.TryGetValue(name, out var ch)) return;
            if (ch.Val.Text != value) ch.Val.Text = value;
            if (ch.Val.ForeColor != color) ch.Val.ForeColor = color;
        }

        private static string RebirthElapsed(Character c)
        {
            try
            {
                int s = (int)Math.Floor(c.rebirthTime.totalseconds);
                int h = s / 3600, m = (s % 3600) / 60, sec = s % 60;
                return h > 0 ? $"{h}h {m}m" : (m > 0 ? $"{m}m {sec}s" : $"{sec}s");
            }
            catch { return "-"; }
        }

        private static string Resources(Character c)
        {
            try
            {
                string e = Pct(c.curEnergy, c.totalCapEnergy());
                string m = Pct(c.magic.curMagic, c.totalCapMagic());
                string r = c.res3 != null && c.res3.res3On ? Pct(c.res3.curRes3, c.totalCapRes3()) : "-";
                return $"E {e}  M {m}  R3 {r}";
            }
            catch { return "-"; }
        }

        private static string Pct(double cur, double cap)
            => cap > 0 ? $"{(int)Math.Round(Math.Min(cur, cap) / cap * 100)}%" : "-";

        private static string Capitalize(string s)
            => string.IsNullOrEmpty(s) ? "-" : char.ToUpper(s[0]) + s.Substring(1);
    }
}
