// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - synthetic Adobe-shaped windows for the audit
// ----------------------------------------------------------------------------
//  Why raw Win32 and not WinForms
//      The adapters recognise a product by its frame window CLASS and validate
//      the viewport by its structural relationship -- an OWL.Document whose
//      parent is an OWL.TabGroup. To test that machinery mechanically, the
//      harness has to be able to produce windows with exactly those class names.
//      WinForms cannot: NativeWindow rewrites a requested class name into
//      "WindowsForms10.<name>.app.0.<hash>", so a Control claiming to be
//      "OWL.Document" registers as something else and the thing under test is
//      never exercised. RegisterClassW takes the name it is given.
//
//      Window classes are per-process unless CS_GLOBALCLASS is set, and it is
//      not set here, so registering "OWL.Document" inside the audit process
//      cannot affect Adobe or anything else on the machine.
//
//  What it builds, per frame:
//
//      NVHarnessFrame                       top-level, movable, resizable
//        OWL.Dock                           one per viewport, side by side
//          OWL.TabPane
//            OWL.TabGroup
//              OWL.Document                 <- what the adapter must find
//                NVHarnessInner             <- inset like a scrollbar; the canvas
//
//  The inner canvas paints the same six-level step wedge the single-window
//  target does, so photometry works per viewport rather than per application.
//
//  Not shipped. Development tree only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class HarnessNative
{
    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public Native.RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref Native.RECT r, IntPtr brush);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string n);

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CHILD = 0x40000000,
                      WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000,
                      WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000,
                      WS_BORDER = 0x00800000;
    public const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_CLOSE = 0x0010,
                      WM_DESTROY = 0x0002;
    public const int SW_SHOW = 5, SW_SHOWNA = 8, SW_HIDE = 0, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public static readonly IntPtr IDC_ARROW = new IntPtr(32512);

    public static int Bgr(int r, int g, int b) { return r | (g << 8) | (b << 16); }
}

/// <summary>
/// One synthetic Adobe-shaped application window. Everything the audit needs to
/// do to it -- move it, resize it, add a document, close a document, raise it
/// without its owned windows, put an owned dialog over it -- is a method here,
/// so the multi-target behaviour can be tested without touching Adobe at all.
/// </summary>
internal sealed class SyntheticApp : IDisposable
{
    public const string FrameClass = "NVHarnessFrame";
    private const string InnerClass = "NVHarnessInner";
    private const string PopupClass = "NVHarnessPopup";

    public static readonly int[] Levels = { 0, 32, 64, 128, 192, 255 };

    private static bool _registered;
    private static HarnessNative.WndProc _proc;      // must outlive every window
    private static readonly Dictionary<IntPtr, int> _fill = new Dictionary<IntPtr, int>();
    private static readonly Dictionary<IntPtr, bool> _wedge = new Dictionary<IntPtr, bool>();

    public IntPtr Frame { get; private set; }
    public readonly List<IntPtr> Documents = new List<IntPtr>();
    private readonly List<IntPtr> _all = new List<IntPtr>();
    private readonly List<IntPtr> _popups = new List<IntPtr>();
    private int _viewports;

    // ------------------------------------------------------------- registration

    private static void EnsureClasses()
    {
        if (_registered) return;
        _registered = true;
        _proc = Proc;
        IntPtr inst = HarnessNative.GetModuleHandleW(null);
        IntPtr cursor = HarnessNative.LoadCursorW(IntPtr.Zero, HarnessNative.IDC_ARROW);

        // The Adobe names are deliberate: the adapter under test matches on them.
        foreach (string name in new[] { FrameClass, InnerClass, PopupClass, AuditTarget.TargetClass,
                                        "OWL.Dock", "OWL.TabPane", "OWL.TabGroup", "OWL.Document" })
        {
            var wc = new HarnessNative.WNDCLASS
            {
                style = 0x0003,                     // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = _proc,
                hInstance = inst,
                hCursor = cursor,
                hbrBackground = IntPtr.Zero,        // WM_PAINT does all of it
                lpszClassName = name,
            };
            HarnessNative.RegisterClassW(ref wc);
        }
    }

    private static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == HarnessNative.WM_ERASEBKGND) return new IntPtr(1);
        if (msg == HarnessNative.WM_PAINT)
        {
            HarnessNative.PAINTSTRUCT ps;
            IntPtr dc = HarnessNative.BeginPaint(h, out ps);
            Native.RECT c; Native.GetClientRect(h, out c);
            bool wedge;
            if (_wedge.TryGetValue(h, out wedge) && wedge) PaintWedge(dc, c);
            else
            {
                int col;
                if (!_fill.TryGetValue(h, out col)) col = HarnessNative.Bgr(40, 40, 40);
                IntPtr br = HarnessNative.CreateSolidBrush(col);
                HarnessNative.FillRect(dc, ref c, br);
                HarnessNative.DeleteObject(br);
            }
            HarnessNative.EndPaint(h, ref ps);
            return IntPtr.Zero;
        }
        return HarnessNative.DefWindowProcW(h, msg, wp, lp);
    }

    /// <summary>
    /// Horizontal bands of exact 8-bit grey, so one capture of the canvas contains
    /// every input level we want to measure and the output for each is the mean of
    /// a large uniform region rather than a single pixel.
    /// </summary>
    private static void PaintWedge(IntPtr dc, Native.RECT c)
    {
        int n = Levels.Length;
        for (int i = 0; i < n; i++)
        {
            var band = new Native.RECT
            {
                Left = 0,
                Top = c.H * i / n,
                Right = c.W,
                Bottom = c.H * (i + 1) / n,
            };
            IntPtr br = HarnessNative.CreateSolidBrush(
                HarnessNative.Bgr(Levels[i], Levels[i], Levels[i]));
            HarnessNative.FillRect(dc, ref band, br);
            HarnessNative.DeleteObject(br);
        }
    }

    // ------------------------------------------------------------- construction

    public SyntheticApp(string title, Rectangle rect, int viewports)
    {
        EnsureClasses();
        IntPtr inst = HarnessNative.GetModuleHandleW(null);
        Frame = HarnessNative.CreateWindowExW(0, FrameClass, title,
            HarnessNative.WS_OVERLAPPEDWINDOW | HarnessNative.WS_CLIPCHILDREN |
            HarnessNative.WS_CLIPSIBLINGS,
            rect.X, rect.Y, rect.Width, rect.Height, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        _fill[Frame] = HarnessNative.Bgr(32, 32, 32);
        HarnessNative.ShowWindow(Frame, HarnessNative.SW_SHOW);
        SetViewportCount(viewports);
    }

    /// <summary>Open or close documents. This is the tabbed/tiled case, mechanically.</summary>
    public void SetViewportCount(int n)
    {
        foreach (IntPtr h in _all) HarnessNative.DestroyWindow(h);
        _all.Clear(); Documents.Clear();
        _viewports = Math.Max(0, n);
        Layout();
    }

    public int ViewportCount { get { return _viewports; } }

    private void Layout()
    {
        Native.RECT client = Native.ClientRectOnScreen(Frame);
        int w = client.W, h = client.H;
        if (_viewports <= 0 || w <= 0 || h <= 0) return;

        // Leave a strip of application chrome on all four sides, so a viewport is
        // never the whole client area -- an overlay that covered the chrome would
        // otherwise pass a test it should fail.
        const int chrome = 40;
        int usableW = w - chrome * 2, usableH = h - chrome * 2;
        if (usableW <= 40 || usableH <= 40) return;

        bool rebuild = Documents.Count != _viewports;
        int colW = usableW / _viewports;

        for (int i = 0; i < _viewports; i++)
        {
            int x = chrome + i * colW, y = chrome;
            int cw = colW - 8, ch = usableH;

            if (rebuild)
            {
                IntPtr inst = HarnessNative.GetModuleHandleW(null);
                uint child = HarnessNative.WS_CHILD | HarnessNative.WS_VISIBLE |
                             HarnessNative.WS_CLIPCHILDREN | HarnessNative.WS_CLIPSIBLINGS;

                IntPtr dock = HarnessNative.CreateWindowExW(0, "OWL.Dock", "", child,
                    x, y, cw, ch, Frame, IntPtr.Zero, inst, IntPtr.Zero);
                IntPtr pane = HarnessNative.CreateWindowExW(0, "OWL.TabPane", "", child,
                    0, 0, cw, ch, dock, IntPtr.Zero, inst, IntPtr.Zero);
                IntPtr group = HarnessNative.CreateWindowExW(0, "OWL.TabGroup", "", child,
                    0, 0, cw, ch, pane, IntPtr.Zero, inst, IntPtr.Zero);
                // The document is inset inside the tab group, exactly as the tab
                // strip insets it in a real Adobe window.
                IntPtr doc = HarnessNative.CreateWindowExW(0, "OWL.Document", "", child,
                    1, 24, cw - 2, ch - 25, group, IntPtr.Zero, inst, IntPtr.Zero);
                // ... and the canvas is inset inside the document by the width of a
                // scrollbar, which is what makes region=canvas differ from
                // region=document at all.
                IntPtr inner = HarnessNative.CreateWindowExW(0, InnerClass, "", child,
                    0, 0, cw - 2 - 16, ch - 25 - 16, doc, IntPtr.Zero, inst, IntPtr.Zero);

                _fill[dock] = _fill[pane] = _fill[group] = HarnessNative.Bgr(64, 64, 64);
                _fill[doc] = HarnessNative.Bgr(96, 96, 96);
                _wedge[inner] = true;

                _all.Add(dock); _all.Add(pane); _all.Add(group); _all.Add(doc); _all.Add(inner);
                Documents.Add(doc);
            }
            else
            {
                IntPtr doc = Documents[i];
                IntPtr group = Native.GetParent(doc);
                IntPtr pane = Native.GetParent(group);
                IntPtr dock = Native.GetParent(pane);
                HarnessNative.MoveWindow(dock, x, y, cw, ch, true);
                HarnessNative.MoveWindow(pane, 0, 0, cw, ch, true);
                HarnessNative.MoveWindow(group, 0, 0, cw, ch, true);
                HarnessNative.MoveWindow(doc, 1, 24, cw - 2, ch - 25, true);
                IntPtr inner = Native.GetWindow(doc, 5 /*GW_CHILD*/);
                if (inner != IntPtr.Zero)
                    HarnessNative.MoveWindow(inner, 0, 0, cw - 2 - 16, ch - 25 - 16, true);
            }
        }
    }

    // ------------------------------------------------------------------ actions

    public void SetBounds(Rectangle r)
    {
        HarnessNative.MoveWindow(Frame, r.X, r.Y, r.Width, r.Height, true);
        Layout();
    }

    public Native.RECT ClientRect { get { return Native.ClientRectOnScreen(Frame); } }

    /// <summary>Screen rect of one viewport's canvas, i.e. what region=canvas should give.</summary>
    public Native.RECT CanvasRect(int i)
    {
        if (i < 0 || i >= Documents.Count) return new Native.RECT();
        IntPtr inner = Native.GetWindow(Documents[i], 5 /*GW_CHILD*/);
        return Native.RectOf(inner == IntPtr.Zero ? Documents[i] : inner);
    }

    public Native.RECT DocumentRect(int i)
    {
        if (i < 0 || i >= Documents.Count) return new Native.RECT();
        return Native.RectOf(Documents[i]);
    }

    /// <summary>
    /// An owned top-level window over the frame, shown WITHOUT activation. This is
    /// the exact shape of an Adobe floating panel or modeless dialog, and showing
    /// one is what used to bury the overlay permanently.
    /// </summary>
    public IntPtr ShowOwnedPopup(Rectangle r)
    {
        IntPtr inst = HarnessNative.GetModuleHandleW(null);
        IntPtr p = HarnessNative.CreateWindowExW(0, PopupClass, "Harness dialog",
            HarnessNative.WS_POPUP | HarnessNative.WS_BORDER,
            r.X, r.Y, r.Width, r.Height, Frame, IntPtr.Zero, inst, IntPtr.Zero);
        _fill[p] = HarnessNative.Bgr(200, 60, 60);
        HarnessNative.ShowWindow(p, HarnessNative.SW_SHOWNA);
        _popups.Add(p);
        return p;
    }

    public void ClosePopups()
    {
        foreach (IntPtr p in _popups) HarnessNative.DestroyWindow(p);
        _popups.Clear();
    }

    /// <summary>
    /// Raise the frame WITHOUT bringing its owned windows along. This is the state
    /// Windows leaves behind when another owned window is shown, and it is the
    /// deterministic way to reproduce the overlay being buried.
    /// </summary>
    public void RaiseWithoutOwned()
    {
        Native.SetWindowPos(Frame, Native.HWND_TOP, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE |
            Native.SWP_NOOWNERZORDER);
    }

    public void Minimise() { HarnessNative.ShowWindow(Frame, HarnessNative.SW_MINIMIZE); }
    public void Restore() { HarnessNative.ShowWindow(Frame, HarnessNative.SW_RESTORE); Layout(); }
    public void HideFrame() { HarnessNative.ShowWindow(Frame, HarnessNative.SW_HIDE); }
    public void ShowFrame() { HarnessNative.ShowWindow(Frame, HarnessNative.SW_SHOW); Layout(); }
    public void Repaint() { HarnessNative.InvalidateRect(Frame, IntPtr.Zero, true); }

    public void Dispose()
    {
        ClosePopups();
        if (Frame != IntPtr.Zero) { HarnessNative.DestroyWindow(Frame); Frame = IntPtr.Zero; }
        _all.Clear(); Documents.Clear();
    }

    /// <summary>
    /// An owned, unactivated top-level window over any owner. Standalone version of
    /// ShowOwnedPopup, so the single-target phase can reproduce the burial the same
    /// way the multi-target phase does.
    /// </summary>
    internal static IntPtr CreateOwnedPopup(IntPtr owner, Rectangle r)
    {
        EnsureClasses();
        IntPtr p = HarnessNative.CreateWindowExW(0, PopupClass, "Harness dialog",
            HarnessNative.WS_POPUP | HarnessNative.WS_BORDER,
            r.X, r.Y, r.Width, r.Height, owner, IntPtr.Zero,
            HarnessNative.GetModuleHandleW(null), IntPtr.Zero);
        _fill[p] = HarnessNative.Bgr(200, 60, 60);
        HarnessNative.ShowWindow(p, HarnessNative.SW_SHOWNA);
        return p;
    }

    /// <summary>A bare top-level window of a dedicated class, painting the wedge.</summary>
    internal static IntPtr CreateWedgeWindow(string cls, string title, Rectangle r)
    {
        EnsureClasses();
        IntPtr h = HarnessNative.CreateWindowExW(0, cls, title,
            HarnessNative.WS_OVERLAPPEDWINDOW | HarnessNative.WS_CLIPSIBLINGS,
            r.X, r.Y, r.Width, r.Height, IntPtr.Zero, IntPtr.Zero,
            HarnessNative.GetModuleHandleW(null), IntPtr.Zero);
        _wedge[h] = true;
        HarnessNative.ShowWindow(h, HarnessNative.SW_SHOW);
        return h;
    }
}

/// <summary>
/// The single-window target the first four audit phases drive.
///
/// It is a raw window rather than a WinForms Form for one reason: it needs a
/// window class of its own. The harness aims the shipping engine at a target by
/// class name, and every WinForms window in this process shares one class -- so
/// with a Form as the target, the audit's own test DIALOGS matched the adapter
/// too, were tracked as extra applications, and the overlay moved onto them.
/// Two ownership assertions failed for that reason and neither failure was
/// about the product.
/// </summary>
internal sealed class AuditTarget : System.Windows.Forms.IWin32Window, IDisposable
{
    public const string TargetClass = "NVAuditTarget";

    public IntPtr Handle { get; private set; }

    public AuditTarget(string title, Rectangle r)
    {
        Handle = SyntheticApp.CreateWedgeWindow(TargetClass, title, r);
    }

    public Rectangle Bounds
    {
        get { var w = Native.RectOf(Handle); return new Rectangle(w.Left, w.Top, w.W, w.H); }
        set { HarnessNative.MoveWindow(Handle, value.X, value.Y, value.Width, value.Height, true); }
    }

    public void Show() { HarnessNative.ShowWindow(Handle, HarnessNative.SW_SHOW); }
    public void Hide() { HarnessNative.ShowWindow(Handle, HarnessNative.SW_HIDE); }
    public void Minimise() { HarnessNative.ShowWindow(Handle, HarnessNative.SW_MINIMIZE); }
    public void Restore() { HarnessNative.ShowWindow(Handle, HarnessNative.SW_RESTORE); }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) { HarnessNative.DestroyWindow(Handle); Handle = IntPtr.Zero; }
    }
}
