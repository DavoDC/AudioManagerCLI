#Requires -Version 5.1
# Tests for snapshot-guard.ps1. Uses only fresh temp dirs under $env:TEMP.
# Run: powershell -NoProfile -File scripts/dev/snapshot-guard.tests.ps1
# Exit 0 when all cases pass, 1 when any case fails.

. (Join-Path $PSScriptRoot 'snapshot-guard.ps1')

$script:Failures = 0
$script:Base = Join-Path $env:TEMP ('snapguard-test-' + [guid]::NewGuid().ToString('N'))
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
    [System.IO.File]::WriteAllText($Path, $Content)
}

function Invoke-Case {
    param([string]$Name, [scriptblock]$Body)
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

# Shared fixture: a.txt and sub\b.txt
function New-Fixture {
    param([string]$Name)
    $d = New-CaseDir $Name
    Write-Text (Join-Path $d 'a.txt') 'aaa'
    Write-Text (Join-Path $d 'sub\b.txt') 'bbb'
    return $d
}

Invoke-Case 'identical tree gives no diffs' {
    $d = New-Fixture 'identical'
    $before = Get-TreeSnapshot -Path $d
    $after = Get-TreeSnapshot -Path $d
    $diffs = @(Compare-TreeSnapshot -Before $before -After $after)
    return ($diffs.Count -eq 0)
}

Invoke-Case 'snapshot covers nested files' {
    $d = New-Fixture 'nested'
    $snap = Get-TreeSnapshot -Path $d
    return ($snap.ContainsKey('a.txt') -and $snap.ContainsKey((Join-Path 'sub' 'b.txt')) -and $snap.Count -eq 2)
}

Invoke-Case 'modified file is CHANGED' {
    $d = New-Fixture 'changed'
    $before = Get-TreeSnapshot -Path $d
    Write-Text (Join-Path $d 'a.txt') 'bbbbbb'
    $after = Get-TreeSnapshot -Path $d
    $diffs = @(Compare-TreeSnapshot -Before $before -After $after)
    return ($diffs.Count -eq 1 -and $diffs[0] -eq 'CHANGED a.txt')
}

Invoke-Case 'added file is ADDED' {
    $d = New-Fixture 'added'
    $before = Get-TreeSnapshot -Path $d
    Write-Text (Join-Path $d 'c.txt') 'ccc'
    $after = Get-TreeSnapshot -Path $d
    $diffs = @(Compare-TreeSnapshot -Before $before -After $after)
    return ($diffs.Count -eq 1 -and $diffs[0] -eq 'ADDED c.txt')
}

Invoke-Case 'deleted file is REMOVED' {
    $d = New-Fixture 'removed'
    $before = Get-TreeSnapshot -Path $d
    Remove-Item -LiteralPath (Join-Path $d 'sub\b.txt') -Force
    $after = Get-TreeSnapshot -Path $d
    $diffs = @(Compare-TreeSnapshot -Before $before -After $after)
    return ($diffs.Count -eq 1 -and $diffs[0] -eq ('REMOVED ' + (Join-Path 'sub' 'b.txt')))
}

Invoke-Case 'Assert-TreeUnchanged throws on a touched file' {
    $d = New-Fixture 'assert-touched'
    $before = Get-TreeSnapshot -Path $d
    Write-Text (Join-Path $d 'a.txt') 'changed content here'
    $after = Get-TreeSnapshot -Path $d
    $threw = $false
    try {
        $null = Assert-TreeUnchanged -Name 'touched' -Before $before -After $after
    } catch {
        $threw = $true
    }
    return $threw
}

Invoke-Case 'Assert-TreeUnchanged returns true when untouched' {
    $d = New-Fixture 'assert-untouched'
    $before = Get-TreeSnapshot -Path $d
    $after = Get-TreeSnapshot -Path $d
    $r = Assert-TreeUnchanged -Name 'untouched' -Before $before -After $after
    return ($r -eq $true)
}

Invoke-Case 'missing dir gives empty snapshot' {
    $missing = Join-Path $script:Base 'does-not-exist'
    $snap = Get-TreeSnapshot -Path $missing
    return ($snap -is [hashtable] -and $snap.Count -eq 0)
}

Remove-Item -LiteralPath $script:Base -Recurse -Force -ErrorAction SilentlyContinue

if ($script:Failures -gt 0) {
    Write-Output "FAILED: $($script:Failures) case(s) failed"
    exit 1
}
Write-Output 'ALL PASS'
exit 0
