# Changelog

All notable changes to NGU Advisor are documented in this file.

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
