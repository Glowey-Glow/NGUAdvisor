# NGU Advisor - the text-level guards. Two things nothing in this repo was measuring.
#
#   pwsh -NoProfile -File build/check-tree.ps1
#   (or, from a PowerShell prompt at the repo root:  .\build\check-tree.ps1)
#
# --- ASCII ONLY, ON PURPOSE ------------------------------------------------------------------------
# Same rule as deploy.ps1, and check 3 below is the one that enforces it on all four scripts.
#
# --- WHY THIS EXISTS -------------------------------------------------------------------------------
# On 2026-08-07 `build\deploy.ps1` was found on `integration` with its merge conflict markers still
# in it, committed at 0594573 ("Merge branch 'fix/deploy-inventory' into integration"). Two further
# commits and a full test run passed over it. NOTHING CAUGHT IT, and the reason is exact:
#
#   NO PART OF THIS REPO PARSES POWERSHELL.
#
# The build was green and 1511 xunit + 216 jsdom tests passed, because none of them can see a .ps1.
# `.\build\deploy.ps1 -CheckOnly` failed at PARSE time with "The '<' operator is reserved for future
# use." - the deploy command had been unrunnable for three commits and the suite said nothing.
#
# That is audit/42's own subject - A GREEN RESULT THAT MEANS "NOT MEASURED" - occurring inside the
# fix for it, and it is the third recorded instance (41 5.2's camelCase grep that returned nothing
# and was read as absence; the SampleProfiles/sampleprofiles path collision recorded in 1f2402f,
# where a mirror compared every file to itself and reported a healthy "in sync").
#
# So these checks report their DENOMINATOR whether or not they find anything. "342 files scanned, 0
# markers" and "not measured" have to look different on the way past. Same reason
# deploy-sampleprofiles.ps1:300-302 always prints its checked count.
#
# --- WHAT THIS DOES NOT COVER ----------------------------------------------------------------------
# These are text and parse checks. They cannot see a deploy gap: audit/42 6 records that CI cannot
# build NGUAdvisor.csproj at all (the Unity HintPaths do not exist on a runner), so no deploy gap in
# that document is detectable by CI, by construction. Nothing here changes that. A green run of this
# script means "the scripts parse and no marker is committed", and nothing whatsoever about whether
# the product shipped. Do not widen that reading.

[CmdletBinding()]
param(
    # Print every file scanned, not just the failures and the summary.
    [switch]$Verbose_
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$failures = @()

function Note($msg) { Write-Host $msg }

# --- the file list ---------------------------------------------------------------------------------
# `git ls-files` on purpose rather than a directory walk: the defect class is a COMMITTED marker, and
# the tracked set is exactly that question. It also excludes bin\, obj\ and node_modules\ for free -
# a directory walk finds conflict markers in vendored sources and NuGet caches and cries wolf.
Push-Location $repo
try {
    $tracked = @(& git ls-files 2>$null)
    $gitOk = ($LASTEXITCODE -eq 0 -and $tracked.Count -gt 0)
} catch {
    $gitOk = $false
}
Pop-Location

if (-not $gitOk) {
    Write-Host 'CHECK FAILED: `git ls-files` produced nothing, so NOTHING WAS SCANNED.' -ForegroundColor Red
    Write-Host '  This check refuses to pass on an empty file list. An empty sweep is the exact' -ForegroundColor Red
    Write-Host '  "green means not measured" shape it exists to prevent. Is this a git checkout?' -ForegroundColor Red
    exit 1
}

# --- 1. conflict markers ---------------------------------------------------------------------------
# A line that STARTS with seven '<', '|', '=' or '>' is a merge conflict marker and is never
# intentional in a committed file. Verified against this tree before the rule was written: exactly
# zero tracked lines match once deploy.ps1 is resolved, including the 8 tracked .md files - a setext
# markdown h1 underline of exactly seven '=' would collide with the '=======' form, and none exists.
#
# The patterns are BUILT rather than written out, so this file does not contain a seven-character run
# of any of them and therefore does not flag itself. That is not a cute trick; a guard that trips on
# its own source gets deleted the first time it fires.
$mkOurs   = ('<' * 7)
$mkBase   = ('|' * 7)
$mkSplit  = ('=' * 7)
$mkTheirs = ('>' * 7)
$markerRe = '^(' + [regex]::Escape($mkOurs) + '|' + [regex]::Escape($mkBase) + '|' +
                   [regex]::Escape($mkTheirs) + ')( |$)|^' + [regex]::Escape($mkSplit) + '$'

$scanned = 0
$skippedBinary = 0
$parsed = 0

foreach ($rel in $tracked) {
    # `git ls-files` always emits forward slashes. Do NOT convert them to backslashes: this job runs
    # on ubuntu-latest, where '\' is an ordinary filename character, every Test-Path would miss, and
    # the sweep would scan zero files and report a pass. Windows accepts forward slashes here.
    $full = Join-Path $repo $rel
    if (-not (Test-Path -LiteralPath $full)) { continue }   # deleted-but-staged, or a submodule

    try { $bytes = [System.IO.File]::ReadAllBytes($full) } catch { continue }

    # Binary detection the way git does it: a NUL in the head of the file. Skips the .png/.ico assets
    # without a hardcoded extension allowlist that would silently stop covering a new text type.
    $head = [Math]::Min($bytes.Length, 8000)
    $isBinary = $false
    for ($i = 0; $i -lt $head; $i++) { if ($bytes[$i] -eq 0) { $isBinary = $true; break } }
    if ($isBinary) { $skippedBinary++; continue }

    $scanned++
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $lines = $text -split "`r`n|`n|`r"
    for ($n = 0; $n -lt $lines.Length; $n++) {
        if ($lines[$n] -match $markerRe) {
            $failures += ("CONFLICT MARKER  {0}:{1}  {2}" -f $rel, ($n + 1), $lines[$n])
        }
    }
    if ($Verbose_) { Note "  scanned $rel" }
}

# --- 2. every tracked .ps1 must PARSE --------------------------------------------------------------
# This is the check that would have caught 0594573 on the commit that made it. PSParser::Tokenize is
# the whole of it: no execution, no side effects, no Unity, no game - it reads text and reports the
# same errors the shell would have reported at launch.
#
# The type is asserted before it is used. PSParser ships in System.Management.Automation on every
# host this could run on - Windows PowerShell 5.1 and pwsh 7, Windows and Linux - but "the check
# quietly did nothing on the runner" is the precise failure this whole file exists to prevent, so an
# absent type is a LOUD failure and never a silent pass.
if (-not ('System.Management.Automation.PSParser' -as [type])) {
    Write-Host 'CHECK FAILED: PSParser is unavailable on this host, so NO SCRIPT WAS PARSED.' -ForegroundColor Red
    Write-Host ("  host: " + $PSVersionTable.PSVersion + " on " + [System.Environment]::OSVersion.Platform) -ForegroundColor Red
    Write-Host '  Refusing to report a pass on an unmeasured check. Use Language.Parser::ParseInput here.' -ForegroundColor Red
    exit 1
}

$psFiles = @($tracked | Where-Object { $_ -like '*.ps1' })
if ($psFiles.Count -eq 0) {
    Write-Host 'CHECK FAILED: no tracked .ps1 files were found, so the parse check measured NOTHING.' -ForegroundColor Red
    exit 1
}
foreach ($rel in $psFiles) {
    # `git ls-files` always emits forward slashes. Do NOT convert them to backslashes: this job runs
    # on ubuntu-latest, where '\' is an ordinary filename character, every Test-Path would miss, and
    # the sweep would scan zero files and report a pass. Windows accepts forward slashes here.
    $full = Join-Path $repo $rel
    if (-not (Test-Path -LiteralPath $full)) { continue }
    $errors = $null
    [void][System.Management.Automation.PSParser]::Tokenize((Get-Content -LiteralPath $full -Raw), [ref]$errors)
    $parsed++
    foreach ($e in $errors) {
        $failures += ("PARSE ERROR      {0}:{1}  {2}" -f $rel, $e.Token.StartLine, $e.Message)
    }
}

# --- the denominator assertions --------------------------------------------------------------------
# Everything above reports a problem it FOUND. These two report the case where it found nothing
# because it looked at nothing, which is the only way a check like this ever really fails. A path
# bug, a changed `git ls-files` output shape or a wrong working directory all land here rather than
# passing quietly - that mistake is what put the markers in deploy.ps1 through three commits.
if ($scanned -eq 0) {
    Write-Host ("CHECK FAILED: {0} tracked files, but NOT ONE was read." -f $tracked.Count) -ForegroundColor Red
    Write-Host '  The sweep measured nothing. Refusing to report a pass.' -ForegroundColor Red
    exit 1
}
if ($parsed -ne $psFiles.Count) {
    Write-Host ("CHECK FAILED: {0} tracked .ps1 files, but only {1} were parsed." -f $psFiles.Count, $parsed) -ForegroundColor Red
    Write-Host '  A script that could not be opened is not a script that passed.' -ForegroundColor Red
    exit 1
}

# --- 3. non-ASCII bytes in a .ps1 - REPORTED, NOT FAILED -------------------------------------------
# deploy.ps1's header documents this hazard first-hand: Windows PowerShell 5.1 reads a BOM-less
# script as Windows-1252, so a UTF-8 dash (E2 80 94) decodes to "a" + euro + a RIGHT DOUBLE QUOTATION
# MARK, and 5.1 accepts that last one as a STRING DELIMITER - the file then fails with a bogus
# "Missing closing '}'". Check 2 running under pwsh 7 (which assumes UTF-8) cannot see it.
#
# !! THIS IS A WARNING AND DOES NOT FAIL THE RUN, because the claim was MEASURED before it was
# enforced and the measurement did not support enforcing it. On 2026-08-07 all four tracked scripts
# were tokenized under BOTH hosts - Windows PowerShell 5.1.26100.8655 and pwsh 7 - and all four
# returned ZERO parse errors. build\decomp-diff.ps1 carries two UTF-8 em dashes (lines 1 and 6) and
# still parses clean under 5.1, because BOTH sit inside '#' comments: a comment runs to end of line,
# so a smart quote in one can never open a string. The hazard is real and the file is fine.
#
# Failing the build on that would make the guard's first act a red tick on a non-defect, which is how
# guards get deleted. It warns instead, and the warning is worth having: the same three bytes in CODE
# rather than a comment is the exact break deploy.ps1's header describes.
$warnings = @()
foreach ($rel in $psFiles) {
    # `git ls-files` always emits forward slashes. Do NOT convert them to backslashes: this job runs
    # on ubuntu-latest, where '\' is an ordinary filename character, every Test-Path would miss, and
    # the sweep would scan zero files and report a pass. Windows accepts forward slashes here.
    $full = Join-Path $repo $rel
    if (-not (Test-Path -LiteralPath $full)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($full)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $warnings += ("BOM              {0}:1  starts with a UTF-8 BOM; these scripts are written BOM-less ASCII." -f $rel)
    }
    $line = 1
    $hits = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 0x0A) { $line++; continue }
        if ($bytes[$i] -gt 127) {
            if ($hits -eq 0) {
                $warnings += ("NON-ASCII        {0}:{1}  first of several, byte 0x{2:X2}. Harmless in a comment; a break in code." -f $rel, $line, $bytes[$i])
            }
            $hits++
        }
    }
}

# --- 4. the advisor/page payload contract ----------------------------------------------------------
# WHY A SECOND UI CHECK, when BuildStamp already reports drift. BuildStamp compares TIMESTAMPS: the
# advisor's build time against the mtime of the index.html the companion serves, tolerance 24h. It
# never looks at content, so it cannot tell "the UI did not need to change" from "the UI needed to
# change and did not". On 2026-08-09 it had been reporting the UI stale for 69.8h during a week of
# pure advisor-logic work in which the page needed no change at all - a true reading of the clock and
# a false alarm about the product. Meanwhile TWO fields (bossNow, yggCap) had been emitted since
# 2026-07-24 and never rendered, and the clock had nothing to say about that because the page they
# were missing from was the current one.
#
# So this asks the question the clock cannot: does the page reference every key the advisor emits?
# It is a text check on two tracked files, no Unity and no game, which is why it can live here.
#
# WHAT COUNTS AS EMITTED: an assignment into a JSON object under construction, `<obj>["<key>"] =`.
# That deliberately includes NESTED objects (goal["bossNow"], mp["throw"]) and not just root[...] -
# a narrower scan over root[...] alone was run first and found 2 orphans where there are 16.
#
# WHAT COUNTS AS REFERENCED: `.key` not followed by a word character, or the key as a quoted literal.
# The word-boundary form is load-bearing: a plain substring test passes `.text` on `.textContent` and
# would rubber-stamp most of the short key names in here.
#
# THE BASELINE IS THE POINT. Sixteen keys are unrendered TODAY, all of them real payload data rather
# than extraction noise. Failing on those would make this guard's first act a red tick on pre-existing
# debt, which is how guards get deleted (same reasoning as check 3, same conclusion, different
# remedy). So the known set is listed and warns, and ANYTHING NOT ON THE LIST FAILS - which is the
# entire value: a new field wired into the advisor and forgotten in the page is caught on the commit
# that adds it. The list is also checked for entries that have since been rendered, so it cannot rot
# into a permanent excuse: resolving one and leaving it here is itself reported.
$knownOrphans = @(
    # `msg` is the ONE that is not a missing field. PumpFeed() builds {t, who, msg, detail} into a
    # capped ring and ships it every tick, and the page's renderFeed() was DELETED - index.html says so
    # itself: "F6: renderFeed() used to sit here. It read s.feed and wrote it into #flist - an element
    # this document [no longer has]". So an entire shipped feature is built and discarded. Restoring a
    # feed surface or retiring PumpFeed is a product decision, not a render, which is why it stays here
    # while the other fifteen were resolved.
    #
    # ⚠ AND IT MARKS THIS CHECK'S BLIND SPOT. `t`, `who` and `detail` are just as unrendered, and do NOT
    # appear here, because those short strings occur coincidentally elsewhere in a 600 KB document and
    # the word-boundary test passes them. This list is a FLOOR, not a census: short and common key names
    # are where it under-reports. Do not read an empty baseline as "every key is rendered".
    'msg'
)

$bridgeRel = 'NGUAdvisor/Managers/UiBridge.cs'
$pageRel   = 'NGUAdvisorCompanion/wwwroot/index.html'
$bridgeFull = Join-Path $repo $bridgeRel
$pageFull   = Join-Path $repo $pageRel

# Either file missing is a LOUD failure, never a silent pass. A renamed UiBridge or a moved wwwroot
# would otherwise make this check measure zero keys and report success - the exact shape of the three
# incidents in this file's header.
if (-not (Test-Path -LiteralPath $bridgeFull)) {
    Write-Host ("CHECK FAILED: {0} not found, so NO payload key was extracted." -f $bridgeRel) -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $pageFull)) {
    Write-Host ("CHECK FAILED: {0} not found, so the payload contract was NOT measured." -f $pageRel) -ForegroundColor Red
    exit 1
}

$bridgeText = [System.IO.File]::ReadAllText($bridgeFull)
$pageText   = [System.IO.File]::ReadAllText($pageFull)

$payloadKeys = @([regex]::Matches($bridgeText, '(\w+)\["(\w+)"\]\s*=') |
                 ForEach-Object { $_.Groups[2].Value } | Sort-Object -Unique)

if ($payloadKeys.Count -eq 0) {
    Write-Host ("CHECK FAILED: no payload keys matched in {0}, so the contract measured NOTHING." -f $bridgeRel) -ForegroundColor Red
    Write-Host '  The emit pattern is `<obj>["<key>"] =`. If UiBridge stopped building JSON that way,' -ForegroundColor Red
    Write-Host '  this check needs rewriting, not deleting - it is reporting that it cannot see.' -ForegroundColor Red
    exit 1
}

$unrendered = @()
foreach ($k in $payloadKeys) {
    $e = [regex]::Escape($k)
    $seen = ($pageText -match ('\.' + $e + '(?!\w)')) -or
            ($pageText -match ('"' + $e + '"')) -or
            ($pageText -match ("'" + $e + "'"))
    if (-not $seen) { $unrendered += $k }
}

foreach ($k in $unrendered) {
    if ($knownOrphans -contains $k) {
        $warnings += ("UNRENDERED KEY   {0}  emitted by UiBridge, never referenced by the page (known backlog)." -f $k)
    } else {
        $failures += ("UNRENDERED KEY   {0}:?  `"{1}`" is emitted into the payload and the companion page never reads it." -f $bridgeRel, $k)
    }
}

# A baseline that only ever grows is an excuse. Report entries that no longer need to be here.
$staleBaseline = @($knownOrphans | Where-Object { $unrendered -notcontains $_ })
foreach ($k in $staleBaseline) {
    $warnings += ("BASELINE STALE   {0}  is rendered now - remove it from `$knownOrphans in build/check-tree.ps1." -f $k)
}

# --- the report ------------------------------------------------------------------------------------
# The denominator prints ALWAYS, pass or fail. See the header: this is the entire point.
Note ''
Note ("checked  {0} tracked text files for conflict markers ({1} binary skipped)" -f $scanned, $skippedBinary)
Note ("         {0} PowerShell scripts parsed and byte-checked: {1}" -f $psFiles.Count, ($psFiles -join ', '))
Note ("         {0} payload keys from {1} against {2}: {3} rendered, {4} not ({5} known, {6} new)" -f `
      $payloadKeys.Count, (Split-Path $bridgeRel -Leaf), (Split-Path $pageRel -Leaf),
      ($payloadKeys.Count - $unrendered.Count), $unrendered.Count,
      @($unrendered | Where-Object { $knownOrphans -contains $_ }).Count,
      @($unrendered | Where-Object { $knownOrphans -notcontains $_ }).Count)

if ($warnings.Count -gt 0) {
    Note ''
    foreach ($w in $warnings) { Write-Host "  WARN  $w" -ForegroundColor Yellow }
    Note ('  ' + $warnings.Count + ' warning(s). These do NOT fail the run - see check 3 in this file for why.')
}

if ($failures.Count -gt 0) {
    Note ''
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    Note ''
    Write-Host ("CHECK FAILED: {0} problem(s)." -f $failures.Count) -ForegroundColor Red
    exit 1
}

Note ''
if ($warnings.Count -gt 0) {
    Write-Host ("CHECK PASSED: no conflict markers, every script parses, no NEW unrendered payload key. {0} warning(s) above." -f $warnings.Count) -ForegroundColor Green
} else {
    Write-Host 'CHECK PASSED: no conflict markers, every script parses, every script is BOM-less ASCII, every payload key is rendered.' -ForegroundColor Green
}
exit 0
