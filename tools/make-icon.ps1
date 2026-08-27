# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Vixen420
#
# Abode Night View is free software: you may redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free
# Software Foundation, either version 3 of the License, or (at your option) any
# later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
# <https://www.gnu.org/licenses/>, for the full text.

# =============================================================================
#  Abode Night View - build assets/AbodeNightView.ico from a source image
# -----------------------------------------------------------------------------
#  Windows Vista and later accept PNG-compressed images inside an .ico for every
#  size, so the container this writes is just ICONDIR + one ICONDIRENTRY and one
#  PNG payload per size. That avoids the BMP/AND-mask encoding entirely, which is
#  where hand-rolled icon writers usually go wrong.
#
#  The source is letterboxed into a square with transparent margins rather than
#  stretched, so the artwork keeps its proportions at every size.
#
#      .\tools\make-icon.ps1
#      .\tools\make-icon.ps1 -Source C:\path\to\other.png
#      .\tools\make-icon.ps1 -Source x.png -Out y.ico -Sizes 16,24,32,48,64,96
#
#  -Sizes exists because not every icon needs every size. The application icon
#  is handed to Explorer and to the notification area and needs the full set up
#  to 256; a balloon icon is only ever asked for at SM_CXICON, so carrying a
#  256 px entry for it would put a hundred kilobytes of unreachable artwork into
#  the binary.
# =============================================================================

param(
    [string]$Source = '',
    [string]$Out    = '',
    [int[]] $Sizes  = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -LiteralPath (Split-Path -LiteralPath $MyInvocation.MyCommand.Path)
# Both defaults live inside the repository, so a clean checkout regenerates
# every icon with no argument and nothing from outside the tree.
if (-not $Source) { $Source = Join-Path $root 'assets\source-icon.png' }
if (-not $Out) { $Out = Join-Path $root 'assets\AbodeNightView.ico' }
$outDir = Split-Path -LiteralPath $Out
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

if (-not (Test-Path -LiteralPath $Source)) { throw "Source image not found: $Source" }

$src = [System.Drawing.Image]::FromFile($Source)
Write-Host ("source {0}  {1}x{2}" -f (Split-Path $Source -Leaf), $src.Width, $src.Height)

# The default is the set Windows actually asks for: notification area at
# 100/125/150/200%, Explorer's small/medium/large/extra-large, and the 256 px
# entry Vista+ shells use for the largest views.
$sizes = $Sizes | Sort-Object -Unique

$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # Letterbox: scale to fit, centre, keep aspect.
    $scale = [Math]::Min($s / $src.Width, $s / $src.Height)
    $w = [int][Math]::Round($src.Width  * $scale)
    $h = [int][Math]::Round($src.Height * $scale)
    $x = [int][Math]::Round(($s - $w) / 2)
    $y = [int][Math]::Round(($s - $h) / 2)
    $g.DrawImage($src, $x, $y, $w, $h)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@($s, $ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}
$src.Dispose()

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type 1 = icon
    $bw.Write([UInt16]$pngs.Count)

    # Directory entries come first, so every offset is known up front.
    $offset = 6 + 16 * $pngs.Count
    foreach ($e in $pngs) {
        $size = $e[0]; $data = $e[1]
        # 256 is written as 0 in the one-byte width/height fields.
        $bw.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
        $bw.Write([Byte]$(if ($size -ge 256) { 0 } else { $size }))
        $bw.Write([Byte]0)               # palette count (0 = no palette)
        $bw.Write([Byte]0)               # reserved
        $bw.Write([UInt16]1)             # colour planes
        $bw.Write([UInt16]32)            # bits per pixel
        $bw.Write([UInt32]$data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($e in $pngs) { $bw.Write($e[1]) }
}
finally { $bw.Dispose(); $fs.Dispose() }

$info = Get-Item -LiteralPath $Out
Write-Host ("wrote {0}  {1:N0} bytes  sizes {2}" -f $info.FullName, $info.Length, ($sizes -join ','))
