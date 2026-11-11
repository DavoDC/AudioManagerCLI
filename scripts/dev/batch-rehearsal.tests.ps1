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

Write-Output ''
Write-Output "Total: $script:Total  Failed: $script:Failures"
if ($script:Failures -gt 0) { exit 1 }
exit 0
