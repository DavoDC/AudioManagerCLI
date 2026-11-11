#Requires -Version 5.1
# Batch rehearsal: copies the real sources into a fresh WorkDir, runs the tier-1 readiness dry run on the copies and,
# with -Integrate, a real integrate on the copies only. The real folders are read-only sources, snapshotted before and after.
# Usage:
#   powershell -NoProfile -File scripts/dev/batch-rehearsal.ps1 -Exe <AudioManager.exe> -WorkDir <new folder> [-Integrate] [-SpellingFromSuggested] [-KeepCopy]
# With -Integrate, after tier 2 it also runs --libfix-plan on the copy, writes summary.md and summary.json (one-command check),
# and deletes WorkDir\root when the verdict is pass, unless -KeepCopy. Verdict rule: Get-RehearsalVerdict.
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
    [switch]$SpellingFromSuggested,
    [switch]$KeepCopy
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
    KeepCopy             = $KeepCopy.IsPresent
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

# Runs the exe on the copy: maps a free drive to Root, sets AUDIOMANAGER_TEST_ROOT to it, waits up to TimeoutSec.
# ArgsFor is a scriptblock given the drive letter; it returns the argument list. Console and stderr go to RunDir\<Label>-*.txt.
# Returns the exit code. The subst drive and the env var are always restored.
function Invoke-ExeOnCopy {
    param([string]$Exe, [string]$Root, [string]$RunDir, [string]$Label, [scriptblock]$ArgsFor, [int]$TimeoutSec)
    $stdin = Join-Path $RunDir 'empty-stdin.txt'
    [IO.File]::WriteAllText($stdin, '', (New-Object Text.UTF8Encoding($false)))
    $console = Join-Path $RunDir "$Label-console.txt"
    $errPath = Join-Path $RunDir "$Label-stderr.txt"
    $prevTestRoot = $env:AUDIOMANAGER_TEST_ROOT
    $drive = Get-FreeDriveLetter
    try {
        & subst "$($drive):" $Root
        if ($LASTEXITCODE -ne 0) { throw "subst $($drive): failed" }
        $env:AUDIOMANAGER_TEST_ROOT = "$($drive):\"
        $exeArgs = @(& $ArgsFor $drive)
        $p = Start-Process -FilePath $Exe -ArgumentList $exeArgs -WorkingDirectory $RunDir -RedirectStandardInput $stdin -RedirectStandardOutput $console -RedirectStandardError $errPath -NoNewWindow -PassThru
        $null = $p.Handle
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            Stop-Process -Id $p.Id -Force
            throw "KILLED: $Label exceeded $TimeoutSec sec"
        }
        $p.Refresh()
        return $p.ExitCode
    } finally {
        $env:AUDIOMANAGER_TEST_ROOT = $prevTestRoot
        & subst "$($drive):" /D | Out-Null
    }
}

# Real integrate on the copy. Returns the exit code.
function Invoke-IntegrateOnCopy {
    param([string]$Exe, [string]$Root, [string]$Tier2Dir, [int]$TimeoutSec)
    return (Invoke-ExeOnCopy -Exe $Exe -Root $Root -RunDir $Tier2Dir -Label 'integrate' -TimeoutSec $TimeoutSec -ArgsFor {
        param($drive) @('integrate', '--json-output', '--no-input', '--manifest', "$($drive):\manifest.json")
    })
}

# Read-only libfix plan on the copy (--libfix-plan writes Root\logs\libfix-plan.json). Copies it to Tier2Dir and returns its path.
function Invoke-LibfixPlanOnCopy {
    param([string]$Exe, [string]$Root, [string]$Tier2Dir, [int]$TimeoutSec)
    $code = Invoke-ExeOnCopy -Exe $Exe -Root $Root -RunDir $Tier2Dir -Label 'libfix' -TimeoutSec $TimeoutSec -ArgsFor { param($drive) @('--libfix-plan') }
    if ($code -ne 0) { throw "libfix-plan exited $code" }
    $plan = Join-Path $Root 'logs\libfix-plan.json'
    if (-not (Test-Path -LiteralPath $plan -PathType Leaf)) { throw "libfix-plan.json not written under $Root\logs" }
    $dest = Join-Path $Tier2Dir 'libfix-plan.json'
    Copy-Item -LiteralPath $plan -Destination $dest -Force
    return $dest
}

# Hit kinds a human decides on. Any other kind in hits[] is an engine fault for the verdict rule.
function Get-DavidHitKinds {
    return @('unwanted_text', 'duplicate', 'missing_tag', 'filename_mismatch', 'album_in_singles')
}

# Pure: hits[] objects (kind, path, detail) in, kind -> count map out, sorted by kind. Null entries are ignored.
function Get-HitKindCounts {
    param([object[]]$Hits)
    $counts = @{}
    $list = @($Hits | Where-Object { $null -ne $_ -and [string]$_.kind -ne '' })
    foreach ($h in $list) {
        $k = [string]$h.kind
        if ($counts.ContainsKey($k)) { $counts[$k]++ } else { $counts[$k] = 1 }
    }
    $sorted = [ordered]@{}
    foreach ($k in @($counts.Keys | Sort-Object)) { $sorted[$k] = $counts[$k] }
    return $sorted
}

# Pure: verdict for the batch check summary. Checked in this order (one place, do not split it):
#   fail   - errors > 0, or SourcesUnchanged false, or tier-1 blocked.
#   pass   - tier-1 ready; or every hit kind is one a human decides (Get-DavidHitKinds) AND errors is exactly 0
#            AND SourcesUnchanged AND mirror status ok or libchecker_issues.
#   review - anything else. Errors null (integrate not run) can never pass through the second rule.
function Get-RehearsalVerdict {
    param([string]$Tier1Verdict, $Errors, [bool]$SourcesUnchanged, [string]$MirrorStatus, [object[]]$Hits)
    if ($null -ne $Errors -and [int]$Errors -gt 0) { return 'fail' }
    if (-not $SourcesUnchanged) { return 'fail' }
    if ($Tier1Verdict -eq 'blocked') { return 'fail' }
    if ($Tier1Verdict -eq 'ready') { return 'pass' }
    $davidKinds = Get-DavidHitKinds
    $counts = Get-HitKindCounts -Hits $Hits
    $onlyDavid = $true
    foreach ($k in $counts.Keys) { if ($davidKinds -notcontains $k) { $onlyDavid = $false } }
    $mirrorOk = ($MirrorStatus -eq 'ok' -or $MirrorStatus -eq 'libchecker_issues')
    if ($null -ne $Errors -and [int]$Errors -eq 0 -and $onlyDavid -and $mirrorOk) { return 'pass' }
    return 'review'
}

# Finds the newest other rehearsal-* sibling of WorkDir (same parent) that has a summary.json, by its write time.
# Returns that summary's hitsByKind as a hashtable (an empty one when it had no hits), or $null when there is none.
function Get-PreviousHitsByKind {
    param([string]$WorkDir)
    $parent = Split-Path -Parent $WorkDir
    $self = Split-Path -Leaf $WorkDir
    if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) { return $null }
    $best = $null
    foreach ($d in @(Get-ChildItem -LiteralPath $parent -Directory -Filter 'rehearsal-*' -ErrorAction SilentlyContinue)) {
        if ($d.Name -eq $self) { continue }
        $sj = Join-Path $d.FullName 'summary.json'
        if (-not (Test-Path -LiteralPath $sj -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $sj
        if ($null -eq $best -or $item.LastWriteTime -gt $best.LastWriteTime) { $best = $item }
    }
    if ($null -eq $best) { return $null }
    $map = @{}
    try {
        $obj = Get-Content -Raw -Encoding UTF8 -LiteralPath $best.FullName | ConvertFrom-Json
        if ($null -ne $obj.hitsByKind) {
            foreach ($p in $obj.hitsByKind.PSObject.Properties) { $map[$p.Name] = [int]$p.Value }
        }
    } catch { return $null }
    return $map
}

# Pure helpers for Build-RehearsalSummary.
function Get-CountValue {
    param($Counts, [string]$Name)
    if ($null -eq $Counts) { return $null }
    if ($null -eq $Counts.PSObject.Properties[$Name]) { return $null }
    return $Counts.$Name
}

function Get-ShortPath {
    param([string]$Path)
    if ($Path.Length -le 120) { return $Path }
    return ($Path.Substring(0, 117) + '...')
}

# Pure: builds the batch check summary in a fixed layout (title, field table, hits by kind, changes since previous run,
# needs David). Returns {Markdown, Summary}; Summary is the summary.json shape, hitsByKind is the map the next run diffs against.
function Build-RehearsalSummary {
    param(
        [string]$Verdict,
        [string]$MirrorStatus,
        $ExecCounts,
        [object[]]$Hits = @(),
        $LibfixMoves,
        [bool]$SourcesUnchanged,
        [System.Collections.IDictionary]$Previous
    )
    $hitList = @($Hits | Where-Object { $null -ne $_ })
    $counts = Get-HitKindCounts -Hits $hitList
    $fileVals = [ordered]@{
        files            = Get-CountValue -Counts $ExecCounts -Name 'files'
        routed           = Get-CountValue -Counts $ExecCounts -Name 'routed'
        skipped          = Get-CountValue -Counts $ExecCounts -Name 'skipped'
        errors           = Get-CountValue -Counts $ExecCounts -Name 'errors'
        libraryDuplicates = Get-CountValue -Counts $ExecCounts -Name 'libraryDuplicates'
    }

    $md = New-Object System.Collections.Generic.List[string]
    $md.Add("# Batch check: $Verdict")
    $md.Add('')
    $md.Add('| field | value |')
    $md.Add('| --- | --- |')
    foreach ($k in $fileVals.Keys) {
        $v = if ($null -eq $fileVals[$k]) { 'n/a' } else { [string]$fileVals[$k] }
        $md.Add("| $k | $v |")
    }
    $mirrorText = if ($MirrorStatus) { $MirrorStatus } else { 'none' }
    $libfixText = if ($null -eq $LibfixMoves) { 'not run' } else { [string]$LibfixMoves }
    $sourcesText = if ($SourcesUnchanged) { 'true' } else { 'false' }
    $md.Add("| mirror status | $mirrorText |")
    $md.Add("| libfix moves planned | $libfixText |")
    $md.Add("| sourcesUnchanged | $sourcesText |")

    $md.Add('')
    $md.Add('## Hits by kind')
    $md.Add('')
    $md.Add('| kind | count |')
    $md.Add('| --- | --- |')
    if ($counts.Count -eq 0) { $md.Add('| none | 0 |') }
    foreach ($k in $counts.Keys) { $md.Add("| $k | $($counts[$k]) |") }

    $md.Add('')
    $md.Add('## Changes since previous run')
    $md.Add('')
    if ($null -eq $Previous) {
        $md.Add('first run')
    } else {
        $changed = $false
        $allKinds = @(@($counts.Keys) + @($Previous.Keys) | Sort-Object -Unique)
        foreach ($k in $allKinds) {
            $now = if ($counts.Contains($k)) { [int]$counts[$k] } else { 0 }
            $before = if ($Previous.Contains($k)) { [int]$Previous[$k] } else { 0 }
            $delta = $now - $before
            if ($delta -gt 0) { $md.Add("new: $k +$delta"); $changed = $true }
            if ($delta -lt 0) { $md.Add("gone: $k -$(-$delta)"); $changed = $true }
        }
        if (-not $changed) { $md.Add('no change') }
    }

    $md.Add('')
    $md.Add('## Needs David')
    $md.Add('')
    $needs = 0
    foreach ($k in $counts.Keys) {
        if ((Get-DavidHitKinds) -notcontains $k) { continue }
        $needs++
        $n = [int]$counts[$k]
        $paths = @($hitList | Where-Object { [string]$_.kind -eq $k } | ForEach-Object { Get-ShortPath -Path ([string]$_.path) })
        $shown = @($paths | Select-Object -First 5)
        $more = $paths.Count - $shown.Count
        $noun = if ($n -eq 1) { 'hit' } else { 'hits' }
        $line = "- ${k}: $n $noun"
        if ($k -eq 'album_in_singles') { $line += ", libfix moves planned: $libfixText" }
        $line += ". First $($shown.Count): " + ($shown -join '; ')
        if ($more -gt 0) { $line += " (+$more more)" }
        $md.Add($line + '.')
    }
    if ($needs -eq 0) { $md.Add('nothing') }

    $changeLines = @($md | Where-Object { $_ -like 'new: *' -or $_ -like 'gone: *' })
    $summary = [ordered]@{
        verdict          = $Verdict
        files            = $fileVals['files']
        routed           = $fileVals['routed']
        skipped          = $fileVals['skipped']
        errors           = $fileVals['errors']
        libraryDuplicates = $fileVals['libraryDuplicates']
        mirrorStatus     = $MirrorStatus
        libfixMoves      = $LibfixMoves
        sourcesUnchanged = $SourcesUnchanged
        hitsByKind       = $counts
        changes          = $changeLines
    }
    return [pscustomobject]@{
        Markdown = (($md -join "`n") + "`n")
        Summary  = $summary
    }
}

# Deletes WorkDir\root only. Refuses unless RootPath (default WorkDir\root) ends in \root and sits directly under WorkDir.
# Called only after the source guard has passed.
function Remove-RehearsalRoot {
    param([string]$WorkDir, [string]$RootPath)
    if (-not $RootPath) { $RootPath = Join-Path $WorkDir 'root' }
    $full = [IO.Path]::GetFullPath($RootPath).TrimEnd('\')
    $wd = [IO.Path]::GetFullPath($WorkDir).TrimEnd('\')
    $leaf = Split-Path -Leaf $full
    $parent = Split-Path -Parent $full
    if ($leaf -ne 'root' -or -not ($parent -ieq $wd)) { throw "refusing to delete: $full is not <WorkDir>\root ($wd\root)" }
    if (-not (Test-Path -LiteralPath $full)) { return $false }
    Remove-Item -LiteralPath $full -Recurse -Force
    return $true
}

function Invoke-BatchRehearsal {
    param(
        [string]$Exe,
        [string]$WorkDir,
        [string]$SourceNewMusic,
        [string]$SourceLibrary,
        [string]$SourceMirror,
        [switch]$Integrate,
        [switch]$SpellingFromSuggested,
        [switch]$KeepCopy
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
        $hits = @()
        $libfixMoves = $null

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
                $mrObj = Get-Content -Raw -Encoding UTF8 -LiteralPath $mr | ConvertFrom-Json
                $st = $mrObj.status
                if ($null -ne $st) { $mirrorStatus = [string]$st }
                $hits = @($mrObj.hits | Where-Object { $null -ne $_ })
            }
            $ex = Get-ChildItem -LiteralPath $tier2 -Filter 'execution-*.json' -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($ex) {
                $execSummary = (Get-Content -Raw -Encoding UTF8 -LiteralPath $ex.FullName | ConvertFrom-Json).summary.counts
            }

            $planPath = Invoke-LibfixPlanOnCopy -Exe $Exe -Root $root -Tier2Dir $tier2 -TimeoutSec 600
            $plan = Get-Content -Raw -Encoding UTF8 -LiteralPath $planPath | ConvertFrom-Json
            $libfixMoves = if ($null -eq $plan.moves) { 0 } else { @($plan.moves).Count }
        }

        [pscustomobject]@{
            tier1Verdict     = $verdict
            integrated       = $did
            integrateExit    = $exit
            mirrorStatus     = $mirrorStatus
            executionSummary = $execSummary
            hits             = $hits
            libfixMoves      = $libfixMoves
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

    # The source guard has passed by now (Invoke-WithSourceGuard throws otherwise), so the copy may be cleaned.
    $summaryText = $null
    $summaryPath = $null
    $cleanLine = $null
    if ($state.integrated) {
        $previous = Get-PreviousHitsByKind -WorkDir $WorkDir
        $errs = $null
        if ($null -ne $state.executionSummary) { $errs = $state.executionSummary.errors }
        $verdict = Get-RehearsalVerdict -Tier1Verdict ([string]$state.tier1Verdict) -Errors $errs -SourcesUnchanged $true -MirrorStatus $state.mirrorStatus -Hits $state.hits
        $built = Build-RehearsalSummary -Verdict $verdict -MirrorStatus $state.mirrorStatus -ExecCounts $state.executionSummary -Hits $state.hits -LibfixMoves $state.libfixMoves -SourcesUnchanged $true -Previous $previous
        $summaryPath = Join-Path $WorkDir 'summary.md'
        [IO.File]::WriteAllText($summaryPath, $built.Markdown, (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText((Join-Path $WorkDir 'summary.json'), (ConvertTo-Json -InputObject $built.Summary -Depth 6), (New-Object Text.UTF8Encoding($false)))
        if ($verdict -eq 'pass' -and -not $KeepCopy) {
            $null = Remove-RehearsalRoot -WorkDir $WorkDir
            $cleanLine = 'cleaned root copy'
        } else {
            $cleanLine = 'kept root copy'
        }
        $summaryText = $built.Markdown.TrimEnd()
    }

    return [pscustomobject]@{
        Verdict       = $state.tier1Verdict
        Integrated    = [bool]$state.integrated
        IntegrateExit = $state.integrateExit
        Mirror        = $state.mirrorStatus
        OutDir        = $WorkDir
        CleanLine     = $cleanLine
        SummaryText   = $summaryText
        SummaryPath   = $summaryPath
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $r = Invoke-BatchRehearsal @cliArgs
        if ($r.Integrated) { Write-Output "integrate exit=$($r.IntegrateExit)" }
        $mirrorText = if ($r.Mirror) { $r.Mirror } else { 'none' }
        Write-Output "rehearsal verdict=$($r.Verdict) mirror=$mirrorText outDir=$($r.OutDir)"
        if ($r.SummaryText) {
            Write-Output $r.CleanLine
            Write-Output $r.SummaryText
            Write-Output "summary: $($r.SummaryPath)"
        }
        exit 0
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 1
    }
}
