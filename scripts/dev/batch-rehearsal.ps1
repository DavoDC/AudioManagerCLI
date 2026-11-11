#Requires -Version 5.1
# Batch rehearsal: copies the real sources into a fresh WorkDir, runs the tier-1 readiness dry run on the copies and,
# with -Integrate, a real integrate on the copies only. The real folders are read-only sources, snapshotted before and after.
# Usage:
#   powershell -NoProfile -File scripts/dev/batch-rehearsal.ps1 -Exe <AudioManager.exe> -WorkDir <new folder> [-Integrate] [-SpellingFromSuggested]
# -Exe and -WorkDir are required. They are checked in the main body, not by a Mandatory binder, so dot-sourcing never prompts.
# Dot-source this file to reach the helpers without running it:
#   . (Join-Path $PSScriptRoot 'batch-rehearsal.ps1')

[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkDir,
    [string]$SourceNewMusic = 'C:\Users\David\Downloads\NewMusic',
    [string]$SourceLibrary = 'C:\Users\David\Audio',
    [string]$SourceMirror = 'C:\Users\David\GitHubRepos\AudioMirror',
    [switch]$Integrate,
    [switch]$SpellingFromSuggested
)

# Captured before the dot-sourcing below: batch-readiness.ps1 has its own param block, which rebinds $Exe and friends in this scope.
$cliArgs = @{
    Exe                  = $Exe
    WorkDir              = $WorkDir
    SourceNewMusic       = $SourceNewMusic
    SourceLibrary        = $SourceLibrary
    SourceMirror         = $SourceMirror
    Integrate            = $Integrate.IsPresent
    SpellingFromSuggested = $SpellingFromSuggested.IsPresent
}

. (Join-Path $PSScriptRoot 'snapshot-guard.ps1')
. (Join-Path $PSScriptRoot 'batch-readiness.ps1')

# Robocopy each source folder into Sources-key subfolders of RootDir. Real copies only (no /MIR, no links).
function Copy-RehearsalSources {
    param([System.Collections.IDictionary]$Sources, [string]$RootDir)
    foreach ($name in $Sources.Keys) {
        $src = [string]$Sources[$name]
        if (-not (Test-Path -LiteralPath $src -PathType Container)) { throw "source folder not found: $src" }
        $dst = Join-Path $RootDir $name
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        & robocopy $src $dst /E /R:1 /W:1 /MT:16 /NFL /NDL /NJH /NJS | Out-Null
        $code = $LASTEXITCODE
        if ($code -ge 8) { throw "robocopy failed ($code): $src -> $dst" }
    }
}

# Writes README.md with the DELETE-AFTER line first (six days out) and one line saying the folder holds copies only.
function Write-RehearsalReadme {
    param([string]$WorkDir, [datetime]$Today)
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $due = $Today.AddDays(6).ToString('yyyy-MM-dd')
    $lines = @(
        "DELETE-AFTER: $due (and once the real integration is confirmed finished)",
        'Copies only: root holds robocopy copies of NewMusic, Audio and AudioMirror. The real folders are read-only sources and are checked unchanged.',
        ''
    )
    [IO.File]::WriteAllText((Join-Path $WorkDir 'README.md'), (($lines -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))
}

# Refuses a WorkDir that exists and has anything in it. A missing or empty folder passes.
function Assert-WorkDirEmpty {
    param([string]$WorkDir)
    if (-not (Test-Path -LiteralPath $WorkDir)) { return }
    $items = @(Get-ChildItem -LiteralPath $WorkDir -Force -ErrorAction SilentlyContinue)
    if ($items.Count -gt 0) { throw "WorkDir is not empty: $WorkDir (pass a new folder)" }
}

# Pure: routing record in, manifest object out. Object form: {tracks, artistSpellings}.
# Rows with libraryDuplicate true get dupResolution K. Spelling choices only with -SpellingFromSuggested.
function Build-RehearsalManifest {
    param([object]$Routing, [switch]$SpellingFromSuggested)
    $files = if ($null -eq $Routing.files) { @() } else { @($Routing.files) }
    $tracks = New-Object System.Collections.Generic.List[object]
    foreach ($e in $files) {
        $row = [ordered]@{ filename = $e.filename; sourceRel = $e.sourceRel; artist = $e.artist; title = $e.title }
        if ($e.libraryDuplicate -eq $true) { $row['dupResolution'] = 'K' }
        $tracks.Add($row)
    }
    $spellings = New-Object System.Collections.Generic.List[object]
    if ($SpellingFromSuggested) {
        $cands = if ($null -eq $Routing.artistSpellingCandidates) { @() } else { @($Routing.artistSpellingCandidates) }
        foreach ($c in $cands) { $spellings.Add([ordered]@{ key = $c.key; spelling = $c.suggested }) }
    }
    return [ordered]@{ tracks = $tracks.ToArray(); artistSpellings = $spellings.ToArray() }
}

function Get-SourceSnapshots {
    param([System.Collections.IDictionary]$Sources)
    $snaps = @{}
    foreach ($k in $Sources.Keys) { $snaps[$k] = Get-TreeSnapshot -Path ([string]$Sources[$k]) }
    return $snaps
}

# Runs Body between a before and after snapshot of every source folder and the mirror HEAD. Any change throws
# REAL FOLDER TOUCHED, also when Body threw (the check runs in finally). Returns Body's output.
function Invoke-WithSourceGuard {
    param([System.Collections.IDictionary]$Sources, [string]$MirrorRepo, [scriptblock]$Body)
    $before = Get-SourceSnapshots -Sources $Sources
    $headBefore = Get-MirrorHeadOrNone -Path $MirrorRepo
    $result = $null
    try {
        $result = & $Body
    } finally {
        $after = Get-SourceSnapshots -Sources $Sources
        $headAfter = Get-MirrorHeadOrNone -Path $MirrorRepo
        try {
            foreach ($k in $Sources.Keys) { $null = Assert-TreeUnchanged -Name $k -Before $before[$k] -After $after[$k] }
            $null = Assert-TreeUnchanged -Name 'mirror HEAD' -Before @{ HEAD = $headBefore } -After @{ HEAD = $headAfter }
        } catch {
            throw "REAL FOLDER TOUCHED: $($_.Exception.Message)"
        }
    }
    return $result
}

# Real integrate on the copy: maps a free drive to Root, points the exe at it, waits up to TimeoutSec. Returns the exit code.
# The subst drive and the env var are always restored.
function Invoke-IntegrateOnCopy {
    param([string]$Exe, [string]$Root, [string]$Tier2Dir, [int]$TimeoutSec)
    $stdin = Join-Path $Tier2Dir 'empty-stdin.txt'
    [IO.File]::WriteAllText($stdin, '', (New-Object Text.UTF8Encoding($false)))
    $console = Join-Path $Tier2Dir 'integrate-console.txt'
    $errPath = Join-Path $Tier2Dir 'integrate-stderr.txt'
    $prevTestRoot = $env:AUDIOMANAGER_TEST_ROOT
    $drive = Get-FreeDriveLetter
    $manifestPath = "$($drive):\manifest.json"
    try {
        & subst "$($drive):" $Root
        if ($LASTEXITCODE -ne 0) { throw "subst $($drive): failed" }
        $env:AUDIOMANAGER_TEST_ROOT = "$($drive):\"
        $p = Start-Process -FilePath $Exe -ArgumentList @('integrate', '--json-output', '--no-input', '--manifest', $manifestPath) -WorkingDirectory $Tier2Dir -RedirectStandardInput $stdin -RedirectStandardOutput $console -RedirectStandardError $errPath -NoNewWindow -PassThru
        $null = $p.Handle
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            Stop-Process -Id $p.Id -Force
            throw "KILLED: integrate exceeded $TimeoutSec sec"
        }
        $p.Refresh()
        return $p.ExitCode
    } finally {
        $env:AUDIOMANAGER_TEST_ROOT = $prevTestRoot
        & subst "$($drive):" /D | Out-Null
    }
}

function Invoke-BatchRehearsal {
    param(
        [string]$Exe,
        [string]$WorkDir,
        [string]$SourceNewMusic,
        [string]$SourceLibrary,
        [string]$SourceMirror,
        [switch]$Integrate,
        [switch]$SpellingFromSuggested
    )
    if (-not $Exe) { throw 'pass -Exe <path to the AudioManager exe>' }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "exe not found: $Exe" }
    if (-not $WorkDir) { throw 'pass -WorkDir <new folder to build under>' }
    Assert-WorkDirEmpty -WorkDir $WorkDir
    $WorkDir = [IO.Path]::GetFullPath($WorkDir)

    $sources = [ordered]@{ NewMusic = $SourceNewMusic; Audio = $SourceLibrary; AudioMirror = $SourceMirror }
    foreach ($k in $sources.Keys) {
        if (-not (Test-Path -LiteralPath ([string]$sources[$k]) -PathType Container)) { throw "source folder not found: $($sources[$k])" }
    }

    $root = Join-Path $WorkDir 'root'
    $tier1 = Join-Path $WorkDir 'tier1'
    $tier2 = Join-Path $WorkDir 'tier2'
    New-Item -ItemType Directory -Force -Path $root, $tier1 | Out-Null

    $state = @(Invoke-WithSourceGuard -Sources $sources -MirrorRepo $SourceMirror -Body {
        Copy-RehearsalSources -Sources $sources -RootDir $root
        Write-RehearsalReadme -WorkDir $WorkDir -Today (Get-Date)

        $r1 = @(Invoke-BatchReadiness -Exe $Exe -Root $root -OutDir $tier1 -Tier 1 -TestRoot -TimeoutSec 2400)[-1]
        $verdict = [string]$r1.Verdict
        $did = $false
        $exit = $null
        $mirrorStatus = $null
        $execSummary = $null

        if ($Integrate -and $verdict -ne 'blocked') {
            $routing = Get-ChildItem -LiteralPath $tier1 -Filter 'routing-*.json' -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $routing) { throw "no routing-*.json in $tier1" }
            $rj = Get-Content -Raw -Encoding UTF8 -LiteralPath $routing.FullName | ConvertFrom-Json
            $manifest = Build-RehearsalManifest -Routing $rj -SpellingFromSuggested:$SpellingFromSuggested
            [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), (ConvertTo-Json -InputObject $manifest -Depth 6), (New-Object Text.UTF8Encoding($false)))

            New-Item -ItemType Directory -Force -Path $tier2 | Out-Null
            $exit = Invoke-IntegrateOnCopy -Exe $Exe -Root $root -Tier2Dir $tier2 -TimeoutSec 2400
            $did = $true

            $logs = Join-Path $root 'logs'
            foreach ($pat in @('execution-*.json', 'mirror-result.json', 'integrate-gate.json')) {
                Get-ChildItem -LiteralPath $logs -Filter $pat -File -ErrorAction SilentlyContinue | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $tier2 $_.Name) -Force
                }
            }
            $mr = Join-Path $tier2 'mirror-result.json'
            if (Test-Path -LiteralPath $mr -PathType Leaf) {
                $st = (Get-Content -Raw -Encoding UTF8 -LiteralPath $mr | ConvertFrom-Json).status
                if ($null -ne $st) { $mirrorStatus = [string]$st }
            }
            $ex = Get-ChildItem -LiteralPath $tier2 -Filter 'execution-*.json' -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($ex) {
                $execSummary = (Get-Content -Raw -Encoding UTF8 -LiteralPath $ex.FullName | ConvertFrom-Json).summary.counts
            }
        }

        [pscustomobject]@{
            tier1Verdict     = $verdict
            integrated       = $did
            integrateExit    = $exit
            mirrorStatus     = $mirrorStatus
            executionSummary = $execSummary
        }
    })[-1]

    $out = [ordered]@{
        generatedAt      = (Get-Date).ToString('o')
        tier1Verdict     = $state.tier1Verdict
        integrated       = [bool]$state.integrated
        mirrorStatus     = $state.mirrorStatus
        executionSummary = $state.executionSummary
        sourcesUnchanged = $true
    }
    [IO.File]::WriteAllText((Join-Path $WorkDir 'rehearsal.json'), (ConvertTo-Json -InputObject $out -Depth 6), (New-Object Text.UTF8Encoding($false)))

    return [pscustomobject]@{
        Verdict       = $state.tier1Verdict
        Integrated    = [bool]$state.integrated
        IntegrateExit = $state.integrateExit
        Mirror        = $state.mirrorStatus
        OutDir        = $WorkDir
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $r = Invoke-BatchRehearsal @cliArgs
        if ($r.Integrated) { Write-Output "integrate exit=$($r.IntegrateExit)" }
        $mirrorText = if ($r.Mirror) { $r.Mirror } else { 'none' }
        Write-Output "rehearsal verdict=$($r.Verdict) mirror=$mirrorText outDir=$($r.OutDir)"
        exit 0
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 1
    }
}
