<#
.SYNOPSIS
    Fails when a theme dictionary cannot be parsed by the .NET Framework's WPF, or when the light and dark
    palettes stop having the same keys.

.DESCRIPTION
    Revit 2024 runs the add-in on .NET Framework 4.8; 2025 and 2026 run it on .NET 8 and 10. WPF is where they
    differ, and a theme dictionary is parsed the first time a window is built - so an entry the older WPF rejects
    does not fail a compile and does not fail any check on the newer runtimes. It costs the add-in its panel on
    2024, with a "Failed to start" dialog. That happened: two of the system-colour overrides
    (InactiveSelectionHighlightBrushKey and its Text twin) are distinct keys on .NET 8 and the SAME key as
    ControlBrushKey on .NET Framework, so the dictionary threw "Item has already been added". Every test on
    Revit 2026 was green.

    Windows PowerShell 5.1 IS the .NET Framework's WPF, so this parses UI\Theme.xaml and UI\ThemeDark.xaml with it
    and realises every entry (a dictionary's content is deferred until asked for), then compares the SolidColorBrush
    keys of the two: a colour added to one palette and not the other would leave part of a dark window light.

    The windows and the panel are parsed the same way. Their XAML is loaded with the parts that need the add-in
    itself taken out (x:Class, the event handlers named in the code-behind, the `ui:` attached properties), the theme
    pointed at the file on disk, and then measured, so the templates and resources are created too. That catches
    the same class of fault where it is most likely to be introduced next: a markup extension, an attached property
    or a resource reference that the older WPF handles differently, in a window that is built at start-up.
    It needs a single-threaded apartment to build a window, so the gate starts it with -Sta.

    Runs in the quick gate too: it takes a few seconds and needs no Revit.

.PARAMETER Root
    The repo root. Default: the parent of ci\.

Exit code is the number of problems. MUST STAY ASCII (Windows PowerShell 5.1 reads this file as ANSI).
#>
[CmdletBinding()]
param([string] $Root = '')

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$bad = 0

function Load-Dictionary([string] $path) {
    $xml = [IO.File]::ReadAllText($path)
    try {
        $d = [Windows.Markup.XamlReader]::Parse($xml)
    } catch {
        $e = $_.Exception
        while ($e.InnerException) { $e = $e.InnerException }
        Write-Host ("  {0}: does not parse on the .NET Framework: {1}" -f (Split-Path -Leaf $path), $e.Message) -ForegroundColor Red
        return $null
    }
    $failed = 0
    foreach ($k in @($d.Keys)) {
        try { $null = $d[$k] }
        catch {
            $e = $_.Exception
            while ($e.InnerException) { $e = $e.InnerException }
            Write-Host ("  {0}: entry '{1}' cannot be created: {2}" -f (Split-Path -Leaf $path), $k, $e.Message) -ForegroundColor Red
            $failed++
        }
    }
    if ($failed -gt 0) { $script:bad += $failed }
    return $d
}

function Brush-Keys($d) {
    $out = @()
    foreach ($k in @($d.Keys)) {
        try { if ($d[$k] -is [Windows.Media.SolidColorBrush]) { $out += $k.ToString() } } catch { }
    }
    return $out
}

Write-Host "-- theme dictionaries on the .NET Framework's WPF --" -ForegroundColor Cyan
$light = Load-Dictionary (Join-Path $Root 'UI\Theme.xaml')
if (-not $light) { $bad++ }

$darkPath = Join-Path $Root 'UI\ThemeDark.xaml'
if (Test-Path $darkPath) {
    $dark = Load-Dictionary $darkPath
    if (-not $dark) { $bad++ }
    if ($light -and $dark) {
        $lk = Brush-Keys $light; $dk = Brush-Keys $dark
        $onlyLight = @($lk | Where-Object { $dk -notcontains $_ })
        $onlyDark  = @($dk | Where-Object { $lk -notcontains $_ })
        if ($onlyLight.Count -gt 0) { Write-Host ("  in Theme.xaml but not ThemeDark.xaml: " + ($onlyLight -join ', ')) -ForegroundColor Red; $bad += $onlyLight.Count }
        if ($onlyDark.Count -gt 0)  { Write-Host ("  in ThemeDark.xaml but not Theme.xaml: " + ($onlyDark -join ', ')) -ForegroundColor Red; $bad += $onlyDark.Count }
        if ($onlyLight.Count -eq 0 -and $onlyDark.Count -eq 0) { Write-Host ("  light and dark palettes define the same {0} brushes" -f $lk.Count) }
    }
}

# ---------------------------------------------------------------- the windows and the panel

Write-Host "-- windows and panel on the .NET Framework's WPF --" -ForegroundColor Cyan
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Write-Host "  cannot build a window here: this needs a single-threaded apartment (run it with powershell -Sta)" -ForegroundColor Red
    $bad++
}
else {
    $themeUri = 'file:///' + ((Join-Path $Root 'UI\Theme.xaml') -replace '\\', '/')
    $checked = 0
    foreach ($f in Get-ChildItem -Path (Join-Path $Root 'UI') -Filter *.xaml | Where-Object { $_.Name -notlike 'Theme*' }) {
        $text = [IO.File]::ReadAllText($f.FullName)

        # what only the add-in itself can resolve
        $text = [regex]::Replace($text, '\sx:Class="[^"]*"', '')
        $text = [regex]::Replace($text, '\sxmlns:ui="[^"]*"', '')
        $text = [regex]::Replace($text, '\sui:[\w\.]+="[^"]*"', '')
        $text = $text.Replace('/LiveClash;component/UI/Theme.xaml', $themeUri)
        $code = [IO.Path]::ChangeExtension($f.FullName, '.xaml.cs')
        if (Test-Path $code) {
            $handlers = [regex]::Matches([IO.File]::ReadAllText($code), '\bvoid\s+(\w+)\s*\(\s*(?:object|System\.Object)\s+\w+\s*,') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
            foreach ($h in $handlers) { $text = [regex]::Replace($text, '\s[\w:\.]+="' + [regex]::Escape($h) + '"', '') }
        }

        try {
            $element = [Windows.Markup.XamlReader]::Parse($text)
            # Create the templates and resolve the resources too: a window's content is only built when it is measured.
            $content = $null
            if ($element -is [Windows.Window]) { $content = $element.Content; $element.Content = $null } else { $content = $element }
            $hostBox = New-Object Windows.Controls.Border
            $hostBox.Resources.MergedDictionaries.Add($element.Resources)
            if ($content -is [Windows.UIElement]) { $hostBox.Child = $content }
            $size = New-Object Windows.Size 900, 700
            $hostBox.Measure($size)
            $hostBox.Arrange((New-Object Windows.Rect 0, 0, 900, 700))
            $hostBox.UpdateLayout()
            $checked++
        }
        catch {
            $e = $_.Exception
            while ($e.InnerException) { $e = $e.InnerException }
            Write-Host ("  {0}: does not build on the .NET Framework: {1}" -f $f.Name, $e.Message) -ForegroundColor Red
            $bad++
        }
    }
    if ($bad -eq 0 -or $checked -gt 0) { Write-Host ("  {0} windows and panel parse, and lay out" -f $checked) }
}
exit $bad
