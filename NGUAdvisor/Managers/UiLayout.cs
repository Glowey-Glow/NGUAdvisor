using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NGUAdvisor.Managers
{
    // Layout engine + runtime overlap auditor (added after repeated hand-math overlap bugs; the
    // pre-flight is now ENFORCED, not a habit).
    //
    //  - Row(): places controls left-to-right from measured sizes — sibling overlap in a row is
    //    impossible by construction. Returns the y below the row.
    //  - Audit(): recursively checks every visible-or-not control tree for (a) intersecting sibling
    //    bounds, (b) fixed-size Labels/Buttons whose text measures wider than the control. Violations
    //    go to the log as "UI AUDIT" lines — after any UI deploy, the log must show zero of them.
    //    Panels tagged "exclusive" (alternate views sharing one area) are exempt from pairwise checks.
    public static class UiLayout
    {
        // Design canvas width for the custom panels (the old hardcoded ~664px assumption). Set ONCE
        // in the SettingsForm ctor BEFORE the panels are constructed: 920 when WideLayout, else the
        // legacy 664. Panels derive every full-width surface and column grid from this — including
        // content they rebuild at runtime — so the whole tab tracks the window width consistently.
        // (Not read from ClientSize: ctor-time reads are stale under Mono; Shown re-asserts 940.)
        public static int PanelW = 664;

        // MEASURE WITH THE RENDERER (round-3 root cause): Labels/Buttons paint via GDI
        // (TextRenderer); GDI+ Graphics.MeasureString reads NARROWER than what actually paints,
        // which cut strings mid-word with no ellipsis across the app. One engine for both.
        public static int MeasureText(string text, Font font)
            => TextRenderer.MeasureText(text ?? "", font).Width;

        public static int BtnWidth(string text) => Math.Max(42, MeasureText(text, UiTheme.Ui) + 22);

        // Shared measured-ellipsis fit (the Mono blank-label law: a fixed label with overflowing
        // text renders NOTHING — every variable string goes through a Fit).
        public static string FitText(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (MeasureText(text, font) <= width) return text;
            while (text.Length > 1 && MeasureText(text + "…", font) > width)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        // NO-ELLIPSIS rule (user): measure first — if the text fits, single line; if not, the label
        // GROWS vertically and the text word-wraps up to maxLines. Only past maxLines does the last
        // line ellipsize (and anything that long also lands in LOGS in full). Returns the label's
        // new bottom edge so callers can reflow the rows below it.
        public static int FitOrGrow(Label l, string text, int maxLines = 2)
        {
            text = text ?? "";
            int width = l.Width - 4;
            if (MeasureText(text, l.Font) <= width)
            {
                l.Height = UiTheme.TextH;
                l.Text = text;
                return l.Bottom;
            }
            var lines = WrapLines(text, l.Font, width, maxLines);
            l.Height = lines.Count * UiTheme.LinePitch - 4;
            l.Text = string.Join("\n", lines.ToArray());
            return l.Bottom;
        }

        // Wrap into a fixed-height multi-line label (chip sub-captions): the label keeps its
        // pre-reserved two-line height, only the text wraps.
        public static string WrapText(string text, Font font, int width, int maxLines)
            => string.Join("\n", WrapLines(text ?? "", font, width, maxLines).ToArray());

        private static List<string> WrapLines(string text, Font font, int width, int maxLines)
        {
            var lines = new List<string>();
            var words = text.Split(' ');
            string cur = "";
            for (int i = 0; i < words.Length; i++)
            {
                string cand = cur.Length == 0 ? words[i] : cur + " " + words[i];
                if (cur.Length == 0 || MeasureText(cand, font) <= width)
                {
                    cur = cand;
                    continue;
                }
                lines.Add(cur);
                cur = words[i];
                if (lines.Count == maxLines - 1)
                {
                    // Last permitted line takes the whole remainder, ellipsized only if needed.
                    var rest = new System.Text.StringBuilder(cur);
                    for (int j = i + 1; j < words.Length; j++) rest.Append(' ').Append(words[j]);
                    lines.Add(FitText(rest.ToString(), font, width));
                    return lines;
                }
            }
            if (cur.Length > 0) lines.Add(cur);
            return lines;
        }

        // Place controls left-to-right starting at (x, y), gap px apart. Sizes must be set BEFORE the
        // call (buttons via BtnWidth; AutoSize labels are measured directly). Returns bottom edge.
        public static int Row(int x, int y, int gap, params Control[] controls)
        {
            int cx = x, maxH = 0;
            foreach (var c in controls)
            {
                if (c == null) continue;
                int w = c.AutoSize && c is Label lb ? MeasureText(lb.Text, lb.Font) : c.Width;
                // Vertically center small controls on the row's first control baseline.
                c.Location = new Point(cx, y + (c is Label ? 4 : 0));
                cx += w + gap;
                maxH = Math.Max(maxH, c.Height + (c is Label ? 4 : 0));
            }
            return y + Math.Max(maxH, 24);
        }

        // Left-to-right with wrapping at maxRight (chip strips): returns the y below the last row.
        public static int WrapRow(int x, int y, int gap, int maxRight, int rowPitch, IEnumerable<Control> controls)
        {
            int cx = x, cy = y;
            foreach (var c in controls)
            {
                if (c == null) continue;
                if (cx + c.Width > maxRight && cx > x)
                {
                    cx = x;
                    cy += rowPitch;
                }
                c.Location = new Point(cx, cy);
                cx += c.Width + gap;
            }
            return cy + rowPitch;
        }

        public static void Audit(Control root, string context)
        {
            try
            {
                int issues = AuditNode(root, context);
                Main.LogDebug(issues == 0
                    ? $"UI AUDIT [{context}]: clean"
                    : $"UI AUDIT [{context}]: {issues} ISSUE(S) — see lines above");
            }
            catch (Exception e) { Main.LogDebug($"UI AUDIT [{context}] failed: {e.Message}"); }
        }

        private static int AuditNode(Control node, string context)
        {
            int issues = 0;
            var kids = node.Controls.Cast<Control>().ToList();

            for (int i = 0; i < kids.Count; i++)
            {
                var a = kids[i];
                // Hidden controls do not paint — they cannot visually overlap. Hidden PAGES get their
                // own audit when selected (AuditOnce from the view switchers).
                if (!a.Visible) continue;

                for (int j = i + 1; j < kids.Count; j++)
                {
                    var b = kids[j];
                    if (!b.Visible) continue;
                    if (Equals(a.Tag, "exclusive") && Equals(b.Tag, "exclusive")) continue;
                    // 1px tolerance: control chrome may touch; real glyph collisions must flag.
                    var ra = EffectiveBounds(a);
                    var rb = EffectiveBounds(b);
                    ra.Inflate(-1, -1);
                    rb.Inflate(-1, -1);
                    if (ra.Width > 0 && ra.Height > 0 && rb.Width > 0 && rb.Height > 0 && ra.IntersectsWith(rb))
                    {
                        Main.LogDebug($"UI AUDIT [{context}]: OVERLAP '{Desc(a)}' {EffectiveBounds(a)} x '{Desc(b)}' {EffectiveBounds(b)}");
                        issues++;
                    }
                }

                // Content must stay inside its parent's client area (an AutoSize label past the edge
                // CLIPS silently — the Adventure footer bug).
                var eb = EffectiveBounds(a);
                if (node.ClientSize.Width > 0 && eb.Right > node.ClientSize.Width && !(node is Form))
                {
                    Main.LogDebug($"UI AUDIT [{context}]: PAST PARENT EDGE '{Desc(a)}' right={eb.Right} parent={node.ClientSize.Width}");
                    issues++;
                }

                issues += TextFit(a, context);
                issues += AuditNode(a, context);
            }
            return issues;
        }

        // DPI truth (learned from live audits): the game's Mono renders 9pt text ~25px tall — the
        // RENDERED AutoSize height is the real one; Font.Height (96-DPI based, ~15px) UNDERSTATES it.
        // Glyphs occupy roughly the rendered height minus ~4px of box padding.
        private static Rectangle EffectiveBounds(Control c)
        {
            int w = c.Width, h = c.Height;
            if (c is Label l && l.AutoSize)
            {
                w = Math.Max(w, MeasureText(l.Text, l.Font));
                h = Math.Max(l.Font.Height, h - 4);
            }
            else if (c is CheckBox cb && cb.AutoSize)
            {
                w = Math.Max(w, MeasureText(cb.Text, cb.Font) + 20);
                h = Math.Max(cb.Font.Height, h - 4);
            }
            return new Rectangle(c.Left, c.Top, w, h);
        }

        // Once-per-context audit for lazily shown views (called from page/segment switchers).
        private static readonly HashSet<string> _audited = new HashSet<string>();

        public static void AuditOnce(Control root, string context)
        {
            if (root == null || !_audited.Add(context)) return;
            Audit(root, context);
        }

        private static int TextFit(Control c, string context)
        {
            string text = c.Text;
            if (string.IsNullOrEmpty(text) || text.Contains("…")) return 0;
            int needed = -1;
            if (c is Button btn) needed = MeasureText(text, btn.Font) + 14;
            else if (c is Label l && !l.AutoSize) needed = MeasureText(text, l.Font);
            if (needed > 0 && needed > c.Width)
            {
                Main.LogDebug($"UI AUDIT [{context}]: TEXT CLIPPED '{Desc(c)}' needs {needed}px has {c.Width}px");
                return 1;
            }
            // Vertical clip: fixed-height single-line labels need >= UiTheme.TextH under this Mono's
            // ~25px 9pt rendering (16px boxes cut descenders).
            if (c is Label fl && !fl.AutoSize && fl.Height < UiTheme.TextH && fl.Font.Size >= 8.5f)
            {
                Main.LogDebug($"UI AUDIT [{context}]: TEXT MAY CLIP VERTICALLY '{Desc(c)}' h={fl.Height} < {UiTheme.TextH}");
                return 1;
            }
            return 0;
        }

        private static string Desc(Control c)
        {
            var t = c.GetType().Name;
            var txt = c.Text;
            if (!string.IsNullOrEmpty(txt) && txt.Length > 24) txt = txt.Substring(0, 24) + "…";
            return string.IsNullOrEmpty(txt) ? t : $"{t}:{txt}";
        }
    }
}
