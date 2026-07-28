# NGU Advisor

NGU Advisor is an automation platform and advisor for the Steam version of NGU Idle. An injected DLL runs
the automation in-game; a separate **companion window** is the configuration surface and live dashboard.

**Version 2.0.1** — the Companion release.

# Install & update

1. Download the latest release from the [releases page](https://github.com/Glowey-Glow/NGUAdvisor/releases)
   — grab the zip named for the version (`NGUAdvisor_2.0.1.zip`), **not** the source archive.
2. Extract it anywhere.
3. With **NGU Idle open**, run **`Advisor Launcher.exe`** (or the `Run NGU Advisor.bat` fallback).

Injection worked when the advisor overlay appears in the top-left of NGU.

<!-- TODO(user): add docs/screenshots/injected.png (the in-game overlay) to show it here. -->

**To update:** close the companion, unload from **Settings → Unload** (or just close the game), then inject
the new version.

# Files & folders

On first inject a folder is created at `%UserProfile%\AppData\LocalLow\NGUAdvisor` containing:

- **settings.json** — all settings (managed from the companion; editable by hand for bulk paste, e.g. Gear
  Optimizer loadouts).
- **zoneOverrides.json** — override the default Idle/Manual Power/Toughness used for gold sniping.
- **profiles/** — allocation profiles (`default.json`, plus any you add). See [Profiles & allocation](#profiles--allocation).
- **logs/** — see [Logs](#logs).

Saving `settings.json`, `zoneOverrides.json`, or any profile automatically reloads the advisor — no game
restart needed.

# Logs

Logs are written to `…\NGUAdvisor\logs\` and are viewable directly in the companion — click **Log** in the
top bar to open the log drawer, then pick a log from the dropdown (it live-tails while open). No external
tools needed.

| File | Contents |
|---|---|
| `inject.log` | General advisor activity (the default "Session" view). |
| `debug.log` | Errors and diagnostics — check here first if a profile misbehaves. |
| `loot.log` | Loot dropped by enemies. |
| `combat.log` | Combat-algorithm decisions. |
| `cards.log` | Cards cast and trashed (persists across sessions). |
| `pitspin.log` | Money-pit, daily-spin, and fruit-harvest results (persists across sessions). |

# The companion UI

Configuration and live status live in the companion window. Open it in-game with **F1** (it also
auto-launches on inject if enabled in Settings).

Every system view has up to three layers:

- **Advisor** — what the advisor recommends right now (read-only readout).
- **Manual** — the settings / timeline you control. Flip a view between Advisor and Manual with its segmented
  control.
- **persist** — live status shown in **both** modes (e.g. beard levels, quest status, the EXP ratio).

Toggle **Automation** per system (each view's switch) or globally (the top-bar pill / **F2**). The nav rail
groups the views:

| Group | Views |
|---|---|
| **Dashboard** | Overview |
| **Progression** | Adventure / ITOPOD · Titans · Challenges |
| **Resources** | Energy / Magic / R3 · EXP · Wandoos · NGU |
| **Gear** | Loadouts · Gear · Boosts · Inventory |
| **Economy** | Gold & Money-pit · Quests · Wishes |
| **Growth** | Diggers · Beards · Yggdrasil · Blood · Cards · Cooking · Perks & Quirks |
| **Profile & Setup** | Consumables · Rebirth · Settings · Profiles |

## Overview

![Overview](docs/screenshots/overview.png)

The dashboard. The **top bar** carries the Automation pause/resume pill, live Difficulty · Profile ·
next-loop countdown, a Focus/Full density switch, **Reload**, and **Log**. Below it a **growth strip** shows
per-hour EXP / NGU / PP / AP / Cube with sparklines.

- **Do this now** — the single highest-priority action, with a button to jump to its system.
- **Current stage** and **Next goal** — where you are and what's next.
- **Also worth doing** — the rest of the advisor's ranked suggestions.
- **Instruments** — live resources, titan attack/defence vs its autokill gate, boost-farm zone vs ITOPOD,
  and NGU rate.

## Progression

### Adventure / ITOPOD

![Adventure / ITOPOD](docs/screenshots/adventure-itopod.png)

Where to idle-farm — the furthest one-shottable zone, or ITOPOD's optimal floor as the fallback — with
gear/boost farm toggles, gear-hunt (camp a stage), combat mode, ITOPOD optimisation and auto-push, and the
sniped-enemy blacklist.

### Titans

![Titans](docs/screenshots/titans.png)

Which titans the advisor may target, and the combat mode. The readout follows the kill ladder
(**first kill → idle-stat farm → auto-kill**) for the next titan in reach.

### Challenges

![Challenges](docs/screenshots/challenges.png)

The challenge rotation queued in the active profile, plus live challenge progress. Each row is a challenge and
the completion it runs *up to* — the profile itself stores one entry per run, as a 1-based completion ordinal
(`"BASIC-1" … "BASIC-5"` for five Basic runs), which is what the advisor matches against your completion count.

## Resources

### Energy / Magic / R3

![Energy / Magic / R3](docs/screenshots/energy-magic-r3.png)

The resource allocator. A live readout shows current E/M/R3 fill; below it are per-resource **priority
timeline editors** reading the active profile — add, edit, and delete breakpoints (time + priority codes)
right here and they save to the profile. The priority-code grammar is under
[Profiles & allocation](#profiles--allocation).

### EXP

![EXP](docs/screenshots/exp.png)

Spends EXP toward the guide's stat-value ratio. A chip shows the current base **Energy : Magic** ratio and
whether you're on-ratio; Manual mode adds a **Set ratio** override.

### Wandoos

![Wandoos](docs/screenshots/wandoos.png)

Picks the Wandoos OS with the best projected payoff over the run.

### NGU

![NGU](docs/screenshots/ngu.png)

Auto-targets Energy and Magic NGUs by rating; difficulty follows the profile timeline.

## Gear

### Loadouts

![Loadouts — titan & gold](docs/screenshots/loadouts.png)
![Loadouts — quest & shockwave](docs/screenshots/loadouts-quest-shockwave.png)

Shows the currently-equipped set, then lets each mode (idle / titan / gold / quest / cooking / loot-hunter)
choose the optimiser objective it targets, with per-mode respawn options.

### Gear

![Gear](docs/screenshots/gear.png)

Equips the optimiser's best set for the active objective and re-optimises when a genuinely better set
appears. A **Re-optimize gear now** button forces it immediately and reports the outcome.

### Boosts

![Boosts](docs/screenshots/boosts.png)

The boost-farm advisor compares the best one-shottable farm zone against ITOPOD's optimal floor (flips to
**On target** when you're already there), and the **Infinity Cube** readout shows power/toughness against
their softcaps so you can see when boosts stop helping the cube. Manual controls: auto-convert, cube
priority, favored MacGuffin, boost-type priority (Power / Toughness / Special), a priority-boost list, and a
never-touch blacklist.

### Inventory

![Inventory](docs/screenshots/inventory.png)

Inventory automation (merge, convert, consumables) plus the **transform-chain editor**: per item in a
chain, choose **Promote** (climb it up the chain), **Keep** (protect a maxed copy) or **Filter**
(loot-filter superseded tiers). Convertibles are only consumed when safe — never a chain tier or a protected
level-100 copy.

## Economy

### Gold & Money-pit

![Gold & Money-pit — advisor](docs/screenshots/gold-moneypit-advisor.png)
![Gold & Money-pit — manual](docs/screenshots/gold-moneypit-manual.png)

Gold-snipe management (auto titan gold, re-snipe triggers, re-snipe timer, Snipe now) merged with the
money-pit (auto-throw, predict + prep, run mode, threshold, daily spin, daycare feed). The readout shows the
next-throw prediction (outcome category), pit-ready/ETA, and the advisor's throw plan.

### Quests

![Quests — advisor](docs/screenshots/quests-advisor.png)
![Quests — manual](docs/screenshots/quests-manual.png)

Quest automation and rules (majors/minors, buttering, pooling, abandon thresholds). The status strip shows
the banked "N of N", whether minors are being idled, and current quest progress.

### Wishes

![Wishes](docs/screenshots/wishes.png)

Wish selection mode and the % of idle Energy/Magic/R3 to spend, with priority and blacklist lists.

## Growth

### Diggers

![Diggers](docs/screenshots/diggers.png)

Levels the highest-value active diggers by the digger laws (leveling is decoupled from set completion); the
ordered set comes from the profile timeline.

### Beards

![Beards](docs/screenshots/beards.png)

Runs the ordered beard set; the status chips show each beard's current level and the permanent levels it
will bank on the next rebirth.

### Yggdrasil

![Yggdrasil](docs/screenshots/yggdrasil.png)

Harvest automation plus the **orchard**: one tile per fruit, its bar filling with growth progress through
the current tier and coloured per tier (gold when maxed), the fruit name inside the bar. Unpurchased fruits
show the seeds needed to unlock them. Toggles for activate-fruits, loadout/digger/beard swaps, and the swap
tier threshold.

### Blood

![Blood](docs/screenshots/blood.png)

Casts blood spells and pools iron pills; the planner mirrors the caster's own refusal rules.

### Cards

![Cards](docs/screenshots/cards.png)

Card automation — auto-cast, trash junk, sort the deck, and protected-card handling. The **trash filter** is
a per-bonus-type grid (trash at/below a rarity and cost); bonus-type names are shown readably (e.g.
`energyNGUSpeed` → **ENGU Speed**). A sort-order list controls cast/trash priority.

### Cooking

Manages cooking automatically and swaps to the cooking gear set when enabled. (The cooking gear set/objective
is edited under **Loadouts**.)

### Perks & Quirks

![Perks and Quirks](docs/screenshots/perks-quirks.png)

The guide-ordered spend plan for your current chapter, split into **Upcoming** and **Purchased** tabs. Each
step shows the perk/quirk, its current → target level, and whether it's the next buy, queued, or gated to a
later chapter. (Guide order is authored for chapters 2–5; later chapters show fewer steps.)

## Profile & Setup

### Consumables

![Consumables](docs/screenshots/consumables.png)

Runs consumables on the profile timeline; see [Consumables](#consumables).

### Rebirth

![Rebirth](docs/screenshots/rebirth.png)

The rebirth cadence and rules; see [Rebirth](#rebirth).

### Settings

![Settings](docs/screenshots/settings.png)

Master and per-system automation switches — each system has an **Automation** (act) and **Decisions**
(advise) toggle, ANDed in code — plus miscellaneous options (disable the in-game overlay, auto-launch the
companion on game load, digger GPS cap, **Unload**).

### Profiles

Switch the active profile live and toggle auto-profile (the advisor generates allocation). Under **Profile
files**, **Edit** opens the Profile Editor and **Open Profile Folder** shows the profile `.json` files in
Explorer, with the active one selected.

### Profile Editor

Every breakpoint in the active profile, one system at a time — Energy, Magic, R3, Gear, Diggers, Beards,
Wandoos OS, NGU difficulty, Consumables and Rebirth. Add, edit and delete breakpoints; each change is
validated, written to the profile file, and picked up by the advisor without a restart.

The editor always follows the **active** profile, so switch profiles on the Profiles page first. Press
**F9** in the game window to jump straight here (it opens the companion first if it is closed).

# Profiles & allocation

A profile (`profiles/<name>.json`) is a set of **breakpoints** grouped by lane. Every breakpoint has a
**`Time`** (rebirth time) and a payload; the advisor applies the latest breakpoint whose time has passed.
Edit them in the [Profile Editor](#profile-editor) (all lanes in one place, **F9**), in each system view's
timeline editor, or in the JSON directly.
[Sample profiles](https://github.com/Glowey-Glow/NGUAdvisor/tree/main/SampleProfiles) ship with the release.

**Time** is seconds (`86400`) or an object: `{ "h": 1, "m": 30, "s": 20 }`.

A profile looks like this (every lane is optional):

```jsonc
{
  "Energy":     [ { "Time": 0, "Priorities": ["CAPNGU-0", "CAPWAN", "AT-1", "NGU-1"] } ],
  "Magic":      [ { "Time": 0, "Priorities": ["CAPNGU-0", "CAPWAN", "BR", "NGU-1"] } ],
  "R3":         [ { "Time": 0, "Priorities": ["HACK-1"] } ],
  "Gear":       [ { "Time": 0, "ID":   [189, 442, 160, 441] } ],
  "Beards":     [ { "Time": 0, "List": [5, 1, 6, 3] } ],
  "Diggers":    [ { "Time": 3650, "List": [8, 3, 4, 5] } ],
  "Wandoos":    [ { "Time": 0, "OS":   1 } ],
  "NGUDiff":    [ { "Time": 0, "Diff": 0 } ],
  "Rebirth":    [ { "Type": "Time", "Time": { "h": 24 } } ],
  "Challenges": ["BASIC-1", "TC-1"],
  "Consumables":[ { "Time": 0, "Items": ["EPOT-B", "MPOT-B"] } ]
}
```

## Resource priorities (Energy / Magic / R3)

Each priority is a **code**, optionally with `-X` (a 0-indexed target) and `:P` (a percent cap). Two flavours:

- **CAP…** — fills that lane to its 10-second cap; a single CAP priority can drink the whole idle pool.
- **no prefix** — takes an even share of the remaining idle resource. Excess always spills to later
  priorities.

Add `:P` to limit a priority to **P % of your cap** (or of idle resource for non-cap priorities), e.g.
`CAPRIT-0:30`.

| Code (add `CAP` prefix to cap) | Lane | Target |
|---|---|---|
| `NGU-X` / `ALLNGU` | E (0–8), M (0–6) | a single NGU / every NGU |
| `AUG-X` / `BESTAUG` | E (0–13) | an augment / the best affordable augment |
| `AT-X` / `ALLAT` | E (0–4) | an Advanced Training slot / all AT |
| `BT-X` / `ALLBT` | E (0–11) | a Basic Training slot / all BT |
| `WAN` | E, M | Wandoos for that resource |
| `TM` | E, M | Time Machine for that resource |
| `RIT-X` | M | a blood ritual (magic) |
| `BR` / `BR-X` | M | cast rituals high→low (`BR-3600` = only those finishing within the hour) |
| `HACK-X` / `ALLHACK` | R3 (0–14) | a hack / every hack |

Indexes are 0-based and follow the in-game order (e.g. NGU 0 is the first NGU, augment 0 the first augment).

## Other timed lanes

| Lane | Field | Notes |
|---|---|---|
| **Gear** | `"ID": [ … ]` | equipment item IDs (dump yours with **F11**, or from Gear Optimizer). |
| **Beards** | `"List": [ … ]` | 0-indexed (Fu Manchu 0 … Golden 6). |
| **Diggers** | `"List": [ … ]` | 0-indexed (Drop-chance 0 … EXP 11). |
| **Wandoos** | `"OS": n` | 0 = Wandoos 98, 1 = MEH, 2 = XL. |
| **NGUDiff** | `"Diff": n` | 0 = Normal, 1 = Evil, 2 = Sadistic. |

## Rebirth

Simple: `"RebirthTime": 86400` (rebirth at that many seconds; `-1` = never). Or a list of rules under
`"Rebirth"`, evaluated by time then by target:

| `Type` | Rebirths when… | `Target` |
|---|---|---|
| `Time` | the time passes | — |
| `Number` | your number will reach OldNumber × Target | multiplier |
| `Bosses` | you can beat Target more bosses than last rebirth | count |
| `Muffin` | optimising Muffin usage (cycles 24h ↔ 24h − Target min) | 1–60 min |
| `TimeBalancedMuffin` | like Muffin but keeps rebirths at the same clock time | 1–15 min |

```jsonc
"Rebirth": [
  { "Type": "Number", "Time": { "m": 30 }, "Target": 10 },   // if run ≥ 30m and number would 10×
  { "Type": "Time",   "Time": { "h": 24 } }                  // otherwise at 24h
]
```

**Challenges** — rebirth into challenges with `"Challenges": ["BASIC-1", "TC-1"]` (code + 1-based number):
`BASIC`, `NOAUG`, `24HR`, `100LC`, `NOEC`, `TC`, `NORB`, `LSC`, `BLIND`, `NONGU`, `NOTM`.

The number is the *completion ordinal*, not a count — an entry runs only when it would earn exactly that
completion. So five Basic runs is `["BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5"]`; `["BASIC-5"]` on
its own does nothing until four Basic completions already exist. Order matters: the first eligible entry wins.

## Consumables

`"Consumables": [ { "Time": 0, "Items": ["EPOT-B", "MPOT-B:5"] } ]` — add `:N` for a count (beta potions and
Muffins ignore it and only ever use one).

| Code | Item | Code | Item |
|---|---|---|---|
| `EPOT-A/B/C` | Energy Potion α/β/δ | `EBARBAR` | Energy Bar Bar |
| `MPOT-A/B/C` | Magic Potion α/β/δ | `MBARBAR` | Magic Bar Bar |
| `R3POT-A/B/C` | R3 Potion α/β/δ | `MUFFIN` | MacGuffin Muffin |
| `LC` / `SLC` | Lucky / Super Lucky Charm | `MAYO` | Mayo Infuser |

**How timing works (and its limits):** the advisor can't know consumables used manually or across sessions,
so a breakpoint re-runs whenever the profile (re)loads. It compensates by estimating when a consumable
*would* have expired and only using what's needed to reach that expiry — it won't double-dose, and won't
re-use one that's still running unless **Use consumables if already running** is on. Beta potions and Muffins
only activate if not already active. There's no handling for two of the same consumable in one breakpoint or
for alpha/delta sharing a timer, so keep breakpoints simple.

# Zone stat overrides

The best gold-snipe zone is chosen from a set of Power/Toughness thresholds per zone (manual threshold →
snipe without fast combat; idle threshold → snipe with fast combat). Override them in `zoneOverrides.json`;
defaults are on the [Default Zone Stats wiki](https://github.com/Glowey-Glow/NGUAdvisor/wiki/Default-Zone-Stats-for-Sniping).

# Keybinds & extras

| Key | Action |
|---|---|
| **F1** | Open the companion window (relaunches it if closed). |
| **F2** | Globally disable / enable all automation. |
| **F3** | Quicksave — dumps a save + `ngusav.es` JSON (for Gear Optimizer) to the NGUAdvisor folder. |
| **F7** | Quickload the F3 save. |
| **F8** | Toggle the Quick Loadout / Diggers / Beards temp-swap. |
| **F9** | Open the [Profile Editor](#profile-editor) in the companion (opens the companion first if closed). |
| **F11** | Dump equipped gear IDs to the log (for `Gear` breakpoints). Was **F5** before 2.1. |
