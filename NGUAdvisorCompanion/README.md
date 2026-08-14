# NGU Advisor — Modern UI Companion (M1)

Out-of-process WebView2 host for the **Focus** modern UI. It renders a live, **read-only** dashboard of
advisor state that the injected DLL publishes over the named pipe `NGUAdvisorUI`. Advisor logic is
untouched; this app never writes saves and never drives the game (commands arrive in **M2**).

This is a **separate** project on modern .NET (net8.0-windows) because the game's Unity/Mono runtime
cannot load a modern-.NET assembly in-process. It is intentionally **not** part of `NGUAdvisor.sln` and
its build never touches the injector DLL.

## Architecture (M1)

```
INJECTOR (Mono/net48, in game)         COMPANION (net8, this app)        WEB UI (Focus HTML)
  Managers/UiBridge.cs                   PipeClient.cs                     wwwroot/index.html
  • builds a JSON snapshot on the        • pipe client, reconnects         • applySnapshot(e.data)
    Unity main thread each ~1s           • relays line -> webview            renders Overview live
  • bg thread writes it to the pipe  ══► • PostWebMessageAsJson         ══► • browser-dev fallback keeps
    "NGUAdvisorUI" (write-only in M1)      WebMessageReceived -> pipe          the mock when unhosted
```

## Build

```
dotnet build NGUAdvisorCompanion/NGUAdvisorCompanion.csproj -c Debug
```

Needs the **WebView2 Evergreen runtime** (ships with Windows 11; on Windows 10 install it once). The
`Microsoft.Web.WebView2` SDK is restored from NuGet; `wwwroot/` is copied next to the exe.

## Deploy — use `build/deploy.ps1`, not a Release build of this project

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/deploy.ps1
```

A Release build of this project alone runs `DeployCompanion`, which **fails** (`MSB3021 ... used by
another process`) whenever the companion is running — its normal state, since the injector
auto-launches it — and it fails *after* copying `wwwroot`, so you get a partial deploy with a FAILED
build. `build/deploy.ps1` stops the companion, ships this project **and** the advisor DLL, verifies
both landed, and restarts it. See `BUILD.md` and `audit/41-zone-phases-campaign.md` §7.3.

## Run (M1 end-to-end)

1. Build the injector **Release** so it deploys, and **Reload Advisor** in-game so the running DLL has
   `UiBridge` (it opens the `NGUAdvisorUI` pipe server on load):
   ```
   dotnet build NGUAdvisor/NGUAdvisor.csproj -c Release
   ```
2. Launch this companion (order doesn't matter — it reconnects until the injector is up):
   ```
   dotnet run --project NGUAdvisorCompanion -c Debug
   ```
   or run `NGUAdvisorCompanion/bin/Debug/net8.0-windows/NGUAdvisorCompanion.exe`.

The window title shows **"NGU Advisor — connecting…"** until the pipe connects, then the **Overview**
renders live and updates each tick: automation state, profile, difficulty, next-loop countdown, current
stage/goal, the severity-ranked "what needs you" list, the instruments row (resources, titan atk/def,
boost-farm zone-vs-ITOPOD, growth rate), the top growth-rate strip, and the log drawer feed.

## What's live in M1 vs. later

- **Live now:** the **Overview** page + top bar + growth strip + log drawer, read-only, updating ~1/s.
- **Still mock (by design):** the System View pages (Titans/Resources/Boosts/Gold/…) show the design's
  sample content. They get real per-system data in **M3**. Their toggles/segments are inert until **M2**.
- **Design fallback:** open `wwwroot/index.html` directly in a browser and the live layer stays dormant,
  so the authored Focus mock still renders for design iteration.

## Design source

`wwwroot/index.html` is the locked **Focus** mockup with a `<script>` "M1 live layer" appended: it adds
`id`s to the Overview nodes and renders them from incoming `window.chrome.webview` messages. The pipe
protocol is v1 (see `NGUAdvisor_UI_Plan.md`).
