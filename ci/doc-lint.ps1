<#
.SYNOPSIS
    Fails when a doc comment has two <summary> blocks in a row.

.DESCRIPTION
    The doc-integrity leg of ci\gate.ps1 asks the compiler about malformed XML and dangling
    params, and it cannot see this: insert a new member ABOVE an existing one and its summary
    ends up stacked directly under the previous member's, with the same two `///` blocks back
    to back. It compiles clean, warns nothing, and the tooling then attaches the FIRST block to
    the wrong member - the explanation of the old method reads as the new one's, and the old
    method has none. Found in review of ClashEngine.CheckInsulation / AddDeduped, which the
    doc leg had passed.

    Two consecutive summaries are never intentional, so this is a plain text check.

.PARAMETER Root
    Where to scan. Default: the repo root (the parent of ci\).

Exit code is the number of offences. MUST STAY ASCII (Windows PowerShell 5.1).
#>
[CmdletBinding()]
param([string] $Root = '')

# Not a param default: $PSScriptRoot is empty there when the script is started with `powershell -File`.
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

$bad = 0
$files = Get-ChildItem -Path $Root -Filter *.cs -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|tests\\fixtures)\\' }

foreach ($f in $files) {
    $text = [IO.File]::ReadAllText($f.FullName)
    foreach ($m in [regex]::Matches($text, '</summary>[ \t]*\r?\n[ \t]*///[ \t]*<summary>')) {
        $line = ($text.Substring(0, $m.Index) -split "`n").Count
        Write-Host ("DOC LINT: {0}:{1} - a <summary> directly follows another <summary>; one of them is documenting the wrong member" -f $f.FullName.Substring($Root.Length).TrimStart('\'), $line) -ForegroundColor Red
        $bad++
    }
}

# Start-up / shutdown pairing. Everything the add-in subscribes to or registers in OnStartup has to be taken back in
# OnShutdown - the theme-changed handler was added as an anonymous lambda that could not be, and nothing noticed.
# A plain text check on LiveClashApp.cs: every `application...Event += Handler` needs the same `-= Handler`, and every
# `Thing.HookRevit(` / `Thing.Arm(` / `Thing.Register(` needs `Thing.UnhookRevit(` / `Thing.Disarm(` / `Thing.Unregister(`.
$appFile = Join-Path $Root 'LiveClashApp.cs'
if (Test-Path $appFile) {
    $app = [IO.File]::ReadAllText($appFile)
    $s = $app.IndexOf('OnStartup(')
    $d = $app.IndexOf('OnShutdown(')
    if ($s -ge 0 -and $d -gt $s) {
        $startup = $app.Substring($s, $d - $s)
        $shutdown = $app.Substring($d)
        foreach ($m in [regex]::Matches($startup, '(application(?:\.ControlledApplication)?\.\w+)\s*\+=\s*(\w+)\s*;')) {
            $ev = $m.Groups[1].Value; $h = $m.Groups[2].Value
            if ($shutdown -notmatch ([regex]::Escape($ev) + '\s*-=\s*' + [regex]::Escape($h) + '\s*;')) {
                Write-Host ("START-UP/SHUTDOWN: LiveClashApp.cs subscribes {0} += {1} in OnStartup and never does {0} -= {1} in OnShutdown" -f $ev, $h) -ForegroundColor Red
                $bad++
            }
        }
        # An anonymous handler (a lambda or a delegate) cannot be taken back at all - which is exactly how the theme
        # one went unremoved. Name it, or hand it to something that keeps it (ThemeHook does).
        foreach ($m in [regex]::Matches($startup, '(application(?:\.ControlledApplication)?\.\w+)\s*\+=\s*(?:\(|delegate\b|\w+\s*=>)')) {
            Write-Host ("START-UP/SHUTDOWN: LiveClashApp.cs subscribes {0} with an anonymous handler in OnStartup; it cannot be removed in OnShutdown" -f $m.Groups[1].Value) -ForegroundColor Red
            $bad++
        }        $pairs = @{ 'HookRevit' = 'UnhookRevit'; 'Arm' = 'Disarm'; 'Register' = 'Unregister' }
        foreach ($m in [regex]::Matches($startup, '([A-Za-z_][\w\.]*)\.(HookRevit|Arm|Register)\(')) {
            $owner = $m.Groups[1].Value; $undo = $pairs[$m.Groups[2].Value]
            if ($shutdown -notmatch ([regex]::Escape($owner) + '\.' + $undo + '\(')) {
                Write-Host ("START-UP/SHUTDOWN: LiveClashApp.cs calls {0}.{1}( in OnStartup and never {0}.{2}( in OnShutdown" -f $owner, $m.Groups[2].Value, $undo) -ForegroundColor Red
                $bad++
            }
        }
    }
}

if ($bad -eq 0) { Write-Host ("doc lint: {0} files, no stacked <summary> blocks, start-up/shutdown paired" -f @($files).Count) -ForegroundColor Green }
exit $bad
