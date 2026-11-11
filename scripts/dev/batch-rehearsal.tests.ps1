#Requires -Version 5.1
# Tests for batch-rehearsal.ps1. Uses only fake folders under $env:TEMP, never the real sources.
# Run: powershell -NoProfile -File scripts/dev/batch-rehearsal.tests.ps1
# Exit 0 when all cases pass, 1 when any case fails.

. (Join-Path $PSScriptRoot 'batch-rehearsal.ps1')

$script:Failures = 0
$script:Total = 0
$script:Base = Join-Path $env:TEMP ('batchrehearsal-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:Base -Force | Out-Null

function New-CaseDir {
    param([string]$Name)
    $d = Join-Path $script:Base $Name
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    return $d
}

function Write-Text {
    param([string]$Path, [string]$Content)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($false)))
}

function Invoke-Case {
    param([string]$Name, [scriptblock]$Body)
    $script:Total++
    try {
        $ok = & $Body
        if ($ok -eq $true) {
            Write-Output "[PASS] $Name"
        } else {
            Write-Output "[FAIL] $Name"
            $script:Failures++
        }
    } catch {
        Write-Output "[FAIL] $Name (threw: $($_.Exception.Message))"
        $script:Failures++
    }
}

# Fake sources: three small folders with a few files each. Returns an ordered map NewMusic/Audio/AudioMirror -> folder.
function New-FakeSources {
    param([string]$Name)
    $base = New-CaseDir $Name
    $sources = [ordered]@{
        NewMusic    = Join-Path $base 'src-NewMusic'
        Audio       = Join-Path $base 'src-Audio'
        AudioMirror = Join-Path $base 'src-AudioMirror'
    }
    Write-Text (Join-Path $sources.NewMusic 'Artist - Song.mp3') 'new-music-bytes'
    Write-Text (Join-Path $sources.NewMusic 'sub\Other - Tune.mp3') 'other-bytes'
    Write-Text (Join-Path $sources.Audio 'Album\Track.mp3') 'library-bytes'
    Write-Text (Join-Path $sources.AudioMirror 'README.md') 'mirror-bytes'
    return $sources
}

# A routing record shaped like the exe's routing-*.json: files[] rows plus artistSpellingCandidates[].
function New-SampleRouting {
    return (ConvertFrom-Json @'
{"files":[
 {"filename":"Dup - Song.mp3","sourceRel":"Dup\\Dup - Song.mp3","artist":"Dup","title":"Song","libraryDuplicate":true,"dupLibraryTrack":"Dup - Song"},
 {"filename":"New - Thing.mp3","sourceRel":"New - Thing.mp3","artist":"New","title":"Thing","libraryDuplicate":false}
],"artistSpellingCandidates":[{"key":"charli xcx","suggested":"Charli XCX","spellings":[]}]}
'@)
}

Invoke-Case 'copy: every source file lands under root with equal bytes' {
    $s = New-FakeSources 'copy-content'
    $root = Join-Path (New-CaseDir 'copy-content-root') 'root'
    Copy-RehearsalSources -Sources $s -RootDir $root | Out-Null
    $ok = $true
    foreach ($k in $s.Keys) {
        $srcRoot = $s[$k].TrimEnd('\')
        foreach ($f in @(Get-ChildItem -LiteralPath $srcRoot -Recurse -File)) {
            $rel = $f.FullName.Substring($srcRoot.Length).TrimStart('\')
            $dst = Join-Path (Join-Path $root $k) $rel
            if (-not (Test-Path -LiteralPath $dst -PathType Leaf)) { $ok = $false; continue }
            if ((Get-FileHash -LiteralPath $dst).Hash -ne (Get-FileHash -LiteralPath $f.FullName).Hash) { $ok = $false }
        }
    }
    return $ok
}

Invoke-Case 'copy: a copy is not a hardlink (editing the copy leaves the source bytes unchanged)' {
    $s = New-FakeSources 'copy-not-link'
    $root = Join-Path (New-CaseDir 'copy-not-link-root') 'root'
    Copy-RehearsalSources -Sources $s -RootDir $root | Out-Null
    $copy = Join-Path $root 'NewMusic\Artist - Song.mp3'
    $src = Join-Path $s.NewMusic 'Artist - Song.mp3'
    $existed = Test-Path -LiteralPath $copy -PathType Leaf
    $attrs = (Get-Item -LiteralPath $copy).Attributes
    $notReparse = (($attrs -band [IO.FileAttributes]::ReparsePoint) -eq 0)
    $srcBefore = [IO.File]::ReadAllText($src)
    [IO.File]::WriteAllText($copy, 'edited-inside-the-copy')
    $srcAfter = [IO.File]::ReadAllText($src)
    return ($existed -and $notReparse -and ($srcBefore -eq $srcAfter))
}

Invoke-Case 'copy: a missing source folder throws' {
    $s = New-FakeSources 'copy-missing'
    $s['Audio'] = Join-Path $s['Audio'] 'does-not-exist'
    $root = Join-Path (New-CaseDir 'copy-missing-root') 'root'
    $threw = $false
    try { Copy-RehearsalSources -Sources $s -RootDir $root | Out-Null } catch { $threw = $true }
    return $threw
}

Invoke-Case 'guard: a body that modifies a source file throws REAL FOLDER TOUCHED' {
    $s = New-FakeSources 'guard-touch'
    $mirror = New-CaseDir 'guard-touch-mirror'
    $msg = $null
    try {
        Invoke-WithSourceGuard -Sources $s -MirrorRepo $mirror -Body {
            [IO.File]::WriteAllText((Join-Path $s.NewMusic 'Artist - Song.mp3'), 'touched-with-longer-content')
        } | Out-Null
    } catch { $msg = $_.Exception.Message }
    return ([string]$msg -like '*REAL FOLDER TOUCHED*')
}

Invoke-Case 'guard: a body that leaves the sources alone passes and returns its output' {
    $s = New-FakeSources 'guard-clean'
    $mirror = New-CaseDir 'guard-clean-mirror'
    $got = Invoke-WithSourceGuard -Sources $s -MirrorRepo $mirror -Body { 'body-value' }
    return ($got -eq 'body-value')
}

Invoke-Case 'workdir: a non-empty folder is refused' {
    $d = New-CaseDir 'workdir-full'
    Write-Text (Join-Path $d 'leftover.txt') 'x'
    $threw = $false
    try { Assert-WorkDirEmpty -WorkDir $d } catch { $threw = $true }
    return $threw
}

Invoke-Case 'workdir: an empty or missing folder is accepted' {
    $empty = New-CaseDir 'workdir-empty'
    $missing = Join-Path $script:Base 'workdir-missing'
    $threw = $false
    try { Assert-WorkDirEmpty -WorkDir $empty; Assert-WorkDirEmpty -WorkDir $missing } catch { $threw = $true }
    return (-not $threw)
}

Invoke-Case 'batch: Invoke-BatchRehearsal refuses a non-empty WorkDir before copying anything' {
    $d = New-CaseDir 'batch-refuse'
    Write-Text (Join-Path $d 'x.txt') 'x'
    $s = New-FakeSources 'batch-refuse-src'
    $msg = $null
    try {
        Invoke-BatchRehearsal -Exe $PSCommandPath -WorkDir $d -SourceNewMusic $s.NewMusic -SourceLibrary $s.Audio -SourceMirror $s.AudioMirror | Out-Null
    } catch { $msg = $_.Exception.Message }
    return (([string]$msg -like '*not empty*') -and (-not (Test-Path -LiteralPath (Join-Path $d 'root'))))
}

Invoke-Case 'readme: first line carries DELETE-AFTER six days out plus the integration clause' {
    $d = New-CaseDir 'readme'
    Write-RehearsalReadme -WorkDir $d -Today ([datetime]'2026-11-11') | Out-Null
    $first = Get-Content -LiteralPath (Join-Path $d 'README.md') -TotalCount 1
    return ($first -eq 'DELETE-AFTER: 2026-11-17 (and once the real integration is confirmed finished)')
}

Invoke-Case 'manifest: object form with tracks, dupResolution K only on libraryDuplicate rows' {
    $m = Build-RehearsalManifest -Routing (New-SampleRouting)
    $rows = @($m.tracks)
    $okCount = ($rows.Count -eq 2)
    $okDup = ($rows[0]['dupResolution'] -eq 'K')
    $okNoDup = (-not $rows[1].Contains('dupResolution'))
    $okFields = ($rows[0]['filename'] -eq 'Dup - Song.mp3') -and ($rows[0]['sourceRel'] -eq 'Dup\Dup - Song.mp3') -and ($rows[0]['artist'] -eq 'Dup') -and ($rows[0]['title'] -eq 'Song')
    $json = ConvertTo-Json -InputObject $m -Depth 6
    $okObj = ($json.TrimStart().StartsWith('{')) -and (@((ConvertFrom-Json $json).tracks).Count -eq 2)
    return ($okCount -and $okDup -and $okNoDup -and $okFields -and $okObj)
}

Invoke-Case 'manifest: no files gives empty tracks and artistSpellings arrays in JSON' {
    $m = Build-RehearsalManifest -Routing (ConvertFrom-Json '{"files":[]}')
    $json = ConvertTo-Json -InputObject $m -Depth 6
    return (($json -match '"tracks":\s*\[\s*\]') -and ($json -match '"artistSpellings":\s*\[\s*\]'))
}

Invoke-Case 'manifest: spelling entries appear only with -SpellingFromSuggested' {
    $r = New-SampleRouting
    $without = Build-RehearsalManifest -Routing $r
    $with = Build-RehearsalManifest -Routing $r -SpellingFromSuggested
    $sp = @($with.artistSpellings)
    return ((@($without.artistSpellings).Count -eq 0) -and ($sp.Count -eq 1) -and ($sp[0]['key'] -eq 'charli xcx') -and ($sp[0]['spelling'] -eq 'Charli XCX'))
}

# Last case: override the mirror HEAD reader so a HEAD move inside the guarded body is caught.
$script:FakeHead = 'head-a'
function Get-MirrorHeadOrNone {
    param([string]$Path)
    return [string]$script:FakeHead
}

Invoke-Case 'guard: a moved mirror HEAD throws REAL FOLDER TOUCHED' {
    $s = New-FakeSources 'guard-head'
    $msg = $null
    try {
        Invoke-WithSourceGuard -Sources $s -MirrorRepo 'unused' -Body { $script:FakeHead = 'head-b' } | Out-Null
    } catch { $msg = $_.Exception.Message }
    return ([string]$msg -like '*REAL FOLDER TOUCHED*')
}

# Batch check summary (BL-152): pure functions, no exe. Fake hit objects carry kind and path like mirror-result hits[].
function New-Hit {
    param([string]$Kind, [string]$Path)
    return [pscustomobject]@{ kind = $Kind; path = $Path; detail = 'detail' }
}

function Get-Lines {
    param([string]$Text)
    return @($Text -split "`n" | ForEach-Object { $_.TrimEnd("`r") })
}

function New-SampleSummary {
    param([object[]]$Hits = @(), $Previous = $null, $Errors = 0, [string]$Verdict = 'pass')
    return Build-RehearsalSummary -Verdict $Verdict -MirrorStatus 'ok' `
        -ExecCounts ([pscustomobject]@{ files = 10; routed = 8; skipped = 1; errors = $Errors; libraryDuplicates = 1 }) `
        -Hits $Hits -LibfixMoves 2 -SourcesUnchanged $true -Previous $Previous
}

Invoke-Case 'summary: headings appear in the fixed order' {
    $lines = Get-Lines (New-SampleSummary -Hits @((New-Hit 'unwanted_text' 'A\b.mp3'))).Markdown
    $want = @('# Batch check: pass', '| field | value |', '## Hits by kind', '## Changes since previous run', '## Needs David')
    $idx = @()
    foreach ($w in $want) { $idx += [array]::IndexOf($lines, $w) }
    if ($idx -contains -1) { return $false }
    for ($i = 1; $i -lt $idx.Count; $i++) { if ($idx[$i] -le $idx[$i - 1]) { return $false } }
    return $true
}

Invoke-Case 'summary: field table carries every required field' {
    $lines = Get-Lines (New-SampleSummary).Markdown
    $fields = @('files', 'routed', 'skipped', 'errors', 'libraryDuplicates', 'mirror status', 'libfix moves planned', 'sourcesUnchanged')
    foreach ($f in $fields) { if (-not ($lines -like "| $f |*")) { return $false } }
    return $true
}

Invoke-Case 'summary: hits table is sorted by kind with counts' {
    $hits = @((New-Hit 'unwanted_text' 'a'), (New-Hit 'album_in_singles' 'b'), (New-Hit 'unwanted_text' 'c'))
    $lines = Get-Lines (New-SampleSummary -Hits $hits).Markdown
    $start = [array]::IndexOf($lines, '| kind | count |')
    return (($lines[$start + 2] -eq '| album_in_singles | 1 |') -and ($lines[$start + 3] -eq '| unwanted_text | 2 |'))
}

Invoke-Case 'summary: empty hits give a none row' {
    $lines = Get-Lines (New-SampleSummary -Hits @()).Markdown
    $start = [array]::IndexOf($lines, '| kind | count |')
    return ($lines[$start + 2] -eq '| none | 0 |')
}

Invoke-Case 'summary: diff prints new and gone with the count change' {
    $hits = @((New-Hit 'unwanted_text' 'a'), (New-Hit 'unwanted_text' 'b'), (New-Hit 'unwanted_text' 'c'), (New-Hit 'duplicate' 'd'))
    $lines = Get-Lines (New-SampleSummary -Hits $hits -Previous @{ unwanted_text = 1; missing_tag = 2 }).Markdown
    return (($lines -contains 'new: unwanted_text +2') -and ($lines -contains 'new: duplicate +1') -and ($lines -contains 'gone: missing_tag -2') -and -not ($lines -contains 'first run'))
}

Invoke-Case 'summary: no previous summary prints first run' {
    $lines = Get-Lines (New-SampleSummary -Hits @((New-Hit 'duplicate' 'a'))).Markdown
    return (($lines -contains 'first run') -and -not ($lines -contains 'new: duplicate +1'))
}

Invoke-Case 'summary: an empty previous map is a real previous run, not first run' {
    $lines = Get-Lines (New-SampleSummary -Hits @((New-Hit 'duplicate' 'a')) -Previous @{}).Markdown
    return (($lines -contains 'new: duplicate +1') -and -not ($lines -contains 'first run'))
}

Invoke-Case 'summary: identical counts print no change' {
    $lines = Get-Lines (New-SampleSummary -Hits @((New-Hit 'duplicate' 'a')) -Previous @{ duplicate = 1 }).Markdown
    return ($lines -contains 'no change')
}

Invoke-Case 'needs David: names the kind and caps the paths at five' {
    $hits = @(1..7 | ForEach-Object { New-Hit 'unwanted_text' "Artist\Track$_.mp3" })
    $hits += New-Hit 'artist_folder_mismatch' 'X\y.mp3'
    $lines = Get-Lines (New-SampleSummary -Hits $hits).Markdown
    $start = [array]::IndexOf($lines, '## Needs David')
    $section = (@($lines[($start + 1)..($lines.Count - 1)]) -join "`n")
    return (($section -match '- unwanted_text: 7 hits') -and ($section -match 'Track5\.mp3') -and -not ($section -match 'Track6\.mp3') -and ($section -match '\(\+2 more\)') -and -not ($section -match 'artist_folder_mismatch'))
}

Invoke-Case 'needs David: nothing when no hit needs a decision' {
    $lines = Get-Lines (New-SampleSummary -Hits @()).Markdown
    $start = [array]::IndexOf($lines, '## Needs David')
    return ($lines[$start + 2] -eq 'nothing')
}

Invoke-Case 'needs David: album_in_singles bullet names the libfix move count' {
    $lines = Get-Lines (New-SampleSummary -Hits @((New-Hit 'album_in_singles' 'A\Singles\x.mp3'))).Markdown
    return [bool]($lines | Where-Object { $_ -like '- album_in_singles: 1 hit*libfix moves planned: 2*' })
}

Invoke-Case 'summary json: structured form carries verdict and hitsByKind' {
    $r = New-SampleSummary -Hits @((New-Hit 'duplicate' 'a'), (New-Hit 'duplicate' 'b')) -Verdict 'fail'
    return (($r.Summary.verdict -eq 'fail') -and ($r.Summary.hitsByKind['duplicate'] -eq 2))
}

Invoke-Case 'verdict: ready with no errors passes' {
    return ((Get-RehearsalVerdict -Tier1Verdict 'ready' -Errors 0 -SourcesUnchanged $true -MirrorStatus $null -Hits @()) -eq 'pass')
}

Invoke-Case 'verdict: any errors fail even when tier-1 is ready' {
    return ((Get-RehearsalVerdict -Tier1Verdict 'ready' -Errors 1 -SourcesUnchanged $true -MirrorStatus $null -Hits @()) -eq 'fail')
}

Invoke-Case 'verdict: tier-1 blocked fails' {
    return ((Get-RehearsalVerdict -Tier1Verdict 'blocked' -Errors $null -SourcesUnchanged $true -MirrorStatus $null -Hits @()) -eq 'fail')
}

Invoke-Case 'verdict: sources changed fails' {
    return ((Get-RehearsalVerdict -Tier1Verdict 'review' -Errors 0 -SourcesUnchanged $false -MirrorStatus 'ok' -Hits @()) -eq 'fail')
}

Invoke-Case 'verdict: review with only David kinds, no errors, mirror ok passes' {
    $hits = @((New-Hit 'unwanted_text' 'a'), (New-Hit 'album_in_singles' 'b'))
    return ((Get-RehearsalVerdict -Tier1Verdict 'review' -Errors 0 -SourcesUnchanged $true -MirrorStatus 'libchecker_issues' -Hits $hits) -eq 'pass')
}

Invoke-Case 'verdict: review with an engine-fault kind is review, not pass' {
    $hits = @((New-Hit 'unwanted_text' 'a'), (New-Hit 'artist_folder_mismatch' 'b'))
    return ((Get-RehearsalVerdict -Tier1Verdict 'review' -Errors 0 -SourcesUnchanged $true -MirrorStatus 'ok' -Hits $hits) -eq 'review')
}

Invoke-Case 'verdict: a failed mirror commit with only David kinds is review' {
    return ((Get-RehearsalVerdict -Tier1Verdict 'review' -Errors 0 -SourcesUnchanged $true -MirrorStatus 'mirror_commit_failed' -Hits @((New-Hit 'duplicate' 'a'))) -eq 'review')
}

Invoke-Case 'verdict: unknown errors (integrate not run) is review, not pass' {
    return ((Get-RehearsalVerdict -Tier1Verdict 'review' -Errors $null -SourcesUnchanged $true -MirrorStatus $null -Hits @((New-Hit 'duplicate' 'a'))) -eq 'review')
}

Invoke-Case 'clean: removes WorkDir\root and keeps summary and tier folders' {
    $d = New-CaseDir 'clean-ok'
    Write-Text (Join-Path $d 'root\NewMusic\a.mp3') 'x'
    Write-Text (Join-Path $d 'summary.md') 's'
    Write-Text (Join-Path $d 'tier1\routing-1.json') 'r'
    Remove-RehearsalRoot -WorkDir $d
    return ((-not (Test-Path -LiteralPath (Join-Path $d 'root'))) -and (Test-Path -LiteralPath (Join-Path $d 'summary.md')) -and (Test-Path -LiteralPath (Join-Path $d 'tier1\routing-1.json')))
}

Invoke-Case 'clean: refuses a root path that does not end in \root' {
    $d = New-CaseDir 'clean-refuse'
    Write-Text (Join-Path $d 'keep.txt') 'x'
    $msg = $null
    try { Remove-RehearsalRoot -WorkDir $d -RootPath $d } catch { $msg = $_.Exception.Message }
    return (([string]$msg -like '*refusing to delete*') -and (Test-Path -LiteralPath (Join-Path $d 'keep.txt')))
}

Invoke-Case 'clean: refuses a root that is not directly under WorkDir' {
    $d = New-CaseDir 'clean-outside'
    $other = Join-Path (New-CaseDir 'clean-outside-other') 'root'
    Write-Text (Join-Path $other 'x.txt') 'x'
    $msg = $null
    try { Remove-RehearsalRoot -WorkDir $d -RootPath $other } catch { $msg = $_.Exception.Message }
    return (([string]$msg -like '*refusing to delete*') -and (Test-Path -LiteralPath (Join-Path $other 'x.txt')))
}

Invoke-Case 'previous: picks the newest other rehearsal sibling summary.json' {
    $parent = New-CaseDir 'prev-parent'
    $old = Join-Path $parent 'rehearsal-old'
    Write-Text (Join-Path $old 'summary.json') '{"hitsByKind":{"duplicate":4}}'
    (Get-Item -LiteralPath (Join-Path $old 'summary.json')).LastWriteTime = (Get-Date).AddDays(-2)
    $new = Join-Path $parent 'rehearsal-new'
    Write-Text (Join-Path $new 'summary.json') '{"hitsByKind":{"duplicate":9}}'
    $current = Join-Path $parent 'rehearsal-now'
    $prev = Get-PreviousHitsByKind -WorkDir $current
    return ($prev['duplicate'] -eq 9)
}

Invoke-Case 'previous: no sibling summary gives null' {
    $parent = New-CaseDir 'prev-none'
    $prev = Get-PreviousHitsByKind -WorkDir (Join-Path $parent 'rehearsal-now')
    return ($null -eq $prev)
}

Write-Output ''
Write-Output "Total: $script:Total  Failed: $script:Failures"
if ($script:Failures -gt 0) { exit 1 }
exit 0
