<#
.SYNOPSIS
    Mutation testing of the Revit-free logic with Stryker.NET, with a minimum score. No Revit needed.
    Not part of ci\gate.ps1 (it takes tens of minutes); run it on demand and weekly in CI
    (.github\workflows\mutation.yml).

.DESCRIPTION
    Stryker plants small defects ("mutants") in tests\LiveClash.Logic - flipped comparisons, removed
    conditions, changed constants, emptied strings - and reruns the headless checks against each. A
    mutant the checks still pass is one they cannot see. The mutation score is the share caught.

    Scope and limits:
      - Mutates the Revit-free Core files only. Code bound to the Revit API cannot run outside Revit,
        so it is mutation-tested by hand instead: a planted defect, a predicted failing set, a run in
        Revit, then a revert (the "mutation rounds" recorded in the compendium).
      - [RevitBound] members cannot run here, so they are not mutated: ParameterReader.cs and IdUtil.cs
        are left out in stryker-config.json, and the thin wrappers in other files are fenced with
        "// Stryker disable all : [RevitBound] ..." / "// Stryker restore all" comments.
      - Some survivors are EQUIVALENT mutants: a change with no observable effect (an initial value
        always overwritten, a tie that cannot occur). They cap the score below 100% by nature.

    Thresholds (stryker-config.json in tests\LiveClash.LogicTests): high 85, low 70. This script
    fails below -Break.

    coverage-analysis is "off" ON PURPOSE. The headless checks reach Stryker as ONE MSTest test
    (SharedChecksTest), and in Stryker's per-test coverage mode the active mutant never reached that
    test: a first full run reported 8% with mutants "surviving" that the checks plainly catch (a blank
    search query counted as active). Measured on one file: perTest 0% killed, off 83%. "off" reruns
    the whole suite per mutant, which is slower but honest.

.PARAMETER Since
    Incremental run: only files changed against this git ref are mutated (Stryker --since). Use it
    while working; the score then describes only the changed files.

.PARAMETER Break
    Fail when the mutation score (killed + timeout over all valid mutants) is below this percent.

MUST STAY ASCII (Windows PowerShell 5.1 reads this file as ANSI).
#>
[CmdletBinding()]
param(
    [double] $Break = 90,   # a ratchet: 93.3% measured 2026-10-05; raise it as survivors are killed, never lower it
    # Only mutate files changed against this git ref (e.g. master). A full run is ~15 minutes; a typical
    # change touches one or two Core files and takes a minute or two. The weekly CI run stays full.
    [string] $Since = '',
    [string] $Out = (Join-Path ([System.IO.Path]::GetTempPath()) 'LiveClash-mutation')   # where Stryker writes its reports
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'tests\LiveClash.LogicTests'
$out  = $Out
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Push-Location $root
try { & dotnet tool restore | Out-Null } finally { Pop-Location }

Push-Location $proj
try {
    $sArgs = @('--output', $out)
    if ($Since) { $sArgs += ('--since:' + $Since) }
    & dotnet stryker @sArgs
    $exit = $LASTEXITCODE
}
finally { Pop-Location }

$json = Get-ChildItem $out -Recurse -Filter 'mutation-report.json' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $json) { Write-Host "Stryker wrote no report (exit $exit)" -ForegroundColor Red; exit 1 }
$r = Get-Content $json.FullName -Raw | ConvertFrom-Json

$tot = @{}
foreach ($f in $r.files.PSObject.Properties) {
    foreach ($m in $f.Value.mutants) { $tot[$m.status] = 1 + [int]$tot[$m.status] }
}
$killed = [int]$tot['Killed'] + [int]$tot['Timeout']
$survived = [int]$tot['Survived']
$nocov = [int]$tot['NoCoverage']
$score = if (($killed + $survived + $nocov) -gt 0) { 100.0 * $killed / ($killed + $survived + $nocov) } else { 0 }
$covered = if (($killed + $survived) -gt 0) { 100.0 * $killed / ($killed + $survived) } else { 0 }

Write-Host ""
Write-Host ("-- mutation score: {0:N1}% of {1} valid mutants ({2} killed or timed out, {3} survived, {4} not covered); covered code: {5:N1}%" -f `
    $score, ($killed + $survived + $nocov), $killed, $survived, $nocov, $covered) -ForegroundColor Cyan
$html = Get-ChildItem $out -Recurse -Filter 'mutation-report.html' | Select-Object -First 1
if ($html) { Write-Host ("   report: " + $html.FullName) }

if ($score -lt $Break) { Write-Host ("MUTATION SCORE BELOW {0}%" -f $Break) -ForegroundColor Red; exit 1 }
Write-Host ("MUTATION PASSED ({0:N1}% >= {1}%)" -f $score, $Break) -ForegroundColor Green
exit 0
