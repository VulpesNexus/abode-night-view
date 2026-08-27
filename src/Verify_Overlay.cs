// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - Verify: does the overlay actually change pixels, and only the
//  right pixels?
// ----------------------------------------------------------------------------
//  This exists because the first version of the checker verified everything
//  EXCEPT the screen. Handle, rectangle, extended styles and hit-testing all
//  passed while the overlay was painting nothing at all. Structural checks
//  cannot see an invisible window; only photometry can.
//
//  It is a compiled exe rather than a PowerShell script because inline
//  Add-Type P/Invoke gets blocked by AMSI on this machine.
//
//  Read-only with respect to the target application: window geometry queries
//  and a BitBlt from the screen DC. Nothing is sent to it and nothing is scripted.
//
//  Usage (identical as AbodeNightView.exe --verify / --watch / --baseline):
//    Verify.exe                  full report
//    Verify.exe --save=out.png   also write the captured canvas
//    Verify.exe --watch=25       log and time every overlay transition
//    Verify.exe --watch=25 --csv=w.csv    also dump the raw intervals
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

static class Verify
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h,
                                                       IntPtr s, int sx, int sy, uint rop);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    const int SW_RESTORE = 9;
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(
        IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    const int GWL_EXSTYLE = -20;
    const uint GW_OWNER = 4;

    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what +
                          (detail == null ? "" : "  " + detail));
        if (ok) _pass++; else _fail++;
    }

    // ---------------------------------------------------------------- capture

    static Bitmap Grab(Native.RECT r)
    {
        var bmp = new Bitmap(r.W, r.H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr src = GetDC(IntPtr.Zero), dst = g.GetHdc();
            BitBlt(dst, 0, 0, r.W, r.H, src, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
            g.ReleaseHdc(dst); ReleaseDC(IntPtr.Zero, src);
        }
        return bmp;
    }

    static double MeanLuma(Bitmap b, Rectangle area)
    {
        double sum = 0; long n = 0;
        var data = b.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < area.Height; y += 2)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < area.Width; x += 2)
                {
                    sum += 0.2126 * row[x * 4 + 2] + 0.7152 * row[x * 4 + 1] + 0.0722 * row[x * 4];
                    n++;
                }
            }
        }
        b.UnlockBits(data);
        return n == 0 ? -1 : sum / n;
    }

    // ------------------------------------------------------------------ watch

    private sealed class Gap
    {
        public double StartS, Ms;
        public string Cause;
    }

    /// <summary>
    /// Sample the overlay continuously and time every interval during which the
    /// canvas was NOT dimmed. "Not dimmed" is the union of the three ways that can
    /// happen: the overlay is hidden, it has fallen below InDesign in the z-order,
    /// or its rectangle no longer matches the canvas.
    ///
    /// Counting transitions alone is not enough. A transition that lasts one
    /// compositor frame is invisible at any refresh rate; one that lasts 200 ms is
    /// the flashbang. So every interval is timed, and the distribution is reported
    /// against the frame budget of several refresh rates rather than just 30 Hz --
    /// a 12 ms gap is under one frame at 60 Hz and nearly two at 144 Hz.
    /// </summary>

    /// <summary>
    /// The overlay covering a given rectangle, or zero.
    ///
    /// There can be several now -- one per tracked Adobe viewport -- so taking the
    /// first window with the right title is no longer good enough: it might belong
    /// to Illustrator while the measurement is about InDesign, and the resulting
    /// numbers would be meaningless without ever looking wrong. Exact match first,
    /// then largest overlap.
    /// </summary>
    static IntPtr OverlayOver(Native.RECT want)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        Native.EnumWindows((h, lp) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.TitleOf(h) != "AbodeNV Overlay") return true;
            Native.RECT r = Native.RectOf(h);
            if (r.Same(want)) { best = h; bestArea = long.MaxValue; return false; }
            long w = Math.Min(r.Right, want.Right) - Math.Max(r.Left, want.Left);
            long hh = Math.Min(r.Bottom, want.Bottom) - Math.Max(r.Top, want.Top);
            if (w <= 0 || hh <= 0) return true;
            long a = w * hh;
            if (a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static int CountOverlays()
    {
        int n = 0;
        Native.EnumWindows((h, lp) =>
        {
            if (Native.IsWindowVisible(h) && Native.TitleOf(h) == "AbodeNV Overlay") n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    /// <summary>Which application this run is about, for messages. Set by --product/--proc.</summary>
    static string AppName = "InDesign";

    static int Watch(int seconds, ViewportLocator loc, string csvPath)
    {
        Console.WriteLine("Watching the overlay for " + seconds + "s.");
        Console.WriteLine("Click, drag frames, edit text, switch panels. Every change prints.");
        Console.WriteLine();

        IntPtr ov = IntPtr.Zero;
        bool haveState = false, wasVis = false, wasAbove = false, wasMatch = false;
        Native.RECT wasRect = new Native.RECT();
        int events = 0;
        var gaps = new List<Gap>();
        Gap open = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (ov == IntPtr.Zero || !Native.IsWindow(ov))
                ov = OverlayOver(loc.Resolve());
            if (ov == IntPtr.Zero) { System.Threading.Thread.Sleep(50); continue; }

            bool vis = Native.IsWindowVisible(ov);
            Native.RECT r = Native.RectOf(ov);
            bool above = IsAbove(ov, loc.MainWindow);
            Native.RECT canvas = loc.Resolve();
            bool match = !canvas.IsEmpty && r.Same(canvas);
            double now = sw.Elapsed.TotalSeconds;

            if (haveState && (vis != wasVis || !r.Same(wasRect) || above != wasAbove || match != wasMatch))
            {
                events++;
                var what = new System.Text.StringBuilder();
                if (vis != wasVis) what.Append(vis ? "SHOWN " : "HIDDEN ");
                if (!r.Same(wasRect)) what.Append("MOVED->" + r + " ");
                if (above != wasAbove) what.Append(above ? "RAISED " : "FELL-BEHIND ");
                if (match != wasMatch) what.Append(match ? "REMATCHED " : "RECT!=CANVAS ");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,7:0.000}s  {1}", now, what));
            }

            bool dimmed = vis && above && match;
            if (haveState)
            {
                if (!dimmed && open == null)
                    open = new Gap { StartS = now, Cause = Cause(vis, above, match) };
                else if (dimmed && open != null)
                {
                    open.Ms = (now - open.StartS) * 1000;
                    gaps.Add(open);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,7:0.000}s  ^ canvas was undimmed for {1:0.0} ms ({2}){3}",
                        now, open.Ms, open.Cause, open.Ms > 33.3 ? "   <-- VISIBLE FLASH" : ""));
                    open = null;
                }
                else if (!dimmed && open != null)
                {
                    string c = Cause(vis, above, match);
                    if (!open.Cause.Contains(c)) open.Cause += "+" + c;
                }
            }

            wasVis = vis; wasRect = r; wasAbove = above; wasMatch = match; haveState = true;
            System.Threading.Thread.Sleep(8);
        }

        if (open != null)
        {
            open.Ms = (sw.Elapsed.TotalSeconds - open.StartS) * 1000;
            open.Cause += " (still open at end of run)";
            gaps.Add(open);
        }

        Console.WriteLine();
        Console.WriteLine("  " + events + " transitions, " + gaps.Count + " undimmed intervals.");

        if (gaps.Count == 0)
        {
            Console.WriteLine("  The canvas was continuously dimmed for the whole run.");
        }
        else
        {
            var ms = new List<double>();
            foreach (var g in gaps) ms.Add(g.Ms);
            ms.Sort();
            Console.WriteLine();
            Console.WriteLine("  Undimmed interval duration (ms)");
            Console.WriteLine("    count   " + ms.Count);
            Console.WriteLine("    median  " + F(Pct(ms, 50)));
            Console.WriteLine("    p95     " + F(Pct(ms, 95)));
            Console.WriteLine("    p99     " + F(Pct(ms, 99)));
            Console.WriteLine("    max     " + F(ms[ms.Count - 1]));
            Console.WriteLine();
            Console.WriteLine("  How many exceed one frame at each refresh rate");
            foreach (int hz in new[] { 30, 60, 120, 144 })
            {
                double budget = 1000.0 / hz;
                int over = 0;
                foreach (double v in ms) if (v > budget) over++;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    {0,3} Hz ({1,5:0.0} ms)   {2} of {3}", hz, budget, over, ms.Count));
            }
            Console.WriteLine();
            Console.WriteLine("  By cause");
            var byCause = new Dictionary<string, int>();
            foreach (var g in gaps)
            { int n; byCause.TryGetValue(g.Cause, out n); byCause[g.Cause] = n + 1; }
            foreach (var kv in byCause) Console.WriteLine("    " + kv.Value + " x  " + kv.Key);

            Console.WriteLine();
            Console.WriteLine("  Raw durations, sorted (ms):");
            var line = new System.Text.StringBuilder("   ");
            foreach (double v in ms)
            {
                line.Append(" " + F(v));
                if (line.Length > 68) { Console.WriteLine(line); line = new System.Text.StringBuilder("   "); }
            }
            if (line.Length > 3) Console.WriteLine(line);
        }

        if (csvPath != null)
        {
            try
            {
                var sb = new System.Text.StringBuilder("start_s,duration_ms,cause\n");
                foreach (var g in gaps)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.000},{1:0.0},{2}\n",
                        g.StartS, g.Ms, g.Cause.Replace(',', ';'));
                System.IO.File.WriteAllText(csvPath, sb.ToString());
                Console.WriteLine();
                Console.WriteLine("  Raw intervals written to " + csvPath);
            }
            catch (Exception e) { Console.WriteLine("  Could not write " + csvPath + ": " + e.Message); }
        }
        return 0;
    }

    static string Cause(bool vis, bool above, bool match)
    {
        if (!vis) return "hidden";
        if (!above) return "below the frame";
        if (!match) return "rect != canvas";
        return "?";
    }

    static string F(double v)
    { return v.ToString("0.0", CultureInfo.InvariantCulture); }

    /// <summary>Nearest-rank percentile of an already-sorted list.</summary>
    static double Pct(List<double> sorted, int p)
    {
        if (sorted.Count == 0) return 0;
        int i = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Count - 1, i))];
    }

    // ------------------------------------------------------------------- main

    public static int Run(string[] argv)
    {
        try { Native.ApplyBestDpiAwareness(); } catch { }
        string save = null, csv = null;
        foreach (var a in argv)
        {
            if (a.StartsWith("--save=")) save = a.Substring(7);
            if (a.StartsWith("--csv=")) csv = a.Substring(6);
        }

        // --shot: capture an arbitrary screen rectangle. Used by the audit harness
        // to photograph a controllable test window with the overlay off and on, so
        // Transfer.exe can recover the per-level curve without needing InDesign to
        // be unobstructed at the exact moment of the measurement.
        foreach (var a in argv)
            if (a.StartsWith("--shot="))
            {
                var p = a.Substring(7).Split(',');
                if (p.Length != 4 || save == null)
                { Console.WriteLine("usage: --shot=x,y,w,h --save=file.png"); return 2; }
                var sr = new Native.RECT
                {
                    Left = int.Parse(p[0], CultureInfo.InvariantCulture),
                    Top = int.Parse(p[1], CultureInfo.InvariantCulture),
                };
                sr.Right = sr.Left + int.Parse(p[2], CultureInfo.InvariantCulture);
                sr.Bottom = sr.Top + int.Parse(p[3], CultureInfo.InvariantCulture);
                double lum = Sample(sr, save);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "captured {0} -> {1} – mean luma {2:0.00}", sr, save, lum));
                return 0;
            }

        Console.WriteLine("Abode Night View – overlay verification");
        Console.WriteLine("=======================================");

        // 1 -- the target application and its canvas
        var loc = new ViewportLocator();
        loc.TargetSpec = "canvas";

        // --product=<id> points the verifier at any adapter in the registry, so a
        // newly supported Adobe application is regression-tested by the same tool
        // and the same assertions as InDesign, not by a second one written later.
        foreach (var a in argv)
            if (a.StartsWith("--product="))
            {
                AdobeTarget t = TargetRegistry.ById(a.Substring(10));
                if (t == null)
                {
                    Console.WriteLine("  Unknown product " + a.Substring(10) + ".");
                    return 2;
                }
                loc.ProcessName = t.ProcessNames[0];
                loc.FrameClass = t.FrameClasses[0];
                loc.Adapter = t;
                loc.AdapterRegion = t.DefaultRegion;
                loc.TargetSpec = t.DefaultRegion;
                AppName = t.Family;
            }

        foreach (var a in argv)
            if (a.StartsWith("--target=") || a.StartsWith("--region="))
            {
                string reg = a.Substring(a.IndexOf((char)61) + 1);
                loc.TargetSpec = reg == Region.Document ? "owldoc" : reg;
                loc.AdapterRegion = Region.Normalize(reg) ?? Region.Canvas;
            }
        foreach (var a in argv)
        {
            if (a.StartsWith("--proc=")) { loc.ProcessName = a.Substring(7); AppName = a.Substring(7); }
            if (a.StartsWith("--frameclass=")) loc.FrameClass = a.Substring(13);
        }
        if (!loc.FindMain()) { Console.WriteLine("  " + AppName + " is not running."); return 2; }

        // --focus brings InDesign forward so the capture is of InDesign rather than
        // of whatever shell you launched this from. It is a test-tool convenience
        // and is opt-in: activating another application's window is a visible thing
        // to do to someone's desktop, and the shipping overlay never does it.
        // SetForegroundWindow only, nothing is sent to InDesign.
        if (Array.IndexOf(argv, "--focus") >= 0)
        {
            // A minimised frame reports every child at -32000 and resolves to
            // nothing, so "bring it forward" has to include un-minimising it.
            if (Native.IsIconic(loc.MainWindow))
            {
                Console.WriteLine("  " + AppName + " is minimised; restoring it.");
                ShowWindow(loc.MainWindow, SW_RESTORE);
                System.Threading.Thread.Sleep(800);
            }
            bool okFocus = SetForegroundWindow(loc.MainWindow);
            System.Threading.Thread.Sleep(600);
            loc.InvalidateTargetCache();
            Console.WriteLine("  focus requested: " + (okFocus ? "granted" : "REFUSED by Windows") +
                              ", foreground is now 0x" +
                              Native.GetForegroundWindow().ToInt64().ToString("X8"));
        }

        // --delay lets you start the command and then click into InDesign, which is
        // the only way to get an unobstructed capture when the shell you launched
        // from is sitting on top of the canvas.
        foreach (var a in argv)
            if (a.StartsWith("--delay="))
            {
                int ds;
                if (int.TryParse(a.Substring(8), out ds) && ds > 0)
                {
                    Console.WriteLine("  Waiting " + ds + "s – click into " + AppName + " now.");
                    for (int i = ds; i > 0; i--)
                    { Console.Write("\r  " + i + "  "); System.Threading.Thread.Sleep(1000); }
                    Console.WriteLine("\r        ");
                    loc.InvalidateTargetCache();
                }
            }

        Native.RECT canvas = loc.Resolve();
        Check(!canvas.IsEmpty, "canvas rect resolved", canvas.ToString());
        if (canvas.IsEmpty) return 2;

        foreach (var a in argv)
            if (a.StartsWith("--watch"))
            {
                int secs = 20;
                int eq = a.IndexOf('=');
                if (eq > 0) int.TryParse(a.Substring(eq + 1), out secs);
                return Watch(secs, loc, csv);
            }

        // --baseline is taken with Abode Night View OFF, so it must not require the
        // overlay to exist. Sample and leave.
        string statePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(Verify).Assembly.Location) ?? ".",
            "AbodeNightView-verify.txt");
        foreach (var a in argv) if (a.StartsWith("--state=")) statePath = a.Substring(8);
        if (Array.IndexOf(argv, "--baseline") >= 0)
        {
            // A baseline is the UNDIMMED reference. Taking it with Abode Night View
            // switched on makes every ratio afterwards come out at 1.000 and the
            // photometry check fail for a reason that has nothing to do with the
            // overlay. Refuse: this is the same "do not fabricate a result" rule as
            // the obstruction and stale-baseline guards below.
            IntPtr already = OverlayOver(canvas);
            if (already != IntPtr.Zero && Native.IsWindowVisible(already))
            {
                Native.RECT ar = Native.RectOf(already);
                bool overCanvas = ar.Left < canvas.Right && ar.Right > canvas.Left &&
                                  ar.Top < canvas.Bottom && ar.Bottom > canvas.Top;
                if (overCanvas)
                {
                    Console.WriteLine("  [ABORT] an Abode Night View overlay is visible over the canvas " +
                                      "(" + ar + ").");
                    Console.WriteLine("          A baseline must be the UNDIMMED reference, or every");
                    Console.WriteLine("          ratio afterwards comes out at 1.000.");
                    Console.WriteLine("          Switch Abode Night View off first, then re-run --baseline.");
                    return 2;
                }
            }

            // A baseline captured through whatever window happens to be covering
            // the canvas is worse than no baseline: it becomes the denominator of
            // every ratio printed afterwards. Refuse rather than record it.
            var c = new Native.POINT();
            c.X = canvas.Left + canvas.W / 2; c.Y = canvas.Top + canvas.H / 2;
            IntPtr onTop = WindowFromPoint(c);
            uint topPid; GetWindowThreadProcessId(onTop, out topPid);
            if (topPid != loc.Pid)
            {
                Console.WriteLine("  [ABORT] " + Native.ClassOf(onTop) + " (pid " + topPid +
                                  ") is covering the canvas.");
                Console.WriteLine("          A baseline taken through it would be meaningless.");
                Console.WriteLine("          Bring " + AppName + " forward, or re-run with --delay=5");
                Console.WriteLine("          and click into " + AppName + " while it counts down.");
                return 2;
            }

            double c0 = Sample(canvas, save);
            double h0 = Sample(ChromeStrip(loc, canvas), null);
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "canvas={0}\nchrome={1}\nrect={2}\n", c0, h0, canvas);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "         canvas {0:0.00}   chrome {1:0.00}", c0, h0));
            foreach (var st in ExcludedStrips(loc, canvas))
            {
                double v = Sample(st.Rect, null);
                sb.AppendFormat(CultureInfo.InvariantCulture, "strip.{0}={1}\n", st.Name, v);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "         excluded strip {0,-6} {1}   {2:0.00}", st.Name, st.Rect, v));
            }
            System.IO.File.WriteAllText(statePath, sb.ToString());
            Console.WriteLine();
            Console.WriteLine("  Baseline written to " + statePath + ".");
            Console.WriteLine("  Now switch Abode Night View ON and re-run without --baseline.");
            return 0;
        }

        // 2 -- the overlay window covering THIS application's canvas
        IntPtr ov = OverlayOver(canvas);
        int overlays = CountOverlays();
        Check(ov != IntPtr.Zero, "overlay window found",
              ov == IntPtr.Zero ? "not running" : "0x" + ov.ToInt64().ToString("X8") +
              (overlays > 1 ? " (" + overlays + " overlays live; picked the one over this canvas)" : ""));
        if (ov == IntPtr.Zero) return 2;

        Native.RECT ovr = Native.RectOf(ov);
        Check(ovr.Same(canvas), "overlay rect == canvas rect", ovr.ToString());

        uint ex = (uint)GetWindowLong(ov, GWL_EXSTYLE);
        const uint LAYERED = 0x80000, TRANSPARENT = 0x20, NOACTIVATE = 0x8000000,
                   TOOLWINDOW = 0x80, APPWINDOW = 0x40000, TOPMOST = 0x8;
        Check((ex & (LAYERED | TRANSPARENT | NOACTIVATE | TOOLWINDOW)) ==
              (LAYERED | TRANSPARENT | NOACTIVATE | TOOLWINDOW) && (ex & APPWINDOW) == 0,
              "extended styles", "0x" + ex.ToString("X8"));

        uint key, flags; byte alpha;
        bool la = GetLayeredWindowAttributes(ov, out key, out alpha, out flags);
        Check(la && alpha > 0, "layered alpha set", la
            ? string.Format(CultureInfo.InvariantCulture,
                            "alpha={0} ({1:0}%) flags=0x{2:X}", alpha, alpha / 2.55, flags)
            : "GetLayeredWindowAttributes failed");

        Check((ex & TOPMOST) == 0, "not WS_EX_TOPMOST",
              "so the application menus and panels stay above it");

        // 2b -- the overlay must never be the foreground window, or it would have
        //       taken focus away from InDesign at some point.
        Check(Native.GetForegroundWindow() != ov, "overlay is not the foreground window", null);

        // 3 -- precondition. Everything below is only meaningful while InDesign is
        //      the window actually on screen at that point. If another application
        //      is covering the canvas, the overlay is CORRECTLY behind it -- that is
        //      the whole reason it is not topmost -- so these are skips, not faults.
        var mid = new Native.POINT();
        mid.X = canvas.Left + canvas.W / 2;
        mid.Y = canvas.Top + canvas.H / 2;
        IntPtr hit = WindowFromPoint(mid);
        uint hitPid; GetWindowThreadProcessId(hit, out hitPid);
        bool onScreen = hitPid == loc.Pid || hit == ov;

        if (!onScreen)
        {
            Console.WriteLine("  [SKIP] another application is covering the canvas (" +
                              Native.ClassOf(hit) + ", pid " + hitPid + ")");
            Console.WriteLine("         z-order, click-through and photometry need " +
                              AppName + " visible. Bring it forward and re-run.");
            Console.WriteLine();
            Console.WriteLine("  " + _pass + " passed, " + _fail + " failed.");
            return _fail == 0 ? 0 : 1;
        }

        // 4 -- z-order: ABOVE InDesign, not necessarily adjacent to it. Requiring
        //      adjacency would fail every time one of InDesign's own popups sits in
        //      between, which is legitimate and is what keeps menus undimmed.
        Check(IsAbove(ov, loc.MainWindow), "overlay is above the application frame",
              "window directly above the frame = 0x" +
              Native.GetWindow(loc.MainWindow, Native.GW_HWNDPREV).ToInt64().ToString("X8"));

        Check(Native.GetWindow(ov, GW_OWNER) == loc.MainWindow ||
              Native.GetWindow(ov, GW_OWNER) == IntPtr.Zero,
              "overlay ownership",
              Native.GetWindow(ov, GW_OWNER) == loc.MainWindow
                  ? "owned by the application frame (zmode=owned)"
                  : "not owned – running zmode=above/topmost");

        // 5 -- click-through, across the whole rectangle rather than one point. A
        //      single centre sample would not catch a child window that only covers
        //      part of the canvas.
        int probes = 0, through = 0;
        var badPoint = "";
        for (int gy = 1; gy <= 3; gy++)
            for (int gx = 1; gx <= 3; gx++)
            {
                var p = new Native.POINT();
                p.X = canvas.Left + canvas.W * gx / 4;
                p.Y = canvas.Top + canvas.H * gy / 4;
                IntPtr h = WindowFromPoint(p);
                probes++;
                if (h != ov) through++;
                else badPoint += " (" + p.X + "," + p.Y + ")";
            }
        Check(through == probes, "hit-test falls through to the application at every probe",
              through + "/" + probes + " points" +
              (badPoint.Length > 0 ? "; overlay was hit at" + badPoint : "; centre -> " +
               Native.ClassOf(hit) + " pid=" + hitPid));

        // 6 -- InDesign's own top-level popups (menus, floating panels, tool tips,
        //      modal dialogs) must stay ABOVE the overlay or they get dimmed too.
        //      This matters most in --zmode=owned, where they share an owner with
        //      the overlay. Enumerate everything top-level in InDesign's process,
        //      not only what the frame owns, because menus are owned by whichever
        //      window opened them.
        Console.WriteLine("  " + AppName + " top-level windows and where they sit:");
        int popups = 0, buried = 0;
        Native.EnumWindows((h, p) =>
        {
            if (h == ov || h == loc.MainWindow || !Native.IsWindowVisible(h)) return true;
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != loc.Pid) return true;
            var rr = Native.RectOf(h);
            if (rr.IsEmpty || rr.Left <= -30000) return true;
            // Ignore anything that does not overlap the canvas: it cannot be dimmed.
            if (rr.Right <= canvas.Left || rr.Left >= canvas.Right ||
                rr.Bottom <= canvas.Top || rr.Top >= canvas.Bottom) return true;
            popups++;
            bool ok = !IsAbove(ov, h);          // the popup should be above the overlay
            if (!ok) buried++;
            Console.WriteLine("         " + (ok ? "above" : "BELOW") + " overlay: 0x" +
                              h.ToInt64().ToString("X8") + " " +
                              Kind(Native.ClassOf(h)) + " " + rr);
            return true;
        }, IntPtr.Zero);
        if (popups == 0)
            Console.WriteLine("         (nothing overlapping the canvas right now – open a menu, " +
                              "a dialog or float a panel and re-run)");
        else
            Check(buried == 0, "application popups over the canvas are not dimmed",
                  popups + " checked");

        // 7 -- photometry.
        //
        // NOT "is the canvas darker than the chrome next to it" -- InDesign's dark
        // UI is already darker than a dimmed white page, so that comparison is
        // meaningless. The only sound test is the SAME regions with the overlay off
        // and on: the canvas must fall to k, and everything outside must not move.
        double lumCanvas = Sample(canvas, save);
        double lumChrome = Sample(ChromeStrip(loc, canvas), null);
        double k = 1.0 - alpha / 255.0;

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "         canvas {0:0.00}   chrome {1:0.00}   alpha implies k={2:0.000}",
            lumCanvas, lumChrome, k));

        if (System.IO.File.Exists(statePath))
        {
            double bc = 0, bh = 0;
            var baseStrips = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in System.IO.File.ReadAllLines(statePath))
            {
                var kv = line.Split('=');
                if (kv.Length != 2) continue;
                if (kv[0] == "canvas") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bc);
                if (kv[0] == "chrome") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bh);
                if (kv[0].StartsWith("strip."))
                {
                    double sv;
                    if (double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out sv))
                        baseStrips[kv[0].Substring(6)] = sv;
                }
            }
            double rc = bc > 0.5 ? lumCanvas / bc : -1;
            double rh = bh > 0.5 ? lumChrome / bh : -1;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "         vs baseline: canvas {0:0.00} -> {1:0.00} (ratio {2:0.000}, expected {3:0.000})",
                bc, lumCanvas, rc, k));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "                      chrome {0:0.00} -> {1:0.00} (ratio {2:0.000}, expected 1.000)",
                bh, lumChrome, rh));
            // The chrome region is outside the overlay, so it MUST read the same in
            // both captures. If it does not, the screen changed between the two runs
            // and the canvas ratio is comparing two different pictures -- which says
            // nothing about the overlay. Report that rather than a bogus failure.
            if (Math.Abs(rh - 1.0) >= 0.03)
            {
                Console.WriteLine("  [SKIP] baseline is stale: chrome outside the canvas moved by " +
                                  ((rh - 1.0) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%,");
                Console.WriteLine("         so the screen changed between the two captures. Retake it:");
                Console.WriteLine("         switch Abode Night View off, run --baseline, switch on, re-run.");
            }
            else
            {
                Check(Math.Abs(rc - k) < 0.05, "canvas dimmed to the predicted k", null);
                Check(true, "chrome outside the canvas is untouched", null);
                CheckExcludedStrips(loc, canvas, baseStrips, k);
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  No baseline at " + statePath + " – switch Abode Night View OFF");
            Console.WriteLine("  and run:  AbodeNightView.exe --baseline");
        }

        Console.WriteLine();
        Console.WriteLine("  " + _pass + " passed, " + _fail + " failed.");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Name the kind of window a class represents, for the popup report.</summary>
    static string Kind(string cls)
    {
        switch (cls)
        {
            case "#32768": return cls + " (menu)";
            case "#32770": return cls + " (dialog)";
            case "ComboLBox": return cls + " (dropdown)";
            case "tooltips_class32": return cls + " (tooltip)";
            case "indesign": return cls + " (frame)";
            default: return cls;
        }
    }

    /// <summary>True if <paramref name="a"/> is anywhere above <paramref name="b"/> in the z-order.</summary>
    static bool IsAbove(IntPtr a, IntPtr b)
    {
        for (IntPtr h = a; h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDNEXT))
            if (h == b) return true;
        return false;
    }

    /// <summary>One strip of the document container that the canvas rectangle
    /// leaves out. In InDesign the left and top strips are the rulers.</summary>
    internal struct Strip { public string Name; public Native.RECT Rect; }

    /// <summary>
    /// The parts of the document container that the canvas rule excludes, as
    /// rectangles worth photographing. Anything thinner than 6 physical pixels is
    /// dropped: a one- or two-pixel border is a frame line, and its mean is
    /// dominated by anti-aliasing rather than by whether we dimmed it.
    ///
    /// On InDesign this yields four strips -- left and top are the rulers, right
    /// and bottom the scrollbars. On Illustrator and Photoshop it yields only
    /// right and bottom, because their canvas child starts at the document origin
    /// and their rulers are painted inside the region we dim. That difference is
    /// not a defect in this function; it is the finding.
    /// </summary>
    static List<Strip> ExcludedStrips(ViewportLocator loc, Native.RECT canvas)
    {
        var list = new List<Strip>();
        Native.RECT doc, can;
        if (!loc.ResolveDocumentAndCanvas(out doc, out can)) return list;
        // Only meaningful when the measured canvas really is the one inside doc.
        if (can.Left != canvas.Left || can.Top != canvas.Top ||
            can.Right != canvas.Right || can.Bottom != canvas.Bottom) return list;
        if (doc.IsEmpty) return list;

        const int MinThickness = 6;
        if (can.Left - doc.Left >= MinThickness)
            list.Add(MakeStrip("left", doc.Left, can.Top, can.Left, can.Bottom));
        if (can.Top - doc.Top >= MinThickness)
            list.Add(MakeStrip("top", can.Left, doc.Top, can.Right, can.Top));
        if (doc.Right - can.Right >= MinThickness)
            list.Add(MakeStrip("right", can.Right, can.Top, doc.Right, can.Bottom));
        if (doc.Bottom - can.Bottom >= MinThickness)
            list.Add(MakeStrip("bottom", can.Left, can.Bottom, can.Right, doc.Bottom));
        return list;
    }

    static Strip MakeStrip(string name, int l, int t, int r, int b)
    {
        var s = new Strip();
        s.Name = name;
        s.Rect = new Native.RECT { Left = l, Top = t, Right = r, Bottom = b };
        return s;
    }

    /// <summary>
    /// Assert that every strip the canvas rule excludes reads the same with the
    /// overlay on as it did with it off. This is the ruler check: in InDesign the
    /// left and top strips ARE the rulers, and a change here means the overlay has
    /// started covering interface Adobe draws outside the canvas.
    /// </summary>
    static void CheckExcludedStrips(ViewportLocator loc, Native.RECT canvas,
                                    Dictionary<string, double> baseline, double k)
    {
        var strips = ExcludedStrips(loc, canvas);
        if (strips.Count == 0)
        {
            Console.WriteLine("         (the canvas rectangle fills the document container on every");
            Console.WriteLine("          side, so there is no excluded strip to check. In this product");
            Console.WriteLine("          the rulers, if shown, are inside the region being dimmed.)");
            return;
        }
        // The threshold is the midpoint between "dimmed" and "not dimmed", not a
        // tight band around 1.0. These strips are the rulers and the scrollbars:
        // they are thin, high-contrast, and their content genuinely moves between
        // two captures -- a scrollbar thumb slides, a ruler's cursor indicator
        // follows the mouse, the tick labels change with the scroll position. A
        // 3 % band around 1.000 measures that jitter, not the overlay, and it
        // failed on a run where nothing was wrong.
        //
        // What must never happen is the strip being MULTIPLIED by k, which is
        // unmistakable: k here is around 0.45, and content jitter is a few tens of
        // a percent around 1.0. Anything below the midpoint has been dimmed.
        double floorRatio = (1.0 + k) / 2.0;
        int checkedCount = 0, dimmed = 0, jittery = 0;
        foreach (var st in strips)
        {
            double b0;
            if (!baseline.TryGetValue(st.Name, out b0) || b0 <= 0.5) continue;
            double now = Sample(st.Rect, null);
            double ratio = now / b0;
            checkedCount++;
            if (ratio < floorRatio) dimmed++;
            else if (Math.Abs(ratio - 1.0) >= 0.10) jittery++;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "                      {0,-6} {1:0.00} -> {2:0.00} (ratio {3:0.000}, dimmed would be {4:0.000})",
                st.Name, b0, now, ratio, k));
        }
        if (checkedCount == 0)
        {
            Console.WriteLine("  [SKIP] the baseline predates excluded-strip measurement; retake it.");
            return;
        }
        Check(dimmed == 0, "rulers and scrollbars outside the canvas are not dimmed",
              checkedCount + " strip(s)" +
              (jittery > 0 ? "; " + jittery + " changed content between captures" : ""));
    }

    /// <summary>A band of InDesign chrome outside the canvas: the dock to its left.</summary>
    static Native.RECT ChromeStrip(ViewportLocator loc, Native.RECT canvas)
    {
        Native.RECT client = Native.ClientRectOnScreen(loc.MainWindow);
        int right = Math.Max(client.Left + 4, canvas.Left - 4);
        return new Native.RECT {
            Left = client.Left, Right = right,
            Top = canvas.Top, Bottom = canvas.Bottom };
    }

    static double Sample(Native.RECT r, string save)
    {
        if (r.IsEmpty) return -1;
        using (var bmp = Grab(r))
        {
            if (save != null) bmp.Save(save, ImageFormat.Png);
            return MeanLuma(bmp, new Rectangle(0, 0, r.W, r.H));
        }
    }
}
