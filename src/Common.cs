// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - shared Win32 layer
// ----------------------------------------------------------------------------
//  Everything in here is read-only with respect to the target application:
//  enumeration and geometry queries. Nothing sends a message to InDesign,
//  posts input to it, or scripts it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int W { get { return Right - Left; } }
        public int H { get { return Bottom - Top; } }
        public bool IsEmpty { get { return W <= 0 || H <= 0; } }
        public bool Same(RECT o) { return Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom; }
        public override string ToString()
        { return string.Format(CultureInfo.InvariantCulture, "{0},{1} {2}x{3}", Left, Top, W, H); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd,
                                      int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod,
        WinEventProc cb, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    // --- added for the compatibility audit: DPI, monitors, OS version ----------
    // Every one of these is optional. They exist on some Windows versions and not
    // others, and a missing export throws EntryPointNotFoundException at the call
    // site rather than at load time, so each is reached only through a Try* helper
    // below. See Diagnostics.cs for what is probed and reported.

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(
        IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    [DllImport("ntdll.dll")] public static extern int RtlGetVersion(ref OSVERSIONINFOEX v);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(
        IntPtr h, int attr, out int value, int size);
    public const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// True for a window Windows keeps in the z-order but does not draw. Modern
    /// Windows leaves a lot of these around -- suspended packaged applications,
    /// windows belonging to another virtual desktop -- and they are visible to
    /// IsWindowVisible. Anything that walks the z-order and reasons about what is
    /// covering what has to skip them or it will react to windows that are not
    /// on the screen at all.
    /// </summary>
    public static bool IsCloaked(IntPtr h)
    {
        try
        {
            int v;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out v, sizeof(int)) == 0) return v != 0;
        }
        catch { }
        return false;
    }

    public delegate bool MonitorEnumProc(IntPtr mon, IntPtr dc, ref RECT r, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        public static MONITORINFOEX Create()
        { return new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) }; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
        public byte wProductType, wReserved;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;
    public const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79,
                     SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CMONITORS = 80;
    public const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    public const int GWLP_HWNDPARENT = -8;

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3, GW_OWNER = 4;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                      SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080,
                      SWP_NOOWNERZORDER = 0x0200, SWP_NOSENDCHANGING = 0x0400;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint LWA_ALPHA = 0x2;
    public const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public static readonly IntPtr DPI_PMv2 = new IntPtr(-4);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    public static string ClassOf(IntPtr h)
    { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }

    public static string TitleOf(IntPtr h)
    { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }

    // ---------------------------------------------------------------- DPI ----
    //  Three generations of API, newest first. The call itself is what throws on
    //  an older Windows -- the P/Invoke resolves lazily -- so each one is tried
    //  inside its own try/catch and the result is recorded for --diagnostics.
    //
    //  This used to be a bare SetProcessDpiAwarenessContext() with the return
    //  value discarded. On Windows 10 before 1703 that export does not exist and
    //  the process died on the first line of Main with EntryPointNotFoundException.

    public static string DpiAwarenessApplied = "(not attempted)";

    public static void ApplyBestDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_PMv2) != IntPtr.Zero)
            { DpiAwarenessApplied = "PerMonitorV2 (SetProcessDpiAwarenessContext)"; return; }
            DpiAwarenessApplied = "PerMonitorV2 refused (already set?)";
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        try
        {
            if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0)
            { DpiAwarenessApplied = "PerMonitor v1 (SetProcessDpiAwareness, shcore)"; return; }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        try
        {
            if (SetProcessDPIAware())
            { DpiAwarenessApplied = "System DPI aware (SetProcessDPIAware)"; return; }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        DpiAwarenessApplied = "none available - running DPI-unaware";
    }

    /// <summary>Effective DPI of the monitor a window is on, or 96 if unknowable.</summary>
    public static uint DpiOf(IntPtr h)
    {
        try { uint d = GetDpiForWindow(h); if (d != 0) return d; }
        catch (EntryPointNotFoundException) { }
        try
        {
            uint x, y;
            IntPtr mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out x, out y) == 0) return x;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        try { return GetDpiForSystem(); } catch (EntryPointNotFoundException) { }
        return 96;
    }

    /// <summary>Real OS version. Environment.OSVersion lies without a manifest.</summary>
    public static string OsVersionString()
    {
        try
        {
            var v = new OSVERSIONINFOEX();
            v.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX));
            if (RtlGetVersion(ref v) == 0)
                return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}",
                    v.dwMajorVersion, v.dwMinorVersion, v.dwBuildNumber);
        }
        catch { }
        return Environment.OSVersion.Version.ToString();
    }

    public static int OsBuild()
    {
        try
        {
            var v = new OSVERSIONINFOEX();
            v.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX));
            if (RtlGetVersion(ref v) == 0) return v.dwBuildNumber;
        }
        catch { }
        return Environment.OSVersion.Version.Build;
    }

    public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

    public static RECT ClientRectOnScreen(IntPtr h)
    {
        RECT c; GetClientRect(h, out c);
        POINT o = new POINT(); ClientToScreen(h, ref o);
        return new RECT { Left = o.X, Top = o.Y, Right = o.X + c.W, Bottom = o.Y + c.H };
    }

    public static List<IntPtr> Descendants(IntPtr parent)
    {
        var all = new List<IntPtr>();
        EnumChildWindows(parent, (h, p) => { all.Add(h); return true; }, IntPtr.Zero);
        return all;
    }
}

// ---------------------------------------------------------------------------
//  Locating the InDesign document canvas
// ---------------------------------------------------------------------------
internal sealed class ViewportLocator
{
    public string ProcessName = "InDesign";

    /// <summary>
    /// Window class of the application frame. "indesign" is the only value the
    /// shipping default ever uses; it is settable so the same tracking code can be
    /// pointed at a controllable test window during the compatibility audit, and
    /// so a future Adobe release that renames the class is a command-line flag
    /// rather than a rebuild. "*" means "the largest top-level window of the
    /// process", which is looser and is not the default for that reason.
    /// </summary>
    public string FrameClass = "indesign";

    public string TargetSpec = "owldoc";
    public int InsetL, InsetT, InsetR, InsetB;

    /// <summary>
    /// Optional product adapter. When set, viewport resolution goes through it
    /// instead of the built-in OWL walk, which is how the verifier can measure
    /// Acrobat -- a product with no OWL.Document anywhere in it.
    ///
    /// Left null by default ON PURPOSE: with no adapter this class behaves exactly
    /// as it did when the InDesign results were verified, so the regression
    /// baseline is still produced by the code that produced it.
    /// </summary>
    public AdobeTarget Adapter;
    public string AdapterRegion = "canvas";

    public IntPtr MainWindow { get; private set; }
    public uint Pid { get; private set; }
    private IntPtr _cachedTarget;
    private string _cachedClass = "";

    /// <summary>
    /// Explicitly attach to one process (--pid=). Zero means "choose automatically".
    /// </summary>
    public void AttachTo(uint pid) { PinnedPid = pid; Reattach(); }

    /// <summary>Forget the latched frame and process and choose again from scratch.</summary>
    public void Reattach() { MainWindow = IntPtr.Zero; Pid = 0; _cachedTarget = IntPtr.Zero; }

    /// <summary>Set by --pid=. Zero means the automatic policy below applies.</summary>
    public uint PinnedPid;

    /// <summary>
    /// Frame window of the InDesign application (class "indesign").
    ///
    /// The instance and the frame are both LATCHED once chosen and kept until the
    /// window they refer to is destroyed. Re-deciding on every call would make the
    /// behavior non-deterministic with more than one InDesign open, because the
    /// tie-breakers -- working set, window area -- change while you work, and the
    /// overlay would hop between instances on its own. Tray > Re-attach and
    /// --pid= are the two ways to choose deliberately.
    /// </summary>
    public bool FindMain()
    {
        if (MainWindow != IntPtr.Zero && Native.IsWindow(MainWindow)) return true;

        var procs = Process.GetProcessesByName(ProcessName);
        if (procs.Length == 0) { MainWindow = IntPtr.Zero; return false; }

        uint pid;
        if (PinnedPid != 0 && Array.Exists(procs, p => (uint)p.Id == PinnedPid))
            pid = PinnedPid;
        else if (Pid != 0 && Array.Exists(procs, p => (uint)p.Id == Pid))
            pid = Pid;                       // keep the instance we were already on
        else
            pid = (uint)procs.OrderByDescending(p => p.WorkingSet64).First().Id;

        IntPtr best = IntPtr.Zero; long bestArea = -1;
        Native.EnumWindows((h, p) =>
        {
            uint wpid; Native.GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid) return true;
            if (FrameClass != "*" &&
                !Native.ClassOf(h).Equals(FrameClass, StringComparison.OrdinalIgnoreCase)) return true;
            if (FrameClass == "*" && !Native.IsWindowVisible(h)) return true;
            var r = Native.RectOf(h);
            long a = (long)r.W * r.H;
            if (a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);

        MainWindow = best; Pid = pid; _cachedTarget = IntPtr.Zero;
        return best != IntPtr.Zero;
    }

    /// <summary>The currently cached target HWND, or zero. Callers use this to decide
    /// whether a window event is actually relevant before throwing the cache away.</summary>
    public IntPtr CachedTarget { get { return _cachedTarget; } }

    public void InvalidateTargetCache() { _cachedTarget = IntPtr.Zero; }

    /// <summary>True when the foreground window belongs to InDesign (incl. its dialogs).</summary>
    public bool InDesignHasFocus()
    {
        uint fgPid; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);
        return fgPid == Pid;
    }

    /// <summary>Resolve the target rectangle in physical screen pixels. Empty if unavailable.</summary>
    public Native.RECT Resolve()
    {
        if (!FindMain()) return new Native.RECT();
        if (Native.IsIconic(MainWindow) || !Native.IsWindowVisible(MainWindow)) return new Native.RECT();

        Native.RECT r;
        string spec = TargetSpec.ToLowerInvariant();

        if (Adapter != null)
        {
            var vps = Adapter.Viewports(MainWindow);
            IntPtr best = IntPtr.Zero; long bestArea = -1;
            foreach (IntPtr vp in vps)
            {
                var vr = Native.RectOf(vp);
                long a = (long)vr.W * vr.H;
                if (a > bestArea) { bestArea = a; best = vp; }
            }
            if (best == IntPtr.Zero) return new Native.RECT();
            r = Adapter.RectOf(MainWindow, best, AdapterRegion);
            _cachedTarget = AdapterRegion == "document" ? Adapter.Document(best) : Adapter.Canvas(best);
            _cachedClass = Native.ClassOf(_cachedTarget);
            r.Left += InsetL; r.Top += InsetT; r.Right -= InsetR; r.Bottom -= InsetB;
            return r.IsEmpty ? new Native.RECT() : r;
        }

        if (spec == "window") r = Native.RectOf(MainWindow);
        else if (spec == "client") r = Native.ClientRectOnScreen(MainWindow);
        else
        {
            IntPtr h = ResolveTargetHwnd(spec);
            if (h == IntPtr.Zero) return new Native.RECT();
            r = Native.RectOf(h);
        }

        r.Left += InsetL; r.Top += InsetT; r.Right -= InsetR; r.Bottom -= InsetB;
        return r.IsEmpty ? new Native.RECT() : r;
    }

    private IntPtr ResolveTargetHwnd(string spec)
    {
        if (spec.StartsWith("hwnd:"))
        {
            long v;
            try { v = Convert.ToInt64(spec.Substring(5).Replace("0x", ""), 16); }
            catch { return IntPtr.Zero; }     // junk in the settings file, not a crash
            var h = new IntPtr(v);
            return Native.IsWindow(h) ? h : IntPtr.Zero;
        }

        // Cached handle still good?
        if (_cachedTarget != IntPtr.Zero && Native.IsWindow(_cachedTarget)
            && Native.IsWindowVisible(_cachedTarget)
            && Native.ClassOf(_cachedTarget) == _cachedClass)
            return _cachedTarget;

        string wantClass =
            spec.StartsWith("class:") ? spec.Substring(6) :
            "OWL.Document";

        var kids = Native.Descendants(MainWindow);
        IntPtr doc = IntPtr.Zero; long docArea = -1;
        foreach (var h in kids)
        {
            if (!Native.IsWindowVisible(h)) continue;
            if (!Native.ClassOf(h).Equals(wantClass, StringComparison.OrdinalIgnoreCase)) continue;
            var rr = Native.RectOf(h);
            long a = (long)rr.W * rr.H;
            if (a > docArea) { docArea = a; doc = h; }
        }
        if (doc == IntPtr.Zero) return IntPtr.Zero;

        IntPtr result = doc;
        if (spec == "canvas")
        {
            // Inside OWL.Document, Adobe applications nest view containers. The
            // largest one that is strictly smaller than OWL.Document is the canvas
            // proper - OWL.Document itself includes the rulers and the scrollbars.
            //
            // The rule lives in TargetRegistry so that this class and the product
            // adapters cannot drift apart: it was verified against InDesign, and
            // Illustrator, InCopy and Photoshop are now relying on the same code
            // rather than on a second copy of it that merely looks the same.
            IntPtr best = TargetRegistry.LargestStrictlyInside(doc, 2);
            if (best != IntPtr.Zero) result = best;
        }

        _cachedTarget = result; _cachedClass = Native.ClassOf(result);
        return result;
    }

    /// <summary>
    /// The document container and the canvas inside it, together, in physical
    /// screen pixels. False if either cannot be resolved.
    ///
    /// The difference between the two is the point. In InDesign the canvas child
    /// window is inset from the document container by exactly the strips Adobe
    /// draws the rulers and scrollbars into, so dimming the canvas leaves the
    /// rulers alone for free -- measured at ratio 1.000, and it follows View >
    /// Show/Hide Rulers on its own because the child window resizes. In
    /// Illustrator and Photoshop the canvas child starts at the document origin
    /// and the rulers are painted inside it, so they are dimmed. The verifier
    /// needs both rectangles to be able to tell those two cases apart and to
    /// assert that the strips it does exclude really do stay undimmed.
    /// </summary>
    public bool ResolveDocumentAndCanvas(out Native.RECT doc, out Native.RECT canvas)
    {
        doc = new Native.RECT(); canvas = new Native.RECT();
        if (!FindMain()) return false;
        if (Native.IsIconic(MainWindow) || !Native.IsWindowVisible(MainWindow)) return false;

        if (Adapter != null)
        {
            IntPtr best = IntPtr.Zero; long bestArea = -1;
            foreach (IntPtr vp in Adapter.Viewports(MainWindow))
            {
                var vr = Native.RectOf(vp);
                long a = (long)vr.W * vr.H;
                if (a > bestArea) { bestArea = a; best = vp; }
            }
            if (best == IntPtr.Zero) return false;
            doc = Adapter.RectOf(MainWindow, best, Region.Document);
            canvas = Adapter.RectOf(MainWindow, best, Region.Canvas);
        }
        else
        {
            // Resolving by spec latches a cache that the caller did not ask to
            // change; put it back so measuring cannot alter what is tracked.
            IntPtr savedTarget = _cachedTarget; string savedClass = _cachedClass;
            IntPtr d = ResolveTargetHwnd("owldoc");
            IntPtr c = ResolveTargetHwnd("canvas");
            _cachedTarget = savedTarget; _cachedClass = savedClass;
            if (d == IntPtr.Zero || c == IntPtr.Zero) return false;
            doc = Native.RectOf(d); canvas = Native.RectOf(c);
        }
        return !doc.IsEmpty && !canvas.IsEmpty;
    }

    public string Describe()
    {
        if (!FindMain()) return "InDesign not found";
        IntPtr t = TargetSpec == "window" || TargetSpec == "client"
            ? MainWindow : ResolveTargetHwnd(TargetSpec.ToLowerInvariant());
        return string.Format(CultureInfo.InvariantCulture,
            "pid={0} main=0x{1:X} dpi={2} ({3}%) target={4} hwnd=0x{5:X} class={6} rect={7}",
            Pid, MainWindow.ToInt64(), Native.DpiOf(MainWindow), Native.DpiOf(MainWindow) * 100 / 96,
            TargetSpec, t.ToInt64(), t == IntPtr.Zero ? "-" : Native.ClassOf(t), Resolve());
    }
}

