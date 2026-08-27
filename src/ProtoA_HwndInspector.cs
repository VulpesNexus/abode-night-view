// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - Prototype A : Adobe HWND inspector
// ----------------------------------------------------------------------------
//  Purpose
//      Answer one question: is the InDesign document canvas a distinct,
//      stable child HWND whose rectangle we can track?
//
//      This tool is 100% read-only with respect to InDesign. It only calls
//      window-enumeration and window-query APIs. It never sends a message,
//      never posts input, never scripts InDesign, and never touches a document.
//
//  Modes
//      ProtoA.exe                 dump the full hierarchy + canvas analysis
//      ProtoA.exe hover           live: print the HWND under the mouse cursor
//      ProtoA.exe probe           grid-probe the client area with WindowFromPoint
//      ProtoA.exe watch           poll the canvas rect and print when it changes
//      ProtoA.exe all             dump + probe (default for a capture session)
//
//  Optional: add  --proc=NAME  to target something other than InDesign.
//
//  Output goes to the console AND to NightView-ProtoA-<timestamp>.txt
//  next to the executable.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class ProtoA
{
    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom;
        public int W { get { return Right - Left; } }
        public int H { get { return Bottom - Top; } }
        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture,
                "{0,6},{1,6} {2,5}x{3,-5}", Left, Top, W, H); } }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX mi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const uint GA_ROOT = 2;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    // ------------------------------------------------------------------ output

    private static TextWriter _log;
    private static void P(string fmt, params object[] a)
    {
        string s = a.Length == 0 ? fmt : string.Format(CultureInfo.InvariantCulture, fmt, a);
        Console.WriteLine(s);
        if (_log != null) _log.WriteLine(s);
    }

    // ------------------------------------------------------------- win helpers

    private static string ClassOf(IntPtr h)
    { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }

    private static string TextOf(IntPtr h)
    { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }

    private static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

    private static long Style(IntPtr h) { return GetWindowLongPtrW(h, GWL_STYLE).ToInt64(); }
    private static long ExStyle(IntPtr h) { return GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64(); }

    private static string Hx(IntPtr h) { return "0x" + h.ToInt64().ToString("X"); }

    private static string DecodeStyle(long s)
    {
        var f = new List<string>();
        if ((s & 0x10000000L) != 0) f.Add("VISIBLE");
        if ((s & 0x40000000L) != 0) f.Add("CHILD");
        if ((s & 0x80000000L) != 0) f.Add("POPUP");
        if ((s & 0x08000000L) != 0) f.Add("DISABLED");
        if ((s & 0x02000000L) != 0) f.Add("CLIPCHILDREN");
        if ((s & 0x04000000L) != 0) f.Add("CLIPSIBLINGS");
        if ((s & 0x00100000L) != 0) f.Add("VSCROLL");
        if ((s & 0x00200000L) != 0) f.Add("HSCROLL");
        if ((s & 0x01000000L) != 0) f.Add("MAXIMIZE");
        if ((s & 0x20000000L) != 0) f.Add("MINIMIZE");
        return string.Join("|", f.ToArray());
    }

    private static string DecodeExStyle(long s)
    {
        var f = new List<string>();
        if ((s & 0x00000008L) != 0) f.Add("TOPMOST");
        if ((s & 0x00000020L) != 0) f.Add("TRANSPARENT");
        if ((s & 0x00000040L) != 0) f.Add("MDICHILD");
        if ((s & 0x00000080L) != 0) f.Add("TOOLWINDOW");
        if ((s & 0x00080000L) != 0) f.Add("LAYERED");
        if ((s & 0x08000000L) != 0) f.Add("NOACTIVATE");
        if ((s & 0x00200000L) != 0) f.Add("NOREDIRECTIONBITMAP");
        if ((s & 0x02000000L) != 0) f.Add("COMPOSITED");
        if ((s & 0x00010000L) != 0) f.Add("CONTROLPARENT");
        if ((s & 0x00040000L) != 0) f.Add("APPWINDOW");
        return string.Join("|", f.ToArray());
    }

    /// <summary>Screen rect of a window's CLIENT area (excludes non-client chrome).</summary>
    private static RECT ClientRectOnScreen(IntPtr h)
    {
        RECT c; GetClientRect(h, out c);
        POINT o = new POINT(0, 0); ClientToScreen(h, ref o);
        return new RECT { Left = o.X, Top = o.Y, Right = o.X + c.W, Bottom = o.Y + c.H };
    }

    private static List<IntPtr> ChildrenOf(IntPtr parent)
    {
        var all = new List<IntPtr>();
        EnumChildWindows(parent, (h, p) => { all.Add(h); return true; }, IntPtr.Zero);
        return all;
    }

    // -------------------------------------------------------------- discovery

    private sealed class Node
    {
        public IntPtr H;
        public IntPtr Parent;
        public string Class;
        public string Text;
        public RECT R;
        public bool Visible, Enabled;
        public long St, Ex;
        public int Depth;
        public List<Node> Kids = new List<Node>();
        public long Area { get { return (long)R.W * R.H; } }
    }

    private static Node Snap(IntPtr h)
    {
        return new Node {
            H = h, Parent = GetParent(h), Class = ClassOf(h), Text = TextOf(h),
            R = RectOf(h), Visible = IsWindowVisible(h), Enabled = IsWindowEnabled(h),
            St = Style(h), Ex = ExStyle(h) };
    }

    private static List<IntPtr> TopLevelWindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, p) => {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid == (uint)pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Pick the application frame window. InDesign's frame has class "indesign".
    /// Fall back to the largest visible top-level window.
    /// </summary>
    private static IntPtr FindMainWindow(int pid)
    {
        var tops = TopLevelWindowsOf(pid);
        var byClass = tops.Where(h => ClassOf(h).Equals("indesign", StringComparison.OrdinalIgnoreCase)).ToList();
        if (byClass.Count > 0)
            return byClass.OrderByDescending(h => (long)RectOf(h).W * RectOf(h).H).First();

        var vis = tops.Where(h => IsWindowVisible(h) && RectOf(h).W > 200 && RectOf(h).H > 200).ToList();
        if (vis.Count > 0)
            return vis.OrderByDescending(h => (long)RectOf(h).W * RectOf(h).H).First();

        return tops.Count > 0 ? tops[0] : IntPtr.Zero;
    }

    // ------------------------------------------------------------------- dump

    private static Node BuildTree(IntPtr root)
    {
        var flat = ChildrenOf(root);           // EnumChildWindows returns ALL descendants
        var map = new Dictionary<IntPtr, Node>();
        var rootNode = Snap(root); rootNode.Depth = 0;
        map[root] = rootNode;
        foreach (var h in flat) map[h] = Snap(h);

        foreach (var h in flat)
        {
            Node n = map[h];
            Node parent;
            if (n.Parent != IntPtr.Zero && map.TryGetValue(n.Parent, out parent)) parent.Kids.Add(n);
            else rootNode.Kids.Add(n);         // orphan: attach at root so nothing is lost
        }
        AssignDepth(rootNode, 0);
        return rootNode;
    }

    private static void AssignDepth(Node n, int d)
    { n.Depth = d; foreach (var k in n.Kids) AssignDepth(k, d + 1); }

    private static void PrintNode(Node n, bool visibleOnly)
    {
        if (visibleOnly && !n.Visible) return;
        P("{0}{1,-11} par={2,-11} {3,-30} {4} vis={5,-5} en={6,-5} st={7:X8} ex={8:X8} [{9}] [{10}] '{11}'",
            new string(' ', n.Depth * 2), Hx(n.H), Hx(n.Parent), n.Class, n.R,
            n.Visible, n.Enabled, n.St, n.Ex, DecodeStyle(n.St), DecodeExStyle(n.Ex), Trunc(n.Text, 48));
        foreach (var k in n.Kids) PrintNode(k, visibleOnly);
    }

    private static string Trunc(string s, int n)
    { if (s == null) return ""; s = s.Replace("\r", " ").Replace("\n", " "); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

    private static IEnumerable<Node> Flatten(Node n)
    { yield return n; foreach (var k in n.Kids) foreach (var g in Flatten(k)) yield return g; }

    // --------------------------------------------------------------- analysis

    private static void AnalyseCanvas(IntPtr main, Node tree)
    {
        RECT client = ClientRectOnScreen(main);
        P("");
        P("================ CANVAS CANDIDATE ANALYSIS ================");
        P("main client area (screen) : {0}", client);
        long clientArea = (long)client.W * client.H;
        if (clientArea <= 0) { P("!! main window has no client area (minimized?) - restore it and re-run"); return; }

        POINT centre = new POINT(client.Left + client.W / 2, client.Top + client.H / 2);
        P("client centre point       : {0},{1}", centre.X, centre.Y);
        P("");

        var cands =
            Flatten(tree).Where(n => n.H != main && n.Visible && n.Area > 0)
            .Select(n => new {
                N = n,
                Frac = (double)n.Area / clientArea,
                Covers = n.R.Left <= centre.X && n.R.Right >= centre.X &&
                         n.R.Top <= centre.Y && n.R.Bottom >= centre.Y,
                Scroll = (n.St & 0x00300000L) != 0
            })
            .Where(x => x.Frac >= 0.05 && x.Frac <= 1.05)
            .OrderByDescending(x => (x.Covers ? 1000 : 0) + (x.Scroll ? 500 : 0) + x.Frac * 100)
            .Take(25).ToList();

        P("{0,-11} {1,-30} {2,-24} {3,7} {4,7} {5,7}", "HWND", "CLASS", "RECT", "%CLIENT", "CENTRE", "SCROLL");
        foreach (var c in cands)
            P("{0,-11} {1,-30} {2} {3,6:P1} {4,7} {5,7}  '{6}'",
                Hx(c.N.H), c.N.Class, c.N.R, c.Frac, c.Covers ? "yes" : "-", c.Scroll ? "yes" : "-", Trunc(c.N.Text, 30));

        // InDesign-specific: the OWL widget framework labels the document view.
        var owlDoc = Flatten(tree).Where(n =>
            n.Class.Equals("OWL.Document", StringComparison.OrdinalIgnoreCase)).ToList();
        P("");
        P("--- OWL.Document instances (InDesign document view container) : {0} found", owlDoc.Count);
        foreach (var d in owlDoc)
            P("    {0}  {1}  vis={2}  parent={3} ({4})  '{5}'",
                Hx(d.H), d.R, d.Visible, Hx(d.Parent), ClassOf(d.Parent), Trunc(d.Text, 40));

        if (owlDoc.Count == 1 && owlDoc[0].Visible && owlDoc[0].Area > 0)
        {
            var d = owlDoc[0];
            RECT dc = ClientRectOnScreen(d.H);
            P("");
            P("    >>> RECOMMENDED TARGET for Prototype B/C:");
            P("        --target=hwnd:{0}      (this session only - handles are not stable across restarts)", Hx(d.H));
            P("        --target=class:OWL.Document   (resolve by class every time - preferred)");
            P("        window rect  {0}", d.R);
            P("        client rect  {0}", dc);
            P("        insets from main client: L={0} T={1} R={2} B={3}",
                d.R.Left - client.Left, d.R.Top - client.Top, client.Right - d.R.Right, client.Bottom - d.R.Bottom);
        }
    }

    // ------------------------------------------------------------------ probe

    private static void GridProbe(IntPtr main, int cols, int rows)
    {
        RECT c = ClientRectOnScreen(main);
        P("");
        P("================ WindowFromPoint GRID PROBE ({0}x{1}) ================", cols, rows);
        if (c.W <= 0 || c.H <= 0) { P("!! no client area - restore the window and re-run"); return; }
        P("NOTE: this reports what the SYSTEM would hit-test at each point, which is");
        P("      exactly what determines whether a click-through overlay behaves.");
        P("");

        var hits = new Dictionary<IntPtr, List<POINT>>();
        for (int iy = 0; iy < rows; iy++)
        for (int ix = 0; ix < cols; ix++)
        {
            var p = new POINT(
                c.Left + (int)((ix + 0.5) * c.W / cols),
                c.Top + (int)((iy + 0.5) * c.H / rows));
            IntPtr h = WindowFromPoint(p);
            if (h == IntPtr.Zero) continue;
            if (!hits.ContainsKey(h)) hits[h] = new List<POINT>();
            hits[h].Add(p);
        }

        P("{0,-11} {1,-30} {2,5}  {3,-24} {4}", "HWND", "CLASS", "HITS", "WINDOW RECT", "HIT BOUNDING BOX");
        foreach (var kv in hits.OrderByDescending(k => k.Value.Count))
        {
            var pts = kv.Value;
            int l = pts.Min(p => p.X), t = pts.Min(p => p.Y), r = pts.Max(p => p.X), b = pts.Max(p => p.Y);
            P("{0,-11} {1,-30} {2,5}  {3}  {4},{5} .. {6},{7}",
                Hx(kv.Key), ClassOf(kv.Key), pts.Count, RectOf(kv.Key), l, t, r, b);
        }

        // ASCII map: which cell belongs to the dominant window
        IntPtr dominant = hits.OrderByDescending(k => k.Value.Count).First().Key;
        P("");
        P("ASCII map ('#' = {0} {1}, '.' = something else):", Hx(dominant), ClassOf(dominant));
        for (int iy = 0; iy < rows; iy++)
        {
            var sb = new StringBuilder("    ");
            for (int ix = 0; ix < cols; ix++)
            {
                var p = new POINT(c.Left + (int)((ix + 0.5) * c.W / cols), c.Top + (int)((iy + 0.5) * c.H / rows));
                sb.Append(WindowFromPoint(p) == dominant ? '#' : '.');
            }
            P(sb.ToString());
        }
    }

    // ------------------------------------------------------------------ hover

    private static void Hover(int pid)
    {
        P("");
        P("================ LIVE HOVER PROBE ================");
        P("Move the mouse over the InDesign document canvas, then over rulers,");
        P("panels, the Control bar and the pasteboard. Press Ctrl+C to stop.");
        P("");
        IntPtr last = IntPtr.Zero;
        while (true)
        {
            POINT p; GetCursorPos(out p);
            IntPtr h = WindowFromPoint(p);
            if (h != last && h != IntPtr.Zero)
            {
                last = h;
                uint wpid; GetWindowThreadProcessId(h, out wpid);
                string chain = "";
                for (IntPtr a = h; a != IntPtr.Zero; a = GetParent(a))
                    chain = ClassOf(a) + (chain.Length > 0 ? " > " + chain : "");
                P("{0,4},{1,-4} {2,-11} {3,-28} {4} pid={5}{6}",
                    p.X, p.Y, Hx(h), ClassOf(h), RectOf(h), wpid,
                    wpid == (uint)pid ? "" : "  <-- NOT InDesign");
                P("            chain: {0}", chain);
            }
            System.Threading.Thread.Sleep(60);
        }
    }

    // ------------------------------------------------------------------ watch

    private static void Watch(int pid)
    {
        P("");
        P("================ CANVAS RECT WATCH ================");
        P("Now: move the InDesign window, resize it, maximize/restore it, collapse");
        P("and expand panels, switch document tabs, open/close documents, drag it to");
        P("another monitor. Every change to the canvas rect prints a line.");
        P("Press Ctrl+C to stop.");
        P("");
        IntPtr main = FindMainWindow(pid);
        RECT last = new RECT();
        string lastKey = "";
        while (true)
        {
            IntPtr cur = FindMainWindow(pid);
            if (cur != main) { main = cur; P("** main window handle changed -> {0}", Hx(main)); }
            if (main == IntPtr.Zero) { System.Threading.Thread.Sleep(500); continue; }

            IntPtr canvas = ChildrenOf(main).FirstOrDefault(h =>
                ClassOf(h).Equals("OWL.Document", StringComparison.OrdinalIgnoreCase) && IsWindowVisible(h));
            RECT r = canvas != IntPtr.Zero ? RectOf(canvas) : ClientRectOnScreen(main);
            string key = string.Format("{0}|{1}|{2}|{3}|{4}", Hx(canvas), r.Left, r.Top, r.W, r.H);
            if (key != lastKey)
            {
                lastKey = key; last = r;
                P("{0:HH:mm:ss.fff} canvas={1,-11} {2} dpi={3} min={4} max={5} fg={6}",
                    DateTime.Now, Hx(canvas), r, GetDpiForWindow(main),
                    IsIconic(main), IsZoomed(main), GetForegroundWindow() == main);
            }
            System.Threading.Thread.Sleep(100);
        }
    }

    // ------------------------------------------------------------------- main

    private static int Main(string[] argv)
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        string mode = "all";
        string procName = "InDesign";
        bool visibleOnly = false;
        foreach (var a in argv)
        {
            if (a.StartsWith("--proc=", StringComparison.OrdinalIgnoreCase)) procName = a.Substring(7);
            else if (a.Equals("--visible-only", StringComparison.OrdinalIgnoreCase)) visibleOnly = true;
            else if (!a.StartsWith("--")) mode = a.ToLowerInvariant();
        }

        string logPath = Path.Combine(
            Path.GetDirectoryName(typeof(ProtoA).Assembly.Location) ?? ".",
            "NightView-ProtoA-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        _log = new StreamWriter(logPath, false, new UTF8Encoding(true));

        try
        {
            var procs = Process.GetProcessesByName(procName);
            if (procs.Length == 0)
            {
                P("No process named '{0}' is running. Start InDesign first.", procName);
                return 1;
            }
            var proc = procs.OrderByDescending(p => p.WorkingSet64).First();

            P("Abode Night View - Prototype A   ({0})", DateTime.Now.ToString("u"));
            P("process       : {0}.exe  pid={1}", procName, proc.Id);
            try { P("executable    : {0}", proc.MainModule.FileName); } catch { }
            P("this inspector is READ-ONLY: enumeration and query calls only");
            P("");

            IntPtr main = FindMainWindow(proc.Id);
            if (main == IntPtr.Zero) { P("Could not find a main window."); return 1; }

            var tops = TopLevelWindowsOf(proc.Id);
            P("top-level windows owned by this process : {0}", tops.Count);
            P("visible top-level windows :");
            foreach (var t in tops.Where(IsWindowVisible))
                P("    {0,-11} {1,-30} {2} [{3}] '{4}'",
                    Hx(t), ClassOf(t), RectOf(t), DecodeExStyle(ExStyle(t)), Trunc(TextOf(t), 60));
            P("");

            RECT mr = RectOf(main);
            P("MAIN WINDOW   : {0}  class='{1}'", Hx(main), ClassOf(main));
            P("  title       : {0}", TextOf(main));
            P("  window rect : {0}", mr);
            P("  client rect : {0}", ClientRectOnScreen(main));
            P("  dpi         : {0}   minimized={1}  maximized={2}  foreground={3}",
                GetDpiForWindow(main), IsIconic(main), IsZoomed(main), GetForegroundWindow() == main);
            var mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfoW(MonitorFromWindow(main, 2), ref mi))
                P("  monitor     : {0}  full={1}  work={2}", mi.szDevice, mi.rcMonitor, mi.rcWork);
            P("  style       : {0:X8} [{1}]", Style(main), DecodeStyle(Style(main)));
            P("  exstyle     : {0:X8} [{1}]", ExStyle(main), DecodeExStyle(ExStyle(main)));

            if (IsIconic(main))
                P("\n  !! WINDOW IS MINIMIZED - all child rectangles are meaningless (-32000).\n" +
                  "     Restore InDesign and run this again.");

            var tree = BuildTree(main);
            var flat = Flatten(tree).ToList();
            P("");
            P("descendant windows : {0} total, {1} visible", flat.Count - 1, flat.Count(n => n.Visible) - 1);
            P("class histogram :");
            foreach (var g in flat.Skip(1).GroupBy(n => n.Class).OrderByDescending(g => g.Count()))
                P("    {0,5}  {1}  ({2} visible)", g.Count(), g.Key, g.Count(n => n.Visible));

            if (mode == "dump" || mode == "all")
            {
                P("");
                P("================ FULL CHILD HIERARCHY{0} ================",
                    visibleOnly ? " (visible only)" : "");
                PrintNode(tree, visibleOnly);
            }

            AnalyseCanvas(main, tree);

            if (mode == "probe" || mode == "all") GridProbe(main, 60, 24);
            if (mode == "hover") Hover(proc.Id);
            if (mode == "watch") Watch(proc.Id);

            P("");
            P("saved to: {0}", logPath);
            return 0;
        }
        finally { if (_log != null) { _log.Flush(); _log.Dispose(); } }
    }
}
