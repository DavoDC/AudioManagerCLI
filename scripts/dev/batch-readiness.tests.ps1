#Requires -Version 5.1
# Tests for batch-readiness.ps1 pure helpers only. No exe, no real folders; sample console text is written to a fresh temp dir.
# Run: powershell -NoProfile -File scripts/dev/batch-readiness.tests.ps1
# Exit 0 when all cases pass, 1 when any case fails.

. (Join-Path $PSScriptRoot 'batch-readiness.ps1')

$script:Failures = 0
$script:Total = 0
$script:Base = Join-Path $env:TEMP ('batchready-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:Base -Force | Out-Null

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

# Writes a sample console text file and returns its path. Lines are joined with CRLF, like the exe's console.
function Write-SampleConsole {
    param([string]$Name, [string[]]$Lines)
    $path = Join-Path $script:Base ($Name + '.txt')
    [IO.File]::WriteAllText($path, (($Lines -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))
    return $path
}

$projectedHeading = @(
    '===========================================================================',
    'Projected LibChecker (Dry Run)',
    '==========================================================================='
)

Invoke-Case 'hits counted from the projected section, Total hits lines excluded' {
    $lines = @('Checking library...', '') + $projectedHeading + @(
        ' - Checking all tags against filenames..',
        '  - Found version in title of Joyner Lucas Fall Slowly',
        '  - Found bonus in filename of Shaboozey Chrome',
        '  - Total hits: 2',
        ' - Checking all tags for unwanted or missing info...',
        '  - Total hits: 0',
        '',
        'Routed: 121  |  Skipped: 5',
        '',
        'Routing - time taken: 4.244 seconds'
    )
    $path = Write-SampleConsole 'two-hits' $lines
    $got = Get-ProjectedLibCheckerHits -Lines (Get-Content -LiteralPath $path)
    return ($got -eq 2)
}

Invoke-Case 'hit lines after the Routed line and before the heading are not counted' {
    $lines = @('  - Found before the heading') + $projectedHeading + @(
        '  - Found one',
        'Routed: 3  |  Skipped: 0',
        '  - Found after routed'
    )
    $path = Write-SampleConsole 'bounded' $lines
    $got = Get-ProjectedLibCheckerHits -Lines (Get-Content -LiteralPath $path)
    return ($got -eq 1)
}

Invoke-Case 'clean projected section gives zero hits' {
    $lines = $projectedHeading + @(
        ' - Checking all tags against filenames..',
        '  - Total hits: 0',
        'Routed: 40  |  Skipped: 8'
    )
    $path = Write-SampleConsole 'clean' $lines
    $got = Get-ProjectedLibCheckerHits -Lines (Get-Content -LiteralPath $path)
    return ($got -eq 0)
}

Invoke-Case 'skipped projected section gives null (unknown, not zero)' {
    $lines = $projectedHeading + @(
        ' - SKIP: could not load current library tags: example',
        'Routed: 40  |  Skipped: 8'
    )
    $path = Write-SampleConsole 'skipped' $lines
    $got = Get-ProjectedLibCheckerHits -Lines (Get-Content -LiteralPath $path)
    return ($null -eq $got)
}

Invoke-Case 'missing projected heading gives null' {
    $lines = @('  - Found something', 'Routed: 40  |  Skipped: 8')
    $path = Write-SampleConsole 'no-heading' $lines
    $got = Get-ProjectedLibCheckerHits -Lines (Get-Content -LiteralPath $path)
    return ($null -eq $got)
}

Invoke-Case 'section that never reaches Routed gives null (incomplete run)' {
    $lines = $projectedHeading + @('  - Found one')
    $path = Write-SampleConsole 'no-routed' $lines
    $got = Get-ProjectedLibCheckerHits -Lines (Get-Content -LiteralPath $path)
    return ($null -eq $got)
}

Invoke-Case 'verdict is ready when errors are 0 and hits are 0' {
    return ((Get-ReadinessVerdict -Errors 0 -Hits 0) -eq 'ready')
}

Invoke-Case 'verdict is review when there are routing errors' {
    return ((Get-ReadinessVerdict -Errors 1 -Hits 0) -eq 'review')
}

Invoke-Case 'verdict is review when projected hits are present' {
    return ((Get-ReadinessVerdict -Errors 0 -Hits 2) -eq 'review')
}

Invoke-Case 'verdict is review when projected hits are unknown' {
    return ((Get-ReadinessVerdict -Errors 0 -Hits $null) -eq 'review')
}

Invoke-Case 'gate block: passed status gives null' {
    $path = Write-SampleConsole 'gate-passed' @('{"status":"passed","action":"none","mirrorDirtyCount":0,"libCheckerIssues":0}')
    return ($null -eq (Get-GateBlock -GatePath $path))
}

Invoke-Case 'gate block: missing file gives null' {
    $path = Join-Path $script:Base 'gate-does-not-exist.json'
    return ($null -eq (Get-GateBlock -GatePath $path))
}

Invoke-Case 'gate block: mirror_dirty sample gives Status and Action' {
    $path = Write-SampleConsole 'gate-mirror-dirty' @('{"status":"mirror_dirty","action":"commit_mirror","mirrorDirtyCount":3,"libCheckerIssues":0}')
    $got = Get-GateBlock -GatePath $path
    return ($null -ne $got -and $got.Status -eq 'mirror_dirty' -and $got.Action -eq 'commit_mirror')
}

Invoke-Case 'gate block: mirror_dirty Detail joins mirrorDirtyCount and libCheckerIssues' {
    $path = Write-SampleConsole 'gate-detail' @('{"status":"mirror_dirty","action":"commit_mirror","mirrorDirtyCount":3,"libCheckerIssues":0}')
    $got = Get-GateBlock -GatePath $path
    return ($null -ne $got -and $got.Detail -eq 'mirrorDirtyCount 3; libCheckerIssues 0')
}

Invoke-Case 'gate block: malformed JSON gives null' {
    $path = Write-SampleConsole 'gate-malformed' @('{"status":"mirror_dirty",')
    return ($null -eq (Get-GateBlock -GatePath $path))
}

try {
    Remove-Item -LiteralPath $script:Base -Recurse -Force -ErrorAction SilentlyContinue
} catch { }

Write-Output ("{0} case(s), {1} failed" -f $script:Total, $script:Failures)
if ($script:Failures -gt 0) { exit 1 } else { exit 0 }
