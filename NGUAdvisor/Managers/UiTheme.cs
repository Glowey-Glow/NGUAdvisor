using System.Drawing;
using System.Windows.Forms;

namespace NGUAdvisor.Managers
{
    // Shared design tokens for the advisor's WinForms UI (editor windows + a runtime theme pass on the
    // main settings form). Mirrors the approved mockup. Everything here is Mono-WinForms-safe: flat colors,
    // borders, the system Segoe UI font - no gradients/rounded corners.
    public static class UiTheme
    {
        // Light theme (original).
        public static readonly Color Ground = Hex("EEF0F3");
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Hex("C6CBD3");
        public static readonly Color BorderStrong = Hex("AEB4BF");
        public static readonly Color Ink = Hex("20242E");
        public static readonly Color Muted = Hex("6A7180");
        public static readonly Color Faint = Hex("9AA1AD");
        public static readonly Color Accent = Hex("3B5BA5");
        public static readonly Color AccentDark = Hex("2F4C8C");
        public static readonly Color AccentWeak = Hex("E4E9F4");
        public static readonly Color Cap = Hex("2F7A55");
        public static readonly Color CapBg = Hex("E6F1EA");
        public static readonly Color Danger = Hex("B23B3B");
        public static readonly Color Zebra = Hex("F7F8FA");
        public static readonly Color BtnFace = Hex("F5F6F8");

        // Per-system identity colors.
        public static readonly Color Energy = Hex("C0851F");
        public static readonly Color Magic = Hex("5B57A6");
        public static readonly Color R3 = Hex("2E8B8B");
        public static readonly Color Diggers = Hex("6E8B3D");
        public static readonly Color Beards = Hex("A0623A");
        public static readonly Color Gear = Hex("4A6D8C");
        public static readonly Color Wandoos = Hex("8C5A9E");
        public static readonly Color NGUDiff = Hex("B5504A");

        public static readonly Font Ui = new Font("Segoe UI", 9f);
        public static readonly Font Bold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font ColHeader = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        public static readonly Font Chip = new Font("Segoe UI", 7.5f, FontStyle.Bold);

        // DPI-TRUE line metrics (root cause of every stacked-text overlap): the game's Mono renders
        // our 9pt text ~25px tall and 7.5pt headers ~22px (observed rendered AutoSize heights — the
        // 96-DPI guess of 15-16px was WRONG). All stacked text uses these pitches.
        public const int LinePitch = 26;    // between stacked 9pt lines
        public const int HeadPitch = 24;    // section header -> first content line
        public const int TextH = 22;        // min height for a fixed-size single-line 9pt label

        private static Color Hex(string h) => ColorTranslator.FromHtml("#" + h);

        // Wide-layout chokepoint fix: legacy resx layouts use ABSOLUTE table columns, so a wider
        // window just grows dead space on the right. Converting Absolute column styles to Percent
        // (weighted by their designed width — TLP normalizes the sum) makes the grid redistribute,
        // and anchoring the large content controls lets them actually fill their cells. Only called
        // when WideLayout is on; narrow mode keeps the original layouts byte-for-byte.
        public static void MakeStretchy(System.Windows.Forms.Control root)
        {
            if (root is System.Windows.Forms.TableLayoutPanel tlp)
            {
                // The legacy grids are almost entirely AUTOSIZE columns (size-to-content, never
                // stretch) — convert every non-Percent column to Percent, weighted by its CURRENT
                // rendered width so the existing proportions carry over and only the surplus space
                // redistributes. Must run after layout (Shown), when GetColumnWidths is real.
                int[] widths = null;
                try { widths = tlp.GetColumnWidths(); } catch { }
                for (int i = 0; i < tlp.ColumnStyles.Count; i++)
                {
                    var cs = tlp.ColumnStyles[i];
                    if (cs.SizeType != System.Windows.Forms.SizeType.Percent)
                    {
                        float w = widths != null && i < widths.Length && widths[i] > 0
                            ? widths[i]
                            : System.Math.Max(cs.Width, 30f);
                        cs.SizeType = System.Windows.Forms.SizeType.Percent;
                        cs.Width = w;
                    }
                }
                foreach (System.Windows.Forms.Control c in tlp.Controls)
                {
                    if (c is System.Windows.Forms.ListBox || c is System.Windows.Forms.ListView ||
                        c is System.Windows.Forms.TextBox || c is System.Windows.Forms.GroupBox ||
                        c is System.Windows.Forms.FlowLayoutPanel || c is System.Windows.Forms.Panel)
                    {
                        c.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom |
                                   System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
                    }
                }
            }
            foreach (System.Windows.Forms.Control ch in root.Controls)
                MakeStretchy(ch);
        }

        private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
        private static Color Shift(Color c, int d) => Color.FromArgb(Clamp(c.R + d), Clamp(c.G + d), Clamp(c.B + d));

        // Set EXPLICIT hover/press colors. Mono's default FlatAppearance.MouseOverBackColor (Color.Empty)
        // renders a light-grey hover that never clears on mouse-leave (sticks until reload); giving it a
        // concrete value avoids that buggy path. Hover = slightly lighter, press = slightly darker.
        private static void Hover(Button b)
        {
            b.FlatAppearance.MouseOverBackColor = Shift(b.BackColor, 14);
            b.FlatAppearance.MouseDownBackColor = Shift(b.BackColor, -10);
        }

        // State styling for toggle/segment buttons: sets colors AND the explicit hover/pressed colors
        // derived from the new background. Without this, Mono's default hover tint STICKS after the
        // mouse leaves (user-reported on the sub-tab bars) — every dynamic BackColor change must go
        // through here.
        public static void ApplyState(Button b, Color bg, Color fg)
        {
            b.BackColor = bg;
            b.ForeColor = fg;
            Hover(b);
        }

        public static void StylePrimary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = AccentDark;
            b.Font = Bold;
            b.UseVisualStyleBackColor = false;
            Hover(b);
        }

        public static void StyleFlat(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = BtnFace;
            b.ForeColor = Ink;
            b.FlatAppearance.BorderColor = BorderStrong;
            b.UseVisualStyleBackColor = false;
            Hover(b);
        }

        // Small icon/ghost button (reorder, remove, add).
        public static void StyleIcon(Button b, bool danger = false)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Surface;
            b.ForeColor = danger ? Danger : Muted;
            b.FlatAppearance.BorderColor = Border;
            b.UseVisualStyleBackColor = false;
            Hover(b);
        }

        public static void StyleGhost(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Surface;
            b.ForeColor = Accent;
            b.FlatAppearance.BorderColor = AccentDark;
            b.UseVisualStyleBackColor = false;
            Hover(b);
        }

        // Recolor ONLY the input controls (combo/text/numeric/list) of a form we built with UiTheme tokens,
        // so they don't stay light islands in dark mode. Leaves panels/accent strips/labels alone (they are
        // already themed at construction). Use on the code-built editor windows.
        public static void ThemeInputs(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is ComboBox || c is TextBoxBase || c is NumericUpDown || c is ListBox || c is ListView)
                {
                    c.BackColor = Surface;
                    c.ForeColor = Ink;
                }
                ThemeInputs(c);
            }
        }

        // Recolor an existing form (e.g. the localized settings form) to this palette WITHOUT touching
        // fonts or layout - color changes can't clip text or shift controls. Safe to run once after the
        // form is built. Containers take the ground tone; inputs take Surface; buttons go flat.
        public static void ApplyTo(Control root)
        {
            if (root is Form f) { f.BackColor = Ground; f.ForeColor = Ink; }
            foreach (Control c in root.Controls)
                Theme(c);
        }

        private static void Theme(Control c)
        {
            bool recurse = true;

            if (c is Button b)
            {
                StyleFlat(b);
                recurse = false;
            }
            else if (c is ComboBox || c is TextBoxBase || c is NumericUpDown)
            {
                c.BackColor = Surface; c.ForeColor = Ink; recurse = false;
            }
            else if (c is ListBox || c is ListView || c is DataGridView || c is TreeView)
            {
                c.BackColor = Surface; c.ForeColor = Ink; recurse = false;
            }
            else if (c is Label || c is CheckBox || c is RadioButton || c is LinkLabel)
            {
                c.BackColor = Color.Transparent; c.ForeColor = Ink; recurse = false;
            }
            else if (c is TabControl)
            {
                c.ForeColor = Ink; // leave the strip to the OS; recurse to theme the pages
            }
            else
            {
                // Panels, GroupBox, TableLayoutPanel, FlowLayoutPanel, TabPage, SplitContainer, etc.
                c.BackColor = Ground; c.ForeColor = Ink;
            }

            if (recurse)
                foreach (Control child in c.Controls)
                    Theme(child);
        }

        // Owner-draw a TabControl's tabs so the SELECTED tab is obvious in dark mode (the OS renders the tab
        // strip dark-on-dark otherwise). Selected = accent fill + white bold; others = surface + muted.
        public static void OwnerDrawTabs(TabControl tc)
        {
            try
            {
                tc.DrawMode = TabDrawMode.OwnerDrawFixed;
                tc.DrawItem -= TabDraw;
                tc.DrawItem += TabDraw;
            }
            catch { }
        }

        private static void TabDraw(object sender, DrawItemEventArgs e)
        {
            var tc = (TabControl)sender;
            if (e.Index < 0 || e.Index >= tc.TabPages.Count) return;
            bool selected = e.Index == tc.SelectedIndex;
            var g = e.Graphics;
            using (var bg = new SolidBrush(selected ? Accent : Surface))
                g.FillRectangle(bg, e.Bounds);
            var rect = e.Bounds; rect.Y += 2;
            TextRenderer.DrawText(g, tc.TabPages[e.Index].Text, selected ? Bold : Ui, rect,
                selected ? Color.White : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
