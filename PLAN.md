# NGUAdvisor — Status & Plan

_Living status doc for the "NGUAdvisor Updates" effort. Updated 2026-07-05._
_Fine-grained per-build history lives in the assistant's project memory; this is the shape of the thing._

## 0. Governing constraints (read first)

NGU Idle runs on **Unity 2019.4.22f1 + Mono (BleedingEdge) + .NET 4.x**. The injector is a Mono
injection payload (`NGUAdvisorBootstrap` byte-loads `NGUAdvisor.dll` into the game's Mono runtime).

> **The injected DLL MUST be .NET Framework 4.x (net48). A modern .NET build cannot be loaded.**

Hard rules learned the hard way:
- **Main-thread only.** Watcher/timer/background threads never touch Unity or WinForms — set a
  volatile flag, drain it in `Update()`.
- **No silent data loss.** The profile model types only what it edits; everything else passes
  through verbatim (`Extras` + `IsCommentKey` denylist). Round-trip tested against all 57 profiles.
- **UI is machine-enforced.** `UiLayout` is the law: `Row`/`WrapRow` placement, `MeasureText`
  (TextRenderer — the GDI engine labels actually paint with), `FitOrGrow` (no silent ellipsis:
  text wraps and containers grow), `Audit` at Shown (debug.log "UI AUDIT" lines must be clean).
  AutoScroll content lays out to width−20 (scrollbar allowance). Only Mono-proven controls.

## 1. Shipped / done

- ✅ **SDK-style net48 build** (no Visual Studio): `dotnet build`, classic `.resources`
  pre-generated via Windows PowerShell 5.1 (`build/convert-resx.ps1`).
- ✅ **Full visual profile editor** (F9 / EDIT buttons): all 11 profile systems editable, themed,
  crash- and data-loss-hardened. Gear breakpoints have a **GEAR SOURCE picker** — Manual item IDs
  or "Optimize: <objective>" (live optimization), plus top-respawn and challenge tags.
- ✅ **Native gear optimizer (route C3)** — scoring validated against gmiclotte's tool to 10 sig
  figs; coordinate-ascent + greedy accessories; mode loadouts (titan/gold/quest/ygg/cooking/
  shockwave) optimize live via objectives; advisor **gear refresh** re-optimizes every 30s pass;
  **offhand reads the game's `weapon2Factor()` live** (was hardcoded 100%).
- ✅ **Advisor suite** — auto-profile allocation generator (segments TM HOUR → AT HOUR → RECOVERY
  → NGU MARATHON), titan kill ladder + version forcing (per-version Beast kills read from
  achievements 148–151), gold pipeline w/ titan-bank coordination, digger/beard laws, NGU value
  ranking, boost-farm zone table, money-pit planner, quest strategy + capstone holds, blood/pill
  worth gates, level planner (AT/TM caps), EXP custom-plan mirror, LSC opportunity advisor.
- ✅ **M1 "Control Room" GUI** (fixed 1240×760): left rail with per-section health dots and an
  **accordion sub-nav** (children grow downward under the active section) — Advisors[Status ·
  Top Actions] · Combat · Economy · Systems[Yggdrasil · Quests · Boosts · Inventory · Cooking] ·
  Loadouts[6 modes] · Logs[Advisor · Loot · Session] · Settings · Cards[Cards · Wishes].
  Advisors home = 12-light board → AUTO PROFILE card → self-sizing CHALLENGES block. LOGS reads
  the advisor feed, a live loot.log mirror, and inject.log. Combat/Economy carry log slivers
  (combat.log / pitspin.log). Legacy tab control hidden but alive for bindings; only Cards/Wishes
  remain legacy (unlock-gated by design).

## 2. Open / next

- [ ] **LSC calibration** — compare the advisor's finish estimate against a real run.
- [ ] **T6 spawn validation batch** — one spawn confirms: v2 forcing, Adventure kill gear,
      Defensive posture, beast off, DC-in/PP-out digger window (evidence lands in COMBAT LOG).
- [ ] **Pre-Evil prep** — re-extract boost-farm zone table for Rooted/evil zones; T7+ have no
      per-version kill records (only V4 achievements) → rethink version detection there.
- [ ] **NGUAdvisors tick-rate calibration** — compare ×/hr against the GO site (50 ticks/s
      constant unverified; ranking is constant-free).
- [ ] (Optional) gear hard caps, if any objective's picks ever diverge from the website.

## 3. Build & deploy

```
dotnet build "NGUAdvisor-src/NGUAdvisor/NGUAdvisor.csproj" -c Release
# copy the newest bin/Release/net48/NGUAdvisor.r<timestamp>.dll → NGU/injector/NGUAdvisor.dll
```

- The assembly name carries a per-build timestamp so the bootstrap can hot-reload it
  (`Reload Injector` on the Settings section). A cold start needs the game open + `Run NGU Advisor (no hot-reload).bat`.
- Bump `Main.BuildTag` every build — it shows in the rail footer.
- Logs: `%userprofile%\AppData\LocalLow\NGUAdvisor\logs\` (inject/debug/loot/combat/pitspin/cards).
- After any UI change: check debug.log for "UI AUDIT … clean" lines before calling it done.
