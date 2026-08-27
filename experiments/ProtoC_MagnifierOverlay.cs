// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - Prototype C : Windows Magnification API viewport filter
// ----------------------------------------------------------------------------
//  Concept
//      Not magnification. A WC_MAGNIFIER control at a magnification factor of
//      exactly 1.0, hosted in a borderless click-through layered window parked
//      precisely over the InDesign document canvas, with the canvas itself as
//      the source rectangle, and a MAGCOLOREFFECT colour matrix applied.
//
//  Why bother, when Prototype B already dims?
//      MAGCOLOREFFECT is a full 5x5 GDI+ colour matrix, so it can do things
//      alpha blending cannot: per-channel gain (true warm dim), channel mixing
//      (grayscale), and inversion. It still cannot do a non-linear curve - the
//      matrix is affine by construction. See FEASIBILITY.md.
//
//  What this prototype is designed to answer
//      1. Does the magnifier recurse when the host sits over its own source?
//         Two candidate fixes are wired up and independently switchable:
//           --exclude=filter    MagSetWindowFilterList(MW_FILTERMODE_EXCLUDE)
//           --exclude=affinity  SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)
//           --exclude=both / none
//      2. Is 1.0x rendering pixel-sharp, or is there resampling?
//      3. Does interaction stay normal (the host is WS_EX_TRANSPARENT)?
//      4. What does it cost? FPS / CPU / working set are printed once a second.
//      5. Does it survive InDesign's GPU Preview rendering path?
//
//  Diagnostic first
//      Run  ProtoC.exe --diag=700  first. That parks the host 700 px to the
//      right of the canvas instead of on top of it, so you can see the source
//      and the filtered copy side by side. If that looks right, the pipeline
//      and the matrix are correct and any problem is purely recursion.
//
//  IMPORTANT: the Magnification API is not supported under WOW64. This must be
//      built as a 64-bit executable. The build script passes -platform:x64.
//
//  Usage
//      ProtoC.exe [options]
//        --target=owldoc|canvas|client|window|class:NAME|hwnd:0x..
//        --inset=L,T,R,B
//        --mode=neutral|warm|gray|invert|identity
//        --k=0.45            output gain, 0..1   (0.45 ~= "55% dim")
//        --warm=0.6          extra blue reduction 0..1, mode=warm
//        --factor=1.0        magnification factor; 1.0 means 1:1
//        --exclude=filter|affinity|both|none
//        --cursor            add MS_SHOWMAGNIFIEDCURSOR (expect a doubled cursor)
//        --diag=700          offset the host from the source, for A/B comparison
//        --interval=16       refresh timer in ms
//        --zmode=above|topmost
//        --proc=InDesign
//
//      Ctrl+Alt+Q quits. Closing the console window also quits.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
//  Magnification.dll
// ---------------------------------------------------------------------------
internal static class Mag
{
    /// <summary>float[3][3] geometric transform. v[0][0] and v[1][1] are the scale.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MAGTRANSFORM
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public float[] v;
        public static MAGTRANSFORM Scale(float f)
        {
            var t = new MAGTRANSFORM { v = new float[9] };
            t.v[0] = f; t.v[4] = f; t.v[8] = 1.0f;
            return t;
        }
    }

    /// <summary>
    /// float[5][5] colour matrix, GDI+ semantics: the colour is the ROW vector
    /// [R G B A 1] multiplied on the LEFT of the matrix. So element [i][j] is
    /// "how much of input channel i goes into output channel j", row 4 holds
    /// the additive offsets, and column 4 must be (0,0,0,0,1).
    /// Indices below are row*5 + col.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] transform;

        public static MAGCOLOREFFECT Identity()
        {
            var e = new MAGCOLOREFFECT { transform = new float[25] };
            e.transform[0] = e.transform[6] = e.transform[12] = e.transform[18] = e.transform[24] = 1f;
            return e;
        }

        /// <summary>R'=kR*R, G'=kG*G, B'=kB*B. Alpha row left as identity.</summary>
        public static MAGCOLOREFFECT Gain(float kR, float kG, float kB)
        {
            var e = new MAGCOLOREFFECT { transform = new float[25] };
            e.transform[0] = kR;      // [0][0] R -> R
            e.transform[6] = kG;      // [1][1] G -> G
            e.transform[12] = kB;     // [2][2] B -> B
            e.transform[18] = 1f;     // [3][3] A -> A   (alpha must pass through)
            e.transform[24] = 1f;     // [4][4] homogeneous 1
            return e;
        }

        /// <summary>Rec.709 luma into all three channels, then scaled by k.</summary>
        public static MAGCOLOREFFECT Grayscale(float k)
        {
            var e = new MAGCOLOREFFECT { transform = new float[25] };
            float r = 0.2126f * k, g = 0.7152f * k, b = 0.0722f * k;
            e.transform[0] = r; e.transform[1] = r; e.transform[2] = r;   // row 0: input R
            e.transform[5] = g; e.transform[6] = g; e.transform[7] = g;   // row 1: input G
            e.transform[10] = b; e.transform[11] = b; e.transform[12] = b; // row 2: input B
            e.transform[18] = 1f;
            e.transform[24] = 1f;
            return e;
        }

        /// <summary>out = k*(1-in): inverted and dimmed. Offsets live in row 4.</summary>
        public static MAGCOLOREFFECT Invert(float k)
        {
            var e = new MAGCOLOREFFECT { transform = new float[25] };
            e.transform[0] = -k; e.transform[6] = -k; e.transform[12] = -k;
            e.transform[18] = 1f;
            e.transform[20] = k; e.transform[21] = k; e.transform[22] = k;  // row 4 = translation
            e.transform[24] = 1f;
            return e;
        }

        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < 5; r++)
            {
                sb.Append("      ");
                for (int c = 0; c < 5; c++)
                    sb.Append(transform[r * 5 + c].ToString("0.000", CultureInfo.InvariantCulture).PadLeft(8));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    [DllImport("Magnification.dll")] public static extern bool MagInitialize();
    [DllImport("Magnification.dll")] public static extern bool MagUninitialize();
    [DllImport("Magnification.dll")] public static extern bool MagSetWindowSource(IntPtr hwnd, Native.RECT rect);
    [DllImport("Magnification.dll")] public static extern bool MagGetWindowSource(IntPtr hwnd, out Native.RECT rect);
    [DllImport("Magnification.dll")] public static extern bool MagSetWindowTransform(IntPtr hwnd, ref MAGTRANSFORM t);
    [DllImport("Magnification.dll")] public static extern bool MagSetColorEffect(IntPtr hwnd, ref MAGCOLOREFFECT e);
    [DllImport("Magnification.dll")] public static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] list);
    [DllImport("Magnification.dll")] public static extern bool MagShowSystemCursor(bool show);

    public const string WC_MAGNIFIER = "Magnifier";
    public const int MW_FILTERMODE_EXCLUDE = 0;   // MW_FILTERMODE_INCLUDE (1) is
                                                  // documented as unsupported on
                                                  // Windows 7 and newer.
    public const int MS_SHOWMAGNIFIEDCURSOR = 0x0001;
    public const int MS_CLIPAROUNDCURSOR = 0x0002;
    public const int MS_INVERTCOLORS = 0x0004;
}

internal static class NativeC
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string m);

    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
}

// ---------------------------------------------------------------------------
//  Host window: layered, opaque, click-through
// ---------------------------------------------------------------------------
internal sealed class MagHost : Form
{
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
                      WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080,
                      WS_EX_APPWINDOW = 0x00040000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;

    public MagHost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Text = "NightView MagHost";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // The magnifier control REQUIRES a WS_EX_LAYERED host. WS_EX_TRANSPARENT
            // is what makes clicks fall through to InDesign; the docs call this out
            // explicitly for magnifier hosts.
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            cp.ExStyle &= ~WS_EX_APPWINDOW;
            cp.Style |= WS_CLIPCHILDREN;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }
}

// ---------------------------------------------------------------------------
//  Controller
// ---------------------------------------------------------------------------
internal sealed class MagContext : ApplicationContext
{
    private readonly ViewportLocator _loc = new ViewportLocator();
    private readonly MagHost _host = new MagHost();
    private readonly Timer _refresh = new Timer();
    private readonly Timer _stats = new Timer();
    private readonly HotkeyWindowC _hotkeys;
    private readonly List<IntPtr> _hooks = new List<IntPtr>();
    private Native.WinEventProc _cb;

    private IntPtr _hwndMag = IntPtr.Zero;
    private string _mode = "neutral";
    private float _k = 0.45f, _warm = 0.6f, _factor = 1.0f;
    private string _exclude = "both", _zmode = "above";
    private bool _magCursor;
    private int _diagOffset;
    private int _interval = 16;

    private Native.RECT _lastRect;
    private bool _shown;
    private IntPtr _lastZOwner = IntPtr.Zero;
    private bool _movingOrSizing;
    private long _frames;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastCpu;
    private readonly Process _self = Process.GetCurrentProcess();

    public MagContext(string[] argv)
    {
        ParseArgs(argv);

        Console.WriteLine("Abode Night View - Prototype C (Magnification API)");
        Console.WriteLine("64-bit process : {0}   (required: the Magnification API is not supported under WOW64)",
            Environment.Is64BitProcess);

        if (!Mag.MagInitialize())
        {
            Console.WriteLine("MagInitialize FAILED, win32 error {0}. Aborting.", Marshal.GetLastWin32Error());
            Environment.Exit(2);
        }
        Console.WriteLine("MagInitialize  : ok");

        if (!_loc.FindMain())
        {
            Console.WriteLine("InDesign not found. Start it and try again.");
            Mag.MagUninitialize(); Environment.Exit(1);
        }
        Console.WriteLine("target         : {0}", _loc.Describe());

        _host.Show();
        Native.SetLayeredWindowAttributes(_host.Handle, 0, 255, Native.LWA_ALPHA);  // fully opaque host

        // The magnifier control is a child of the layered host and fills it.
        int style = NativeC.WS_CHILD | NativeC.WS_VISIBLE;
        if (_magCursor) style |= Mag.MS_SHOWMAGNIFIEDCURSOR;   // off by default: the
                                                               // real system cursor is
                                                               // already drawn on top
        _hwndMag = NativeC.CreateWindowExW(0, Mag.WC_MAGNIFIER, "NightViewMag", style,
            0, 0, 100, 100, _host.Handle, IntPtr.Zero,
            NativeC.GetModuleHandleW(null), IntPtr.Zero);

        if (_hwndMag == IntPtr.Zero)
        {
            Console.WriteLine("CreateWindowEx(WC_MAGNIFIER) FAILED, win32 error {0}", Marshal.GetLastWin32Error());
            Mag.MagUninitialize(); Environment.Exit(3);
        }
        Console.WriteLine("magnifier hwnd : 0x{0:X}", _hwndMag.ToInt64());

        var t = Mag.MAGTRANSFORM.Scale(_factor);
        Console.WriteLine("MagSetWindowTransform({0:0.###}) : {1}", _factor, Mag.MagSetWindowTransform(_hwndMag, ref t));

        ApplyColorEffect();
        ApplyExclusion();

        _hotkeys = new HotkeyWindowC(id => { if (id == 4) Quit(); });
        InstallHooks();

        _refresh.Interval = _interval;
        _refresh.Tick += (s, e) => Tick();
        _refresh.Start();

        _lastCpu = _self.TotalProcessorTime;
        _stats.Interval = 1000;
        _stats.Tick += (s, e) => PrintStats();
        _stats.Start();

        Console.WriteLine();
        Console.WriteLine("Running. Ctrl+Alt+Q to quit.");
        Console.WriteLine("Watch for: recursion (infinite mirror), a doubled cursor, soft/resampled text,");
        Console.WriteLine("scroll lag, and whether clicks reach InDesign normally.");
        Console.WriteLine();
    }

    // ------------------------------------------------------------ the matrix

    private void ApplyColorEffect()
    {
        Mag.MAGCOLOREFFECT e;
        switch (_mode)
        {
            case "identity": e = Mag.MAGCOLOREFFECT.Identity(); break;
            case "gray": e = Mag.MAGCOLOREFFECT.Grayscale(_k); break;
            case "invert": e = Mag.MAGCOLOREFFECT.Invert(_k); break;
            case "warm":
                // True per-channel gain - this is the thing a tint overlay cannot do.
                e = Mag.MAGCOLOREFFECT.Gain(_k, _k * (1f - 0.10f * _warm), _k * (1f - 0.45f * _warm));
                break;
            default: e = Mag.MAGCOLOREFFECT.Gain(_k, _k, _k); break;
        }
        bool ok = Mag.MagSetColorEffect(_hwndMag, ref e);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "MagSetColorEffect(mode={0}, k={1:0.00}) : {2}", _mode, _k, ok));
        Console.Write(e.Dump());
    }

    private void ApplyExclusion()
    {
        // The docs say the magnification window itself is automatically excluded.
        // The HOST is a different HWND, sits over the source rect, and is opaque -
        // so it is the recursion candidate. Both defences are tried here.
        if (_exclude == "filter" || _exclude == "both")
        {
            var list = new[] { _host.Handle };
            bool ok = Mag.MagSetWindowFilterList(_hwndMag, Mag.MW_FILTERMODE_EXCLUDE, list.Length, list);
            Console.WriteLine("MagSetWindowFilterList(EXCLUDE host 0x{0:X}) : {1}", _host.Handle.ToInt64(), ok);
        }
        if (_exclude == "affinity" || _exclude == "both")
        {
            bool ok = Native.SetWindowDisplayAffinity(_host.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            Console.WriteLine("SetWindowDisplayAffinity(host, WDA_EXCLUDEFROMCAPTURE) : {0}{1}",
                ok, ok ? "" : "  err=" + Marshal.GetLastWin32Error());
        }
        if (_exclude == "none")
            Console.WriteLine("exclusion      : NONE (recursion expected - this is the control case)");
    }

    // -------------------------------------------------------------- tracking

    private void InstallHooks()
    {
        _cb = OnWinEvent;
        _hooks.Add(Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _cb, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS));
        _hooks.Add(Native.SetWinEventHook(
            Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _cb, _loc.Pid, 0, Native.WINEVENT_OUTOFCONTEXT));
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint th, uint t)
    {
        if (idObject != Native.OBJID_WINDOW) return;
        if (ev == Native.EVENT_SYSTEM_MOVESIZESTART && hwnd == _loc.MainWindow) _movingOrSizing = true;
        if (ev == Native.EVENT_SYSTEM_MOVESIZEEND && hwnd == _loc.MainWindow) _movingOrSizing = false;
        if (ev == Native.EVENT_OBJECT_SHOW || ev == Native.EVENT_OBJECT_HIDE || ev == Native.EVENT_OBJECT_DESTROY)
            _loc.InvalidateTargetCache();
        if (ev == Native.EVENT_SYSTEM_FOREGROUND) _lastZOwner = IntPtr.Zero;
    }

    /// <summary>
    /// One frame: reposition the host if needed, then push the source rect and
    /// invalidate. The magnifier control does not poll on its own - the MSDN
    /// sample drives it from a ~16 ms timer, and so do we.
    /// </summary>
    private void Tick()
    {
        if (_movingOrSizing) { Hide(); return; }

        Native.RECT src = _loc.Resolve();
        if (src.IsEmpty) { Hide(); return; }

        Native.RECT client = Native.ClientRectOnScreen(_loc.MainWindow);
        src.Left = Math.Max(src.Left, client.Left); src.Top = Math.Max(src.Top, client.Top);
        src.Right = Math.Min(src.Right, client.Right); src.Bottom = Math.Min(src.Bottom, client.Bottom);
        if (src.IsEmpty) { Hide(); return; }

        // The host normally sits exactly on the source. --diag pushes it aside so
        // source and result can be compared without any recursion in the way.
        Native.RECT dst = src;
        if (_diagOffset != 0) { dst.Left += _diagOffset; dst.Right += _diagOffset; }

        bool zNeedsWork = _zmode == "topmost"
            ? _lastZOwner != Native.HWND_TOPMOST
            : _lastZOwner != _loc.MainWindow;

        if (!_shown || !dst.Same(_lastRect) || zNeedsWork)
        {
            IntPtr after = _zmode == "topmost" ? Native.HWND_TOPMOST : _loc.MainWindow;
            uint flags = Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW |
                         Native.SWP_NOOWNERZORDER | Native.SWP_NOSENDCHANGING;
            if (!zNeedsWork) flags |= Native.SWP_NOZORDER;
            Native.SetWindowPos(_host.Handle, zNeedsWork ? after : IntPtr.Zero,
                dst.Left, dst.Top, dst.W, dst.H, flags);
            NativeC.MoveWindow(_hwndMag, 0, 0, dst.W, dst.H, true);
            _lastRect = dst; _shown = true;
            if (zNeedsWork) _lastZOwner = after;
        }

        Mag.MagSetWindowSource(_hwndMag, src);
        NativeC.InvalidateRect(_hwndMag, IntPtr.Zero, false);
        _frames++;
    }

    private void Hide()
    {
        if (!_shown) return;
        Native.SetWindowPos(_host.Handle, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_HIDEWINDOW);
        _shown = false; _lastZOwner = IntPtr.Zero;
    }

    private void PrintStats()
    {
        _self.Refresh();
        TimeSpan cpu = _self.TotalProcessorTime;
        double cpuPct = (cpu - _lastCpu).TotalMilliseconds / (10.0 * Environment.ProcessorCount);
        _lastCpu = cpu;
        Console.WriteLine("{0:HH:mm:ss}  {1,4} fps   cpu {2,5:0.0}%   ws {3,6:0} MB   rect {4}",
            DateTime.Now, _frames, cpuPct, _self.WorkingSet64 / 1048576.0, _lastRect);
        _frames = 0;
    }

    // ------------------------------------------------------------------ misc

    private void ParseArgs(string[] argv)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var a in argv)
        {
            if (a.StartsWith("--target=")) _loc.TargetSpec = a.Substring(9);
            else if (a.StartsWith("--mode=")) _mode = a.Substring(7).ToLowerInvariant();
            else if (a.StartsWith("--k=")) _k = float.Parse(a.Substring(4), inv);
            else if (a.StartsWith("--warm=")) _warm = float.Parse(a.Substring(7), inv);
            else if (a.StartsWith("--factor=")) _factor = float.Parse(a.Substring(9), inv);
            else if (a.StartsWith("--exclude=")) _exclude = a.Substring(10).ToLowerInvariant();
            else if (a.StartsWith("--zmode=")) _zmode = a.Substring(8).ToLowerInvariant();
            else if (a.StartsWith("--diag=")) _diagOffset = int.Parse(a.Substring(7), inv);
            else if (a.StartsWith("--interval=")) _interval = Math.Max(1, int.Parse(a.Substring(11), inv));
            else if (a.StartsWith("--proc=")) _loc.ProcessName = a.Substring(7);
            else if (a == "--cursor") _magCursor = true;
            else if (a.StartsWith("--inset="))
            {
                var p = a.Substring(8).Split(',');
                if (p.Length == 4) { _loc.InsetL = int.Parse(p[0]); _loc.InsetT = int.Parse(p[1]);
                                     _loc.InsetR = int.Parse(p[2]); _loc.InsetB = int.Parse(p[3]); }
            }
        }
    }

    private void Quit()
    {
        _refresh.Stop(); _stats.Stop();
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hotkeys.Dispose();
        if (_hwndMag != IntPtr.Zero) NativeC.DestroyWindow(_hwndMag);
        _host.Close();
        Mag.MagUninitialize();
        Console.WriteLine("stopped.");
        ExitThread();
    }
}

internal sealed class HotkeyWindowC : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private readonly Action<int> _cb;

    public HotkeyWindowC(Action<int> cb)
    {
        _cb = cb;
        CreateHandle(new CreateParams());
        Native.RegisterHotKey(Handle, 4, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.Q);
    }

    protected override void WndProc(ref Message m)
    { if (m.Msg == WM_HOTKEY) _cb((int)m.WParam); base.WndProc(ref m); }

    public void Dispose() { Native.UnregisterHotKey(Handle, 4); DestroyHandle(); }
}

internal static class ProtoC
{
    [STAThread]
    private static void Main(string[] argv)
    {
        Native.SetProcessDpiAwarenessContext(Native.DPI_PMv2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MagContext(argv));
    }
}
