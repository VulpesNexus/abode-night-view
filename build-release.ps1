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

# 2. Delete every generated icon, so the shipped binary cannot carry artwork
#    left over from an earlier edit of a source PNG. build.ps1 regenerates each
#    one it finds missing, from the sources in the repository, which is what
#    makes a clean checkout produce a byte-identical resource.
#
#    Deleted here and regenerated there, rather than regenerated here, so the
#    size ladder for each icon is written down in exactly one place. This block
#    used to repeat "-Sizes 32,48,64,96" next to build.ps1's own copy of the
#    same list, which is a release script quietly overriding the build it is
#    about to run -- and it would have shipped 1.4.0 without the small entries
#    the tray icon needs.
foreach ($generated in 'assets\AbodeNightView.ico',
                       'assets\state-off.ico',
                       'assets\state-on.ico') {
    $g = Join-Path $root $generated
    if (Test-Path -LiteralPath $g) { Remove-Item -LiteralPath $g -Force }
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
        #
        # The directory is compared by identity, not by spelling. %TEMP% can be
        # an 8.3 short path -- on GitHub's hosted Windows runners it is
        # C:\Users\RUNNER~1\AppData\Local\Temp -- while Windows hands a process
        # the long form of its own module path. String-comparing the two is
        # comparing two spellings of one directory, and fails on a machine that
        # is behaving perfectly. A marker file settles it without knowing
        # anything about how either side chose to spell the path.
        $said = ($diag -split "`n" | Where-Object { $_ -match 'settings file' } |
                 Select-Object -First 1)
        if (-not $said) { throw "--diagnostics has no 'settings file' row" }
        $reported = ($said -replace '^\s*settings file\s+', '').Trim()
        if ([IO.Path]::GetFileName($reported) -ne 'AbodeNightView.ini') {
            throw "the settings file is not named AbodeNightView.ini; --diagnostics said '$reported'"
        }

        $marker = 'smoke-' + [Guid]::NewGuid().ToString('N') + '.marker'
        New-Item -ItemType File -Path (Join-Path $tmp $marker) | Out-Null
        $reportedDir = Split-Path -Parent $reported
        if (-not $reportedDir -or -not (Test-Path -LiteralPath (Join-Path $reportedDir $marker))) {
            # Quote both sides: an assertion that only says "it did not match"
            # cannot be diagnosed from a build log on a machine nobody can
            # reach, which is exactly where this one first failed.
            throw ("settings do not resolve beside the executable" +
                   "`n    the executable is in  $tmp" +
                   "`n    --diagnostics says    $reported")
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
# written by 1.2 has to be recognized in order to have them removed. What CAN be
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
foreach ($gone in 'white 255 becomes',
                  '% dim | 255 (pure white) displays as ',
                  '{0}% dim') {
    if ($text.Contains($gone)) { throw "a superseded strength readout is still in the binary: '$gone'" }
}
foreach ($needed in '{0}% (k = {1:0.00})', '255 (pure white) now displays as {0}.',
                    'nothing to dim') {
    if (-not $text.Contains($needed)) { throw "expected string missing from the binary: '$needed'" }
}
Write-Host "  the strength readout and the hover reasons are the current ones" -ForegroundColor DarkGray

# "No document open" is the Targets menu's per-product note. It stopped being
# one of the hover's reasons, but the hover built that suffix at run time from
# " | " + the reason, so there is no literal here to find the absence of: the
# assertion that the hover has stopped saying it lives in Audit.exe --selftest,
# which can call IdleReason and read null back. What the artifact CAN say is
# that the menu still has the words at all.
if (-not $text.Contains('no document open')) {
    throw "the Targets menu's per-product note is missing from the binary"
}
Write-Host "  the Targets menu can still name an empty document window" -ForegroundColor DarkGray

# The off state says only that it is off. Both formats used to be built by
# substituting "ON"/"OFF" into one composite format string, which is what put a
# strength on a hover for a screen that was not being dimmed. Those two literals
# are the evidence the substitution is gone: while either survives in the
# artifact, something is still assembling a percentage around the state word.
foreach ($gone in 'Abode Night View: [{0}] | {1}%', '[{0}] {1}% (k = {2:0.00})') {
    if ($text.Contains($gone)) {
        throw ("the state word is still being substituted into a format that carries " +
               "a strength, so the off state can still show one: '$gone'")
    }
}
foreach ($needed in 'Abode Night View: [OFF]', 'Abode Night View: [ON] | {0}%', '[ON] {0}% (k = {1:0.00})') {
    if (-not $text.Contains($needed)) { throw "expected string missing from the binary: '$needed'" }
}
Write-Host "  switched off, the hover and the notification carry no strength" -ForegroundColor DarkGray

# 4c. License and provenance.
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
    if (-not $text.Contains($needed)) { throw "license or provenance missing from the binary: '$needed'" }
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

# The state artwork, which is now what the tray icon is made of as well as the
# balloon. A -resource: flag quietly dropped from the build produces a binary
# that works and shows the wrong picture, which is exactly the class of fault a
# smoke test exists for. Managed resource NAMES are stored in the metadata as
# UTF-8, so they are searched in the byte stream as ASCII -- where the UTF-16
# string literals in the code cannot be mistaken for them.
$ascii = [Text.Encoding]::ASCII.GetString($bytes)
foreach ($res in 'state-off.ico', 'state-on.ico') {
    if (-not $ascii.Contains($res)) { throw "state artwork missing from the binary: '$res'" }
}
Write-Host "  both state icons are embedded" -ForegroundColor DarkGray

# ...and that they carry an entry the notification area can use. Embedding the
# resource is not enough: the sizes inside it are chosen by build.ps1, and an
# .ico whose smallest entry is 32 px is a tray icon the shell has to halve.
# Read back out of the generated containers rather than trusted from the list
# that generated them.
foreach ($container in 'assets\state-off.ico', 'assets\state-on.ico') {
    $raw = [IO.File]::ReadAllBytes((Join-Path $root $container))
    $count = [BitConverter]::ToUInt16($raw, 4)
    $widths = @()
    for ($i = 0; $i -lt $count; $i++) {
        $w = $raw[6 + $i * 16]
        $widths += $(if ($w -eq 0) { 256 } else { [int]$w })
    }
    foreach ($needed in 16, 20, 24, 32) {
        if ($widths -notcontains $needed) {
            throw ("$container has no ${needed}px entry, so the notification area " +
                   "would be shown a scaled icon (it carries " + ($widths -join ',') + ")")
        }
    }
}
Write-Host "  both carry 16/20/24/32 px for the notification area" -ForegroundColor DarkGray

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
Write-Host ("  architecture  x64 (see docs/internals.md: Architecture)")
Write-Host ("  runtime       .NET Framework 4.x - an in-box Windows component, nothing to install")
Write-Host ("  privileges    runs as the invoking user; no manifest requests elevation")
Write-Host ("  targets       InDesign, Illustrator, InCopy, Photoshop, Acrobat")
Write-Host ("  sha256        {0}" -f $hash)
Write-Host ""
Write-Host "Hand dist\AbodeNightView.exe to a tester. It needs no other file." -ForegroundColor Green
Write-Host "SmartScreen will warn on first run because the binary is unsigned - see README." -ForegroundColor DarkGray
