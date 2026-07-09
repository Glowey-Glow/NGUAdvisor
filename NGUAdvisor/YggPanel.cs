using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using NGUAdvisor.Managers;
using static NGUAdvisor.Main;

namespace NGUAdvisor
{
    // Systems > YGGDRASIL, Y1 "orchard grid" (user-approved): every unlocked fruit is a live tile —
    // name, tier bar filling toward harvest (fruits grow 1s/s, ~1h per tier), ETA to max — maxed
    // tiles glow gold, inactive fade. Header = MANAGED toggle + next-harvest status + Harvest Now
    // (gated by the Harvest Safety toggle, mirroring the legacy guard). Manual strip = Activate
    // Fruits / Swap Loadouts + tier threshold. Advisor line below reads the poop placement.
    public class YggPanel : Panel
    {
        // B1 game-mimic bar: fills over ONE TIER's hour and resets, fill color alternating per tier
        // (green/blue; gold when maxed) with the status text INSIDE. Text-in-fill is the two-layer
        // clipped-label trick: a dark base label spans the bar; the fill panel on top carries an
        // identical white label at the same coordinates, clipped to the fill width as it grows.
        private class Tile
        {
            public Panel Box;
            public Label Name;
            public Panel Dot;      // top-right poop marker: brown = advisor's best target, grey = current-but-suboptimal
            public Panel BarOuter;
            public Label TxtBase;
            public Panel Fill;
            public Label TxtFill;
        }

        private static readonly Color PoopBrown = Color.FromArgb(139, 94, 59);

        private class FruitInfo
        {
            public int Idx;
            public string Name;
            public bool Locked;
            public long UnlockCost;
            public bool Active;
            public bool Max;
            public int HTier;
            public double Frac;
            public double Eta;
            public bool Poop;
        }

        private static bool SafeFlag(Func<bool> get)
        {
            try { return get(); } catch { return false; }
        }

        // Both text layers get the same fitted string; the fill layer is revealed as the bar grows.
        // Elastic tiles (round-3): every width reads from the tile's own bar, no fixed 105px.
        private static void SetBar(Tile t, double frac, Color fill, string text, Color baseFg)
        {
            string fitted = Fit(text, UiTheme.Chip, t.TxtBase.Width - 4);
            t.TxtBase.Text = fitted;
            t.TxtBase.ForeColor = baseFg;
            t.TxtFill.Text = fitted;
            t.Fill.BackColor = fill;
            t.TxtFill.BackColor = fill;
            t.Fill.Width = (int)((t.BarOuter.Width - 2) * Math.Max(0, Math.Min(1, frac)));
        }

        private static string FmtSeeds(long n)
        {
            if (n >= 1000000) return $"{n / 1000000.0:0.#}M";
            if (n >= 1000) return $"{n / 1000.0:0.#}K";
            return n.ToString();
        }

        private const int MaxFruits = 21;
        private readonly int _w;
        private readonly int _tileW;
        // Orchard columns: bigger tiles (~165px pitch) so fruit names + the tier/ETA bar text read
        // clearly — ~6 across the full canvas, min 4 in a narrow cell.
        private int Cols => Math.Max(4, (_w - 14) / 165);
        private Button _managed;
        private Label _info;
        private Button _harvestNow;
        private Button _safety;
        private Button _refresh;
        private readonly List<Tile> _tiles = new List<Tile>();
        private Button _activate;
        private Button _swap;
        private Button _swapDig;
        private Button _swapBeard;
        private Label _tierLbl;
        private NumericUpDown _swapTier;
        private Label _advice;
        private bool _syncing;
        private bool _safetyOn;
        private static bool _dumpedFruits;   // one-time diagnostic: log every fruit's real in-game name + tier

        // canvasW: explicit canvas width when hosted in an M1 section column (0 = UiLayout.PanelW).
        public YggPanel(int canvasW = 0)
        {
            _w = canvasW > 0 ? canvasW : UiLayout.PanelW;
            Dock = DockStyle.Fill;
            BackColor = UiTheme.Ground;
            // Quad-cell hosting: late-game fruit counts outgrow the cell — scroll inside the panel
            // rather than clip (the section canvas keeps its own scroll for the whole quad).
            AutoScroll = true;

            _managed = MkBtn("MANAGED");
            _managed.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.ManageYggdrasil = !Settings.ManageYggdrasil;
                SyncFromSettings();
            };
            _info = new Label { Text = "…", AutoSize = false, Size = new Size(240, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _harvestNow = MkBtn("Harvest Now");
            UiTheme.StyleFlat(_harvestNow);
            _harvestNow.Click += (s, e) =>
            {
                if (!_safetyOn) { Log("Harvest Safety is off — flip it green first."); return; }
                if (!YggdrasilManager.AnyHarvestable()) { Log("Nothing harvestable yet."); return; }
                if (LockManager.TryYggdrasilSwap(true))
                    YggdrasilManager.HarvestAll(true);
                else
                    Log("Unable to harvest now");
                RefreshTiles();
            };
            _safety = MkBtn("Harvest Safety");
            _safety.Click += (s, e) => { _safetyOn = !_safetyOn; SyncFromSettings(); };
            _refresh = new Button { Text = "↻", Size = new Size(36, 24), Font = UiTheme.Ui };
            UiTheme.StyleFlat(_refresh);
            _refresh.Click += (s, e) => RefreshTiles();
            Controls.Add(_managed);
            Controls.Add(_info);
            Controls.Add(_harvestNow);
            Controls.Add(_safety);
            Controls.Add(_refresh);
            UiLayout.Row(10, 10, 8, _managed, _info, _harvestNow, _safety, _refresh);

            // Elastic tiles: width computed from the cell (scrollbar allowance included) so the
            // orchard fills whatever column hosts it — no fixed 117px pitch, no horizontal scroll.
            _tileW = ((_w - 20 - 17) - (Cols - 1) * 6) / Cols;
            for (int i = 0; i < MaxFruits; i++)
            {
                var t = new Tile();
                t.Box = new Panel { Size = new Size(_tileW, 62), BackColor = UiTheme.Surface, BorderStyle = BorderStyle.FixedSingle, Visible = false };
                t.Name = new Label { Text = "", AutoSize = false, Size = new Size(_tileW - 24, 18), Font = UiTheme.Bold, ForeColor = UiTheme.Accent, BackColor = UiTheme.Surface, Location = new Point(6, 4) };
                t.Dot = new Panel { Location = new Point(_tileW - 14, 6), Size = new Size(8, 8), BackColor = UiTheme.Surface, Visible = false };
                t.BarOuter = new Panel { Location = new Point(6, 26), Size = new Size(_tileW - 12, 30), BackColor = UiTheme.Zebra, BorderStyle = BorderStyle.FixedSingle };
                t.TxtBase = new Label { Text = "", AutoSize = false, Size = new Size(_tileW - 14, 28), Font = UiTheme.Ui, ForeColor = UiTheme.Ink, BackColor = UiTheme.Zebra, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(0, 0), Tag = "exclusive" };
                t.Fill = new Panel { Location = new Point(0, 0), Size = new Size(0, 28), BackColor = UiTheme.Cap, Tag = "exclusive" };
                t.TxtFill = new Label { Text = "", AutoSize = false, Size = new Size(_tileW - 14, 28), Font = UiTheme.Ui, ForeColor = Color.White, BackColor = UiTheme.Cap, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(0, 0) };
                t.Fill.Controls.Add(t.TxtFill);
                t.BarOuter.Controls.Add(t.TxtBase);
                t.BarOuter.Controls.Add(t.Fill);
                t.Fill.BringToFront();
                t.Box.Controls.Add(t.Name);
                t.Box.Controls.Add(t.Dot);
                t.Box.Controls.Add(t.BarOuter);
                Controls.Add(t.Box);
                _tiles.Add(t);
            }

            _activate = MkBtn("Activate Fruits");
            _activate.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.ActivateFruits = !Settings.ActivateFruits;
                SyncFromSettings();
            };
            _swap = MkBtn("Swap Loadouts");
            _swap.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.SwapYggdrasilLoadouts = !Settings.SwapYggdrasilLoadouts;
                SyncFromSettings();
            };
            _tierLbl = new Label { Text = "at tier", AutoSize = true, Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground };
            _swapTier = new NumericUpDown { Width = 48, Minimum = 1, Maximum = 20, Font = UiTheme.Ui };
            _swapTier.ValueChanged += (s, e) => { if (!_syncing && Settings != null) Settings.YggSwapThreshold = (int)_swapTier.Value; };
            Controls.Add(_activate);
            Controls.Add(_swap);
            Controls.Add(_tierLbl);
            Controls.Add(_swapTier);
            UiLayout.Row(10, 226, 8, _activate, _swap, _tierLbl, _swapTier);

            // Re-homed from the retired Old Yggdrasil page (Phase B): harvest-swap companions.
            _swapDig = MkBtn("Swap Diggers");
            _swapDig.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.SwapYggdrasilDiggers = !Settings.SwapYggdrasilDiggers;
                SyncFromSettings();
            };
            _swapBeard = MkBtn("Swap Beards");
            _swapBeard.Click += (s, e) =>
            {
                if (Settings == null) return;
                Settings.SwapYggdrasilBeards = !Settings.SwapYggdrasilBeards;
                SyncFromSettings();
            };
            Controls.Add(_swapDig);
            Controls.Add(_swapBeard);
            UiLayout.Row(10, 258, 8, _swapDig, _swapBeard);

            _advice = new Label { Text = "", AutoSize = false, Size = new Size(_w - 54, UiTheme.TextH), Font = UiTheme.Ui, ForeColor = UiTheme.Muted, BackColor = UiTheme.Ground, Location = new Point(10, 290) };
            Controls.Add(_advice);

            VisibleChanged += (s, e) => { if (Visible) RefreshTiles(); };
            SyncFromSettings();
        }

        private static Button MkBtn(string text)
        {
            var b = new Button { Text = text, Size = new Size(UiLayout.BtnWidth(text), 24), Font = UiTheme.Ui, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = UiTheme.Border;
            return b;
        }

        public void SyncFromSettings()
        {
            if (Settings == null) return;
            _syncing = true;
            try
            {
                UiTheme.ApplyState(_managed, Settings.ManageYggdrasil ? UiTheme.Cap : UiTheme.Danger, Color.White);
                _managed.Text = Settings.ManageYggdrasil ? "MANAGED" : "UNMANAGED";
                UiTheme.ApplyState(_safety, _safetyOn ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_activate, Settings.ActivateFruits ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_swap, Settings.SwapYggdrasilLoadouts ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_swapDig, Settings.SwapYggdrasilDiggers ? UiTheme.Cap : UiTheme.Danger, Color.White);
                UiTheme.ApplyState(_swapBeard, Settings.SwapYggdrasilBeards ? UiTheme.Cap : UiTheme.Danger, Color.White);
                int v = Math.Max(1, Math.Min(20, Settings.YggSwapThreshold));
                _swapTier.Value = v;
            }
            finally { _syncing = false; }
            RefreshTiles();
        }

        private static string ShortName(string full)
        {
            if (string.IsNullOrEmpty(full)) return "?";
            return full.StartsWith("Fruit of ", StringComparison.OrdinalIgnoreCase) ? full.Substring(9) : full;
        }

        private static string FmtEta(double s)
        {
            if (s <= 0) return "now";
            if (s >= 3600) return $"{s / 3600:0.#}h";
            return $"{s / 60:0}m";
        }

        // Mono blanks a fixed-size label whose text overflows — everything variable gets fitted.
        private static string Fit(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (UiLayout.MeasureText(text, font) <= width) return text;
            while (text.Length > 1 && UiLayout.MeasureText(text + "…", font) > width)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        // Poop priority (guide verbatim: "poop Pom ALWAYS, others at max"): Pomegranate first,
        // then macguffins > knowledge (EXP) > quirk > luck > gold > adventure.
        private static int PoopRank(string shortName)
        {
            string n = (shortName ?? "").ToLowerInvariant();
            if (n.Contains("pomegranate")) return 0;
            if (n.Contains("macguffin") && n.Contains("beta")) return 1;
            if (n.Contains("macguffin")) return 2;
            if (n.Contains("knowledge")) return 3;
            if (n.Contains("quirk")) return 4;
            if (n.Contains("luck")) return 5;
            if (n.Contains("gold")) return 6;
            if (n.Contains("adventure")) return 7;
            return 9;
        }

        private void RefreshTiles()
        {
            try
            {
                var c = Main.Character;
                if (c == null || Settings == null) return;
                var yc = c.yggdrasilController;
                var fruits = c.yggdrasil.fruits;
                if (yc == null || fruits == null) return;
                if (!_dumpedFruits)
                {
                    _dumpedFruits = true;
                    try { for (int i = 0; i < fruits.Count; i++) LogDebug($"FRUIT[{i}] '{yc.fruitName[i]}' maxTier={fruits[i].maxTier} active={fruits[i].activated} poop={fruits[i].usePoop}"); }
                    catch (Exception de) { LogDebug($"fruit dump: {de.Message}"); }
                }
                var fc = yc.fruits[0];
                float thr = fc.tierThreshold();

                // Pass 1: collect visible fruits. maxTier==0 means NOT YET BOUGHT, not irrelevant —
                // the unlock IS the first seed purchase (game increments maxTier from 0). Those show
                // as LOCKED tiles with their cost. Content-gated fruits (troll-only id 8, ITOPOD 9,
                // titan achievement 10, beast 14, cards 15-20) stay hidden until their gate opens.
                var infos = new List<FruitInfo>();
                for (int i = 0; i < fruits.Count && infos.Count < _tiles.Count; i++)
                {
                    var f = fruits[i];
                    bool locked = f.maxTier == 0;
                    if (locked)
                    {
                        // Near-term reveal (user request): show every locked fruit as a placeholder tile
                        // with its real in-game name so the orchard reads complete — but keep the deep
                        // card-gated "Mayo" fruits (index 15+) hidden until the Cards feature is on, so
                        // they don't crowd the early-game orchard.
                        bool gateOpen = i < 15 || SafeFlag(() => c.cards.cardsOn);
                        if (!gateOpen) continue;
                    }
                    var fi = new FruitInfo { Idx = i, Locked = locked, Poop = f.usePoop, Active = f.activated };
                    try { fi.Name = ShortName(yc.fruitName[i]); } catch { fi.Name = "?"; }
                    if (locked)
                    {
                        try { fi.UnlockCost = yc.baseSeedCost[i]; } catch { }
                    }
                    else
                    {
                        try { fi.HTier = fc.harvestTier(i); fi.Max = fc.fruitMaxxed(i); } catch { }
                        // B1 bar: progress through the CURRENT tier's hour, ETA to the NEXT tier.
                        double into = thr > 0 ? f.seconds % thr : 0;
                        fi.Frac = thr > 0 ? into / thr : 0;
                        fi.Eta = Math.Max(0, thr - into);
                    }
                    infos.Add(fi);
                }

                int poopCount = infos.Count(x => x.Poop);
                var best = infos.Where(x => x.Active && !x.Locked)
                    .OrderBy(x => PoopRank(x.Name))
                    .ThenByDescending(x => fruits[x.Idx].maxTier)
                    .Take(Math.Max(3, poopCount))
                    .Select(x => x.Idx)
                    .ToList();

                long seeds = 0;
                try { seeds = c.yggdrasil.seeds; } catch { }

                int shown = 0, maxed = 0;
                foreach (var fi in infos)
                {
                    var f = fruits[fi.Idx];
                    var t = _tiles[shown];
                    int col = shown % Cols, row = shown / Cols;
                    t.Box.Location = new Point(10 + col * (_tileW + 6), 44 + row * 68);
                    t.Box.Visible = true;
                    shown++;

                    t.Name.Text = Fit(fi.Name, UiTheme.Chip, t.Name.Width - 2);

                    // Brown dot = advisor's best poop target; grey = poop is here but a better fruit exists.
                    bool recommended = best.Contains(fi.Idx);
                    t.Dot.Visible = recommended || fi.Poop;
                    t.Dot.BackColor = recommended ? PoopBrown : UiTheme.Faint;

                    var bg = UiTheme.Surface;
                    if (fi.Locked)
                    {
                        bool affordable = fi.UnlockCost > 0 && seeds >= fi.UnlockCost;
                        t.Name.ForeColor = UiTheme.Faint;
                        SetBar(t, 0, UiTheme.Cap,
                            fi.UnlockCost > 0 ? $"UNLOCK: {FmtSeeds(fi.UnlockCost)} SEEDS" : "LOCKED",
                            affordable ? UiTheme.Cap : UiTheme.Faint);
                    }
                    else if (!fi.Active)
                    {
                        t.Name.ForeColor = UiTheme.Faint;
                        SetBar(t, 0, UiTheme.Cap, "INACTIVE", UiTheme.Faint);
                    }
                    else if (fi.Max)
                    {
                        maxed++;
                        bg = Color.FromArgb(253, 246, 233);
                        t.Name.ForeColor = UiTheme.Energy;
                        SetBar(t, 1, UiTheme.Energy, $"T{fi.HTier}/{f.maxTier} · MAXED", UiTheme.Ink);
                    }
                    else
                    {
                        t.Name.ForeColor = UiTheme.Accent;
                        // Alternating fill per tier (the game's "reset = progress" signal).
                        var fill = fi.HTier % 2 == 0 ? UiTheme.Cap : UiTheme.Accent;
                        SetBar(t, fi.Frac, fill, $"T{fi.HTier}/{f.maxTier} · {FmtEta(fi.Eta)}", UiTheme.Ink);
                    }
                    t.Box.BackColor = bg;
                    t.Name.BackColor = bg;
                }
                for (int i = shown; i < _tiles.Count; i++) _tiles[i].Box.Visible = false;

                // Reflow the strip + swap row + advice under however many rows the orchard used
                // (user-caught overlap: the Phase B swap row wasn't part of this reflow, so the
                // advice line rendered underneath the new buttons).
                int rows = (shown + Cols - 1) / Cols;
                int stripY = 44 + rows * 68 + 8;
                _activate.Top = _swap.Top = _swapTier.Top = stripY;
                _tierLbl.Top = stripY + 5;
                _swapDig.Top = _swapBeard.Top = stripY + 32;
                _advice.Top = stripY + 64;

                _info.Text = Fit($"{(maxed > 0 ? $"NEXT HARVEST: {maxed} maxed" : "NEXT HARVEST: none maxed yet")} · SEEDS {seeds}", UiTheme.Ui, 238);

                // Advisor line: current placement vs the brown-dot recommendation (+ affordable unlocks).
                var curNames = infos.Where(x => x.Poop).Select(x => x.Name).ToList();
                var bestNames = infos.Where(x => best.Contains(x.Idx)).Select(x => x.Name).ToList();
                string advice;
                if (curNames.Count == 0)
                    advice = $"Advisor: poop unassigned — best targets (brown dots): {string.Join(", ", bestNames.ToArray())}.";
                else if (curNames.All(n => bestNames.Contains(n)))
                    advice = $"Advisor: poop on {string.Join(", ", curNames.ToArray())} — matches the best targets.";
                else
                {
                    var better = bestNames.Where(n => !curNames.Contains(n)).ToList();
                    advice = $"Advisor: poop on {string.Join(", ", curNames.ToArray())} — better: {string.Join(", ", better.ToArray())} (brown dots).";
                }
                var buy = infos.Where(x => x.Locked && x.UnlockCost > 0 && seeds >= x.UnlockCost)
                    .OrderBy(x => x.UnlockCost).FirstOrDefault();
                if (buy != null)
                    advice += $" · Can unlock {buy.Name} ({buy.UnlockCost} seeds).";
                UiLayout.FitOrGrow(_advice, advice);   // last element on the panel — free to wrap
            }
            catch (Exception ex) { LogDebug($"Ygg panel: {ex.Message}"); }
        }
    }
}
