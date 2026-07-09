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
    // AUTO PROFILE card (B1, user-approved): one full-width card on the Advisors home — the run
    // plan (segment chips + E/M/R3 token lines) on the left, the profile strip (recommendation,
    // switcher, caps/run notes) docked on the right. Fully reflowed per refresh: wrapped text grows
    // rows, never "…" on plan tokens. The challenge block owns everything below this panel.
    public class AutopilotPanel : Panel
    {
        private Button _toggle;
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
        private Label _recProfile;
        private Button _applyRec;
        private ComboBox _profileCombo;
        private Button _switchBtn, _editBtn, _filesBtn;
        private string _recommended = "";
        private readonly int _planW;    // left zone width
        private readonly int _stripX;   // right zone x inside the card

        public AutopilotPanel(SettingsForm form, int canvasW = 0)
        {
            int W = canvasW > 0 ? canvasW : UiLayout.PanelW;
            _form = form;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _toggle = new Button { Text = "AUTO PROFILE ACTIVE", Size = new Size(UiLayout.BtnWidth("AUTO PROFILE ACTIVE"), 24), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            _toggle.FlatAppearance.BorderColor = UiTheme.Border;
            _toggle.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.AutoProfile = !Settings.AutoProfile;
                Log(Settings.AutoProfile
                    ? "Auto profile ON — allocation now generated (profile file on standby)"
                    : $"Auto profile OFF — back to {Settings.AllocationFile ?? "profile"} timeline");
                SyncFromSettings();
            };
            _refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_refresh);
            _refresh.Click += (s, e) => RefreshView();
            Controls.Add(_toggle);
            Controls.Add(_refresh);
            UiLayout.Row(10, 8, 8, _toggle, _refresh);

            _card = new Panel { Location = new Point(10, 40), Size = new Size(W - 40, 170), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(_card);

            // Zones: plan left, profile strip right (B1). A vertical divider keeps them readable.
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

            _recProfile = new Label { Text = "", AutoSize = false, Size = new Size(stripW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(_stripX, 6) };
            _card.Controls.Add(_recProfile);
            _profileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Math.Min(220, stripW - 8), Font = UiTheme.Ui };
            _card.Controls.Add(_profileCombo);
            _switchBtn = MkStripBtn("SWITCH");
            _switchBtn.Click += (s, e) => { var sel = _profileCombo.SelectedItem?.ToString(); if (!string.IsNullOrEmpty(sel)) ApplyProfileByName(sel); };
            _editBtn = MkStripBtn("EDIT");
            _editBtn.Click += (s, e) => { try { ProfileEditorForm.ShowEditor(GetProfilesDir(), Settings.AllocationFile); } catch (Exception ex) { LogDebug($"Autopilot edit: {ex.Message}"); } };
            _filesBtn = MkStripBtn("FILES");
            _filesBtn.Click += (s, e) => { try { System.Diagnostics.Process.Start(GetProfilesDir()); } catch (Exception ex) { LogDebug($"Autopilot files: {ex.Message}"); } };
            _applyRec = new Button { Text = "APPLY", Size = new Size(UiLayout.BtnWidth("APPLY"), 24), Font = UiTheme.Ui, Visible = false };
            UiTheme.StylePrimary(_applyRec);
            _applyRec.Click += (s, e) => { if (!string.IsNullOrEmpty(_recommended)) ApplyProfileByName(_recommended); };
            _card.Controls.Add(_applyRec);

            _note1 = new Label { Text = "", AutoSize = false, Size = new Size(stripW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(_stripX, 100) };
            _note2 = new Label { Text = "", AutoSize = false, Size = new Size(stripW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Faint, BackColor = UiTheme.Surface, Location = new Point(_stripX, 126) };
            _card.Controls.Add(_note1);
            _card.Controls.Add(_note2);

            LoadProfiles();
            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
            SyncFromSettings();
        }

        private Button MkStripBtn(string text)
        {
            var b = new Button { Text = text, Size = new Size(UiLayout.BtnWidth(text), 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(b);
            _card.Controls.Add(b);
            return b;
        }

        private Label MkLine()
        {
            var l = new Label { Text = "", AutoSize = false, Size = new Size(_planW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Ink, BackColor = UiTheme.Surface, Location = new Point(10, 60) };
            _card.Controls.Add(l);
            return l;
        }

        private void LoadProfiles()
        {
            try
            {
                var names = System.IO.Directory.GetFiles(GetProfilesDir())
                    .Select(System.IO.Path.GetFileNameWithoutExtension).OrderBy(n => n).ToArray();
                _profileCombo.Items.Clear();
                _profileCombo.Items.AddRange(names);
                if (Settings != null) _profileCombo.SelectedItem = Settings.AllocationFile;
            }
            catch (Exception e) { LogDebug($"Autopilot LoadProfiles: {e.Message}"); }
        }

        private void ApplyProfileByName(string name)
        {
            try
            {
                _form?.ApplyProfile(name);
                LoadProfiles();
                RefreshView();
            }
            catch (Exception e) { LogDebug($"Autopilot apply: {e.Message}"); }
        }

        private void UpdateProfileStrip()
        {
            try
            {
                var prog = ProgressionAnalyzer.Detect();
                _recommended = prog.Known ? prog.RecommendedProfile : "";
                string active = Settings?.AllocationFile ?? "";
                bool applyVisible = !string.IsNullOrEmpty(_recommended) && _recommended != active;
                string recText = string.IsNullOrEmpty(_recommended) ? ""
                    : applyVisible ? $"Recommended: {_recommended} — {prog.RecommendReason}"
                    : $"Recommended: {_recommended} (current ✓)";
                UiLayout.FitOrGrow(_recProfile, recText);
                _applyRec.Visible = applyVisible;
                if (_profileCombo.SelectedItem?.ToString() != active)
                    _profileCombo.SelectedItem = active;
            }
            catch (Exception e) { LogDebug($"Autopilot strip: {e.Message}"); }
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
            bool on = Settings.AutoProfile;
            _toggle.Text = on ? "AUTO PROFILE ACTIVE" : "AUTO PROFILE OFF";
            UiTheme.ApplyState(_toggle, on ? UiTheme.Cap : UiTheme.Danger, Color.White);
            RefreshView();
        }

        private void RefreshView()
        {
            try
            {
                if (Settings == null) return;
                UpdateProfileStrip();
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

                // Profile strip (right): recommendation → combo row → APPLY/buttons → notes.
                int sy = _recProfile.Bottom + 6;
                _profileCombo.Location = new Point(_stripX, sy);
                int bx = _stripX;
                int by = sy + 30;
                foreach (var b in new[] { _switchBtn, _editBtn, _filesBtn, _applyRec })
                {
                    if (bx + b.Width > _card.Width - 10 && bx > _stripX) { bx = _stripX; by += 30; }
                    b.Location = new Point(bx, by);
                    bx += b.Width + 6;
                }
                _note1.Top = by + 32;
                _note2.Top = _note1.Bottom + 4;

                _card.Height = Math.Max(_rLine.Bottom, _note2.Bottom) + 10;
                Height = _card.Bottom + 8;
            }
            catch (Exception ex) { LogDebug($"Autopilot reflow: {ex.Message}"); }
        }
    }
}
