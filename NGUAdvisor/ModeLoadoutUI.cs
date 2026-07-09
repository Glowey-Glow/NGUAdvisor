using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;

namespace NGUAdvisor
{
    // Route C3 phase 3.2 (in-tab). Transforms a mode's manual loadout section (Titan / Gold / Quest /
    // Yggdrasil / Cooking) IN the main settings form into the gear optimizer: a "GEAR SOURCE" objective
    // picker (paramount) + a "keep best Respawn" toggle, and REUSES the section's existing ListBox - right
    // where the static loadout was shown - as a live, read-only PREVIEW of the exact items the optimizer
    // will equip for that mode. Picking "Static loadout" previews the saved fallback IDs instead.
    //
    // Operates only within the mode's own inner TableLayoutPanel (uniform layout: row0 label, row1
    // list+remove, row2 addNumeric+add) so it never touches the locked-down outer resx layout.
    public class ModeLoadoutUI
    {
        private const string StaticText = "Static loadout (fixed IDs)";
        private const string OptPrefix = "Optimize: ";

        private readonly ListBox _preview;
        private readonly ComboBox _combo;
        private readonly CheckBox _respawn;
        private readonly Func<string> _getObj;
        private readonly Action<string> _setObj;
        private readonly Func<bool> _getResp;
        private readonly Action<bool> _setResp;
        private readonly Func<int[]> _getStatic;
        private bool _loading;

        private ModeLoadoutUI(ListBox preview, ComboBox combo, CheckBox respawn,
            Func<string> getObj, Action<string> setObj, Func<bool> getResp, Action<bool> setResp, Func<int[]> getStatic)
        {
            _preview = preview; _combo = combo; _respawn = respawn;
            _getObj = getObj; _setObj = setObj; _getResp = getResp; _setResp = setResp; _getStatic = getStatic;
        }

        // Transform one section. Returns the installed instance (for later Sync), or null if anything about
        // the section's layout was unexpected (guarded so one bad section can't break the whole form).
        public static ModeLoadoutUI Install(
            TableLayoutPanel tlp, ListBox list, Control label, Control[] editingControls,
            Func<string> getObj, Action<string> setObj, Func<bool> getResp, Action<bool> setResp, Func<int[]> getStatic)
        {
            try
            {
                tlp.SuspendLayout();

                // Free the cells used by the manual editor: remove label + add/remove/numeric from the TLP.
                if (label != null) { tlp.Controls.Remove(label); label.Visible = false; }
                foreach (var c in editingControls)
                    if (c != null) { tlp.Controls.Remove(c); c.Visible = false; }

                // The original loadout ListBox is DataSource-bound by its ItemControlGroup (which still fires
                // on settings load), so it can't be populated via .Items and must keep SelectionMode != None.
                // Orphan it off-screen (harmless) and drop our own preview ListBox into its cell (0,1).
                tlp.Controls.Remove(list);
                list.Visible = false;

                var preview = new ListBox
                {
                    Dock = DockStyle.Fill,
                    IntegralHeight = false,
                    TabStop = false,
                    Font = UiTheme.Ui,
                    BackColor = UiTheme.Surface,
                    ForeColor = UiTheme.Ink,
                    Margin = new Padding(3)
                };
                tlp.Controls.Add(preview, 0, 1);
                tlp.SetColumnSpan(preview, 2);

                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                    Margin = new Padding(3, 3, 3, 3),
                    Font = UiTheme.Ui
                };
                combo.Items.Add(StaticText);
                foreach (var o in GearObjectives.Objectives) combo.Items.Add(OptPrefix + o.Name);

                var refresh = new Button
                {
                    Text = "↻",
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    Width = 36,
                    Height = 22,
                    Margin = new Padding(1, 3, 1, 1),
                    Font = UiTheme.Ui
                };
                UiTheme.StyleFlat(refresh);

                var respawn = new CheckBox
                {
                    Text = "Keep the single best Respawn item",
                    Anchor = AnchorStyles.Left | AnchorStyles.Top,
                    AutoSize = true,
                    Margin = new Padding(3, 3, 3, 3),
                    Font = UiTheme.Ui
                };

                tlp.Controls.Add(combo, 0, 0);
                tlp.Controls.Add(refresh, 1, 0);
                tlp.Controls.Add(respawn, 0, 2);
                tlp.SetColumnSpan(respawn, 2);

                tlp.ResumeLayout();

                var ui = new ModeLoadoutUI(preview, combo, respawn, getObj, setObj, getResp, setResp, getStatic);
                combo.SelectedIndexChanged += ui.ComboChanged;
                respawn.CheckedChanged += ui.RespawnChanged;
                refresh.Click += (s, e) => ui.RefreshPreview();
                ui.SyncState();
                ui.RefreshPreview();
                return ui;
            }
            catch (Exception e)
            {
                Main.LogDebug($"ModeLoadout install failed: {e.Message}");
                return null;
            }
        }

        // Re-read the objective + respawn state from settings (e.g. after a settings reload). Cheap: does
        // NOT run the optimizer, so it is safe to call on every settings save. Call RefreshPreview to update
        // the displayed gear (that runs the optimizer).
        public void SyncState()
        {
            try
            {
                _loading = true;
                var obj = _getObj() ?? "";
                int idx = 0;
                if (obj.Length > 0)
                {
                    for (int i = 0; i < GearObjectives.Objectives.Count; i++)
                        if (string.Equals(GearObjectives.Objectives[i].Name, obj, StringComparison.OrdinalIgnoreCase))
                        { idx = i + 1; break; }
                    if (idx == 0) { _combo.Items.Add(OptPrefix + obj); idx = _combo.Items.Count - 1; }
                }
                _combo.SelectedIndex = idx;
                _respawn.Checked = _getResp();
                _respawn.Enabled = idx > 0;
            }
            catch (Exception e) { Main.LogDebug($"ModeLoadout sync failed: {e.Message}"); }
            finally { _loading = false; }
        }

        private void ComboChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            string name;
            if (_combo.SelectedIndex <= 0) name = "";
            else
            {
                var txt = _combo.SelectedItem.ToString();
                name = txt.StartsWith(OptPrefix) ? txt.Substring(OptPrefix.Length) : txt;
            }
            _setObj(name);
            _respawn.Enabled = _combo.SelectedIndex > 0;
            RefreshPreview();
        }

        private void RespawnChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            _setResp(_respawn.Checked);
            RefreshPreview();
        }

        // Populate the preview ListBox with the exact items that will be equipped: the optimizer's result
        // for the chosen objective, or the static fallback IDs when on "Static loadout".
        public void RefreshPreview()
        {
            try
            {
                _preview.BeginUpdate();
                _preview.Items.Clear();

                var obj = _getObj() ?? "";
                int[] ids;

                if (obj.Length == 0)
                {
                    ids = _getStatic() ?? new int[0];
                    if (ids.Length == 0) { _preview.Items.Add("(no static loadout set)"); return; }
                }
                else
                {
                    if (Main.Character == null || Main.InventoryController == null)
                    { _preview.Items.Add("(preview updates in-game)"); return; }

                    var o = GearOptimizer.FindObjective(obj);
                    if (o == null) { _preview.Items.Add("(unknown objective: " + obj + ")"); return; }

                    try { ids = GearOptimizer.OptimizeIds(o, _getResp()); }
                    catch (Exception ex) { _preview.Items.Add("(preview unavailable)"); Main.LogDebug($"Mode preview optimize failed: {ex.Message}"); return; }

                    if (ids.Length == 0) { _preview.Items.Add("(no gear found)"); return; }
                }

                foreach (var id in ids.Where(x => x > 0))
                {
                    var name = Main.ItemName(id);
                    _preview.Items.Add(string.IsNullOrEmpty(name) ? id.ToString() : $"{name}  (#{id})");
                }
            }
            catch (Exception e) { Main.LogDebug($"ModeLoadout preview failed: {e.Message}"); }
            finally { try { _preview.EndUpdate(); } catch { } }
        }
    }
}
