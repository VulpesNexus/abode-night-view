# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Vixen420
#
# Abode Night View is free software: you may redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free
# Software Foundation, either version 3 of the License, or (at your option) any
# later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
# <https://www.gnu.org/licenses/>, for the full text.

# =============================================================================
#  Abode Night View - produce the distributable binary
# -----------------------------------------------------------------------------
#  One command, from a clean checkout, no IDE:
#
#      .\build-release.ps1
#
#  Produces  dist\AbodeNightView.exe  and nothing else. That single file is the
#  whole distribution: the overlay engine, the Adobe target adapters, the tray
#  application, configurable hotkeys, the diagnostics report, the Adobe probe
#  and the verifier are all inside it. So is the artwork: the application icon
#  as a Win32 resource, and the two notification icons as managed resources.
#
#  Toolchain
#      csc.exe from %WINDIR%\Microsoft.NET\Framework64\v4.0.30319, which is a
#      Windows OS component -- present on every Windows 10 and 11 install. There
#      is nothing to download and no SDK to match versions with.
#
#  Determinism
#      The output is not bit-for-bit reproducible: the C# compiler stamps an
#      MVID and a build timestamp into every assembly. Everything else is fixed
#      by this script -- source list, compiler, flags, architecture, icon,
#      version metadata -- so two builds of the same source differ only in those
#      stamps. The SHA-256 printed at the end is what identifies the binary that
#      was actually shipped; record it with the release.
# =============================================================================

param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -LiteralPath $MyInvocation.MyCommand.Path

Write-Host ""
Write-Host "Abode Night View release build" -ForegroundColor Cyan
Write-Host "==============================" -ForegroundColor Cyan

# 1. Clean, so nothing stale can survive into the artifact.
$dist = Join-Path $root 'dist'
if (Test-Path -LiteralPath $dist) {
    try { Remove-Item -LiteralPath $dist -Recurse -Force -ErrorAction Stop }
    catch {
        # A previously built copy may still be running from dist\, which holds a
        # write lock on it -- and on any earlier copy that was already renamed
        # aside for the same reason. Windows always permits renaming a running
        # exe, so move whatever cannot be deleted out of the way under a name
        # nothing will look for, and let a later build sweep it up once the
        # process holding it has exited.
        #
        # This used to assume the one locked file was called AbodeNightView.exe,
        # and threw on the second release build in a row with a copy running.
        foreach ($item in Get-ChildItem -LiteralPath $dist -File) {
            try { Remove-Item -LiteralPath $item.FullName -Force -ErrorAction Stop }
            catch {
                # Strip any prefix a previous run added, so repeated release
                # builds against one running copy do not grow the name each time.
                $bare  = $item.Name -replace '^locked-[0-9a-f]{8}-', ''
                $aside = 'locked-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '-' + $bare
                Rename-Item -LiteralPath $item.FullName -NewName $aside
                Write-Host "  ($($item.Name) is locked by a running copy; renamed it aside)" -ForegroundColor Yellow
            }
        }
    }
}

# 2. Regenerate the icon from the source artwork in the repository, so a clean
#    checkout produces a byte-identical resource without anything external.
$ico = Join-Path $root 'assets\AbodeNightView.ico'
if (Test-Path -LiteralPath $ico) { Remove-Item -LiteralPath $ico -Force }
& (Join-Path $root 'tools\make-icon.ps1') `
    -Source (Join-Path $root 'assets\source-icon.png') -Out $ico

# Same for the two balloon icons, which are embedded as managed resources rather
# than as the Win32 icon group. build.ps1 generates them if they are missing;
# here they are regenerated unconditionally, so the shipped binary can never
# carry artwork left over from an earlier edit of the source PNG.
foreach ($b in @(
    @{ Src = 'assets\source-icon.png';      Out = 'assets\balloon-off.ico' },
    @{ Src = 'assets\source-icon-cool.png'; Out = 'assets\balloon-on.ico'  })) {
    $bico = Join-Path $root $b.Out
    if (Test-Path -LiteralPath $bico) { Remove-Item -LiteralPath $bico -Force }
    & (Join-Path $root 'tools\make-icon.ps1') `
        -Source (Join-Path $root $b.Src) -Out $bico -Sizes 32,48,64,96
}

# 3. Compile.
& (Join-Path $root 'build.ps1') -Release
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

$exe = Join-Path $dist 'AbodeNightView.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "dist\AbodeNightView.exe was not produced" }

# 4. Prove the artifact runs and reports itself correctly. A release that has
#    never been executed once is not a release.
if (-not $SkipTests) {
    Write-Host ""
    Write-Host "Smoke test" -ForegroundColor Cyan
    $tmp = Join-Path $env:TEMP ("abodenv-smoke-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        Copy-Item -LiteralPath $exe -Destination $tmp
        $out = Join-Path $tmp 'out.txt'
        $app = Join-Path $tmp 'AbodeNightView.exe'

        Start-Process -FilePath $app -ArgumentList '--version' -NoNewWindow -Wait -RedirectStandardOutput $out
        $ver = (Get-Content -LiteralPath $out -Raw).Trim()
        if (-not $ver) { throw "--version produced no output" }
        Write-Host "  $ver" -ForegroundColor DarkGray

        Start-Process -FilePath $app -ArgumentList '--diagnostics' -NoNewWindow -Wait -RedirectStandardOutput $out
        $diag = Get-Content -LiteralPath $out -Raw
        foreach ($needle in 'Abode Night View diagnostics', 'DPI awareness applied', 'Monitors',
                            'Rendering', 'Adobe applications') {
            if ($diag -notmatch [regex]::Escape($needle)) { throw "--diagnostics missing '$needle'" }
        }
        Write-Host "  --diagnostics produced a complete report" -ForegroundColor DarkGray

        Start-Process -FilePath $app -ArgumentList '--probe' -NoNewWindow -Wait -RedirectStandardOutput $out
        $probe = Get-Content -LiteralPath $out -Raw
        foreach ($needle in 'Adobe InDesign', 'Adobe Illustrator', 'Adobe InCopy',
                            'Adobe Photoshop', 'Adobe Acrobat') {
            if ($probe -notmatch [regex]::Escape($needle)) { throw "--probe missing '$needle'" }
        }
        Write-Host "  --probe reports every supported product" -ForegroundColor DarkGray

        # Portable-first: from a writable folder the binary must choose to keep
        # its settings BESIDE itself, not in AppData. The diagnostic subcommands
        # do not write the file (they are read-only on purpose), so what is
        # checked is the decision, which --diagnostics reports.
        $want = Join-Path $tmp 'AbodeNightView.ini'
        if ($diag -notmatch [regex]::Escape($want)) {
            throw "settings path is not beside the executable; --diagnostics says otherwise"
        }
        if ($diag -notmatch 'settings mode\s+portable') {
            throw "settings did not resolve as portable from a writable folder"
        }
        Write-Host "  settings resolve beside the executable (portable)" -ForegroundColor DarkGray
    }
    finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

# 4b. What the binary itself says.
#
# A removed feature is only removed when it is not in the artifact. The menu is
# the one surface with no mechanical test -- it exists only while a human holds
# the mouse still -- so the closest thing to asserting "Greyscale is not in the
# tray menu" is asserting that the string is not in the file. Literals are UTF-16
# in the metadata and can start on an odd byte, so both alignments are searched.
$bytes = [IO.File]::ReadAllBytes($exe)
$text  = [Text.Encoding]::Unicode.GetString($bytes) + "`0" +
         [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)

foreach ($gone in 'Warm  (approximate)', 'Greyscale (experimental)', 'Shader (experimental)',
                  '(experimental)', '--warm=') {
    if ($text.Contains($gone)) { throw "removed feature still present in the binary: '$gone'" }
}
Write-Host "  no removed mode appears in the shipped binary" -ForegroundColor DarkGray

foreach ($needed in 'Abode Night View: [', 'Enabled', 'Disabled',
                    'Vibecoded by Vixen420 in August 2026.') {
    if (-not $text.Contains($needed)) { throw "expected string missing from the binary: '$needed'" }
}
Write-Host "  tray states and the About attribution are present" -ForegroundColor DarkGray

# The default shortcuts were withdrawn in 1.3. Their absence cannot be asserted
# by string search -- Settings.cs still names all four, because a settings file
# written by 1.2 has to be recognised in order to have them removed. What CAN be
# asserted from outside is that the binary tells the user nothing is bound, and
# that the schedule that replaces the toggle-by-hand workflow is in there.
foreach ($needed in 'No shortcut is bound out of the box', 'Abode Night View (Schedule)') {
    if (-not $text.Contains($needed)) { throw "expected string missing from the binary: '$needed'" }
}
Write-Host "  ships with no bound shortcut, and with the schedule" -ForegroundColor DarkGray

# Wording the user reported as wrong, and its replacement. Both directions,
# because "the new sentence is in there" says nothing about whether the old one
# is still in there beside it, on a code path nobody looked at.
# This script is deliberately pure ASCII. It has no byte-order mark, and
# PowerShell 5.1 reads a BOM-less file in the machine's ANSI codepage -- so an en
# dash typed literally here becomes two mojibake characters, and a "this wording
# is gone" assertion then passes because it is searching for a string that could
# not be in the binary either way. As a code point it means the same thing on
# every machine.
$dash = [string][char]0x2013

foreach ($gone in ('Off ' + $dash + ' switch it on and off yourself'),
                  'can only be known by trying it',
                  'No schedule: the tray item',
                  ($dash + ' no document open'),
                  ($dash + ' unsupported version')) {
    if ($text.Contains($gone)) { throw "superseded wording still in the binary: '$gone'" }
}
foreach ($needed in 'Schedule is currently off.', 'Dimming is set to switch on at ',
                    '<i>Esc</i>', '<i>Backspace</i>', 'By default, nothing is bound.') {
    if (-not $text.Contains($needed)) { throw "expected string missing from the binary: '$needed'" }
}
Write-Host "  the schedule and shortcut wording is the current one" -ForegroundColor DarkGray

# Spacing. The reported fault was a run of spaces in the middle of a line --
# "Photoshop 2026  - no document open", "Strength  (55%)" -- and every one of
# them came from a separator padded by hand at the point of use. The label text
# now comes from TrayState and is asserted by Audit.exe --selftest; what is
# asserted HERE is the shape of what shipped, because a string literal with two
# spaces in it is visible in the artifact whether or not anything calls it.
foreach ($gone in 'Strength  (', 'Schedule  (', 'Targets  (', '%   (k = ', 'Custom...   (',
                  '% dim     white') {
    if ($text.Contains($gone)) { throw "a padded separator is still in the binary: '$gone'" }
}
Write-Host "  no hand-padded separator survives in a label" -ForegroundColor DarkGray

# The strength readout, and the hover text's reason for dimming nothing. Both
# were reported by the user against a shipped binary, so both are asserted
# against the artifact. 'white 255 becomes' is the superseded wording; it is
# checked as plain ASCII because the string it came from carried en dashes and
# the point here is to catch the phrase, not the punctuation around it.
if ($text.Contains('white 255 becomes')) {
    throw "the superseded strength readout is still in the binary"
}
foreach ($needed in '% dim | 255 (pure white) displays as ', 'nothing to dim') {
    if (-not $text.Contains($needed)) { throw "expected string missing from the binary: '$needed'" }
}
Write-Host "  the strength readout and the hover reasons are the current ones" -ForegroundColor DarkGray

# 4c. Licence and provenance.
#
# The GPL asks a program to be able to show its notice. Shipping a binary that
# cannot is not a cosmetic miss, and the About box is the only place it is said,
# so the three paragraphs are checked in the artifact rather than in the source.
foreach ($needed in 'is free software: you can redistribute it',
                    'either version 3 of the License',
                    'WITHOUT ANY WARRANTY',
                    'FITNESS FOR A PARTICULAR PURPOSE',
                    'You should have received a copy of the GNU General Public License',
                    'https://www.gnu.org/licenses/',
                    'https://github.com/VulpesNexus',
                    'https://github.com/VulpesNexus/abode-night-view') {
    if (-not $text.Contains($needed)) { throw "licence or provenance missing from the binary: '$needed'" }
}
Write-Host "  the GPL notice and both repository links are in the About box" -ForegroundColor DarkGray

$license = Join-Path $root 'LICENSE'
if (-not (Test-Path -LiteralPath $license)) { throw "LICENSE is missing from the repository root" }
$licenseText = Get-Content -LiteralPath $license -Raw
if (-not ($licenseText.Contains('GNU GENERAL PUBLIC LICENSE') -and
          $licenseText.Contains('Version 3, 29 June 2007'))) {
    throw "LICENSE is not the GPL version 3 text"
}
Write-Host ("  LICENSE is the full GPLv3 text ({0:N0} bytes)" -f (Get-Item -LiteralPath $license).Length) -ForegroundColor DarkGray

# Icon.ToBitmap() cannot read a PNG-compressed .ico entry: it walks the payload
# as a DIB and returns noise. Every entry in this project's icons is PNG (see
# tools\make-icon.ps1), so a single call to it is a scrambled picture somewhere
# in the user interface -- which is exactly how it got into the About box and
# stayed there. Grep, because the failure is silent and looks like a rendering
# glitch rather than like a bug.
$offenders = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' |
             Select-String -Pattern '\.ToBitmap\(' -SimpleMatch:$false |
             Where-Object { $_.Line -notmatch '^\s*//' }
if ($offenders) {
    throw ("Icon.ToBitmap() cannot render this project's PNG icons; use Graphics.DrawIcon: " +
           ($offenders | ForEach-Object { "{0}:{1}" -f $_.Filename, $_.LineNumber }) -join ', ')
}
Write-Host "  no icon is drawn through Icon.ToBitmap()" -ForegroundColor DarkGray

# The notification artwork. A -resource: flag quietly dropped from the build
# produces a binary that works and shows the wrong picture, which is exactly the
# class of fault a smoke test exists for. Managed resource NAMES are stored in
# the metadata as UTF-8, so they are searched in the byte stream as ASCII --
# where the UTF-16 string literals in the code cannot be mistaken for them.
$ascii = [Text.Encoding]::ASCII.GetString($bytes)
foreach ($res in 'balloon-off.ico', 'balloon-on.ico') {
    if (-not $ascii.Contains($res)) { throw "notification artwork missing from the binary: '$res'" }
}
Write-Host "  both notification icons are embedded" -ForegroundColor DarkGray

# 5. Describe exactly what was produced.
$item = Get-Item -LiteralPath $exe
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$vi   = $item.VersionInfo

Write-Host ""
Write-Host "Artifact" -ForegroundColor Cyan
Write-Host ("  path          {0}" -f $item.FullName)
Write-Host ("  size          {0:N0} bytes" -f $item.Length)
Write-Host ("  product       {0} {1}" -f $vi.ProductName, $vi.ProductVersion)
Write-Host ("  file version  {0}" -f $vi.FileVersion)
Write-Host ("  architecture  x64 (see README: Architecture)")
Write-Host ("  runtime       .NET Framework 4.x - an in-box Windows component, nothing to install")
Write-Host ("  privileges    runs as the invoking user; no manifest requests elevation")
Write-Host ("  targets       InDesign, Illustrator, InCopy, Photoshop, Acrobat")
Write-Host ("  sha256        {0}" -f $hash)
Write-Host ""
Write-Host "Hand dist\AbodeNightView.exe to a tester. It needs no other file." -ForegroundColor Green
Write-Host "SmartScreen will warn on first run because the binary is unsigned - see README." -ForegroundColor DarkGray
