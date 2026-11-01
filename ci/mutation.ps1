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
    Incremental run: only the files changed against this git ref (a branch, tag or commit; the working
    tree counts, so uncommitted edits too) are mutated. Use it while working; the score then describes
    only the changed files.

    Stryker's own --since looks only at files UNDER tests\LiveClash.Logic. The product files are LINKED
    into that project from Core\, so a change to Core\X.cs was never seen: a run reported "unable to
    calculate a mutation score" (0 mutants) after minutes of building, and this script then printed a
    false "MUTATION SCORE BELOW 90%". So this script does the diff itself: it maps the changed files to
    the Compile Include entries of LiveClash.Logic.csproj (and the .cs files inside that project) and
    hands each to Stryker with --mutate.

    A FULL run is done instead, and said so, when something changed that can move any mutant's fate:
    a checks file (a .cs linked into or inside tests\LiveClash.LogicTests, e.g. Testing\*Checks.cs),
    either test project file, LiveClash.Logic.csproj, stryker-config.json or dotnet-tools.json. If
    nothing maps to a file that is mutated, it says "nothing to mutate" and exits 0.

.PARAMETER ListOnly
    With -Since: print the mode (incremental, full, nothing to mutate) and the files, then stop. Runs no
    Stryker and builds nothing; it is how the mapping is checked.

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
    [switch] $ListOnly,
    [string] $Out = (Join-Path ([System.IO.Path]::GetTempPath()) 'LiveClash-mutation')   # where Stryker writes its reports
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'tests\LiveClash.LogicTests'
$out  = $Out
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# ---- -Since: which files does a change reach? (see .PARAMETER Since) ----
$mutateArgs = @()
$mode = 'full'
$incrementalFiles = @()
$sinceNotes = @()
if ($Since) {
    Push-Location $root
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # git prints line-ending warnings to stderr; under Stop that is a terminating error
    try {
        $sha = (& git rev-parse --verify --quiet ($Since + '^{commit}') 2>$null)
        if ($LASTEXITCODE -ne 0 -or -not $sha) { Write-Host "-Since: no branch, tag or commit named '$Since'" -ForegroundColor Red; exit 2 }
        $changed = @(& git diff --name-only $sha 2>$null | ForEach-Object { $_.Trim().Replace('\', '/') } | Where-Object { $_ })
    }
    finally { $ErrorActionPreference = $eap; Pop-Location }

    # Compile Include entries of a project file -> repo-relative path, and the path inside the project (Link, or the file name).
    function Get-Compiled([string] $csproj) {
        $dir = Split-Path -Parent $csproj
        $out = @()
        foreach ($m in [regex]::Matches((Get-Content $csproj -Raw), '<Compile Include="([^"]+)"(?:\s+Link="([^"]+)")?')) {
            $full = [System.IO.Path]::GetFullPath((Join-Path $dir $m.Groups[1].Value))
            $rel = $full.Substring($root.Length).TrimStart('\').Replace('\', '/')
            $inside = if ($m.Groups[2].Success) { $m.Groups[2].Value.Replace('\', '/') } else { $m.Groups[1].Value.Replace('\', '/') }
            $out += [pscustomobject] @{ Repo = $rel; Inside = $inside }
        }
        return $out
    }
    $logicProj = Join-Path $root 'tests\LiveClash.Logic\LiveClash.Logic.csproj'
    $testsProj = Join-Path $root 'tests\LiveClash.LogicTests\LiveClash.LogicTests.csproj'
    $logicFiles = @(Get-Compiled $logicProj)
    $testFiles = @(Get-Compiled $testsProj)
    $leftOut = @('ParameterReader.cs', 'IdUtil.cs', 'RevitBoundAttribute.cs')   # the same exclusions as stryker-config.json

    $fullBecause = @()
    foreach ($c in $changed) {
        if ($c -match '^tests/LiveClash\.Logic/.*\.csproj$' -or $c -match '^tests/LiveClash\.LogicTests/.*\.csproj$' -or
            $c -eq 'tests/LiveClash.LogicTests/stryker-config.json' -or $c -eq 'dotnet-tools.json') { $fullBecause += ($c + ' (project or tool config)'); continue }
        if ($c -like 'tests/LiveClash.LogicTests/*.cs' -or ($testFiles | Where-Object { $_.Repo -eq $c })) { $fullBecause += ($c + ' (a checks file)'); continue }
    }
    foreach ($c in $changed) {
        if (-not (Test-Path (Join-Path $root $c))) { continue }   # deleted
        $hit = $logicFiles | Where-Object { $_.Repo -eq $c } | Select-Object -First 1
        $inside = $null
        if ($hit) { $inside = $hit.Inside }
        elseif ($c -like 'tests/LiveClash.Logic/*.cs' -and $c -notmatch '/(obj|bin)/') { $inside = $c.Substring('tests/LiveClash.Logic/'.Length) }
        if (-not $inside) { continue }
        $name = Split-Path -Leaf $inside
        if ($leftOut -contains $name) { $sinceNotes += ($c + ' changed but is left out of mutation by stryker-config.json'); continue }
        $incrementalFiles += $c
        $mutateArgs += @('--mutate', ('**/' + $inside))
    }

    if ($fullBecause.Count -gt 0) {
        $mode = 'full'
        $mutateArgs = @()
        Write-Host ("-Since $Since ($($sha.Substring(0,10))): FULL RUN, not incremental, because a change can move any mutant:") -ForegroundColor Yellow
        foreach ($f in $fullBecause) { Write-Host ("   " + $f) -ForegroundColor Yellow }
    }
    elseif ($incrementalFiles.Count -eq 0) {
        $mode = 'none'
        Write-Host ("-Since $Since ($($sha.Substring(0,10))): nothing to mutate - no changed file is one that Stryker mutates ($($changed.Count) changed file(s) in all).") -ForegroundColor Green
        foreach ($n in $sinceNotes) { Write-Host ("   " + $n) }
    }
    else {
        $mode = 'incremental'
        Write-Host ("-Since $Since ($($sha.Substring(0,10))): INCREMENTAL, $($incrementalFiles.Count) file(s) mutated:") -ForegroundColor Cyan
        foreach ($f in $incrementalFiles) { Write-Host ("   " + $f) }
        foreach ($n in $sinceNotes) { Write-Host ("   " + $n) }
    }
    if ($ListOnly) { Write-Host ("mode: " + $mode); exit 0 }
    if ($mode -eq 'none') { exit 0 }
}
elseif ($ListOnly) { Write-Host "-ListOnly needs -Since"; exit 2 }
$clock = [System.Diagnostics.Stopwatch]::StartNew()

Push-Location $root
try { & dotnet tool restore | Out-Null } finally { Pop-Location }

Push-Location $proj
try {
    $sArgs = @('--output', $out)
    $sArgs += $mutateArgs   # empty for a full run
    & dotnet stryker @sArgs
    $exit = $LASTEXITCODE
}
finally { Pop-Location }

$json = Get-ChildItem $out -Recurse -Filter 'mutation-report.json' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $json) { Write-Host "Stryker wrote no report (exit $exit)" -ForegroundColor Red; exit 1 }
$r = Get-Content $json.FullName -Raw | ConvertFrom-Json

Write-Host ("   mutation phase and build took {0:N1} minute(s) in all" -f $clock.Elapsed.TotalMinutes)
$tot = @{}
$perFile = @()
foreach ($f in $r.files.PSObject.Properties) {
    $c = @{}
    foreach ($m in $f.Value.mutants) { $tot[$m.status] = 1 + [int]$tot[$m.status]; $c[$m.status] = 1 + [int]$c[$m.status] }
    $perFile += [pscustomobject] @{ File = ($f.Name.Replace('\', '/')); Mutants = @($f.Value.mutants).Count; Killed = [int]$c['Killed']; Timeout = [int]$c['Timeout']; Survived = [int]$c['Survived']; NoCoverage = [int]$c['NoCoverage']; Ignored = [int]$c['Ignored'] + [int]$c['CompileError'] }
}
Write-Host ""
Write-Host "-- per file (Mutants includes ignored and compile-error ones; the score uses Killed+Timeout / Killed+Timeout+Survived+NoCoverage)"
$perFile | Sort-Object File | Format-Table File, Mutants, Killed, Timeout, Survived, NoCoverage, Ignored -AutoSize | Out-String -Width 200 | Write-Host
if ($mode -eq 'incremental') {
    foreach ($want in $incrementalFiles) {
        $leaf = Split-Path -Leaf $want
        if (-not ($perFile | Where-Object { $_.File -like ('*/' + $leaf) -or $_.File -eq $leaf })) {
            Write-Host ("Stryker did not report $want, which this script asked it to mutate (does the --mutate pattern match?)") -ForegroundColor Red
            exit 1
        }
    }
}
$killed = [int]$tot['Killed'] + [int]$tot['Timeout']
$survived = [int]$tot['Survived']
$nocov = [int]$tot['NoCoverage']
if (($killed + $survived + $nocov) -eq 0 -and $mode -eq 'incremental') {
    Write-Host "The changed file(s) hold no mutants that can be scored (declarations only?): nothing to score, not a failure." -ForegroundColor Green
    exit 0
}
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
