#!/usr/bin/env bash
#
# package-release.sh — build a runnable NGU Advisor release zip from THIS (public) source tree.
#
# Produces  dist/NGUAdvisor_<version>.zip  containing:
#   Advisor Launcher.exe        (iconned launcher — direct-inject, no bootstrap / hot-reload)
#   Run NGU Advisor.bat         (.bat fallback, same direct-inject)
#   injector/NGUAdvisor.dll     (freshly built from this tree)
#   injector/companion/         (self-contained WebView2 companion UI; auto-launched by the advisor)
#   injector/SharpMonoInjector.dll, injector/smi.exe   (third-party injector tools)
#   sampleprofiles/             (Normal / Evil / Sadistic)
#
# The DLL is built from the PUBLIC source on purpose: it has no Reload button and no
# bootstrap, so what ships matches what people can build here. Run this on the maintainer's
# machine, where the game assemblies (csproj HintPath) and injector tools exist.
#
# Usage:
#   ./package-release.sh                 # version read from NGUAdvisor/Main.cs
#   ./package-release.sh 1.0.1           # explicit version
#   NGU_RUNTIME=/path/to/NGU ./package-release.sh 1.0.1
#
# After it runs, publish with the printed gh commands.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CSPROJ="$ROOT/NGUAdvisor/NGUAdvisor.csproj"
REPO="Glowey-Glow/NGUAdvisor"

# Injector tools live in the maintainer's runtime folder (a sibling of this repo by default), because
# they are third-party binaries this repo deliberately does not track. Override with env vars if your
# layout differs.
RUNTIME="${NGU_RUNTIME:-$ROOT/../NGU}"
TOOLS="${NGU_TOOLS:-$RUNTIME/injector}"

# Sample profiles come from THE REPO, not the runtime folder. They used to be copied from
# $RUNTIME/sampleprofiles, which meant the release shipped whatever was in an untracked directory on one
# machine: through 2.0.1 that was the pre-repair copies, plus cblock3-evil.json and cblock4.json, two
# superseded files that nest `Challenges` inside a Rebirth breakpoint where the loader never sees it — so
# they load with zero challenges and say nothing. Shipping from the repo means what users get is what is
# reviewed here.
PROFILES="${NGU_PROFILES:-$ROOT/NGUAdvisor/SampleProfiles}"

# Version: first arg, else the Version const in Main.cs.
VERSION="${1:-$(grep -oE 'Version = "[^"]+"' "$ROOT/NGUAdvisor/Main.cs" | head -1 | sed -E 's/.*"([^"]+)".*/\1/')}"
[ -n "$VERSION" ] || { echo "ERROR: could not determine version"; exit 1; }

OUT="$ROOT/dist"
STAGE="$OUT/NGUAdvisor-$VERSION"
ZIP="$OUT/NGUAdvisor_$VERSION.zip"

echo "==> NGU Advisor release packager"
echo "    version : $VERSION"
echo "    runtime : $RUNTIME"

# --- sanity: required tools present -----------------------------------------
for f in "$TOOLS/smi.exe" "$TOOLS/SharpMonoInjector.dll"; do
  [ -f "$f" ] || { echo "ERROR: missing injector tool: $f (set NGU_TOOLS)"; exit 1; }
done
[ -d "$PROFILES" ] || { echo "ERROR: missing sample profiles: $PROFILES (set NGU_PROFILES)"; exit 1; }

# --- build -------------------------------------------------------------------
echo "==> Building NGUAdvisor (Release)..."
dotnet build "$CSPROJ" -c Release -v quiet
DLL="$(ls -t "$ROOT/NGUAdvisor/bin/Release/net48/"NGUAdvisor.r*.dll 2>/dev/null | head -1)"
[ -f "$DLL" ] || { echo "ERROR: build produced no NGUAdvisor.r*.dll"; exit 1; }
echo "    built: $(basename "$DLL")"

echo "==> Building Advisor Launcher (Release)..."
dotnet build "$ROOT/NGUAdvisorLauncher/NGUAdvisorLauncher.csproj" -c Release -v quiet
LAUNCHER="$ROOT/NGUAdvisorLauncher/bin/Release/net48/Advisor Launcher.exe"
[ -f "$LAUNCHER" ] || { echo "ERROR: launcher build produced no 'Advisor Launcher.exe'"; exit 1; }

echo "==> Publishing Companion (Release, self-contained win-x64)..."
COMPANION_PUB="$ROOT/NGUAdvisorCompanion/bin/Release/net8.0-windows/win-x64/publish"
rm -rf "$COMPANION_PUB"
dotnet publish "$ROOT/NGUAdvisorCompanion/NGUAdvisorCompanion.csproj" -c Release -r win-x64 --self-contained true -v quiet
[ -f "$COMPANION_PUB/NGUAdvisorCompanion.exe" ] || { echo "ERROR: companion publish produced no NGUAdvisorCompanion.exe"; exit 1; }

# --- stage -------------------------------------------------------------------
echo "==> Staging $STAGE ..."
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE/injector"

# single direct-inject launcher (CRLF line endings for cmd.exe)
# It MUST write injector-path.txt: the advisor is byte-loaded by smi, so Assembly.Location is empty and
# that file is the only way it can find injector\companion\NGUAdvisorCompanion.exe (auto-launch + F1).
# Keep in sync with Advisor Launcher.exe (NGUAdvisorLauncher/Program.cs), which writes the same file.
{ cat <<'BAT'
@setlocal enableextensions
pushd "%~dp0"

rem The advisor is loaded from memory and cannot know where it lives - hand it our injector path so it
rem can launch the companion UI (injector\companion\NGUAdvisorCompanion.exe; F1 reopens it in-game).
if not exist "%USERPROFILE%\AppData\LocalLow\NGUAdvisor" mkdir "%USERPROFILE%\AppData\LocalLow\NGUAdvisor"
<nul set /p="%~dp0injector"> "%USERPROFILE%\AppData\LocalLow\NGUAdvisor\injector-path.txt"

.\injector\smi.exe inject -p NGUIdle -a .\injector\NGUAdvisor.dll -n NGUAdvisor -c Loader -m Init

popd
BAT
} | sed 's/$/\r/' > "$STAGE/Run NGU Advisor.bat"

cp "$DLL" "$STAGE/injector/NGUAdvisor.dll"
cp "$TOOLS/SharpMonoInjector.dll" "$TOOLS/smi.exe" "$STAGE/injector/"
cp -r "$PROFILES" "$STAGE/sampleprofiles"

# iconned launcher (primary) — the .bat above stays as a fallback
cp "$LAUNCHER" "$STAGE/Advisor Launcher.exe"

# self-contained WebView2 companion, auto-launched by the advisor from injector/companion/
mkdir -p "$STAGE/injector/companion"
cp -r "$COMPANION_PUB/." "$STAGE/injector/companion/"
find "$STAGE/injector/companion" -type f \( -iname '*.pdb' -o -iname '*.xml' \) -delete

# --- guard: both entry points must write injector-path.txt (else no companion) ----
grep -q 'injector-path.txt' "$STAGE/Run NGU Advisor.bat" \
  || { echo "ERROR: staged .bat does not write injector-path.txt - companion would never launch"; exit 1; }
# (.NET stores literals as UTF-16 in the #US heap, so strip NULs before grepping the exe)
if ! tr -d '\000' < "$STAGE/Advisor Launcher.exe" | grep -aq 'injector-path.txt'; then
  echo "ERROR: 'Advisor Launcher.exe' does not write injector-path.txt - companion would never launch"
  exit 1
fi

# --- guard: never ship the bootstrap, game assemblies, or backups ------------
if find "$STAGE" \( -iname '*Bootstrap*' -o -iname 'Assembly-CSharp.dll' -o -iname '*.bak*' -o -iname '*.orig' \) | grep -q .; then
  echo "ERROR: forbidden file staged:"; find "$STAGE" \( -iname '*Bootstrap*' -o -iname 'Assembly-CSharp.dll' -o -iname '*.bak*' -o -iname '*.orig' \)
  exit 1
fi

# --- zip (Windows-friendly) --------------------------------------------------
echo "==> Zipping..."
STAGE_WIN="$(cygpath -w "$STAGE")"
ZIP_WIN="$(cygpath -w "$ZIP")"
powershell.exe -NoProfile -Command "Compress-Archive -Path '$STAGE_WIN' -DestinationPath '$ZIP_WIN' -CompressionLevel Optimal -Force"

echo ""
echo "==> Done: $ZIP  ($(du -h "$ZIP" | cut -f1))"
echo ""
echo "Publish a NEW release:"
echo "  gh release create v$VERSION \"$ZIP\" --repo $REPO --title \"NGU Advisor v$VERSION\" --notes-file <notes.md>"
echo ""
echo "Or refresh an EXISTING release's asset:"
echo "  gh release upload v$VERSION \"$ZIP\" --repo $REPO --clobber"
