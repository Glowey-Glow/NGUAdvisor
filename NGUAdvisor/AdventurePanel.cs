using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Combat > ADVENTURE sub-tab, V2 — REBUILT on UiLayout.Row (no hand-computed x positions: rows
    // place measured controls left-to-right, so sibling overlap is impossible by construction) and
    // audited at Shown by UiLayout.Audit. Segmented [ZONES] [ITOPOD] [BLACKLIST].
    public class AdventurePanel : Panel
    {
        private readonly List<Button> _segButtons = new List<Button>();
        private readonly List<Panel> _pages = new List<Panel>();

        // ZONES view
        private Button _srcToggle;
        private Button _farmGear;
        private Button _farmBoost;
        private ComboBox _zoneCombo;
        private Label _zoneLbl;
        private Button _gearHunt;
        private Label _huntLbl;
        private ComboBox _huntZone;
        private Label _huntLine;
        private Label _boostLine1;
        private Label _boostLine2;
        private Label _gearLine;
        private Button _combat;
        private Button _beast;
        private Button _bossesOnly;
        private Button _fallthrough;
        private ComboBox _combatMode;

        // ITOPOD view
        private Button _targetItopod;
        private Button _autoPush;
        private Button _itopodBeast;
        private ComboBox _itopodOptimize;
        private ComboBox _itopodCombat;
        private Label _floorInfo;

        // BLACKLIST view
        private ListBox _blackList;
        private ComboBox _blZone;
        private ComboBox _blEnemy;
        private Dictionary<int, string> _spriteNames;

        private bool _syncing;
        private readonly int _w;

        // canvasW: explicit canvas width when hosted in an M1 section column (0 = UiLayout.PanelW).
        public AdventurePanel(int canvasW = 0)
        {
            _w = canvasW > 0 ? canvasW : UiLayout.PanelW;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            int bx = 10;
            foreach (var name in new[] { "ZONES", "ITOPOD", "BLACKLIST" })
            {
                var b = MkBtn(name, Math.Max(88, UiLayout.BtnWidth(name)));
                b.Location = new Point(bx, 6);
                int idx = _segButtons.Count;
                b.Click += (s, e) => SelectPage(idx);
                Controls.Add(b);
                _segButtons.Add(b);
                bx += b.Width + 6;
            }

            _pages.Add(BuildZonesPage());
            _pages.Add(BuildItopodPage());
            _pages.Add(BuildBlacklistPage());
            foreach (var p in _pages)
            {
                p.Tag = "exclusive";   // alternate views share the area below the segment bar
                Controls.Add(p);
            }

            SyncFromSettings();
            SelectPage(0);
        }

        private static Button MkBtn(string text, int? width = null)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(width ?? UiLayout.BtnWidth(text), 24),
                Font = UiTheme.Ui,
                FlatStyle = FlatStyle.Flat
            };
            b.FlatAppearance.BorderColor = UiTheme.Border;
            return b;
        }

        private static Label MkLbl(string text, bool muted = true)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = muted ? UiTheme.Muted : UiTheme.Ink,
                BackColor = UiTheme.Ground
            };
        }

        private static Label MkHead(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = UiTheme.ColHeader,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            };
        }

        private void SelectPage(int idx)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].Visible = i == idx;
                UiTheme.ApplyState(_segButtons[i], i == idx ? UiTheme.Accent : UiTheme.BtnFace, i == idx ? Color.White : UiTheme.Ink);
            }
            if (idx == 0) RefreshBoostAdvice();
            if (idx == 1) RefreshFloorInfo();
            UiLayout.AuditOnce(_pages[idx], $"Adventure/{_segButtons[idx].Text}");
        }

        private Panel NewPage() => new Panel { Location = new Point(0, 38), Size = new Size(_w - 34, 440), BackColor = UiTheme.Ground, Visible = false };

        private Button MkToggle(string text, Action onClick)
        {
            var b = MkBtn(text);
            b.Click += (s, e) =>
            {
                if (Settings == null) return;
                try { onClick(); } catch (Exception ex) { LogDebug($"Adventure toggle: {ex.Message}"); }
                SyncFromSettings();
            };
            return b;
        }

        private Panel BuildZonesPage()
        {
            var page = NewPage();
            int y = 8;

            var head = MkHead("ZONE SOURCE");
            page.Controls.Add(head);
            head.Location = new Point(10, y);
            y += UiTheme.HeadPitch;

            _srcToggle = MkToggle("ADVISOR ROUTES ZONES", () => Settings.AdvisorZones = !Settings.AdvisorZones);
            _zoneLbl = MkLbl("Zone");
            _zoneCombo = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            foreach (var kv in ZoneHelpers.ZoneList)
            {
                if (kv.Key < 0 || ZoneHelpers.ZoneIsTitan(kv.Key)) continue;
                _zoneCombo.Items.Add(new KeyValuePair<int, string>(kv.Key, kv.Value));
            }
            _zoneCombo.Items.Add(new KeyValuePair<int, string>(1000, "ITOPOD"));
            _zoneCombo.DisplayMember = "Value";
            _zoneCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null || _zoneCombo.SelectedItem == null) return;
                Settings.SnipeZone = ((KeyValuePair<int, string>)_zoneCombo.SelectedItem).Key;
            };
            page.Controls.Add(_srcToggle);
            page.Controls.Add(_zoneLbl);
            page.Controls.Add(_zoneCombo);
            y = UiLayout.Row(10, y, 10, _srcToggle, _zoneLbl, _zoneCombo) + 6;

            // Advisor strategies (visible in advisor mode): gear-capping farm outranks the boost
            // farm; the boost farm only leaves the ITOPOD while something consumes boosts.
            _farmGear = MkToggle("Farm Gear Zones", () => Settings.AdvisorFarmGear = !Settings.AdvisorFarmGear);
            _farmBoost = MkToggle("Farm Best Boost", () => Settings.AdvisorFarmBoost = !Settings.AdvisorFarmBoost);
            page.Controls.Add(_farmGear);
            page.Controls.Add(_farmBoost);
            y = UiLayout.Row(10, y, 8, _farmGear, _farmBoost) + 10;

            // GEAR HUNT (user feature): camp a chosen stage for its drops in the Loot Hunter hybrid
            // set (pool accessories + best P/T). Works in BOTH zone-source modes and outranks the
            // automatic farms; the pool itself is curated in Loadouts › Loot Hunter.
            var ghead = MkHead("GEAR HUNT");
            page.Controls.Add(ghead);
            ghead.Location = new Point(10, y);
            y += UiTheme.HeadPitch;

            _gearHunt = MkToggle("Gear Hunt", () =>
            {
                Settings.GearHuntEnabled = !Settings.GearHuntEnabled;
                AdvisorApply.GearRestored();   // re-arm the gear pass: swap on the next tick, not after the 120s throttle
            });
            _huntLbl = MkLbl("Stage");
            _huntZone = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            foreach (var kv in ZoneHelpers.ZoneList)
            {
                if (kv.Key < 0 || ZoneHelpers.ZoneIsTitan(kv.Key)) continue;
                _huntZone.Items.Add(new KeyValuePair<int, string>(kv.Key, kv.Value));
            }
            _huntZone.DisplayMember = "Value";
            _huntZone.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null || _huntZone.SelectedItem == null) return;
                Settings.GearHuntZone = ((KeyValuePair<int, string>)_huntZone.SelectedItem).Key;
                SyncFromSettings();
            };
            page.Controls.Add(_gearHunt);
            page.Controls.Add(_huntLbl);
            page.Controls.Add(_huntZone);
            y = UiLayout.Row(10, y, 10, _gearHunt, _huntLbl, _huntZone) + 4;

            _huntLine = MkLbl("");
            _huntLine.AutoSize = false;
            _huntLine.Size = new Size(page.Width - 20, UiTheme.TextH);
            page.Controls.Add(_huntLine);
            _huntLine.Location = new Point(10, y);
            y += UiTheme.LinePitch * 2;

            var bhead = MkHead("BOOST FARM ADVICE");
            page.Controls.Add(bhead);
            bhead.Location = new Point(10, y);
            y += UiTheme.HeadPitch;
            _boostLine1 = new Label { Text = "…", AutoSize = true, Font = UiTheme.Bold, ForeColor = UiTheme.AccentDark, BackColor = UiTheme.Ground };
            page.Controls.Add(_boostLine1);
            _boostLine1.Location = new Point(10, y);
            y += UiTheme.LinePitch;
            // Fixed width + 2-line reservation: these advisor verdicts run long and were AutoSize labels
            // that clipped past the narrow combat-column edge. FitOrGrow (in RefreshBoostAdvice) wraps them.
            _boostLine2 = MkLbl("");
            _boostLine2.AutoSize = false;
            _boostLine2.Size = new Size(page.Width - 20, UiTheme.TextH);
            page.Controls.Add(_boostLine2);
            _boostLine2.Location = new Point(10, y);
            y += UiTheme.LinePitch * 2;
            _gearLine = MkLbl("");
            _gearLine.AutoSize = false;
            _gearLine.Size = new Size(page.Width - 20, UiTheme.TextH);
            page.Controls.Add(_gearLine);
            _gearLine.Location = new Point(10, y);
            y += UiTheme.LinePitch * 2 + 4;

            var chead = MkHead("COMBAT STYLE");
            page.Controls.Add(chead);
            chead.Location = new Point(10, y);
            y += UiTheme.HeadPitch;

            _combat = MkToggle("Combat", () => Settings.CombatEnabled = !Settings.CombatEnabled);
            _beast = MkToggle("Beast Mode", () => Settings.BeastMode = !Settings.BeastMode);
            _bossesOnly = MkToggle("Bosses Only", () => Settings.SnipeBossOnly = !Settings.SnipeBossOnly);
            _fallthrough = MkToggle("Fallthrough", () => Settings.AllowZoneFallback = !Settings.AllowZoneFallback);
            var modeLbl = MkLbl("Mode");
            _combatMode = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            _combatMode.Items.AddRange(new object[] { "Idle", "Snipe", "Defensive", "Offensive" });
            _combatMode.SelectedIndexChanged += (s, e) => { if (!_syncing && Settings != null) Settings.CombatMode = _combatMode.SelectedIndex; };
            foreach (Control c in new Control[] { _combat, _beast, _bossesOnly, _fallthrough, modeLbl, _combatMode })
                page.Controls.Add(c);
            // Wraps in narrow M1 columns (six controls run ~550px).
            y = UiLayout.WrapRow(10, y, 8, page.Width - 10, 30, new Control[] { _combat, _beast, _bossesOnly, _fallthrough, modeLbl, _combatMode }) + 8;

            // Two short stacked lines: the single long line measured past the page edge and clipped.
            var note1 = MkLbl("Advisor routing: gold and quest logic keep their overrides;");
            var note2 = MkLbl("otherwise the best boost farm wins.");
            page.Controls.Add(note1);
            page.Controls.Add(note2);
            note1.Location = new Point(10, y);
            note2.Location = new Point(10, y + UiTheme.LinePitch);
            return page;
        }

        private Panel BuildItopodPage()
        {
            var page = NewPage();
            int y = 8;

            var head = MkHead("ITOPOD");
            page.Controls.Add(head);
            head.Location = new Point(10, y);
            y += UiTheme.HeadPitch;

            _targetItopod = MkToggle("Target ITOPOD", () => Settings.AdventureTargetITOPOD = !Settings.AdventureTargetITOPOD);
            _autoPush = MkToggle("Auto-Push", () => Settings.ITOPODAutoPush = !Settings.ITOPODAutoPush);
            _itopodBeast = MkToggle("Beast Mode", () => Settings.ITOPODBeastMode = !Settings.ITOPODBeastMode);
            foreach (Control c in new Control[] { _targetItopod, _autoPush, _itopodBeast })
                page.Controls.Add(c);
            y = UiLayout.Row(10, y, 8, _targetItopod, _autoPush, _itopodBeast) + 14;

            var optLbl = MkLbl("Optimize");
            _itopodOptimize = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            _itopodOptimize.Items.AddRange(new object[] { "Disabled", "Default", "PP", "EXP/AP" });
            _itopodOptimize.SelectedIndexChanged += (s, e) => { if (!_syncing && Settings != null) Settings.ITOPODOptimizeMode = _itopodOptimize.SelectedIndex; };
            var cmbLbl = MkLbl("Combat");
            _itopodCombat = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            _itopodCombat.Items.AddRange(new object[] { "Idle", "Snipe", "Defensive", "Offensive" });
            _itopodCombat.SelectedIndexChanged += (s, e) => { if (!_syncing && Settings != null) Settings.ITOPODCombatMode = _itopodCombat.SelectedIndex; };
            foreach (Control c in new Control[] { optLbl, _itopodOptimize, cmbLbl, _itopodCombat })
                page.Controls.Add(c);
            y = UiLayout.Row(10, y, 8, optLbl, _itopodOptimize, cmbLbl, _itopodCombat) + 14;

            _floorInfo = MkLbl("");
            page.Controls.Add(_floorInfo);
            _floorInfo.Location = new Point(10, y);
            return page;
        }

        private Panel BuildBlacklistPage()
        {
            var page = NewPage();
            int y = 8;

            var head = MkHead("ENEMY BLACKLIST (never sniped)");
            page.Controls.Add(head);
            head.Location = new Point(10, y);
            y += UiTheme.HeadPitch;

            _blackList = new ListBox { Location = new Point(10, y), Size = new Size(page.Width - 20, 190), Font = UiTheme.Ui, BorderStyle = BorderStyle.FixedSingle };
            page.Controls.Add(_blackList);
            y += 198;

            _spriteNames = new Dictionary<int, string>();
            _blZone = new ComboBox { Width = 185, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            _blEnemy = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            try
            {
                var el = Main.Character.adventureController.enemyList;
                for (int z = 0; z < el.Count; z++)
                {
                    if (el[z] == null || el[z].Count == 0) continue;
                    foreach (var en in el[z])
                        if (!_spriteNames.ContainsKey(en.spriteID))
                            _spriteNames[en.spriteID] = en.name;
                    string zn = ZoneHelpers.ZoneList.TryGetValue(z, out var n) ? n : $"Zone {z}";
                    _blZone.Items.Add(new KeyValuePair<int, string>(z, zn));
                }
            }
            catch (Exception ex) { LogDebug($"Blacklist zones: {ex.Message}"); }
            _blZone.DisplayMember = "Value";
            _blZone.SelectedIndexChanged += (s, e) =>
            {
                try
                {
                    if (_blZone.SelectedItem == null) return;
                    int z = ((KeyValuePair<int, string>)_blZone.SelectedItem).Key;
                    _blEnemy.Items.Clear();
                    foreach (var en in Main.Character.adventureController.enemyList[z].Select(x => new KeyValuePair<int, string>(x.spriteID, x.name)).Distinct())
                        _blEnemy.Items.Add(en);
                    _blEnemy.DisplayMember = "Value";
                    if (_blEnemy.Items.Count > 0) _blEnemy.SelectedIndex = 0;
                }
                catch (Exception ex) { LogDebug($"Blacklist enemies: {ex.Message}"); }
            };

            var add = MkBtn("Add");
            UiTheme.StyleFlat(add);
            add.Click += (s, e) =>
            {
                if (Settings == null || _blEnemy.SelectedItem == null) return;
                int id = ((KeyValuePair<int, string>)_blEnemy.SelectedItem).Key;
                var cur = (Settings.BlacklistedBosses ?? new int[0]).ToList();
                if (!cur.Contains(id)) { cur.Add(id); Settings.BlacklistedBosses = cur.ToArray(); }
                SyncFromSettings();
            };
            var rem = MkBtn("Remove");
            UiTheme.StyleFlat(rem);
            rem.Click += (s, e) =>
            {
                if (Settings == null) return;
                int sel = _blackList.SelectedIndex;
                var cur = (Settings.BlacklistedBosses ?? new int[0]).ToList();
                if (sel < 0 || sel >= cur.Count) return;
                cur.RemoveAt(sel);
                Settings.BlacklistedBosses = cur.ToArray();
                SyncFromSettings();
            };
            foreach (Control c in new Control[] { _blZone, _blEnemy, add, rem })
                page.Controls.Add(c);
            UiLayout.WrapRow(10, y, 8, page.Width - 10, 30, new Control[] { _blZone, _blEnemy, add, rem });
            return page;
        }

        private static void StyleOnOff(Button b, bool on)
        {
            UiTheme.ApplyState(b, on ? UiTheme.Cap : UiTheme.Danger, Color.White);
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            _syncing = true;
            try
            {
                bool advisor = Settings.AdvisorZones;
                _srcToggle.Text = advisor ? "ADVISOR ROUTES ZONES" : "MANUAL ZONE";
                StyleOnOff(_srcToggle, advisor);
                _zoneCombo.Visible = _zoneLbl.Visible = !advisor;
                _farmGear.Visible = _farmBoost.Visible = advisor;
                StyleOnOff(_farmGear, Settings.AdvisorFarmGear);
                StyleOnOff(_farmBoost, Settings.AdvisorFarmBoost);
                for (int i = 0; i < _zoneCombo.Items.Count; i++)
                    if (((KeyValuePair<int, string>)_zoneCombo.Items[i]).Key == Settings.SnipeZone)
                    { _zoneCombo.SelectedIndex = i; break; }

                StyleOnOff(_gearHunt, Settings.GearHuntEnabled);
                for (int i = 0; i < _huntZone.Items.Count; i++)
                    if (((KeyValuePair<int, string>)_huntZone.Items[i]).Key == Settings.GearHuntZone)
                    { _huntZone.SelectedIndex = i; break; }
                string hunt;
                if (!Settings.GearHuntEnabled)
                    hunt = "Off — pick a stage; curate the accessory pool in Loadouts › Loot Hunter";
                else if (Settings.GearHuntZone < 0)
                    hunt = "On — pick a stage to hunt";
                else if (!GearHunter.ZoneReachable())
                    hunt = "Stage not reachable yet — zone routing unchanged until it unlocks";
                else
                {
                    int pool = (Settings.LootHunterAccessories ?? new int[0]).Count(x => x > 0);
                    int wr = Settings.LootHunterRespawnCount, wd = Settings.LootHunterDropCount;
                    string picks = wr == 0 && wd == 0
                        ? $"optimizer auto over the {pool}-item pool"
                        : $"{wr} respawn + {wd} DC from the {pool}-item pool";
                    hunt = $"Hunting this stage in the Loot Hunter set ({picks} + best P/T gear)";
                }
                UiLayout.FitOrGrow(_huntLine, hunt, 2);

                StyleOnOff(_combat, Settings.CombatEnabled);
                StyleOnOff(_beast, Settings.BeastMode);
                StyleOnOff(_bossesOnly, Settings.SnipeBossOnly);
                StyleOnOff(_fallthrough, Settings.AllowZoneFallback);
                int cm = Settings.CombatMode;
                if (cm >= 0 && cm < _combatMode.Items.Count) _combatMode.SelectedIndex = cm;

                StyleOnOff(_targetItopod, Settings.AdventureTargetITOPOD);
                StyleOnOff(_autoPush, Settings.ITOPODAutoPush);
                StyleOnOff(_itopodBeast, Settings.ITOPODBeastMode);
                int om = Settings.ITOPODOptimizeMode;
                if (om >= 0 && om < _itopodOptimize.Items.Count) _itopodOptimize.SelectedIndex = om;
                int icm = Settings.ITOPODCombatMode;
                if (icm >= 0 && icm < _itopodCombat.Items.Count) _itopodCombat.SelectedIndex = icm;

                _blackList.BeginUpdate();
                _blackList.Items.Clear();
                foreach (var id in Settings.BlacklistedBosses ?? new int[0])
                    _blackList.Items.Add(_spriteNames != null && _spriteNames.TryGetValue(id, out var n) ? $"{n}  (#{id})" : $"#{id}");
                _blackList.EndUpdate();
            }
            finally { _syncing = false; }
        }

        private void RefreshBoostAdvice()
        {
            try
            {
                var v = BoostFarmAdvisor.Analyze();
                if (!v.Known) { _boostLine1.Text = "…"; return; }
                _boostLine1.Text = $"Best boost farm: {v.BestName}";
                string line2 = v.BestZone == -1000
                    ? $"~{v.ItopodRate:0.##} boost-value/kill at the optimal floor — beats every one-shottable zone"
                    : $"~{v.BestRate:0.##} boost-value/kill (ITOPOD {v.ItopodRate:0.##}) — updates with your drop chance";
                if (Settings != null && Settings.AdvisorFarmBoost && !BoostFarmAdvisor.BoostDemandExists(out var why))
                    line2 += $" · no demand ({why}) — ITOPOD wins";
                UiLayout.FitOrGrow(_boostLine2, line2, 2);

                var g = GearFarmAdvisor.Analyze();
                UiLayout.FitOrGrow(_gearLine, g.Known ? g.Text : "", 2);
            }
            catch (Exception ex) { LogDebug($"Boost advice: {ex.Message}"); }
        }

        private void RefreshFloorInfo()
        {
            try
            {
                var c = Main.Character;
                if (c == null) return;
                double atk = c.totalAdvAttack() * c.idleAttackPower() / 771.375;
                int optimal = atk > 1 ? (int)Math.Floor(Math.Log(atk, 1.05)) : 0;
                _floorInfo.Text = $"Optimal idle floor right now: {optimal}  (highest reached: {c.adventure.highestItopodLevel})";
            }
            catch (Exception ex) { LogDebug($"Floor info: {ex.Message}"); }
        }
    }
}
