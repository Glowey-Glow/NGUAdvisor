using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Loadouts tab v2 (user feedback: v1's five stacked cards with 2-line lists were unusable).
    // One mode visible at a time behind a sub-tab bar (the proven Combat/Systems switcher pattern),
    // giving the full tab to that mode: source picker + a tall WILL EQUIP list that shows the whole
    // loadout with no scrolling. "Override Advisor" checkbox replaced by an ADVISOR/MANUAL segmented
    // pair (measured buttons — clearer, and sidesteps the faint checkbox rendering seen in v1).
    //
    // Layout pre-flight (~660x380 tab client): mode bar buttons measured +26px pad at y6.
    // Source row y48: "SOURCE" x10 (~50px) · [ADVISOR] x64 w76 (right 140) · [MANUAL] x144 w72
    // (right 216) · objective combo x230 w170 (right 400) · "Keep Respawn" x412 (~111px, right 523) ·
    // refresh x600 w28 (right 628). Manual row y78: "IDs" x10 · textbox x40 w380 (right 420) · Save
    // x426 w52 (right 478) · "Use Current Gear" x486 w126 (right 612). Preview header y112; list
    // y130 h228 w618 (~13 rows visible; loadouts are <=12 items). No overlaps, no horizontal scroll.
    public class LoadoutsPanel : Panel
    {
        private class Mode
        {
            public string Name;
            public Func<string> GetObj;
            public Action<string> SetObj;
            public Func<bool> GetResp;
            public Action<bool> SetResp;
            public Func<int[]> GetStatic;
            public Action<int[]> SetStatic;
            public bool GoldDefault;

            public Button Tab;
            public Panel Page;
            public Button Src;
            public Button Refresh;
            public Button Save;
            public Button UseCur;
            public ComboBox Combo;
            public Button Resp;
            public Panel ManualRow;
            public TextBox Ids;
            public Label PvHead;
            public ListBox Preview;
            public string LastObjective;
        }

        private readonly List<Mode> _modes = new List<Mode>();
        private bool _syncing;
        private int _hostW;
        private int _wSave;
        private int _wUse;

        // Measured button width (design system: never hardcode text-fitted widths) — renderer-true.
        private static int MeasureBtn(string text) => Math.Max(46, UiLayout.MeasureText(text, UiTheme.Ui) + 22);

        // Mono settles the form's true (DPI-scaled) size after construction, so ctor-time widths can
        // be stale — SettingsForm calls this from Shown with the final client width to re-fit.
        public void SetHostWidth(int hostW)
        {
            hostW = Math.Max(560, hostW);
            if (hostW == _hostW) return;
            _hostW = hostW;
            foreach (var m in _modes)
            {
                if (m.Page == null) continue;
                m.Page.Width = _hostW;
                m.Refresh.Left = _hostW - 46;
                m.Preview.Width = _hostW - 22;
                m.ManualRow.Width = _hostW;
                int idsW = _hostW - (40 + 6 + _wSave + 8 + _wUse + 10);
                m.Ids.Width = idsW;
                m.Save.Left = 40 + idsW + 6;
                m.UseCur.Left = 40 + idsW + 6 + _wSave + 8;
            }
        }

        public LoadoutsPanel(int hostW = 640)
        {
            _hostW = Math.Max(560, hostW);
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _modes.Add(new Mode
            {
                Name = "TITAN",
                GetObj = () => Settings.TitanObjective, SetObj = v => Settings.TitanObjective = v,
                GetResp = () => Settings.TitanObjectiveRespawn, SetResp = v => Settings.TitanObjectiveRespawn = v,
                GetStatic = () => Settings.TitanLoadout, SetStatic = v => Settings.TitanLoadout = v,
            });
            _modes.Add(new Mode
            {
                Name = "GOLD",
                GetObj = () => Settings.GoldObjective, SetObj = v => Settings.GoldObjective = v,
                GetResp = () => Settings.GoldObjectiveRespawn, SetResp = v => Settings.GoldObjectiveRespawn = v,
                GetStatic = () => Settings.GoldDropLoadout, SetStatic = v => Settings.GoldDropLoadout = v,
                GoldDefault = true,
            });
            _modes.Add(new Mode
            {
                Name = "QUEST",
                GetObj = () => Settings.QuestObjective, SetObj = v => Settings.QuestObjective = v,
                GetResp = () => Settings.QuestObjectiveRespawn, SetResp = v => Settings.QuestObjectiveRespawn = v,
                GetStatic = () => Settings.QuestLoadout, SetStatic = v => Settings.QuestLoadout = v,
            });
            _modes.Add(new Mode
            {
                Name = "YGGDRASIL",
                GetObj = () => Settings.YggdrasilObjective, SetObj = v => Settings.YggdrasilObjective = v,
                GetResp = () => Settings.YggdrasilObjectiveRespawn, SetResp = v => Settings.YggdrasilObjectiveRespawn = v,
                GetStatic = () => Settings.YggdrasilLoadout, SetStatic = v => Settings.YggdrasilLoadout = v,
            });
            _modes.Add(new Mode
            {
                Name = "COOKING",
                GetObj = () => Settings.CookingObjective, SetObj = v => Settings.CookingObjective = v,
                GetResp = () => Settings.CookingObjectiveRespawn, SetResp = v => Settings.CookingObjectiveRespawn = v,
                GetStatic = () => Settings.CookingLoadout, SetStatic = v => Settings.CookingLoadout = v,
            });
            // Re-homed from the retired Old Pit page (Phase B): the 7-day shockwave set is a plain
            // static list — no objective concept, so the objective lambdas are inert.
            _modes.Add(new Mode
            {
                Name = "SHOCKWAVE",
                GetObj = () => "", SetObj = v => { },
                GetResp = () => false, SetResp = v => { },
                GetStatic = () => Settings.Shockwave, SetStatic = v => Settings.Shockwave = v,
            });

            // Mode sub-tab bar (measured widths — never estimate).
            int bx = 10;
            {
                foreach (var m in _modes)
                {
                    int textW = UiLayout.MeasureText(m.Name, UiTheme.Ui);
                    var mode = m;
                    m.Tab = new Button
                    {
                        Text = m.Name,
                        Location = new Point(bx, 6),
                        Size = new Size(Math.Max(70, textW + 26), 25),
                        Font = UiTheme.Ui,
                        FlatStyle = FlatStyle.Flat
                    };
                    m.Tab.FlatAppearance.BorderColor = UiTheme.Border;
                    m.Tab.Click += (s, e) => SelectMode(mode);
                    Controls.Add(m.Tab);
                    bx += m.Tab.Width + 6;
                }
            }

            foreach (var m in _modes)
                BuildPage(m);

            SyncFromSettings();
            SelectMode(_modes[0]);
        }

        // A1 rail sub-nav: the mode bar folds into the left rail — the rail calls these.
        public void HideModeBar()
        {
            foreach (var m in _modes)
                if (m.Tab != null) m.Tab.Visible = false;
        }

        public void SelectModeByName(string name)
        {
            foreach (var m in _modes)
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) { SelectMode(m); return; }
        }

        private void SelectMode(Mode sel)
        {
            foreach (var m in _modes)
            {
                bool on = m == sel;
                m.Page.Visible = on;
                UiTheme.ApplyState(m.Tab, on ? UiTheme.Accent : UiTheme.BtnFace, on ? Color.White : UiTheme.Ink);
            }
            RefreshCard(sel);
            UiLayout.AuditOnce(sel.Page, $"Loadouts/{sel.Name}");
        }

        private void BuildPage(Mode m)
        {
            var page = new Panel
            {
                Location = new Point(0, 38),
                Size = new Size(_hostW, 340),
                BackColor = UiTheme.Ground,
                Visible = false,
                Tag = "exclusive"   // mode pages share the area below the sub-tab bar
            };
            m.Page = page;
            Controls.Add(page);

            page.Controls.Add(new Label
            {
                Text = "SOURCE",
                Location = new Point(10, 15),
                AutoSize = true,
                Font = UiTheme.ColHeader,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            });

            // One status toggle, styled like the AUTO light (user request): green ADVISOR ACTIVE /
            // red MANUAL MODE; clicking flips the source and reveals/hides the manual ID row.
            // x measured off the SOURCE label (hand-set 64 overlapped it by 3px — audit catch).
            int srcX = 10 + UiLayout.MeasureText("SOURCE", UiTheme.ColHeader) + 10;
            m.Src = new Button { Text = "ADVISOR ACTIVE", Location = new Point(srcX, 10), Size = new Size(150, 24), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            m.Src.FlatAppearance.BorderColor = UiTheme.Border;
            m.Src.Click += (s, e) =>
            {
                bool manualNow = false;
                try { manualNow = string.IsNullOrEmpty(m.GetObj()); } catch { }
                SetSource(m, manual: !manualNow);
            };
            page.Controls.Add(m.Src);

            m.Combo = new ComboBox
            {
                Location = new Point(Math.Max(230, srcX + 158), 11),
                Width = 170,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = UiTheme.Ui
            };
            foreach (var o in GearObjectives.Objectives)
                m.Combo.Items.Add(o.Name);
            m.Combo.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null) return;
                if (string.IsNullOrEmpty(m.GetObj())) return;   // manual mode: combo is display-only
                try { m.SetObj((string)m.Combo.SelectedItem ?? ""); } catch (Exception ex) { LogDebug($"Loadouts obj: {ex.Message}"); }
                RefreshCard(m);
            };
            page.Controls.Add(m.Combo);

            // Toggle button (design system: green on / red off; checkboxes render unreliably).
            m.Resp = new Button
            {
                Text = "Keep Respawn",
                Location = new Point(412, 10),
                Size = new Size(MeasureBtn("Keep Respawn"), 24),
                Font = UiTheme.Ui,
                FlatStyle = FlatStyle.Flat
            };
            m.Resp.FlatAppearance.BorderColor = UiTheme.Border;
            m.Resp.Click += (s, e) =>
            {
                if (Settings == null) return;
                bool now = false;
                try { now = m.GetResp(); } catch { }
                try { m.SetResp(!now); } catch (Exception ex) { LogDebug($"Loadouts resp: {ex.Message}"); }
                SyncCard(m);
            };
            page.Controls.Add(m.Resp);

            m.Refresh = new Button { Text = "↻", Location = new Point(_hostW - 46, 10), Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(m.Refresh);
            m.Refresh.Click += (s, e) => RefreshCard(m);
            page.Controls.Add(m.Refresh);

            m.ManualRow = new Panel { Location = new Point(0, 42), Size = new Size(_hostW, 28), BackColor = UiTheme.Ground, Visible = false };
            page.Controls.Add(m.ManualRow);

            m.ManualRow.Controls.Add(new Label
            {
                Text = "IDs",
                Location = new Point(10, 6),
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            });
            // Width-aware manual row: IDs box grows with the host; buttons (MEASURED widths — the old
            // hardcoded 126 clipped "Use Current Gear") trail it, row ends 10px inside the host edge.
            if (_wSave == 0) { _wSave = MeasureBtn("Save"); _wUse = MeasureBtn("Use Current Gear"); }
            int idsW = _hostW - (40 + 6 + _wSave + 8 + _wUse + 10);
            m.Ids = new TextBox { Location = new Point(40, 3), Width = idsW, Font = UiTheme.Ui };
            m.ManualRow.Controls.Add(m.Ids);

            m.Save = new Button { Text = "Save", Location = new Point(40 + idsW + 6, 2), Size = new Size(_wSave, 23), Font = UiTheme.Ui };
            var save = m.Save;
            UiTheme.StyleFlat(save);
            save.Click += (s, e) =>
            {
                if (Settings == null) return;
                var ids = ParseIds(m.Ids.Text);
                try { m.SetStatic(ids); } catch (Exception ex) { LogDebug($"Loadouts save: {ex.Message}"); }
                Log($"Loadouts: {m.Name} manual loadout set ({ids.Length} items)");
                RefreshCard(m);
            };
            m.ManualRow.Controls.Add(save);

            m.UseCur = new Button { Text = "Use Current Gear", Location = new Point(40 + idsW + 6 + _wSave + 8, 2), Size = new Size(_wUse, 23), Font = UiTheme.Ui };
            var useCur = m.UseCur;
            UiTheme.StyleFlat(useCur);
            useCur.Click += (s, e) =>
            {
                var ids = LoadoutManager.CurrentGearIds();
                if (ids.Length == 0) return;
                m.Ids.Text = string.Join(", ", ids.Select(x => x.ToString()).ToArray());
                if (Settings == null) return;
                try { m.SetStatic(ids); } catch (Exception ex) { LogDebug($"Loadouts usecur: {ex.Message}"); }
                Log($"Loadouts: {m.Name} manual loadout set from equipped gear ({ids.Length} items)");
                RefreshCard(m);
            };
            m.ManualRow.Controls.Add(useCur);

            m.PvHead = new Label
            {
                Text = "WILL EQUIP",
                Location = new Point(10, 78),
                AutoSize = true,
                Font = UiTheme.ColHeader,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            };
            page.Controls.Add(m.PvHead);

            m.Preview = new ListBox
            {
                Location = new Point(10, 104),
                Size = new Size(_hostW - 22, 228),
                Font = UiTheme.Ui,
                BorderStyle = BorderStyle.FixedSingle
            };
            page.Controls.Add(m.Preview);
        }

        private void SetSource(Mode m, bool manual)
        {
            if (Settings == null) return;
            try
            {
                if (manual)
                {
                    var cur = m.GetObj();
                    if (!string.IsNullOrEmpty(cur)) m.LastObjective = cur;
                    m.SetObj("");
                }
                else
                {
                    string back = !string.IsNullOrEmpty(m.LastObjective) ? m.LastObjective : (string)m.Combo.SelectedItem;
                    if (string.IsNullOrEmpty(back) && m.Combo.Items.Count > 0) back = (string)m.Combo.Items[0];
                    m.SetObj(back ?? "");
                }
            }
            catch (Exception ex) { LogDebug($"Loadouts source: {ex.Message}"); }
            SyncCard(m);
        }

        private static int[] ParseIds(string text)
        {
            var list = new List<int>();
            foreach (Match match in Regex.Matches(text ?? "", @"\d+"))
                if (int.TryParse(match.Value, out var id) && id > 0 && !list.Contains(id))
                    list.Add(id);
            return list.ToArray();
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            foreach (var m in _modes) SyncCard(m);
        }

        private void SyncCard(Mode m)
        {
            _syncing = true;
            try
            {
                string obj = "";
                try { obj = m.GetObj() ?? ""; } catch { }
                bool manual = string.IsNullOrEmpty(obj);
                if (!manual) m.LastObjective = obj;

                m.Src.Text = manual ? "MANUAL MODE" : "ADVISOR ACTIVE";
                UiTheme.ApplyState(m.Src, manual ? UiTheme.Danger : UiTheme.Cap, Color.White);
                m.ManualRow.Visible = manual;
                m.Combo.Enabled = !manual;

                string show = !string.IsNullOrEmpty(obj) ? obj : m.LastObjective;
                if (string.IsNullOrEmpty(show) && m.GoldDefault) show = "Gold Drops";
                int idx = show != null ? m.Combo.Items.IndexOf(show) : -1;
                if (idx >= 0) m.Combo.SelectedIndex = idx;

                bool resp = false;
                try { resp = m.GetResp(); } catch { }
                UiTheme.ApplyState(m.Resp, resp ? UiTheme.Cap : UiTheme.Danger, Color.White);

                var ids = new int[0];
                try { ids = m.GetStatic() ?? new int[0]; } catch { }
                m.Ids.Text = string.Join(", ", ids.Where(x => x > 0).Select(x => x.ToString()).ToArray());
            }
            finally { _syncing = false; }
            RefreshCard(m);
        }

        // WILL EQUIP preview via the same resolution the swap code uses.
        private void RefreshCard(Mode m)
        {
            try
            {
                if (Settings == null || Main.Character == null) return;

                string obj = "";
                try { obj = m.GetObj() ?? ""; } catch { }
                int[] ids;
                string note;

                if (string.IsNullOrEmpty(obj))
                {
                    int[] fallback = new int[0];
                    try { fallback = (m.GetStatic() ?? new int[0]).Where(x => x > 0).ToArray(); } catch { }
                    if (m.GoldDefault && fallback.Length == 0)
                    {
                        ids = GearOptimizer.ResolveGoldGear();
                        note = "advisor default: Gold Drops";
                    }
                    else
                    {
                        ids = fallback;
                        note = ids.Length > 0 ? "your manual items" : "nothing configured";
                    }
                }
                else
                {
                    var objective = GearOptimizer.FindObjective(obj);
                    bool resp = false;
                    try { resp = m.GetResp(); } catch { }
                    ids = objective != null ? GearOptimizer.OptimizeIds(objective, resp) : new int[0];
                    note = "optimized live at swap time";
                }

                ids = (ids ?? new int[0]).Where(x => x > 0).Distinct().ToArray();
                m.PvHead.Text = $"WILL EQUIP ({ids.Length}) — {note}".ToUpperInvariant();
                m.Preview.BeginUpdate();
                m.Preview.Items.Clear();
                foreach (var id in ids)
                    m.Preview.Items.Add($"{ItemNameNice(id)}  (#{id})");
                m.Preview.EndUpdate();
            }
            catch (Exception ex) { LogDebug($"Loadouts preview {m.Name}: {ex.Message}"); }
        }

        public void RefreshPreviews()
        {
            foreach (var m in _modes)
                if (m.Page != null && m.Page.Visible)
                    RefreshCard(m);
        }
    }
}
