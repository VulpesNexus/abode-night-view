// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – configurable global hotkeys
// ----------------------------------------------------------------------------
//  Why there are no default shortcuts
//      RegisterHotKey installs a SYSTEM-WIDE keyboard grab. The shell dispatches
//      WM_HOTKEY to the registering window before the keystroke is offered to
//      whatever has focus, so Abode Night View's shortcut BEATS the
//      application's own for the same combination -- InDesign never sees the
//      key at all, and neither does anything else. A default binding is
//      therefore not a convenience, it is a key taken away from every program
//      on the machine on the user's behalf.
//
//      Every candidate for a default was ruled out, and each for a different
//      reason:
//
//        Ctrl+Alt+<printable>   AltGr reports itself to Windows as Ctrl+Alt, so
//                               on German, French, Polish, Portuguese, Nordic,
//                               Spanish, Czech, Turkish and US-International
//                               layouts this IS the AltGr character on that key.
//                               Taking it stops that character being typable
//                               anywhere, in any program. This is what 1.2 and
//                               earlier shipped.
//        Win+<anything>         Windows 10 and 11 reserve the Windows key: the
//                               combinations are consumed by the shell and are
//                               not reliably registrable at all.
//        Ctrl+Shift / Alt+Shift Windows' own defaults for switching keyboard
//                               layout and input language live on these pairs,
//                               and Ctrl+Shift+<letter> is heavily used inside
//                               every Adobe application.
//        F1..F12, any modifier  Adobe assigns panels and commands across the
//                               whole function row in InDesign, Illustrator and
//                               Photoshop, and users remap them freely. A
//                               global grab takes the key from every
//                               application, not only from Adobe.
//        Numpad, Pause, ScrLk   Layout-proof, and absent from most laptop and
//                               tenkeyless keyboards, or behind Fn.
//
//      Nothing satisfies "free on every layout AND free in Adobe AND present on
//      every keyboard", so nothing is bound out of the box. The tray menu and
//      the schedule work without a shortcut, and anyone who wants one picks it
//      themselves in tray > Shortcuts, on their own keyboard, where the editor
//      can tell them exactly what it would cost them.
//
//  Layout independence
//      RegisterHotKey takes a VIRTUAL KEY, not a scan code. Virtual keys are
//      produced by the active keyboard layout, so "Ctrl+Alt+N" means "the key
//      that types N on the layout in use", which is what the user expects and
//      what the letter printed on the keycap says. It also means the physical
//      position moves between QWERTY / AZERTY / QWERTZ, which is correct.
//      For an exact, layout-proof binding, a raw code may be written as VK:0x4E.
//
//      A custom binding being captured from the user's own keyboard does NOT
//      make the AltGr warning unnecessary -- it makes it more necessary. On a
//      German layout, pressing AltGr+Q to type "@" arrives here as Ctrl+Alt+Q
//      and would be accepted as a perfectly ordinary-looking shortcut. The user
//      would have bound the key they type "@" with, and would not find out
//      until the next time they needed one. So the editor asks the CURRENT
//      layout what AltGr+<key> types (ToUnicodeEx) and quotes the character
//      back at them before they can apply it.
//
//  Limits of RegisterHotKey
//      * First come, first served, process-wide across the session: if another
//        application already owns the combination, registration FAILS with
//        ERROR_HOTKEY_ALREADY_REGISTERED (1409). It is never stolen silently,
//        and we never fail silently either.
//      * It cannot be registered for combinations the shell reserves
//        (most Win+<key> combinations belong to Explorer).
//      * It does not fire while a UAC secure-desktop prompt is up, and it does
//        not fire when an elevated process has foreground and we are not
//        elevated (User Interface Privilege Isolation). Nothing can change that
//        short of running elevated, which Abode Night View deliberately does not.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

/// <summary>A parsed modifier+key combination, e.g. "Ctrl+Alt+N".</summary>
internal struct HotkeySpec
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8,
                      MOD_NOREPEAT = 0x4000;

    public uint Mods;      // without MOD_NOREPEAT; that is added at registration
    public uint Vk;

    public bool IsEmpty { get { return Vk == 0; } }

    public static HotkeySpec Make(uint mods, uint vk)
    { return new HotkeySpec { Mods = mods, Vk = vk }; }

    // ------------------------------------------------------------- parsing

    /// <summary>
    /// Parse "Ctrl+Alt+N". Returns false and an explanation rather than throwing,
    /// because the input can come from a hand-edited settings file.
    /// </summary>
    public static bool TryParse(string text, out HotkeySpec spec, out string error)
    {
        spec = new HotkeySpec(); error = null;
        if (string.IsNullOrEmpty(text)) { error = "empty"; return false; }

        string[] parts = text.Split('+');
        uint mods = 0, vk = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length == 0)
            {
                // "Ctrl++" -- the final empty piece means the '+' key itself.
                if (i == parts.Length - 1 && text.EndsWith("+")) { vk = 0xBB; continue; }
                continue;
            }

            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": case "windows": case "meta": mods |= MOD_WIN; continue;
            }

            if (vk != 0) { error = "more than one non-modifier key"; return false; }

            uint parsed;
            if (!TryParseKey(p, out parsed)) { error = "unknown key \"" + p + "\""; return false; }
            vk = parsed;
        }

        if (vk == 0) { error = "no key, only modifiers"; return false; }
        if (mods == 0)
        {
            // A bare key would swallow that key everywhere in Windows.
            error = "needs at least one modifier (Ctrl, Alt, Shift or Win)";
            return false;
        }
        spec = Make(mods, vk);
        return true;
    }

    private static bool TryParseKey(string p, out uint vk)
    {
        vk = 0;

        // Raw escape hatch, exact and layout-proof: VK:0x4E  or  VK:78
        if (p.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
        {
            string v = p.Substring(3).Trim();
            try
            {
                vk = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt32(v.Substring(2), 16)
                    : uint.Parse(v, CultureInfo.InvariantCulture);
            }
            catch { return false; }
            return vk > 0 && vk <= 0xFF;
        }

        string k = p.ToLowerInvariant();
        uint alias;
        if (Aliases.TryGetValue(k, out alias)) { vk = alias; return true; }

        if (p.Length == 1)
        {
            char c = char.ToUpperInvariant(p[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
        }

        // Anything System.Windows.Forms.Keys knows: F1..F24, OemPeriod, NumPad3, ...
        try
        {
            var key = (Keys)Enum.Parse(typeof(Keys), p, true);
            if (Enum.IsDefined(typeof(Keys), key)) { vk = (uint)key; return true; }
        }
        catch { }
        return false;
    }

    private static readonly Dictionary<string, uint> Aliases =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        { "up", (uint)Keys.Up }, { "down", (uint)Keys.Down },
        { "left", (uint)Keys.Left }, { "right", (uint)Keys.Right },
        { "pgup", (uint)Keys.PageUp }, { "pageup", (uint)Keys.PageUp },
        { "pgdn", (uint)Keys.PageDown }, { "pagedown", (uint)Keys.PageDown },
        { "home", (uint)Keys.Home }, { "end", (uint)Keys.End },
        { "ins", (uint)Keys.Insert }, { "insert", (uint)Keys.Insert },
        { "del", (uint)Keys.Delete }, { "delete", (uint)Keys.Delete },
        { "space", (uint)Keys.Space }, { "spacebar", (uint)Keys.Space },
        { "enter", (uint)Keys.Return }, { "return", (uint)Keys.Return },
        { "esc", (uint)Keys.Escape }, { "escape", (uint)Keys.Escape },
        { "tab", (uint)Keys.Tab }, { "backspace", (uint)Keys.Back },
        { "plus", 0xBB }, { "minus", 0xBD }, { "comma", 0xBC }, { "period", 0xBE },
        // Numpad spelled the short way. These are distinct virtual keys from the
        // digit row and are the same on every layout, which makes them the safest
        // choice on an AltGr keyboard.
        { "num0", (uint)Keys.NumPad0 }, { "num1", (uint)Keys.NumPad1 },
        { "num2", (uint)Keys.NumPad2 }, { "num3", (uint)Keys.NumPad3 },
        { "num4", (uint)Keys.NumPad4 }, { "num5", (uint)Keys.NumPad5 },
        { "num6", (uint)Keys.NumPad6 }, { "num7", (uint)Keys.NumPad7 },
        { "num8", (uint)Keys.NumPad8 }, { "num9", (uint)Keys.NumPad9 },
        { "numadd", (uint)Keys.Add }, { "numplus", (uint)Keys.Add },
        { "numsub", (uint)Keys.Subtract }, { "numminus", (uint)Keys.Subtract },
        { "nummul", (uint)Keys.Multiply }, { "numdiv", (uint)Keys.Divide },
        { "numdec", (uint)Keys.Decimal },
    };

    // ------------------------------------------------------------ printing

    public override string ToString()
    {
        if (IsEmpty) return "(none)";
        var sb = new System.Text.StringBuilder();
        if ((Mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((Mods & MOD_ALT) != 0) sb.Append("Alt+");
        if ((Mods & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((Mods & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(KeyName(Vk));
        return sb.ToString();
    }

    /// <summary>
    /// The ONE name a virtual key is printed as. This deliberately does not search
    /// the alias table: several aliases map to the same key ("pgdn" and "pagedown"),
    /// Dictionary enumeration order is not specified, and the first match therefore
    /// varied -- so the same binding could be written to the settings file under
    /// two different spellings on two different runs. Every name here parses back
    /// to the same key, which the self-test checks by round trip.
    /// </summary>
    public static string KeyName(uint vk)
    {
        string named = LookupDisplay(vk);
        if (named != null) return named;
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
        var k = (Keys)vk;
        return Enum.IsDefined(typeof(Keys), k)
            ? k.ToString()
            : "VK:0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string LookupDisplay(uint vk)
    {
        for (int i = 0; i < DisplayVk.Length; i++)
            if (DisplayVk[i] == vk) return DisplayName[i];
        return null;
    }

    private static readonly uint[] DisplayVk =
    {
        (uint)Keys.Up, (uint)Keys.Down, (uint)Keys.Left, (uint)Keys.Right,
        (uint)Keys.PageUp, (uint)Keys.PageDown, (uint)Keys.Home, (uint)Keys.End,
        (uint)Keys.Insert, (uint)Keys.Delete, (uint)Keys.Space, (uint)Keys.Return,
        (uint)Keys.Escape, (uint)Keys.Tab, (uint)Keys.Back,
        0xBB, 0xBD, 0xBC, 0xBE,
        (uint)Keys.NumPad0, (uint)Keys.NumPad1, (uint)Keys.NumPad2, (uint)Keys.NumPad3,
        (uint)Keys.NumPad4, (uint)Keys.NumPad5, (uint)Keys.NumPad6, (uint)Keys.NumPad7,
        (uint)Keys.NumPad8, (uint)Keys.NumPad9,
        (uint)Keys.Add, (uint)Keys.Subtract, (uint)Keys.Multiply,
        (uint)Keys.Divide, (uint)Keys.Decimal,
    };

    private static readonly string[] DisplayName =
    {
        "Up", "Down", "Left", "Right",
        "PageUp", "PageDown", "Home", "End",
        "Insert", "Delete", "Space", "Enter",
        "Esc", "Tab", "Backspace",
        "Plus", "Minus", "Comma", "Period",
        "Num0", "Num1", "Num2", "Num3",
        "Num4", "Num5", "Num6", "Num7",
        "Num8", "Num9",
        "NumAdd", "NumSub", "NumMul",
        "NumDiv", "NumDec",
    };

    // ------------------------------------------------- the current layout

    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyExW(uint code, uint mapType, IntPtr layout);
    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state,
                                          [Out] StringBuilder buf, int bufLen, uint flags, IntPtr layout);

    private const uint MAPVK_VK_TO_VSC = 0;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
                      VK_SPACE = 0x20, VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_RMENU = 0xA5;

    /// <summary>
    /// Does AltGr + this key actually do something on the keyboard layout that
    /// is loaded RIGHT NOW? <paramref name="typed"/> comes back as the
    /// character it types, or null when it is a dead key -- which is a
    /// collision just the same, and a worse one to lose.
    ///
    /// This is asked of the live layout rather than assumed from a list of
    /// countries, so the warning quotes the actual character ("this is how you
    /// type @") instead of hedging about layouts the user may not have. It is
    /// the difference between a notice that is ignored and one that is read.
    ///
    /// Control codes are filtered out deliberately. With Ctrl held, a US layout
    /// answers Ctrl+Alt+N with U+000E -- a real return value, and not something
    /// anybody types. Without that filter every layout on earth "produces a
    /// character" and the warning fires for everything.
    /// </summary>
    public bool AltGrIsUsedHere(out string typed)
    {
        typed = null;
        if (IsEmpty) return false;
        if ((Mods & MOD_CONTROL) == 0 || (Mods & MOD_ALT) == 0) return false;
        if ((Mods & MOD_WIN) != 0) return false;

        try
        {
            IntPtr layout = GetKeyboardLayout(0);
            uint scan = MapVirtualKeyExW(Vk, MAPVK_VK_TO_VSC, layout);

            var state = new byte[256];
            state[VK_CONTROL] = 0x80; state[VK_LCONTROL] = 0x80;
            state[VK_MENU] = 0x80; state[VK_RMENU] = 0x80;
            if ((Mods & MOD_SHIFT) != 0) { state[VK_SHIFT] = 0x80; state[VK_LSHIFT] = 0x80; }

            var buf = new StringBuilder(8);
            int n = ToUnicodeEx(Vk, scan, state, buf, buf.Capacity, 0, layout);
            ClearDeadKey(layout);

            if (n < 0) return true;                       // dead key: typed stays null
            if (n == 0) return false;

            string t = buf.ToString();
            if (t.Length == 0) return false;
            if (t[0] < ' ' || t[0] == '\u007F') return false;   // a control code, not a character
            typed = t;
            return true;
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }

    /// <summary>
    /// ToUnicodeEx leaves a pending dead key in the calling thread's keyboard
    /// state, which would then be applied to the next key the user really
    /// presses -- inside our own dialog, where they are trying to set a
    /// shortcut. Feeding it a space resolves the pending state; the loop is
    /// because a layout may chain.
    /// </summary>
    private static void ClearDeadKey(IntPtr layout)
    {
        var empty = new byte[256];
        var junk = new StringBuilder(8);
        uint scan = MapVirtualKeyExW(VK_SPACE, MAPVK_VK_TO_VSC, layout);
        for (int i = 0; i < 4; i++)
            if (ToUnicodeEx(VK_SPACE, scan, empty, junk, junk.Capacity, 0, layout) >= 0) return;
    }

    /// <summary>True for Ctrl+Alt+&lt;printable&gt;, which AltGr layouts also produce.
    /// The layout-independent half of the test: what this machine's own layout
    /// does is AltGrIsUsedHere, and a combination that is free HERE can still
    /// be somebody's AltGr key on the copy they were sent.</summary>
    public bool CollidesWithAltGr
    {
        get
        {
            if ((Mods & MOD_CONTROL) == 0 || (Mods & MOD_ALT) == 0) return false;
            if ((Mods & MOD_WIN) != 0) return false;
            // Numpad and function keys are never AltGr characters.
            if (Vk >= (uint)Keys.F1 && Vk <= (uint)Keys.F24) return false;
            if (Vk >= (uint)Keys.NumPad0 && Vk <= (uint)Keys.Divide) return false;
            if (Vk >= (uint)Keys.Left && Vk <= (uint)Keys.Down) return false;
            return (Vk >= 'A' && Vk <= 'Z') || (Vk >= '0' && Vk <= '9') || (Vk >= 0xBA && Vk <= 0xE2);
        }
    }
}

// ---------------------------------------------------------------------------
//  Registration
// ---------------------------------------------------------------------------
internal sealed class HotkeyBinding
{
    public readonly int Id;
    public readonly string Key;          // settings key, e.g. "hotkey.toggle"
    public readonly string Label;        // for the UI, e.g. "Toggle"
    public readonly string Default;      // empty: nothing is bound out of the box
    public readonly bool NoRepeat;       // toggles yes, +/- steppers no
    public HotkeySpec Spec;
    public bool Registered;
    public string Error;                 // null when fine, and null when unset

    public HotkeyBinding(int id, string key, string label, string dflt, bool noRepeat)
    { Id = id; Key = key; Label = label; Default = dflt; NoRepeat = noRepeat; }

    /// <summary>
    /// No combination is bound to this action. Kept distinct from "failed to
    /// register" everywhere it is reported: an unset action is the shipped
    /// state and says nothing is wrong, while a failed one means the user asked
    /// for something and did not get it.
    /// </summary>
    public bool Unset { get { return Spec.IsEmpty; } }

    /// <summary>Asked for and not obtained. This is what gets reported.</summary>
    public bool Failed { get { return !Registered && !Unset; } }
}

/// <summary>
/// Message-only window owning every global hotkey. One window, one place that
/// knows what is registered, so rebinding cannot leak a stale registration.
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly Action<int> _onHotkey;
    private readonly List<HotkeyBinding> _bindings = new List<HotkeyBinding>();

    public IEnumerable<HotkeyBinding> Bindings { get { return _bindings; } }

    public HotkeyManager(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    public void Add(HotkeyBinding b) { _bindings.Add(b); }

    public HotkeyBinding Find(string key)
    { return _bindings.Find(b => b.Key == key); }

    /// <summary>(Re)register everything. Returns the number that failed.</summary>
    public int RegisterAll()
    {
        int failed = 0;
        foreach (var b in _bindings)
        {
            Unregister(b);
            Register(b);
            if (b.Failed) failed++;
        }
        return failed;
    }

    private bool Register(HotkeyBinding b)
    {
        b.Registered = false; b.Error = null;
        // Not a failure: nothing is bound out of the box, and an action the
        // user has deliberately cleared must not be reported as broken.
        if (b.Spec.IsEmpty) return false;

        uint mods = b.Spec.Mods | (b.NoRepeat ? HotkeySpec.MOD_NOREPEAT : 0);
        if (Native.RegisterHotKey(Handle, b.Id, mods, b.Spec.Vk))
        { b.Registered = true; return true; }

        int err = Marshal.GetLastWin32Error();
        b.Error = err == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "already taken by another application"
            : "RegisterHotKey failed (Windows error " + err + ")";
        return false;
    }

    private void Unregister(HotkeyBinding b)
    {
        if (!b.Registered) return;
        Native.UnregisterHotKey(Handle, b.Id);
        b.Registered = false;
    }

    /// <summary>
    /// Change one binding. On failure the previous combination is put back, so a
    /// rejected edit can never leave the user with no working shortcut at all.
    /// </summary>
    public bool Rebind(HotkeyBinding b, HotkeySpec want, out string error)
    {
        HotkeySpec old = b.Spec;
        bool wasRegistered = b.Registered;

        Unregister(b);
        b.Spec = want;
        // Clearing a binding always succeeds: there is nothing to ask Windows
        // for. Without this the empty spec fell through to Register, came back
        // false, and the old combination was silently restored -- so a cleared
        // shortcut could not be cleared.
        if (want.IsEmpty) { b.Registered = false; b.Error = null; error = null; return true; }
        if (Register(b)) { error = null; return true; }

        error = b.Error;
        b.Spec = old;
        if (wasRegistered) Register(b);
        return false;
    }

    /// <summary>Human-readable summary for the tray tooltip and diagnostics.</summary>
    public string Summary()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in _bindings)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    {0,-22} {1,-18} {2}",
                b.Label, b.Spec,
                b.Registered ? "registered"
                             : b.Unset ? "not set (no default; set one in tray > Shortcuts)"
                                       : "NOT REGISTERED – " + b.Error));
        return sb.ToString().TrimEnd();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) { _onHotkey((int)m.WParam); return; }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var b in _bindings) Unregister(b);
        DestroyHandle();
    }
}

// ---------------------------------------------------------------------------
//  A label that can put one word in italics
// ---------------------------------------------------------------------------
/// <summary>
/// Why this exists: "press Esc to cancel" reads as an instruction containing
/// the ordinary word "escape" until Esc is set apart from the sentence around
/// it. The same goes for Backspace and for the name of a button. A Label draws
/// its whole Text in one Font, so a mixed run needs either a RichTextBox --
/// which brings a caret, a selection, a scrollbar and an RTF encoder to put two
/// words in italics -- or this, which is a Label that lays out its own words.
///
/// The markup is deliberately the smallest thing that can work: &lt;i&gt; and
/// &lt;/i&gt;, nothing nested, no attributes, no escaping. Anything else in the
/// text is text.
///
/// Wrapping is done here rather than by TextRenderer because a line can change
/// font partway along, so its width is not a property of one string. Words are
/// placed left to right and the line is broken when the next one would not fit;
/// a word carries the space in FRONT of it into its own measurement, which is
/// what makes the gaps exact -- the space is measured in the same call, with
/// the same font, as the text it is drawn with.
///
/// GetPreferredSize is the honest answer to "how big does this need to be", so
/// AutoSize containers size it correctly and the self-test can assert that
/// nothing is clipped without measuring the text a second, different way.
/// </summary>
internal sealed class RichLabel : Label
{
    private sealed class Piece
    {
        public string Text;
        public bool Italic;
        public int X, Y;
    }

    private sealed class Token
    {
        public string Word;      // null for a line break
        public bool Italic;
    }

    private const TextFormatFlags Draw =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    private static readonly Size Unbounded = new Size(int.MaxValue, int.MaxValue);

    private readonly List<Piece> _pieces = new List<Piece>();
    private Font _italicFont;
    private Size _extent;
    private int _laidOutFor = -1;
    private int _overhang = -1, _overhangItalic = -1;

    /// <summary>
    /// MeasureText does not return the advance width. DT_CALCRECT allows for the
    /// glyph overhang at the end of the run, so the answer is (real width + c)
    /// for a constant c that depends only on the font -- 7 px for the 8.25 pt
    /// Microsoft Sans Serif this dialog uses. Adding word widths measured that
    /// way therefore adds c once per word, which is what put a visible double
    /// space between every pair of words in the first version of this control.
    ///
    /// c falls straight out of measuring one glyph and two: M(X) = w + c and
    /// M(XX) = 2w + c, so c = 2*M(X) - M(XX). Subtracting it makes the measured
    /// widths add up exactly -- summing them word by word and measuring the
    /// whole line agree to the pixel -- and it is added back once, at the end of
    /// the widest line, as the overhang allowance it actually is.
    /// </summary>
    private int Overhang(Font f, bool italic)
    {
        int cached = italic ? _overhangItalic : _overhang;
        if (cached >= 0) return cached;
        int one = TextRenderer.MeasureText("X", f, Unbounded, Draw).Width;
        int two = TextRenderer.MeasureText("XX", f, Unbounded, Draw).Width;
        cached = Math.Max(0, 2 * one - two);
        if (italic) _overhangItalic = cached; else _overhang = cached;
        return cached;
    }

    private int Advance(string text, Font f, bool italic)
    {
        return TextRenderer.MeasureText(text, f, Unbounded, Draw).Width - Overhang(f, italic);
    }

    /// <summary>The allowance to leave at the right-hand end of the longest
    /// line, for whichever of the two fonts leans furthest.</summary>
    private int Slack
    {
        get { return Math.Max(Overhang(Font, false), Overhang(ItalicFont, true)); }
    }

    public RichLabel()
    {
        AutoSize = true;
        UseMnemonic = false;
    }

    /// <summary>The text with the markup taken out, which is what anything
    /// reporting on this control should quote.</summary>
    public string PlainText
    {
        get { return (Text ?? "").Replace("<i>", "").Replace("</i>", ""); }
    }

    private Font ItalicFont
    {
        get
        {
            if (_italicFont == null) _italicFont = new Font(Font, Font.Style | FontStyle.Italic);
            return _italicFont;
        }
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Reset(); }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_italicFont != null) { _italicFont.Dispose(); _italicFont = null; }
        _overhang = -1; _overhangItalic = -1;
        Reset();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _italicFont != null) { _italicFont.Dispose(); _italicFont = null; }
        base.Dispose(disposing);
    }

    private void Reset() { _laidOutFor = -1; _pieces.Clear(); Invalidate(); }

    /// <summary>Split into words and line breaks, carrying the italic flag.</summary>
    private static List<Token> Parse(string text)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text)) return tokens;

        bool italic = false;
        int i = 0;
        var word = new StringBuilder();

        while (i <= text.Length)
        {
            if (i == text.Length)
            {
                if (word.Length > 0) tokens.Add(new Token { Word = word.ToString(), Italic = italic });
                break;
            }
            if (string.CompareOrdinal(text, i, "<i>", 0, 3) == 0)
            {
                if (word.Length > 0) { tokens.Add(new Token { Word = word.ToString(), Italic = italic }); word.Length = 0; }
                italic = true; i += 3; continue;
            }
            if (string.CompareOrdinal(text, i, "</i>", 0, 4) == 0)
            {
                if (word.Length > 0) { tokens.Add(new Token { Word = word.ToString(), Italic = italic }); word.Length = 0; }
                italic = false; i += 4; continue;
            }

            char c = text[i];
            if (c == '\r') { i++; continue; }
            if (c == '\n' || c == ' ' || c == '\t')
            {
                if (word.Length > 0) { tokens.Add(new Token { Word = word.ToString(), Italic = italic }); word.Length = 0; }
                if (c == '\n') tokens.Add(new Token { Word = null, Italic = false });
                i++; continue;
            }
            word.Append(c);
            i++;
        }
        return tokens;
    }

    private void LayOut(int maxWidth)
    {
        if (_laidOutFor == maxWidth) return;
        _laidOutFor = maxWidth;
        _pieces.Clear();

        int lineHeight = Math.Max(Font.Height, ItalicFont.Height);
        int slack = Slack;
        // Break one overhang short of the limit, so the slack added to the
        // extent below cannot push the reported width past MaximumSize -- which
        // the layout engine would then clamp, reintroducing the clipping the
        // slack is there to prevent.
        int wrapAt = maxWidth > 0 ? Math.Max(1, maxWidth - slack) : 0;
        int x = 0, y = 0, widest = 0;
        bool atLineStart = true;

        foreach (Token t in Parse(Text))
        {
            // A break always advances, even from an empty line: that is what
            // makes "\n\n" a blank line rather than a no-op.
            if (t.Word == null) { y += lineHeight; x = 0; atLineStart = true; continue; }

            Font f = t.Italic ? ItalicFont : Font;
            string draw = atLineStart ? t.Word : " " + t.Word;
            int w = Advance(draw, f, t.Italic);

            if (!atLineStart && wrapAt > 0 && x + w > wrapAt)
            {
                y += lineHeight; x = 0; atLineStart = true;
                draw = t.Word;
                w = Advance(draw, f, t.Italic);
            }

            _pieces.Add(new Piece { Text = draw, Italic = t.Italic, X = x, Y = y });
            x += w;
            atLineStart = false;
            if (x > widest) widest = x;
        }

        _extent = _pieces.Count == 0 ? Size.Empty : new Size(widest + slack, y + lineHeight);
    }

    private int WrapWidth(Size proposed)
    {
        if (MaximumSize.Width > 0) return MaximumSize.Width;
        if (proposed.Width > 0 && proposed.Width < int.MaxValue) return proposed.Width;
        return 0;                                    // unbounded: one line per paragraph
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        LayOut(WrapWidth(proposedSize));
        return new Size(_extent.Width + Padding.Horizontal, _extent.Height + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Deliberately not calling base.OnPaint: Label would draw Text as it
        // stands, markup and all, underneath this.
        LayOut(MaximumSize.Width > 0 ? MaximumSize.Width : ClientSize.Width);
        Color ink = Enabled ? ForeColor : SystemColors.GrayText;
        foreach (Piece pc in _pieces)
            TextRenderer.DrawText(e.Graphics, pc.Text, pc.Italic ? ItalicFont : Font,
                                  new Point(Padding.Left + pc.X, Padding.Top + pc.Y), ink, Draw);
    }
}

// ---------------------------------------------------------------------------
//  The editor
// ---------------------------------------------------------------------------
/// <summary>
/// Four rows, each a box you click into and then press the combination you want.
/// Deliberately small: one screen, no tabs, no framework.
/// </summary>
internal sealed class HotkeyEditor : Form
{
    /// <summary>The one wrap width, shared by the explanation and the status
    /// line, so the dialog cannot end up two different widths depending on
    /// which of the two happens to be longer.</summary>
    private const int IntroWidth = 460;

    private readonly HotkeyManager _mgr;
    private readonly List<KeyBox> _boxes = new List<KeyBox>();
    private readonly Label _status = new Label();

    private sealed class KeyBox : TextBox
    {
        public readonly HotkeyBinding Binding;
        private HotkeySpec _pending;
        public HotkeySpec Pending { get { return _pending; } set { _pending = value; } }
        public event Action Changed;

        public KeyBox(HotkeyBinding b)
        {
            Binding = b; Pending = b.Spec;
            ReadOnly = true; Cursor = Cursors.Hand;
            Text = b.Spec.ToString();
            TextAlign = HorizontalAlignment.Center;
        }

        protected override bool IsInputKey(Keys keyData) { return true; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true; e.Handled = true;

            if (e.KeyCode == Keys.Escape) { Pending = Binding.Spec; Text = Pending.ToString();
                                            if (Changed != null) Changed(); return; }

            // The modifier keys themselves are not a binding; wait for a real key.
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu ||
                e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
            { Text = Preview(e) + "..."; return; }

            uint mods = 0;
            if (e.Control) mods |= HotkeySpec.MOD_CONTROL;
            if (e.Alt) mods |= HotkeySpec.MOD_ALT;
            if (e.Shift) mods |= HotkeySpec.MOD_SHIFT;
            if (mods == 0)
            {
                // A bare key can never be a global hotkey -- it would swallow
                // that key everywhere in Windows -- so the unmodified keys are
                // free to mean something to the box itself. Clearing has to be
                // reachable now that the shipped state is "nothing bound".
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                {
                    Pending = new HotkeySpec();
                    Text = Pending.ToString();
                    if (Changed != null) Changed();
                    return;
                }
                Text = "needs Ctrl / Alt / Shift"; return;
            }

            Pending = HotkeySpec.Make(mods, (uint)e.KeyCode);
            Text = Pending.ToString();
            if (Changed != null) Changed();
        }

        private static string Preview(KeyEventArgs e)
        {
            var sb = new System.Text.StringBuilder();
            if (e.Control) sb.Append("Ctrl+");
            if (e.Alt) sb.Append("Alt+");
            if (e.Shift) sb.Append("Shift+");
            return sb.ToString();
        }

        protected override void OnGotFocus(EventArgs e)
        { base.OnGotFocus(e); BackColor = SystemColors.Info; }

        protected override void OnLostFocus(EventArgs e)
        { base.OnLostFocus(e); BackColor = SystemColors.Window; Text = Pending.ToString(); }
    }

    public HotkeyEditor(HotkeyManager mgr)
    {
        _mgr = mgr;
        Text = "Abode Night View (Shortcuts)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(IntroWidth + 40, 60);
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Wrapped to a width rather than broken with \n. An AutoSize label is
        // exactly as wide as its longest hard-coded line, so a line that does
        // not fit is not wrapped -- it is CUT OFF, which is what happened to the
        // second sentence here. MaximumSize gives the wrap point and lets the
        // label grow downwards instead.
        //
        // The key names are italicised because "Esc cancels that row" otherwise
        // reads as a sentence about escaping, and "Backspace clears it" as one
        // about going back.
        var intro = new RichLabel
        {
            Text = "Click a box and press the desired combination. <i>Esc</i> cancels " +
                   "that row. <i>Backspace</i> clears it.\n\n" +
                   "These shortcuts take priority over application shortcuts, so avoid " +
                   "combinations you have already assigned elsewhere.\n\n" +
                   "By default, nothing is bound.",
            MaximumSize = new Size(IntroWidth, 0),
            Margin = new Padding(3, 3, 3, 12),
        };
        layout.Controls.Add(intro); layout.SetColumnSpan(intro, 2);

        foreach (var b in mgr.Bindings)
        {
            var lbl = new Label { Text = b.Label, AutoSize = true, Anchor = AnchorStyles.Left,
                                  Margin = new Padding(3, 7, 12, 3) };
            var box = new KeyBox(b) { Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 6) };
            box.Changed += Revalidate;
            _boxes.Add(box);
            layout.Controls.Add(lbl); layout.Controls.Add(box);
        }

        _status.AutoSize = true;
        _status.MaximumSize = new Size(IntroWidth, 0);
        _status.Margin = new Padding(3, 8, 3, 3);
        layout.Controls.Add(_status); layout.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel
        { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        // "Defaults" would now mean "clear everything", which is what this says
        // instead. A button whose label promises to restore something and then
        // empties four boxes is worse than no button.
        var reset = new Button { Text = "Clear all", AutoSize = true };
        reset.Click += (s, e) =>
        {
            foreach (var box in _boxes)
            {
                HotkeySpec sp; string err;
                if (!HotkeySpec.TryParse(box.Binding.Default, out sp, out err))
                    sp = new HotkeySpec();
                box.Pending = sp; box.Text = sp.ToString();
            }
            Revalidate();
        };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); buttons.Controls.Add(reset);
        layout.Controls.Add(buttons); layout.SetColumnSpan(buttons, 2);

        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (s, e) => Apply();

        Controls.Add(layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Revalidate();
    }

    /// <summary>Warn about everything that can be known before trying to register.</summary>
    private void Revalidate()
    {
        var notes = new List<string>();
        for (int i = 0; i < _boxes.Count; i++)
            for (int j = i + 1; j < _boxes.Count; j++)
                if (!_boxes[i].Pending.IsEmpty &&
                    _boxes[i].Pending.Mods == _boxes[j].Pending.Mods &&
                    _boxes[i].Pending.Vk == _boxes[j].Pending.Vk)
                    notes.Add("Two actions share " + _boxes[i].Pending + ".");

        foreach (var box in _boxes)
        {
            if (box.Pending.IsEmpty) continue;

            // What THIS keyboard does, first: it is a fact rather than a
            // caution, and it names the character the user is about to lose.
            string typed;
            if (box.Pending.AltGrIsUsedHere(out typed))
            {
                notes.Add(box.Pending + " is AltGr+" + HotkeySpec.KeyName(box.Pending.Vk) +
                          " on this keyboard, which " +
                          (typed == null ? "starts an accent (a dead key)."
                                         : "types \u201C" + typed + "\u201D.") +
                          " Taking it stops that being typable in any program while " +
                          "Abode Night View runs.");
            }
            else if (box.Pending.CollidesWithAltGr)
            {
                notes.Add(box.Pending + " is free on this keyboard, and is AltGr+" +
                          HotkeySpec.KeyName(box.Pending.Vk) +
                          " on layouts that have AltGr (German, French, Polish, Nordic and " +
                          "others). It would cost that character to anyone you pass this on to.");
            }
        }

        // Nothing to say when there is nothing wrong. This used to carry a
        // standing note that Apply reports combinations Windows refuses, which
        // is a warning about a thing that has not happened, sitting under the
        // boxes on every visit. Apply still reports them -- at the moment they
        // actually fail, which is when the reader can do something about it.
        _status.ForeColor = notes.Count == 0 ? SystemColors.GrayText : Color.FromArgb(160, 80, 0);
        _status.Text = notes.Count == 0 ? "" : string.Join("\n", notes.ToArray());
    }

    /// <summary>
    /// Try every changed binding. Anything Windows refuses is reported by name and
    /// reverted by HotkeyManager, so the dialog can stay open on partial failure.
    /// </summary>
    private void Apply()
    {
        var failures = new List<string>();
        foreach (var box in _boxes)
        {
            if (box.Pending.Mods == box.Binding.Spec.Mods &&
                box.Pending.Vk == box.Binding.Spec.Vk && box.Binding.Registered) continue;
            string err;
            if (!_mgr.Rebind(box.Binding, box.Pending, out err))
                failures.Add(box.Binding.Label + " (" + box.Pending + "): " + err);
        }

        if (failures.Count > 0)
        {
            DialogResult = DialogResult.None;
            _status.ForeColor = Color.FromArgb(180, 0, 0);
            _status.Text = "Kept the previous shortcut for:\n" + string.Join("\n", failures.ToArray());
            foreach (var box in _boxes) { box.Pending = box.Binding.Spec; box.Text = box.Pending.ToString(); }
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
