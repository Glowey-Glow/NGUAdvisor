using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Economy > PIT (draft-approved): three status chips — NEXT TOSS (cooldown + toss count this
    // run), PREDICTION (RNG-read outcome + its prep loadout), THROW PLAN (the shared advisor policy
    // verdict) — with the ADVISOR THROWS GOLD toggle and the manual strip (min tier, Predict, Pit
    // Run, Daily Spin, Throw Now). The plan chip renders MoneyPitManager.AdvisorPlan, the same
    // policy ApplyPit acts on, so display and behavior cannot disagree.
    public class PitPanel : Panel
    {
        private class Chip
        {
            public Panel Box;
            public Label Title;
            public Label Value;
            public Label Sub;
        }

        private Button _srcToggle;
        private Button _throwNow;
        private Button _refresh;
        private readonly Chip[] _chips = new Chip[3];

        private Panel _manualStrip;
        private ComboBox _minTier;
        private Button _predict;
        private Button _pitRun;
        private Button _dailySpin;
        private Button _swapDiggers;
        private Button _daycare;
        private NumericUpDown _daycareTh;
        private Label _advisorNote;
        private Label _shockNote;

        private bool _syncing;
        private const string ShockAdvice = "Shockwave set not configured — a Pit Run is worth considering once you have Worn gear to farm.";

        // canvasW: explicit canvas width when hosted in an M1 section column (0 = UiLayout.PanelW).
        public PitPanel(int canvasW = 0)
        {
            int W = canvasW > 0 ? canvasW : UiLayout.PanelW;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _srcToggle = MkBtn("ADVISOR THROWS GOLD");
            _srcToggle.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.AdvisorPit = !Settings.AdvisorPit;
                SyncFromSettings();
            };
            _throwNow = MkBtn("Throw Now");
            UiTheme.StyleFlat(_throwNow);
            _throwNow.Click += (s, e) =>
            {
                if (Settings == null) return;
                if (!MoneyPitManager.MoneyPitReady()) { Log("Money pit is on cooldown."); return; }
                Log("Money pit: manual throw");
                MoneyPitManager.AdvisorThrow();
                RefreshChips();
            };
            _refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_refresh);
            _refresh.Click += (s, e) => RefreshChips();
            Controls.Add(_srcToggle);
            Controls.Add(_throwNow);
            Controls.Add(_refresh);
            UiLayout.Row(10, 10, 8, _srcToggle, _throwNow, _refresh);

            string[] titles = { "NEXT TOSS", "PREDICTION", "THROW PLAN" };
            int chipW = (W - 36) / 3;   // three chips, 10px margins + 8px gaps
            int x = 10;
            for (int i = 0; i < 3; i++)
            {
                var ch = new Chip();
                // Two-line sub-caption budget (round-3: "TOSS #5 THIS RUN …" never again).
                ch.Box = new Panel { Location = new Point(x, 48), Size = new Size(chipW, 88), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
                ch.Title = new Label { Text = titles[i], AutoSize = true, Font = UiTheme.Chip, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(6, 4) };
                ch.Value = new Label { Text = "…", AutoSize = false, Size = new Size(chipW - 12, 22), Font = UiTheme.Bold, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(6, 22) };
                ch.Sub = new Label { Text = "", AutoSize = false, Size = new Size(chipW - 12, 36), Font = UiTheme.Chip, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(6, 48) };
                ch.Box.Controls.Add(ch.Title);
                ch.Box.Controls.Add(ch.Value);
                ch.Box.Controls.Add(ch.Sub);
                Controls.Add(ch.Box);
                _chips[i] = ch;
                x += chipW + 8;
            }

            _manualStrip = new Panel { Location = new Point(0, 148), Size = new Size(W - 4, 34), BackColor = UiTheme.Ground, Tag = "exclusive" };
            Controls.Add(_manualStrip);
            var tierLbl = new Label { Text = "min tier", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _minTier = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            foreach (var t in MoneyPitManager.moneyPitThresholds)
                _minTier.Items.Add(MoneyPitManager.TierName(t));
            _minTier.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null || _minTier.SelectedIndex < 0) return;
                Settings.MoneyPitThreshold = MoneyPitManager.moneyPitThresholds[_minTier.SelectedIndex];
            };
            _predict = MkTrig("Predict + Prep", () => Settings.PredictMoneyPit = !Settings.PredictMoneyPit);
            _pitRun = MkTrig("Pit Run Mode", () => Settings.MoneyPitRunMode = !Settings.MoneyPitRunMode);
            _dailySpin = MkTrig("Daily Spin", () => Settings.AutoSpin = !Settings.AutoSpin);
            _manualStrip.Controls.Add(tierLbl);
            _manualStrip.Controls.Add(_minTier);
            _manualStrip.Controls.Add(_predict);
            _manualStrip.Controls.Add(_pitRun);
            _manualStrip.Controls.Add(_dailySpin);
            UiLayout.Row(10, 4, 8, tierLbl, _minTier, _predict, _pitRun, _dailySpin);

            _advisorNote = new Label
            {
                AutoSize = false,
                Size = new Size(W - 20, UiTheme.TextH),
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground,
                Location = new Point(10, 154),
                Tag = "exclusive"
            };
            UiLayout.FitOrGrow(_advisorNote,
                "Throws only when TM is funded and augments stay affordable; holds when the next reward tier is close.");
            Controls.Add(_advisorNote);

            _shockNote = new Label
            {
                AutoSize = false,
                Size = new Size(W - 20, UiTheme.TextH),
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground,
                Location = new Point(10, Math.Max(148 + _manualStrip.Height + 8, _advisorNote.Bottom + 8))
            };
            // Reserve the wrapped height up front so the rows below never collide with it.
            UiLayout.FitOrGrow(_shockNote, ShockAdvice);
            int belowShock = _shockNote.Bottom + 10;
            _shockNote.Text = "";
            Controls.Add(_shockNote);

            // Re-homed from the retired Old Pit page (Phase B): digger swap + daycare feed.
            _swapDiggers = MkTrig("Pit Diggers", () => Settings.SwapPitDiggers = !Settings.SwapPitDiggers);
            _daycare = MkTrig("Daycare Feed", () => Settings.MoneyPitDaycare = !Settings.MoneyPitDaycare);
            var dcLbl = new Label { Text = "daycare ≥", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _daycareTh = new NumericUpDown { Width = 60, Minimum = 0, Maximum = 100, Font = UiTheme.Ui };
            _daycareTh.ValueChanged += (s, e) => { if (!_syncing && Settings != null) Settings.DaycareThreshold = (int)_daycareTh.Value; };
            Controls.Add(_swapDiggers);
            Controls.Add(_daycare);
            Controls.Add(dcLbl);
            Controls.Add(_daycareTh);
            UiLayout.Row(10, Math.Max(204, belowShock), 8, _swapDiggers, _daycare, dcLbl, _daycareTh);

            VisibleChanged += (s, e) => { if (Visible) RefreshChips(); };
            SyncFromSettings();
        }

        private static Button MkBtn(string text)
        {
            var b = new Button { Text = text, Size = new Size(UiLayout.BtnWidth(text), 24), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = UiTheme.Border;
            return b;
        }

        private Button MkTrig(string text, Action toggle)
        {
            var b = MkBtn(text);
            b.Click += (s, e) =>
            {
                if (Settings == null) return;
                try { toggle(); } catch (Exception ex) { LogDebug($"Pit toggle: {ex.Message}"); }
                SyncFromSettings();
            };
            return b;
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            _syncing = true;
            try
            {
                bool advisor = Settings.AdvisorPit;
                _srcToggle.Text = advisor ? "ADVISOR THROWS GOLD" : "MANUAL PIT";
                UiTheme.ApplyState(_srcToggle, advisor ? UiTheme.Cap : UiTheme.Danger, Color.White);
                _manualStrip.Visible = !advisor;
                _advisorNote.Visible = advisor;

                int idx = MoneyPitManager.moneyPitThresholds.FindIndex(t => Math.Abs(t - Settings.MoneyPitThreshold) < t * 0.01);
                if (idx >= 0) _minTier.SelectedIndex = idx;
                UiTheme.ApplyState(_predict, Settings.PredictMoneyPit ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_pitRun, Settings.MoneyPitRunMode ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_dailySpin, Settings.AutoSpin ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_swapDiggers, Settings.SwapPitDiggers ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_daycare, Settings.MoneyPitDaycare ? UiTheme.Cap : UiTheme.Danger, Color.White);
                _daycareTh.Value = Math.Max(0, Math.Min(100, Settings.DaycareThreshold));
            }
            finally { _syncing = false; }
            RefreshChips();
        }

        private void SetChip(int i, bool lit, string value, string sub)
        {
            var ch = _chips[i];
            var bg = lit ? Color.FromArgb(253, 246, 233) : UiTheme.Surface;
            ch.Box.BackColor = bg;
            ch.Title.BackColor = ch.Value.BackColor = ch.Sub.BackColor = bg;
            ch.Title.ForeColor = lit ? UiTheme.Energy : UiTheme.Muted;
            ch.Value.Text = Fit(value, UiTheme.Bold, ch.Value.Width - 2);
            ch.Sub.Text = UiLayout.WrapText(sub, UiTheme.Chip, ch.Sub.Width - 2, 2);
        }

        private static string Fit(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (UiLayout.MeasureText(text, font) <= width) return text;
            while (text.Length > 1 && UiLayout.MeasureText(text + "…", font) > width)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        private void RefreshChips()
        {
            try
            {
                var c = Main.Character;
                if (c == null || Settings == null) return;

                // NEXT TOSS.
                bool ready = MoneyPitManager.MoneyPitReady();
                int toss = 0;
                try { toss = c.pit.tossCount; } catch { }
                string cd;
                if (ready) cd = "READY";
                else
                {
                    float t = MoneyPitManager.TimeUntilReady();
                    cd = t > 3600 ? $"in {t / 3600:0.#}h" : $"in {t / 60:0}m";
                }
                SetChip(0, ready, cd, $"TOSS #{toss + 1} THIS RUN ({toss + 2}H CD AFTER)");

                // PREDICTION.
                double gold = c.realGold;
                if (gold >= 1e13)
                {
                    var outcome = MoneyPitManager.PredictNext();
                    string prep;
                    switch (outcome)
                    {
                        case MoneyPitManager.Outcomes.IronPill: prep = "PREP: MAGIC + RITUALS"; break;
                        case MoneyPitManager.Outcomes.Worn: prep = "PREP: SHOCKWAVE SET"; break;
                        case MoneyPitManager.Outcomes.Exp: prep = "PREP: EXP GEAR"; break;
                        case MoneyPitManager.Outcomes.Pomegranate: prep = "PREP: YGG LOADOUT"; break;
                        case MoneyPitManager.Outcomes.Daycare: prep = "PREP: FILL DAYCARE"; break;
                        default: prep = "NO SPECIAL OUTCOME"; break;
                    }
                    SetChip(1, outcome != MoneyPitManager.Outcomes.None, outcome == MoneyPitManager.Outcomes.None ? "STANDARD" : outcome.ToString().ToUpperInvariant(), prep);
                }
                else
                {
                    SetChip(1, false, "STANDARD", "OUTCOMES START AT 1E13");
                }

                // THROW PLAN (advisor policy) / manual summary.
                if (Settings.AdvisorPit)
                {
                    var plan = MoneyPitManager.AdvisorPlan();
                    SetChip(2, plan.Throw, plan.Verdict, plan.Detail);
                }
                else
                {
                    SetChip(2, false, $"MANUAL — min {MoneyPitManager.TierName(Settings.MoneyPitThreshold)}", Settings.AutoMoneyPit ? "AUTO THROW AT THRESHOLD" : "AUTO MONEY PIT IS OFF");
                }

                // Shockwave advice.
                bool shockEmpty = Settings.Shockwave == null || Settings.Shockwave.Length == 0;
                if (shockEmpty) UiLayout.FitOrGrow(_shockNote, ShockAdvice);
                else _shockNote.Text = "";
            }
            catch (Exception ex) { LogDebug($"Pit chips: {ex.Message}"); }
        }
    }
}
