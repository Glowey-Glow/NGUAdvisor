# Changelog

All notable changes to NGU Advisor are documented in this file.

## [2.4.1] - 2026-08-19 — The advisor owns the percentages

The allocator now decides how much every system gets, sized from what that system can actually
absorb this tick. The knob you used to turn by hand is still there, but it means something
different — read the first section if any of your profiles use `:percent`. Settings and profiles
carry over — extract over your old copy.

### Changed — read this if your profile uses `:percent`

- **`:P` on a priority now means MANUAL MODE for that system, not "cap it at P%".** The advisor
  sizes every lane it owns from live capacities, so you no longer need percentages at all — that
  is the default and the recommended setup. Writing `:P` tells the advisor to **stop optimising
  that system**: the lane claims P% of the resource off the top, before anything else allocates,
  and is excluded from the optimiser. Remove the `:P` to hand the system back. The load banner
  warns you for every percentage it finds, and those lanes are tagged `[Manual]` in the log.

  Two things `:P` cannot do. **It does nothing while Auto Profile is on** — Auto Profile generates
  the lists itself, so your timeline is never consulted. And **it cannot claim Wandoos**, which is
  the surplus sink every other lane's leftovers fall into; taking it out of the optimiser would
  leave the remainder nowhere to go, so the percentage is ignored there and the log says so once.

  A manual lane still has to pass the allocator's safety gates — the 100-Level-Challenge budget and
  the per-system unlock checks — so `:P` chooses the **amount**, not the rules. If a gate refuses
  one, the log names the gate and the reason.

- **The shipped presets and sample profiles have been migrated for you.** Percentages were removed
  from their Energy and Magic timelines and kept on R3. Under the constraint allocator (the
  default) the removed ones did nothing, so those presets behave exactly as before. **If you have
  turned the constraint allocator off**, the original share loop does read them, and removing a
  percentage from a `CAP…` priority makes that lane unbounded rather than inert — re-check any
  preset you rely on in that mode.

### Added

- **R3 picks the hack that is actually worth the most.** After the guide's first-milestone sweep,
  R3 used to park on the Adventure hack for the rest of the run no matter what it was worth.
  It is now priced every minute — value per level against what a level costs, both read live from
  the game — and the pool goes to whichever hack wins. On a mature board the Adventure hack ranked
  **eleventh of fifteen**. The order re-prices itself as levels land, so nothing needs tuning.
- **Beards fill limited slots in a set order**: BEARd, Neckbeard, Beard Cage, then Reverse Hitler
  and LadyBeard up to their 1000 permanent-level softcap, then the Golden Beard once Troll
  Challenge 7 is done, then Fu Manchu, then whichever of Reverse/Lady has already saturated.
  Past 1000 permanent levels a beard's bonus stops growing linearly and starts growing as a square
  root, which is why the pair drops down the order once they cross it.
- **Wishes can be chosen by value.** The existing modes rank on speed or price, so two wishes that
  finish at the same rate were indistinguishable however differently they paid. The new **Value**
  mode ranks on what a level is actually worth. Relevant if you run wishes as a surplus sink.
- **The augment lane is funded during the NGU+AT phase.** That phase previously held nothing that
  could absorb a large pool — every capped lane in it summed to about 14% of energy.
- **Allocation telemetry now reports per round**: how many lanes were in each round, what each was
  offered, and what it took. This is what the pool bar's numbers are built from.

### Fixed

- **Idle energy and magic that had nowhere to go before the wish system unlocks.** The allocator
  reserved a share of the pool for Wandoos every round, but Wandoos can only absorb so much per
  tick, and the surplus was reserved rather than offered onward. Before wishes unlock (a T8 titan
  kill) nothing else could claim it, so it simply sat idle — measured at **26% of energy and 30% of
  magic** on a live save. It now flows to the lanes that can use it. After wishes unlock the
  reserve is left alone, because that surplus is what funds them.
- **A profile gear row that lists item IDs now holds.** It used to be overwritten within the same
  second by your standing pick from Loadouts › Main, and could never re-assert — so you wore
  something other than what your timeline said, permanently and silently. The authored row now
  wins while it is current, and your standing pick resumes when the timeline moves on.
- **The advisor no longer claims your profile is allocating when it isn't.** With Auto Profile on,
  generated lane lists were labelled as the profile's, alongside a line stating the profile was
  still in charge. Both now say which list is actually running.
- **Writes whose reason has expired are reported as stale** rather than still active — for example
  an Advanced Training target set for a run phase that has since ended.
- **The beard recommendation panel no longer suggests a set the advisor will not equip.**

## [2.4.0] - 2026-08-14 — Allocation engine & wishes

The largest release since the companion. The resource allocator has been rebuilt around a
constraint layer that measures what each system can actually absorb before offering it the pool,
and the wish sliders now mean what they say. Settings and profiles carry over — extract over
your old copy.

### Changed — read this if you use wishes

- **The wish "% of idle" sliders now take a share of what is actually left.** They used to take
  their percentage of your *entire* pool, off the top, before every other system allocated — and
  all leftover idle went to wishes anyway, even with a slider at 0. Wishes are now funded last:
  the sliders take their percentage of whatever is still idle after Wandoos, augments, NGUs and
  the rest have taken their fill, and **0% genuinely allocates nothing**. If your sliders are set
  low, wish progress will visibly slow — that is the fix working, not a regression; raise the
  sliders if you want wishes funded harder. This behaviour is new in this release: if wishes do
  something you don't expect, please open an issue with your slider values and a log snippet.

### Added

- **The constraint layer.** Every energy and magic pass now runs through a capacity-aware fill:
  each lane is offered what it can provably use this tick (stair-snap level costs, stall floors,
  per-tick absorption), surplus flows to a designated sink, and anything left idle is *reported*
  with a reason instead of vanishing. The companion's pool bar shows where every unit went.
- **Guide targets.** Where the community guide names a hard number, the advisor now stops there
  instead of over-levelling — first wire: the Advanced Training Block cap on Evil chapter 5.
- **R3 after CBlock 3: the first-milestone sweep.** With AutoProfile on, R3 used to sit on hack 0
  forever. Once CBlock 3 completes on Evil, the advisor now runs the guide's first-milestone
  sweep across hacks 3–7, then parks on the Adventure hack.
- **Titans: the spawn parks on the highest *killable* version**, not the highest version you have
  auto-killed — a version you can beat but have never AK'd is no longer skipped. And when your
  stats clear a titan's kill requirement with room to spare, the gear optimizer spends the
  surplus on drop-chance and loot instead of wasted stats. Both are new live-fight behaviours —
  if a titan fight surprises you, please report it.

### Fixed

- **The Wandoos advanced-training slots no longer receive advisor-written targets.** Only the
  Block slot ever takes one; the Wandoos E/M dumps self-limit as designed.
- Hundreds of smaller correctness fixes ride along with the engine work; the automated test
  suite grew from 361 to 2,159 checks between 2.3.0 and this release.

## [2.3.0] - 2026-08-07 — Allocation safety

One unusable entry in a profile's priority list used to stop that resource being allocated at all —
for the rest of the run, with nothing in the log to say so. All three resource lanes were affected.
Settings and profiles carry over — extract over your old copy.

### Fixed

- **One bad priority stopped a whole resource.** A priority the advisor cannot recognise — a typo
  (`NUG`, `CAPWANDOOS`), or a newer profile's entry on an older build — was left in the list as an
  empty slot, and reading it faulted the lane every tick. The failure was caught and throttled to one
  log line per ten minutes, so nothing surfaced. What it cost, per resource:
  - **Energy and Magic.** If **Manage Wishes** is on, the wish step clears energy and magic out of
    every system *before* the allocation step runs — and the allocation step was the thing faulting,
    so nothing went back. Every tick, energy and magic were pulled out of Wandoos, augments, the time
    machine, advanced training, the NGUs and blood magic, and left idle. With Manage Wishes off it
    froze instead: whatever split was in place when the profile loaded stayed there forever, so no
    later breakpoint in the timeline ever took effect, and after a rebirth the whole pool sat idle for
    the entire run.
  - **R3.** Hacks stopped levelling permanently. The same list also had a second failure: a priority
    with an unreadable hack number (`HACK-`, `HACK-x`) looked valid, so every pass emptied all sixteen
    hacks and refilled none.

  Unusable entries are now skipped and the rest of the list allocates normally. A profile with no bad
  entries behaves exactly as before.

### Changed

- **The shipped files now report the version you downloaded.** `NGUAdvisor.dll` reported 1.1.0.0 and
  `Advisor Launcher.exe` reported 1.0.0.0 in File Properties, regardless of the release. Cosmetic —
  nothing in the advisor read those numbers — but they now track the release.

## [2.2.0] - 2026-07-31 — Loadouts

Gear loadouts were configurable but not obviously *armed*: you could pick an objective, watch it save,
and never see a swap — because the switch that arms it lived on a different page, or because a silent
defect swallowed it. This release makes every gear decision visible and fixes the defects behind it.
Settings and profiles carry over — extract over your old copy.

### Fixed

- **Loadouts that never swapped.** Four separate causes, all silent:
  - **Cooking** required a non-empty item list before it would swap at all, so choosing only an
    objective — which is what the objective dropdown encourages — did nothing, ever.
  - **The active gear objective leaked between runs and profiles.** It survived a profile switch (the
    old profile's objective kept re-equipping over the new one), survived a rebirth (the first stretch
    of every run used the *previous* run's objective), and a typo'd objective name pinned the old one
    for the rest of the session.
  - **"Re-optimize gear now" ignored every mode lock except quests.** Pressing it during a titan
    window equipped your main set over the kill set — on a real, non-autokill titan that strips exactly
    the Power/Toughness the fight needs.
  - **Accessories silently refused to swap.** The game reverts an accessory swap when committed
    energy/magic/R3 exceeds the new cap, and reports it through a tooltip the advisor never sees. The
    advisor was not releasing Basic Training energy before swapping, so any accessory that lowered the
    energy cap quietly bounced back. Every loadout swap was affected, not just titans.
- **Blood rituals drank the NGU marathon's magic.** During NGU MARATHON the ritual lane sat ahead of the
  surplus absorbers and took the whole remaining pool — measured at 245.7B magic while the NGUs starved.
  Now bounded to a slice of the surplus (26.6B measured, rituals still funded).
- **Favored MacGuffin reset itself** a few seconds after being picked — every selection was being
  rewritten to an invalid id.
- **A beard list longer than your unlocked slots** put the profile into a silent retry loop, re-equipping
  beards every tick forever. Both beard and digger lists now truncate to the slots you actually have, and
  the entries past the cut start running as you unlock more.
- Durations no longer render fractional seconds (`13.333333333333332s`).
- Item-ID inputs were too narrow to show their own placeholder ("ite", "gea").

### Added

- **Every loadout shows what arms it.** Each mode carries its real automation switches inline, plus a
  line naming which one is off and why nothing is happening. The same state appears as a chip on each
  system's own page (Titans, Gold, Quests, Yggdrasil, Cooking).
- **A Main / idle gear objective**, with the set the optimiser would equip. A profile gear breakpoint
  computes its picks, equips them and discards them, so `Optimize: NGUs` in a timeline previously showed
  you nothing at all.
- **Fill from objective** writes the optimiser's best set straight into a loadout's item list — the
  answer to "how do I find an item ID". Choosing an objective auto-fills an *empty* list; replacing a
  curated one stays an explicit button press.
- **Last gear swap** explains itself in the three outcomes that actually differ: swapped, **kept** (a
  slot this objective scores nothing for, which is where your Power/Toughness survives a Gold Drops
  swap), and **didn't fit**. Only the last is a fault.
- **Re-optimise when new gear drops**, on by default. It only re-checks sooner; it can never stop gear
  from moving.
- **The auto profile's segment plan is back on Current stage** — `TM HOUR › AT HOUR › RECOVERY ›
  NGU MARATHON` with the current step marked. A manual profile is named instead. This was lost in 2.0.0.
- **Poop advice is back on the orchard** — also lost in 2.0.0 — showing both where poop is and where the
  advisor would put it.
- **Boost time-to-cap**, per row and in total, on the priority list.
- **Drag to reorder** every ordered list: boost priority, boost type priority, the loadouts, wish
  priorities, and digger/beard breakpoints. The arrows remain for keyboard use.
- **Digger and beard breakpoints are drag lists** with named slots, and diggers take an **activation
  count** — list your top two, run four, let the advisor pick the rest.
- **Completed campaign blocks fold away** behind a "Completed campaigns" header.

### Changed

- The Settings gear toggle was mislabelled: `ManageGear` is the automation gate, and labelling it
  "Advisor gear refresh" hid the decisions toggle entirely — both must be on for gear to move.
- The boost type priority list lost its Add control; all three types are always present, so it could
  never add anything.

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
