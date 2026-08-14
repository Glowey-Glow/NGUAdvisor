# NGU Advisor - the SampleProfiles mirror. Makes NGU\sampleprofiles\ equal NGUAdvisor\SampleProfiles\,
# and never destroys a file without a copy of it first.
#
#   .\build\deploy-sampleprofiles.ps1              apply the mirror
#   .\build\deploy-sampleprofiles.ps1 -CheckOnly   report drift, change nothing, exit 1 if drifted
#
# build\deploy.ps1 calls this as its last step. It is a separate file so it can be run - and tested -
# without a build, without the injector directory, and without touching either DLL.
#
# --- ASCII ONLY, ON PURPOSE ------------------------------------------------------------------------
# Same rule as deploy.ps1: no BOM, and Windows PowerShell 5.1 reads a BOM-less script as Windows-1252,
# so a UTF-8 box-drawing dash decodes to a smart quote and the parse dies on a bogus "Missing '}'".
# Keep every character 7-bit ASCII.
#
# --- WHY THIS EXISTS (audit/42 5, ranked #1 in 9) --------------------------------------------------
# SampleProfiles appears in NO .csproj. It is not embedded, not copied, not built. The deploy was a
# human with a mouse, and it last happened 2026-07-02. Measured 2026-08-06 against 49 repo files, the
# operator's NGU\sampleprofiles\ held 57: 18 current, 30 stale, 1 never copied, and 9 files the repo had
# DELETED still sitting there - among them cblock4.json, which the codebase already knows is broken.
# CampaignTables.cs:357 names it by path ("Challenges nested inside Breakpoints.Rebirth, loads as zero
# ... Delete it rather than copying it in"), and NGUAdvisor-public/package-release.sh:36-41 records that
# shipping from that untracked runtime folder is what put it in public releases through 2.0.1. The
# public side was fixed by shipping from the repo. The operator's own reference folder never was.
#
# Nothing reads NGU\sampleprofiles\ at runtime, so the blast radius is bounded: this is the folder the
# OPERATOR copies from. That is why it is a mirror and not a merge - a copy that only adds leaves
# cblock4.json exactly where it is, which is the entire defect.
#
# --- THE POLICY IS PresetInstallPlan'S, PLUS A DELETE SIDE -----------------------------------------
# Managers/PresetInstallPlan.cs decided this once already, for the embedded presets, and the reasoning
# transfers whole: a file in a folder the operator edits is USER DATA, "never overwrite" is a one-way
# valve that strands every future fix, and the way out is a manifest recording the hash of what THIS
# tool last wrote. That single fact separates "our file, untouched" from "the operator's file now".
#
#   not on disk                              -> Install
#   on disk == repo                          -> AlreadyCurrent    (nothing written; hash recorded)
#   no manifest record, content differs      -> BackupThenInstall (the one-time migration)
#   manifest record == disk, repo moved      -> UpdateInPlace     (our file, untouched: deliver the fix)
#   manifest record != disk                  -> PreserveUserEdit  (hand-edited: the operator wins)
#   on disk, NOT in the repo                 -> BackupThenRemove  (the mirror's delete side)
#
# BackupThenRemove is the verdict PresetInstallPlan has no equivalent of, because the presets are a set
# that only grows into a folder full of other people's files, and this is a mirror of a tree. It is the
# only branch that deletes, so it ALWAYS copies to _backup first and ALWAYS names the file and its
# backup path in the report. Nothing here is silent.
#
# The hash folds CRLF to LF before hashing, for PresetInstallPlan's reason and one more of its own: git
# checks this tree out with `* text=auto`, so on this machine EVERY one of the 48 shared files differs
# at the byte level and NONE of them differs as text. A byte comparison would call the whole folder
# stale forever and back up 48 files on every single run.

[CmdletBinding()]
param(
    # Defaults resolve to <repo>\NGUAdvisor\SampleProfiles and <repo>\..\NGU\sampleprofiles - the same
    # ..\..\NGU that every deploy target in this repo computes. Overridable so the tests can run the
    # REAL script against a scratch tree; see tests\NGUAdvisor.Tests\SampleProfileMirrorTests.cs.
    [string]$Source,
    [string]$Target,

    # Report what the mirror WOULD do and change nothing. Exit 1 if the two trees disagree. This is the
    # detector: before it there was no test, no gate, no log line and no build step on this artifact.
    [switch]$CheckOnly,

    # Suppress the per-file lines; keep the one-line summary. deploy.ps1 does not use this.
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$ManifestFileName = '_deployed-samples.manifest'
$BackupFolderName = '_backup'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repo 'NGUAdvisor\SampleProfiles' }
if (-not $Target) { $Target = [System.IO.Path]::GetFullPath((Join-Path $repo '..\NGU\sampleprofiles')) }

function Say($msg) { if (-not $Quiet) { Write-Host $msg } }

# SHA-256 over the TEXT with CRLF folded to LF, byte-for-byte the same function as
# PresetInstallPlan.Hash. ReadAllText strips a BOM the way that class's StreamReader does.
function Get-SampleHash([string]$path) {
    $text = [System.IO.File]::ReadAllText($path)
    $text = $text.Replace("`r`n", "`n")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
        $sb = New-Object System.Text.StringBuilder
        foreach ($b in $bytes) { [void]$sb.Append($b.ToString('x2')) }
        return $sb.ToString()
    } finally { $sha.Dispose() }
}

# Relative paths, forward-slashed, of every *.json under $root EXCEPT the manifest and anything under
# _backup. Excluding _backup is load-bearing: it lives inside the target, so without this the mirror
# would find its own backups as "extras" and delete them - on the very next run.
function Get-SampleSet([string]$root) {
    # The leading comma on every return is not a typo: `return $list` UNROLLS the list onto the
    # pipeline, so an empty tree would come back as $null and a one-file tree as a bare string.
    $set = New-Object 'System.Collections.Generic.List[string]'
    if (-not (Test-Path -LiteralPath $root)) { return ,$set }
    $prefix = (Get-Item -LiteralPath $root).FullName.TrimEnd('\') + '\'
    foreach ($f in @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($prefix.Length).Replace('\', '/')
        if ($rel -eq $ManifestFileName) { continue }
        if ($rel -like "$BackupFolderName/*") { continue }
        [void]$set.Add($rel)
    }
    $set.Sort([System.StringComparer]::OrdinalIgnoreCase)
    return ,$set
}

# One record per line, "<relative path><TAB><hash>", '#' comments. Deliberately not JSON, and
# deliberately not named *.json: the runtime's profile list is Directory.GetFiles(dir, "*.json"), so a
# manifest with a .json name would show up in the profile picker. Same reasoning as
# PresetInstallPlan.ManifestFileName, one folder over.
function Read-SampleManifest([string]$path) {
    $map = @{}   # PowerShell hashtables are case-insensitive on string keys, which is what we want here
    if (-not (Test-Path -LiteralPath $path)) { return $map }
    try { $text = [System.IO.File]::ReadAllText($path) } catch { return $map }
    foreach ($rawLine in $text.Replace("`r`n", "`n").Split("`n")) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
        $tab = $line.IndexOf("`t")
        if ($tab -le 0 -or $tab -eq $line.Length - 1) { continue }
        $name = $line.Substring(0, $tab).Trim()
        $hash = $line.Substring($tab + 1).Trim()
        if ($name.Length -eq 0 -or $hash.Length -eq 0) { continue }
        $map[$name] = $hash
    }
    return $map
}

function Write-SampleManifest([string]$path, $map) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("# NGU Advisor sample-profile mirror record. Each line is the SHA-256 of the text`n")
    [void]$sb.Append("# build\deploy-sampleprofiles.ps1 last wrote for that file (CRLF folded to LF).`n")
    [void]$sb.Append("# Delete a line to let the repo version overwrite your copy on the next deploy;`n")
    [void]$sb.Append("# delete this file to re-adopt everything (which costs one backup per file).`n")
    foreach ($name in @($map.Keys | Sort-Object)) {
        if ([string]::IsNullOrEmpty($name) -or $name.Contains("`t")) { continue }
        $hash = $map[$name]
        if ([string]::IsNullOrEmpty($hash)) { continue }
        [void]$sb.Append($name).Append("`t").Append($hash).Append("`n")
    }
    [System.IO.File]::WriteAllText($path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

# _backup\<same subdirs>\<stem>.<stamp>.json. The subdirectories are PRESERVED on purpose: this tree's
# whole cautionary tale is that a flat `cblock4.json` and `Evil\CBlock4.json` are one name once the
# folders are dropped on a case-insensitive filesystem (CampaignTables.cs:340-346). Flattening the
# backups would let the file being deleted overwrite the backup of the file being kept.
function Get-BackupPath([string]$targetRoot, [string]$rel, [string]$stamp) {
    $dir  = Split-Path -Parent $rel.Replace('/', '\')
    $leaf = Split-Path -Leaf   $rel.Replace('/', '\')
    if ($leaf.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) {
        $leaf = $leaf.Substring(0, $leaf.Length - 5)
    }
    $backupDir = Join-Path $targetRoot $BackupFolderName
    if ($dir) { $backupDir = Join-Path $backupDir $dir }
    return (Join-Path $backupDir ($leaf + '.' + $stamp + '.json'))
}

# --- preconditions ---------------------------------------------------------------------------------
# Both are hard failures rather than a silent skip. A silent skip on a missing directory is exactly the
# `Exists(...)` condition on the three csproj deploy targets that audit/42 1 calls out: a deploy that
# ships nothing and still reports success.
if (-not (Test-Path -LiteralPath $Source)) {
    Write-Host "SAMPLE PROFILES FAILED: the source tree does not exist: $Source" -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath $Target)) {
    if ($CheckOnly) {
        Write-Host "SAMPLE PROFILES FAILED: the target does not exist: $Target" -ForegroundColor Red
        exit 2
    }
    [void](New-Item -ItemType Directory -Path $Target -Force)
    Say "Created $Target"
}

$Source = (Get-Item -LiteralPath $Source).FullName
$Target = (Get-Item -LiteralPath $Target).FullName

# ONE FOLDER CANNOT BE BOTH SIDES OF A MIRROR. This is not a hypothetical: the repo tree is
# `SampleProfiles` and the runtime tree is `sampleprofiles`, which are THE SAME NAME on a
# case-insensitive filesystem - the identical collision CampaignTables.cs:340-346 documents for
# cblock4. Point both at one parent and every file reads as AlreadyCurrent, the mirror reports a
# healthy "in sync", and it has compared the folder to itself. Nesting is worse: if the target
# contained the source, every repo file would also be an "extra" and the delete side would eat it.
$srcCmp = $Source.TrimEnd('\') + '\'
$dstCmp = $Target.TrimEnd('\') + '\'
if ($srcCmp.Equals($dstCmp, [System.StringComparison]::OrdinalIgnoreCase) -or
    $srcCmp.StartsWith($dstCmp, [System.StringComparison]::OrdinalIgnoreCase) -or
    $dstCmp.StartsWith($srcCmp, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "SAMPLE PROFILES FAILED: the source and target are the same tree, or one contains the other." -ForegroundColor Red
    Write-Host "  source $Source"
    Write-Host "  target $Target"
    exit 2
}

$manifestPath = Join-Path $Target $ManifestFileName
$records      = Read-SampleManifest $manifestPath
$stamp        = (Get-Date).ToString('yyyyMMdd-HHmmss')

$sourceSet = Get-SampleSet $Source
$targetSet = Get-SampleSet $Target
$targetHas = @{}
foreach ($r in $targetSet) { $targetHas[$r] = $true }

$verdicts = @()   # one PSCustomObject per file that is NOT AlreadyCurrent
$current  = 0
$dirty    = $false

# --- the repo's files ------------------------------------------------------------------------------
foreach ($rel in $sourceSet) {
    $srcPath = Join-Path $Source $rel.Replace('/', '\')
    $dstPath = Join-Path $Target $rel.Replace('/', '\')
    $shipped = Get-SampleHash $srcPath
    $exists  = $targetHas.ContainsKey($rel)
    $destH   = $null
    if ($exists) { $destH = Get-SampleHash $dstPath }
    $recorded = $null
    if ($records.ContainsKey($rel)) { $recorded = $records[$rel] }

    if (-not $exists) {
        $action = 'Install'
    } elseif ($destH -eq $shipped) {
        $action = 'AlreadyCurrent'
    } elseif ($null -eq $recorded) {
        $action = 'BackupThenInstall'
    } elseif ($destH -eq $recorded) {
        $action = 'UpdateInPlace'
    } else {
        $action = 'PreserveUserEdit'
    }

    if ($action -eq 'AlreadyCurrent') {
        $current++
        # Nothing to write, but adopt the hash so a LATER edit is recognisable as one. Without this the
        # first hand-edit of a file that happened to already match would read as BackupThenInstall and
        # be replaced (recoverably, but replaced) instead of preserved.
        if (-not $CheckOnly -and $records[$rel] -ne $shipped) { $records[$rel] = $shipped; $dirty = $true }
        continue
    }

    $backup = $null
    if (-not $CheckOnly) {
        if ($action -eq 'BackupThenInstall') {
            $backup = Get-BackupPath $Target $rel $stamp
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force)
            Copy-Item -LiteralPath $dstPath -Destination $backup -Force
        }
        if ($action -ne 'PreserveUserEdit') {
            $destDir = Split-Path -Parent $dstPath
            if (-not (Test-Path -LiteralPath $destDir)) { [void](New-Item -ItemType Directory -Path $destDir -Force) }
            Copy-Item -LiteralPath $srcPath -Destination $dstPath -Force
            $records[$rel] = $shipped
            $dirty = $true
        }
    } elseif ($action -eq 'BackupThenInstall') {
        $backup = Get-BackupPath $Target $rel $stamp
    }

    $verdicts += [pscustomobject]@{ Action = $action; Path = $rel; Backup = $backup }
}

# --- the files the repo does NOT have: the mirror's delete side ------------------------------------
foreach ($rel in $targetSet) {
    if ($sourceSet -contains $rel) { continue }
    $dstPath = Join-Path $Target $rel.Replace('/', '\')
    $backup  = Get-BackupPath $Target $rel $stamp
    if (-not $CheckOnly) {
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force)
        # Copy THEN delete, in that order and never the reverse: if the copy throws, the original is
        # still there and the run fails with the file intact.
        Copy-Item -LiteralPath $dstPath -Destination $backup -Force
        Remove-Item -LiteralPath $dstPath -Force
        if ($records.ContainsKey($rel)) { $records.Remove($rel); $dirty = $true }
    }
    $verdicts += [pscustomobject]@{ Action = 'BackupThenRemove'; Path = $rel; Backup = $backup }
}

# Directories the delete side just emptied (cblock2\, on this machine). Left behind they are a phantom
# folder in a reference tree. _backup is excluded by construction - it is never empty after a removal.
$prunedDirs = @()
if (-not $CheckOnly) {
    $backupRoot = Join-Path $Target $BackupFolderName
    foreach ($d in @(Get-ChildItem -LiteralPath $Target -Recurse -Directory -ErrorAction SilentlyContinue |
                     Sort-Object { $_.FullName.Length } -Descending)) {
        if ($d.FullName -eq $backupRoot -or $d.FullName.StartsWith($backupRoot + '\')) { continue }
        if (@(Get-ChildItem -LiteralPath $d.FullName -Force).Count -eq 0) {
            Remove-Item -LiteralPath $d.FullName -Force
            $prunedDirs += $d.FullName.Substring($Target.Length + 1)
        }
    }
}

if ($dirty -and -not $CheckOnly) { Write-SampleManifest $manifestPath $records }

# --- the report ------------------------------------------------------------------------------------
# Everything that is not AlreadyCurrent gets a line with its name. The summary prints ALWAYS, including
# the checked count, even when nothing changed - "49 checked, all current" and "not measured" have to
# look different, which is the generalised finding audit/42 was written about.
function Count-Of($name) { return @($verdicts | Where-Object { $_.Action -eq $name }).Count }

$installed = Count-Of 'Install'
$updated   = (Count-Of 'UpdateInPlace') + (Count-Of 'BackupThenInstall')
$removed   = Count-Of 'BackupThenRemove'
$preserved = Count-Of 'PreserveUserEdit'
$verb      = 'would be'
if (-not $CheckOnly) { $verb = 'was' }

if ($verdicts.Count -gt 0) {
    Say ''
    foreach ($v in $verdicts) {
        switch ($v.Action) {
            'Install'           { Say ("  + installed   {0}" -f $v.Path) }
            'UpdateInPlace'     { Say ("  ~ updated     {0}  (your copy was unmodified)" -f $v.Path) }
            'BackupThenInstall' { Say ("  ~ updated     {0}  -> previous copy saved to {1}" -f $v.Path, $v.Backup) }
            'BackupThenRemove'  { Say ("  - REMOVED     {0}  (the repo deleted it) -> saved to {1}" -f $v.Path, $v.Backup) }
            'PreserveUserEdit'  { Say ("  ! KEPT YOURS  {0}  differs from the repo and you edited it. Delete it to take the repo version." -f $v.Path) }
        }
    }
}
foreach ($d in $prunedDirs) { Say ("  - removed empty folder {0}\" -f $d) }

$summary = ("sample profiles  {0} in repo | {1} current | {2} installed | {3} updated | {4} removed | {5} kept yours" -f
            $sourceSet.Count, $current, $installed, $updated, $removed, $preserved)
Say ''
if ($CheckOnly) {
    if ($verdicts.Count -eq 0) {
        Write-Host "IN SYNC   $summary" -ForegroundColor Green
        exit 0
    }
    Write-Host "DRIFTED   $summary" -ForegroundColor Yellow
    Write-Host "  $Target does not match $Source. Run build\deploy.ps1 (or this script without -CheckOnly)."
    exit 1
}

if ($verdicts.Count -eq 0) {
    Write-Host "SAMPLE PROFILES in sync   $summary" -ForegroundColor Green
} else {
    Write-Host "SAMPLE PROFILES mirrored  $summary" -ForegroundColor Green
}
if ($preserved -gt 0) {
    Write-Host "  $preserved file(s) kept your edits and will NOT track the repo until you delete them." -ForegroundColor Yellow
}
exit 0
