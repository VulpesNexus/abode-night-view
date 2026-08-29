// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – click-through layered tint overlays  (the shipping engine)
// ----------------------------------------------------------------------------
//  What this is
//      One borderless, click-through, layered window per tracked Adobe document
//      viewport, filled with a solid color and blended by DWM.
//
//      With a BLACK fill this computes, per pixel, in the compositor:
//
//          out = src*alpha + dst*(1-alpha)
//              = 0*alpha    + dst*(1-alpha)
//              = dst * k          where k = 1 - alpha
//
//      i.e. exactly the "R' = R*k, G' = G*k, B' = B*k" transform, for free,
//      with no screen capture, no duplicated rendering and no added latency.
//      Measured over 7.6 M pixel pairs on InDesign's GPU-composited canvas and
//      again over 2.8 M on a plain GDI window: DWM composites layered windows in
//      sRGB-encoded values, so this is a plain 8-bit multiply, it does not depend
//      on the target's renderer, and no gamma correction belongs anywhere in the
//      path. See FEASIBILITY.md.
//
//  What it does NOT do
//      Per-channel gain, channel mixing (grayscale), or any non-linear curve.
//      Alpha blending gives you out = k*dst + c*alpha only, with one shared k.
//      See FEASIBILITY.md and measurements/magnification-api.md for what was
//      tried instead and why none of it shipped.
//
//  Document safety
//      This process never opens, scripts, or messages an Adobe application. It
//      calls window-query APIs to read geometry, and positions its own windows.
//
//  Usage
//      AbodeNightView.exe [options]
//        --on / --off                 start switched on/off regardless of settings
//        --schedule=20:00-07:00       switch on and off by the clock
//        --schedule=off
//        --strength=55                dim to 55% (k = 0.45)
//        --region=canvas|document|client|window        applies to every product
//        --region.photoshop=document                   applies to one product
//        --products=indesign,illustrator               only these, this run
//        --zmode=owned|above|topmost
//        --capture=exclude            keep the overlays out of screenshots
//        --pid=1234                   restrict to one process
//        --adapter=id:Name:proc:class register an extra target (test harness)
//
//      Diagnostics: --version --diagnostics --probe[=product] --verify --watch=N
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
//  The overlay window itself
// ---------------------------------------------------------------------------
internal sealed class TintOverlay : Form
{
    private const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
                      WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080,
                      WS_EX_APPWINDOW = 0x00040000;
    private const int WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;

    private Color _tint = Color.Black;
    private int _strength;

    public TintOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;   // we work in physical pixels only
        BackColor = Color.Black;
        Text = "AbodeNV Overlay";

        // Park it off-screen at 1x1 until Sync() gives it the real rectangle, so
        // there is no black flash at the WinForms default position during startup.
        // -32000 is the same coordinate Windows itself parks minimized windows at
        // and is outside every possible virtual desktop, including one whose
        // monitors sit at negative coordinates.
        Bounds = new Rectangle(-32000, -32000, 1, 1);

        // UserPaint + AllPaintingInWmPaint + Opaque means OnPaint below is the ONLY
        // thing that draws, in a single WM_PAINT with no background erase.
        //
        // Opaque on its own -- which is what this was -- suppresses the background
        // erase WITHOUT providing a replacement, so the window painted nothing at
        // all. Its redirection surface stayed empty, and an empty surface blended
        // with LWA_ALPHA is exactly invisible: correct handle, correct rectangle,
        // correct styles, correct alpha, zero pixels. Every structural check passes
        // and the screen is unchanged.
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.Opaque, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint does it all */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var b = new SolidBrush(_tint))
            e.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            cp.ExStyle &= ~WS_EX_APPWINDOW;   // never in Alt+Tab, never in the taskbar
            return cp;
        }
    }

    // The form must never take activation, even transiently.
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        // Belt and braces on top of WS_EX_TRANSPARENT: report every point as
        // "not mine" so hit testing falls straight through to the application.
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public void ApplyLook(Color tint, int strengthPercent)
    {
        bool same = _tint == tint && _strength == strengthPercent;
        _tint = tint; _strength = strengthPercent;
        BackColor = tint;
        byte alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(strengthPercent * 2.55)));
        Native.SetLayeredWindowAttributes(Handle, 0, alpha, Native.LWA_ALPHA);
        if (same) return;                    // nothing to repaint
        Invalidate();
        Update();                            // repaint now, not at the next idle
    }

    /// <summary>
    /// Hide the overlay from screen capture. Needs Windows 10 2004 (build 19041);
    /// on anything older the call fails and the caller is told, rather than the
    /// user believing their screenshots are clean when they are not.
    /// </summary>
    public bool SetCaptureVisible(bool visibleInCaptures)
    {
        try
        {
            bool ok = Native.SetWindowDisplayAffinity(Handle,
                visibleInCaptures ? Native.WDA_NONE : Native.WDA_EXCLUDEFROMCAPTURE);
            if (!ok || visibleInCaptures) return ok;

            // The return value cannot be trusted for this one. Microsoft document
            // that on Windows before 10 version 2004, WDA_EXCLUDEFROMCAPTURE
            // "will behave as if WDA_MONITOR is applied" -- the call SUCCEEDS and
            // silently does something else. Believing it would tell the user their
            // screenshots are clean when they are not, so gate on the build number.
            return Native.OsBuild() >= 19041;
        }
        catch (EntryPointNotFoundException) { return false; }
    }
}

// ---------------------------------------------------------------------------
//  One overlay window and everything latched to the viewport it is covering
// ---------------------------------------------------------------------------
internal sealed class OverlaySlot
{
    public TintOverlay Window;
    public IntPtr Hwnd;                 // captured at creation; see AssertAlive
    public IntPtr OwnedTo = IntPtr.Zero;
    public IntPtr Frame = IntPtr.Zero;
    public IntPtr Viewport = IntPtr.Zero;
    public Native.RECT LastRect;
    public bool Shown;
    public string ZMode = "owned";      // may fall back per slot
    public bool InUse;

    /// <summary>
    /// Set when the application was just activated: re-seat the overlay directly
    /// above its frame once, rather than merely checking that it is somewhere
    /// above it. See AbodeNvContext.Place for why this is done on activation only.
    /// </summary>
    public bool ReseatZ;

    public OverlaySlot(Color tint, int strength, bool visibleInCaptures, out bool captureOk)
    {
        Window = new TintOverlay();
        Window.Show();
        Window.ApplyLook(tint, strength);
        captureOk = Window.SetCaptureVisible(visibleInCaptures);
        Hwnd = Window.Handle;
    }

    /// <summary>
    /// Windows destroys a window's OWNED windows when the owner is destroyed, and
    /// our overlays are deliberately owned by an Adobe frame. So quitting Adobe can
    /// take an overlay with it. Detected rather than assumed: if the handle we were
    /// given no longer exists, the slot is rebuilt.
    /// </summary>
    public bool Alive { get { return Hwnd != IntPtr.Zero && Native.IsWindow(Hwnd); } }

    public void Release()
    {
        InUse = false; Frame = IntPtr.Zero; Viewport = IntPtr.Zero;
        Hide();
        Disown();
    }

    /// <summary>
    /// Drop the owner link.
    ///
    /// Ownership is the only state this program leaves inside another process's
    /// window tree, and setting it was the easy half. A slot that is no longer
    /// attached to anything has no business still being owned by an Adobe frame:
    /// Windows destroys a window's owned windows with it, so a pooled spare left
    /// owned is a window we have volunteered to have destroyed, and OwnedTo --
    /// which --diagnostics prints -- would go on naming a frame we are not on.
    ///
    /// Idempotent and guarded on OwnedTo, because Release() runs on every sync:
    /// once the link is gone this costs a comparison.
    /// </summary>
    public void Disown()
    {
        if (OwnedTo == IntPtr.Zero) return;
        if (Alive) Native.SetWindowLongPtr(Hwnd, Native.GWLP_HWNDPARENT, IntPtr.Zero);
        OwnedTo = IntPtr.Zero;
    }

    public void Hide()
    {
        // Not "if (!Shown)": Show() in the constructor makes the window visible
        // before the first Sync(), so starting up switched off used to leave a
        // shown, never-positioned window behind. Trust the window, not the flag.
        Shown = false;
        if (!Alive) return;
        if (!Native.IsWindowVisible(Hwnd)) return;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_HIDEWINDOW);
    }

    public void Destroy()
    {
        // Before Close(), not after: once the window is gone the handle cannot be
        // used to take the link back off, and closing a window that is still owned
        // by a foreign frame is the one ordering that leaves the link behind.
        Disown();
        try { if (Alive) Window.Close(); } catch { }
        try { Window.Dispose(); } catch { }
        Hwnd = IntPtr.Zero;
    }
}

// ---------------------------------------------------------------------------
//  Per-frame tracking state
// ---------------------------------------------------------------------------
internal sealed class FrameTrack
{
    public AdobeTarget Adapter;
    public IntPtr Frame;
    public uint Pid;
    public string Label = "";
    public string Version = "";

    public List<IntPtr> Viewports = new List<IntPtr>();
    public List<Native.RECT> LastRects = new List<Native.RECT>();
    public int Misses;
    public int SinceFullScan = int.MaxValue;   // force a scan the first time
    public bool EverResolved;
}

// ---------------------------------------------------------------------------
//  Controller: discovery, tracking, hotkeys, tray
// ---------------------------------------------------------------------------
internal sealed class AbodeNvContext : ApplicationContext
{
    private readonly NotifyIcon _tray = new NotifyIcon();
    private readonly Timer _resync = new Timer();
    private readonly HotkeyManager _hotkeys;

    private readonly List<IntPtr> _hooks = new List<IntPtr>();
    private readonly Dictionary<uint, IntPtr> _procHooks = new Dictionary<uint, IntPtr>();
    private Native.WinEventProc _cb;   // must stay rooted or the hook faults

    private readonly List<OverlaySlot> _slots = new List<OverlaySlot>();
    private readonly Dictionary<IntPtr, FrameTrack> _tracks = new Dictionary<IntPtr, FrameTrack>();
    private List<DetectedFrame> _detected = new List<DetectedFrame>();

    // ---- state the user can change -------------------------------------------
    private bool _enabled = true;
    private int _strength = 55;
    private string _mode = Modes.Neutral;
    private NightSchedule _schedule = new NightSchedule();
    private bool _visibleInCaptures = true;
    // "owned" by default: measured at zero z-order transitions over 25 s of editing,
    // against 8 fall-behind/raise pairs for "above" over the same kind of session.
    private string _wantZmode = "owned";
    private readonly Dictionary<string, bool> _productOn =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _productRegion =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ---- state derived at run time --------------------------------------------
    private readonly List<AdobeTarget> _adapters = new List<AdobeTarget>();
    private uint _pinnedPid;
    private bool _captureAffinityFailed;
    private int _liveOverlays;

    // What the hover text was last built from. The tooltip used to be refreshed
    // only on user actions, so opening a document in an already-running product
    // left it reading "no target" until the next click. These two are the cheap
    // test for "the answer may have changed"; the survey behind the text is only
    // run when one of them moves.
    private int _tipLive = -1, _tipDetected = -1;
    private int _rebuiltSlots;

    /// <summary>
    /// What the schedule last said, so it can be EDGE triggered. Null while
    /// there is no schedule. See NightSchedule: a level-triggered schedule
    /// would undo every manual toggle within 250 ms, which makes the tray item
    /// and the shortcut look broken for half the night.
    /// </summary>
    private bool? _scheduleSaid;

    /// <summary>--on / --off was on the command line, so that state wins over
    /// the schedule for this launch, until the range next begins or ends.</summary>
    private bool _stateFromCommandLine;

    private const int MissesBeforeHiding = 4;   // ~1 s at the 250 ms safety-net rate
    private const int FullScanEvery = 8;        // ~2 s; events do the real work

    public AbodeNvContext(string[] argv)
    {
        _adapters.AddRange(TargetRegistry.All);

        Config.Load();
        LoadConfig();

        // Materialise the settings file on first run, and immediately after any
        // migration, so the migration is not re-done on every launch and so the
        // file the user is told about actually exists. Written BEFORE the
        // command line is applied, so --strength=90 for one session does not
        // quietly become the new default.
        //
        // DroppedHotkeys is in that list for the same reason MigratedFrom is: if
        // the withdrawal is not written down, it happens again on the next
        // launch and the user is told about it again, every time, for as long as
        // they never change a setting.
        if (Config.MigratedFrom != null || Config.DroppedHotkeys > 0 ||
            !File.Exists(Config.Path)) SaveConfig();

        ParseArgs(argv);

        _hotkeys = BuildHotkeys();
        BuildTray();
        InstallHooks();

        // The desktop can change under us in ways no window event describes:
        // resolution and scaling changes, a monitor arriving or leaving, and the
        // frame buffer being rebuilt after sleep or a graphics-driver reset. Each
        // of these invalidates every rectangle we hold.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDesktopChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnDesktopChanged;

        // A schedule is a statement about the wall clock, and the wall clock
        // moves under us: daylight saving, a manual correction, a laptop waking
        // up and resynchronizing with a time server. Re-evaluate rather than
        // waiting for a boundary that has already silently gone past.
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;

        _resync.Interval = 250;              // cheap safety net; hooks do the real work
        _resync.Tick += OnTimer;
        _resync.Start();

        Sync(true);
        Diag.LiveState = DescribeLiveState;

        // Before the balloon, so what it announces is the state the user will
        // actually find: opening the laptop at 3am with a night schedule set
        // should come up already dimmed, not dimmed a moment after the
        // notification said it was off.
        ApplySchedule(true);
        _stateFromCommandLine = false;

        Announce(true);
    }

    // ------------------------------------------------------------ appearance

    /// <summary>
    /// Black, and only black. A layered window composites as
    /// out = src*a + dst*(1-a), so a black source is a pure per-channel multiply
    /// by k = 1-a: hue, saturation and relative contrast are all preserved and
    /// only the level moves. Any other source color turns the same expression
    /// into "multiply by k, then ADD light", which is what the removed Warm mode
    /// did -- see docs/design-notes.md "Why Warm was removed" for the measurements.
    /// </summary>
    private static Color TintColor() { return Color.Black; }

    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

    private void Restyle()
    {
        Color c = TintColor();
        foreach (var s in _slots) if (s.Alive) s.Window.ApplyLook(c, _strength);
        UpdateTrayText();
        SaveConfig();
    }

    // -------------------------------------------------------------- schedule

    /// <summary>
    /// Bring the on/off state into line with the clock.
    ///
    /// <paramref name="reseed"/> is the difference between "the schedule has
    /// just been set up, make it so" and "another quarter of a second has
    /// passed, has a boundary gone by?". Only the second one is edge triggered,
    /// and that is what leaves a manual override standing: switching it off by
    /// hand at 01:00 stays off until 07:00, because nothing between now and
    /// then CHANGES the schedule's answer.
    /// </summary>
    private void ApplySchedule(bool reseed)
    {
        if (!_schedule.Active) { _scheduleSaid = null; return; }

        bool want = _schedule.Covers(DateTime.Now);

        if (reseed)
        {
            _scheduleSaid = want;
            // --on / --off is an explicit instruction for this launch and beats
            // the stored schedule until the range next begins or ends.
            if (_stateFromCommandLine) return;
        }
        else
        {
            if (_scheduleSaid.HasValue && _scheduleSaid.Value == want) return;
            _scheduleSaid = want;
        }

        if (_enabled == want) return;
        _enabled = want;
        Sync(true); UpdateTrayText(); SaveConfig();
    }

    private void SetSchedule(bool active, ClockTime from, ClockTime to)
    {
        _schedule.Active = active;
        _schedule.From = from;
        _schedule.To = to;
        SaveConfig();
        ApplySchedule(true);
        UpdateTrayText();
    }

    private void OnTimeChanged(object sender, EventArgs e) { ApplySchedule(true); }

    // -------------------------------------------------------------- products

    private bool ProductOn(AdobeTarget t)
    {
        bool v;
        return _productOn.TryGetValue(t.Id, out v) ? v : t.DefaultEnabled;
    }

    private void SetProductOn(AdobeTarget t, bool on)
    {
        _productOn[t.Id] = on;
        Sync(true);
        UpdateTrayText();
        SaveConfig();
    }

    private string RegionFor(AdobeTarget t)
    {
        string v;
        if (_productRegion.TryGetValue(t.Id, out v))
        {
            string norm = Region.Normalize(v);
            if (norm != null) return norm;
        }
        return t.DefaultRegion;
    }

    private void SetRegionFor(AdobeTarget t, string region)
    {
        _productRegion[t.Id] = region;
        foreach (var ft in _tracks.Values) ft.SinceFullScan = int.MaxValue;
        Sync(true);
        SaveConfig();
    }

    /// <summary>Adapters the user currently wants tracked. Enumerated a lot; kept cheap.</summary>
    private List<AdobeTarget> ActiveAdapters()
    {
        var list = new List<AdobeTarget>();
        foreach (var t in _adapters) if (ProductOn(t)) list.Add(t);
        return list;
    }

    // ------------------------------------------------------------- tracking

    private void InstallHooks()
    {
        if (_cb == null) _cb = OnWinEvent;

        // System-wide: foreground changes, move/size drags, minimize/restore.
        // A window being created or destroyed anywhere is how we notice an Adobe
        // application starting or quitting without polling the process list.
        _hooks.Add(Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _cb, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS));
    }

    /// <summary>
    /// A process-scoped hook carries a pid, so it dies with the process it was
    /// created for. Without re-arming, quitting and restarting an Adobe application
    /// left Abode NV tracking on the 250 ms timer alone -- which is exactly the
    /// ~200 ms undimmed gap the owned-window work was done to remove.
    ///
    /// One hook per tracked process, removed when the process stops being tracked,
    /// so having four Adobe applications open does not mean four times the events
    /// from each of them.
    /// </summary>
    private void SyncProcessHooks()
    {
        var want = new HashSet<uint>();
        foreach (var d in _detected) want.Add(d.Pid);

        var stale = new List<uint>();
        foreach (var kv in _procHooks) if (!want.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (uint pid in stale)
        {
            Native.UnhookWinEvent(_procHooks[pid]);
            _procHooks.Remove(pid);
            TargetRegistry.Forget(pid);
        }

        foreach (uint pid in want)
        {
            if (_procHooks.ContainsKey(pid)) continue;
            // Geometry and structure, this process only. LOCATIONCHANGE is chatty,
            // so the handler filters to OBJID_WINDOW and to windows we track.
            IntPtr h = Native.SetWinEventHook(
                Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _cb, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            if (h != IntPtr.Zero) _procHooks[pid] = h;
        }
    }

    private void OnTimer(object sender, EventArgs e)
    {
        // Two DateTime comparisons, and only when a schedule exists. Putting the
        // clock check here rather than on a timer of its own means there is one
        // periodic thing in this process, not two.
        ApplySchedule(false);

        // The timer is the safety net, and it is also the only thing that notices a
        // product being launched when its first window happens not to raise an event
        // we hear. Rediscovery on the timer is one EnumWindows pass.
        Sync(true);
    }

    private void OnDesktopChanged(object sender, EventArgs e)
    {
        // Everything cached is in screen coordinates, so a topology change makes
        // all of it meaningless at once. Drop it and re-resolve from scratch.
        foreach (var ft in _tracks.Values)
        {
            ft.Viewports.Clear(); ft.LastRects.Clear();
            ft.SinceFullScan = int.MaxValue; ft.Misses = 0;
        }
        // Ownership is deliberately NOT touched here. It used to be cleared -- the
        // field only, not the link -- to force the next Place() to re-own. That is
        // no longer needed now that Place() asks the window who owns it, and doing
        // it anyway would be worse than useless: a display change is not a change
        // of owner, so dropping the link would give up the one guarantee that
        // survives the topology change and re-take it a moment later.
        foreach (var s in _slots) { s.Shown = false; s.LastRect = new Native.RECT(); }
        Sync(true);
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnDesktopChanged(sender, EventArgs.Empty);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint th, uint t)
    {
        if (idObject != Native.OBJID_WINDOW) return;   // ignore caret/menu/cursor noise

        switch (ev)
        {
            case Native.EVENT_SYSTEM_MOVESIZESTART:
            case Native.EVENT_SYSTEM_MOVESIZEEND:
                // Previously this hid the overlay for the whole drag, which reads
                // as the filter switching off and on again. Track it instead --
                // LOCATIONCHANGE fires continuously during the drag.
                if (_tracks.ContainsKey(hwnd)) Sync(false);
                return;

            case Native.EVENT_OBJECT_DESTROY:
            case Native.EVENT_OBJECT_HIDE:
                // ONLY when the window that went away is one we are tracking.
                //
                // This used to invalidate on every show/hide anywhere in the
                // application. Dragging a frame or editing text spawns transient
                // children constantly, so the cache was thrown away many times a
                // second and the target re-resolved from scratch each time --
                // occasionally onto a different window, which moves the overlay and
                // shows bare white page underneath. That was the flicker on every
                // click.
                if (_tracks.ContainsKey(hwnd)) { Sync(true); return; }
                if (Tracked(hwnd)) { Invalidate(hwnd); Sync(false); }
                return;

            case Native.EVENT_OBJECT_SHOW:
                // A window merely appearing never invalidates a still-valid target,
                // and re-scanning on it is what made editing flicker. But a new
                // top-level window of a class we recognize IS how a product being
                // launched, or a second document being tiled, first becomes visible.
                if (Native.GetParent(hwnd) == IntPtr.Zero) Sync(true);
                return;

            case Native.EVENT_OBJECT_LOCATIONCHANGE:
                // Only a tracked frame or a tracked viewport can change our
                // rectangles. Everything else moving inside Adobe is none of our
                // business, and there is a great deal of it.
                if (!_tracks.ContainsKey(hwnd) && !Tracked(hwnd)) return;
                Sync(false);
                return;

            case Native.EVENT_SYSTEM_FOREGROUND:
                // Activating an application raises its frame together with every
                // window it owns -- including ours, which Windows puts at the TOP
                // of that group, above the application's own floating panels and
                // toolbars. Left alone, those get dimmed for as long as the
                // application stays active. Re-seat once, here, where it costs one
                // SetWindowPos per activation and nothing at all while editing.
                foreach (var s2 in _slots) s2.ReseatZ = true;
                Sync(false);
                return;
        }
        Sync(false);
    }

    private bool Tracked(IntPtr hwnd)
    {
        foreach (var ft in _tracks.Values)
            if (ft.Viewports.Contains(hwnd)) return true;
        return false;
    }

    private void Invalidate(IntPtr viewport)
    {
        foreach (var ft in _tracks.Values)
            if (ft.Viewports.Contains(viewport)) ft.SinceFullScan = int.MaxValue;
    }

    // ------------------------------------------------------------------ sync

    /// <summary>
    /// Reposition (or hide) every overlay to match its viewport.
    /// <paramref name="rediscover"/> costs one EnumWindows pass and is how a
    /// product starting or quitting is noticed; the geometry-only path skips it.
    /// </summary>
    private void Sync(bool rediscover)
    {
        if (!_enabled) { ReleaseAll(); RefreshTrayIfChanged(); return; }

        if (rediscover) Rediscover();

        var wanted = new List<KeyValuePair<FrameTrack, Native.RECT>>();
        var wantedVp = new List<IntPtr>();

        foreach (var d in _detected)
        {
            FrameTrack ft;
            if (!_tracks.TryGetValue(d.Frame, out ft)) continue;

            if (!Native.IsWindow(d.Frame)) continue;
            if (Native.IsIconic(d.Frame) || !Native.IsWindowVisible(d.Frame)) continue;

            Native.RECT client = Native.ClientRectOnScreen(d.Frame);
            if (client.IsEmpty) continue;

            string region = RegionFor(d.Adapter);
            var rects = new List<Native.RECT>();
            var vps = new List<IntPtr>();

            if (region == Region.Window || region == Region.Client)
            {
                rects.Add(region == Region.Window ? Native.RectOf(d.Frame) : client);
                vps.Add(d.Frame);
            }
            else
            {
                foreach (IntPtr vp in ViewportsOf(ft))
                {
                    Native.RECT r = d.Adapter.RectOf(d.Frame, vp, region);
                    if (r.IsEmpty) continue;
                    rects.Add(r); vps.Add(vp);
                }
            }

            if (rects.Count == 0)
            {
                // Don't blink out on a single failed resolve. A tab switch or a
                // panel toggle invalidates the cached handle and the next lookup
                // can miss once while the application rebuilds its view; hiding on
                // that first miss is visible as a flash. Hold the last good
                // rectangles briefly instead.
                if (ft.EverResolved && ft.LastRects.Count > 0 && ++ft.Misses <= MissesBeforeHiding)
                {
                    for (int i = 0; i < ft.LastRects.Count; i++)
                    {
                        wanted.Add(new KeyValuePair<FrameTrack, Native.RECT>(ft, ft.LastRects[i]));
                        wantedVp.Add(i < ft.Viewports.Count ? ft.Viewports[i] : d.Frame);
                    }
                }
                continue;
            }

            ft.Misses = 0; ft.EverResolved = true;
            ft.LastRects.Clear();

            for (int i = 0; i < rects.Count; i++)
            {
                Native.RECT r = rects[i];
                // Clip to the application client area so a stale viewport rect can
                // never spill out over the desktop or another application.
                r.Left = Math.Max(r.Left, client.Left); r.Top = Math.Max(r.Top, client.Top);
                r.Right = Math.Min(r.Right, client.Right); r.Bottom = Math.Min(r.Bottom, client.Bottom);
                if (r.IsEmpty) continue;
                ft.LastRects.Add(r);
                wanted.Add(new KeyValuePair<FrameTrack, Native.RECT>(ft, r));
                wantedVp.Add(vps[i]);
            }
        }

        Assign(wanted, wantedVp);
        RefreshTrayIfChanged();
    }

    /// <summary>
    /// Rebuild the hover text when, and only when, the thing it describes has
    /// moved: the number of overlays on screen, or the number of frames found.
    /// Between those the answer cannot have changed, and the survey behind the
    /// text is not free.
    /// </summary>
    private void RefreshTrayIfChanged()
    {
        int detected = _detected == null ? 0 : _detected.Count;
        if (_liveOverlays == _tipLive && detected == _tipDetected) return;
        _tipLive = _liveOverlays; _tipDetected = detected;
        UpdateTrayText();
    }

    /// <summary>One EnumWindows pass; adds and removes frames, and re-arms hooks.</summary>
    private void Rediscover()
    {
        var found = TargetRegistry.Discover(ActiveAdapters());

        if (_pinnedPid != 0)
            found.RemoveAll(delegate(DetectedFrame d) { return d.Pid != _pinnedPid; });

        _detected = found;

        var live = new HashSet<IntPtr>();
        foreach (var d in found)
        {
            live.Add(d.Frame);
            FrameTrack ft;
            if (!_tracks.TryGetValue(d.Frame, out ft))
            {
                ft = new FrameTrack();
                _tracks[d.Frame] = ft;
            }
            ft.Adapter = d.Adapter; ft.Frame = d.Frame; ft.Pid = d.Pid;
            ft.Label = d.Label; ft.Version = d.ProductVersion ?? "";
        }

        var gone = new List<IntPtr>();
        foreach (var key in _tracks.Keys) if (!live.Contains(key)) gone.Add(key);
        foreach (var key in gone) _tracks.Remove(key);

        SyncProcessHooks();
    }

    /// <summary>
    /// Cached viewport handles, revalidated cheaply every call and re-enumerated
    /// only when something says they might have changed. Enumerating an Adobe
    /// frame's descendants is 200-650 windows; doing that per sync per application
    /// is the difference between an idle utility and a busy one.
    /// </summary>
    private List<IntPtr> ViewportsOf(FrameTrack ft)
    {
        bool ok = ft.Viewports.Count > 0;
        if (ok)
        {
            foreach (IntPtr h in ft.Viewports)
            {
                if (Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.RectOf(h).IsEmpty) continue;
                ok = false; break;
            }
        }

        if (ok && ++ft.SinceFullScan < FullScanEvery) return ft.Viewports;

        ft.SinceFullScan = 0;
        var fresh = ft.Adapter.Viewports(ft.Frame);
        if (fresh.Count > 0 || !ok) ft.Viewports = fresh;
        return ft.Viewports;
    }

    // ------------------------------------------------------------- overlays

    private void ReleaseAll()
    {
        foreach (var s in _slots) if (s.InUse || s.Shown) s.Release();
        _liveOverlays = 0;
    }

    private OverlaySlot SlotFor(IntPtr frame, IntPtr viewport)
    {
        // Prefer the slot that was already on this exact viewport: keeping the same
        // HWND on the same rectangle means no re-owning, no re-showing, and no
        // one-frame gap when nothing has actually moved.
        foreach (var s in _slots)
            if (!s.InUse && s.Alive && s.Frame == frame && s.Viewport == viewport) return s;
        foreach (var s in _slots)
            if (!s.InUse && s.Alive && s.Frame == frame) return s;
        foreach (var s in _slots)
            if (!s.InUse && s.Alive) return s;

        bool captureOk;
        var made = new OverlaySlot(TintColor(), _strength, _visibleInCaptures, out captureOk);
        if (!captureOk && !_visibleInCaptures) _captureAffinityFailed = true;
        made.ZMode = _wantZmode;
        _slots.Add(made);
        return made;
    }

    private void Assign(List<KeyValuePair<FrameTrack, Native.RECT>> wanted, List<IntPtr> viewports)
    {
        // A slot whose window Windows destroyed under us (its owner quit) is
        // rebuilt rather than used. Counted, because silently recreating windows
        // in a loop is how a utility ends up burning a handle a second.
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (_slots[i].Alive) continue;
            _slots[i].Destroy();
            _slots.RemoveAt(i);
            _rebuiltSlots++;
        }
        foreach (var s in _slots) s.InUse = false;

        var used = new List<OverlaySlot>();
        for (int i = 0; i < wanted.Count; i++)
        {
            FrameTrack ft = wanted[i].Key;
            OverlaySlot slot = SlotFor(ft.Frame, viewports[i]);
            slot.InUse = true;
            used.Add(slot);
            Place(slot, ft, viewports[i], wanted[i].Value);
        }

        foreach (var s in _slots) if (!s.InUse) s.Release();
        _liveOverlays = used.Count;

        // Keep the pool proportional to what is actually in use. One spare absorbs
        // the common churn (a dialog, a tab switch) without a create/destroy cycle;
        // beyond that the windows go away.
        int keep = Math.Max(1, used.Count + 1);
        for (int i = _slots.Count - 1; i >= 0 && _slots.Count > keep; i--)
        {
            if (_slots[i].InUse) continue;
            _slots[i].Destroy();
            _slots.RemoveAt(i);
        }
    }

    private void Place(OverlaySlot slot, FrameTrack ft, IntPtr viewport, Native.RECT r)
    {
        slot.Frame = ft.Frame; slot.Viewport = viewport;
        bool reseatRequested = slot.ReseatZ;
        slot.ReseatZ = false;

        // Z-order has to be re-checked every time, not latched: activating the
        // application raises it above the overlay, so "set it once at startup"
        // means the overlay is correct until the first click into the document and
        // buried afterwards.
        //
        // But the test is "is the overlay ABOVE the frame", not "is it DIRECTLY
        // above the frame". Requiring adjacency makes every transient window that
        // appears between them -- tooltips, panel flyouts, the application's own
        // popups -- look like a z-order fault, and each spurious correction is a
        // visible flash.
        IntPtr after;
        bool zNeedsWork;

        if (slot.ZMode == "owned")
        {
            // Make the overlay an OWNED window of the application frame. Windows
            // then keeps an owned window above its owner and raises the pair
            // together, so activating the application cannot produce even the
            // one-frame gap that chasing the z-order leaves behind. Ownership is
            // not parenting: it sets the owner of a top-level window and does not
            // attach input queues.
            //
            // Ask the window, not the bookkeeping field. Window handles are
            // recycled, so a stale OwnedTo can match a BRAND NEW frame that Windows
            // happened to hand a destroyed one's handle -- and the re-own would
            // then be skipped for a window we do not actually own. One GetWindow
            // call, on a path that already makes several.
            if (Native.GetWindow(slot.Hwnd, Native.GW_OWNER) != ft.Frame)
            {
                Native.SetWindowLongPtr(slot.Hwnd, Native.GWLP_HWNDPARENT, ft.Frame);
                if (Native.GetWindow(slot.Hwnd, Native.GW_OWNER) == ft.Frame)
                    slot.OwnedTo = ft.Frame;
                else
                {
                    // Refused. Drop whatever link is still installed rather than
                    // chasing the z-order by hand with another frame's owner set.
                    slot.Disown();
                    slot.ZMode = "above";
                }

                // Take ownership and then SIT DOWN: place the overlay directly
                // above the frame, below everything else the application owns.
                //
                // Otherwise a popup that already existed when we attached stays
                // below us until the next time the user activates the application,
                // because the re-seat is driven by the foreground event and no
                // foreground event is coming -- the application is already
                // foreground. Reproduced against Acrobat's floating page toolbar:
                // start Abode NV while Acrobat is in front with the toolbar
                // showing, and the toolbar is dimmed with the page.
                reseatRequested = true;
            }

            // Ownership is not the whole invariant, which is what this used to
            // assume by hard-coding zNeedsWork = false here.
            //
            // "An owned window is above its owner" is enforced when the OWNER is
            // activated -- the case that used to flicker, and the case ownership
            // solves completely. It is not enforced when the owner is re-ordered
            // some other way. Showing another owned window raises the owner without
            // taking the overlay with it, and the overlay is then BEHIND the
            // application with nothing in the code that would ever notice: the
            // dimming silently stops until something happens to raise it again.
            // Reproduced in Audit.exe with SWP_NOOWNERZORDER.
            //
            // So the invariant is checked every sync. The repair inserts the
            // overlay DIRECTLY above the frame rather than at the top, so anything
            // legitimately between them -- menus, dialogs, floating panels -- stays
            // above it.
            //
            // Three separate conditions, and they are not the same question:
            //   - not above the owner at all      -> the burial defect
            //   - a FOREIGN window in between     -> we are dimming somebody else
            //   - just activated                  -> re-seat below the owner's own
            //                                        floating panels, once
            // What is deliberately NOT here is "not adjacent to the owner", checked
            // continuously. That was measured as the flicker cause: the owner's
            // transient windows appear between the two many times a second while
            // you edit, and every correction is a visible flash.
            IntPtr abv = Native.GetWindow(ft.Frame, Native.GW_HWNDPREV);
            after = abv == IntPtr.Zero ? Native.HWND_TOP : abv;
            bool reseat = reseatRequested && after != slot.Hwnd;
            zNeedsWork = !IsAbove(slot.Hwnd, ft.Frame)
                         || Sandwiched(slot.Hwnd, ft.Frame, ft.Pid)
                         || reseat;
        }
        else if (slot.ZMode == "topmost")
        {
            after = Native.HWND_TOPMOST;
            zNeedsWork = !slot.Shown;
        }
        else
        {
            zNeedsWork = !IsAbove(slot.Hwnd, ft.Frame) || Sandwiched(slot.Hwnd, ft.Frame, ft.Pid);
            IntPtr above = Native.GetWindow(ft.Frame, Native.GW_HWNDPREV);
            after = above == IntPtr.Zero ? Native.HWND_TOP : above;
        }

        bool visible = Native.IsWindowVisible(slot.Hwnd);
        if (!slot.Shown || !visible || !r.Same(slot.LastRect) || zNeedsWork)
        {
            uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER |
                         Native.SWP_NOSENDCHANGING;
            if (!visible) flags |= Native.SWP_SHOWWINDOW;   // only when it truly is hidden
            if (!zNeedsWork) flags |= Native.SWP_NOZORDER;

            Native.SetWindowPos(slot.Hwnd, zNeedsWork ? after : IntPtr.Zero,
                r.Left, r.Top, r.W, r.H, flags);

            slot.LastRect = r; slot.Shown = true;
        }
    }

    private static uint _selfPid;

    /// <summary>
    /// Is some OTHER application's window sandwiched between this overlay and the
    /// window it belongs to?
    ///
    /// "Above my owner" is necessary but not sufficient once more than one Adobe
    /// application is tracked. Two maximized applications on the same monitor
    /// overlap completely; if InDesign's overlay is left above Illustrator's frame
    /// -- which it will be, because activating Illustrator raises Illustrator but
    /// nothing lowers a stale overlay that is still legitimately above ITS owner --
    /// then Illustrator's canvas is dimmed twice and the utility looks broken.
    /// Measured, before this existed: Illustrator's canvas came out at k = 0.176
    /// against a requested 0.451, with every structural check passing.
    ///
    /// The test is deliberately NOT "is the overlay adjacent to its owner". That
    /// was tried and rejected: the owner's own transient windows -- tooltips, panel
    /// flyouts, in-canvas editors -- constantly appear between the two, and
    /// correcting for each of them is a visible flash on every click. Windows
    /// belonging to the owner, and our own overlays, are therefore ignored here;
    /// only a foreign window is a fault.
    ///
    /// Cost: normally one GetWindow step, because the overlay normally IS adjacent
    /// to its owner.
    /// </summary>
    private static bool Sandwiched(IntPtr overlay, IntPtr frame, uint framePid)
    {
        if (_selfPid == 0)
            _selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        int guard = 0;
        for (IntPtr h = Native.GetWindow(overlay, Native.GW_HWNDNEXT);
             h != IntPtr.Zero && guard++ < 4096;
             h = Native.GetWindow(h, Native.GW_HWNDNEXT))
        {
            if (h == frame) return false;                  // reached the owner: clean
            if (!Native.IsWindowVisible(h)) continue;
            if (Native.IsCloaked(h)) continue;             // in the z-order, not on the screen
            if (Native.RectOf(h).IsEmpty) continue;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == framePid || pid == _selfPid) continue;
            return true;
        }
        return false;   // owner not found below us; IsAbove already covers that case
    }

    /// <summary>True if <paramref name="a"/> is anywhere above <paramref name="b"/> in the z-order.</summary>
    private static bool IsAbove(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        // Walk down from a. Adjacent is the normal case, so this is one step.
        for (IntPtr h = a; h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDNEXT))
            if (h == b) return true;
        return false;
    }

    // -------------------------------------------------------------- hotkeys

    private const int HkToggle = 1, HkBrighter = 2, HkDarker = 3, HkQuit = 4;

    private HotkeyManager BuildHotkeys()
    {
        var mgr = new HotkeyManager(OnHotkey);

        // Nothing is bound out of the box, and the empty string is that stated
        // rather than implied. Hotkeys.cs holds the full reasoning; the short
        // version is that RegisterHotKey takes a key away from every program on
        // the machine, and no combination is simultaneously free of AltGr, free
        // inside Adobe, and present on every keyboard.
        //
        // The labels do not repeat the product name. They are read inside a
        // window already titled "Abode Night View (Shortcuts)", where "Toggle
        // Abode Night View" says the same thing twice in one glance.
        mgr.Add(new HotkeyBinding(HkToggle,   "hotkey.toggle",   "Toggle",         "", true));
        mgr.Add(new HotkeyBinding(HkBrighter, "hotkey.brighter", "Brighter (-5%)", "", false));
        mgr.Add(new HotkeyBinding(HkDarker,   "hotkey.darker",   "Darker (+5%)",   "", false));
        mgr.Add(new HotkeyBinding(HkQuit,     "hotkey.quit",     "Quit",           "", true));

        foreach (var b in mgr.Bindings)
        {
            // A hand-edited or corrupted line leaves the action unbound rather
            // than crashing or silently binding something else. There is no
            // default to fall back to any more, and the editor is one click
            // away in the tray.
            HotkeySpec spec; string err;
            HotkeySpec.TryParse(Config.Str(b.Key, b.Default), out spec, out err);
            b.Spec = spec;
        }
        mgr.RegisterAll();
        return mgr;
    }

    private string ToggleKeyText()
    {
        var b = _hotkeys == null ? null : _hotkeys.Find("hotkey.toggle");
        return b == null || b.Unset ? "the tray icon" : b.Spec.ToString();
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HkToggle: ToggleEnabled(); break;
            case HkBrighter: _strength = Clamp(_strength - 5, 0, 90); Restyle(); break;
            case HkDarker: _strength = Clamp(_strength + 5, 0, 90); Restyle(); break;
            case HkQuit: Quit(); break;
        }
    }

    /// <summary>
    /// Switching it by hand: the tray item, a double-click on the icon, or the
    /// shortcut. Each of those gets a notification, because the only other
    /// feedback is looking at the screen and deciding whether it changed, which
    /// is the judgment a dimmer exists to make harder.
    ///
    /// The clock does NOT come through here. ApplySchedule moves the same state
    /// without announcing it: a scheduled change at 03:00 is not something to
    /// raise a toast about.
    /// </summary>
    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        Sync(true); UpdateTrayText(); SaveConfig();
        Announce(false);
    }

    private void EditHotkeys()
    {
        using (var dlg = new HotkeyEditor(_hotkeys))
        {
            dlg.Icon = AbodeNvMain.AppIcon(32);
            if (dlg.ShowDialog() != DialogResult.OK) return;
        }
        foreach (var b in _hotkeys.Bindings)
            Config.Set(b.Key, b.Spec.IsEmpty ? "" : b.Spec.ToString());
        SaveConfig();
        UpdateTrayText();
    }

    // ------------------------------------------------------------------ tray

    private ToolStripMenuItem _miEnabled, _miTargets, _miRegion, _miStrength, _miSchedule;

    /// <summary>
    /// Nothing here stores a checkmark. Every Checked flag is computed from the
    /// live state at the moment the menu opens, and the two dynamic submenus are
    /// rebuilt from the live state when they open.
    ///
    /// This is the fix for the bug where Strength, Mode and Target all had correct
    /// runtime values after a restart and the menu showed nothing ticked: the
    /// checkmarks were being set once, at construction, from values that had not
    /// been loaded yet. A menu that derives its state cannot drift from it.
    /// </summary>
    private void BuildTray()
    {
        // Before Visible: a NotifyIcon with no Icon is added to the notification
        // area as a blank slot, and the shell does not always redraw it when one
        // arrives afterwards.
        ShowStateIcon();
        _tray.Visible = true;

        var menu = new ContextMenuStrip();
        TrayMenuStyle.Apply(menu);

        var header = new ToolStripMenuItem(AboutInfo.ProductName + " " + Diag.Version);
        header.Click += delegate { ShowAbout(); };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        _miEnabled = new ToolStripMenuItem(TrayState.EnabledText(_enabled));
        _miEnabled.Click += delegate { ToggleEnabled(); };
        menu.Items.Add(_miEnabled);

        // Next to Enabled rather than down with the settings: they are the same
        // switch, one thrown by hand and one thrown by the clock.
        _miSchedule = new ToolStripMenuItem("Schedule");
        _miSchedule.DropDownOpening += delegate { BuildScheduleMenu(); };
        _miSchedule.DropDownItems.Add(new ToolStripMenuItem("(scanning)") { Enabled = false });
        menu.Items.Add(_miSchedule);

        menu.Items.Add(new ToolStripSeparator());

        _miTargets = new ToolStripMenuItem("Targets");
        _miTargets.DropDownOpening += delegate { BuildTargetsMenu(); };
        _miTargets.DropDownItems.Add(new ToolStripMenuItem("(scanning)") { Enabled = false });
        menu.Items.Add(_miTargets);

        _miRegion = new ToolStripMenuItem("Region");
        _miRegion.DropDownOpening += delegate { BuildRegionMenu(); };
        _miRegion.DropDownItems.Add(new ToolStripMenuItem("(scanning)") { Enabled = false });
        menu.Items.Add(_miRegion);

        _miStrength = new ToolStripMenuItem("Strength");
        _miStrength.DropDownOpening += delegate { BuildStrengthMenu(); };
        _miStrength.DropDownItems.Add(new ToolStripMenuItem("(scanning)") { Enabled = false });
        menu.Items.Add(_miStrength);

        // There is no Mode submenu. This build renders one filter, and a submenu
        // offering a single choice is a control that cannot be used for anything.
        // The tooltip still names the filter, which is where the name was actually
        // being read from anyway.

        menu.Items.Add(new ToolStripSeparator());

        var cap = new ToolStripMenuItem("Include in screen captures");
        cap.Click += delegate
        {
            _visibleInCaptures = !_visibleInCaptures;
            bool ok = true;
            foreach (var s in _slots) if (s.Alive && !s.Window.SetCaptureVisible(_visibleInCaptures)) ok = false;
            if (!ok && !_visibleInCaptures)
            {
                _captureAffinityFailed = true;
                MessageBox.Show("Excluding the overlay from screen captures needs " +
                                "Windows 10 version 2004 (build 19041) or newer.\n\n" +
                                "This machine reports build " + Native.OsBuild() + ".",
                                "Abode Night View (Screen captures)",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            SaveConfig();
        };
        menu.Items.Add(cap);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Shortcuts...", null, delegate { EditHotkeys(); }));
        menu.Items.Add(new ToolStripMenuItem("Re-scan for Adobe applications", null, delegate
        {
            TargetRegistry.ForgetAll();
            _tracks.Clear();
            foreach (var s in _slots) s.Release();
            Sync(true);
            // Same artwork as the state balloon: two notifications from the
            // same program, a minute apart, should not arrive wearing different
            // faces.
            Balloon.Show(_tray, 2500, "Abode Night View (Targets)",
                         DescribeTargetsShort(), Balloon.Art(_enabled), ToolTipIcon.Info);
        }));
        menu.Items.Add(new ToolStripMenuItem("Diagnostics...", null, delegate { ShowReport(false); }));
        menu.Items.Add(new ToolStripMenuItem("Probe Adobe applications...", null, delegate { ShowReport(true); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Quit(); }));

        menu.Opening += delegate
        {
            // Both halves of the global state, every time the menu opens: the word
            // says which state it is in, the tick says it again. Neither is stored.
            _miEnabled.Text = TrayState.EnabledText(_enabled);
            _miEnabled.Checked = _enabled;
            _miSchedule.Text = TrayState.ScheduleItem(_schedule.Active, _schedule.Range);
            _miSchedule.Checked = _schedule.Active;
            _miTargets.Text = TargetsSuffix();
            _miRegion.Enabled = _adapters.Count > 0;
            _miStrength.Text = TrayState.StrengthItem(_strength);
            cap.Checked = _visibleInCaptures;
        };

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += delegate { ToggleEnabled(); };
        UpdateTrayText();
    }

    private void BuildScheduleMenu()
    {
        _miSchedule.DropDownItems.Clear();

        var off = new ToolStripMenuItem("Off");
        off.Checked = !_schedule.Active;
        off.Click += delegate { SetSchedule(false, _schedule.From, _schedule.To); };
        _miSchedule.DropDownItems.Add(off);

        var on = new ToolStripMenuItem("On from " + _schedule.From + " to " + _schedule.To);
        on.Checked = _schedule.Active;
        on.Click += delegate { SetSchedule(true, _schedule.From, _schedule.To); };
        _miSchedule.DropDownItems.Add(on);

        _miSchedule.DropDownItems.Add(new ToolStripSeparator());
        _miSchedule.DropDownItems.Add(
            new ToolStripMenuItem("Set range...", null, delegate { AskSchedule(); }));

        // Nothing else. The state and the range are both already on the parent
        // item -- "Schedule (20:00 – 07:00)" or "Schedule (off)" -- and a
        // submenu that repeats what the item above it just said is something the
        // eye has to read twice to find out it says nothing new.
    }

    private void AskSchedule()
    {
        using (var dlg = new ScheduleDialog(_schedule))
        {
            dlg.Icon = AbodeNvMain.AppIcon(32);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            // Setting a range is asking for it: there is no reason to make the
            // user then find the switch that turns the thing they just
            // configured on.
            SetSchedule(true, dlg.From, dlg.To);
        }
    }

    /// <summary>Adapters in the order a person reads a list: alphabetically, by
    /// the name with "Adobe" taken off the front of it.</summary>
    private List<AdobeTarget> Alphabetical()
    {
        var list = new List<AdobeTarget>(_adapters);
        list.Sort(delegate(AdobeTarget a, AdobeTarget b)
        {
            return string.Compare(a.ShortName, b.ShortName, StringComparison.CurrentCultureIgnoreCase);
        });
        return list;
    }

    private string TargetsSuffix()
    {
        int on = 0, running = 0;
        foreach (var t in _adapters) if (ProductOn(t)) on++;
        var seen = new HashSet<string>();
        foreach (var d in _detected) seen.Add(d.Adapter.Id);
        running = seen.Count;
        return TrayState.TargetsItem(on, running);
    }

    private void BuildTargetsMenu()
    {
        _miTargets.DropDownItems.Clear();

        // Recompute now: the user may have opened the menu specifically because
        // they just launched something.
        var found = TargetRegistry.Discover(_adapters);
        var byId = new Dictionary<string, List<DetectedFrame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in found)
        {
            List<DetectedFrame> l;
            if (!byId.TryGetValue(d.Adapter.Id, out l)) { l = new List<DetectedFrame>(); byId[d.Adapter.Id] = l; }
            l.Add(d);
        }

        // Why each product is or is not attached, in one pass. This is what
        // lets the menu tell "no document open" apart from "this version's
        // windows are not ones this build can read", which are the same silence
        // and opposite answers.
        var status = TargetRegistry.Survey(_adapters, found);

        var missing = new List<string>();
        int shown = 0, unhookable = 0;

        foreach (var t in Alphabetical())
        {
            TargetStatus st;
            if (!status.TryGetValue(t.Id, out st)) st = TargetStatus.NotRunning;
            if (st == TargetStatus.NotRunning) { missing.Add(t.ShortName); continue; }

            List<DetectedFrame> insts;
            byId.TryGetValue(t.Id, out insts);
            string name = insts != null && insts.Count > 0 ? insts[0].ShortLabel : t.ShortName;

            if (st == TargetStatus.Unsupported)
            {
                // A specific error rather than an absence. Not tickable: there
                // is nothing to switch on, and offering the tick would promise
                // an overlay that cannot be produced.
                _miTargets.DropDownItems.Add(
                    new ToolStripMenuItem(TrayState.Labeled(name, "unsupported version"))
                    { Enabled = false });
                unhookable++; shown++;
                continue;
            }

            // One parenthesis, however many things there are to say inside it.
            // Two products' worth of asides -- "(2 windows)" and then a dash and
            // another phrase -- read as two separate labels on one row.
            string label = TrayState.Labeled(name,
                insts != null && insts.Count > 1 ? insts.Count + " windows" : null,
                st == TargetStatus.NoDocument ? "no document open" : null);

            AdobeTarget captured = t;
            var item = new ToolStripMenuItem(label);
            item.Checked = ProductOn(t);
            item.Click += delegate { SetProductOn(captured, !ProductOn(captured)); };
            _miTargets.DropDownItems.Add(item);
            shown++;
        }

        if (shown == 0)
        {
            _miTargets.DropDownItems.Add(
                new ToolStripMenuItem("No supported Adobe application is running") { Enabled = false });

            // And nothing else. The "Not running" list exists to contrast with
            // what IS running; with nothing running it is the entire supported
            // set restated immediately under a line that just said none of it is
            // here. Note that "running" means one of THESE products: Premiere
            // Pro is not an adapter, so no amount of it running can bring this
            // list back.
            return;
        }

        if (unhookable > 0)
        {
            _miTargets.DropDownItems.Add(new ToolStripSeparator());
            _miTargets.DropDownItems.Add(new ToolStripMenuItem(
                unhookable == 1
                    ? "That version's windows are not ones this build can read."
                    : "Those versions' windows are not ones this build can read.")
            { Enabled = false });
            _miTargets.DropDownItems.Add(new ToolStripMenuItem(
                "Run \"Probe Adobe applications...\" and send the report.") { Enabled = false });
        }

        // Products that are supported and not running: listed so "is my
        // application supported at all?" is answerable from the menu rather than
        // only from the README. Alphabetical, and without "Adobe" in front of
        // every one of them -- they are all Adobe, so the word sorts nothing
        // and tells the reader nothing.
        if (missing.Count > 0)
        {
            _miTargets.DropDownItems.Add(new ToolStripSeparator());
            _miTargets.DropDownItems.Add(
                new ToolStripMenuItem("Not running: " + string.Join(", ", missing.ToArray())) { Enabled = false });
        }
    }

    private void BuildRegionMenu()
    {
        _miRegion.DropDownItems.Clear();
        var found = TargetRegistry.Discover(ActiveAdapters());
        var seen = new HashSet<string>();

        foreach (var d in found)
        {
            if (!seen.Add(d.Adapter.Id)) continue;
            AdobeTarget t = d.Adapter;
            var sub = new ToolStripMenuItem(d.ShortLabel);
            foreach (string reg in new[] { Region.Canvas, Region.Document, Region.Client, Region.Window })
            {
                string captured = reg;
                AdobeTarget ct = t;
                var it = new ToolStripMenuItem(Region.Pretty(reg));
                it.Checked = RegionFor(t) == reg;
                it.Click += delegate { SetRegionFor(ct, captured); };
                sub.DropDownItems.Add(it);
            }
            _miRegion.DropDownItems.Add(sub);
        }

        if (_miRegion.DropDownItems.Count == 0)
            _miRegion.DropDownItems.Add(
                new ToolStripMenuItem("Nothing is being tracked") { Enabled = false });
    }

    private static readonly int[] StrengthSteps = { 0, 20, 30, 35, 40, 45, 55, 60, 65, 70, 75, 85 };

    private void BuildStrengthMenu()
    {
        _miStrength.DropDownItems.Clear();
        bool matched = false;
        foreach (int v in StrengthSteps)
        {
            int vv = v;
            var it = new ToolStripMenuItem(vv + "% (k = " +
                (1 - vv / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + ")");
            it.Checked = _strength == vv;
            if (it.Checked) matched = true;
            it.Click += delegate { _strength = vv; Restyle(); };
            _miStrength.DropDownItems.Add(it);
        }
        _miStrength.DropDownItems.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem("Custom... (" + _strength + "%)");
        // A value that is not one of the steps must still be visibly the active
        // one, or the menu is lying about the state by omission.
        custom.Checked = !matched;
        custom.Click += delegate { AskStrength(); };
        _miStrength.DropDownItems.Add(custom);
    }

    private void AskStrength()
    {
        using (var dlg = new StrengthDialog(_strength))
        {
            dlg.Icon = AbodeNvMain.AppIcon(32);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            _strength = Clamp(dlg.Value, 0, 90);
        }
        Restyle();
    }

    /// <summary>
    /// A tiny native About box: the product, the version the binary actually
    /// reports, and who wrote it. It exists because the top line of the tray menu
    /// was a disabled label, which looks clickable, does nothing, and is the
    /// obvious place to put this.
    /// </summary>
    private void ShowAbout()
    {
        using (var dlg = new AboutDialog(Diag.Version))
        {
            dlg.Icon = AbodeNvMain.AppIcon(32);
            dlg.ShowDialog();
        }
    }

    private void ShowReport(bool probe)
    {
        string name = probe ? "AbodeNightView-probe.txt" : "AbodeNightView-diagnostics.txt";
        string path = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Config.Path) ?? ".", name);
        try
        {
            using (var w = new StreamWriter(path, false))
            {
                if (probe)
                {
                    w.WriteLine("Abode Night View – Adobe application probe");
                    w.WriteLine("==========================================");
                    TargetRegistry.Probe(w, null);
                }
                else Diag.Write(w);
            }
            // Opening the report is a convenience; writing it is the job. They are
            // caught separately because a machine with no handler for .txt -- a
            // build agent, a stripped server install -- would otherwise be told
            // the write failed when it did not, and told so in a MODAL box, on a
            // command-line switch nobody is sitting in front of. --diagnostics
            // runs in the release smoke test and in CI; it must always terminate.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                { UseShellExecute = true });
            }
            catch { }
        }
        catch (Exception ex)
        {
            // The report could not be WRITTEN, which is worth interrupting for.
            MessageBox.Show("Could not write the report:\n" + ex.Message,
                "Abode Night View (Diagnostics)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// One line per compatible product that is actually here, saying what is
    /// happening to it. Built from the survey rather than from the frame list,
    /// so a version that is running and cannot be attached to says exactly that
    /// instead of being indistinguishable from one that is not installed.
    ///
    /// The desktop is enumerated here rather than read out of _detected, which
    /// only holds the products the user has switched ON. Reading that list
    /// meant a product that was running but unticked had no frame to take a
    /// name from and fell back to the family name -- so the same Photoshop was
    /// "Photoshop 2026" in the tray menu and "Photoshop" in the notification,
    /// one line apart, with no way for a reader to tell which was the truth.
    /// One EnumWindows pass, once per notification, buys the same answer in
    /// both places.
    /// </summary>
    private string DescribeTargetsShort()
    {
        var found = TargetRegistry.Discover(_adapters);
        var status = TargetRegistry.Survey(_adapters, found);

        var first = new Dictionary<string, DetectedFrame>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in found)
            if (!first.ContainsKey(d.Adapter.Id)) first[d.Adapter.Id] = d;

        var sb = new StringBuilder();
        int here = 0;
        foreach (var t in Alphabetical())
        {
            TargetStatus st;
            if (!status.TryGetValue(t.Id, out st) || st == TargetStatus.NotRunning) continue;
            here++;

            DetectedFrame d;
            string name = first.TryGetValue(t.Id, out d) ? d.ShortLabel : t.ShortName;

            if (st == TargetStatus.Unsupported)
                sb.AppendLine(TrayState.Labeled(name, "unsupported version, cannot attach"));
            else if (st == TargetStatus.NoDocument)
                sb.AppendLine(TrayState.Labeled(name, "no document open"));
            else
                sb.AppendLine(TrayState.Labeled(name, ProductOn(t) ? RegionFor(t) : "not selected"));
        }

        if (here == 0) return "No supported Adobe application is running.";
        sb.Append(_liveOverlays + " overlay(s) active");
        return sb.ToString();
    }

    /// <summary>The part of the diagnostics report only the running process knows.</summary>
    private string DescribeLiveState()
    {
        var sb = new StringBuilder();
        sb.AppendLine("    enabled                " + _enabled);
        sb.AppendLine("    strength               " + _strength + "% (alpha " +
                      (int)Math.Round(_strength * 2.55) + ", k=" +
                      (1.0 - _strength / 100.0).ToString("0.000", CultureInfo.InvariantCulture) + ")");
        sb.AppendLine("    mode                   " + _mode);
        sb.AppendLine("    schedule               " + (_schedule.Active
            ? _schedule.Range
            : "off (switched by hand only)"));
        if (_schedule.Active)
            sb.AppendLine("                           " + _schedule.Status(DateTime.Now));
        sb.AppendLine("    z-order mode requested " + _wantZmode);
        sb.AppendLine("    overlays in use        " + _liveOverlays + " of " + _slots.Count + " in the pool");
        sb.AppendLine("    overlays rebuilt       " + _rebuiltSlots + " (owner window destroyed under us)");
        sb.AppendLine("    include in captures    " + _visibleInCaptures +
                      (_captureAffinityFailed ? " (exclusion unsupported on this build)" : ""));
        sb.AppendLine("    win-event hooks        " + (_hooks.Count + _procHooks.Count) +
                      " (" + _hooks.Count + " global + " + _procHooks.Count + " process-scoped)");
        sb.AppendLine("    resync timer           " + _resync.Interval + " ms");
        // "window icon", not "tray icon": since the tray started following the
        // state it reads the state artwork below like the balloon does, and
        // AppIcon is what puts a picture on the dialogs and what the tray falls
        // back to. A row labeled for the consumer it no longer has is how a
        // diagnostics report starts lying.
        sb.AppendLine("    window icon            " + AbodeNvMain.IconSource);
        sb.AppendLine("    state artwork          " + Balloon.Artwork);
        sb.AppendLine("    tray icon shows        " +
                      (_trayIconState < 0 ? "(not set yet)" :
                       _trayIconState == 1 ? "on (sunglasses)" : "off (plain)"));
        sb.AppendLine("    balloon mechanism      " + Balloon.Mechanism);

        sb.AppendLine("    products");
        foreach (var t in _adapters)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "      {0,-13} {1,-3} region={2,-9} {3}",
                t.Id, ProductOn(t) ? "on" : "off", RegionFor(t),
                Config.Has("target." + t.Id) ? "(user choice)" : "(default)"));

        sb.AppendLine("    tracked frames");
        if (_detected.Count == 0) sb.AppendLine("      (none)");
        foreach (var d in _detected)
        {
            FrameTrack ft;
            _tracks.TryGetValue(d.Frame, out ft);
            sb.AppendLine("      " + d.Label + " " + (d.ProductVersion ?? "?") +
                          " – pid " + d.Pid + ", frame 0x" + d.Frame.ToInt64().ToString("X8"));
            sb.AppendLine("        viewports " + (ft == null ? 0 : ft.Viewports.Count) +
                          ", misses " + (ft == null ? 0 : ft.Misses) +
                          " – " + (ft != null && ft.EverResolved ? "resolved" :
                                   "NOT RESOLVING – nothing is being dimmed for this window"));
        }

        sb.AppendLine("    overlay slots");
        if (_slots.Count == 0) sb.AppendLine("      (none)");
        foreach (var s in _slots)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "      0x{0:X8} {1,-6} zmode={2,-7} owner=0x{3:X8} rect={4}",
                s.Hwnd.ToInt64(), s.InUse ? "in use" : "spare", s.ZMode,
                s.OwnedTo.ToInt64(), s.Alive ? Native.RectOf(s.Hwnd).ToString() : "(dead)"));

        sb.AppendLine("    hotkeys");
        sb.Append(_hotkeys.Summary());
        return sb.ToString();
    }

    /// <summary>
    /// Exactly the format the tooltip was asked for: Abode Night View: [ON] | 55%
    /// Anything extra goes AFTER it and is the first thing dropped if Windows
    /// truncates, because the state is what the hover is for.
    /// </summary>
    private void UpdateTrayText()
    {
        int running, noDoc, unsupported;
        SurveySelected(out running, out noDoc, out unsupported);
        _tray.Text = TrayState.Tooltip(_enabled, _strength, _liveOverlays,
                                       running, noDoc, unsupported);
        ShowStateIcon();
    }

    /// <summary>-1 until the icon has been set once, so the first call always
    /// sets one. Not a bool: "off" and "never set" are different, and starting
    /// up switched off has to still put an icon in the tray.</summary>
    private int _trayIconState = -1;

    /// <summary>
    /// The tray icon follows the state: plain when the dimming is off, in
    /// sunglasses when it is on. Hung off UpdateTrayText because every path
    /// that can change _enabled already calls it -- the menu item, the double
    /// click, the hotkey, the schedule, the command line and the resync -- so
    /// the picture and the words it explains can never be updated separately.
    ///
    /// Guarded on the state rather than assigned every time: each assignment is
    /// a Shell_NotifyIcon(NIM_MODIFY) round trip to the shell, and this runs
    /// from Sync, which runs on the resync timer. Setting the same icon four
    /// times a second is a call the shell does not need and a flicker some
    /// tray implementations show.
    /// </summary>
    private void ShowStateIcon()
    {
        int want = _enabled ? 1 : 0;
        if (want == _trayIconState) return;

        Icon art = AbodeNvMain.StateIcon(_enabled, SystemInformation.SmallIconSize.Width);
        if (art == null) return;      // leave whatever is there; never blank the tray

        _trayIconState = want;
        _tray.Icon = art;
    }

    /// <summary>
    /// What the products the user has ticked are actually doing, counted from
    /// the same <see cref="TargetRegistry.Survey"/> the Targets menu is built
    /// from. This is the fix for the hover text and the menu contradicting each
    /// other: there is now one survey and two renderings of it, rather than two
    /// separate ideas of what "a target" means.
    ///
    /// Survey is handed the frames discovery already found, so the common case
    /// costs nothing extra; it only goes back to the desktop for products it
    /// was given no frame for. This runs on user actions and on a change in what
    /// is attached, not on the timer, so the cost is paid when something has
    /// actually happened.
    /// </summary>
    private void SurveySelected(out int running, out int noDocument, out int unsupported)
    {
        running = noDocument = unsupported = 0;

        var selected = new List<AdobeTarget>();
        foreach (var t in _adapters) if (ProductOn(t)) selected.Add(t);
        if (selected.Count == 0) return;

        var status = TargetRegistry.Survey(selected, _detected);
        foreach (var t in selected)
        {
            TargetStatus st;
            if (!status.TryGetValue(t.Id, out st) || st == TargetStatus.NotRunning) continue;
            running++;
            if (st == TargetStatus.NoDocument) noDocument++;
            else if (st == TargetStatus.Unsupported) unsupported++;
        }
    }

    /// <summary>
    /// The balloon. <paramref name="startup"/> separates "here is what you are
    /// looking at" from "here is what went wrong on the way here": the second
    /// half is only news once, and repeating it every time the user flicks the
    /// switch would train them to dismiss the notification unread.
    /// </summary>
    private void Announce(bool startup)
    {
        // State first, and loudly. Starting up switched-off from a remembered .ini
        // looks exactly like a broken build otherwise.
        //
        // The filter name used to sit in the middle of this line. It is gone:
        // Neutral is the only mode this build renders, so the word was the same
        // on every launch and the number beside it is the thing being reported.
        var msg = new StringBuilder();
        msg.AppendLine(TrayState.StatusLine(_enabled, _strength));
        if (!_enabled) msg.AppendLine("Switch it on with " + ToggleKeyText() + ".");
        if (_schedule.Active) msg.AppendLine("Schedule " + _schedule.Range + ". " +
                                             _schedule.Status(DateTime.Now));
        msg.AppendLine(DescribeTargetsShort());

        // Unset is the shipped state and is not a fault; only a combination the
        // user asked for and did not get is worth interrupting them about.
        int failed = 0;
        foreach (var b in _hotkeys.Bindings) if (b.Failed) failed++;
        if (startup)
        {
            if (failed > 0) msg.Append("\n" + failed + " shortcut(s) unavailable – see tray > Shortcuts");
            if (Config.DroppedHotkeys > 0)
                msg.Append("\nThe old Ctrl+Alt shortcuts were withdrawn (they are AltGr on many " +
                           "keyboards). Set your own in tray > Shortcuts.");
            if (_captureAffinityFailed)
                msg.Append("\nScreen-capture exclusion needs Windows 10 2004 or newer");
            if (Config.MigratedFrom != null)
                msg.Append("\nSettings imported from " + Config.MigratedFrom);
        }

        // The picture carries the state before the sentence under it has been
        // read: the plain artwork when the dimming is off, the same character in
        // sunglasses when it is on. Balloon falls back to a stock Windows icon by
        // itself if the artwork or the shell call is unavailable, and records
        // which path it took for --diagnostics.
        Balloon.Show(_tray, 3500, "Abode Night View " + Diag.Version, msg.ToString(),
                     Balloon.Art(_enabled),
                     _enabled && failed == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    // ------------------------------------------------------------------ misc

    private void ParseArgs(string[] argv)
    {
        foreach (var a in argv)
        {
            try
            {
                if (a == "--on") { _enabled = true; _stateFromCommandLine = true; }
                else if (a == "--off") { _enabled = false; _stateFromCommandLine = true; }
                else if (a.StartsWith("--strength=")) _strength = Clamp(int.Parse(a.Substring(11)), 0, 90);
                else if (a.StartsWith("--mode=")) _mode = a.Substring(7).ToLowerInvariant();
                else if (a.StartsWith("--zmode=")) _wantZmode = a.Substring(8).ToLowerInvariant();
                else if (a.StartsWith("--capture=")) _visibleInCaptures = a.Substring(10) != "exclude";
                else if (a.StartsWith("--schedule="))
                {
                    // A malformed range keeps the stored schedule rather than
                    // inventing a different one out of half a parse.
                    NightSchedule sch = NightSchedule.Parse(a.Substring(11));
                    if (sch != null) _schedule = sch;
                }
                else if (a.StartsWith("--pid=")) _pinnedPid = uint.Parse(a.Substring(6));
                else if (a.StartsWith("--region."))
                {
                    int eq = a.IndexOf('=');
                    if (eq > 9)
                    {
                        string id = a.Substring(9, eq - 9);
                        string reg = Region.Normalize(a.Substring(eq + 1));
                        if (reg != null) _productRegion[id] = reg;
                    }
                }
                else if (a.StartsWith("--region=") || a.StartsWith("--target="))
                {
                    string reg = Region.Normalize(a.Substring(a.IndexOf('=') + 1));
                    if (reg != null) foreach (var t in _adapters) _productRegion[t.Id] = reg;
                }
                else if (a.StartsWith("--products="))
                {
                    var want = new HashSet<string>(a.Substring(11).Split(','), StringComparer.OrdinalIgnoreCase);
                    foreach (var t in _adapters) _productOn[t.Id] = want.Contains(t.Id);
                }
                else if (a.StartsWith("--adapter="))
                {
                    // id:Family:process:frameclass -- how the audit harness points
                    // the real engine at a window it controls, without the engine
                    // needing a special test mode.
                    AdobeTarget extra = GenericTarget.Parse(a.Substring(10));
                    if (extra != null)
                    {
                        _adapters.Add(extra);
                        _productOn[extra.Id] = true;
                    }
                }
            }
            catch (FormatException) { /* a malformed switch keeps the previous value */ }
            catch (OverflowException) { }
        }
        if (_wantZmode != "owned" && _wantZmode != "above" && _wantZmode != "topmost") _wantZmode = "owned";
        _mode = Modes.Normalize(_mode);
    }

    private void LoadConfig()
    {
        _strength = Config.Int("strength", _strength, 0, 90);
        // Anything this build cannot render -- 1.1's greyscale and shader, a
        // hand-edited value, a mode from some later release -- becomes Neutral
        // here and is written back as Neutral on the next save. Silently: the
        // only way to have had greyscale or shader in a settings file was to type
        // it in by hand, because neither was ever selectable.
        _mode = Modes.Normalize(Config.Str("mode", _mode));
        _enabled = Config.Bool("enabled", _enabled);
        _schedule = NightSchedule.Load();
        _visibleInCaptures = Config.Bool("captures", _visibleInCaptures);
        _wantZmode = Config.Str("zmode", _wantZmode).ToLowerInvariant();

        foreach (var t in _adapters)
        {
            if (Config.Has("target." + t.Id)) _productOn[t.Id] = ProductPrefs.Enabled(t);
            if (Config.Has("region." + t.Id)) _productRegion[t.Id] = ProductPrefs.RegionOf(t);
        }
    }

    private void SaveConfig()
    {
        Config.Set("strength", _strength);
        Config.Set("mode", _mode);
        Config.SetBool("enabled", _enabled);
        Config.SetBool("captures", _visibleInCaptures);
        Config.Set("zmode", _wantZmode);
        foreach (var t in _adapters)
        {
            // Runtime-only adapters (--adapter=, used by the audit harness) must
            // never reach the settings file: they would be read back on the next
            // ordinary launch as products that do not exist.
            if (TargetRegistry.ById(t.Id) != t) continue;
            Config.SetBool("target." + t.Id, ProductOn(t));
            Config.Set("region." + t.Id, RegionFor(t));
        }
        if (_hotkeys != null)
            // An empty value, not "(none)": what is written here has to parse
            // back to the same thing on the next launch, and "(none)" is a
            // label for a human, not a value.
            foreach (var b in _hotkeys.Bindings)
                Config.Set(b.Key, b.Spec.IsEmpty ? "" : b.Spec.ToString());
        _schedule.Save();
        Config.Save();
    }

    private void Quit()
    {
        _resync.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDesktopChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnDesktopChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        foreach (var h in _procHooks.Values) Native.UnhookWinEvent(h);
        _hooks.Clear(); _procHooks.Clear();
        _hotkeys.Dispose();
        _tray.Visible = false; _tray.Dispose();
        foreach (var s in _slots) s.Destroy();
        _slots.Clear();
        ExitThread();
    }
}

// ---------------------------------------------------------------------------
//  About
// ---------------------------------------------------------------------------
/// <summary>
/// An icon, drawn as an icon.
///
/// PictureBox wants an Image, and the only way to get one out of an Icon is
/// Icon.ToBitmap() -- which cannot read a PNG-compressed .ico entry. It walks
/// the payload as though it were a DIB and hands back noise. Every entry in
/// this project's icons is PNG-compressed (see tools\make-icon.ps1, and the
/// same hazard written up at length in Balloon.cs), so the About box was
/// showing a scrambled copy of the artwork on its own title bar.
///
/// Graphics.DrawIcon goes through DrawIconEx, which is the path the shell
/// itself uses and understands both encodings. So the icon is PAINTED rather
/// than converted, and the question of which encoding is inside it stops being
/// this dialog's problem.
/// </summary>
internal sealed class IconView : Control
{
    private readonly Icon _icon;

    public IconView(Icon icon, int side)
    {
        _icon = icon;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(side, side);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_icon != null) e.Graphics.DrawIcon(_icon, new Rectangle(0, 0, Width, Height));
    }
}

/// <summary>
/// Deliberately tiny: an icon, a few lines of text, and a Close button.
///
/// The repository line is conditional on AboutInfo.HasRepository rather than
/// hard-coded, so a build made before the repository existed showed no link
/// instead of a dead one. It resolves now, and --selftest asserts that it does.
/// </summary>
internal sealed class AboutDialog : Form
{
    /// <summary>The one wrap width. Every paragraph is wrapped to it and the
    /// window is then sized to what they came out as, so there is no width
    /// stated twice for the two to disagree about.</summary>
    private const int BodyWidth = 452;

    public AboutDialog(string version)
    {
        Text = AboutInfo.ProductName + " (About)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(14);

        // Laid out rather than positioned. The license notice is three
        // paragraphs of somebody else's words, which cannot be shortened to fit
        // a number typed into SetBounds -- so the panel measures them and the
        // window takes the size that comes out.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(Masthead(version));

        layout.Controls.Add(Linked(AboutInfo.Attribution, AboutInfo.Author, AboutInfo.AuthorUrl, 0));
        layout.Controls.Add(Plain(AboutInfo.Copyright, AboutInfo.HasRepository ? 0 : 12));

        if (AboutInfo.HasRepository)
            layout.Controls.Add(Linked("Source code: " + AboutInfo.RepositoryUrl,
                                       AboutInfo.RepositoryUrl, AboutInfo.RepositoryUrl, 12));

        // The GPL's own three paragraphs, verbatim, with the URL in the last one
        // made clickable where it already stands. Adding a separate "license"
        // link beside it would be the same address written twice.
        for (int i = 0; i < AboutInfo.License.Length; i++)
        {
            string para = AboutInfo.License[i];
            int gap = i == AboutInfo.License.Length - 1 ? 12 : 8;
            if (para.Contains(AboutInfo.LicenseUrl))
                layout.Controls.Add(Linked(para, AboutInfo.LicenseUrl, AboutInfo.LicenseUrl, gap));
            else
                layout.Controls.Add(Plain(para, gap));
        }

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons);

        AcceptButton = close; CancelButton = close;

        Controls.Add(layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    /// <summary>Icon, product, version: the three things somebody opening an
    /// About box came for, before any of the words underneath.</summary>
    private Control Masthead(string version)
    {
        var head = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 14),
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var icon = new IconView(AbodeNvMain.AppIcon(32), 32)
        { Margin = new Padding(0, 2, 14, 0) };
        head.Controls.Add(icon, 0, 0);
        head.SetRowSpan(icon, 2);

        var title = new Label { Text = AboutInfo.ProductName, AutoSize = true, Margin = new Padding(0) };
        title.Font = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold);
        head.Controls.Add(title, 1, 0);

        head.Controls.Add(new Label
        {
            Text = "Version " + version,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 3, 0, 0),
        }, 1, 1);

        return head;
    }

    private static Label Plain(string text, int gapBelow)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(BodyWidth, 0),
            Margin = new Padding(0, 0, 0, gapBelow),
        };
    }

    /// <summary>
    /// A wrapped paragraph with one stretch of it turned into a link.
    ///
    /// The stretch is FOUND in the text rather than given as an offset: a
    /// LinkArea is a character range, and a range typed in by hand goes on
    /// pointing at characters 12 to 20 after somebody rewords the sentence in
    /// front of it. If the text is not there the paragraph simply has no link,
    /// which is a missing link rather than a link on the wrong words.
    /// </summary>
    private LinkLabel Linked(string text, string linkText, string url, int gapBelow)
    {
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(BodyWidth, 0),
            Margin = new Padding(0, 0, 0, gapBelow),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        l.Links.Clear();
        int at = text.IndexOf(linkText, StringComparison.Ordinal);
        if (at >= 0) l.Links.Add(at, linkText.Length, url);
        l.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e)
        {
            object data = e.Link == null ? null : e.Link.LinkData;
            Open(data == null ? url : data.ToString());
        };
        return l;
    }

    /// <summary>
    /// The system browser, via the shell, exactly as clicking the same URL
    /// anywhere else on the desktop would. A failure is reported rather than
    /// thrown: an About box that takes the application down with it is worse
    /// than one whose link did not open.
    /// </summary>
    private void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the link:\n" + ex.Message,
                AboutInfo.ProductName + " (About)",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

// ---------------------------------------------------------------------------
//  The custom range
// ---------------------------------------------------------------------------
/// <summary>
/// Two spin fields and a sentence. Deliberately not a free-text box: the pair
/// of DateTimePickers in HH:mm mode cannot produce 25:70, cannot produce an
/// empty value, and steps with the arrow keys, so there is no format to explain
/// and nothing to validate beyond the one thing the widgets cannot rule out --
/// the two times being equal, which is not a range.
///
/// The sentence under them says what the schedule will DO, recomputed on every
/// keystroke, because 20:00 to 07:00 and 07:00 to 20:00 look almost identical
/// and mean opposite things.
/// </summary>
internal sealed class ScheduleDialog : Form
{
    /// <summary>Whether the schedule was RUNNING when the dialog opened. The
    /// sentence under the two fields used to be computed from a throwaway
    /// schedule that was always active, so opening the dialog with the feature
    /// switched off still read "Off now, until 20:00" -- a countdown to
    /// something that was not going to happen.</summary>
    private readonly bool _wasActive;

    private readonly DateTimePicker _from = new DateTimePicker();
    private readonly DateTimePicker _to = new DateTimePicker();
    private readonly Label _read = new Label();
    private readonly Button _ok = new Button { Text = "OK", DialogResult = DialogResult.OK };

    public ClockTime From { get { return ClockTime.Of(_from.Value); } }
    public ClockTime To { get { return ClockTime.Of(_to.Value); } }

    public ScheduleDialog(NightSchedule current)
    {
        _wasActive = current.Active;
        Text = "Abode Night View (Schedule)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(430, 152);

        var lead = new Label
        {
            Text = "Switch the dimming on and off by the clock. A range that crosses " +
                   "midnight is normal.",
            AutoSize = false,
        };
        lead.SetBounds(12, 10, 406, 32);
        Controls.Add(lead);

        var fromLabel = new Label { Text = "On at", AutoSize = true };
        fromLabel.SetBounds(12, 52, 40, 18);
        var toLabel = new Label { Text = "Off at", AutoSize = true };
        toLabel.SetBounds(216, 52, 40, 18);
        Controls.Add(fromLabel); Controls.Add(toLabel);

        Prepare(_from, current.From);
        _from.SetBounds(58, 48, 100, 24);
        Prepare(_to, current.To);
        _to.SetBounds(262, 48, 100, 24);
        Controls.Add(_from); Controls.Add(_to);

        _read.SetBounds(12, 82, 406, 32);
        _read.AutoSize = false;
        Controls.Add(_read);

        _ok.SetBounds(254, 116, 80, 26);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(338, 116, 80, 26);
        Controls.Add(_ok); Controls.Add(cancel);
        AcceptButton = _ok; CancelButton = cancel;

        Sync();
    }

    private void Prepare(DateTimePicker p, ClockTime at)
    {
        p.Format = DateTimePickerFormat.Custom;
        p.CustomFormat = "HH:mm";
        p.ShowUpDown = true;
        p.Value = DateTime.Today.AddMinutes(at.Minutes);
        p.ValueChanged += delegate { Sync(); };
    }

    private void Sync()
    {
        bool degenerate = From.Minutes == To.Minutes;
        _ok.Enabled = !degenerate;
        if (degenerate)
        {
            _read.Text = "Both times are the same, which is not a range. Move one of them.";
            return;
        }
        // Two sentences, and both have to hold whether the schedule is running
        // or not: the first states the range being edited, the second states what
        // the schedule is doing about it at this moment -- which, if it is
        // switched off, is nothing.
        var probe = new NightSchedule { Active = _wasActive, From = From, To = To };
        _read.Text = probe.Plan + " " + probe.Status(DateTime.Now);
    }
}

// ---------------------------------------------------------------------------
//  Custom strength
// ---------------------------------------------------------------------------
internal sealed class StrengthDialog : Form
{
    private readonly TrackBar _bar = new TrackBar();
    private readonly Label _read = new Label();
    private readonly Label _note = new Label();
    public int Value { get { return _bar.Value; } }

    public StrengthDialog(int start)
    {
        Text = "Abode Night View (Strength)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        // The reading sits above the slider and its consequence below it, so
        // the control is between the number it sets and the effect that number
        // has. Both used to be one line above, pipe-separated, which put three
        // readings in a row and left the eye to split them.
        //
        // Narrower than it was, because the width was set by that concatenated
        // line and the longest thing here now is the sentence underneath.
        // DialogShot reports whether every label fits; this dialog is the one
        // that has moved every time the wording has.
        ClientSize = new Size(290, 158);

        _read.SetBounds(12, 12, 266, 20);
        Controls.Add(_read);

        _bar.SetBounds(12, 38, 266, 45);
        _bar.Minimum = 0; _bar.Maximum = 90; _bar.TickFrequency = 5;
        _bar.Value = Math.Max(0, Math.Min(90, start));
        _bar.ValueChanged += delegate { Sync(); };
        Controls.Add(_bar);

        // Gray, because it is the explanation rather than the setting: the eye
        // should land on the number it came here to change first.
        _note.SetBounds(12, 88, 266, 20);
        _note.ForeColor = SystemColors.GrayText;
        Controls.Add(_note);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        ok.SetBounds(114, 118, 80, 26);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(198, 118, 80, 26);
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;

        Sync();
    }

    private void Sync()
    {
        // Both strings come from TrayState so the harness reads exactly what
        // the user reads -- these were the last user-visible strings in the
        // program still being formatted at the point of display.
        _read.Text = TrayState.StrengthHeadline(_bar.Value);
        _note.Text = TrayState.StrengthNote(_bar.Value);
    }
}

// ---------------------------------------------------------------------------
//  Entry point
// ---------------------------------------------------------------------------
internal static class AbodeNvMain
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, IntPtr name, uint type,
                                           int cx, int cy, uint load);
    private const int ATTACH_PARENT_PROCESS = -1;
    private const uint IMAGE_ICON = 1, LR_SHARED = 0x8000;
    private const int IDI_APPLICATION_GROUP = 32512;   // the id csc gives -win32icon

    /// <summary>How AppIcon last resolved, for --diagnostics. An icon that has
    /// silently fallen back to the stock one is exactly the sort of thing that
    /// looks fine to whoever built it and wrong to everybody else.</summary>
    public static string IconSource = "(not loaded)";

    /// <summary>
    /// The application icon at an exact size, read out of this executable's own
    /// Win32 icon group. Using the resource that is already there rather than a
    /// second embedded copy keeps the distributed file to one icon, and asking
    /// for the size we want gets the right entry of the multi-size .ico instead
    /// of a downscaled 32x32. Falls back rather than failing to start.
    /// </summary>
    public static Icon AppIcon(int size)
    {
        try
        {
            IntPtr h = LoadImage(GetModuleHandleW(null), new IntPtr(IDI_APPLICATION_GROUP),
                                 IMAGE_ICON, size, size, LR_SHARED);
            if (h != IntPtr.Zero)
            {
                var ico = Icon.FromHandle(h);
                IconSource = "Win32 resource " + IDI_APPLICATION_GROUP +
                             ", asked for " + size + "px, got " + ico.Width + "x" + ico.Height;
                return ico;
            }
            IconSource = "LoadImage returned NULL (Windows error " +
                         Marshal.GetLastWin32Error() + ")";
        }
        catch (Exception e) { IconSource = "LoadImage threw " + e.GetType().Name; }
        try
        {
            var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            IconSource += "; fell back to ExtractAssociatedIcon";
            return ico;
        }
        catch { }
        IconSource += "; FELL BACK TO THE STOCK WINDOWS ICON";
        return SystemIcons.Application;
    }

    /// <summary>
    /// The icon for a state, at an exact size: the plain artwork when the
    /// dimming is off, the same character in sunglasses when it is on. This is
    /// what the notification area shows, and it is the same pair the balloon
    /// shows, resolved through the same function -- so a notification cannot
    /// arrive wearing a different face from the icon that raised it.
    ///
    /// The off artwork is generated from the same source PNG as the Win32 icon
    /// group in this executable, so falling back to <see cref="AppIcon"/> when
    /// the managed resource is missing is a fallback in provenance only: the
    /// user sees the same picture either way. The on artwork has no such
    /// twin, so a build without the resource shows the off icon in both states
    /// rather than no icon in either -- wrong, but wrong in the direction that
    /// leaves the program usable, and --diagnostics says so.
    /// </summary>
    public static Icon StateIcon(bool enabled, int size)
    {
        Icon art = Balloon.Art(enabled, size);
        return art != null ? art : AppIcon(size);
    }

    private static bool Has(string[] argv, string name)
    {
        foreach (var a in argv)
            if (a == name || a.StartsWith(name + "=")) return true;
        return false;
    }

    private static string Value(string[] argv, string name)
    {
        foreach (var a in argv)
            if (a.StartsWith(name + "=")) return a.Substring(name.Length + 1);
        return null;
    }

    private static bool _ownConsole;

    /// <summary>
    /// This is a GUI-subsystem binary so double-clicking it never flashes a console
    /// window. The diagnostic subcommands still have to print somewhere, so borrow
    /// the console we were launched from, or make one if there is not one.
    ///
    /// The cost of the GUI subsystem is that a shell does not WAIT for us, so from
    /// PowerShell or cmd the prompt comes back before the text arrives. Piping
    /// (| Out-String) or redirecting makes the shell wait. When we had to make our
    /// own console -- launched from Explorer -- we hold it open at the end instead,
    /// or the report would vanish with the window.
    /// </summary>
    private static void EnsureConsole()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) _ownConsole = AllocConsole();

        // Without this the report prints paths in the console OEM codepage, and a
        // project folder with non-ASCII characters in its name comes out as
        // mojibake in the one place a bug report most needs to be readable.
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }

    private static int Leave(int code)
    {
        if (_ownConsole)
        {
            Console.WriteLine();
            Console.Write("Press any key to close...");
            try { Console.ReadKey(true); } catch { System.Threading.Thread.Sleep(15000); }
        }
        return code;
    }

    private const string HelpText = @"
Abode Night View – a display-only dimmer for Adobe document viewports.

  AbodeNightView.exe                     start, using AbodeNightView.ini
  AbodeNightView.exe --on                start switched on regardless of settings
  AbodeNightView.exe --schedule=20:00-07:00   on and off by the clock
  AbodeNightView.exe --schedule=off      no schedule; switch it by hand
  AbodeNightView.exe --strength=55       dim to 55% (k = 0.45)
  AbodeNightView.exe --region=canvas     canvas only, for every product
  AbodeNightView.exe --region.photoshop=document
  AbodeNightView.exe --products=indesign,illustrator
  AbodeNightView.exe --zmode=above       chase the z-order instead of owning the window
  AbodeNightView.exe --capture=exclude   keep the overlays out of screenshots (Win10 2004+)
  AbodeNightView.exe --pid=1234          track one process only

Regions:   canvas | document | client | window
Products:  indesign, illustrator, incopy, photoshop, acrobat

Diagnostics:
  AbodeNightView.exe --version
  AbodeNightView.exe --diagnostics       write and open AbodeNightView-diagnostics.txt
  AbodeNightView.exe --probe             why did this Adobe version not attach?
  AbodeNightView.exe --probe=photoshop   one product only
  AbodeNightView.exe --verify            photometric and structural self-test
  AbodeNightView.exe --baseline          capture the reference, switched OFF
  AbodeNightView.exe --watch=25          log every overlay transition for 25 seconds

No shortcut is bound out of the box: a global hotkey is taken from every
program on the machine, and no combination is free of AltGr, free inside Adobe
and present on every keyboard at once. Set your own in tray icon > Shortcuts,
or as hotkey.* in the .ini.

The document is never touched. Abode Night View only reads window geometry and
paints its own windows; it never opens, scripts, or sends a message to Adobe.
Disable it for color-critical visual judgment.
";

    [STAThread]
    private static int Main(string[] argv)
    {
        // Must happen before any HWND exists, so every rect we read and write
        // is in real physical pixels regardless of display scaling. Older Windows
        // does not have the newest of the three APIs, hence the cascade.
        Native.ApplyBestDpiAwareness();

        if (Has(argv, "--help") || Has(argv, "-h") || Has(argv, "/?"))
        { EnsureConsole(); Console.WriteLine(HelpText); return Leave(0); }

        if (Has(argv, "--version"))
        {
            EnsureConsole();
            // ASCII separators, deliberately. This one line is what somebody
            // pastes into a bug report, and it is read back through whatever
            // console codepage that machine happens to have -- an en dash here
            // arrives as mojibake on any machine that is not on a UTF-8
            // codepage, which is most of them.
            Console.WriteLine("Abode Night View " + Diag.Version + " | " +
                              (IntPtr.Size == 8 ? "x64" : "x86") +
                              " | .NET Framework " + Environment.Version +
                              " | Windows " + Native.OsVersionString());
            return Leave(0);
        }

        if (Has(argv, "--probe"))
        {
            EnsureConsole();
            Config.Load();
            string which = Value(argv, "--probe");
            AdobeTarget only = which == null ? null : TargetRegistry.ById(which);
            if (which != null && only == null)
            {
                Console.WriteLine("Unknown product '" + which + "'. Known: indesign, illustrator, " +
                                  "incopy, photoshop, acrobat.");
                return Leave(2);
            }
            var sw = new StringWriter();
            sw.WriteLine("Abode Night View – Adobe application probe");
            sw.WriteLine("==========================================");
            sw.WriteLine("Windows " + Native.OsVersionString() + " | " + Native.DpiAwarenessApplied);
            TargetRegistry.Probe(sw, only);
            Console.WriteLine(sw.ToString());
            string path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Config.Path) ?? ".", "AbodeNightView-probe.txt");
            try { File.WriteAllText(path, sw.ToString()); Console.WriteLine("Written to " + path); }
            catch (Exception e) { Console.WriteLine("Could not write " + path + ": " + e.Message); }
            return Leave(0);
        }

        if (Has(argv, "--diagnostics"))
        {
            EnsureConsole();
            Config.Load();
            string path = Value(argv, "--diagnostics");
            var sw = new StringWriter();
            Diag.Write(sw);
            Console.WriteLine(sw.ToString());
            if (path == null)
                path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Config.Path) ?? ".",
                    "AbodeNightView-diagnostics.txt");
            try { File.WriteAllText(path, sw.ToString()); Console.WriteLine("Written to " + path); }
            catch (Exception e) { Console.WriteLine("Could not write " + path + ": " + e.Message); }
            return Leave(0);
        }

        if (Has(argv, "--verify") || Has(argv, "--watch") || Has(argv, "--baseline"))
        {
            EnsureConsole();
            return Leave(Verify.Run(argv));
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // A second instance is worse than useless: RegisterHotKey is first-come,
        // so the OLD process keeps the shortcuts and the new one silently has none.
        // Two sets of overlays then fight over the same rectangles while the
        // keyboard drives the wrong one.
        bool mine;
        using (var single = new System.Threading.Mutex(
                   true, "Local\\AbodeNightView.SingleInstance", out mine))
        {
            if (!mine)
            {
                MessageBox.Show(
                    "Abode Night View is already running.\n\n" +
                    "Use its tray icon to change settings, or exit it first " +
                    "(tray icon > Exit) and start again.",
                    "Abode Night View (Already running)",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            Application.Run(new AbodeNvContext(argv));
        }
        return 0;
    }
}
