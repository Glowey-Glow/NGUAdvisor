using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.AllocationProfiles.BreakpointTypes;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // AUTO PROFILE card (B1) — the run plan (segment chips + E/M/R3 token lines) on the left, and a
    // READ-ONLY profile summary on the right. UI4 moved every MUTABLE profile control (the mode toggle,
    // file selector, SWITCH/EDIT/FILES/APPLY) out to the dedicated PROFILE section; this card now only
    // SHOWS allocation source / selected(standby) file / recommendation, with OPEN PROFILE as the route
    // to change any of it. Fully reflowed per refresh: wrapped text grows rows, never "…" on plan tokens.
    public class AutopilotPanel : Panel
    {
        private Button _openBtn;
        private Button _refresh;
        private Panel _card;
        private Label _title;
        private Label _eLine;
        private Label _mLine;
        private Label _rLine;
        private Label _note1;
        private Label _note2;
        private static readonly string[] SegOrder = { "TM HOUR", "AT HOUR", "RECOVERY", "MARATHON" };
        private readonly Label[] _segChips = new Label[4];
        private readonly SettingsForm _form;
        private Label _srcLine;      // read-only: allocation source
        private Label _fileLine;     // read-only: active/standby file
        private Label _recProfile;   // read-only: recommendation
        private readonly ToolTip _tips = new ToolTip();
        private readonly int _planW;    // left zone width
        private readonly int _stripX;   // right zone x inside the card

        public AutopilotPanel(SettingsForm form, int canvasW = 0)
        {
            int W = canvasW > 0 ? canvasW : UiLayout.PanelW;
            _form = form;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _openBtn = new Button { Text = "OPEN PROFILE →", Size = new Size(UiLayout.BtnWidth("OPEN PROFILE →"), 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_openBtn);
            _openBtn.Click += (s, e) => { try { _form?.NavigateTo(Destinations.Profile); } catch (Exception ex) { LogDebug($"Open profile: {ex.Message}"); } };
            _refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_refresh);
            _refresh.Click += (s, e) => RefreshView();
            Controls.Add(_openBtn);
            Controls.Add(_refresh);
            UiLayout.Row(10, 8, 8, _openBtn, _refresh);

            _card = new Panel { Location = new Point(10, 40), Size = new Size(W - 40, 170), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(_card);

            // Zones: plan left, read-only profile summary right (B1). A vertical divider keeps them readable.
            _stripX = _card.Width * 3 / 5 + 20;
            _planW = _stripX - 30;
            int stripW = _card.Width - _stripX - 10;
            _card.Controls.Add(new Panel { Location = new Point(_stripX - 10, 6), Size = new Size(1, 150), BackColor = UiTheme.Border, Tag = "exclusive" });

            _title = new Label { Text = "…", AutoSize = false, Size = new Size(_planW, UiTheme.TextH), Font = UiTheme.Bold, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(10, 6) };
            _card.Controls.Add(_title);
            for (int i = 0; i < SegOrder.Length; i++)
            {
                _segChips[i] = new Label
                {
                    Text = SegOrder[i], AutoSize = false,
                    Size = new Size(UiLayout.MeasureText($"{SegOrder[i]} ✓", UiTheme.Chip) + 14, 18),
                    Font = UiTheme.Chip, ForeColor = Color.White, BackColor = UiTheme.Faint,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                _card.Controls.Add(_segChips[i]);
            }
            _eLine = MkLine();
            _mLine = MkLine();
            _rLine = MkLine();

            // Read-only profile summary (right strip): source -> file -> recommendation. The mutable controls
            // live on the PROFILE page now; OPEN PROFILE (top row) is the route to them.
            _srcLine = MkStripLabel(stripW, 6, UiTheme.Ink);
            _fileLine = MkStripLabel(stripW, 32, UiTheme.Ink);
            _recProfile = MkStripLabel(stripW, 58, UiTheme.Accent);

            _note1 = new Label { Text = "", AutoSize = false, Size = new Size(stripW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(_stripX, 100) };
            _note2 = new Label { Text = "", AutoSize = false, Size = new Size(stripW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Faint, BackColor = UiTheme.Surface, Location = new Point(_stripX, 126) };
            _card.Controls.Add(_note1);
            _card.Controls.Add(_note2);

            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
            RefreshView();
        }

        private Label MkStripLabel(int stripW, int y, Color fore)
        {
            var l = new Label { Text = "", AutoSize = false, Size = new Size(stripW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = fore, BackColor = UiTheme.Surface, AutoEllipsis = false, Location = new Point(_stripX, y) };
            _card.Controls.Add(l);
            return l;
        }

        // Fixed-width measured fit + full text in the shared tooltip (Mono blanks an overflowing fixed label).
        private void SetSummary(Label l, string text)
        {
            _tips.SetToolTip(l, text ?? "");
            string fit = UiLayout.FitText(text ?? "", l.Font, l.Width);
            if (l.Text != fit) l.Text = fit;
        }

        private Label MkLine()
        {
            var l = new Label { Text = "", AutoSize = false, Size = new Size(_planW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Ink, BackColor = UiTheme.Surface, Location = new Point(10, 60) };
            _card.Controls.Add(l);
            return l;
        }

        // Read-only summary of the profile state — mirrors the PROFILE page's three concepts, no controls.
        private void UpdateSummary()
        {
            try
            {
                if (Settings == null) return;
                bool advisor = Settings.AutoProfile;
                string file = Settings.AllocationFile ?? "-";
                // Bounded FIXED-HEIGHT fit (never FitOrGrow): a long file/recommendation name must not grow
                // the summary label downward into the run-plan/notes — it truncates with the full text in a
                // tooltip. RefreshView is show/refresh-driven (not per-frame), so the tooltip set is cheap.
                SetSummary(_srcLine, advisor ? "Allocation source: advisor-generated" : "Allocation source: profile file");
                SetSummary(_fileLine, advisor ? $"Standby file: {file}" : $"Active file: {file}");
                string rec = "";
                try { var prog = ProgressionAnalyzer.Detect(); rec = prog.Known ? prog.RecommendedProfile : ""; } catch { }
                SetSummary(_recProfile, string.IsNullOrEmpty(rec) ? "Recommended: —" : $"Recommended: {rec}");
            }
            catch (Exception e) { LogDebug($"Autopilot summary: {e.Message}"); }
        }

        private static readonly string[] ENguNames = { "Augs", "Wandoos", "Respawn", "Gold", "Adv-α", "Power-α", "DC", "Magic", "PP" };
        private static readonly string[] MNguNames = { "Ygg", "EXP", "Power-β", "Number", "TM", "Energy", "Adv-β" };

        private static string Friendly(ResourceType type, string tok)
        {
            switch (tok)
            {
                case "CAPTM:5": return "TM 5%";
                case "CAPTM:30": return "TM 30%";
                case "CAPWAN:40": return "WAN 40%";
                case "CAPWAN:60": return "WAN 60%";
                case "BESTAUG": return "best aug";
                case "CAPALLAT": return "AT caps";
                case "ALLNGU": return "all NGU";
                case "ALLBT": return "all BT";
                case "BR-30": return "rituals";
                case "ALLHACK": return "all hacks";
            }
            if (tok.StartsWith("NGU-") && int.TryParse(tok.Substring(4), out var idx))
            {
                var names = type == ResourceType.Magic ? MNguNames : ENguNames;
                if (idx >= 0 && idx < names.Length) return $"NGU:{names[idx]}";
            }
            return tok;
        }

        private static string PlanLine(string prefix, ResourceType type)
        {
            var toks = ChallengeOverlay.AutoTokens(type).Select(t => Friendly(type, t)).ToArray();
            return toks.Length > 0 ? $"{prefix}: {string.Join(" → ", toks)}" : $"{prefix}: —";
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            RefreshView();
        }

        private void RefreshView()
        {
            try
            {
                if (Settings == null) return;
                UpdateSummary();
                bool on = Settings.AutoProfile;
                string challenge = null;
                try { challenge = ChallengeDetector.Current(); } catch { }

                if (!on)
                {
                    UiLayout.FitOrGrow(_title, "AUTO PROFILE — off");
                    _title.ForeColor = UiTheme.Muted;
                    UiLayout.FitOrGrow(_eLine, $"Allocation comes from the profile timeline: {Settings.AllocationFile ?? "-"}");
                    UiLayout.FitOrGrow(_mLine, "Flip the toggle and the advisor generates priorities from run phase + TM state.");
                    UiLayout.FitOrGrow(_rLine, "");
                }
                else if (challenge != null && Settings.AdvisorChallenges)
                {
                    UiLayout.FitOrGrow(_title, $"AUTO PROFILE — standing by ({challenge} overlay owns allocation)");
                    _title.ForeColor = UiTheme.Energy;
                    UiLayout.FitOrGrow(_eLine, "Challenge strips/templates outrank the generator while the challenge runs.");
                    UiLayout.FitOrGrow(_mLine, "Generation resumes the moment the challenge ends.");
                    UiLayout.FitOrGrow(_rLine, "");
                }
                else
                {
                    UiLayout.FitOrGrow(_title, $"AUTO PROFILE — {ChallengeOverlay.AutoStatus()}");
                    _title.ForeColor = UiTheme.Accent;
                    var mTokens = ChallengeOverlay.AutoTokens(ResourceType.Magic);
                    string ritual = Array.IndexOf(mTokens, "BR-30") < 0 ? "   · rituals off (no live consumer)" : "";
                    // The run plan is the one thing that must never truncate.
                    UiLayout.FitOrGrow(_eLine, PlanLine("E", ResourceType.Energy));
                    UiLayout.FitOrGrow(_mLine, PlanLine("M", ResourceType.Magic) + ritual);
                    UiLayout.FitOrGrow(_rLine, PlanLine("R3", ResourceType.R3));
                }

                // Timeline chips: window passed = green ✓, current = gold ←, future = grey.
                double runSec = 0;
                try { runSec = Main.Character.rebirthTime.totalseconds; } catch { }
                string cur = ChallengeOverlay.Segment;
                double[] windowEnd = { 3600, 7200, 14400, double.MaxValue };
                for (int i = 0; i < SegOrder.Length; i++)
                {
                    bool current = SegOrder[i] == cur || (SegOrder[i] == "MARATHON" && cur == "NGU MARATHON");
                    bool walked = !current && runSec >= windowEnd[i];
                    _segChips[i].Text = current ? $"{SegOrder[i]} ←" : walked ? $"{SegOrder[i]} ✓" : SegOrder[i];
                    _segChips[i].BackColor = current ? UiTheme.Energy : walked ? UiTheme.Cap : UiTheme.Faint;
                    _segChips[i].ForeColor = Color.White;
                }

                string caps = string.IsNullOrEmpty(LevelPlanner.Status) ? "" : $"caps: {LevelPlanner.Status}";
                UiLayout.FitOrGrow(_note1, caps);
                string runLen = OptimizationAdvisor.RecommendedRunLength();
                UiLayout.FitOrGrow(_note2, string.IsNullOrEmpty(runLen) ? "" : $"Guide run length: {runLen}");

                Reflow();
            }
            catch (Exception ex) { LogDebug($"Autopilot panel: {ex.Message}"); }
        }

        // Vertical reflow of both zones; the card takes whichever column runs deeper.
        private void Reflow()
        {
            try
            {
                // Plan zone (left): title → chips row → E/M/R3.
                int cy = _title.Bottom + 6;
                int cx = 10;
                for (int i = 0; i < _segChips.Length; i++)
                {
                    if (cx + _segChips[i].Width > _planW && cx > 10) { cx = 10; cy += 22; }
                    _segChips[i].Location = new Point(cx, cy);
                    cx += _segChips[i].Width + 5;
                }
                int afterChips = cy + 22 + 4;
                _eLine.Top = afterChips;
                _mLine.Top = _eLine.Bottom + 2;
                _rLine.Top = _mLine.Bottom + 2;

                // Profile summary (right): source → file → recommendation → notes (all read-only).
                _srcLine.Top = 6;
                _fileLine.Top = _srcLine.Bottom + 4;
                _recProfile.Top = _fileLine.Bottom + 8;
                _note1.Top = _recProfile.Bottom + 12;
                _note2.Top = _note1.Bottom + 4;

                _card.Height = Math.Max(_rLine.Bottom, _note2.Bottom) + 10;
                Height = _card.Bottom + 8;
            }
            catch (Exception ex) { LogDebug($"Autopilot reflow: {ex.Message}"); }
        }
    }
}
