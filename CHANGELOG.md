# Changelog

All notable changes to NGU Advisor are documented in this file.

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
