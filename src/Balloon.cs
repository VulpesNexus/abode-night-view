// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – the notification balloon, with the artwork on it
// ----------------------------------------------------------------------------
//  What this is for
//      The balloon says what state the utility is in. A blue "i" says only that
//      Windows has a message; the state is then a sentence the reader has to
//      stop and parse. The artwork says it at a glance, and it is the same
//      artwork that is in the tray, so the notification is visibly FROM this
//      program rather than from whatever spoke last.
//
//      Two variants: the plain icon when the dimming is off, the "cool" one --
//      the same character wearing sunglasses -- when it is on. That mapping is
//      the whole point: the icon a user sees is the state they are in.
//
//      The tray icon itself now follows the same mapping, so the two resources
//      below are no longer a balloon's private artwork -- they are the state,
//      drawn -- and they are named state-off / state-on for it. The tray reads
//      them through the same Art(state, size) that the balloon does, because a
//      notification wearing one face while the icon that raised it wears
//      another is the same class of contradiction as a tooltip disagreeing with
//      its own menu.
//
//  Why this is not three lines of WinForms
//      NotifyIcon.ShowBalloonTip takes a ToolTipIcon, which is an enum of four
//      stock pictures. The shell itself has supported an arbitrary icon since
//      Windows XP SP2 -- NIIF_USER with hBalloonIcon in NOTIFYICONDATA -- but
//      that field was never surfaced in Windows Forms, whose NOTIFYICONDATA
//      declaration stops short of it. So the balloon has to be raised with
//      Shell_NotifyIcon directly.
//
//      Doing that needs the (hWnd, uID) pair of an icon that is ALREADY in the
//      notification area, and those two values live in private fields of
//      NotifyIcon. They are read by reflection. That is a real dependency on an
//      implementation detail, so it is written to fail softly in every
//      direction: if the fields are gone, if the window has no handle yet, if
//      the resource is missing, or if the shell refuses the call, the balloon
//      is raised the ordinary way with a stock icon instead. A notification is
//      never worth a crash, and "the picture is wrong" is not worth silence.
//
//      Mechanism and Artwork record which path was taken, and --diagnostics
//      prints them, because a silent fallback that looks fine on the machine it
//      was written on is exactly the failure this project keeps guarding
//      against.
//
//  Sizes
//      NIIF_LARGE_ICON nominally asks for SM_CXICON -- 32 px at 100% scaling --
//      but Windows 10 and 11 do not draw a tray balloon any more: they turn it
//      into a toast, whose image is laid out at around 48 logical pixels. A
//      32 px icon is therefore scaled UP and looks soft, so what is loaded is
//      twice SM_CXICON, capped at the largest size in the resource. Handing the
//      shell something to shrink costs nothing; handing it something to enlarge
//      shows.
//
//      The notification area asks for the opposite end of the same ladder --
//      SM_CXSMICON, 16 px at 100% and 32 px at 200% -- so the resources carry
//      16, 20, 24, 32, 48, 64 and 96: what a tray asks for at the four scalings
//      Windows offers, and what a toast asks for at the same four.
//
//      The closest entry is selected at load time rather than resampled from
//      one bitmap, which is the reason the small sizes had to be added rather
//      than left to the shell: an .ico whose smallest entry is 32 px is a 32 px
//      icon halved by the tray, and halving artwork with a 2 px sunglasses bar
//      in it is how the on state stops being legible. They are PNG-
//      compressed inside the container, which Windows has understood since
//      Vista -- CreateIconFromResourceEx, which is what produces the HICON here,
//      and DrawIconEx, which is what the shell draws it with. (System.Drawing's
//      own Icon.ToBitmap does NOT understand PNG entries and returns noise. It
//      is never used on these icons; nothing here converts them back to a
//      managed bitmap.)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Balloon
{
    // ---------------------------------------------------------------- Win32

    /// <summary>
    /// The Vista-and-later NOTIFYICONDATAW, in full. Sequential layout with the
    /// default packing reproduces the native one exactly on x64: the DWORD at
    /// the front is followed by four bytes of padding before the first pointer,
    /// which is what the C header gets from the compiler for the same reason.
    /// cbSize is taken from Marshal.SizeOf rather than a hard-coded number, so
    /// there is no second copy of the layout to keep in step.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_USER = 0x00000004;
    private const int NIIF_LARGE_ICON = 0x00000020;

    /// <summary>szInfo is 256 WCHARs including the terminator, szInfoTitle 64.
    /// ByValTStr THROWS on an over-long string rather than truncating, so the
    /// truncation is done here where a sensible ellipsis can be put on it.</summary>
    private const int MaxText = 255, MaxTitle = 63;

    // ------------------------------------------------------------- artwork

    public const string OffResource = "state-off.ico";
    public const string OnResource = "state-on.ico";

    /// <summary>
    /// Keyed by state AND size, because the two things that read these now want
    /// very different ones: a toast around 48-96 px, a tray icon 16-32. One
    /// cached Icon per state would have handed whichever asked second the size
    /// the first one wanted.
    /// </summary>
    private static readonly Dictionary<string, Icon> _art = new Dictionary<string, Icon>();

    /// <summary>How the icons last resolved, for --diagnostics.</summary>
    public static string Artwork = "(not loaded)";

    /// <summary>How the last balloon was actually raised, for --diagnostics.</summary>
    public static string Mechanism = "(nothing shown yet)";

    /// <summary>
    /// The picture for a given state at the size a balloon wants it: the "cool"
    /// variant when the dimming is on, the plain one when it is off.
    ///
    /// Twice SM_CXICON, capped at the largest size in the resource -- a Windows
    /// 10/11 toast draws this bigger than the metric it asks for.
    /// </summary>
    public static Icon Art(bool enabled)
    {
        return Art(enabled, Math.Min(96, SystemInformation.IconSize.Width * 2));
    }

    /// <summary>
    /// The same picture at a size the caller names, for the notification area,
    /// which asks for SM_CXSMICON rather than SM_CXICON.
    ///
    /// Cached, and deliberately never disposed. The shell reads the HICON while
    /// the balloon is on screen and for as long as the tray icon is in the
    /// notification area, which for the tray is the life of the process; and
    /// there are at most a handful of them, one per (state, size) actually
    /// asked for.
    /// </summary>
    public static Icon Art(bool enabled, int size)
    {
        if (size < 1) size = 16;
        string key = (enabled ? "on:" : "off:") + size.ToString(CultureInfo.InvariantCulture);

        Icon cached;
        if (_art.TryGetValue(key, out cached)) return cached;

        // Stored even when it is null: a resource that is not in this build will
        // not be in it a second later either, and Note() would otherwise append
        // the same failure to --diagnostics on every repaint.
        Icon loaded = Load(enabled ? OnResource : OffResource,
                           (enabled ? "on" : "off") + " @" + size, size);
        _art[key] = loaded;
        return loaded;
    }

    private static Icon Load(string resource, string which, int want)
    {
        try
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null)
                {
                    Note(which + ": resource " + resource + " IS NOT IN THIS BUILD");
                    return null;
                }
                var ico = new Icon(s, new Size(want, want));
                Note(which + ": " + resource + " asked for " + want + "px, got " +
                     ico.Width + "x" + ico.Height);
                return ico;
            }
        }
        catch (Exception e)
        {
            Note(which + ": " + resource + " FAILED TO LOAD – " + e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    private static void Note(string line)
    {
        Artwork = Artwork == "(not loaded)" ? line : Artwork + "; " + line;
    }

    // -------------------------------------------------- the private fields

    private static FieldInfo _fWindow, _fId;
    private static bool _probed;
    private static string _probeResult;

    /// <summary>
    /// Find NotifyIcon.window and NotifyIcon.id. Kept apart from everything
    /// else so the self-test can ask "does this still work on the runtime we
    /// are actually on?" without raising a notification to find out.
    /// </summary>
    public static bool CanCustomise(out string why)
    {
        if (!_probed)
        {
            _probed = true;
            Type t = typeof(NotifyIcon);
            _fWindow = t.GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
            _fId = t.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);

            if (_fWindow == null || _fId == null)
                _probeResult = "NotifyIcon no longer has the private fields " +
                               "'window' and 'id' on this runtime";
            else if (!typeof(NativeWindow).IsAssignableFrom(_fWindow.FieldType))
                _probeResult = "NotifyIcon.window is a " + _fWindow.FieldType.Name +
                               ", not a NativeWindow";
            else if (_fId.FieldType != typeof(int))
                _probeResult = "NotifyIcon.id is a " + _fId.FieldType.Name + ", not an int";
            else
                _probeResult = null;
        }
        why = _probeResult;
        return _probeResult == null;
    }

    // ------------------------------------------------------------ the call

    /// <summary>
    /// Raise a balloon carrying <paramref name="art"/>. Falls back to the
    /// ordinary WinForms balloon with <paramref name="fallback"/> as its stock
    /// icon if anything at all is not available. Returns true when the custom
    /// path was used, which is what the caller reports rather than asserts:
    /// the fallback is a worse notification, not a failure.
    /// </summary>
    public static bool Show(NotifyIcon tray, int timeoutMs, string title, string text,
                            Icon art, ToolTipIcon fallback)
    {
        if (tray == null) return false;
        if (Custom(tray, timeoutMs, title, text, art)) return true;
        try { tray.ShowBalloonTip(timeoutMs, title, text, fallback); }
        catch (Exception e) { Mechanism = "no balloon at all – " + e.GetType().Name; }
        return false;
    }

    private static bool Custom(NotifyIcon tray, int timeoutMs, string title, string text, Icon art)
    {
        string why;
        if (art == null) { Mechanism = "stock icon (no artwork loaded)"; return false; }
        if (!CanCustomise(out why)) { Mechanism = "stock icon (" + why + ")"; return false; }

        try
        {
            var win = _fWindow.GetValue(tray) as NativeWindow;
            if (win == null || win.Handle == IntPtr.Zero)
            {
                // Windows Forms creates the message window lazily, when the icon
                // is first made visible. Before that there is no icon in the
                // notification area to modify.
                Mechanism = "stock icon (the tray icon has no window yet)";
                return false;
            }

            var d = new NOTIFYICONDATAW();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATAW));
            d.hWnd = win.Handle;
            d.uID = (int)_fId.GetValue(tray);
            d.uFlags = NIF_INFO;
            d.szTip = "";
            d.szInfo = Fit(text, MaxText);
            d.szInfoTitle = Fit(title, MaxTitle);
            d.uTimeoutOrVersion = timeoutMs;
            d.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
            d.hBalloonIcon = art.Handle;

            if (!Shell_NotifyIconW(NIM_MODIFY, ref d))
            {
                Mechanism = "stock icon (Shell_NotifyIcon refused, Windows error " +
                            Marshal.GetLastWin32Error() + ")";
                return false;
            }
            Mechanism = "custom artwork (NIIF_USER, " + art.Width + "px)";
            return true;
        }
        catch (Exception e)
        {
            Mechanism = "stock icon (" + e.GetType().Name + ": " + e.Message + ")";
            return false;
        }
    }

    /// <summary>Cut to a length the shell accepts, without cutting mid-word if
    /// there is a space to cut at. Public because the truncation rule is worth
    /// asserting: a message one character too long would otherwise throw out of
    /// the marshaller at the moment there is something to say.</summary>
    public static string Fit(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r\n", "\n").TrimEnd('\n', ' ');
        if (s.Length <= max) return s;

        string cut = s.Substring(0, max - 1);
        int space = cut.LastIndexOfAny(new[] { ' ', '\n' });
        if (space > max / 2) cut = cut.Substring(0, space);
        return cut + "…";
    }
}
