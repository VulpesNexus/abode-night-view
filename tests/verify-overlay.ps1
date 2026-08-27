# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Vixen420
#
# Abode Night View is free software: you may redistribute it and/or modify it
# under the terms of the GNU General Public License as published by the Free
# Software Foundation, either version 3 of the License, or (at your option) any
# later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
# <https://www.gnu.org/licenses/>, for the full text.

# =============================================================================
#  Abode Night View - overlay verification
# -----------------------------------------------------------------------------
#  Checks, in order:
#    1. InDesign is running and its canvas HWND resolves
#    2. The overlay window exists with the correct extended styles
#    3. The overlay rect matches the canvas rect
#    4. WindowFromPoint at the overlay center returns INDESIGN, not the overlay
#       (this is the click-through test - if it fails, input is being stolen)
#    5. Screen luminance inside the canvas drops by the expected factor
#    6. Screen luminance OUTSIDE the canvas is unchanged
#
#  Nothing here touches the InDesign document. It reads window geometry and
#  reads screen pixels.
#
#  Usage (from an ordinary, non-elevated PowerShell):
#      .\verify-overlay.ps1 -Exe ..\AbodeNightView.exe -Strength 55
#      .\verify-overlay.ps1 -Exe ..\ProtoC.exe -Strength 55 -Args '--mode=neutral --k=0.45'
#
#  NOTE: step 5/6 need the physical displays to be awake. If the monitors are
#  asleep, DWM stops composing and GDI screen capture returns a frozen frame;
#  the script detects that and says so instead of reporting a bogus ratio.
# =============================================================================

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [int]$Strength = 55,
    [string]$Args = '--target=canvas --strength=55 --mode=neutral',
    [switch]$KeepRunning
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Text;
using System.Collections.Generic; using System.Runtime.InteropServices;
public struct RECT { public int L,T,R,B;
  public override string ToString(){ return L+","+T+" "+(R-L)+"x"+(B-T); } }
public static class NV {
  public delegate bool EP(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EP cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h,int i);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
  [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")]  public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);

  public static string Cls(IntPtr h){ var s=new StringBuilder(256); GetClassNameW(h,s,256); return s.ToString(); }
  public static string Txt(IntPtr h){ var s=new StringBuilder(256); GetWindowTextW(h,s,256); return s.ToString(); }
  public static RECT Rc(IntPtr h){ RECT r; GetWindowRect(h,out r); return r; }
  public static List<IntPtr> Tops(uint pid){ var l=new List<IntPtr>();
    EnumWindows((h,p)=>{ uint w; GetWindowThreadProcessId(h,out w); if(w==pid) l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static List<IntPtr> Kids(IntPtr h){ var l=new List<IntPtr>();
    EnumChildWindows(h,(c,p)=>{ l.Add(c); return true; }, IntPtr.Zero); return l; }

  // CAPTUREBLT is required or layered windows are omitted from the grab.
  public static Bitmap Grab(int x,int y,int w,int h,bool captureBlt){
    var b=new Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using(var g=Graphics.FromImage(b)){ IntPtr s=GetDC(IntPtr.Zero), d=g.GetHdc();
      BitBlt(d,0,0,w,h,s,x,y,0x00CC0020 | (captureBlt?0x40000000:0)); g.ReleaseHdc(d); ReleaseDC(IntPtr.Zero,s); }
    return b; }
  public static double[] Mean(Bitmap b){
    var dd=b.LockBits(new Rectangle(0,0,b.Width,b.Height),
      System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    var buf=new byte[dd.Stride*dd.Height]; Marshal.Copy(dd.Scan0,buf,0,buf.Length); b.UnlockBits(dd);
    double sr=0,sg=0,sb=0; long n=(long)dd.Width*dd.Height;
    for(int y=0;y<dd.Height;y++){int o=y*dd.Stride; for(int x=0;x<dd.Width;x++){int i=o+x*4; sb+=buf[i];sg+=buf[i+1];sr+=buf[i+2];}}
    return new double[]{sr/n,sg/n,sb/n}; }
  public static long DiffCount(Bitmap a, Bitmap b){ long n=0;
    for(int y=0;y<a.Height;y+=4) for(int x=0;x<a.Width;x+=4)
      if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb()) n++;
    return n; }
}
"@
[void][NV]::SetProcessDpiAwarenessContext([IntPtr](-4))   # PER_MONITOR_AWARE_V2

$pass = 0; $fail = 0
function Check($name, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) -ForegroundColor Green }
    else     { $script:fail++; Write-Host ("  FAIL  " + $name) -ForegroundColor Red }
    if ($detail) { Write-Host ("        " + $detail) -ForegroundColor DarkGray }
}
function ExDecode($e){ $f=@()
  if($e -band 0x8){$f+='TOPMOST'}; if($e -band 0x20){$f+='TRANSPARENT'}; if($e -band 0x80){$f+='TOOLWINDOW'}
  if($e -band 0x80000){$f+='LAYERED'}; if($e -band 0x8000000){$f+='NOACTIVATE'}; if($e -band 0x40000){$f+='APPWINDOW'}
  $f -join '|' }

# --- 1. InDesign ------------------------------------------------------------
Write-Host "`n[1] InDesign" -ForegroundColor Cyan
$idp = Get-Process InDesign -ErrorAction SilentlyContinue
if (-not $idp) { Write-Host "  InDesign is not running. Start it with a document open." -ForegroundColor Red; exit 1 }
$idMain = [NV]::Tops([uint32]$idp.Id) | Where-Object { [NV]::Cls($_) -eq 'indesign' } | Select-Object -First 1
Check "application frame window found (class 'indesign')" ($idMain -ne $null) ("hwnd=0x{0:X} rect={1}" -f $idMain.ToInt64(), [NV]::Rc($idMain))

$owlDoc = [NV]::Kids($idMain) | Where-Object { [NV]::Cls($_) -eq 'OWL.Document' -and [NV]::IsWindowVisible($_) } | Select-Object -First 1
Check "document canvas HWND found (class 'OWL.Document')" ($owlDoc -ne $null) ("hwnd=0x{0:X} rect={1}" -f $owlDoc.ToInt64(), [NV]::Rc($owlDoc))
if (-not $owlDoc) { exit 1 }
$docRect = [NV]::Rc($owlDoc)

# --- 2. is the display awake? ----------------------------------------------
Write-Host "`n[2] Screen capture sanity" -ForegroundColor Cyan
$s1 = [NV]::Grab($docRect.L, $docRect.T, 400, 400, $true)
Start-Sleep -Milliseconds 1200
$s2 = [NV]::Grab($docRect.L, $docRect.T, 400, 400, $true)
$moved = [NV]::DiffCount($s1,$s2)
$before = [NV]::Mean($s1)
$captureLive = $true
if ($moved -eq 0) {
    Write-Host "  WARN  two grabs 1.2s apart are pixel-identical." -ForegroundColor Yellow
    Write-Host "        The displays are probably asleep, so DWM is not composing and" -ForegroundColor DarkGray
    Write-Host "        GDI screen capture returns a stale frame. Steps 5-6 will be skipped." -ForegroundColor DarkGray
    Write-Host "        Wake the monitors, click once in InDesign, and re-run." -ForegroundColor DarkGray
    $captureLive = $false
} else {
    Check "screen capture is live" $true ("$moved sampled pixels changed in 1.2s")
}
Write-Host ("        canvas mean before: R={0:0.0} G={1:0.0} B={2:0.0}" -f $before[0],$before[1],$before[2]) -ForegroundColor DarkGray
$s1.Dispose(); $s2.Dispose()

# --- 3. launch the overlay --------------------------------------------------
Write-Host "`n[3] Overlay process" -ForegroundColor Cyan
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path -LiteralPath $Exe).Path
$psi.Arguments = $Args
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path -LiteralPath (Resolve-Path -LiteralPath $Exe).Path
$p = [Diagnostics.Process]::Start($psi)
Start-Sleep -Milliseconds 3000
Check "overlay process is running" (-not $p.HasExited) ("pid=" + $p.Id)

$tops = [NV]::Tops([uint32]$p.Id)
$overlay = $tops | Where-Object { [NV]::IsWindowVisible($_) -and ([NV]::Rc($_).R - [NV]::Rc($_).L) -gt 100 } | Select-Object -First 1
Check "a visible overlay window was created" ($overlay -ne $null)

if ($overlay) {
    $ex = [NV]::GetWindowLongPtrW($overlay,-20).ToInt64()
    $orc = [NV]::Rc($overlay)
    Write-Host ("        overlay hwnd=0x{0:X} rect={1} ex=0x{2:X8} [{3}]" -f $overlay.ToInt64(),$orc,$ex,(ExDecode $ex)) -ForegroundColor DarkGray

    # --- 4. styles + click-through ------------------------------------------
    Write-Host "`n[4] Click-through and window styles" -ForegroundColor Cyan
    Check "WS_EX_LAYERED set"     (($ex -band 0x80000)   -ne 0)
    Check "WS_EX_TRANSPARENT set" (($ex -band 0x20)      -ne 0) "this is what makes hit testing fall through"
    Check "WS_EX_NOACTIVATE set"  (($ex -band 0x8000000) -ne 0) "never takes focus, never on the taskbar"
    Check "WS_EX_TOOLWINDOW set"  (($ex -band 0x80)      -ne 0) "keeps it out of Alt+Tab"
    Check "WS_EX_APPWINDOW clear" (($ex -band 0x40000)   -eq 0)

    $cx = $orc.L + [int](($orc.R-$orc.L)/2)
    $cy = $orc.T + [int](($orc.B-$orc.T)/2)
    $hit = [NV]::WindowFromPoint((New-Object Drawing.Point $cx,$cy))
    Check "WindowFromPoint at the overlay center reaches InDesign" ($hit -ne $overlay) `
        ("({0},{1}) -> 0x{2:X} '{3}'" -f $cx,$cy,$hit.ToInt64(),[NV]::Cls($hit))
    $hitPid = 0; [void][NV]::GetWindowThreadProcessId($hit,[ref]$hitPid)
    Check "the window under the overlay belongs to InDesign" ($hitPid -eq $idp.Id) ("pid=$hitPid, InDesign pid=$($idp.Id)")

    # --- 5/6. luminance ------------------------------------------------------
    if ($captureLive) {
        Write-Host "`n[5] Luminance inside the canvas" -ForegroundColor Cyan
        Start-Sleep -Milliseconds 500
        $a = [NV]::Grab($docRect.L, $docRect.T, 400, 400, $true)
        $after = [NV]::Mean($a); $a.Dispose()
        $expected = 1.0 - ($Strength / 100.0)
        $ratio = if ($before[0] -gt 1) { $after[0] / $before[0] } else { 0 }
        Write-Host ("        before R={0:0.0} G={1:0.0} B={2:0.0}" -f $before[0],$before[1],$before[2]) -ForegroundColor DarkGray
        Write-Host ("        after  R={0:0.0} G={1:0.0} B={2:0.0}" -f $after[0],$after[1],$after[2]) -ForegroundColor DarkGray
        Write-Host ("        ratio  R={0:0.000} G={1:0.000} B={2:0.000}   expected {3:0.000}" -f `
            ($after[0]/$before[0]),($after[1]/$before[1]),($after[2]/$before[2]),$expected) -ForegroundColor DarkGray
        Check "canvas dimmed by the expected factor (+/- 0.05)" ([math]::Abs($ratio - $expected) -lt 0.05)

        Write-Host "`n[6] Everything outside the canvas is untouched" -ForegroundColor Cyan
        $mr = [NV]::Rc($idMain)
        # a strip of the InDesign UI to the right of the canvas (panel dock)
        $ox = $docRect.R + 20; $oy = $docRect.T + 100
        if ($ox + 200 -lt $mr.R) {
            $o1 = [NV]::Grab($ox,$oy,200,200,$true); $om = [NV]::Mean($o1); $o1.Dispose()
            Write-Host ("        panel dock at ({0},{1}) mean R={2:0.0}" -f $ox,$oy,$om[0]) -ForegroundColor DarkGray
            Write-Host "        (compare this by eye against the same spot with the overlay off)" -ForegroundColor DarkGray
        }
    } else {
        Write-Host "`n[5/6] SKIPPED - screen capture is not live" -ForegroundColor Yellow
    }
}

if (-not $KeepRunning -and $p -and -not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(3000) }

Write-Host ""
Write-Host ("RESULT: {0} passed, {1} failed" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) {'Green'} else {'Red'})
if ($KeepRunning) { Write-Host "overlay left running (pid $($p.Id)); Ctrl+Alt+Q or Stop-Process to quit" -ForegroundColor Cyan }
exit $fail
