# Building NGU Advisor

## Prerequisites
- **.NET SDK** (9.x is fine) — installed via `winget install Microsoft.DotNet.SDK.9`.
  No Visual Studio needed; net48 reference assemblies come from the
  `Microsoft.NETFramework.ReferenceAssemblies` NuGet package.
- **NGU Idle installed** (Unity 2019.4 / Mono). The `.csproj` references its assemblies at:
  `D:\SteamLibrary\steamapps\common\NGU IDLE\NGUIdle_Data\Managed\`
  If your install path differs, update the `<HintPath>` entries in `NGUAdvisor/NGUAdvisor.csproj`.

## Why net48 (do not "upgrade")
The DLL is injected into NGU Idle's Unity 2019.4 **Mono (.NET 4.x)** runtime and must be a
.NET Framework 4.x assembly. A modern .NET build cannot be loaded by that runtime. See `PLAN.md`.

## Build
```
dotnet build "NGUAdvisor/NGUAdvisor.csproj" -c Release
```
Output: `NGUAdvisor/bin/Release/net48/NGUAdvisor.dll` (single self-contained DLL).

## WinForms resources — important
The `.resx` are **not** compiled by the SDK at build time. The SDK's resource generator emits the
"preserialized" format, which needs `System.Resources.Extensions.dll` at runtime — that assembly
does not exist in the game's Mono domain, so the settings form would crash on open.

Instead we pre-generate **classic** `.resources` (the format Mono reads natively) and embed those:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/convert-resx.ps1
```

This must run under **Windows PowerShell 5.1** (`powershell.exe`), not `pwsh`/PowerShell 7,
because it relies on .NET Framework's classic `ResXResourceReader`/`ResourceWriter`.

Run it whenever you change `SettingsForm.resx` (edit the form in a designer, then regenerate),
then rebuild. The generated `SettingsForm.resources` is what actually gets embedded.

`SettingsForm.dje.resx` is a dead leftover (no code loads it) and is intentionally not embedded.

## Deploy
Copy `NGUAdvisor/bin/Release/net48/NGUAdvisor.dll` over `NGU/injector/NGUAdvisor.dll`,
keeping the existing `smi.exe`, `SharpMonoInjector.dll`, and `Run NGU Advisor (no hot-reload).bat`. Then run `Run NGU Advisor (no hot-reload).bat`
with NGU Idle open.

## Reverting the build system
The original legacy (VS-style) project is preserved as `NGUAdvisor/NGUAdvisor.csproj.legacy`.
