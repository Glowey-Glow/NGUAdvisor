using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NGUAdvisor.Managers;

namespace NGUAdvisor
{
    // Editor for the Gear timeline. Each card is a time breakpoint with an ORDERED list of item IDs (the
    // profile's "ID" array, equipped slot-by-slot in order). Item names are shown from the game.
    //
    // Bridge to the external gear-optimizer: "Paste IDs" replaces the list from a copied ID list (any
    // separators), and "Copy IDs" exports the current list. Named backup loadouts in the breakpoint
    // (e.g. "NoTM (...)") are preserved by the model and surfaced as a count.
    //
    // Mono-safe explicit layout + single-content-child scroll, UiTheme styling.
    public class GearEditorPanel : UserControl
    {
        private const int RowH = 26;
        private const int HeaderH = 40;
        private const int SourceH = 40;
        private const int ObjInfoH = 46;
        private const int BarH = 30;
        private const int ColHeadH = 20;
        private const int AddH = 30;
        private const int BodyPad = 10;
        private const int StripW = 6;
        private const int CardGap = 10;
        private const int OuterPad = 8;

        private readonly List<ProfileModel.ListBreakpoint> _data;
        private readonly Color _accent;
        private readonly Panel _scroll;
        private readonly Panel _content;
        private readonly List<Card> _cards = new List<Card>();
        public event EventHandler Changed;

        public GearEditorPanel(List<ProfileModel.ListBreakpoint> data, Color accent)
        {
            _data = data ?? new List<ProfileModel.ListBreakpoint>();
            _accent = accent;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;

            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Ground };
            _content = new Panel { Location = new Point(0, 0), BackColor = UiTheme.Ground };
            _scroll.Controls.Add(_content);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 8, 0, 0), BackColor = UiTheme.Ground };
            var addBtn = new Button { Text = "+ Add time breakpoint", Height = 26, Width = 170, Font = UiTheme.Ui };
            UiTheme.StyleFlat(addBtn);
            addBtn.Click += (s, e) => AddBreakpoint();
            toolbar.Controls.Add(addBtn);
            toolbar.Controls.Add(new Label { Text = "Items equip in list order. Paste an ID list from the gear-optimizer into a breakpoint.", AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.Ui, Margin = new Padding(10, 6, 0, 0) });

            Controls.Add(_scroll);
            Controls.Add(toolbar);

            _scroll.ClientSizeChanged += (s, e) => Relayout();
            RebuildCards();
        }

        private int CardWidth => Math.Max(460, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - OuterPad * 2);

        private void RebuildCards()
        {
            _content.SuspendLayout();
            foreach (var c in _cards) { _content.Controls.Remove(c); c.Dispose(); }
            _cards.Clear();
            foreach (var bp in _data)
            {
                var card = Build(bp);
                _cards.Add(card);
                _content.Controls.Add(card);
            }
            _content.ResumeLayout();
            Relayout();
        }

        private Card Build(ProfileModel.ListBreakpoint bp)
        {
            var card = new Card(bp, _accent);
            card.Changed += (s, e) => OnChanged();
            card.CardResized += (s, e) => Relayout();
            card.DeleteRequested += (s, e) => { _data.Remove(bp); RebuildCards(); OnChanged(); };
            return card;
        }

        private void AddBreakpoint()
        {
            var bp = new ProfileModel.ListBreakpoint { TimeSeconds = 0 };
            _data.Add(bp);
            var card = Build(bp);
            _cards.Add(card);
            _content.Controls.Add(card);
            Relayout();
            _scroll.ScrollControlIntoView(card);
            OnChanged();
        }

        private void Relayout()
        {
            int w = CardWidth;
            int y = OuterPad;
            foreach (var card in _cards)
            {
                card.SetWidth(w);
                card.Location = new Point(OuterPad, y);
                y += card.Height + CardGap;
            }
            _content.Size = new Size(w + OuterPad * 2, y + OuterPad);
            _scroll.AutoScrollMinSize = _content.Size;
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // Parse any pasted text into a list of positive item ids (handles commas, brackets, spaces, newlines).
        internal static List<int> ParseIds(string text)
        {
            var ids = new List<int>();
            if (string.IsNullOrEmpty(text)) return ids;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\\d+"))
                if (int.TryParse(m.Value, out var v) && v > 0) ids.Add(v);
            return ids;
        }

        // ---------------------------------------------------------------- card

        private class Card : Panel
        {
            private readonly ProfileModel.ListBreakpoint _bp;
            private readonly Panel _body, _rows, _bar, _colHead, _objPanel;
            private readonly NumericUpDown _h, _m, _s;
            private readonly Label _countLbl, _backupLbl, _orderHdr, _objInfo;
            private readonly ComboBox _source;
            private readonly CheckBox _respawn;
            private readonly Button _addItem;
            private Button _del;
            private ComboBox _chTag;
            private bool _loading;

            private bool ObjectiveMode => !string.IsNullOrEmpty(_bp.Objective);

            public event EventHandler Changed;
            public event EventHandler DeleteRequested;
            public event EventHandler CardResized;

            public Card(ProfileModel.ListBreakpoint bp, Color accent)
            {
                _bp = bp;
                BorderStyle = BorderStyle.FixedSingle;
                BackColor = UiTheme.Surface;

                var strip = new Panel { Dock = DockStyle.Left, Width = StripW, BackColor = accent };
                _body = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(12, BodyPad, 12, BodyPad) };

                var header = new Panel { Dock = DockStyle.Top, Height = HeaderH, BackColor = UiTheme.Surface };
                var chip = new Panel { Location = new Point(0, 4), Size = new Size(238, 30), BackColor = UiTheme.AccentWeak, BorderStyle = BorderStyle.FixedSingle };
                chip.Controls.Add(new Label { Text = "TIME", Location = new Point(8, 9), AutoSize = true, ForeColor = UiTheme.Accent, Font = UiTheme.Chip });
                _h = Nud(42, 9999); _m = Nud(106, 59); _s = Nud(170, 59);
                chip.Controls.Add(Sep("h", 90)); chip.Controls.Add(Sep("m", 154)); chip.Controls.Add(Sep("s", 218));
                chip.Controls.Add(_h); chip.Controls.Add(_m); chip.Controls.Add(_s);
                header.Controls.Add(chip);
                header.Controls.Add(new Label { Text = "of the rebirth", Location = new Point(250, 11), AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.Ui });
                _del = new Button { Text = "🗑  Delete breakpoint", Width = 150, Height = 26, Top = 4, Font = UiTheme.Ui };
                UiTheme.StyleFlat(_del); _del.ForeColor = UiTheme.Danger;
                _del.Click += (s, e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
                header.Controls.Add(_del);
                // Challenge tag: this breakpoint applies only while the picked challenge is active.
                _chTag = ChallengeTagUi.Attach(header, () => _bp.Challenge, v => { _bp.Challenge = v; Changed?.Invoke(this, EventArgs.Empty); });

                // Gear source band: pick Manual item IDs, or optimize live for an objective. The tinted
                // band + bold label + bordered dropdown make the mode selector read as a distinct control.
                var sourcePanel = new Panel { Dock = DockStyle.Top, Height = SourceH, BackColor = UiTheme.AccentWeak };
                sourcePanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = UiTheme.Border });
                sourcePanel.Controls.Add(new Label { Text = "GEAR SOURCE", Location = new Point(2, 13), AutoSize = true, ForeColor = UiTheme.Accent, Font = UiTheme.Bold });
                _source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(104, 9), Width = 260, Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat, BackColor = UiTheme.Surface, ForeColor = UiTheme.Ink };
                _source.Items.Add("Manual (item IDs)");
                foreach (var o in GearObjectives.Objectives) _source.Items.Add("Optimize: " + o.Name);
                sourcePanel.Controls.Add(_source);

                // Objective-mode details: the "keep top respawn" toggle + a live-optimize note.
                _objPanel = new Panel { Dock = DockStyle.Top, Height = ObjInfoH, BackColor = UiTheme.Surface };
                _respawn = new CheckBox { Text = "Always keep the single best Respawn item", Location = new Point(2, 3), AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Ink };
                _respawn.CheckedChanged += RespawnChanged;
                _objInfo = new Label { Location = new Point(2, 26), AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.Ui, Text = "Gear is auto-optimized live for this objective while the breakpoint is active." };
                _objPanel.Controls.Add(_respawn);
                _objPanel.Controls.Add(_objInfo);

                // paste/copy bar
                _bar = new Panel { Dock = DockStyle.Top, Height = BarH, BackColor = UiTheme.Surface };
                var paste = new Button { Text = "Paste IDs", Location = new Point(0, 2), Width = 90, Height = 24, Font = UiTheme.Ui };
                var copy = new Button { Text = "Copy IDs", Location = new Point(96, 2), Width = 90, Height = 24, Font = UiTheme.Ui };
                UiTheme.StyleFlat(paste); UiTheme.StyleFlat(copy);
                paste.Click += (s, e) => PasteIds();
                copy.Click += (s, e) => CopyIds();
                _bar.Controls.Add(paste); _bar.Controls.Add(copy);
                _countLbl = new Label { Location = new Point(196, 6), AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.Ui };
                _bar.Controls.Add(_countLbl);
                _backupLbl = new Label { Location = new Point(300, 6), AutoSize = true, ForeColor = UiTheme.Faint, Font = UiTheme.Ui };
                _bar.Controls.Add(_backupLbl);

                _colHead = new Panel { Dock = DockStyle.Top, Height = ColHeadH, BackColor = UiTheme.Surface };
                _colHead.Controls.Add(new Label { Text = "ITEM ID", Location = new Point(6, 5), AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.ColHeader });
                _colHead.Controls.Add(new Label { Text = "NAME", Location = new Point(74, 5), AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.ColHeader });
                _orderHdr = new Label { Text = "ORDER", Location = new Point(0, 5), AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.ColHeader };
                _colHead.Controls.Add(_orderHdr);
                _colHead.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = UiTheme.Border });

                _addItem = new Button { Text = "+ Add item", Height = AddH, Dock = DockStyle.Bottom, Font = UiTheme.Ui };
                UiTheme.StyleGhost(_addItem);
                _addItem.Click += (s, e) => { AddRow(new Row(0)); Restripe(); Sync(); RecalcHeight(); };

                _rows = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };

                _body.Controls.Add(_rows);
                _body.Controls.Add(_addItem);
                _body.Controls.Add(_colHead);
                _body.Controls.Add(_bar);
                _body.Controls.Add(_objPanel);
                _body.Controls.Add(sourcePanel);
                _body.Controls.Add(header);

                Controls.Add(_body);
                Controls.Add(strip);

                _loading = true;
                _h.Value = Math.Min(_h.Maximum, bp.Hours);
                _m.Value = bp.Minutes;
                _s.Value = bp.Seconds;
                foreach (var id in bp.Items) AddRow(new Row(id));

                // Initialize the gear-source dropdown from the model's objective (0 = Manual). Any
                // objective string that doesn't match a preset is added verbatim so it round-trips.
                int objIdx = 0;
                if (ObjectiveMode)
                {
                    for (int i = 0; i < GearObjectives.Objectives.Count; i++)
                        if (string.Equals(GearObjectives.Objectives[i].Name, _bp.Objective, StringComparison.OrdinalIgnoreCase))
                        { objIdx = i + 1; break; }
                    if (objIdx == 0) { _source.Items.Add("Optimize: " + _bp.Objective); objIdx = _source.Items.Count - 1; }
                }
                _source.SelectedIndex = objIdx;
                _source.SelectedIndexChanged += SourceChanged;
                _respawn.Checked = _bp.ForceRespawn;

                _loading = false;
                Restripe();
                ApplyMode();

                _h.ValueChanged += TimeChanged; _m.ValueChanged += TimeChanged; _s.ValueChanged += TimeChanged;
            }

            private static Label Sep(string t, int x) => new Label { Text = t, Location = new Point(x, 9), AutoSize = true, ForeColor = UiTheme.Faint, Font = UiTheme.Ui };
            private NumericUpDown Nud(int x, int max) => new NumericUpDown { Minimum = 0, Maximum = max, Width = 46, Location = new Point(x, 4), Font = UiTheme.Ui, TextAlign = HorizontalAlignment.Right };

            public void SetWidth(int w)
            {
                Width = w;
                int rowW = w - StripW - 24 - 4;
                foreach (Control c in _rows.Controls) if (c is Row r) r.SetWidth(rowW);
                int bodyW = w - StripW - 2;
                if (_del != null) _del.Left = bodyW - _del.Width - 24;
                // Anchor the challenge picker left of Delete so they can never collide.
                if (_chTag != null && _del != null) _chTag.Left = _del.Left - _chTag.Width - 10;
                _orderHdr.Left = rowW - 84;
                RecalcHeight();
            }

            private void RecalcHeight()
            {
                int y = 0;
                foreach (Control c in _rows.Controls) { c.Location = new Point(0, y); y += RowH; }
                int rowsH = Math.Max(RowH, _rows.Controls.Count * RowH);
                int contentH = ObjectiveMode ? ObjInfoH : (BarH + ColHeadH + rowsH + AddH);
                int newH = BodyPad * 2 + HeaderH + SourceH + contentH + 2;
                if (Height != newH) { Height = newH; CardResized?.Invoke(this, EventArgs.Empty); }
            }

            // Toggle the card between Manual (ID rows) and Optimize (live objective) modes.
            private void ApplyMode()
            {
                bool obj = ObjectiveMode;
                _objPanel.Visible = obj;
                _bar.Visible = !obj;
                _colHead.Visible = !obj;
                _rows.Visible = !obj;
                _addItem.Visible = !obj;
                if (obj)
                    _objInfo.Text = "Gear is auto-optimized live for \"" + _bp.Objective + "\" while this breakpoint is active.";
                UpdateInfo();
                RecalcHeight();
            }

            private void SourceChanged(object sender, EventArgs e)
            {
                if (_loading) return;
                if (_source.SelectedIndex <= 0) _bp.Objective = "";
                else
                {
                    var txt = _source.SelectedItem.ToString();
                    const string pre = "Optimize: ";
                    _bp.Objective = txt.StartsWith(pre) ? txt.Substring(pre.Length) : txt;
                }
                ApplyMode();
                OnChanged();
            }

            private void RespawnChanged(object sender, EventArgs e)
            {
                if (_loading) return;
                _bp.ForceRespawn = _respawn.Checked;
                OnChanged();
            }

            private void AddRow(Row row)
            {
                row.Height = RowH;
                row.Changed += (s, e) => { Sync(); UpdateInfo(); };
                row.RemoveRequested += (s, e) => { _rows.Controls.Remove(row); row.Dispose(); Restripe(); Sync(); UpdateInfo(); RecalcHeight(); };
                row.MoveRequested += (s, e) =>
                {
                    var list = _rows.Controls.Cast<Control>().ToList();
                    int i = list.IndexOf(row);
                    int j = i + e.Direction;
                    if (j < 0 || j >= list.Count) return;
                    _rows.Controls.SetChildIndex(row, j);
                    Restripe(); Sync();
                };
                _rows.Controls.Add(row);
            }

            private void Restripe()
            {
                for (int i = 0; i < _rows.Controls.Count; i++)
                {
                    var c = _rows.Controls[i];
                    c.Location = new Point(0, i * RowH);
                    c.BackColor = (i % 2 == 0) ? UiTheme.Surface : UiTheme.Zebra;
                    if (c is Row r) r.ApplyRowColor();
                }
            }

            private void PasteIds()
            {
                try
                {
                    var ids = ParseIds(Clipboard.GetText());
                    if (ids.Count == 0) { UpdateInfo("Clipboard had no item IDs."); return; }
                    UiLayout.DisposeChildren(_rows);
                    _loading = true;
                    foreach (var id in ids) AddRow(new Row(id));
                    _loading = false;
                    Restripe(); Sync(); UpdateInfo($"Pasted {ids.Count} IDs."); RecalcHeight();
                }
                catch (Exception ex) { Main.LogDebug($"Gear paste failed: {ex.Message}"); }
            }

            private void CopyIds()
            {
                try
                {
                    var ids = _rows.Controls.Cast<Control>().OfType<Row>().Select(r => r.Id).Where(x => x > 0);
                    Clipboard.SetText(string.Join(", ", ids));
                    UpdateInfo("Copied IDs to clipboard.");
                }
                catch (Exception ex) { Main.LogDebug($"Gear copy failed: {ex.Message}"); }
            }

            private void UpdateInfo(string msg = null)
            {
                int count = _rows.Controls.Count;
                _countLbl.Text = msg ?? $"{count} item(s)";
                _backupLbl.Text = _bp.Extras.Count > 0 ? $"+ {_bp.Extras.Count} saved backup loadout(s) preserved" : "";
            }

            private void TimeChanged(object sender, EventArgs e)
            {
                if (_loading) return;
                _bp.TimeSeconds = (int)(_h.Value * 3600 + _m.Value * 60 + _s.Value);
                OnChanged();
            }

            private void Sync()
            {
                if (_loading) return;
                _bp.Items.Clear();
                foreach (Control c in _rows.Controls)
                    if (c is Row r && r.Id > 0) _bp.Items.Add(r.Id);
                OnChanged();
            }

            private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- row

        public class MoveEventArgs : EventArgs { public int Direction; }

        private class Row : Panel
        {
            private readonly NumericUpDown _id = new NumericUpDown { Minimum = 0, Maximum = 9999, Width = 60, Font = UiTheme.Ui, TextAlign = HorizontalAlignment.Right };
            private readonly Label _name = new Label { AutoSize = true, ForeColor = UiTheme.Ink, Font = UiTheme.Ui };
            private readonly Button _up, _down, _rem;

            public event EventHandler Changed;
            public event EventHandler RemoveRequested;
            public event EventHandler<MoveEventArgs> MoveRequested;

            public Row(int id)
            {
                Height = RowH;
                _id.Value = Math.Min(_id.Maximum, Math.Max(0, id));
                _up = Icon("↑"); _down = Icon("↓"); _rem = Icon("✕", true);
                _up.Click += (s, e) => MoveRequested?.Invoke(this, new MoveEventArgs { Direction = -1 });
                _down.Click += (s, e) => MoveRequested?.Invoke(this, new MoveEventArgs { Direction = 1 });
                _rem.Click += (s, e) => RemoveRequested?.Invoke(this, EventArgs.Empty);
                Controls.Add(_id); Controls.Add(_name); Controls.Add(_up); Controls.Add(_down); Controls.Add(_rem);
                UpdateName();
                _id.ValueChanged += (s, e) => { UpdateName(); Changed?.Invoke(this, EventArgs.Empty); };
                Place();
            }

            public int Id => (int)_id.Value;

            private void UpdateName() => _name.Text = Main.ItemName((int)_id.Value);

            public void ApplyRowColor() { _name.BackColor = BackColor; }

            public void SetWidth(int w) { Width = w; Place(); }

            private void Place()
            {
                _id.Location = new Point(6, 3);
                _name.Location = new Point(74, 5);
                int rx = Width - 3 * 28 - 8;
                _up.Location = new Point(rx, 2); _down.Location = new Point(rx + 28, 2); _rem.Location = new Point(rx + 56, 2);
                ApplyRowColor();
            }

            private static Button Icon(string t, bool danger = false)
            {
                var b = new Button { Text = t, Width = 26, Height = 22, Font = UiTheme.Ui };
                UiTheme.StyleIcon(b, danger);
                return b;
            }
        }
    }
}
