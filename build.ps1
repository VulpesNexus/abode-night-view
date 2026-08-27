# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Vixen420
#
# Abode Night View is free software: you may redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free
# Software Foundation, either version 3 of the License, or (at your option) any
# later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
# <https://www.gnu.org/licenses/>, for the full text.

# =============================================================================
#  Abode Night View - build
# -----------------------------------------------------------------------------
#  No toolchain to install: this uses the C# compiler that ships with the
#  .NET Framework, an OS component on every Windows 10/11 machine.
#
#  Use this rather than build.cmd if the project lives under a path with
#  non-ASCII characters - cmd.exe resolves paths in the OEM codepage and
#  cannot cd into such a directory, while PowerShell handles it correctly.
#
#      .\build.ps1              everything: the app plus the prototypes
#      .\build.ps1 -Release     only AbodeNightView.exe, into .\dist
#      .\build.ps1 -Clean
# =============================================================================

param([switch]$Clean, [switch]$Release)

$ErrorActionPreference = 'Stop'
$root = Split-Path -LiteralPath $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $csc)) {
    Write-Host "In-box C# compiler not found at:`n  $csc" -ForegroundColor Red
    Write-Host "Install .NET Framework 4.x, or build with Visual Studio instead." -ForegroundColor Red
    exit 1
}

if ($Clean) {
    Get-ChildItem -LiteralPath $root -Filter '*.exe' | Remove-Item -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath (Join-Path $root 'dist')) {
        Remove-Item -LiteralPath (Join-Path $root 'dist') -Recurse -Force
    }
    Write-Host "cleaned"
    if (-not $Release) { exit 0 }
}

# The icon is generated from assets\source-icon.png so a clean checkout builds
# without anything outside the repository.
$ico = Join-Path $root 'assets\AbodeNightView.ico'
if (-not (Test-Path -LiteralPath $ico)) {
    Write-Host "Generating $ico" -ForegroundColor DarkGray
    & (Join-Path $root 'tools\make-icon.ps1') -Source (Join-Path $root 'assets\source-icon.png') -Out $ico
}

# The tray icon and the notification balloon both carry the state: the plain
# artwork when the dimming is off, the "cool" variant when it is on. The shell
# will only take either as an HICON, so both variants are built as .ico files and
# embedded as managed resources, which src\Balloon.cs loads by these exact names.
#
# The sizes are the two ends of one ladder. A balloon asks at NIIF_LARGE_ICON,
# nominally SM_CXICON: 32 px at 100% scaling and 96 px at 300%. The notification
# area asks at SM_CXSMICON: 16 px at 100%, 20 at 125%, 24 at 150%, 32 at 200%.
# 128 and 256 are still left out - nothing here is ever drawn that large, and a
# 256 px entry alone would be a third of the whole binary.
$stateSizes = 16, 20, 24, 32, 48, 64, 96
$stateArt = @(
    @{ Src = 'assets\source-icon.png';      Out = 'assets\state-off.ico'; Id = 'state-off.ico' },
    @{ Src = 'assets\source-icon-cool.png'; Out = 'assets\state-on.ico';  Id = 'state-on.ico'  }
)
$resources = @()
foreach ($b in $stateArt) {
    $bico = Join-Path $root $b.Out
    if (-not (Test-Path -LiteralPath $bico)) {
        Write-Host "Generating $bico" -ForegroundColor DarkGray
        & (Join-Path $root 'tools\make-icon.ps1') `
            -Source (Join-Path $root $b.Src) -Out $bico -Sizes $stateSizes
    }
    $resources += "-resource:$bico,$($b.Id)"
}

# -platform:x64 is NOT optional for Prototype C: the Magnification API is
# documented as unsupported under WOW64. AbodeNightView.exe is built x64 too -
# see README "Architecture" for why there is no x86 build.
#
# Prototype C lives in experiments\, not src\. It is the measurement rig that
# settled whether a Greyscale mode could be built without capturing the screen.
# The answer was no, so none of it ships; it is kept because an answer is only
# worth as much as the thing that produced it.
# -codepage:65001 is NOT optional. These sources have no byte-order mark, and
# without it csc reads them in the machine's ANSI codepage: every en dash in a
# user-visible string comes out as mojibake on a build machine whose codepage is
# not UTF-8, which is most of them. The flag makes the build independent of the
# machine's locale rather than accidentally correct on this one.
$flags = @('-nologo','-platform:x64','-optimize+','-nowarn:649','-codepage:65001')
$refs  = @('-r:System.dll','-r:System.Drawing.dll','-r:System.Windows.Forms.dll')

function Build($name, $target, $sources, $useRefs, $unsafe, $outDir, $extra) {
    if (-not $outDir) { $outDir = $root }
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    $out = Join-Path $outDir $name
    # A running exe holds a write lock, but Windows always allows a rename. This
    # lets a rebuild succeed while an old overlay is still in the tray -- handy,
    # because a process started from an elevated shell cannot be killed from here.
    if (Test-Path -LiteralPath $out) {
        try { Remove-Item -LiteralPath $out -Force -ErrorAction Stop }
        catch {
            $stale = "$out.old"
            if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue }
            Rename-Item -LiteralPath $out -NewName (Split-Path $stale -Leaf) -ErrorAction Stop
            Write-Host "  (previous $name is still running; renamed to $(Split-Path $stale -Leaf))" -ForegroundColor Yellow
        }
    }
    $args = $flags + @("-target:$target", "-out:$out")
    if ($unsafe)  { $args += '-unsafe+' }
    if ($useRefs) { $args += $refs }
    if ($extra)   { $args += $extra }
    $args += ($sources | ForEach-Object { Join-Path $root $_ })
    Write-Host "  $name" -NoNewline
    & $csc $args
    if ($LASTEXITCODE -ne 0) { Write-Host "  FAILED" -ForegroundColor Red; exit 1 }
    Write-Host ("  ->  {0:N0} bytes" -f (Get-Item -LiteralPath $out).Length) -ForegroundColor DarkGray
}

# Shared by everything: the Win32 layer and the Adobe target adapters.
$common = @('src\Common.cs', 'src\Targets.cs')

# The shipping application. One file: overlay engine, target adapters, tray,
# configurable hotkeys, diagnostics, the Adobe probe, and the verifier reachable
# as --verify / --watch.
$appSources = @('src\AssemblyInfo.cs') + $common + @(
    'src\Settings.cs',
    'src\ProductPrefs.cs',
    'src\UiState.cs',
    'src\Schedule.cs',
    'src\Balloon.cs',
    'src\Hotkeys.cs',
    'src\Diagnostics.cs',
    'src\Verify_Overlay.cs',
    'src\AbodeNightView.cs'
)

if ($Release) {
    Write-Host "Building the Abode Night View release..." -ForegroundColor Cyan
    Build 'AbodeNightView.exe' 'winexe' $appSources $true $true (Join-Path $root 'dist') (@("-win32icon:$ico") + $resources)
    exit 0
}

Write-Host "Building Abode Night View..." -ForegroundColor Cyan
Build 'AbodeNightView.exe' 'winexe' $appSources $true $true $root (@("-win32icon:$ico") + $resources)
Build 'ProtoA.exe'     'exe' @('src\ProtoA_HwndInspector.cs')                    $false $false $root $null
Build 'ProtoC.exe'     'exe' ($common + @('experiments\ProtoC_MagnifierOverlay.cs')) $true $false $root $null
Build 'Verify.exe'     'exe' ($common + @('src\Verify_Overlay.cs','src\VerifyMain.cs')) $true $true $root $null
Build 'Transfer.exe'   'exe' @('src\Transfer_Curve.cs')                          $true  $true  $root $null
Build 'TestTarget.exe' 'exe' ($common + @('src\TestTarget.cs'))                  $true  $false $root $null
Build 'Audit.exe'      'exe' ($common + @('src\Settings.cs','src\ProductPrefs.cs','src\UiState.cs','src\Schedule.cs','src\Balloon.cs','src\Hotkeys.cs','src\Harness.cs','src\SelfTest.cs','src\Audit.cs')) $true $false $root $resources

Write-Host @"

Ready. With an Adobe application open and a document visible:

  .\AbodeNightView.exe                  the application (tray icon; no shortcut is bound)
  .\AbodeNightView.exe --help
  .\AbodeNightView.exe --probe          what each Adobe product looks like right now
  .\AbodeNightView.exe --diagnostics    everything a bug report needs
  .\AbodeNightView.exe --verify         structural + photometric self-test
  .\AbodeNightView.exe --watch=25       time every overlay transition

  .\ProtoA.exe dump --proc=Illustrator  full window hierarchy of any process
  .\ProtoC.exe --diag=700               greyscale research rig (experiments\)
  .\Audit.exe                           the mechanical harness
  .\Audit.exe --selftest                hotkeys, settings, migration, schedule

  .\build.ps1 -Release                  produce dist\AbodeNightView.exe
"@ -ForegroundColor Green
