#Requires -Version 5.1
# Snapshot guard: detect filesystem changes under a directory tree.
# Dot-source this file. It defines functions only and has no side effects.
#   . (Join-Path $PSScriptRoot 'snapshot-guard.ps1')

function Get-TreeSnapshot {
    param([Parameter(Mandatory)][string]$Path)
    $snap = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $snap }
    $root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\', '/')
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/')
        $snap[$rel] = "$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
    }
    return $snap
}

function Compare-TreeSnapshot {
    param($Before, $After)
    $diffs = New-Object System.Collections.Generic.List[string]
    foreach ($k in ($After.Keys | Sort-Object)) {
        if (-not $Before.ContainsKey($k)) {
            $diffs.Add("ADDED $k")
        } elseif ($Before[$k] -ne $After[$k]) {
            $diffs.Add("CHANGED $k")
        }
    }
    foreach ($k in ($Before.Keys | Sort-Object)) {
        if (-not $After.ContainsKey($k)) { $diffs.Add("REMOVED $k") }
    }
    return $diffs.ToArray()
}

function Assert-TreeUnchanged {
    param([string]$Name, $Before, $After)
    $diffs = @(Compare-TreeSnapshot -Before $Before -After $After)
    if ($diffs.Count -gt 0) {
        $shown = $diffs | Select-Object -First 20
        Write-Error ("Tree changed [$Name]: $($diffs.Count) diff(s)`n" + ($shown -join "`n"))
        throw "Tree changed [$Name]: $($diffs.Count) diff(s)"
    }
    return $true
}

function Get-GitHead {
    param([Parameter(Mandatory)][string]$Repo)
    return (git -C $Repo rev-parse HEAD)
}
