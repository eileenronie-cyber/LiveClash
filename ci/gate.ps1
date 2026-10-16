<#
.SYNOPSIS
    The CI gate, runnable locally with NO Revit installed. Run it before a push.

.DESCRIPTION
    What a build machine CAN check without Revit in the loop:

      1. Compile gate  - the add-in builds against the Nice3point reference
                         assemblies for every supported Revit runtime
                         (net48 / net8 / net10), in RELEASE. This is what
                         catches a refactor that breaks one target while
                         another still compiles.

      1b. Debug compile - one build of the newest version in DEBUG, into a temp
                         OutputPath so it never fights a running Revit for the
                         live bin\Debug<version> deploy. This is the compile the
                         -Quick subset runs.

      1c. Doc comments - one build with generation ON, so malformed XML comments
                         fail. Normal builds never look at them, so a comment can
                         render incompletely forever without anything saying so.

      2. Logic tests   - the Revit-free serialization and migration checks
                         (tests\LiveClash.LogicTests) actually EXECUTE and must
                         all pass. Exit code is the failure count.

      2b. Coverage     - ci\coverage.ps1: line and branch coverage of the Revit-free
                         logic (tests\LiveClash.Logic) must stay above its ratchet.

      2c. Static analysis - ci\analyze.ps1: the .NET analyzers find nothing in the
                         reliability/security categories and nothing new beyond
                         ci\analyzers-baseline.txt.

      Mutation testing (ci\mutation.ps1) is NOT a gate leg: it takes many minutes, so it
      runs on demand and weekly in CI (.github\workflows\mutation.yml).

    What a build machine CANNOT check: geometry verification, ClashResult.MakeKey
    and the TargetIndex grid. Solid, BooleanOperationsUtils, ElementId and Document
    only exist inside Revit's process.

.PARAMETER Versions
    Which Revit versions to compile-gate. Default: all three supported.

.PARAMETER Quick
    The seconds-long subset the pre-push hook runs (ci\hooks\pre-push): one Debug compile
    plus the headless logic tests. Skips the three Release builds and the doc-comment leg,
    which CI runs on every push. It checks the WORKING TREE, not the commit being pushed.
#>
[CmdletBinding()]
param(
    [int[]] $Versions = @(2026, 2025, 2024),

    # The fast subset, for the pre-push hook (ci\hooks\pre-push): ONE Debug compile plus the
    # headless logic tests. CI still runs everything.
    [switch] $Quick
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # repo root = parent of ci\
$fail = 0

Write-Host "== LiveClash CI gate ==" -ForegroundColor Cyan

# 1. Compile gate, every runtime, from NuGet reference assemblies (no Revit needed).
$releaseVersions = if ($Quick) { @() } else { $Versions }   # -Quick: the Debug compile below covers the sources
foreach ($v in $releaseVersions) {
    Write-Host ""
    Write-Host ("-- compile: Revit {0} --" -f $v) -ForegroundColor Cyan
    & dotnet build (Join-Path $root 'LiveClash.csproj') `
        -p:RevitVersion=$v -p:RevitApiSource=NuGet -c Release -v m
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("COMPILE FAILED for {0}" -f $v) -ForegroundColor Red
        $fail++
    }
}

# 1b. One DEBUG compile of the default version.
# $devVersion is a separate variable on purpose: "$Versions[0]" inside an argument does
# NOT index in PowerShell — it expands the whole array and appends a literal "[0]".
$devVersion = $Versions[0]
Write-Host ""
Write-Host ("-- compile: Revit {0}, Debug --" -f $devVersion) -ForegroundColor Cyan
# Into a THROWAWAY folder, never bin\Debug<version>\ — that path is the live deploy the
# .addin manifests point at, so a plain Debug build here would try to overwrite the DLL a
# running Revit has locked, and the gate would "fail" for having Revit open.
$devOut = Join-Path ([System.IO.Path]::GetTempPath()) 'LiveClash-gate-debug'
& dotnet build (Join-Path $root 'LiveClash.csproj') `
    -p:RevitVersion=$devVersion -p:RevitApiSource=NuGet -c Debug -v m `
    -p:OutputPath=$devOut
if ($LASTEXITCODE -ne 0) {
    Write-Host "DEBUG COMPILE FAILED" -ForegroundColor Red
    $fail++
}

# 1c. XML doc integrity. Doc generation is OFF in every normal build, so the compiler
# never looks at the doc comments and malformed ones cost nothing at compile time and
# everything at read time — they render incomplete, silently.
#
# This exists because of a real one: a cache warning was appended as a SECOND <summary>
# after </returns>, which generation discards. It compiled clean, warned nothing, and the
# guidance reached nobody — on the one method where it was the guard-rail against a bug
# just fixed.
#
# THE LIST IS EVERY XML-DOC DIAGNOSTIC C# HAS, not a chosen subset, because the first
# version of this leg WAS a chosen subset and had a hole in it. It listed 1570-1574 and
# missed CS1587, an orphaned comment — one attached to no member at all. That is the same
# outcome the leg was built for, reached another way: an orphan does not render
# incompletely, it vanishes from the XML entirely. And it is an ordinary accident, not a
# contrived one — insert a member above an existing comment, move a method, split a file,
# and the comment orphans while everything still compiles clean.
#
# So: enumerate, do not curate. A subset has to be re-justified every time the language
# adds a diagnostic; the whole set does not.
#
#   1570 malformed XML          1571 duplicate <param>     1572 <param> for no such parameter
#   1573 param list with a hole 1574 cref resolves to none 1580/1581 bad cref signature
#   1584 cref is not even syntax    1587 comment on no member
#   1589/1590/1592 broken <include> 1710/1711/1712 the <typeparam> equivalents
#   1723 cref points at a type parameter
#
# 1710/1711/1712 cannot fire today — nothing here is generic — and are listed anyway, so
# that the first generic type to arrive is covered without anyone remembering.
#
# -t:Rebuild is LOAD-BEARING. An incremental build skips compilation and re-emits no
# diagnostics, which looks exactly like clean. Do not "optimise" it away.
#
# NoWarn=1591 because undocumented public members are not the target: this tree documents
# what is worth documenting, not everything.
#
# WarningsAsErrors so the leg can actually fail. Without it dotnet prints the warnings,
# exits 0, and the gate reports a pass over the top of them.
if (-not $Quick) {
    Write-Host ""
    Write-Host "-- XML doc integrity (generation on, so malformed comments fail) --" -ForegroundColor Cyan
    $docOut = Join-Path ([System.IO.Path]::GetTempPath()) 'LiveClash-gate-doc'
    & dotnet build (Join-Path $root 'LiveClash.csproj') `
        -p:RevitVersion=$devVersion -p:RevitApiSource=NuGet -c Release -t:Rebuild -v m `
        -p:GenerateDocumentationFile=true -p:NoWarn=1591 `
        -p:WarningsAsErrors=CS1570%3BCS1571%3BCS1572%3BCS1573%3BCS1574%3BCS1580%3BCS1581%3BCS1584%3BCS1587%3BCS1589%3BCS1590%3BCS1592%3BCS1710%3BCS1711%3BCS1712%3BCS1723 `
        -p:OutputPath=$docOut
    if ($LASTEXITCODE -ne 0) {
        Write-Host "DOC COMMENTS FAILED (malformed XML, or a param tag that no longer matches)" -ForegroundColor Red
        $fail++
    }
}

# 1d. Doc lint. The doc leg above asks the compiler about malformed XML and dangling params; it cannot
# see two <summary> blocks stacked on one member (compiles clean, warns nothing, attaches the first to
# the wrong member). Cheap, headless, and also part of -Quick.
Write-Host ""
Write-Host "-- doc lint (no stacked <summary> blocks) --" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'ci\doc-lint.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host ("DOC LINT FAILED: {0} stacked summary block(s)" -f $LASTEXITCODE) -ForegroundColor Red
    $fail += $LASTEXITCODE
}

# 1e. The theme dictionaries on the .NET Framework's WPF (Revit 2024). Windows PowerShell IS that WPF, so this
# catches what no check on 2025/2026 can: an entry the older WPF rejects costs the panel on 2024. Also compares
# the light and dark palettes' keys. A couple of seconds, no Revit, part of -Quick too.
Write-Host ""
& powershell.exe -NoProfile -Sta -ExecutionPolicy Bypass -File (Join-Path $root 'ci\xaml-parse.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host ("THEME XAML FAILED: {0} problem(s)" -f $LASTEXITCODE) -ForegroundColor Red
    $fail += $LASTEXITCODE
}
# 2. Headless logic tests. Exit code == number of failed checks.
Write-Host ""
Write-Host "-- logic tests (headless) --" -ForegroundColor Cyan
& dotnet run --project (Join-Path $root 'tests\LiveClash.LogicTests') -c Release
$logic = $LASTEXITCODE
if ($logic -ne 0) {
    Write-Host ("LOGIC TESTS FAILED: {0} check(s)" -f $logic) -ForegroundColor Red
    $fail += $logic
}

# 2b. Coverage of the Revit-free logic, with a ratchet threshold (ci\coverage.ps1), and
# 2c. static analysis against its baseline (ci\analyze.ps1). Both headless; both skipped by -Quick
# to keep the pre-push hook to seconds - CI runs them on every push.
if (-not $Quick) {
    Write-Host ""
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'ci\coverage.ps1') -NoBuild
    if ($LASTEXITCODE -ne 0) {
        Write-Host "COVERAGE GATE FAILED" -ForegroundColor Red
        $fail++
    }
    Write-Host ""
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'ci\analyze.ps1')
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STATIC ANALYSIS GATE FAILED" -ForegroundColor Red
        $fail++
    }
}

Write-Host ""
if ($fail -eq 0) {
    if ($Quick) {
        $what = "QUICK: 1 Debug compile + logic tests - the full gate and CI run the rest"
    } else {
        $what = "compile x$($Versions.Count) Release + 1 Debug + doc comments + logic tests + coverage + static analysis"
    }
    Write-Host "GATE PASSED ($what)" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("GATE FAILED ({0} problem(s))" -f $fail) -ForegroundColor Red
    exit 1
}
