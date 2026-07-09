using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Systems > BOOSTS sub-tab, V3 (user-approved): segmented [BOOSTING] [TRANSFORMS] views, each
    // getting the full page. First clean rebuild of a legacy section — the resx Boosts page retires
    // to Advanced as the escape hatch.
    //
    // BOOSTING: ADVISOR ACTIVE (green, advisor writes Settings.PriorityBoosts: equipped first via the
    // existing manager pass, then KEEP items by objective usage, then chain climbers) / MANUAL MODE
    // (red: editable priority + blacklist lists). Cube priority + favored MacGuffin ride the top row.
    // TRANSFORMS: one row per chain — live tier/level state + Auto-climb / Keep max lvl / Filter lower.
    //
    // Layout pre-flight (page 620 wide, ~350 tall): top row toggle 10..160, "Cube" 180..215, combo
    // 218..318, "Guffin" 330..377, combo 382..502, refresh 580..608 — no overlaps. Advisor readout
    // list 600x190 at y44. Manual view: priority list h100 + edit row (tb 10..130, Add 136..196,
    // Remove 202..272, Up 278..318, Down 324..364), blacklist h80 + edit row — bottom 328 < 350.
    // Transforms rows at 56px pitch: name 10..120, status 125..375, checkboxes 385/479/563 (measured
    // 86/76/56 wide) right edge 619. Stacked-label pitch rule: all single lines, 18px+ spacing.
    public class BoostsPanel : Panel
    {
        private Button _segBoost;
        private Button _segXform;
        private Panel _boostPage;
        private Panel _xformPage;

        private Button _srcToggle;
        private ComboBox _cube;
        private ComboBox _guffin;
        private Panel _advisorView;
        private Panel _manualView;
        private ListBox _readout;
        private ListBox _prio;
        private TextBox _prioAdd;
        private ListBox _black;
        private TextBox _blackAdd;
        private ComboBox _order;

        // Layout C (user-approved): two-line cards. Line 1 = full item name + right-aligned toggle
        // BUTTONS (measured text — never checkboxes: Mono randomly drops checkbox glyphs). Line 2 =
        // progress bar (nested Panels, proven controls) + "level/100 · next: <name>" detail.
        private class ChainRow
        {
            public Label Name;
            public Button Climb;
            public Button KeepMax;
            public Button Filter;
            public Panel BarOuter;
            public Panel BarInner;
            public Label Detail;
            public void SetVisible(bool v)
            {
                Name.Visible = Climb.Visible = KeepMax.Visible = Filter.Visible = v;
                BarOuter.Visible = Detail.Visible = v;
            }
            public void SetY(int y)
            {
                Name.Top = y + 2;
                Climb.Top = y + 2;
                KeepMax.Top = y + 2;
                Filter.Top = y + 2;
                BarOuter.Top = y + 36;
                Detail.Top = y + 30;
            }
        }
        // Measured button width (design system: never hardcode text-fitted widths) — renderer-true.
        private static int MeasureBtn(string text) => Math.Max(42, UiLayout.MeasureText(text, UiTheme.Ui) + 22);

        private readonly List<ChainRow> _chains = new List<ChainRow>();
        private Panel _xformContent;
        private Label _xformNote1;
        private Label _xformNote2;
        private Label _xformEmpty;

        private bool _syncing;
        private readonly int _w;
        private readonly int _pw;           // per-page width
        private readonly bool _sideBySide;  // C1: full canvas shows BOOSTING and TRANSFORMS together

        // canvasW: explicit canvas width when hosted in an M1 section column (0 = UiLayout.PanelW).
        public BoostsPanel(int canvasW = 0)
        {
            _w = canvasW > 0 ? canvasW : UiLayout.PanelW;
            _sideBySide = _w >= 900;
            _pw = _sideBySide ? (_w - 40) / 2 : _w - 34;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            {
                int bx = 10;
                foreach (var name in new[] { "BOOSTING", "TRANSFORMS" })
                {
                    var b = new Button
                    {
                        Text = name,
                        Location = new Point(bx, 6),
                        Size = new Size(Math.Max(88, UiLayout.MeasureText(name, UiTheme.Ui) + 26), 25),
                        Font = UiTheme.Ui,
                        FlatStyle = FlatStyle.Flat,
                        Visible = !_sideBySide   // segmented buttons retire on the full canvas
                    };
                    b.FlatAppearance.BorderColor = UiTheme.Border;
                    Controls.Add(b);
                    bx += b.Width + 6;
                    if (name == "BOOSTING") _segBoost = b; else _segXform = b;
                }
            }
            _segBoost.Click += (s, e) => SelectPage(boost: true);
            _segXform.Click += (s, e) => SelectPage(boost: false);

            BuildBoostPage();
            BuildXformPage();
            if (_sideBySide)
            {
                // Both pages visible at once: BOOSTING left, TRANSFORMS right, headers instead of
                // the segmented pair.
                Controls.Add(new Label { Text = "BOOSTING", AutoSize = true, Font = UiTheme.ColHeader, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(10, 12) });
                Controls.Add(new Label { Text = "TRANSFORMS", AutoSize = true, Font = UiTheme.ColHeader, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(_pw + 30, 12) });
                _boostPage.Location = new Point(10, 34);
                _xformPage.Location = new Point(_pw + 30, 34);
                _boostPage.Visible = true;
                _xformPage.Visible = true;
                SyncFromSettings();
                RefreshReadout();
                RefreshChains();
            }
            else
            {
                _boostPage.Tag = "exclusive";
                _xformPage.Tag = "exclusive";
                SyncFromSettings();
                SelectPage(boost: true);
            }
            _advisorView.Tag = "exclusive";
            _manualView.Tag = "exclusive";
        }

        private void SelectPage(bool boost)
        {
            if (_sideBySide) return;   // both always visible on the full canvas
            _boostPage.Visible = boost;
            _xformPage.Visible = !boost;
            UiTheme.ApplyState(_segBoost, boost ? UiTheme.Accent : UiTheme.BtnFace, boost ? Color.White : UiTheme.Ink);
            UiTheme.ApplyState(_segXform, boost ? UiTheme.BtnFace : UiTheme.Accent, boost ? UiTheme.Ink : Color.White);
            if (!boost) RefreshChains();
            else RefreshReadout();
            UiLayout.AuditOnce(boost ? _boostPage : _xformPage, boost ? "Boosts/BOOSTING" : "Boosts/TRANSFORMS");
        }

        private void BuildBoostPage()
        {
            _boostPage = new Panel { Location = new Point(0, 38), Size = new Size(_pw, 312), BackColor = UiTheme.Ground, Visible = false };
            Controls.Add(_boostPage);

            _srcToggle = new Button { Text = "ADVISOR ACTIVE", Location = new Point(10, 10), Size = new Size(150, 24), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            _srcToggle.FlatAppearance.BorderColor = UiTheme.Border;
            _srcToggle.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.AutoBoostPriority = !Settings.AutoBoostPriority;
                SyncFromSettings();
            };
            _boostPage.Controls.Add(_srcToggle);

            var cubeLbl = new Label { Text = "Cube", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _boostPage.Controls.Add(cubeLbl);
            _cube = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            _cube.Items.AddRange(new object[] { "None", "Balanced", "Softcap", "Power", "Toughness" });
            _cube.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null) return;
                Settings.CubePriority = _cube.SelectedIndex;
            };
            _boostPage.Controls.Add(_cube);

            var gufLbl = new Label { Text = "Guffin", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _boostPage.Controls.Add(gufLbl);
            _guffin = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui };
            foreach (var kv in InventoryManager.macguffinList)
                _guffin.Items.Add(new KeyValuePair<int, string>(kv.Key, kv.Value));
            _guffin.DisplayMember = "Value";
            _guffin.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null || _guffin.SelectedItem == null) return;
                Settings.FavoredMacguffin = ((KeyValuePair<int, string>)_guffin.SelectedItem).Key;
            };
            _boostPage.Controls.Add(_guffin);

            var refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(refresh);
            refresh.Click += (s, e) => { if (Settings != null && Settings.AutoBoostPriority) RefreshReadout(recompute: true); else RefreshReadout(); };
            _boostPage.Controls.Add(refresh);

            // Measured row layout — the old hand-placed "Cube" label overlapped its combo by 3px.
            UiLayout.Row(10, 10, 8, _srcToggle, cubeLbl, _cube, gufLbl, _guffin, refresh);

            // ADVISOR view: computed order readout.
            _advisorView = new Panel { Location = new Point(0, 44), Size = new Size(_pw - 0, 268), BackColor = UiTheme.Ground, Visible = false };
            _boostPage.Controls.Add(_advisorView);
            _advisorView.Controls.Add(new Label
            {
                Text = "BOOST ORDER (advisor-written; blacklist advisor-managed)",
                Location = new Point(10, 0),
                AutoSize = true,
                Font = UiTheme.ColHeader,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            });
            _readout = new ListBox { Location = new Point(10, 24), Size = new Size(_pw - 30, 190), Font = UiTheme.Ui, BorderStyle = BorderStyle.FixedSingle, SelectionMode = SelectionMode.None };
            _advisorView.Controls.Add(_readout);
            _advisorView.Controls.Add(new Label
            {
                Text = "Order refreshes every 10 minutes (or press the refresh button above).",
                Location = new Point(10, 222),
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            });

            // MANUAL view: editable priority + blacklist.
            _manualView = new Panel { Location = new Point(0, 44), Size = new Size(_pw - 0, 268), BackColor = UiTheme.Ground, Visible = false };
            _boostPage.Controls.Add(_manualView);

            _manualView.Controls.Add(new Label { Text = "PRIORITY BOOSTS (item IDs, boosted top-down)", Location = new Point(10, 0), AutoSize = true, Font = UiTheme.ColHeader, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground });
            _prio = new ListBox { Location = new Point(10, 24), Size = new Size(_pw - 30, 90), Font = UiTheme.Ui, BorderStyle = BorderStyle.FixedSingle };
            _manualView.Controls.Add(_prio);
            int wAdd = MeasureBtn("Add"), wRem = MeasureBtn("Remove"), wUp = MeasureBtn("Up"), wDown = MeasureBtn("Down");
            _prioAdd = new TextBox { Location = new Point(10, 122), Width = 120, Font = UiTheme.Ui };
            _manualView.Controls.Add(_prioAdd);
            int bx = 136;
            _manualView.Controls.Add(MkBtn("Add", bx, 122, wAdd, () => EditList(true, add: true))); bx += wAdd + 6;
            _manualView.Controls.Add(MkBtn("Remove", bx, 122, wRem, () => EditList(true, add: false))); bx += wRem + 6;
            _manualView.Controls.Add(MkBtn("Up", bx, 122, wUp, () => MovePrio(-1))); bx += wUp + 6;
            _manualView.Controls.Add(MkBtn("Down", bx, 122, wDown, () => MovePrio(1)));

            _manualView.Controls.Add(new Label { Text = "BOOST BLACKLIST (never boost/merge these IDs)", Location = new Point(10, 156), AutoSize = true, Font = UiTheme.ColHeader, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground });
            _black = new ListBox { Location = new Point(10, 182), Size = new Size(_pw - 30, 56), Font = UiTheme.Ui, BorderStyle = BorderStyle.FixedSingle };
            _manualView.Controls.Add(_black);
            _blackAdd = new TextBox { Location = new Point(10, 244), Width = 120, Font = UiTheme.Ui };
            _manualView.Controls.Add(_blackAdd);
            bx = 136;
            _manualView.Controls.Add(MkBtn("Add", bx, 244, wAdd, () => EditList(false, add: true))); bx += wAdd + 6;
            _manualView.Controls.Add(MkBtn("Remove", bx, 244, wRem, () => EditList(false, add: false)));

            // Re-homed from the retired Old Boosts page (Phase C): the boost APPLICATION order —
            // Power/Toughness/Special as a six-permutation combo (Mono-safe: no reorder listbox).
            // Narrow M1 column: the combo won't fit after the Remove button — own row below.
            int ordX = bx + wRem + 20, ordY = 248;
            if (_pw < 560)
            {
                ordX = 10;
                ordY = 280;
                _manualView.Height = 312;
                _boostPage.Height = 356;
            }
            var ordLbl = new Label { Text = "Apply order", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(ordX, ordY + 4) };
            _manualView.Controls.Add(ordLbl);
            _order = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, Font = UiTheme.Ui, Location = new Point(ordX + UiLayout.MeasureText("Apply order", UiTheme.Ui) + 8, ordY) };
            foreach (var p in OrderPerms)
                _order.Items.Add(string.Join(" → ", p));
            _order.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || Settings == null || _order.SelectedIndex < 0) return;
                Settings.BoostPriority = (string[])OrderPerms[_order.SelectedIndex].Clone();
            };
            _manualView.Controls.Add(_order);
        }

        private static readonly string[][] OrderPerms =
        {
            new[] { "Power", "Toughness", "Special" },
            new[] { "Power", "Special", "Toughness" },
            new[] { "Toughness", "Power", "Special" },
            new[] { "Toughness", "Special", "Power" },
            new[] { "Special", "Power", "Toughness" },
            new[] { "Special", "Toughness", "Power" },
        };

        private Button MkBtn(string text, int x, int y, int w, Action onClick)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(b);
            b.Click += (s, e) => { try { onClick(); } catch (Exception ex) { LogDebug($"Boosts edit: {ex.Message}"); } };
            return b;
        }

        // Two-line cards, 54px pitch. Toggles right-aligned at edge 610 with MEASURED widths (the
        // "Keep Ma" truncation came from hardcoded widths); the name gets everything left of them.
        private void BuildXformPage()
        {
            _xformPage = new Panel { Location = new Point(0, 38), Size = new Size(_pw, 312), BackColor = UiTheme.Ground, Visible = false, AutoScroll = true };
            Controls.Add(_xformPage);
            _xformContent = new Panel { Location = new Point(0, 0), Size = new Size(_pw - 16, 312), BackColor = UiTheme.Ground };
            _xformPage.Controls.Add(_xformContent);

            int Measure(string t) => UiLayout.MeasureText(t, UiTheme.Ui) + 20;
            int wClimb = Measure("Climb");
            int wKeep = Measure("Keep Max");
            // Filter swaps text; size to the longer so it never moves or clips.
            int wFilter = Math.Max(Measure("Not Filtered"), Measure("Filtered"));
            int xFilter = _xformContent.Width - 4 - wFilter;
            int xKeep = xFilter - 6 - wKeep;
            int xClimb = xKeep - 6 - wClimb;
            int nameW = xClimb - 18;

            for (int i = 0; i < TransformManager.Chains.Length; i++)
            {
                int idx = i;
                var row = new ChainRow();
                _chains.Add(row);

                row.Name = new Label
                {
                    Text = "",
                    Location = new Point(10, 2),
                    Size = new Size(nameW, 22),
                    Font = UiTheme.Bold,
                    ForeColor = UiTheme.Accent,
                    BackColor = UiTheme.Ground
                };
                _xformContent.Controls.Add(row.Name);

                row.Climb = MkChainToggle("Climb", xClimb, wClimb, idx, () => Settings.TransformAutoClimb, v => Settings.TransformAutoClimb = v);
                row.KeepMax = MkChainToggle("Keep Max", xKeep, wKeep, idx, () => Settings.TransformKeepMax, v => Settings.TransformKeepMax = v);
                row.Filter = MkChainToggle("Not Filtered", xFilter, wFilter, idx, () => Settings.TransformFilter, v => Settings.TransformFilter = v);

                row.BarOuter = new Panel
                {
                    Location = new Point(10, 36),
                    Size = new Size(180, 10),
                    BackColor = UiTheme.Surface,
                    BorderStyle = BorderStyle.FixedSingle
                };
                row.BarInner = new Panel { Location = new Point(0, 0), Size = new Size(0, 8), BackColor = UiTheme.Accent };
                row.BarOuter.Controls.Add(row.BarInner);
                _xformContent.Controls.Add(row.BarOuter);

                row.Detail = new Label
                {
                    Text = "",
                    Location = new Point(200, 30),
                    Size = new Size(_xformContent.Width - 204, 22),
                    Font = UiTheme.Ui,
                    ForeColor = UiTheme.Muted,
                    BackColor = UiTheme.Ground
                };
                _xformContent.Controls.Add(row.Detail);

                row.SetVisible(false);
            }

            _xformEmpty = new Label
            {
                Text = "No transformable items owned yet — chains appear here when one drops.",
                Location = new Point(10, 14),
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground,
                Visible = false
            };
            _xformContent.Controls.Add(_xformEmpty);

            _xformNote1 = new Label
            {
                Text = "Held chains freeze only at-100 copies — spare copies keep merging.",
                Location = new Point(10, 240),
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            };
            _xformContent.Controls.Add(_xformNote1);
            _xformNote2 = new Label
            {
                Text = "Keep Max + Climb keeps one maxed copy; extras climb. Filter drops lower-tier loot.",
                Location = new Point(10, 259),
                AutoSize = true,
                Font = UiTheme.Ui,
                ForeColor = UiTheme.Muted,
                BackColor = UiTheme.Ground
            };
            _xformContent.Controls.Add(_xformNote2);
        }

        private Button MkChainToggle(string text, int x, int w, int idx, Func<int[]> get, Action<int[]> set)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, 2),
                Size = new Size(w, 24),
                Font = UiTheme.Ui,
                FlatStyle = FlatStyle.Flat
            };
            b.FlatAppearance.BorderColor = UiTheme.Border;
            b.Click += (s, e) =>
            {
                if (Settings == null) return;
                try
                {
                    var arr = (get() ?? new int[TransformManager.Chains.Length]).ToArray();
                    if (arr.Length < TransformManager.Chains.Length)
                        Array.Resize(ref arr, TransformManager.Chains.Length);
                    arr[idx] = arr[idx] != 0 ? 0 : 1;
                    set(arr);
                }
                catch (Exception ex) { LogDebug($"Chain flag: {ex.Message}"); }
                RefreshChains();
            };
            _xformContent.Controls.Add(b);
            return b;
        }

        private void EditList(bool prio, bool add)
        {
            if (Settings == null) return;
            var box = prio ? _prio : _black;
            var tb = prio ? _prioAdd : _blackAdd;
            var cur = (prio ? Settings.PriorityBoosts : Settings.BoostBlacklist)?.ToList() ?? new List<int>();

            if (add)
            {
                if (!int.TryParse(tb.Text.Trim(), out var id) || id <= 0) return;
                if (!cur.Contains(id)) cur.Add(id);
                tb.Text = "";
            }
            else
            {
                int sel = box.SelectedIndex;
                if (sel < 0 || sel >= cur.Count) return;
                cur.RemoveAt(sel);
            }
            if (prio) Settings.PriorityBoosts = cur.ToArray();
            else Settings.BoostBlacklist = cur.ToArray();
            SyncFromSettings();
        }

        private void MovePrio(int dir)
        {
            if (Settings == null) return;
            var cur = Settings.PriorityBoosts?.ToList() ?? new List<int>();
            int sel = _prio.SelectedIndex;
            int to = sel + dir;
            if (sel < 0 || sel >= cur.Count || to < 0 || to >= cur.Count) return;
            var tmp = cur[sel]; cur[sel] = cur[to]; cur[to] = tmp;
            Settings.PriorityBoosts = cur.ToArray();
            SyncFromSettings();
            _prio.SelectedIndex = to;
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            _syncing = true;
            try
            {
                bool auto = Settings.AutoBoostPriority;
                _srcToggle.Text = auto ? "ADVISOR ACTIVE" : "MANUAL MODE";
                UiTheme.ApplyState(_srcToggle, auto ? UiTheme.Cap : UiTheme.Danger, Color.White);
                _advisorView.Visible = auto;
                _manualView.Visible = !auto;

                int cube = Settings.CubePriority;
                if (cube >= 0 && cube < _cube.Items.Count) _cube.SelectedIndex = cube;
                for (int i = 0; i < _guffin.Items.Count; i++)
                    if (((KeyValuePair<int, string>)_guffin.Items[i]).Key == Settings.FavoredMacguffin)
                    { _guffin.SelectedIndex = i; break; }
                var cur = Settings.BoostPriority != null && Settings.BoostPriority.Length == 3
                    ? Settings.BoostPriority : OrderPerms[0];
                for (int i = 0; i < OrderPerms.Length; i++)
                    if (OrderPerms[i][0] == cur[0] && OrderPerms[i][1] == cur[1])
                    { _order.SelectedIndex = i; break; }

                _prio.BeginUpdate();
                _prio.Items.Clear();
                foreach (var id in Settings.PriorityBoosts ?? new int[0])
                    _prio.Items.Add($"{ItemNameNice(id)}  (#{id})");
                _prio.EndUpdate();

                _black.BeginUpdate();
                _black.Items.Clear();
                foreach (var id in Settings.BoostBlacklist ?? new int[0])
                    _black.Items.Add($"{ItemNameNice(id)}  (#{id})");
                _black.EndUpdate();

            }
            finally { _syncing = false; }
            RefreshChains();
            if (Settings.AutoBoostPriority) RefreshReadout();
        }

        private static bool Flag(int[] arr, int i) => arr != null && i < arr.Length && arr[i] != 0;

        // Advisor readout from the last computed verdict (cheap); the full compute (30+ optimizer
        // runs) happens ONLY on the explicit refresh button — never during form construction.
        private void RefreshReadout(bool recompute = false)
        {
            try
            {
                if (Main.Character == null) return;
                var v = InventoryAdvisor.Last;
                if (v == null && !recompute)
                {
                    _readout.BeginUpdate();
                    _readout.Items.Clear();
                    _readout.Items.Add("1. equipped gear  (always boosted first)");
                    _readout.Items.Add("… press the refresh button above to compute the ranked order");
                    _readout.EndUpdate();
                    return;
                }
                if (v == null || recompute)
                    v = InventoryAdvisor.Compute();

                var ids = InventoryAdvisor.AutoBoostPriority(v);
                _readout.BeginUpdate();
                _readout.Items.Clear();
                _readout.Items.Add("1. equipped gear  (always boosted first)");
                int n = 2;
                foreach (var id in ids)
                {
                    string note = v.Usage.TryGetValue(id, out var u) ? $"used by {u} objectives" : "chain climber";
                    _readout.Items.Add($"{n}. {ItemNameNice(id)}  (#{id})  ·  {note}");
                    n++;
                }
                _readout.EndUpdate();
            }
            catch (Exception ex) { LogDebug($"Boost readout: {ex.Message}"); }
        }

        // Live chain states: only OWNED chains show, packed top-down as two-line cards. C1 naming
        // ("Ascended x n") everywhere; next tier by NAME; top-tier singles show provenance.
        private void RefreshChains()
        {
            try
            {
                if (Main.Character == null || Settings == null) return;
                int y = 6;
                {
                    for (int i = 0; i < _chains.Count; i++)
                    {
                        var row = _chains[i];
                        var s = TransformManager.Read(i);
                        if (s.OwnedTier < 0)
                        {
                            row.SetVisible(false);
                            continue;
                        }

                        row.SetVisible(true);
                        row.SetY(y);
                        row.Name.Text = UiLayout.FitText(ItemNameNice(s.OwnedId), UiTheme.Bold, row.Name.Width - 4);

                        long lvl = Math.Max(0, Math.Min(100, s.Level));
                        row.BarInner.Width = (int)(178 * lvl / 100);
                        string detail;
                        if (s.NextId > 0)
                            detail = $"{s.Level}/100 · next: {ItemNameNice(s.NextId)}";
                        else if (s.OwnedTier > 0)
                            detail = $"{s.Level}/100 · top tier — from {ItemNameNice(TransformManager.Chains[i].Tiers[s.OwnedTier - 1])}";
                        else
                            detail = $"{s.Level}/100 · top tier";
                        row.Detail.Text = UiLayout.FitText(detail, UiTheme.Ui, row.Detail.Width - 4);

                        StyleOnOff(row.Climb, Flag(Settings.TransformAutoClimb, i));
                        StyleOnOff(row.KeepMax, Flag(Settings.TransformKeepMax, i));
                        bool filtered = Flag(Settings.TransformFilter, i);
                        row.Filter.Text = filtered ? "Filtered" : "Not Filtered";
                        UiTheme.ApplyState(row.Filter, filtered ? UiTheme.Faint : UiTheme.Cap, Color.White);

                        y += 54;
                    }
                }
                _xformEmpty.Visible = y == 6;
                _xformNote1.Top = Math.Max(y + 8, 60);
                _xformNote2.Top = _xformNote1.Top + UiTheme.LinePitch;
                _xformContent.Height = _xformNote2.Top + 24;
                _xformPage.AutoScrollMinSize = _xformContent.Size;
            }
            catch (Exception ex) { LogDebug($"Chain status: {ex.Message}"); }
        }

        private static void StyleOnOff(Button b, bool on)
        {
            UiTheme.ApplyState(b, on ? UiTheme.Cap : UiTheme.Danger, Color.White);
        }

    }
}
