# Companion UI — design & spacing rules

The companion (`NGUAdvisorCompanion/wwwroot/index.html`) is one self-contained page.
These are the rules every addition MUST follow so the UI stays consistent. The theme is
the dark "Arcade · Current Era" token set defined in `:root` — never hard-code a colour;
use the `--*` tokens.

**This app is dark-committed.** There is no light mode and no theme switch: nothing in the
page ever sets `data-theme`, so the `prefers-color-scheme: light` block and the
`:root[data-theme=…]` twins that used to sit under `:root` could never execute. They were
deleted (finding F5) rather than left to imply a mode that doesn't exist — git history has
the values if a light theme is ever actually wanted. Do not add "light-theme overrides" for
a new token; there is nothing to override.

## Spacing scale (use these; don't invent one-off margins)

`--sp-1: 4px · --sp-2: 8px · --sp-3: 12px · --sp-4: 16px · --sp-5: 22px`

The scale governs **layout spacing between blocks** — margins and flex/grid `gap`. A
control's own internal padding (e.g. a chip's `5px 10px`, an input's `7px 9px`) is a
component detail and doesn't have to land on the scale; just keep it consistent per
component. When you do need a layout margin/gap, use a `--sp-*` token, not a raw px.

- **Sections are spaced by `.seclabel`, nothing else.** `.seclabel` carries
  `margin: var(--sp-5) 2px var(--sp-3)` — 22px above, 12px below, 2px of optical inset
  either side. (That rule used to be written as the raw `22px 2px 12px`, which is the same
  pixels; it now names the tokens, because this section says to.) NEVER add an inline
  `margin-top` to a `.seclabel` or to the block right after it — that double-spaces or
  fights the rhythm. A section = one `.seclabel` followed by its content.
- Content blocks inside a section (chips, tiles, field grids, hint rows) get their gap
  from their own `gap`, not from top margins.
- Vertical gap between sibling controls: `--sp-3` (grids/switch rows already use ~11–12px).

## Type scale (use these; don't invent one-off font sizes)

`--fs-1: 10px · --fs-2: 11px · --fs-3: 12px · --fs-4: 13px · --fs-5: 15px · --fs-6: 19px ·
--fs-7: 23px · --fs-8: 25px`

Sizes are **not** era tokens. The Arcade block redefines colour, family and radius; it must
never redefine `--fs-*`. Every rendered size in the page comes from one of these eight —
that is a measured invariant, not an aspiration, and the census in the verification harness
counts it (`visual.fontSizeCount === 8`). Before the F1 pass there were **fifteen**, with
half-pixel steps between 9 and 12.5px that no reader can perceive as meaning anything.

| Token | px | What it is for |
|---|---|---|
| `--fs-1` | 10 | data label — column heads, key/value keys, badges, the nav count badge. The floor. |
| `--fs-2` | 11 | dense chrome — chips, pills, tabs, small mono data, **and the section label** |
| `--fs-3` | 12 | secondary copy — hints, rationale, captions |
| `--fs-4` | 13 | body and form controls (also `body`'s own size) |
| `--fs-5` | 15 | lead sentence — the recommendation, the goal line |
| `--fs-6` | 19 | system-view title |
| `--fs-7` | 23 | hero title, instrument big number |
| `--fs-8` | 25 | stage name |

Two rules that keep it at eight:

- `body` states `--fs-4` explicitly. Anything unsized inherits the scale rather than falling
  through to the UA's 16px.
- `button, input, select, textarea { font-size: inherit }`. Form controls don't inherit type
  by default; without this a new unstyled control silently renders at the UA's 13.3333px and
  becomes a ninth size.

## Labels: there are exactly two, and there will not be a third

Every uppercase micro-label on the page resolves to one of two treatments, keyed on what the
label is **for**:

| Class | Family / size / tracking | Role |
|---|---|---|
| `.u-seclabel` | `--mono` / `--fs-2` (11px) / `.12em` / uppercase | **introduces a region** — sits above or beside a block of content |
| `.u-datalabel` | `--mono` / `--fs-1` (10px) / `.1em` / uppercase | **names a value** — a column head, or a key inside dense data |

The legacy selectors (`.eyebrow`, `.lbl`, `.gh`, `.hero-kick`, `.hero-sys`, `.crumb .grp`,
`.tile .th .name`, `.tle-title`, `.stat .k`, `.growth .gl .gk`, `.tl-head span`,
`.tf-shead span`, `.cf-head span`, `.badge`, …) are **folded into these two rules** rather
than re-classed in the markup, so no DOM, id or handler moved. There were fifteen
near-identical treatments before; one semantic role does not get fifteen voices.

**Do not add a third label treatment.** If a new label is needed, add its selector to
whichever of the two rules matches its role. Note also that the two rules set
size/family/tracking/case **only** — colour stays wherever it already lived, so folding a
selector in can never move a contrast-bearing pair. `.lbl` and `.eyebrow` state their own
colour (`--faint`, 5.25:1 on `--surface`) as global one-liners; don't re-scope them per
container, which is exactly how two `.lbl` spans on the Perks & Quirks view ended up
rendering at full `--text` while every other section label was faint.

### Rejected: sentence-case Expressway section labels

An alternative was built and reviewed: put the licensed display face (Expressway) on the
section labels at 11px **sentence case**, on the argument that Expressway is otherwise doing
almost no work and that a page-full of tiny uppercase mono whispers gives it no voice.
It was compared side by side at **1346 × 1184 with a live snapshot off the real pipe**
— the owner's actual viewport and data, not a synthetic fixture — and the owner **rejected**
it.

Reason: on a long, glanceable dashboard the uppercase labels act as scannable landmarks down
the page (CURRENT STAGE / ALSO WORTH DOING / INSTRUMENTS). Sentence case made them recede
into prose. It also kept the shout on `.hero-kick` ("DO THIS NOW"), and kept uppercase doing
the work of separating rail group headers from the nav items beneath them.

The two-class collapse is the part that stands. **Do not re-propose the sentence-case variant
from the audit text** — it has been tried. If it is ever revisited, re-run that same
comparison at 1346px with real data first.

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
    that the user always wants visible (beard levels, quest status, EXP ratio, orchard),
    and for any page-level control that must not be able to hide itself (the Settings
    filter box lives here precisely because `.manual` is the subtree it hides).
    It is a sibling of readout/manual and is never hidden or overwritten.

## One setting, several controls

A `data-setting` key may be bound by **more than one control**. The Loadouts view uses this: each mode
mirrors the real switches that arm it (`ManageTitans`, `SwapTitanLoadouts`, …) next to the objective
they arm, so the fix is where the confusion is stated rather than two views away. The switches on the
system's own view stay exactly where they were — this duplicates, it does not move.

Two rules make that safe, and both already hold in `index.html`:

- **`renderSettings` groups by key first.** `reconcile()` keys on the SETTING, not the element, and
  `LASTOK` holds a single apply closure per key — so every element sharing a key must be driven from
  ONE closure. Per-element reconcile would leave the second copy a snapshot behind, and a
  connection-drop rollback would restore only one of them.
- **The change handler moves every copy optimistically**, and its rollback closure restores every copy.

If you add a mirrored control, add nothing else: the binding, the snapshot and the write path all
already exist. What you must NOT do is invent a second key for the same underlying setting.

`tests/companion/test-loadouts.js` asserts both behaviours, and cross-checks every `[data-setting]`
key the page emits against `UiBridge.BindingList` — that boundary has no compiler behind it, and a key
the injector doesn't bind fails silently as a `LogDebug` while the control appears to work.

## Colour semantics (separate from the accent)

`--ok` good / on-target · `--warn` needs attention / actionable · `--crit` blocking ·
`--faint`/`--muted` inactive or secondary. The accent (`--accent`) is for "current"/
interactive emphasis, not status.
