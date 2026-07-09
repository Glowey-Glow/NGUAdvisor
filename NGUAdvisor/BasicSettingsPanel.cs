using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Strangler step 1: a clean, code-only "Settings" tab surfacing the master toggles that users
    // actually flip day to day, grouped by system. The legacy resx tabs remain for detailed/numeric
    // configuration ("advanced"); over time more of them migrate here. Plain bound checkboxes only —
    // the one control pattern proven reliable under the injected Mono.
    public class BasicSettingsPanel : Panel
    {
        private class Bind
        {
            public CheckBox Box;
            public Func<bool> Get;
        }

        private readonly List<Bind> _binds = new List<Bind>();
        private readonly List<Action> _numSyncs = new List<Action>();
        private bool _syncing;
        private Button _master;

        private const int RowStep = 24;

        public BasicSettingsPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;
            AutoScroll = true;

            // Verb-in-the-header layout (review decision 3): the group header carries the shared verb
            // ("MANAGE Energy", "AUTO Quest"), checkboxes carry only what differs.
            // Round-3 re-space: four columns spread across the 1030 M1 canvas (was 16/180/344/508
            // for the old 664 window) — pure x-arithmetic, no control changes.
            int x0 = 16, x1 = 272, x2 = 528, x3 = 784;

            // Phase D: the MASTER kill-switch, re-homed from the retired General page (also on the
            // F1 hotkey). Everything below obeys it.
            _master = new Button { Text = "ADVISOR ACTIVE", Size = new Size(UiLayout.BtnWidth("ADVISOR ACTIVE"), 26), Location = new Point(x0, 8), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            _master.FlatAppearance.BorderColor = UiTheme.Border;
            _master.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.GlobalEnabled = !Settings.GlobalEnabled;
                Sync();
            };
            Controls.Add(_master);

            int y0 = Build(x0, 44, "MANAGE", new[]
            {
                Mk("Energy", () => Settings.ManageEnergy, v => Settings.ManageEnergy = v),
                Mk("Magic", () => Settings.ManageMagic, v => Settings.ManageMagic = v),
                Mk("R3", () => Settings.ManageR3, v => Settings.ManageR3 = v),
                Mk("Gear", () => Settings.ManageGear, v => Settings.ManageGear = v),
                Mk("Diggers", () => Settings.ManageDiggers, v => Settings.ManageDiggers = v),
                Mk("Beards", () => Settings.ManageBeards, v => Settings.ManageBeards = v),
                Mk("Wandoos", () => Settings.ManageWandoos, v => Settings.ManageWandoos = v),
                Mk("NGU Diff", () => Settings.ManageNGUDiff, v => Settings.ManageNGUDiff = v),
                Mk("Inventory", () => Settings.ManageInventory, v => Settings.ManageInventory = v),
                Mk("Yggdrasil", () => Settings.ManageYggdrasil, v => Settings.ManageYggdrasil = v),
                Mk("Titans", () => Settings.ManageTitans, v => Settings.ManageTitans = v),
                Mk("Cooking", () => Settings.ManageCooking, v => Settings.ManageCooking = v),
                Mk("Wishes", () => Settings.ManageWishes, v => Settings.ManageWishes = v),
                Mk("Gold Loadouts", () => Settings.ManageGoldLoadouts, v => Settings.ManageGoldLoadouts = v),
                Mk("Consumables", () => Settings.ManageConsumables, v => Settings.ManageConsumables = v),
            });

            int y1 = Build(x1, 44, "AUTO", new[]
            {
                Mk("Fight Bosses", () => Settings.AutoFight, v => Settings.AutoFight = v),
                Mk("Quest", () => Settings.AutoQuest, v => Settings.AutoQuest = v),
                Mk("Major Quests", () => Settings.AllowMajorQuests, v => Settings.AllowMajorQuests = v),
                Mk("Money Pit", () => Settings.AutoMoneyPit, v => Settings.AutoMoneyPit = v),
                Mk("Daily Spin", () => Settings.AutoSpin, v => Settings.AutoSpin = v),
                Mk("Rebirth", () => Settings.AutoRebirth, v => Settings.AutoRebirth = v),
                Mk("Convert Boosts", () => Settings.AutoConvertBoosts, v => Settings.AutoConvertBoosts = v),
                Mk("Cast Cards", () => Settings.AutoCastCards, v => Settings.AutoCastCards = v),
                Mk("Activate Fruits", () => Settings.ActivateFruits, v => Settings.ActivateFruits = v),
                Mk("Titan Gold", () => Settings.AutoTitanGold, v => Settings.AutoTitanGold = v),
                Mk("Digger Upgrades", () => Settings.UpgradeDiggers, v => Settings.UpgradeDiggers = v),
                Mk("Buy E/M (EXP)", () => Settings.AutoBuyEM, v => Settings.AutoBuyEM = v),
                Mk("Buy Adventure (EXP)", () => Settings.AutoBuyAdventure, v => Settings.AutoBuyAdventure = v),
                Mk("Daily Save", () => Settings.Autosave, v => Settings.Autosave = v),
                Mk("Buy Consumables", () => Settings.AutoBuyConsumables, v => Settings.AutoBuyConsumables = v),
                Mk("Consume Mid-Run", () => Settings.ConsumeIfAlreadyRunning, v => Settings.ConsumeIfAlreadyRunning = v),
            });

            int y2 = Build(x2, 44, "SWAP GEAR FOR", new[]
            {
                Mk("Titans", () => Settings.SwapTitanLoadouts, v => Settings.SwapTitanLoadouts = v),
                Mk("Titan Diggers", () => Settings.SwapTitanDiggers, v => Settings.SwapTitanDiggers = v),
                Mk("Titan Beards", () => Settings.SwapTitanBeards, v => Settings.SwapTitanBeards = v),
                Mk("Yggdrasil", () => Settings.SwapYggdrasilLoadouts, v => Settings.SwapYggdrasilLoadouts = v),
                Mk("Quests", () => Settings.ManageQuestLoadouts, v => Settings.ManageQuestLoadouts = v),
                Mk("Cooking", () => Settings.ManageCookingLoadouts, v => Settings.ManageCookingLoadouts = v),
            });

            int y3 = Build(x3, 44, "COMBAT + ITOPOD", new[]
            {
                Mk("Adventure Combat", () => Settings.CombatEnabled, v => Settings.CombatEnabled = v),
                Mk("Snipe Boss Only", () => Settings.SnipeBossOnly, v => Settings.SnipeBossOnly = v),
                Mk("Beast Mode", () => Settings.BeastMode, v => Settings.BeastMode = v),
                Mk("Target ITOPOD", () => Settings.AdventureTargetITOPOD, v => Settings.AdventureTargetITOPOD = v),
                Mk("ITOPOD Auto-Push", () => Settings.ITOPODAutoPush, v => Settings.ITOPODAutoPush = v),
            });

            // Uniques absorbed from the old General tab; Phase D added the digger cap + Unload.
            int y4 = Build(x3, y3 + 12, "MISC", new[]
            {
                Mk("Disable Overlay", () => Settings.DisableOverlay, v => Settings.DisableOverlay = v),
                // Wide Layout toggle retired: the M1 Control Room window has ONE designed size.
            });
            y4 = MkDouble(x3, y4 + 2, "Digger cap %", () => Settings.DiggerCap, v => Settings.DiggerCap = Math.Max(0, Math.Min(100, v)));
            var folderBtn = new Button { Text = "Settings Folder", Size = new Size(140, 26), Location = new Point(x3, y4 + 2), Font = UiTheme.Ui };
            UiTheme.StyleFlat(folderBtn);
            folderBtn.Click += (s, e) => { try { System.Diagnostics.Process.Start(GetSettingsDir()); } catch (Exception ex) { LogDebug($"Settings folder: {ex.Message}"); } };
            Controls.Add(folderBtn);
            y4 += 36;

            // Unload, re-homed from the retired General page. Safety-gated exactly like the
            // legacy pair: the button only arms while the checkbox is ticked.
            var unloadSafety = new CheckBox { Text = "Arm unload", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(x3, y4 + 6) };
            var unloadBtn = new Button { Text = "Unload Advisor", Size = new Size(140, 26), Location = new Point(x3, y4 + 30), Font = UiTheme.Ui, Enabled = false };
            UiTheme.StyleFlat(unloadBtn);
            unloadSafety.CheckedChanged += (s, e) => unloadBtn.Enabled = unloadSafety.Checked;
            unloadBtn.Click += (s, e) => { try { Loader.Unload(); } catch (Exception ex) { LogDebug($"Unload: {ex.Message}"); } };
            Controls.Add(unloadSafety);
            Controls.Add(unloadBtn);
            y4 += 66;

            // Blood inputs moved to Systems › Blood (advisor status + manual thresholds together).
            int bottomY = Math.Max(Math.Max(y0, y1), Math.Max(y2, Math.Max(y3, y4))) + 14;

            Controls.Add(new Label
            {
                Text = "Detailed options (loadout IDs, zones, thresholds, priorities) live in the tabs to the right.",
                Location = new Point(20, bottomY + 8),
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            });

            // AutoScroll can restore mid-scroll on tab return; always land at the top.
            VisibleChanged += (s, e) => { if (Visible) AutoScrollPosition = new Point(0, 0); };
        }

        // A measured label+numeric pair for NumRow (inline horizontal layout).
        private Control[] MkPair(string label, int min, int max, Func<int> get, Action<int> set)
        {
            var l = new Label { Text = label, AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            var n = new NumericUpDown { Width = 66, Minimum = min, Maximum = max, Font = UiTheme.Ui };
            n.ValueChanged += (s, e) =>
            {
                if (_syncing || Settings == null) return;
                try { set((int)n.Value); } catch (Exception ex) { LogDebug($"Basic num '{label}': {ex.Message}"); }
            };
            _numSyncs.Add(() =>
            {
                int v;
                try { v = Math.Max(min, Math.Min(max, get())); } catch { return; }
                if ((int)n.Value != v) n.Value = v;
            });
            return new Control[] { l, n };
        }

        private int NumRow(int x, int y, params Control[][] pairs)
        {
            var flat = new List<Control>();
            foreach (var p in pairs)
                foreach (var c in p) { Controls.Add(c); flat.Add(c); }
            return UiLayout.Row(x, y, 8, flat.ToArray());
        }

        // Label + NumericUpDown pair; returns the y below the row. Synced via _numSyncs.
        private int MkNum(int x, int y, string label, int min, int max, Func<int> get, Action<int> set)
        {
            var l = new Label { Text = label, AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(x, y + 4) };
            Controls.Add(l);
            var n = new NumericUpDown { Width = 74, Minimum = min, Maximum = max, Font = UiTheme.Ui, Location = new Point(x + 104, y) };
            n.ValueChanged += (s, e) =>
            {
                if (_syncing || Settings == null) return;
                try { set((int)n.Value); } catch (Exception ex) { LogDebug($"Basic num '{label}': {ex.Message}"); }
            };
            Controls.Add(n);
            _numSyncs.Add(() =>
            {
                int v;
                try { v = Math.Max(min, Math.Min(max, get())); } catch { return; }
                if ((int)n.Value != v) n.Value = v;
            });
            return y + 28;
        }

        // Label + TextBox for doubles (scientific notation accepted, e.g. 1.5E+8).
        private int MkDouble(int x, int y, string label, Func<double> get, Action<double> set)
        {
            var l = new Label { Text = label, AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(x, y + 4) };
            Controls.Add(l);
            var t = new TextBox { Width = 90, Font = UiTheme.Ui, Location = new Point(x + 104, y) };
            t.Leave += (s, e) =>
            {
                if (_syncing || Settings == null) return;
                if (double.TryParse(t.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    try { set(v); } catch (Exception ex) { LogDebug($"Basic double '{label}': {ex.Message}"); }
            };
            Controls.Add(t);
            _numSyncs.Add(() =>
            {
                double v;
                try { v = get(); } catch { return; }
                string txt = v == 0 ? "" : v >= 1e6 ? v.ToString("#.###E+0") : v.ToString("0.##");
                if (!t.Focused && t.Text != txt) t.Text = txt;
            });
            return y + 28;
        }

        private KeyValuePair<string, Bind> Mk(string label, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Text = label, AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Ink, BackColor = UiTheme.Ground };
            var bind = new Bind { Box = cb, Get = get };
            cb.CheckedChanged += (s, e) =>
            {
                if (_syncing || Settings == null) return;
                try { set(cb.Checked); }
                catch (Exception ex) { LogDebug($"Basic settings toggle '{label}': {ex.Message}"); }
            };
            _binds.Add(bind);
            return new KeyValuePair<string, Bind>(label, bind);
        }

        private int Build(int x, int y, string caption, KeyValuePair<string, Bind>[] items)
        {
            Controls.Add(new Label
            {
                Text = caption,
                Location = new Point(x, y),
                AutoSize = true,
                Font = UiTheme.ColHeader,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            });
            y += 24;
            foreach (var it in items)
            {
                it.Value.Box.Location = new Point(x, y);
                Controls.Add(it.Value.Box);
                y += RowStep;
            }
            return y;
        }

        // Pull current values from Settings (called on load and whenever settings reload from disk).
        public void Sync()
        {
            if (Settings == null) return;
            _syncing = true;
            try
            {
                foreach (var b in _binds)
                {
                    bool v;
                    try { v = b.Get(); } catch { continue; }
                    if (b.Box.Checked != v) b.Box.Checked = v;
                }
                foreach (var ns in _numSyncs)
                    try { ns(); } catch { }
                bool on = Settings.GlobalEnabled;
                _master.Text = on ? "ADVISOR ACTIVE" : "ADVISOR PAUSED";
                UiTheme.ApplyState(_master, on ? UiTheme.Cap : UiTheme.Danger, Color.White);
            }
            finally { _syncing = false; }
        }
    }
}
