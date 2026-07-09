using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Systems > QUESTS, Q1 "quest ticket" (user-approved): the running quest as a ticket stub
    // (MAJOR/MINOR badge, zone, drop bar, butter state, capstone-hold line) beside the QUEST BANK
    // meter (banked count, regen bar + next-in, the overfill predictor's verdict). ADVISOR runs the
    // strategy (AdvisorApply.ApplyQuests) + the capstone hold; MANUAL exposes the full rulebook.
    public class QuestsPanel : Panel
    {
        private Button _srcToggle;
        private Button _refresh;

        private Panel _ticket;
        private Label _badge;
        private Label _questName;
        private Panel _dropOuter;
        private Panel _dropInner;
        private Label _dropText;
        private Label _capstone;

        private Panel _bank;
        private Label _bankCount;
        private Label _bankNext;
        private Panel _bankOuter;
        private Panel _bankInner;
        private Label _bankVerdict;

        private Label _plan;
        private Panel _rules;
        private Button _majors;
        private Button _fullBank;
        private Button _manualMinors;
        private Button _abandon;
        private NumericUpDown _abandonPct;
        private Button _fifty;
        private Button _butterMinor;
        private Button _butterMajor;
        private Button _questGear;
        private ComboBox _combatMode;
        private Button _beast;

        private bool _syncing;

        // canvasW: explicit canvas width when hosted in an M1 section column (0 = UiLayout.PanelW).
        public QuestsPanel(int canvasW = 0)
        {
            int W = canvasW > 0 ? canvasW : UiLayout.PanelW;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _srcToggle = MkBtn("ADVISOR RUNS QUESTS");
            _srcToggle.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.AdvisorQuests = !Settings.AdvisorQuests;
                SyncFromSettings();
            };
            _refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_refresh);
            _refresh.Click += (s, e) => RefreshView();
            Controls.Add(_srcToggle);
            Controls.Add(_refresh);
            UiLayout.Row(10, 10, 8, _srcToggle, _refresh);

            // Ticket stub (left) — gold left edge like a torn ticket. Ticket + bank split the canvas
            // 3:2 (the legacy 372/228 in the 664 canvas); in a narrow M1 column they STACK instead
            // (side-by-side at <560 starves the bank meter's labels).
            bool narrow = W < 560;
            int contentW = W - 44;
            int ticketW = narrow ? contentW : contentW * 3 / 5;   // 372 legacy
            int bankW = narrow ? contentW : contentW - ticketW - 20;
            _ticket = new Panel { Location = new Point(10, 44), Size = new Size(ticketW, 100), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(_ticket);
            _ticket.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(4, 98), BackColor = UiTheme.Energy });
            _badge = new Label { Text = "", AutoSize = false, Size = new Size(64, 18), Font = UiTheme.Chip, ForeColor = Color.White, BackColor = UiTheme.Faint, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(12, 7) };
            _questName = new Label { Text = "…", AutoSize = false, Size = new Size(ticketW - 92, UiTheme.TextH), Font = UiTheme.Bold, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(84, 5) };
            _dropOuter = new Panel { Location = new Point(12, 34), Size = new Size(ticketW - 24, 10), BackColor = UiTheme.Zebra, BorderStyle = BorderStyle.FixedSingle };
            _dropInner = new Panel { Location = new Point(0, 0), Size = new Size(0, 8), BackColor = UiTheme.Energy };
            _dropOuter.Controls.Add(_dropInner);
            _dropText = new Label { Text = "", AutoSize = false, Size = new Size(ticketW - 24, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(12, 48) };
            _capstone = new Label { Text = "", AutoSize = false, Size = new Size(ticketW - 24, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Energy, BackColor = UiTheme.Surface, Location = new Point(12, 72) };
            _ticket.Controls.Add(_badge);
            _ticket.Controls.Add(_questName);
            _ticket.Controls.Add(_dropOuter);
            _ticket.Controls.Add(_dropText);
            _ticket.Controls.Add(_capstone);

            // Bank meter (right; below the ticket when narrow).
            _bank = new Panel { Location = narrow ? new Point(10, 150) : new Point(10 + ticketW + 10, 44), Size = new Size(bankW, 100), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(_bank);
            _bank.Controls.Add(new Label { Text = "QUEST BANK", AutoSize = true, Font = UiTheme.ColHeader, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(10, 6) });
            _bankCount = new Label { Text = "…", AutoSize = false, Size = new Size(90, UiTheme.TextH), Font = UiTheme.Bold, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(10, 28) };
            _bankNext = new Label { Text = "", AutoSize = false, Size = new Size(bankW - 116, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(104, 28) };
            _bankOuter = new Panel { Location = new Point(10, 54), Size = new Size(bankW - 22, 10), BackColor = UiTheme.Zebra, BorderStyle = BorderStyle.FixedSingle };
            _bankInner = new Panel { Location = new Point(0, 0), Size = new Size(0, 8), BackColor = UiTheme.Cap };
            _bankOuter.Controls.Add(_bankInner);
            _bankVerdict = new Label { Text = "", AutoSize = false, Size = new Size(bankW - 22, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Surface, Location = new Point(10, 70) };
            _bank.Controls.Add(_bankCount);
            _bank.Controls.Add(_bankNext);
            _bank.Controls.Add(_bankOuter);
            _bank.Controls.Add(_bankVerdict);

            int rulesY = narrow ? 258 : 152;
            _plan = new Label { Text = "", AutoSize = false, Size = new Size(W - 54, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(10, rulesY + 4), Tag = "exclusive" };
            Controls.Add(_plan);

            // Manual rulebook: two measured rows (they WRAP in narrow columns).
            _rules = new Panel { Location = new Point(0, rulesY), Size = new Size(W - 4, 66), BackColor = UiTheme.Ground, Tag = "exclusive" };
            Controls.Add(_rules);
            _majors = MkRule("Majors", () => Settings.AllowMajorQuests = !Settings.AllowMajorQuests);
            _fullBank = MkRule("Full-Bank Guard", () => Settings.QuestsFullBank = !Settings.QuestsFullBank);
            _manualMinors = MkRule("Manual Minors", () => Settings.ManualMinors = !Settings.ManualMinors);
            _abandon = MkRule("Abandon <", () => Settings.AbandonMinors = !Settings.AbandonMinors);
            _abandonPct = new NumericUpDown { Width = 48, Minimum = 0, Maximum = 100, Font = UiTheme.Ui };
            _abandonPct.ValueChanged += (s, e) => { if (!_syncing && Settings != null) Settings.MinorAbandonThreshold = (int)_abandonPct.Value; };
            var pctLbl = new Label { Text = "%", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _rules.Controls.Add(_majors);
            _rules.Controls.Add(_fullBank);
            _rules.Controls.Add(_manualMinors);
            _rules.Controls.Add(_abandon);
            _rules.Controls.Add(_abandonPct);
            _rules.Controls.Add(pctLbl);
            int rulesRow1 = UiLayout.WrapRow(10, 4, 8, _rules.Width - 10, 30,
                new Control[] { _majors, _fullBank, _manualMinors, _abandon, _abandonPct, pctLbl });

            _fifty = MkRule("50-Item Minors", () => Settings.FiftyItemMinors = !Settings.FiftyItemMinors);
            _butterMinor = MkRule("Butter Minors", () => Settings.UseButterMinor = !Settings.UseButterMinor);
            _butterMajor = MkRule("Butter Majors", () => Settings.UseButterMajor = !Settings.UseButterMajor);
            _questGear = MkRule("Quest Gear", () => Settings.ManageQuestLoadouts = !Settings.ManageQuestLoadouts);
            _rules.Controls.Add(_fifty);
            _rules.Controls.Add(_butterMinor);
            _rules.Controls.Add(_butterMajor);
            _rules.Controls.Add(_questGear);
            int rulesRow2 = UiLayout.WrapRow(10, rulesRow1 + 2, 8, _rules.Width - 10, 30,
                new Control[] { _fifty, _butterMinor, _butterMajor, _questGear });
            _rules.Height = rulesRow2 + 2;

            // Re-homed from the retired Old Quests page (Phase B): quest-zone combat style. Sits
            // below whichever of plan/rules is taller (they're exclusive views sharing the slot).
            var cmLbl = new Label { Text = "Quest combat", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _combatMode = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            _combatMode.Items.AddRange(new object[] { "Idle", "Snipe", "Defensive", "Offensive" });
            _combatMode.SelectedIndexChanged += (s, e) => { if (!_syncing && Settings != null && _combatMode.SelectedIndex >= 0) Settings.QuestCombatMode = _combatMode.SelectedIndex; };
            _beast = MkRule("Beast Mode", () => Settings.QuestBeastMode = !Settings.QuestBeastMode);
            Controls.Add(cmLbl);
            Controls.Add(_combatMode);
            Controls.Add(_beast);
            UiLayout.Row(10, Math.Max(226, _rules.Bottom + 8), 8, cmLbl, _combatMode, _beast);

            VisibleChanged += (s, e) => { if (Visible) RefreshView(); };
            SyncFromSettings();
        }

        private static Button MkBtn(string text)
        {
            var b = new Button { Text = text, Size = new Size(UiLayout.BtnWidth(text), 24), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = UiTheme.Border;
            return b;
        }

        private Button MkRule(string text, Action toggle)
        {
            var b = MkBtn(text);
            b.Click += (s, e) =>
            {
                if (Settings == null) return;
                try { toggle(); } catch (Exception ex) { LogDebug($"Quest rule: {ex.Message}"); }
                SyncFromSettings();
            };
            return b;
        }

        private static string Fit(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (UiLayout.MeasureText(text, font) <= width) return text;
            while (text.Length > 1 && UiLayout.MeasureText(text + "…", font) > width)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            _syncing = true;
            try
            {
                bool advisor = Settings.AdvisorQuests;
                _srcToggle.Text = advisor ? "ADVISOR RUNS QUESTS" : "MANUAL RULES";
                UiTheme.ApplyState(_srcToggle, advisor ? UiTheme.Cap : UiTheme.Danger, Color.White);
                _plan.Visible = advisor;
                _rules.Visible = !advisor;

                UiTheme.ApplyState(_majors, Settings.AllowMajorQuests ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_fullBank, Settings.QuestsFullBank ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_manualMinors, Settings.ManualMinors ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_abandon, Settings.AbandonMinors ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_fifty, Settings.FiftyItemMinors ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_butterMinor, Settings.UseButterMinor ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_butterMajor, Settings.UseButterMajor ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_questGear, Settings.ManageQuestLoadouts ? UiTheme.Cap : UiTheme.Danger, Color.White);
                int pct = Math.Max(0, Math.Min(100, Settings.MinorAbandonThreshold));
                _abandonPct.Value = pct;
                _abandonPct.Enabled = Settings.AbandonMinors;
                int cm = Settings.QuestCombatMode;
                if (cm >= 0 && cm < _combatMode.Items.Count) _combatMode.SelectedIndex = cm;
                UiTheme.ApplyState(_beast, Settings.QuestBeastMode ? UiTheme.Cap : UiTheme.Danger, Color.White);
            }
            finally { _syncing = false; }
            RefreshView();
        }

        private void RefreshView()
        {
            try
            {
                var c = Main.Character;
                if (c == null || Settings == null) return;
                var q = c.beastQuest;
                var qc = c.beastQuestController;

                // Ticket.
                if (q.inQuest)
                {
                    bool minor = q.reducedRewards;
                    _badge.Text = minor ? "MINOR" : "MAJOR";
                    _badge.BackColor = minor ? UiTheme.Cap : UiTheme.Energy;
                    string zone = "?";
                    try { ZoneHelpers.ZoneList.TryGetValue(qc.curQuestZone(), out zone); } catch { }
                    _questName.Text = Fit(zone ?? "?", UiTheme.Bold, _questName.Width - 2);
                    double frac = q.targetDrops > 0 ? Math.Min(1.0, q.curDrops / (double)q.targetDrops) : 0;
                    _dropInner.Width = (int)((_dropOuter.Width - 2) * frac);
                    string butter = q.usedButter ? "butter: USED"
                        : (minor ? Settings.UseButterMinor : Settings.UseButterMajor) ? "butter: armed" : "butter: off";
                    string mode = minor && q.idleMode ? " · idle" : " · fighting";
                    _dropText.Text = Fit($"{q.curDrops} / {q.targetDrops} drops · {butter}{mode}", UiTheme.Ui, _dropText.Width - 2);
                    _capstone.Text = QuestManager.CapstoneItem != null
                        ? Fit($"HOLDING — maxing {QuestManager.CapstoneItem}", UiTheme.Ui, _capstone.Width - 2)
                        : "";
                }
                else
                {
                    _badge.Text = "NONE";
                    _badge.BackColor = UiTheme.Faint;
                    _questName.Text = "No quest running";
                    _dropInner.Width = 0;
                    _dropText.Text = Settings.AutoQuest ? "next quest starts automatically" : "Auto Quest is OFF";
                    _capstone.Text = "";
                }

                // Bank.
                int banked = 0, maxBank = 0;
                float thr = 1, timer = 0;
                try
                {
                    banked = q.curBankedQuests;
                    maxBank = qc.maxBankedQuests();
                    thr = qc.timerThreshold();
                    timer = (float)q.dailyQuestTimer.totalseconds;
                }
                catch { }
                _bankCount.Text = $"{banked} / {maxBank}";
                double into = thr > 0 ? timer % thr : 0;
                double next = Math.Max(0, thr - into);
                _bankNext.Text = banked >= maxBank ? "FULL" : Fit($"next in {(next >= 3600 ? $"{next / 3600:0.#}h" : $"{next / 60:0}m")}", UiTheme.Ui, _bankNext.Width - 2);
                _bankInner.Width = (int)((_bankOuter.Width - 2) * (thr > 0 ? into / thr : 0));
                bool overfill = QuestManager.BankOverfill;
                _bankVerdict.Text = overfill ? "overfill: FORCING QUESTS" : "overfill guard: safe";
                _bankVerdict.ForeColor = overfill ? UiTheme.Danger : UiTheme.Muted;

                // Plan sentence (advisor mode).
                if (Settings.AdvisorQuests)
                {
                    string plan;
                    if (!Settings.AutoQuest) plan = "Auto Quest is OFF (Settings tab) — advisor is idle.";
                    else if (QuestManager.CapstoneItem != null) plan = $"Plan: max {QuestManager.CapstoneItem}, turn in, then {(banked > 0 ? "next banked major" : "idle minors")}.";
                    else if (q.inQuest && !q.reducedRewards) plan = $"Plan: finish this major → {(banked > 0 ? $"{banked} more banked" : "idle minors while sniping resumes")}.";
                    else if (banked > 0) plan = $"Plan: {banked} banked major{(banked > 1 ? "s" : "")} queued — starting when current quest clears.";
                    else plan = "Plan: idle minors while sniping; majors start as the bank fills.";
                    UiLayout.FitOrGrow(_plan, plan);
                }
            }
            catch (Exception ex) { LogDebug($"Quests panel: {ex.Message}"); }
        }
    }
}
