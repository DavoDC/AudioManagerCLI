#Requires -Version 5.1
# Batch readiness (tier 1): a read-only integrate dry run plus a verdict.
# Usage:
#   powershell -NoProfile -File scripts/dev/batch-readiness.ps1 -Exe <AudioManager.exe> -Root <root> [-OutDir <dir>] [-TestRoot] [-TimeoutSec 2400]
# -Root is the folder holding Audio, AudioMirror and NewMusic as the exe sees it. Nothing is implicit:
# without -Root the script stops. -TestRoot redirects the exe with AUDIOMANAGER_TEST_ROOT through a subst drive.
# Tier 2 is out of scope and throws. Dot-source this file to reach the helpers without running it:
#   . (Join-Path $PSScriptRoot 'batch-readiness.ps1')

[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Root,
    [string]$OutDir,
    [ValidateSet(1, 2)][int]$Tier = 1,
    [switch]$TestRoot,
    [int]$TimeoutSec = 2400
)

. (Join-Path $PSScriptRoot 'snapshot-guard.ps1')

# Counts the '  - ' hit lines of the 'Projected LibChecker' section, up to the 'Routed:' line.
# Returns $null when the section is missing, was skipped (' - SKIP'), or never reached 'Routed:'.
function Get-ProjectedLibCheckerHits {
    param([string[]]$Lines)
    $inSection = $false
    $sawHeading = $false
    $sawRouted = $false
    $hits = 0
    foreach ($line in $Lines) {
        if (-not $inSection) {
            if ($line -like '*Projected LibChecker*') { $inSection = $true; $sawHeading = $true }
            continue
        }
        if ($line.StartsWith('Routed:')) { $sawRouted = $true; break }
        if ($line -like ' - SKIP*') { return $null }
        if ($line.StartsWith('  - ') -and $line -notlike '*Total hits*') { $hits++ }
    }
    if (-not ($sawHeading -and $sawRouted)) { return $null }
    return $hits
}

# 'ready' only when there are no routing errors and the projected LibChecker found no hits. Unknown hits give 'review'.
function Get-ReadinessVerdict {
    param([int]$Errors, [object]$Hits)
    if ($Errors -eq 0 -and $null -ne $Hits -and [int]$Hits -eq 0) { return 'ready' }
    return 'review'
}

# Reads the exe's pre-integration gate record. Returns $null when the file is missing, unreadable, malformed, or status is 'passed'.
# Otherwise returns {Status, Action, Detail}; Detail joins mirrorDirtyCount and libCheckerIssues when present.
function Get-GateBlock {
    param([string]$GatePath)
    if (-not (Test-Path -LiteralPath $GatePath -PathType Leaf)) { return $null }
    try {
        $g = Get-Content -Raw -Encoding UTF8 -LiteralPath $GatePath | ConvertFrom-Json
    } catch { return $null }
    if ($null -eq $g) { return $null }
    if ([string]$g.status -eq 'passed') { return $null }
    $parts = @()
    if ($null -ne $g.PSObject.Properties['mirrorDirtyCount']) { $parts += "mirrorDirtyCount $($g.mirrorDirtyCount)" }
    if ($null -ne $g.PSObject.Properties['libCheckerIssues']) { $parts += "libCheckerIssues $($g.libCheckerIssues)" }
    return [pscustomobject]@{
        Status = [string]$g.status
        Action = [string]$g.action
        Detail = ($parts -join '; ')
    }
}

function Get-DefaultOutDir {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return (Join-Path $env:TEMP ("AudioManagerCLI\batch-readiness\" + $stamp))
}

function Get-MirrorHeadOrNone {
    param([string]$Path)
    try {
        $head = Get-GitHead -Repo $Path
        if ($head) { return [string]$head }
    } catch { }
    return 'no-git'
}

function Get-FreeDriveLetter {
    foreach ($l in [char[]]'ZYXWVUTSRQPONMLKJIHGFED') {
        if (-not (Test-Path -LiteralPath ("$l`:\"))) { return [string]$l }
    }
    throw 'no free drive letter for subst'
}

function Invoke-IntegrateDryRun {
    param([string]$Exe, [string]$WorkDir, [string]$ConsolePath, [int]$TimeoutSec)
    $stdin = Join-Path $WorkDir 'empty-stdin.txt'
    [IO.File]::WriteAllText($stdin, '')
    $errPath = Join-Path $WorkDir 'integrate-stderr.txt'
    $p = Start-Process -FilePath $Exe -ArgumentList @('integrate', '--dry-run', '--json-output', '--no-input') -WorkingDirectory $WorkDir -RedirectStandardInput $stdin -RedirectStandardOutput $ConsolePath -RedirectStandardError $errPath -NoNewWindow -PassThru
    $null = $p.Handle
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        Stop-Process -Id $p.Id -Force
        throw "KILLED: integrate dry run exceeded $TimeoutSec sec"
    }
    $p.Refresh()
    return $p.ExitCode
}

function Invoke-BatchReadiness {
    param([string]$Exe, [string]$Root, [string]$OutDir, [int]$Tier, [switch]$TestRoot, [int]$TimeoutSec)

    if ($Tier -eq 2) { throw 'Tier 2 is not implemented in this script (out of scope)' }
    if (-not $Exe) { throw 'pass -Exe <path to the AudioManager exe>' }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "exe not found: $Exe" }
    if (-not $Root) { throw 'pass -Root <root folder holding Audio, AudioMirror and NewMusic>' }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "root not found: $Root" }

    $Root = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
    if (-not $OutDir) { $OutDir = Get-DefaultOutDir }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = (Resolve-Path -LiteralPath $OutDir).Path

    # 1. Snapshot the three trees and the mirror HEAD before the run. Logs live under Root\logs, outside these trees.
    $trees = [ordered]@{
        NewMusic = Join-Path $Root 'NewMusic'
        library  = Join-Path $Root 'Audio'
        mirror   = Join-Path $Root 'AudioMirror'
    }
    $before = [ordered]@{}
    foreach ($k in $trees.Keys) { $before[$k] = Get-TreeSnapshot -Path $trees[$k] }
    $headBefore = Get-MirrorHeadOrNone -Path $trees['mirror']

    # 2. Read-only dry run. The test root (when asked for) is a subst drive, removed in finally.
    $started = Get-Date
    $consolePath = Join-Path $OutDir 'integrate-console.txt'
    $prevTestRoot = $env:AUDIOMANAGER_TEST_ROOT
    $drive = $null
    try {
        if ($TestRoot) {
            $drive = Get-FreeDriveLetter
            & subst "$($drive):" $Root
            if ($LASTEXITCODE -ne 0) { throw "subst $($drive): failed" }
            $env:AUDIOMANAGER_TEST_ROOT = "$($drive):\"
        } else {
            Remove-Item Env:AUDIOMANAGER_TEST_ROOT -ErrorAction SilentlyContinue
        }
        $exitCode = Invoke-IntegrateDryRun -Exe $Exe -WorkDir $OutDir -ConsolePath $consolePath -TimeoutSec $TimeoutSec
    } finally {
        $env:AUDIOMANAGER_TEST_ROOT = $prevTestRoot
        if ($drive) { & subst "$($drive):" /D | Out-Null }
    }

    # 3. Snapshot after the run and assert nothing in the real folders changed. Runs before the routing lookup so a touched real folder is always caught.
    $after = [ordered]@{}
    foreach ($k in $trees.Keys) { $after[$k] = Get-TreeSnapshot -Path $trees[$k] }
    $headAfter = Get-MirrorHeadOrNone -Path $trees['mirror']
    try {
        foreach ($k in $trees.Keys) { $null = Assert-TreeUnchanged -Name $k -Before $before[$k] -After $after[$k] }
        $null = Assert-TreeUnchanged -Name 'mirror HEAD' -Before @{ HEAD = $headBefore } -After @{ HEAD = $headAfter }
    } catch {
        throw "REAL FOLDER TOUCHED: $($_.Exception.Message)"
    }

    # 4. Copy the routing record this run wrote. With none, a gate refusal (logs\integrate-gate.json written by this run) is a verdict, not an error.
    $logs = Join-Path $Root 'logs'
    $routing = Get-ChildItem -LiteralPath $logs -Filter 'routing-*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-2) } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $routing) {
        $gatePath = Join-Path $logs 'integrate-gate.json'
        $gateFile = Get-Item -LiteralPath $gatePath -ErrorAction SilentlyContinue
        $gate = $null
        if ($gateFile -and $gateFile.LastWriteTime -ge $started.AddSeconds(-2)) { $gate = Get-GateBlock -GatePath $gatePath }
        if (-not $gate) { throw "no routing-*.json written by this run under $logs (exe exit=$exitCode)" }
        $utf8Gate = New-Object Text.UTF8Encoding($false)
        $genGate = (Get-Date).ToString('o')
        $gateRecord = [ordered]@{
            generatedAt = $genGate
            verdict     = 'blocked'
            gateStatus  = $gate.Status
            gateAction  = $gate.Action
            gateDetail  = $gate.Detail
        }
        [IO.File]::WriteAllText((Join-Path $OutDir 'readiness.json'), ($gateRecord | ConvertTo-Json -Depth 5), $utf8Gate)
        $gateMd = @(
            '# Batch readiness (tier 1)',
            '',
            'Verdict: BLOCKED',
            '',
            "Generated: $genGate",
            '',
            "Gate status: $($gate.Status). Action: $($gate.Action). Detail: $($gate.Detail).",
            '',
            "The exe refused before integrating (exit code $exitCode); no routing record was written.",
            '',
            "Real folders (NewMusic, Audio, AudioMirror) unchanged by the dry run. Mirror HEAD: $headBefore.",
            ''
        )
        [IO.File]::WriteAllText((Join-Path $OutDir 'readiness.md'), (($gateMd -join "`r`n") + "`r`n"), $utf8Gate)
        return [pscustomobject]@{ Verdict = 'blocked'; OutDir = $OutDir }
    }
    $routingCopy = Join-Path $OutDir $routing.Name
    Copy-Item -LiteralPath $routing.FullName -Destination $routingCopy

    # 5. Readiness record and human report.
    $r = Get-Content -Raw -Encoding UTF8 -LiteralPath $routingCopy | ConvertFrom-Json
    $counts = $r.summary.counts
    $errors = [int]$counts.errors
    $duplicates = [int]$counts.libraryDuplicates + [int]$counts.inBatchDuplicates
    $cands = $r.artistSpellingCandidates
    $candCount = if ($null -eq $cands) { 0 } else { @($cands).Count }
    $lines = @(Get-Content -LiteralPath $consolePath)
    $hits = Get-ProjectedLibCheckerHits -Lines $lines
    $verdict = Get-ReadinessVerdict -Errors $errors -Hits $hits
    $generated = (Get-Date).ToString('o')
    $fingerprint = $r.newMusicFingerprint

    $record = [ordered]@{
        generatedAt              = $generated
        counts                   = $counts
        duplicates               = $duplicates
        artistSpellingCandidates = $candCount
        newMusicFingerprint      = $fingerprint
        projectedLibCheckerHits  = $hits
        verdict                  = $verdict
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $OutDir 'readiness.json'), ($record | ConvertTo-Json -Depth 5), $utf8)

    $hitsText = if ($null -eq $hits) { 'not determined (section missing, skipped, or incomplete)' } else { "$hits" }
    $fpText = if ($fingerprint) { "$fingerprint" } else { 'none' }
    $md = @(
        '# Batch readiness (tier 1)',
        '',
        "Verdict: $($verdict.ToUpper())",
        '',
        "Generated: $generated",
        '',
        "Routing record: $($routing.Name). Exe exit code: $exitCode.",
        '',
        "Counts: files $($counts.files), routed $($counts.routed), skipped $($counts.skipped), errors $($counts.errors), unrouted $($counts.unrouted).",
        '',
        "Duplicates: $duplicates (library $($counts.libraryDuplicates), in batch $($counts.inBatchDuplicates)).",
        '',
        "Artist spelling candidates: $candCount.",
        '',
        "Projected LibChecker hits: $hitsText.",
        '',
        "NewMusic fingerprint: $fpText.",
        '',
        "Real folders (NewMusic, Audio, AudioMirror) unchanged by the dry run. Mirror HEAD: $headBefore.",
        ''
    )
    [IO.File]::WriteAllText((Join-Path $OutDir 'readiness.md'), (($md -join "`r`n") + "`r`n"), $utf8)

    return [pscustomobject]@{ Verdict = $verdict; OutDir = $OutDir }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $result = @(Invoke-BatchReadiness -Exe $Exe -Root $Root -OutDir $OutDir -Tier $Tier -TestRoot:$TestRoot -TimeoutSec $TimeoutSec)[-1]
        Write-Output "verdict=$($result.Verdict) outDir=$($result.OutDir)"
        exit 0
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 1
    }
}
