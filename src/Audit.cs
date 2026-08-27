// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - Audit: the mechanical half of the compatibility audit
// ----------------------------------------------------------------------------
//  Runs the overlay against a window this process owns and can therefore put
//  anywhere, at any size, on any monitor, on demand. That turns several things
//  that are normally "reasoned about" into measurements:
//
//    geometry     does the overlay land on the exact client rectangle, at
//                 every size and position tested, including a monitor whose
//                 desktop coordinates are negative, and a window straddling
//                 two monitors -- and how long does it take to catch up?
//    photometry   what is the real per-level transfer function? The target
//                 paints an exact step wedge, so one capture pair measures all
//                 six levels rather than inferring them from a mean.
//    renderer     the target is plain GDI with no Direct3D anywhere. If the
//                 dimming is identical over it and over InDesign's
//                 GPU-composited canvas, the overlay does not depend on the
//                 target's renderer.
//    ownership    an owned dialog over the target must sit ABOVE the overlay,
//                 which is the mechanism that keeps InDesign's menus and modal
//                 dialogs undimmed.
//    input        the overlay must never be what WindowFromPoint returns.
//
//  Not shipped. Development tree only.
//
//  Usage:  Audit.exe [--exe=path\AbodeNightView.exe] [--strength=55] [--out=dir]
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Audit
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    static int _pass, _fail;
    static readonly List<string> _lines = new List<string>();

    static void Say(string s) { Console.WriteLine(s); _lines.Add(s); }

    static void Check(bool ok, string what, string detail)
    {
        Say("  [" + (ok ? "PASS" : "FAIL") + "] " + what + (detail == null ? "" : "  " + detail));
        if (ok) _pass++; else _fail++;
    }

    // ---------------------------------------------------------------- target

    static AuditTarget _form;
    static Process _nv;
    static string _outDir;
    static string _sandboxExe, _sandbox;
    static int _strength = 55;

    /// <summary>Launch the private copy of the shipping binary with given arguments.</summary>
    static Process Start(string args)
    {
        return Process.Start(new ProcessStartInfo(_sandboxExe, args)
        { UseShellExecute = false, WorkingDirectory = _sandbox });
    }

    static void StopNv()
    {
        try { if (_nv != null && !_nv.HasExited) { _nv.Kill(); _nv.WaitForExit(3000); } } catch { }
        _nv = null;
        for (int i = 0; i < 40 && Overlays().Count > 0; i++) Pump(50);
    }

    /// <summary>Every visible overlay window on the desktop, in z-order.</summary>
    static List<IntPtr> Overlays()
    {
        var l = new List<IntPtr>();
        Native.EnumWindows((h, p) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.TitleOf(h) != "AbodeNV Overlay") return true;
            l.Add(h);
            return true;
        }, IntPtr.Zero);
        return l;
    }

    /// <summary>Wait until the overlay set matches a predicate, or give up.</summary>
    static bool Await(Func<List<IntPtr>, bool> ok, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Pump(25);
            if (ok(Overlays())) return true;
        }
        return ok(Overlays());
    }

    [STAThread]
    static int Main(string[] argv)
    {
        Native.ApplyBestDpiAwareness();
        // Paths in this project contain non-ASCII characters; without this the
        // report prints them as mojibake in the console OEM codepage.
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }

        if (Array.IndexOf(argv, "--selftest") >= 0) return SelfTest.Run();

        string exe = Path.Combine(
            Path.GetDirectoryName(typeof(Audit).Assembly.Location) ?? ".", "AbodeNightView.exe");
        int strength = 55;
        _outDir = Path.GetDirectoryName(typeof(Audit).Assembly.Location) ?? ".";
        foreach (var a in argv)
        {
            if (a.StartsWith("--exe=")) exe = a.Substring(6);
            else if (a.StartsWith("--strength=")) strength = int.Parse(a.Substring(11));
            else if (a.StartsWith("--out=")) _outDir = a.Substring(6);
        }

        if (FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "AbodeNV Overlay") != IntPtr.Zero)
        {
            Console.WriteLine("An Abode Night View overlay is already running. Exit it first " +
                              "(tray > Exit) so this audit drives its own copy.");
            return 2;
        }

        Application.EnableVisualStyles();
        _form = new AuditTarget("Abode NV audit target", new Rectangle(120, 120, 1200, 840));
        Pump(400);

        Say("Abode Night View - mechanical audit");
        Say("======================================");
        Say("  audit pid    " + Process.GetCurrentProcess().Id);
        Say("  target HWND  0x" + _form.Handle.ToInt64().ToString("X8"));
        Say("  strength     " + strength + "%   k = " +
            (1.0 - strength / 100.0).ToString("0.000", CultureInfo.InvariantCulture));
        Say("");

        try
        {
            // Phase 1 -- capture the undimmed wedge BEFORE the overlay exists.
            Native.RECT client = Native.ClientRectOnScreen(_form.Handle);
            string off = Path.Combine(_outDir, "audit-off.png");
            IntPtr blocker = CoveredBy(client, _form.Handle);
            if (blocker != IntPtr.Zero)
                Say("  [WARN] " + Native.ClassOf(blocker) + " is covering the target; the wedge " +
                    "captures will be contaminated and Transfer.exe's fit will be meaningless.");
            Shot(client, off);
            Say("  wrote " + off + "  " + client);

            // Phase 2 -- start Abode Night View against this window.
            //
            // From a private copy in a scratch folder, so it reads and writes its
            // own settings file and cannot disturb the one the user is living with.
            string sandbox = Path.Combine(Path.GetTempPath(), "abodenv-audit");
            Directory.CreateDirectory(sandbox);
            string exeCopy = Path.Combine(sandbox, "AbodeNightView.exe");
            File.Copy(exe, exeCopy, true);
            foreach (string junk in new[] { "AbodeNightView.ini", "AbodeNightView-verify.txt",
                                            "NightView.ini" })
            { try { File.Delete(Path.Combine(sandbox, junk)); } catch { } }
            _sandboxExe = exeCopy; _sandbox = sandbox;

            // --products=none FIRST, so none of the real Adobe applications the
            // person running this audit happens to have open gets an overlay, and
            // then one runtime adapter aimed at this process. Getting that order
            // wrong is not a small mistake: the run before this comment existed
            // dimmed the whole client area of every Adobe window on the machine
            // and then measured the wrong one.
            _nv = Start("--on --products=none --adapter=audit:Audit:Audit:NVAuditTarget " +
                        "--region=client --strength=" + strength + " --zmode=owned");
            _nvPid = _nv == null ? 0 : (uint)_nv.Id;

            IntPtr ov = IntPtr.Zero;
            for (int i = 0; i < 200 && ov == IntPtr.Zero; i++)
            {
                Pump(50);
                ov = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "AbodeNV Overlay");
            }
            Check(ov != IntPtr.Zero, "overlay started and attached to a non-Adobe target",
                  ov == IntPtr.Zero ? "never appeared" : "0x" + ov.ToInt64().ToString("X8"));
            if (ov == IntPtr.Zero) return Finish();

            // Wait for the overlay to be ON the target, not merely to exist. The
            // window is created early in Abode Night View's startup and parked off-screen
            // until the first Sync(), so a fixed delay here photographed an
            // undimmed screen and "measured" a transfer function of exactly 1.0.
            var settle = Stopwatch.StartNew();
            bool placed = false;
            while (settle.Elapsed.TotalMilliseconds < 8000)
            {
                Pump(10);
                if (Native.IsWindowVisible(ov) && Native.RectOf(ov).Same(client)) { placed = true; break; }
            }
            Check(placed, "overlay reached the target rectangle before measuring",
                  settle.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms");
            Pump(300);

            // Phase 3 -- photometry on the same rectangle.
            string on = Path.Combine(_outDir, "audit-on.png");
            IntPtr blocker2 = CoveredBy(client, _form.Handle);
            Shot(client, on);
            Say("  wrote " + on + "   overlay at " + Native.RectOf(ov) +
                ", visible " + Native.IsWindowVisible(ov));
            Check(blocker2 == IntPtr.Zero,
                  "nothing else was covering the target while the wedge was photographed",
                  blocker2 == IntPtr.Zero ? "Transfer.exe can be run on these two captures"
                                          : Native.ClassOf(blocker2) + " was in the way");
            Say("");

            Geometry(ov);
            Say("");
            Ownership(ov);
            Say("");
            InputTransparency(ov);
            Say("");
            Lifecycle(ov);
            Say("");

            _strength = strength;
            MultiTarget();
        }
        finally { }

        return Finish();
    }

    static int Finish()
    {
        try { if (_nv != null && !_nv.HasExited) { _nv.Kill(); _nv.WaitForExit(3000); } } catch { }
        try { if (_form != null) _form.Dispose(); } catch { }
        Say("");
        Say("  " + _pass + " passed, " + _fail + " failed.");
        try { File.WriteAllLines(Path.Combine(_outDir, "audit-report.txt"), _lines.ToArray()); } catch { }
        return _fail == 0 ? 0 : 1;
    }

    // -------------------------------------------------------------- geometry

    /// <summary>
    /// Move and resize the target through rectangles chosen to break any hidden
    /// assumption about resolution, origin, or monitor, and measure how closely
    /// and how quickly the overlay follows.
    /// </summary>
    static void Geometry(IntPtr ov)
    {
        Say("  Geometry tracking");
        Say("  ------------------------------------------------------------------");
        Say("    " + Pad("rectangle", 26) + Pad("monitor", 14) + Pad("settled", 9) + "error");

        var tests = new List<KeyValuePair<string, Rectangle>>();
        // A lambda, not a local function: the in-box csc for .NET Framework is a
        // C# 5 compiler and local functions are C# 7.
        Action<string, Rectangle> Add =
            (n, r) => tests.Add(new KeyValuePair<string, Rectangle>(n, r));

        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle b = s.Bounds;
            Add("centered on " + s.DeviceName.Replace(@"\\.\", ""),
                new Rectangle(b.X + b.Width / 6, b.Y + b.Height / 6, b.Width * 2 / 3, b.Height * 2 / 3));
            Add("full " + s.DeviceName.Replace(@"\\.\", ""), b);
        }

        // Common panel resolutions, as window sizes, on the primary monitor.
        Rectangle p = Screen.PrimaryScreen.Bounds;
        Add("1280x720 window", new Rectangle(p.X + 40, p.Y + 40, 1280, 720));
        Add("1920x1080 window", new Rectangle(p.X, p.Y, Math.Min(1920, p.Width), Math.Min(1080, p.Height)));
        Add("2560x1440 window", new Rectangle(p.X, p.Y, Math.Min(2560, p.Width), Math.Min(1440, p.Height)));

        // The shapes that break naive code.
        Add("tiny", new Rectangle(p.X + 500, p.Y + 500, 160, 120));
        Add("wide and thin", new Rectangle(p.X, p.Y + 300, p.Width, 60));
        Add("tall and thin", new Rectangle(p.X + 300, p.Y, 90, p.Height));

        // Straddling a monitor edge, and off the top-left of the desktop.
        Screen[] all = Screen.AllScreens;
        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
            {
                Rectangle a = all[i].Bounds, b2 = all[j].Bounds;
                if (a.Right == b2.Left || b2.Right == a.Left)
                {
                    int edge = a.Right == b2.Left ? a.Right : b2.Right;
                    int top = Math.Max(a.Top, b2.Top) + 100;
                    Add("straddling a vertical edge", new Rectangle(edge - 400, top, 800, 600));
                }
                if (a.Bottom == b2.Top || b2.Bottom == a.Top)
                {
                    int edge = a.Bottom == b2.Top ? a.Bottom : b2.Bottom;
                    int left = Math.Max(a.Left, b2.Left) + 100;
                    Add("straddling a horizontal edge", new Rectangle(left, edge - 300, 900, 600));
                }
            }

        int minX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int minY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        Add("at the virtual-desktop origin", new Rectangle(minX, minY, 700, 500));
        if (minY < 0) Add("negative Y coordinates", new Rectangle(2600, minY + 20, 900, 1200));
        if (minX < 0) Add("negative X coordinates", new Rectangle(minX + 20, 200, 900, 700));

        // Back to something ordinary, twice, to prove repeated moves settle.
        Add("back to the start", new Rectangle(p.X + 120, p.Y + 120, 1200, 840));

        // One throwaway move first. The very first measurement after the overlay
        // process starts also pays for its tray icon, its hooks and its balloon,
        // and timing that as if it were steady-state tracking would be misleading.
        var warm = Stopwatch.StartNew();
        _form.Bounds = new Rectangle(p.X + 200, p.Y + 200, 900, 600);
        while (warm.Elapsed.TotalMilliseconds < 4000)
        {
            Pump(4);
            if (Native.RectOf(ov).Same(Native.ClientRectOnScreen(_form.Handle))) break;
        }
        Say("    (warm-up move settled in " +
            warm.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) +
            " ms; startup cost, not tracking latency)");

        foreach (var t in tests)
        {
            _form.Bounds = t.Value;
            Pump(60);
            Native.RECT want = Native.ClientRectOnScreen(_form.Handle);

            var sw = Stopwatch.StartNew();
            Native.RECT got = new Native.RECT();
            bool okRect = false;
            while (sw.Elapsed.TotalMilliseconds < 2000)
            {
                Pump(4);
                got = Native.RectOf(ov);
                if (got.Same(want) && Native.IsWindowVisible(ov)) { okRect = true; break; }
            }
            double ms = sw.Elapsed.TotalMilliseconds;

            IntPtr mon = Native.MonitorFromWindow(_form.Handle, Native.MONITOR_DEFAULTTONEAREST);
            var mi = Native.MONITORINFOEX.Create();
            string dev = Native.GetMonitorInfoW(mon, ref mi)
                ? mi.szDevice.Replace(@"\\.\DISPLAY", "D") : "?";

            string err = okRect ? "exact" :
                string.Format(CultureInfo.InvariantCulture, "want {0} got {1}", want, got);
            Say("    " + Pad(t.Key, 26) + Pad(dev, 14) +
                Pad(okRect ? ms.ToString("0", CultureInfo.InvariantCulture) + " ms" : "-", 9) + err);
            if (!okRect) _fail++; else _pass++;
        }
        Say("    " + tests.Count + " rectangles tested.");
    }

    // ------------------------------------------------------------- ownership

    /// <summary>
    /// An owned window shown after the overlay must land above it. This is exactly
    /// the relationship InDesign's modal dialogs and floating panels have with the
    /// frame, and it is why owning the overlay does not bury them.
    /// </summary>
    static void Ownership(IntPtr ov)
    {
        Say("  Owned-window ordering (the mechanism that keeps dialogs undimmed)");
        Say("  ------------------------------------------------------------------");

        Check(IsAbove(ov, _form.Handle), "precondition: the overlay starts above its owner", null);

        // Regression test for the defect this audit found. Push the overlay below
        // the frame by hand -- which is what showing an owned window does as a side
        // effect -- and require Abode Night View to notice and repair it. Owned mode used
        // to hard-code "the z-order is fine", so this never recovered and the
        // dimming simply stopped with nothing to report it.
        // SWP_NOOWNERZORDER raises the frame WITHOUT bringing its owned windows
        // along, which is exactly the state Windows leaves behind when another
        // owned window is shown. Deterministic, unlike trying to push the overlay
        // down directly -- Windows sometimes clamps that back.
        //
        // Showing an owned window first is what makes this reliable. On its own the
        // raise sometimes leaves the overlay where it was and the test then passes
        // without ever having reproduced anything -- a regression test that cannot
        // fail is not a regression test. Both steps together are the exact sequence
        // a real application performs when it opens a floating panel.
        //
        // The target has to be FOREGROUND for the raise to take. A process that is
        // not in the foreground cannot move a window to the top of the z-order --
        // Windows silently clamps it -- and the run where that happened reported
        // "repaired in 174 ms" for a burial that never occurred.
        //
        // The burial has to be SAMPLED FAST. Repair is driven by the window event
        // the raise itself produces, so it can complete within a few milliseconds
        // -- and a run that waits 60 ms before looking sees a perfectly correct
        // z-order and reports "repaired" for a burial it never observed. Poll at
        // 2 ms, and if the burial genuinely cannot be produced, say so instead of
        // passing.
        // The burial is produced DIRECTLY -- SetWindowPos(overlay, frame, ...)
        // places the overlay immediately below its own owner -- rather than by
        // raising the frame and hoping. Two reasons. It is deterministic, and it
        // is the exact state to be repaired, whereas the indirect route now
        // usually fails to produce it at all: the product re-seats on activation
        // and repairs from the window event the raise itself generates, so the
        // window of opportunity is a few milliseconds wide. A regression test
        // that only sometimes creates the condition is a regression test that
        // only sometimes tests anything, and the version before this comment
        // silently reported success on runs where nothing had been buried.
        bool sank = false, recovered = false;
        double recoveryMs = 0;

        Native.SetWindowPos(ov, _form.Handle, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE |
            Native.SWP_NOOWNERZORDER | Native.SWP_NOSENDCHANGING);
        sank = !IsAbove(ov, _form.Handle);

        if (sank)
        {
            var rec = Stopwatch.StartNew();
            while (rec.Elapsed.TotalMilliseconds < 3000)
            {
                Pump(2);
                if (IsAbove(ov, _form.Handle)) { recovered = true; break; }
            }
            recoveryMs = rec.Elapsed.TotalMilliseconds;
        }
        Pump(150);

        if (!sank)
            Say("  [SKIP] Windows refused to place the overlay below its owner, so the " +
                "burial could not be produced and nothing was proved here.");
        else
            Check(recovered, "overlay repairs itself after being pushed below its owner",
                  recovered ? recoveryMs.ToString("0", CultureInfo.InvariantCulture) + " ms"
                            : "still buried after 3 s");

        // -- Case A: the application is FOREGROUND ---------------------------
        //
        // This is the case that matters and the one that is asserted, because it
        // is what happens every time a user opens a menu, a panel or a dialog in
        // the application they are working in.
        Say("");
        Say("    Case A - the owning application is FOREGROUND (the normal case)");
        bool fg = SetForegroundWindow(_form.Handle);
        Pump(500);
        fg = Native.GetForegroundWindow() == _form.Handle;
        if (!fg)
            Say("      [SKIP] could not bring the target to the foreground; " +
                "Windows refused the focus change, so Case A cannot be asserted here.");
        DialogCase(ov, fg, "A");

        // -- Case B: the application is in the BACKGROUND ---------------------
        //
        // Forced deterministically by activating the console window, which belongs
        // to conhost -- a different process. Recorded rather than asserted: what
        // happens here is a Windows rule, not an Abode Night View decision. A process that
        // is not in the foreground cannot take the top of the z-order, so a window
        // it shows lands below whatever is already there, including our overlay.
        Say("");
        Say("    Case B - the owning application is in the BACKGROUND (characterised, not asserted)");
        IntPtr con = GetConsoleWindow();
        if (con != IntPtr.Zero) SetForegroundWindow(con);
        Pump(500);
        Say("      foreground is 0x" + Native.GetForegroundWindow().ToInt64().ToString("X8") +
            ", the target frame is 0x" + _form.Handle.ToInt64().ToString("X8"));
        DialogCase(ov, false, "B");

        // A topmost window -- which is what real menus (#32768) are -- must win in
        // every case, foreground or not, because WS_EX_TOPMOST outranks the whole
        // non-topmost band regardless of who is active.
        Say("");
        Native.RECT c2 = Native.ClientRectOnScreen(_form.Handle);
        using (var top = new Form
        {
            Text = "Audit topmost",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(c2.Left + 200, c2.Top + 200, 300, 160),
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Color.White,
        })
        {
            top.Show();
            Pump(400);
            Check(!IsAbove(ov, top.Handle),
                  "a topmost window (what menus are) sits above the overlay, " +
                  "even with the app in the background", null);
            top.Close();
        }
        Pump(200);
    }

    /// <summary>
    /// Show an owned dialog and report where it landed relative to the overlay.
    /// Asserted only when <paramref name="assert"/> is true; otherwise recorded.
    /// </summary>
    static void DialogCase(IntPtr ov, bool assert, string label)
    {
        Native.RECT c = Native.ClientRectOnScreen(_form.Handle);
        using (var dlg = new Form
        {
            Text = "Audit dialog " + label,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(c.Left + 60, c.Top + 60, 420, 220),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            BackColor = Color.White,
        })
        {
            dlg.Show(_form);                      // owner = the target frame
            Pump(500);
            DumpGroup(ov, dlg.Handle, "owned dialog shown normally");
            bool ok = !IsAbove(ov, dlg.Handle);
            if (assert)
                Check(ok, "an owned dialog opened after the overlay sits above it", null);
            else
                Say("      -> dialog above overlay: " + ok +
                    (ok ? "" : "   (KNOWN: a background process cannot take the z-order)"));

            // The unactivated variant: SW_SHOWNA. Same question, no activation at all.
            using (var quiet = new Form
            {
                Text = "Audit unactivated " + label,
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(c.Left + 120, c.Top + 120, 400, 200),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ShowInTaskbar = false,
                BackColor = Color.White,
            })
            {
                Native.SetWindowLongPtr(quiet.Handle, Native.GWLP_HWNDPARENT, _form.Handle);
                Native.SetWindowPos(quiet.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                Pump(500);
                DumpGroup(ov, quiet.Handle, "owned window shown WITHOUT activation (SW_SHOWNA)");
                Say("      -> unactivated window above overlay: " + !IsAbove(ov, quiet.Handle));
                quiet.Close();
            }
            Pump(200);

            Check(IsAbove(ov, _form.Handle),
                  "case " + label + ": the overlay is still above its owner throughout", null);
            dlg.Close();
        }
        Pump(300);
    }

    // ------------------------------------------------------------ hit-testing

    static void InputTransparency(IntPtr ov)
    {
        Say("  Input transparency");
        Say("  ------------------------------------------------------------------");

        Native.RECT r = Native.RectOf(ov);
        int probes = 0, through = 0;
        var missed = new List<string>();
        for (int gy = 0; gy <= 8; gy++)
            for (int gx = 0; gx <= 8; gx++)
            {
                var p = new Native.POINT();
                p.X = r.Left + 2 + (r.W - 5) * gx / 8;
                p.Y = r.Top + 2 + (r.H - 5) * gy / 8;
                IntPtr h = WindowFromPoint(p);
                probes++;
                if (h != ov) through++; else missed.Add(p.X + "," + p.Y);
            }
        Check(through == probes, "WindowFromPoint never returns the overlay",
              through + "/" + probes + " points" +
              (missed.Count > 0 ? "; hit at " + string.Join(" ", missed.ToArray()) : ""));

        Check(Native.GetForegroundWindow() != ov, "the overlay is not the foreground window",
              "foreground = 0x" + Native.GetForegroundWindow().ToInt64().ToString("X8"));

        uint ex = (uint)Native.GetWindowLongPtrW(ov, Native.GWL_EXSTYLE).ToInt64();
        const uint TRANSPARENT = 0x20, NOACTIVATE = 0x8000000, LAYERED = 0x80000,
                   TOOLWINDOW = 0x80, APPWINDOW = 0x40000;
        Check((ex & TRANSPARENT) != 0, "WS_EX_TRANSPARENT: mouse messages pass through", null);
        Check((ex & NOACTIVATE) != 0, "WS_EX_NOACTIVATE: cannot be activated by a click", null);
        Check((ex & LAYERED) != 0, "WS_EX_LAYERED: composited, not painted over", null);
        Check((ex & TOOLWINDOW) != 0 && (ex & APPWINDOW) == 0,
              "WS_EX_TOOLWINDOW without WS_EX_APPWINDOW: never in Alt+Tab or the taskbar",
              "exstyle 0x" + ex.ToString("X8"));
    }

    // -------------------------------------------------------------- lifecycle

    /// <summary>
    /// What happens when the tracked application disappears. The overlay must hide
    /// itself and must not leave a stray window over the desktop.
    /// </summary>
    static void Lifecycle(IntPtr ov)
    {
        Say("  Lifecycle");
        Say("  ------------------------------------------------------------------");

        _form.Hide();
        Pump(1600);                       // 4 misses at the 250 ms safety-net rate
        Check(!Native.IsWindowVisible(ov), "overlay hides when the target window goes away",
              "visible = " + Native.IsWindowVisible(ov));

        // Hiding is only half of detaching. The overlay is owned by the target --
        // a link into ANOTHER process's window tree -- and a released slot that
        // stays owned is a window we have volunteered to have destroyed the moment
        // the application quits. This is checked here rather than trusted because
        // nothing about it is visible: the overlay looks correct either way.
        IntPtr ownerAfterDetach = Native.GetWindow(ov, Native.GW_OWNER);
        Check(ownerAfterDetach == IntPtr.Zero,
              "and hands the owner link back instead of staying owned by a window it left",
              "owner = 0x" + ownerAfterDetach.ToInt64().ToString("X8"));

        _form.Show();
        Pump(1200);
        Native.RECT want = Native.ClientRectOnScreen(_form.Handle);
        bool back = false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 2500)
        {
            Pump(10);
            if (Native.IsWindowVisible(ov) && Native.RectOf(ov).Same(want)) { back = true; break; }
        }
        Check(back, "overlay reacquires the target when it comes back",
              back ? sw.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms"
                   : "did not reacquire within 2.5 s");

        // ... and takes the link back, so giving it up on detach is not a one-way
        // door that quietly leaves the overlay running as zmode=above afterwards.
        IntPtr ownerAfterReacquire = Native.GetWindow(ov, Native.GW_OWNER);
        Check(ownerAfterReacquire == _form.Handle,
              "and takes ownership again, rather than degrading to chasing the z-order",
              "owner = 0x" + ownerAfterReacquire.ToInt64().ToString("X8") +
              ", target = 0x" + _form.Handle.ToInt64().ToString("X8"));

        _form.Minimize();
        Pump(1600);
        Check(!Native.IsWindowVisible(ov), "overlay hides while the target is minimized", null);

        _form.Restore();
        Pump(1200);
        Check(Native.IsWindowVisible(ov), "overlay returns on restore", null);
    }

    // ------------------------------------------------------------------ util

    /// <summary>
    /// Print the top-level windows of the audit process plus the overlay, in true
    /// z-order. Nothing about window ordering is worth arguing over when the actual
    /// list can be printed.
    /// </summary>
    static void DumpGroup(IntPtr ov, IntPtr dlg, string when)
    {
        Say("    z-order, top first -- " + when);
        var wanted = new List<IntPtr> { ov, dlg, _form.Handle };
        uint mypid = (uint)Process.GetCurrentProcess().Id;
        int rank = 0;
        Native.EnumWindows((h, p) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (!wanted.Contains(h)) return true;
            rank++;
            string name = h == ov ? "overlay" : h == dlg ? "owned dialog" : "target frame";
            Say(string.Format(CultureInfo.InvariantCulture,
                "      {0}. {1,-14} 0x{2:X8}  owner=0x{3:X8}",
                rank, name, h.ToInt64(), Native.GetWindow(h, Native.GW_OWNER).ToInt64()));
            return true;
        }, IntPtr.Zero);
        if (mypid == 0) { }
    }

    static bool IsAbove(IntPtr a, IntPtr b)
    {
        for (IntPtr h = a; h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDNEXT))
            if (h == b) return true;
        return false;
    }

    static string Pad(string s, int n)
    { return s.Length >= n ? s.Substring(0, n - 1) + " " : s.PadRight(n); }

    /// <summary>Run the message loop for roughly this many milliseconds.</summary>
    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms)
        { Application.DoEvents(); Thread.Sleep(1); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h,
                                                       IntPtr s, int sx, int sy, uint rop);

    /// <summary>
    /// Is anything that is not ours covering the rectangle we are about to
    /// photograph? Sampled on a 5x5 grid, because a window can cover a corner
    /// without covering the center.
    ///
    /// This exists because it did happen. The two wedge captures feed Transfer.exe,
    /// which fits the transfer function over every pixel in them, and on a run
    /// where an unrelated full-screen window was sitting across two thirds of the
    /// target the fit came back k = 0.8073 against a per-level table that plainly
    /// said 0.456. Every assertion in this audit passed, because none of them looks
    /// at those pixels. A photometric input nobody checked is exactly the failure
    /// this harness has been bitten by before: a number that is wrong rather than
    /// a test that fails. --verify has had this guard from the start; the audit
    /// did not.
    /// </summary>
    static IntPtr CoveredBy(Native.RECT r, IntPtr mine)
    {
        uint minePid; Native.GetWindowThreadProcessId(mine, out minePid);
        for (int gy = 0; gy < 5; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                var pt = new Native.POINT();
                pt.X = r.Left + (int)((gx + 0.5) * r.W / 5);
                pt.Y = r.Top + (int)((gy + 0.5) * r.H / 5);
                IntPtr h = WindowFromPoint(pt);
                if (h == IntPtr.Zero) continue;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == minePid) continue;              // ours, or our overlay
                if (pid == _nvPid) continue;
                return h;
            }
        return IntPtr.Zero;
    }

    static uint _nvPid;

    static void Shot(Native.RECT r, string path)
    {
        using (var bmp = new Bitmap(r.W, r.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr src = GetDC(IntPtr.Zero), dst = g.GetHdc();
                BitBlt(dst, 0, 0, r.W, r.H, src, r.Left, r.Top, 0x00CC0020 | 0x40000000);
                g.ReleaseHdc(dst); ReleaseDC(IntPtr.Zero, src);
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    // ---------------------------------------------------------- multi-target

    /// <summary>
    /// Everything about tracking SEVERAL Adobe-shaped windows at once, driven
    /// against synthetic frames this process owns.
    ///
    /// These use a real OwlTarget adapter -- the same class, and therefore the
    /// same viewport validation, that InDesign and Illustrator go through -- so
    /// what is being tested is the shipping rule and not a stand-in for it.
    /// </summary>
    static void MultiTarget()
    {
        Say("  Multiple targets");
        Say("  ------------------------------------------------------------------");

        StopNv();
        try { _form.Hide(); } catch { }
        Pump(300);

        string harness = "--on --products=none " +
                         "--adapter=harness:Harness:Audit:" + SyntheticApp.FrameClass + ":owl " +
                         "--region=canvas --strength=" + _strength + " --zmode=owned";

        SyntheticApp a = null, b = null;
        try
        {
            a = new SyntheticApp("Harness A", new Rectangle(60, 60, 940, 720), 1);
            Pump(400);
            Check(a.Documents.Count == 1, "synthetic frame built an OWL.Document",
                  a.Documents.Count + " document(s), class " +
                  (a.Documents.Count > 0 ? Native.ClassOf(a.Documents[0]) : "-"));

            _nv = Start(harness);

            bool one = Await(delegate(List<IntPtr> o) { return o.Count == 1; }, 15000);
            Check(one, "one synthetic Adobe frame produces exactly one overlay",
                  Overlays().Count + " overlay(s)");
            if (!one) return;

            Native.RECT want = a.CanvasRect(0);
            bool exact = Await(delegate(List<IntPtr> o)
            { return o.Count == 1 && Native.RectOf(o[0]).Same(want); }, 5000);
            Check(exact, "overlay lands on the canvas the OWL rule validated",
                  "want " + want + "   got " + First());

            // --- a second document in the SAME frame: Window > Arrange > 2-up ---
            a.SetViewportCount(2);
            Pump(200);
            bool two = Await(delegate(List<IntPtr> o) { return o.Count == 2; }, 6000);
            Check(two, "a second document in the same frame gets its own overlay",
                  Overlays().Count + " overlay(s)");
            Check(two && Covers(a.CanvasRect(0)) && Covers(a.CanvasRect(1)),
                  "both tiled documents are covered exactly",
                  a.CanvasRect(0) + " and " + a.CanvasRect(1));

            // --- a second application window -------------------------------
            b = new SyntheticApp("Harness B", new Rectangle(1060, 120, 820, 640), 1);
            Pump(300);
            bool three = Await(delegate(List<IntPtr> o) { return o.Count == 3; }, 8000);
            Check(three, "a second application window is discovered and tracked",
                  Overlays().Count + " overlay(s)");
            Check(three && Covers(b.CanvasRect(0)),
                  "the second application's canvas is covered exactly", b.CanvasRect(0).ToString());

            // --- moving one frame must not disturb the other ---------------
            Native.RECT bWas = b.CanvasRect(0);
            a.SetBounds(new Rectangle(140, 180, 760, 620));
            Pump(120);
            var sw = Stopwatch.StartNew();
            bool followed = Await(delegate(List<IntPtr> o)
            { return o.Count == 3 && Covers(a.CanvasRect(0)) && Covers(a.CanvasRect(1)); }, 4000);
            sw.Stop();
            Check(followed, "overlays follow one frame being moved and resized",
                  sw.ElapsedMilliseconds + " ms");
            Check(Covers(bWas), "the other application's overlay did not move", bWas.ToString());

            // --- the z-order burial that the first audit found --------------
            //     Reproduced here on the SECOND application, to prove the repair
            //     is per overlay and not a property of there being only one.
            //
            // Sampled at 1 ms. Repair is driven by the window event the raise
            // itself produces and has been measured completing in 32 ms, so a
            // test that waits 30 ms before looking sees a correct z-order and
            // reports success for a burial it never observed. That happened.
            bool buried = false;
            double repairMs = 0;
            IntPtr bOverlay = OverlayOver(b.CanvasRect(0));
            if (bOverlay != IntPtr.Zero)
            {
                Native.SetWindowPos(bOverlay, b.Frame, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE |
                    Native.SWP_NOOWNERZORDER | Native.SWP_NOSENDCHANGING);
                buried = !IsAbove(bOverlay, b.Frame);
            }
            Say("    burial reproduced: " + buried);

            if (!buried)
            {
                Say("    [SKIP] Windows refused to sink it; nothing was proved here.");
            }
            else
            {
                var rec = Stopwatch.StartNew();
                bool repaired = Await(delegate(List<IntPtr> o)
                {
                    IntPtr ovb = OverlayOver(b.CanvasRect(0));
                    return ovb != IntPtr.Zero && IsAbove(ovb, b.Frame);
                }, 5000);
                repairMs = rec.Elapsed.TotalMilliseconds;
                Check(repaired, "a buried overlay repairs itself, with several targets tracked",
                      repairMs.ToString("0", CultureInfo.InvariantCulture) + " ms");
            }
            Check(Covers(a.CanvasRect(0)) && Covers(a.CanvasRect(1)),
                  "the other application's overlays were not disturbed by the repair", null);
            b.ClosePopups();
            Pump(200);

            // --- minimize one frame ----------------------------------------
            a.Minimize();
            bool onlyB = Await(delegate(List<IntPtr> o) { return o.Count == 1; }, 5000);
            Check(onlyB, "minimizing one application hides only its overlays",
                  Overlays().Count + " overlay(s) left");
            Check(onlyB && Covers(b.CanvasRect(0)),
                  "the application still on screen keeps its overlay", null);
            a.Restore();
            Pump(200);
            Check(Await(delegate(List<IntPtr> o) { return o.Count == 3; }, 6000),
                  "restoring it brings its overlays back", Overlays().Count + " overlay(s)");

            // --- destroying a frame ----------------------------------------
            //     Windows destroys a window's OWNED windows with it, and our
            //     overlays are deliberately owned. This is the case that could
            //     leave the utility running with no window to draw into.
            b.Dispose(); b = null;
            Pump(200);
            Check(Await(delegate(List<IntPtr> o) { return o.Count == 2; }, 8000),
                  "closing an application removes exactly its overlays",
                  Overlays().Count + " overlay(s) left");
            Check(Covers(a.CanvasRect(0)) && Covers(a.CanvasRect(1)),
                  "the surviving application is still covered exactly", null);

            // --- and a new one afterwards, which is the recovery path -------
            b = new SyntheticApp("Harness C", new Rectangle(1200, 300, 700, 560), 1);
            Pump(300);
            Check(Await(delegate(List<IntPtr> o)
            { return o.Count == 3 && Covers(b.CanvasRect(0)); }, 8000),
                  "a new application window afterwards is tracked normally",
                  Overlays().Count + " overlay(s)");

            // --- closing a document, not the application ---------------------
            a.SetViewportCount(1);
            Pump(200);
            Check(Await(delegate(List<IntPtr> o) { return o.Count == 2; }, 6000),
                  "closing one document of two drops one overlay",
                  Overlays().Count + " overlay(s)");
        }
        finally
        {
            if (b != null) b.Dispose();
            if (a != null) a.Dispose();
        }

        Pump(300);
        Check(Await(delegate(List<IntPtr> o) { return o.Count == 0; }, 8000),
              "every target closing leaves no overlay behind", Overlays().Count + " overlay(s)");
        bool alive = false;
        try { alive = _nv != null && !_nv.HasExited; } catch { }
        Check(alive, "the utility survives every target disappearing", null);

        // --- global OFF -------------------------------------------------------
        StopNv();
        SyntheticApp c = null;
        try
        {
            c = new SyntheticApp("Harness D", new Rectangle(200, 200, 900, 700), 1);
            Pump(300);
            _nv = Start(harness.Replace("--on ", "--off "));
            Pump(3000);
            Check(Overlays().Count == 0, "global OFF creates no overlay for any target",
                  Overlays().Count + " overlay(s)");
        }
        finally { if (c != null) c.Dispose(); }
        StopNv();
    }

    static string First()
    {
        var o = Overlays();
        return o.Count == 0 ? "(none)" : Native.RectOf(o[0]).ToString();
    }

    /// <summary>Is some visible overlay exactly on this rectangle?</summary>
    static bool Covers(Native.RECT r)
    {
        if (r.IsEmpty) return false;
        foreach (IntPtr h in Overlays()) if (Native.RectOf(h).Same(r)) return true;
        return false;
    }

    static IntPtr OverlayOver(Native.RECT r)
    {
        foreach (IntPtr h in Overlays()) if (Native.RectOf(h).Same(r)) return h;
        return IntPtr.Zero;
    }
}
