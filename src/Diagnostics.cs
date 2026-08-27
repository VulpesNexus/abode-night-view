// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – diagnostics report
// ----------------------------------------------------------------------------
//  What a tester can send us instead of a debugger session.
//
//  Everything here is read-only and non-sensitive: OS build, monitor geometry,
//  which Adobe process was found and where it lives, the window handles and
//  rectangles Abode Night View resolved, and which optional Windows APIs this
//  machine actually has. No document names, no file contents, no identifiers, no
//  window titles from anything other than the target application's frame.
//
//      AbodeNightView.exe --version
//      AbodeNightView.exe --diagnostics       print, and write the report beside the .ini
//      AbodeNightView.exe --diagnostics=PATH  write somewhere specific
//      AbodeNightView.exe --probe             the structural report, per product
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Diag
{
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, IntPtr name, uint type,
                                           int cx, int cy, uint load);

    /// <summary>
    /// Does this executable actually carry its icon? The tray icon is loaded out of
    /// the binary's own Win32 resource, and a failure there is invisible -- it just
    /// falls back to the stock Windows icon, which looks like a build that forgot
    /// its artwork. Probed here so it is reported from any process, not only from
    /// the one running the tray.
    /// </summary>
    private static string IconProbe()
    {
        try
        {
            var sizes = new[] { 16, 32, 256 };
            var parts = new List<string>();
            foreach (int sz in sizes)
            {
                IntPtr h = LoadImage(GetModuleHandleW(null), new IntPtr(32512), 1, sz, sz, 0x8000);
                if (h == IntPtr.Zero)
                    parts.Add(sz + "px FAILED (" + Marshal.GetLastWin32Error() + ")");
                else
                {
                    using (var ico = System.Drawing.Icon.FromHandle(h))
                        parts.Add(sz + "px -> " + ico.Width + "x" + ico.Height);
                }
            }
            return "resource 32512: " + string.Join(", ", parts.ToArray());
        }
        catch (Exception e) { return "probe threw " + e.GetType().Name + ": " + e.Message; }
    }

    /// <summary>
    /// Read from the binary's own version resource rather than declared here, so
    /// --version, the tray header, the About box and Explorer's Details tab all
    /// answer from the same place. AssemblyInfo.cs is that place.
    /// </summary>
    public static string Version { get { return AboutInfo.Version; } }

    /// <summary>Set by the running app so --diagnostics from the tray reports live state.</summary>
    public static Func<string> LiveState;

    private static void Section(TextWriter w, string title)
    {
        w.WriteLine();
        w.WriteLine("-- " + title + " " + new string('-', Math.Max(0, 60 - title.Length)));
    }

    private static void KV(TextWriter w, string k, object v)
    {
        w.WriteLine("  {0,-26} {1}", k, v);
    }

    /// <summary>Probe an optional API without letting a missing export kill the report.</summary>
    private static string Probe(string name, Action call)
    {
        try { call(); return "present"; }
        catch (EntryPointNotFoundException) { return "MISSING on this Windows"; }
        catch (DllNotFoundException) { return "MISSING (dll not present)"; }
        catch (Exception e) { return "present (call returned " + e.GetType().Name + ")"; }
    }

    public static void Write(TextWriter w)
    {
        w.WriteLine("Abode Night View diagnostics");
        w.WriteLine("============================");

        Section(w, "Abode Night View");
        KV(w, "version", Version);
        KV(w, "executable", SafeExePath());
        KV(w, "build (file write time)", SafeExeStamp());
        KV(w, "process architecture", IntPtr.Size == 8 ? "x64 (64-bit)" : "x86 (32-bit)");
        KV(w, "CLR", Environment.Version + " (.NET Framework)");
        KV(w, "elevated", IsElevated());
        KV(w, "settings file", Config.Path);
        KV(w, "settings writable", Config.LastSaveOk ? "yes" : "NO – " + (Config.LastSaveError ?? "not tried yet"));
        KV(w, "settings mode", Config.Portable ? "portable (beside the executable)" : "per-user (AppData)");
        KV(w, "settings imported from", Config.MigratedFrom ?? "(nothing to import)");
        KV(w, "working directory", SafeCwd());
        KV(w, "embedded icon", IconProbe());

        Section(w, "Windows");
        KV(w, "version", Native.OsVersionString());
        KV(w, "build", Native.OsBuild());
        KV(w, "OS architecture", Environment.Is64BitOperatingSystem ? "x64/ARM64 (64-bit)" : "32-bit");
        KV(w, "processor count", Environment.ProcessorCount);
        bool comp = false;
        try { DwmIsCompositionEnabled(out comp); } catch { }
        KV(w, "DWM composition", comp ? "enabled" : "reported disabled");
        KV(w, "DPI awareness applied", Native.DpiAwarenessApplied);
        KV(w, "session", Process.GetCurrentProcess().SessionId);

        Section(w, "Optional API availability");
        KV(w, "SetProcessDpiAwarenessCtx", Probe("SetProcessDpiAwarenessContext",
            () => Native.SetProcessDpiAwarenessContext(IntPtr.Zero)));
        KV(w, "GetDpiForWindow", Probe("GetDpiForWindow",
            () => Native.GetDpiForWindow(IntPtr.Zero)));
        KV(w, "GetDpiForMonitor", Probe("GetDpiForMonitor",
            () => { uint x, y; Native.GetDpiForMonitor(IntPtr.Zero, 0, out x, out y); }));
        KV(w, "SetWindowDisplayAffinity", Probe("SetWindowDisplayAffinity",
            () => Native.SetWindowDisplayAffinity(IntPtr.Zero, 0)));
        KV(w, "WDA_EXCLUDEFROMCAPTURE", Native.OsBuild() >= 19041
            ? "supported (build >= 19041)"
            : "NOT supported on this build – --capture=exclude will do nothing");

        Section(w, "Monitors");
        KV(w, "virtual screen", string.Format(CultureInfo.InvariantCulture, "{0},{1} {2}x{3}",
            Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN)));
        KV(w, "monitor count", Native.GetSystemMetrics(Native.SM_CMONITORS));
        foreach (string line in Monitors()) w.WriteLine("    " + line);

        Section(w, "Rendering");
        w.WriteLine("  Neutral    the shipped filter. A black layered window at alpha a,");
        w.WriteLine("             composited by DWM as out = src*a + dst*(1-a), which for a");
        w.WriteLine("             black source is a per-channel multiply by k = 1-a.");
        w.WriteLine("             No capture, no second render path, no added frame.");
        w.WriteLine("  Greyscale  investigated and not shipped: the only non-capture route");
        w.WriteLine("             tested (Magnification API) did not perform the channel");
        w.WriteLine("             mixing it was asked for, and needs an unsynchronised");
        w.WriteLine("             refresh timer. See measurements/magnification-api.md.");
        w.WriteLine("  Shader     investigated and not shipped: a correct tone curve needs");
        w.WriteLine("             capture plus GPU processing, and therefore a frame of");
        w.WriteLine("             latency by construction.");
        w.WriteLine();

        Section(w, "Adobe applications");
        w.WriteLine("  Detection is by process name plus frame window class, then the");
        w.WriteLine("  structure is validated. There is no version whitelist: an Adobe");
        w.WriteLine("  release newer than this build attaches if it still looks the same.");
        w.WriteLine("  Run --probe for the full structural report.");
        w.WriteLine();

        var detected = TargetRegistry.Discover(TargetRegistry.All);
        foreach (var t in TargetRegistry.All)
        {
            var mine = new List<DetectedFrame>();
            foreach (var d in detected) if (d.Adapter == t) mine.Add(d);

            w.WriteLine("  {0,-13} {1}", t.Id,
                mine.Count == 0 ? "not running" : mine.Count + " window(s)");
            if (mine.Count == 0) continue;

            foreach (var d in mine)
            {
                w.WriteLine("      {0} {1}", d.Label, d.ProductVersion ?? "(version unreadable)");
                w.WriteLine("      pid {0}, frame {1} class {2} rect {3}{4}",
                    d.Pid, Hex(d.Frame), Native.ClassOf(d.Frame), Native.RectOf(d.Frame),
                    Native.IsIconic(d.Frame) ? " MINIMISED" : "");
                w.WriteLine("      dpi {0} ({1}%), monitor {2}",
                    Native.DpiOf(d.Frame), Native.DpiOf(d.Frame) * 100 / 96, MonitorOf(d.Frame));

                var vps = t.Viewports(d.Frame);
                if (vps.Count == 0)
                {
                    // Which of the two failures, rather than the friendlier
                    // guess. See AdobeTarget.Inspect.
                    TargetStatus st = t.Inspect(d.Frame);
                    w.WriteLine("      {0} – nothing is being dimmed for this window.",
                        st == TargetStatus.NoDocument ? "NO DOCUMENT OPEN" : "UNSUPPORTED VERSION");
                    w.WriteLine("      Expected: {0}", t.ExpectedStructure);
                    w.WriteLine("      {0}", st == TargetStatus.NoDocument
                        ? "Open a document and re-run."
                        : "The frame is there and the hierarchy inside it is not. Run --probe.");
                    continue;
                }
                foreach (IntPtr vp in vps)
                {
                    w.WriteLine("      viewport {0} {1} {2}", Hex(vp), Native.ClassOf(vp), Native.RectOf(vp));
                    IntPtr c = t.Canvas(vp);
                    w.WriteLine("        canvas   {0} {1} {2}", Hex(c), Native.ClassOf(c), Native.RectOf(c));
                    IntPtr dd = t.Document(vp);
                    w.WriteLine("        document {0} {1} {2}", Hex(dd), Native.ClassOf(dd), Native.RectOf(dd));
                }
            }
        }

        Section(w, "Live state");
        var live = LiveState;
        if (live == null) w.WriteLine("    (Abode Night View is not running in this process)");
        else
        {
            try { w.WriteLine(live()); }
            catch (Exception e) { w.WriteLine("    (unavailable: " + e.Message + ")"); }
        }

        Section(w, "Settings as loaded");
        foreach (var kv in Config.All()) w.WriteLine("    {0} = {1}", kv.Key, kv.Value);

        w.WriteLine();
        w.WriteLine("End of report.");
    }

    private static string Hex(IntPtr h)
    { return "0x" + h.ToInt64().ToString("X8", CultureInfo.InvariantCulture); }

    private static string MonitorOf(IntPtr h)
    {
        IntPtr mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFOEX.Create();
        if (!Native.GetMonitorInfoW(mon, ref mi)) return "(unknown)";
        return mi.szDevice + " " + mi.rcMonitor;
    }

    private static List<string> Monitors()
    {
        var list = new List<string>();
        Native.MonitorEnumProc cb = (IntPtr mon, IntPtr dc, ref Native.RECT r, IntPtr data) =>
        {
            var mi = Native.MONITORINFOEX.Create();
            string dev = Native.GetMonitorInfoW(mon, ref mi) ? mi.szDevice : "?";
            bool primary = (mi.dwFlags & 1) != 0;
            uint dpi = 96;
            try
            {
                uint x, y;
                if (Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out x, out y) == 0) dpi = x;
            }
            catch { }
            list.Add(string.Format(CultureInfo.InvariantCulture,
                "{0,-14} {1,-22} work {2,-22} dpi {3} ({4}%){5}",
                dev, mi.rcMonitor, mi.rcWork, dpi, dpi * 100 / 96,
                primary ? " PRIMARY" : ""));
            return true;
        };
        try { Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero); }
        catch (Exception e) { list.Add("(enumeration failed: " + e.Message + ")"); }
        return list;
    }

    private static string SafeExePath()
    { try { return typeof(Diag).Assembly.Location; } catch { return "(unknown)"; } }

    private static string SafeExeStamp()
    {
        try { return File.GetLastWriteTime(typeof(Diag).Assembly.Location)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        catch { return "(unknown)"; }
    }

    private static string SafeCwd()
    { try { return Environment.CurrentDirectory; } catch { return "(unknown)"; } }

    private static string IsElevated()
    {
        try
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                    ? "yes (not required – see README)" : "no";
        }
        catch { return "(unknown)"; }
    }
}
