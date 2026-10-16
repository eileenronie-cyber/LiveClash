<#
.SYNOPSIS
    Code coverage of the Revit-free logic, with a threshold. No Revit needed; part of ci\gate.ps1.

.DESCRIPTION
    Runs the headless checks (tests\LiveClash.LogicTests) under coverlet and measures how much of
    tests\LiveClash.Logic - the Revit-free Core files - they execute. Members marked [RevitBound]
    are left out of the number: they need a live Revit document, so no build machine can run them,
    and the in-Revit checks cover them instead. Keep those members thin;
    see Core\RevitBoundAttribute.cs.

    Fails when line or branch coverage drops below the thresholds. The thresholds are a RATCHET:
    set just under the measured value, raised when coverage rises, never lowered to make a change
    pass. Prints a per-file table so a drop points at its file.

    What this number does NOT include: the Revit-bound code (geometry, the updater, the UI), which
    is most of the add-in by line count. That code is covered by the in-Revit suite and the
    mutation rounds recorded in the compendium, not by a percentage.

.PARAMETER MinLine
    Minimum line coverage, percent.

.PARAMETER MinBranch
    Minimum branch coverage, percent.

.PARAMETER NoBuild
    Use the existing Release build of the test project.

MUST STAY ASCII (Windows PowerShell 5.1 reads this file as ANSI).
#>
[CmdletBinding()]
param(
    [double] $MinLine = 99,     # measured 99.55% on 2026-10-05
    [double] $MinBranch = 96,   # measured 97.63% on 2026-10-05
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'tests\LiveClash.LogicTests'
$bin  = Join-Path $proj 'bin\Release\net8.0-windows'
$out  = Join-Path ([System.IO.Path]::GetTempPath()) 'LiveClash-coverage\'

Push-Location $root
try {
    & dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "dotnet tool restore failed" -ForegroundColor Red; exit 1 }

    if (-not $NoBuild) {
        & dotnet build $proj -c Release -v q --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "build of the headless tests failed" -ForegroundColor Red; exit 1 }
    }

    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    New-Item -ItemType Directory -Force $out | Out-Null

    # The test assembly is what coverlet runs; --include narrows the measurement to the product code.
    & dotnet coverlet (Join-Path $bin 'LiveClash.LogicTests.dll') `
        --target (Join-Path $bin 'LiveClash.LogicTests.exe') `
        --include '[LiveClash.Logic]*' `
        --exclude-by-attribute 'RevitBound' `
        --format cobertura --output $out | Out-Null
    $testExit = $LASTEXITCODE
    $report = Join-Path $out 'coverage.cobertura.xml'
    if (-not (Test-Path $report)) { Write-Host "coverlet wrote no report" -ForegroundColor Red; exit 1 }
}
finally { Pop-Location }

[xml]$x = Get-Content $report
$lineRate   = [math]::Round(100 * [double]$x.coverage.'line-rate', 2)
$branchRate = [math]::Round(100 * [double]$x.coverage.'branch-rate', 2)

# Per file: lines and branches (cobertura gives condition-coverage per line as "50% (1/2)").
$files = @{}
foreach ($c in $x.coverage.packages.package.classes.class) {
    $name = Split-Path $c.filename -Leaf
    if (-not $files.ContainsKey($name)) { $files[$name] = [pscustomobject]@{ File = $name; Lines = 0; Hit = 0; Br = 0; BrHit = 0 } }
    $f = $files[$name]
    foreach ($l in $c.lines.line) {
        $f.Lines++
        if ([int]$l.hits -gt 0) { $f.Hit++ }
        if ($l.branch -eq 'True' -and $l.'condition-coverage' -match '\((\d+)/(\d+)\)') { $f.BrHit += [int]$Matches[1]; $f.Br += [int]$Matches[2] }
    }
}
Write-Host ""
Write-Host "-- coverage of the Revit-free logic (tests\LiveClash.Logic), [RevitBound] excluded --" -ForegroundColor Cyan
$files.Values | Sort-Object File | ForEach-Object {
    $lp = if ($_.Lines) { 100 * $_.Hit / $_.Lines } else { 100 }
    $bp = if ($_.Br) { 100 * $_.BrHit / $_.Br } else { 100 }
    Write-Host ("    {0,-34} lines {1,6:N1}%  ({2}/{3})   branches {4,6:N1}%  ({5}/{6})" -f $_.File, $lp, $_.Hit, $_.Lines, $bp, $_.BrHit, $_.Br)
}
Write-Host ("    TOTAL  lines {0}%   branches {1}%   (thresholds: lines {2}%, branches {3}%)" -f $lineRate, $branchRate, $MinLine, $MinBranch)

$fail = 0
if ($testExit -ne 0) { Write-Host ("COVERAGE RUN: the checks themselves failed ({0})" -f $testExit) -ForegroundColor Red; $fail++ }
if ($lineRate -lt $MinLine) { Write-Host ("COVERAGE FAILED: lines {0}% < {1}%" -f $lineRate, $MinLine) -ForegroundColor Red; $fail++ }
if ($branchRate -lt $MinBranch) { Write-Host ("COVERAGE FAILED: branches {0}% < {1}%" -f $branchRate, $MinBranch) -ForegroundColor Red; $fail++ }
if ($fail -eq 0) { Write-Host ("COVERAGE PASSED (lines {0}%, branches {1}%)" -f $lineRate, $branchRate) -ForegroundColor Green }
exit $fail
