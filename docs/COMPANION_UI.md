# Companion UI — design & spacing rules

The companion (`NGUAdvisorCompanion/wwwroot/index.html`) is one self-contained page.
These are the rules every addition MUST follow so the UI stays consistent. The theme is
the dark "Arcade · Current Era" token set already defined in `:root` — never hard-code a
colour; use the `--*` tokens (and their light-theme overrides).

## Spacing scale (use these; don't invent one-off margins)

`--sp-1: 4px · --sp-2: 8px · --sp-3: 12px · --sp-4: 16px · --sp-5: 22px`

The scale governs **layout spacing between blocks** — margins and flex/grid `gap`. A
control's own internal padding (e.g. a chip's `5px 10px`, an input's `7px 9px`) is a
component detail and doesn't have to land on the scale; just keep it consistent per
component. When you do need a layout margin/gap, use a `--sp-*` token, not a raw px.

- **Sections are spaced by `.seclabel`, nothing else.** `.seclabel` already carries
  `margin: var(--sp-5) 2px var(--sp-3)` (22px above / 12px below). NEVER add an inline
  `margin-top` to a `.seclabel` or to the block right after it — that double-spaces or
  fights the rhythm. A section = one `.seclabel` followed by its content.
- Content blocks inside a section (chips, tiles, field grids, hint rows) get their gap
  from their own `gap`, not from top margins.
- Vertical gap between sibling controls: `--sp-3` (grids/switch rows already use ~11–12px).

## Components

- **Chip** (`.chip`): a compact status/label pill. Padding `5px 10px`, `.chips` container
  is `display:flex; gap:var(--sp-2); flex-wrap:wrap` (row AND column gap). One fact per
  chip. Inside a chip: `<b>` for the primary value, `.sub` (faint, `margin-left:6px`) for
  a secondary. State colours via `.chip.act/.max/.buy/.lock` (ok / accent / warn / faint).
  Never let chips touch — the container `gap` is mandatory.
- **Tile** (`.tilegrid` + `.tile-…`): for a grid of richer items (e.g. the Yggdrasil
  orchard). `display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr));
  gap:var(--sp-2)`. A tile has a name row and a **fill bar** whose width encodes progress
  and whose colour encodes state — the same language as the retired WinForms orchard.
- **Readout / persist / manual — the three regions of a System View:**
  - `.readout` shows in **Advisor** mode only.
  - `.manual` shows in **Manual** mode only (and, for timeline views, is overwritten live).
  - `.persist` (optional, `v.persist`) shows in **BOTH** modes — use it for live status
    that the user always wants visible (beard levels, quest status, EXP ratio, orchard).
    It is a sibling of readout/manual and is never hidden or overwritten.

## Colour semantics (separate from the accent)

`--ok` good / on-target · `--warn` needs attention / actionable · `--crit` blocking ·
`--faint`/`--muted` inactive or secondary. The accent (`--accent`) is for "current"/
interactive emphasis, not status.
