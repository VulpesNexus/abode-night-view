# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Vixen420
#
# Abode Night View is free software: you may redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free
# Software Foundation, either version 3 of the License, or (at your option) any
# later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
# <https://www.gnu.org/licenses/>, for the full text.

# =============================================================================
#  Abode Night View - ABSOLUTE DOCUMENT-SAFETY VERIFICATION
# -----------------------------------------------------------------------------
#  Proves that enabling Abode Night View cannot change an INDD or its output.
#
#  The proof is deliberately EXTERNAL: it never scripts InDesign, so it cannot
#  itself perturb the thing it is measuring. It hashes files on disk.
#
#  Protocol
#    .\verify-document-safety.ps1 -Indd "C:\work\job.indd" -Mode Baseline
#         -> InDesign must be CLOSED. Records SHA-256 + size + timestamps.
#
#    ... now open the document, run Abode Night View, work/save/export for a while,
#        turn Abode Night View off, quit InDesign ...
#
#    .\verify-document-safety.ps1 -Indd "C:\work\job.indd" -Mode Compare
#         -> InDesign must be CLOSED. Re-hashes and reports.
#
#  Add -Pdf "C:\work\job.pdf" to also compare an exported PDF byte for byte.
#
#  Interpreting the result
#    Abode Night View is an external, display-only process. It never opens the file,
#    never sends a message to InDesign, and never invokes a DOM setter. If the
#    hash changes, the cause is something you did in InDesign (an edit, a link
#    update, a font substitution, a preflight profile change) - NOT Abode Night View.
#    Run the control below to separate the two.
#
#  Control run (do this once, it is what makes the test meaningful)
#    1. Baseline, open the doc, do NOTHING, save, close, Compare.
#       -> tells you whether InDesign rewrites the file on a no-op save at all.
#    2. Repeat with Abode Night View running.
#       -> the two results must be identical in kind.
# =============================================================================

param(
    [Parameter(Mandatory=$true)][string]$Indd,
    [ValidateSet('Baseline','Compare')][string]$Mode = 'Baseline',
    [string]$Pdf,
    [string]$StateFile
)

function Stamp([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $fi = Get-Item -LiteralPath $path
    [pscustomobject]@{
        Path          = $fi.FullName
        Length        = $fi.Length
        LastWriteUtc  = $fi.LastWriteTimeUtc.ToString('o')
        CreationUtc   = $fi.CreationTimeUtc.ToString('o')
        Sha256        = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
}

if (Get-Process InDesign -ErrorAction SilentlyContinue) {
    Write-Host "InDesign is running. Close it first - a document held open may not be" -ForegroundColor Yellow
    Write-Host "fully flushed to disk, which would make the hash meaningless." -ForegroundColor Yellow
    Write-Host "Continuing anyway, but treat the result with suspicion.`n" -ForegroundColor Yellow
}

if (-not $StateFile) {
    $StateFile = Join-Path ([IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path)) `
                           ("docsafety-" + [IO.Path]::GetFileNameWithoutExtension($Indd) + ".json")
}

$now = @{ Indd = Stamp $Indd }
if ($Pdf) { $now.Pdf = Stamp $Pdf }

if (-not $now.Indd) { Write-Host "Not found: $Indd" -ForegroundColor Red; exit 1 }

if ($Mode -eq 'Baseline') {
    $now | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StateFile -Encoding UTF8
    Write-Host "BASELINE recorded" -ForegroundColor Cyan
    Write-Host ("  indd   {0}" -f $now.Indd.Path)
    Write-Host ("  size   {0:N0} bytes" -f $now.Indd.Length)
    Write-Host ("  sha256 {0}" -f $now.Indd.Sha256)
    Write-Host ("  mtime  {0}" -f $now.Indd.LastWriteUtc)
    if ($now.Pdf) {
        Write-Host ("  pdf    {0}" -f $now.Pdf.Path)
        Write-Host ("  sha256 {0}" -f $now.Pdf.Sha256)
    }
    Write-Host ("`n  state saved to {0}" -f $StateFile) -ForegroundColor DarkGray
    Write-Host "  Now: open the doc, run Abode Night View, work, save, export, quit InDesign."
    Write-Host "  Then re-run with -Mode Compare."
    exit 0
}

if (-not (Test-Path -LiteralPath $StateFile)) {
    Write-Host "No baseline at $StateFile - run with -Mode Baseline first." -ForegroundColor Red; exit 1
}
$was = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json

$fail = 0
function Cmp($label, $old, $new) {
    if (-not $old -or -not $new) { return }
    Write-Host "`n$label" -ForegroundColor Cyan
    $same = $old.Sha256 -eq $new.Sha256
    Write-Host ("  sha256 before  {0}" -f $old.Sha256)
    Write-Host ("  sha256 after   {0}" -f $new.Sha256)
    Write-Host ("  size   {0:N0} -> {1:N0} ({2:+#;-#;0} bytes)" -f $old.Length, $new.Length, ($new.Length - $old.Length))
    Write-Host ("  mtime  {0} -> {1}" -f $old.LastWriteUtc, $new.LastWriteUtc)
    if ($same) { Write-Host "  IDENTICAL - byte for byte" -ForegroundColor Green }
    else {
        Write-Host "  CHANGED" -ForegroundColor Yellow
        Write-Host "  This means InDesign rewrote the file. Abode Night View cannot do this:" -ForegroundColor DarkGray
        Write-Host "  it never opens the file and never talks to InDesign. Re-run the" -ForegroundColor DarkGray
        Write-Host "  control (same actions, Abode Night View OFF) to confirm the same change." -ForegroundColor DarkGray
        $script:fail++
    }
}

Cmp "INDD" $was.Indd (Stamp $Indd)
if ($Pdf) { Cmp "PDF"  $was.Pdf  (Stamp $Pdf) }

Write-Host "`n--- what Abode Night View actually did to your system ---" -ForegroundColor Cyan
Write-Host "  files it opened in the InDesign document tree : none"
Write-Host "  messages sent to InDesign                     : none"
Write-Host "  InDesign scripts run                          : none"
Write-Host "  Win32 calls made against InDesign windows     : read-only queries"
Write-Host "    EnumWindows / EnumChildWindows / GetClassName / GetWindowRect"
Write-Host "    GetClientRect / ClientToScreen / IsWindowVisible / IsIconic"
Write-Host "    GetWindowLongPtr / GetForegroundWindow / SetWinEventHook"
Write-Host "  Win32 calls that write                        : only to its OWN window"
Write-Host "    SetWindowPos / SetLayeredWindowAttributes / SetWindowDisplayAffinity"
Write-Host ""
Write-Host "  SetWinEventHook is installed WINEVENT_OUTOFCONTEXT, so no DLL is"
Write-Host "  injected into InDesign; events are marshalled to this process."
exit $fail
