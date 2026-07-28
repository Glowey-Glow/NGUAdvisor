# Changelog

All notable changes to NGU Advisor are documented in this file.

## [2.1.0] - 2026-07-28 — Campaign

The challenge campaign becomes a first-class part of the advisor: the CBlock spine is modelled, its
profiles ship and auto-install, and the UI tells you which block you are on and whether anything in the
chain is unreachable. Settings and profiles carry over — extract over your old copy.

### Added

- **Profile Editor** — every breakpoint in the active profile in one place, grouped by system
  (Energy/Magic/R3, Gear, Diggers, Beards, Wandoos, NGU difficulty, Consumables, Rebirth). Reachable from
  the nav, from **Profiles → Edit**, and from **F9** in-game.
- **Profiles → Open Profile Folder** — opens the profiles folder in Explorer with the active profile
  selected.
- **Challenge campaign** — the CBlock spine as a first-class view: which block you are on, what each one
  runs, and a chain-health report naming any ordinal no profile can reach.
- **Seventeen campaign profiles** ship and auto-install alongside the goal presets.
- **Titan auto-kill chips** show the version being killed, the respawn countdown, and whether a titan is
  riddle-locked; Walderp shows the hunt (`N of 4 found`) and where he is hiding, with **Locate**.

### Changed

- **Dark only.** The light theme is retired; the UI commits to the dark token set.
- **F11** dumps equipped gear IDs. It was F5, which is now reserved for development builds.

### Fixed

- **F9** works again. It opened the old WinForms profile editor, which 2.0.0 removed; it now opens the
  Profile Editor in the companion, launching the companion first if it is closed.
- **Challenge rotations no longer collapse.** The number after a challenge code is a 1-based completion
  *ordinal*, not a count — the advisor engages an entry only when it would earn exactly that completion.
  The editor treated it as a count and deduped by code, so a five-run Basic rotation written
  `BASIC-1 … BASIC-5` was rewritten to a single entry that sits idle until four Basic completions already
  exist, stranding every later ordinal behind it.
- **Growth rates** report again. Their only sampler was the WinForms status pump 2.0.0 removed, so every
  growth figure had been publishing zero.
- **Gold loadouts no longer stick on.** The advisor could target a titan whose clue riddle is unsolved.
  That titan never spawns, so "spawning soon" stayed true forever and the gold set stayed equipped.
- The titan spawn version no longer drops below the version being chased when it is already auto-killable,
  which had been undoing a manual selection on every pass.
- **Wandoos OS advice** accounts for Advanced Training levels earned inside the projection window, and
  projects at the share of the cap the profile actually gives Wandoos rather than the whole cap.
- The companion window remembers its size and position, and no longer leaks a GDI icon handle on launch.

## [2.0.1] - 2026-07-26

A fix release for 2.0.0. Settings and profiles are unchanged — extract over your old copy, or into a new
folder, and run it as usual.

### Fixed

- **The companion window never opened in 2.0.0.** Neither `Advisor Launcher.exe` nor `Run NGU Advisor.bat`
  told the advisor where it had been installed, so the advisor could not find the companion: it did not
  auto-launch on injection, and **F1** appeared to do nothing. Both now record the install path, and the
  companion opens as intended.

## [2.0.0] - 2026-07-25

The **Companion** release. The in-game WinForms settings form is retired; configuration and live status
now live in a separate out-of-process **companion window** (WebView2), which the advisor auto-launches.
Existing settings and profiles remain compatible.

### Added

- **Companion UI** — a live dashboard and full configuration surface (over named pipes): Overview plus
  every system view (Adventure/ITOPOD, Titans, Challenges, Energy/Magic/R3, EXP, Wandoos, NGU, Loadouts,
  Gear, Boosts, Inventory, Gold & Money-pit, Quests, Wishes, Diggers, Beards, Yggdrasil, Blood, Cards,
  Cooking, Perks & Quirks), the profile timeline editors, and a built-in log viewer.
- **Advisor Launcher.exe** — an iconned launcher (the `Run NGU Advisor.bat` stays as a fallback).
- Gear "Re-optimize now", boost-farm compliance + Infinity Cube status, the Yggdrasil orchard, the EXP
  Energy:Magic ratio with a manual override, the Perks & Quirks guide plan (Upcoming / Purchased tabs),
  and per-beard rebirth-gain readouts.

### Changed

- Configuration moved from the in-game F1 WinForms form to the companion; **F1** now opens the companion.
- The gear-advisor "re-optimize" gap now matches the equip logic exactly (no more phantom "+N%").
- Titan targeting follows the kill ladder: first kill → idle-stat farm → auto-kill.

### Removed

- The injected WinForms settings form and all its panels.

## [1.2.0] - 2026-07-22

Existing settings and profile files remain compatible with version 1.1.0. This release is a large
correctness and robustness pass from a full external review, plus the first automated test coverage.

### Fixed

- Profiles no longer risk silent number corruption on comma-decimal system locales, and large integers
  round-trip exactly (culture-invariant JSON number handling).
- The EXP planner no longer overflows at high (Evil-scale) values.
- Boost-farm zone values were corrected — Evil zones were dramatically undervalued against ITOPOD, which
  suppressed zone recommendations; the advisor now compares them on the true boost-value scale.
- The final Sadistic titan zone (THE TRAITOR) is now reachable, and Sadistic zone-unlock thresholds are
  corrected (a missing zone had shifted several later zones).
- Iron-pill blood advice now matches what the caster will actually do (no more "cast now" for a pooled cast).
- Money-pit lock, inventory transform-chain protection, settings-form resilience, and numerous smaller
  correctness fixes across combat, gear, diggers, quests, wishes, and consumables.

### Added

- An automated test project (53 tests) guarding JSON round-trip, number formatting, and the titan tables.
- A single consistent large-number formatter across all panels.

### Changed

- The two progression-chapter engines are now documented as distinct, non-interchangeable concepts.

## [1.1.0] - 2026-07-15

Existing settings and profile files remain compatible with version 1.1.0.

### Added

- Two-level navigation with Overview and Priorities.
- Dedicated Profile page for allocation source, profile selection, editing, and file access.
- Searchable Settings interface.
- Persistent eight-cell status strip.
- Redesigned Loadouts interface covering Titan, Gold, Quest, Yggdrasil, Cooking, Loot Hunter, and Shockwave.
- Configured, WILL EQUIP, and CURRENTLY EQUIPPED snapshot displays.
- Contextual activity feedback for supported user actions.

### Changed

- Advisor home workflow is split between Overview and Priorities.
- Automatic Money Pit actions use a single configured owner, preventing competing automatic throw paths.
- Public release builds no longer embed local build-machine paths.
- Current-equipment snapshots update explicitly through REFRESH STATE rather than implying a live feed.
- The advisor now holds automatic Iron Pill casts until the pill has been available for at least 30 minutes and would add at least 10% of current base adventure power.

### Fixed

- A failure in one advisor operation no longer prevents later operations from running.
- Temporary Money Pit, equipment-lock, Yggdrasil, and MacGuffin state is restored after failures.
- Repeated faults are reported without flooding the log.
- Settings filtering and layout restoration no longer produce false overlap reports.
- Profile Editor paste operations validate before replacing the current loadout and retain the accepted undo behavior.
- Profile, Loadouts, status, and other updated views received layout and audit corrections.

### Removed

- Obsolete mode-loadout UI infrastructure.
- Superseded legacy Profile selector controls.
- Repetitive Yggdrasil fruit-state debug output.
