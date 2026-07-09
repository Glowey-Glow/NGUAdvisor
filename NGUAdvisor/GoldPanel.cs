using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Economy > GOLD, pipeline v2 (user-revised): THREE stage chips with the ALL-CAPS status grammar
    // (ACTIVE:/WAITING:), Time Machine shows its gold TOTAL (single-decimal suffix), the bank chip is
    // honest about challenge-limited banking, and the old SPENDING chip is replaced by a full-width
    // GOLD DRAIN ledger (gross -> net gps, per-consumer rows: diggers + blood rituals, augment status).
    public class GoldPanel : Panel
    {
        private class Stage
        {
            public Panel Box;
            public Label Title;
            public Label Value;
            public Label Sub;
        }

        private Button _srcToggle;
        private Button _snipeNow;
        private Button _resetBanks;
        private Button _refresh;
        private readonly Stage[] _stages = new Stage[3];

        private Panel _manualStrip;
        private Button _trigNewZone;
        private Button _trigRebirth;
        private Button _trigStarved;
        private Button _trigTimer;
        private NumericUpDown _timerMin;
        private Label _minLbl;
        private Button _cblock;
        private Label _advisorNote;

        private Label _grossNet;
        private Label _digVal;
        private Panel _digBarOuter;
        private Panel _digBarInner;
        private Label _bloodVal;
        private Panel _bloodBarOuter;
        private Panel _bloodBarInner;
        private Label _augVal;

        private bool _syncing;

        // canvasW: explicit canvas width when hosted in an M1 section column (0 = UiLayout.PanelW).
        public GoldPanel(int canvasW = 0)
        {
            int W = canvasW > 0 ? canvasW : UiLayout.PanelW;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _srcToggle = MkBtn("ADVISOR MANAGES GOLD");
            _srcToggle.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.AdvisorGold = !Settings.AdvisorGold;
                SyncFromSettings();
            };
            _snipeNow = MkBtn("Snipe Now");
            UiTheme.StyleFlat(_snipeNow);
            _snipeNow.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.GoldSnipeComplete = false;
                LastSnipeTrigger = "manual";
                Log("Re-snipe: manual");
                RefreshPipeline();
            };
            _resetBanks = MkBtn("Reset Banks");
            UiTheme.StyleFlat(_resetBanks);
            _resetBanks.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.TitanMoneyDone = new bool[ZoneHelpers.TitanZones.Length];
                Log("Titan gold banks reset — all AK'd titans will re-bank");
                RefreshPipeline();
            };
            _refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_refresh);
            _refresh.Click += (s, e) => RefreshPipeline();
            Controls.Add(_srcToggle);
            Controls.Add(_snipeNow);
            Controls.Add(_resetBanks);
            Controls.Add(_refresh);
            UiLayout.Row(10, 10, 8, _srcToggle, _snipeNow, _resetBanks, _refresh);

            // Three stage chips + two 16px arrows, stretched across the PanelW canvas.
            string[] titles = { "ZONE SNIPE", "TIME MACHINE", "TITAN BANK" };
            int stageW = (W - 20 - 32) / 3;
            int x = 10;
            for (int i = 0; i < 3; i++)
            {
                var st = new Stage();
                // Two-line sub-caption budget (round-3: "WAITING: 3 TRIGGE…" never again).
                st.Box = new Panel { Location = new Point(x, 48), Size = new Size(stageW, 88), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
                st.Title = new Label { Text = titles[i], AutoSize = true, Font = UiTheme.Chip, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(6, 4) };
                st.Value = new Label { Text = "…", AutoSize = false, Size = new Size(stageW - 12, 22), Font = UiTheme.Bold, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(6, 22) };
                st.Sub = new Label { Text = "", AutoSize = false, Size = new Size(stageW - 12, 36), Font = UiTheme.Chip, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(6, 48) };
                st.Box.Controls.Add(st.Title);
                st.Box.Controls.Add(st.Value);
                st.Box.Controls.Add(st.Sub);
                Controls.Add(st.Box);
                _stages[i] = st;
                x += stageW;
                if (i < 2)
                {
                    var arrow = new Label { Text = "→", AutoSize = false, Size = new Size(16, 22), Font = UiTheme.Bold, ForeColor = UiTheme.Faint, BackColor = UiTheme.Ground, Location = new Point(x, 80), TextAlign = ContentAlignment.MiddleCenter };
                    Controls.Add(arrow);
                    x += 16;
                }
            }

            // Trigger strip (manual) / note (advisor) — below the taller chips.
            _manualStrip = new Panel { Location = new Point(0, 148), Size = new Size(W - 4, 34), BackColor = UiTheme.Ground, Tag = "exclusive" };
            Controls.Add(_manualStrip);
            var trigLbl = new Label { Text = "re-snipe on:", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _trigNewZone = MkTrig("New Zone", () => Settings.SnipeOnNewZone = !Settings.SnipeOnNewZone);
            _trigRebirth = MkTrig("Rebirth", () => Settings.SnipeOnRebirth = !Settings.SnipeOnRebirth);
            _trigStarved = MkTrig("Gold Starved", () => Settings.SnipeOnGoldStarved = !Settings.SnipeOnGoldStarved);
            _trigTimer = MkTrig("Timer", () => Settings.SnipeOnTimer = !Settings.SnipeOnTimer);
            _timerMin = new NumericUpDown { Width = 56, Minimum = 1, Maximum = 240, Font = UiTheme.Ui };
            _timerMin.ValueChanged += (s, e) => { if (!_syncing && Settings != null) Settings.ResnipeTime = (int)_timerMin.Value * 60; };
            _minLbl = new Label { Text = "min into run", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _cblock = MkTrig("CBlock Snipe", () => Settings.GoldCBlockMode = !Settings.GoldCBlockMode);
            _manualStrip.Controls.Add(trigLbl);
            _manualStrip.Controls.Add(_trigNewZone);
            _manualStrip.Controls.Add(_trigRebirth);
            _manualStrip.Controls.Add(_trigStarved);
            _manualStrip.Controls.Add(_trigTimer);
            _manualStrip.Controls.Add(_timerMin);
            _manualStrip.Controls.Add(_minLbl);
            _manualStrip.Controls.Add(_cblock);
            // Wraps in narrow M1 columns; the ledger reflows below whichever strip is taller.
            int stripBottom = UiLayout.WrapRow(10, 4, 6, _manualStrip.Width - 10, 30, new Control[] { trigLbl, _trigNewZone, _trigRebirth, _trigStarved, _trigTimer, _timerMin, _minLbl, _cblock });
            _manualStrip.Height = stripBottom + 2;

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
                "Re-snipes on: new zone fightable · rebirth · gold starvation — challenge snipe mode auto-detected.");
            Controls.Add(_advisorNote);

            BuildDrainLedger(W, Math.Max(148 + _manualStrip.Height, _advisorNote.Bottom) + 10);

            VisibleChanged += (s, e) => { if (Visible) RefreshPipeline(); };
            SyncFromSettings();
        }

        private void BuildDrainLedger(int W, int y)
        {
            int boxW = W - 54;   // 610 legacy
            // GROSS/NET gets a permanent two-line budget — octillion-scale numbers wrap, not clip.
            var box = new Panel { Location = new Point(10, y), Size = new Size(boxW, 150), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(box);

            box.Controls.Add(new Label { Text = "GOLD DRAIN", AutoSize = true, Font = UiTheme.ColHeader, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(8, 4) });

            _grossNet = new Label { Text = "…", AutoSize = false, Size = new Size(boxW - 18, 42), Font = UiTheme.Bold, ForeColor = UiTheme.Ink, BackColor = UiTheme.Surface, Location = new Point(8, 26) };
            box.Controls.Add(_grossNet);

            // Measured label column (the fixed 90px column blanked "Blood rituals" under the game's
            // wider font rendering) + TextH heights and 26px row pitch.
            int labelW = Math.Max(100, Math.Max(
                UiLayout.MeasureText("Blood rituals", UiTheme.Ui),
                Math.Max(UiLayout.MeasureText("Diggers", UiTheme.Ui), UiLayout.MeasureText("Augments", UiTheme.Ui))) + 14);
            int barX = 8 + labelW + 6;
            int valX = barX + 206;

            var digLbl = new Label { Text = "Diggers", AutoSize = false, Size = new Size(labelW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(8, 72) };
            box.Controls.Add(digLbl);
            _digBarOuter = new Panel { Location = new Point(barX, 78), Size = new Size(200, 9), BackColor = UiTheme.Zebra, BorderStyle = BorderStyle.FixedSingle };
            _digBarInner = new Panel { Location = new Point(0, 0), Size = new Size(0, 7), BackColor = UiTheme.Energy };
            _digBarOuter.Controls.Add(_digBarInner);
            box.Controls.Add(_digBarOuter);
            _digVal = new Label { Text = "", AutoSize = false, Size = new Size(boxW - 16 - valX, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(valX, 72) };
            box.Controls.Add(_digVal);

            var bloodLbl = new Label { Text = "Blood rituals", AutoSize = false, Size = new Size(labelW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(8, 98) };
            box.Controls.Add(bloodLbl);
            _bloodBarOuter = new Panel { Location = new Point(barX, 104), Size = new Size(200, 9), BackColor = UiTheme.Zebra, BorderStyle = BorderStyle.FixedSingle };
            _bloodBarInner = new Panel { Location = new Point(0, 0), Size = new Size(0, 7), BackColor = UiTheme.Energy };
            _bloodBarOuter.Controls.Add(_bloodBarInner);
            box.Controls.Add(_bloodBarOuter);
            _bloodVal = new Label { Text = "", AutoSize = false, Size = new Size(boxW - 16 - valX, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(valX, 98) };
            box.Controls.Add(_bloodVal);

            var augLbl = new Label { Text = "Augments", AutoSize = false, Size = new Size(labelW, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(8, 124) };
            box.Controls.Add(augLbl);
            _augVal = new Label { Text = "", AutoSize = false, Size = new Size(boxW - 16 - barX, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(barX, 124) };
            box.Controls.Add(_augVal);
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
                try { toggle(); } catch (Exception ex) { LogDebug($"Gold trigger: {ex.Message}"); }
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
                bool advisor = Settings.AdvisorGold;
                _srcToggle.Text = advisor ? "ADVISOR MANAGES GOLD" : "MANUAL SNIPE";
                UiTheme.ApplyState(_srcToggle, advisor ? UiTheme.Cap : UiTheme.Danger, Color.White);
                _manualStrip.Visible = !advisor;
                _advisorNote.Visible = advisor;

                UiTheme.ApplyState(_trigNewZone, Settings.SnipeOnNewZone ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_trigRebirth, Settings.SnipeOnRebirth ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_trigStarved, Settings.SnipeOnGoldStarved ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_trigTimer, Settings.SnipeOnTimer ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_cblock, Settings.GoldCBlockMode ? UiTheme.Cap : UiTheme.Danger, Color.White);
                int min = Math.Max(1, Math.Min(240, Settings.ResnipeTime / 60));
                _timerMin.Value = min;
                _timerMin.Enabled = _minLbl.Enabled = Settings.SnipeOnTimer;
            }
            finally { _syncing = false; }
            RefreshPipeline();
        }

        private void SetStage(int i, bool lit, string value, string sub)
        {
            var st = _stages[i];
            var bg = lit ? Color.FromArgb(253, 246, 233) : UiTheme.Surface;
            st.Box.BackColor = bg;
            st.Title.BackColor = st.Value.BackColor = st.Sub.BackColor = bg;
            st.Title.ForeColor = lit ? UiTheme.Energy : UiTheme.Muted;
            st.Value.Text = Fit(value, UiTheme.Bold, st.Value.Width - 2);
            st.Sub.Text = UiLayout.WrapText(sub, UiTheme.Chip, st.Sub.Width - 2, 2);
        }

        private static string Fit(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (UiLayout.MeasureText(text, font) <= width) return text;
            while (text.Length > 1 && UiLayout.MeasureText(text + "…", font) > width)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        private static string TriggerCaps(string t)
        {
            switch (t)
            {
                case "new zone fightable": return "NEW ZONE";
                case "rebirth (TM empty)": return "REBIRTH";
                case "gold starvation": return "GOLD STARVED";
                case "timer": return "TIMER";
                case "manual": return "MANUAL";
                default: return "SNIPING";
            }
        }

        // Armed-trigger summary sized to the chip: full list when it fits, count when it wouldn't.
        private string ArmedTriggers()
        {
            var parts = new List<string>();
            if (Settings.AdvisorGold) { parts.Add("ZONE"); parts.Add("REBIRTH"); parts.Add("STARVED"); }
            else
            {
                if (Settings.SnipeOnNewZone) parts.Add("ZONE");
                if (Settings.SnipeOnRebirth) parts.Add("REBIRTH");
                if (Settings.SnipeOnGoldStarved) parts.Add("STARVED");
                if (Settings.SnipeOnTimer) parts.Add("TIMER");
            }
            if (parts.Count == 0) return "MANUAL ONLY";
            string joined = string.Join(" · ", parts.ToArray());
            return UiLayout.MeasureText($"WAITING: {joined}", UiTheme.Chip) <= 174
                ? joined
                : $"{parts.Count} TRIGGERS ARMED";
        }

        private void RefreshPipeline()
        {
            try
            {
                var c = Main.Character;
                if (c == null || Settings == null) return;

                bool sniping = !Settings.GoldSnipeComplete;

                // ZONE SNIPE.
                if (sniping)
                {
                    string zone = "pending";
                    int fz = Main.FurthestZone;
                    if (fz >= 0 && ZoneHelpers.ZoneList.TryGetValue(fz, out var zn)) zone = zn;
                    SetStage(0, true, zone, $"ACTIVE: {TriggerCaps(LastSnipeTrigger)}");
                }
                else
                {
                    SetStage(0, false, "COMPLETE", $"WAITING: {ArmedTriggers()}");
                }

                // TIME MACHINE: gold TOTAL, single-decimal suffix.
                double tmGold = 0;
                try { tmGold = c.machine.realBaseGold; } catch { }
                string cf = "NO COUNTERFEIT";
                try
                {
                    double gb = c.bloodMagic.goldSpellBlood, gm = c.bloodSpells.minGoldBlood();
                    if (gb >= gm && gm > 0)
                        cf = $"COUNTERFEIT +{Math.Floor(Math.Pow(Math.Log(gb / gm, 2.0) + 1.0, 2.0)):0}%";
                }
                catch { }

                // TITAN BANK.
                int best = AdvisorApply.HighestAkTitan();
                bool bankQueued = false;
                string bankValue = "NONE YET";
                string bankSub = "NO AK TITAN";
                string challenge = null;
                try { challenge = ChallengeDetector.Current(); } catch { }
                if (best >= 0)
                {
                    var done = Settings.TitanMoneyDone;
                    bankQueued = done == null || best >= done.Length || !done[best];
                    // Version tag comes from the game's own enemy entry (user-reported: "Walderp v1"
                    // mislabel — WALDERP has no versions; Beast V1/V2 are separate enemy #s).
                    bankValue = TitansPanel.AbbrevWithVersion(best);
                    bankSub = challenge != null ? "CHALLENGE-LIMITED" : (bankQueued ? "AT NEXT AK KILL" : "UP TO DATE");
                }

                SetStage(1, !sniping && !bankQueued, Fmt1(tmGold), tmGold > 0 ? cf : "WAITING ON SNIPE");
                SetStage(2, !sniping && bankQueued, bankValue, bankSub);

                // GOLD DRAIN ledger.
                double gross = 0, drainDig = 0, drainBlood = 0;
                try { gross = c.grossGoldPerSecond(); } catch { }
                try { drainDig = c.totalGPSDrain(); } catch { }
                try
                {
                    var rituals = c.bloodMagicController.bloodMagics;
                    if (rituals != null)
                        foreach (var r in rituals)
                            if (r != null)
                                drainBlood += r.goldConsumedPerSecond();
                }
                catch { }
                double net = gross - drainDig - drainBlood;
                double consumedPct = gross > 0 ? (drainDig + drainBlood) / gross * 100.0 : 0;
                _grossNet.Text = UiLayout.WrapText($"GROSS {Fmt1(gross)}/s   →   NET {Fmt1(Math.Max(0, net))}/s   ({consumedPct:0}% consumed)", UiTheme.Bold, _grossNet.Width - 2, 2);

                double digPct = gross > 0 ? drainDig / gross : 0;
                _digBarInner.Width = (int)(198 * Math.Min(1, digPct));
                _digVal.Text = $"{Fmt1(drainDig)}/s · {digPct * 100:0}%";

                double bloodPct = gross > 0 ? drainBlood / gross : 0;
                _bloodBarInner.Width = (int)(198 * Math.Min(1, bloodPct));
                _bloodVal.Text = $"{Fmt1(drainBlood)}/s · {bloodPct * 100:0}%";

                bool starved = false;
                try { starved = OptimizationAdvisor.GoldStarvedForAugs(c, 1.0); } catch { }
                _augVal.Text = starved ? "STARVED — snipe trigger armed" : "FUNDED";
                _augVal.ForeColor = starved ? UiTheme.Danger : UiTheme.Cap;
            }
            catch (Exception ex) { LogDebug($"Gold pipeline: {ex.Message}"); }
        }

        // The GAME's own formatter first (matches what the player sees in-game and respects their
        // number-display setting) — the hand ladder capped at Q and printed "1194605.7Q" for 1.19e21.
        private static string Fmt1(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            try
            {
                var s = Main.Character?.display(v);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch { }
            // Fallback ladder through the standard idle-game suffixes, then scientific.
            string[] suf = { "", "K", "M", "B", "T", "Qa", "Qi", "Sx", "Sp", "Oc", "No" };
            int tier = 0;
            while (v >= 1000 && tier < suf.Length - 1) { v /= 1000; tier++; }
            return v >= 1000 ? $"{v:0.#e+0}" : $"{v:0.#}{suf[tier]}";
        }
    }
}
