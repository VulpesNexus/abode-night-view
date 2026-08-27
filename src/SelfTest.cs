// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – self-test for the parts that have no visible output
// ----------------------------------------------------------------------------
//  Hotkey parsing, hotkey registration, and settings persistence are all code
//  paths whose failure mode is silence: a shortcut that does nothing, a setting
//  that quietly reverts. This exercises them directly, including the two things
//  that can only be tested against the real API -- that a collision is DETECTED
//  rather than silently swallowed, and that a rejected rebind leaves the
//  previous shortcut still working.
//
//  Not shipped. Development tree only.  Audit.exe --selftest
// ============================================================================

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

internal static class SelfTest
{
    static int _pass, _fail;

    static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what +
                          (detail == null ? "" : "  " + detail));
        if (ok) _pass++; else _fail++;
    }

    public static int Run()
    {
        Console.WriteLine("Abode Night View – self-test");
        Console.WriteLine("============================");
        Console.WriteLine();

        Parsing();
        Console.WriteLine();
        Registration();
        Console.WriteLine();
        Settings();
        Console.WriteLine();
        Migration();
        Console.WriteLine();
        Adapters();
        Console.WriteLine();
        Schedule();
        Console.WriteLine();
        Notification();
        Console.WriteLine();
        MenuState();
        Console.WriteLine();
        GlobalState();
        Console.WriteLine();
        RetiredModes();
        Console.WriteLine();
        Spacing();
        Console.WriteLine();
        About();

        Console.WriteLine();
        Console.WriteLine("  " + _pass + " passed, " + _fail + " failed.");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- parsing

    static void Parsing()
    {
        Console.WriteLine("  Hotkey parsing");
        Console.WriteLine("  ------------------------------------------------------------------");

        // Accepted forms, with the canonical text each should print back as.
        string[,] good =
        {
            { "Ctrl+Alt+N",        "Ctrl+Alt+N" },
            { "ctrl+alt+n",        "Ctrl+Alt+N" },
            { "  Ctrl + Alt + N ", "Ctrl+Alt+N" },
            { "Ctrl+Alt+Up",       "Ctrl+Alt+Up" },
            { "Ctrl+Shift+F9",     "Ctrl+Shift+F9" },
            { "Win+Alt+D",         "Alt+Win+D" },
            { "Ctrl+Alt+Num5",     "Ctrl+Alt+Num5" },
            { "Ctrl+Alt+VK:0x4E",  "Ctrl+Alt+N" },     // raw code, layout-proof
            { "Alt+Space",         "Alt+Space" },
            { "Ctrl+Alt+PgDn",     "Ctrl+Alt+PageDown" },
            { "Ctrl+Alt+PageDown", "Ctrl+Alt+PageDown" },
            { "Ctrl+Alt+Esc",      "Ctrl+Alt+Esc" },
            { "Ctrl+Alt+NumAdd",   "Ctrl+Alt+NumAdd" },
        };
        for (int i = 0; i < good.GetLength(0); i++)
        {
            HotkeySpec s; string err;
            bool ok = HotkeySpec.TryParse(good[i, 0], out s, out err);
            string got = ok ? s.ToString() : "(" + err + ")";
            bool matches = ok && got == good[i, 1];
            Check(matches, "parse \"" + good[i, 0] + "\"", "-> " + got);
        }

        // Rejected forms. Each must come back with a reason, never an exception.
        string[] bad = { "", "Ctrl", "Ctrl+Alt", "N", "F5", "Ctrl+Alt+Nonsense",
                         "Ctrl+Alt+N+M", "%%%", "VK:0x999" };
        foreach (string b in bad)
        {
            HotkeySpec s; string err;
            bool ok = HotkeySpec.TryParse(b, out s, out err);
            Check(!ok, "reject \"" + b + "\"", ok ? "ACCEPTED as " + s : err);
        }

        // Round trip: whatever we print must parse back to the same thing, or the
        // settings file would drift every time it is saved and reloaded.
        foreach (string t in new[] { "Ctrl+Alt+N", "Ctrl+Alt+Up", "Ctrl+Alt+Down",
                                     "Ctrl+Alt+Q", "Ctrl+Shift+Alt+F12", "Ctrl+Alt+NumPad0" })
        {
            HotkeySpec a, b2; string e1, e2;
            bool ok = HotkeySpec.TryParse(t, out a, out e1) &&
                      HotkeySpec.TryParse(a.ToString(), out b2, out e2) &&
                      a.Mods == b2.Mods && a.Vk == b2.Vk;
            Check(ok, "round trip \"" + t + "\"", ok ? a.ToString() : "drifted");
        }

        // The AltGr warning must fire on Ctrl+Alt+<printable> and nowhere else,
        // or it is either useless or noise.
        var altgr = new[] { "Ctrl+Alt+N", "Ctrl+Alt+5", "Ctrl+Alt+Period" };
        var safe = new[] { "Ctrl+Alt+F9", "Ctrl+Alt+Num5", "Ctrl+Alt+Up",
                           "Ctrl+Shift+N", "Win+Alt+N" };
        foreach (string t in altgr)
        {
            HotkeySpec s; string e;
            HotkeySpec.TryParse(t, out s, out e);
            Check(s.CollidesWithAltGr, "AltGr warning fires for " + t, null);
        }
        foreach (string t in safe)
        {
            HotkeySpec s; string e;
            HotkeySpec.TryParse(t, out s, out e);
            Check(!s.CollidesWithAltGr, "AltGr warning stays quiet for " + t, null);
        }
    }

    // -------------------------------------------------------- registration

    static void Registration()
    {
        Console.WriteLine("  Hotkey registration (against the real Win32 API)");
        Console.WriteLine("  ------------------------------------------------------------------");

        var mgr = new HotkeyManager(id => { });
        var a = new HotkeyBinding(101, "test.a", "Test A", "Ctrl+Alt+Shift+F9", true);
        HotkeySpec sp; string err;
        HotkeySpec.TryParse(a.Default, out sp, out err);
        a.Spec = sp;
        mgr.Add(a);
        int failed = mgr.RegisterAll();
        Check(failed == 0 && a.Registered, "register an unused combination",
              a.Registered ? a.Spec.ToString() : a.Error);

        // A SECOND owner of the same combination must be refused, and must say so.
        var mgr2 = new HotkeyManager(id => { });
        var b = new HotkeyBinding(102, "test.b", "Test B", "Ctrl+Alt+Shift+F9", true);
        b.Spec = sp;
        mgr2.Add(b);
        mgr2.RegisterAll();
        Check(!b.Registered && b.Error != null,
              "a combination already taken is refused, with a reason",
              b.Error ?? "REGISTERED ANYWAY - a silent double registration");

        // Rebinding onto a taken combination must fail AND leave the old one live.
        var c = new HotkeyBinding(103, "test.c", "Test C", "Ctrl+Alt+Shift+F10", true);
        HotkeySpec spc; HotkeySpec.TryParse(c.Default, out spc, out err);
        c.Spec = spc;
        mgr2.Add(c);
        mgr2.RegisterAll();
        bool cWas = c.Registered;
        string rerr;
        bool moved = mgr2.Rebind(c, sp, out rerr);       // onto the taken one
        Check(!moved && c.Spec.ToString() == "Ctrl+Alt+Shift+F10" && c.Registered,
              "a rejected rebind reverts and keeps the previous shortcut working",
              (cWas ? "" : "(was not registered to begin with) ") +
              "spec=" + c.Spec + " registered=" + c.Registered + " err=" + rerr);

        // A successful rebind must release the old combination, or Abode Night View
        // would leak registrations and eventually hold keys nobody asked for.
        HotkeySpec spd; HotkeySpec.TryParse("Ctrl+Alt+Shift+F11", out spd, out err);
        bool ok2 = mgr2.Rebind(c, spd, out rerr);
        var probe = new HotkeyManager(id => { });
        var p = new HotkeyBinding(104, "test.p", "Probe", "Ctrl+Alt+Shift+F10", true);
        p.Spec = spc;
        probe.Add(p);
        probe.RegisterAll();
        Check(ok2 && p.Registered, "a successful rebind releases the old combination",
              ok2 ? (p.Registered ? "old combination is free again" : "STILL HELD: " + p.Error)
                  : "rebind failed: " + rerr);
        probe.Dispose();

        // Dispose must release everything.
        mgr.Dispose(); mgr2.Dispose();
        var after = new HotkeyManager(id => { });
        var q = new HotkeyBinding(105, "test.q", "Probe2", "Ctrl+Alt+Shift+F9", true);
        q.Spec = sp;
        after.Add(q);
        after.RegisterAll();
        Check(q.Registered, "Dispose releases every registration",
              q.Registered ? null : q.Error);
        after.Dispose();

        // The editor is only ever reached by clicking a tray menu item, so a
        // constructor or layout fault in it would never show up in any other test.
        // Build it, realise its handle, lay it out, and take it down again.
        try
        {
            var mgr3 = new HotkeyManager(id => { });
            foreach (var name in new[] { "toggle", "brighter", "darker", "quit" })
            {
                var bind = new HotkeyBinding(200 + name.Length, "hotkey." + name, name,
                                             "Ctrl+Alt+Shift+F" + (13 + name.Length), true);
                HotkeySpec s3; string e3;
                HotkeySpec.TryParse("Ctrl+Alt+N", out s3, out e3);
                bind.Spec = s3;
                mgr3.Add(bind);
            }
            using (var dlg = new HotkeyEditor(mgr3))
            {
                dlg.Show();
                for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                bool laidOut = dlg.ClientSize.Width > 100 && dlg.ClientSize.Height > 100;
                Check(laidOut, "the Shortcuts dialog builds, lays out and closes",
                      "client size " + dlg.ClientSize.Width + "x" + dlg.ClientSize.Height);

                // The reported fault was text that read as cut off. Whatever
                // the label says, the window has to be wide enough to draw all
                // of it, and every line has to fit inside the label.
                // PreferredSize is WinForms' OWN answer to "how big does this
                // label need to be", wrapping included. Measuring the text a
                // second way here would only test that two measurements agree.
                bool fits = true; string worst = null;
                foreach (Control ctl in Walk(dlg))
                {
                    var lbl = ctl as Label;
                    if (lbl == null || !lbl.AutoSize) continue;
                    Size need = lbl.PreferredSize;
                    if (need.Width <= lbl.Width && need.Height <= lbl.Height) continue;
                    fits = false;
                    worst = "\"" + lbl.Text.Substring(0, Math.Min(30, lbl.Text.Length)) +
                            "...\" needs " + need.Width + "x" + need.Height +
                            ", has " + lbl.Width + "x" + lbl.Height;
                }
                Check(fits, "and every line of its text fits inside the window",
                      worst ?? "all labels fit");

                // The explanation is a RichLabel so that Esc and Backspace can
                // be set apart from the sentence they are in. What has to be
                // true of it: the markup is markup and not something the reader
                // sees, and the reader sees the sentences we think they see.
                RichLabel blurb = null;
                foreach (Control ctl in Walk(dlg))
                { var r = ctl as RichLabel; if (r != null) { blurb = r; break; } }

                Check(blurb != null, "the explanation is a RichLabel", null);
                if (blurb != null)
                {
                    Check(blurb.Text.Contains("<i>Esc</i>") && blurb.Text.Contains("<i>Backspace</i>"),
                          "with the key names marked up for italics", null);
                    Check(blurb.PlainText.IndexOf('<') < 0,
                          "and no markup left anywhere in what is read out", blurb.PlainText);
                    Check(blurb.PlainText.Contains("Esc cancels that row.") &&
                          blurb.PlainText.Contains("Backspace clears it.") &&
                          blurb.PlainText.Contains("By default, nothing is bound."),
                          "the three things it has to say are all in it", null);
                }

                // The standing note about Apply is gone: an error is reported
                // when there IS one, not pre-announced on every visit.
                bool preannounced = false;
                foreach (Control ctl in Walk(dlg))
                    if ((ctl.Text ?? "").IndexOf("can only be known by trying it",
                                                 StringComparison.Ordinal) >= 0) preannounced = true;
                Check(!preannounced, "and nothing warns about failures that have not happened", null);

                dlg.Close();
            }
            mgr3.Dispose();
        }
        catch (Exception ex)
        {
            Check(false, "the Shortcuts dialog builds, lays out and closes",
                  ex.GetType().Name + ": " + ex.Message);
        }

        RichText();

        // Nothing is bound out of the box, which makes "unset" the SHIPPED state
        // rather than an error case. It has to be distinguishable from "asked
        // for and refused" everywhere it is reported, or the first thing a new
        // user sees is a warning that four shortcuts are unavailable.
        var blank = new HotkeyManager(id => { });
        var u = new HotkeyBinding(107, "test.u", "Unset", "", true);
        HotkeySpec none; string ne;
        HotkeySpec.TryParse("", out none, out ne);
        u.Spec = none;
        blank.Add(u);
        int blankFailures = blank.RegisterAll();
        Check(u.Unset && !u.Registered && !u.Failed && u.Error == null,
              "an unbound action is unset, not failed", "Error=" + (u.Error ?? "(null)"));
        Check(blankFailures == 0,
              "and it is not counted as a shortcut the user did not get", null);

        // Clearing a binding has to actually clear it. This used to fall through
        // to Register, come back false, and silently restore the old
        // combination -- so a shortcut could be set and never unset.
        HotkeySpec keep; HotkeySpec.TryParse("Ctrl+Alt+Shift+F14", out keep, out err);
        var v = new HotkeyBinding(108, "test.v", "Clearable", "Ctrl+Alt+Shift+F14", true);
        v.Spec = keep;
        var live = new HotkeyManager(id => { });
        live.Add(v);
        live.RegisterAll();
        string cleared;
        bool clearedOk = live.Rebind(v, new HotkeySpec(), out cleared);
        Check(clearedOk && v.Unset && !v.Registered,
              "clearing a bound shortcut clears it rather than restoring the old one",
              v.Spec.ToString());
        live.Dispose();
        blank.Dispose();

        // The layout probe must not fire on a combination this keyboard cannot
        // produce with AltGr, and must not throw on any input. What it answers
        // depends on the layout loaded right now, so the assertion is that it
        // answers at all and stays quiet where AltGr is not even involved.
        string typed;
        HotkeySpec plainShift; HotkeySpec.TryParse("Ctrl+Shift+F9", out plainShift, out err);
        Check(!plainShift.AltGrIsUsedHere(out typed),
              "the live-layout AltGr probe stays quiet where there is no Ctrl+Alt", null);
        HotkeySpec fkey; HotkeySpec.TryParse("Ctrl+Alt+F9", out fkey, out err);
        Check(!fkey.AltGrIsUsedHere(out typed),
              "and on a function key, which is not an AltGr character on any layout", null);
        HotkeySpec letter; HotkeySpec.TryParse("Ctrl+Alt+N", out letter, out err);
        letter.AltGrIsUsedHere(out typed);          // must not throw whatever it answers
        Check(true, "and it answers for Ctrl+Alt+<letter> without throwing",
              typed == null ? "this layout types nothing there" : "this layout types \u201C" + typed + "\u201D");
    }

    // ------------------------------------------------------------ schedule

    /// <summary>
    /// The schedule is arithmetic on a wall clock, which is exactly the kind of
    /// thing that looks obviously right and is wrong at midnight. The wrapping
    /// range is the normal case here, not the corner case, so it is tested
    /// first and hardest.
    /// </summary>
    static void Schedule()
    {
        Console.WriteLine("  Schedule");
        Console.WriteLine("  ------------------------------------------------------------------");

        ClockTime t;
        Check(ClockTime.TryParse("20:00", out t) && t.Hour == 20 && t.Minute == 0,
              "\"20:00\" parses", t.ToString());
        Check(ClockTime.TryParse("8:5", out t) && t.Hour == 8 && t.Minute == 5,
              "\"8:5\" parses and prints back padded", t.ToString());
        Check(ClockTime.TryParse("0800", out t) && t.Hour == 8 && t.Minute == 0,
              "a four-digit time parses", t.ToString());
        Check(ClockTime.TryParse("7", out t) && t.Hour == 7 && t.Minute == 0,
              "a bare hour parses", t.ToString());
        Check(ClockTime.TryParse("8h30", out t) && t.Hour == 8 && t.Minute == 30,
              "and so does 8h30", t.ToString());
        foreach (string bad in new[] { "", "24:00", "12:60", "-1:00", "banana", "12:xx" })
            Check(!ClockTime.TryParse(bad, out t), "reject \"" + bad + "\"", null);
        Check(ClockTime.Of(7, 0).ToString() == "07:00" && ClockTime.Of(20, 5).ToString() == "20:05",
              "times print zero-padded and 24-hour, on every machine", null);

        // The wrapping range: 20:00 to 07:00 is a night, and every hour of it
        // has to be inside.
        var night = new NightSchedule { Active = true, From = ClockTime.Of(20, 0), To = ClockTime.Of(7, 0) };
        DateTime day = new DateTime(2026, 8, 27);
        Check(night.Covers(day.AddHours(23)), "23:00 is inside 20:00-07:00", null);
        Check(night.Covers(day.AddHours(3)), "03:00 is inside it (the range wrapped midnight)", null);
        Check(night.Covers(day.AddHours(20)), "20:00 itself is inside: the start is included", null);
        Check(!night.Covers(day.AddHours(7)), "07:00 is outside: the end is excluded", null);
        Check(!night.Covers(day.AddHours(12)), "and midday is outside", null);

        // The non-wrapping range falls out of the same expression.
        var daytime = new NightSchedule { Active = true, From = ClockTime.Of(9, 0), To = ClockTime.Of(17, 0) };
        Check(daytime.Covers(day.AddHours(12)), "midday is inside 09:00-17:00", null);
        Check(!daytime.Covers(day.AddHours(3)), "and 03:00 is not", null);

        var whole = new NightSchedule { Active = true, From = ClockTime.Of(0, 0), To = ClockTime.Of(0, 0) };
        Check(whole.Covers(day.AddHours(3)) && whole.Covers(day.AddHours(15)),
              "a zero-length range is the whole day, not none of it", null);

        DateTime next = night.NextChange(day.AddHours(23));
        Check(next == day.AddDays(1).AddHours(7),
              "at 23:00 the next change is 07:00 tomorrow", next.ToString("yyyy-MM-dd HH:mm"));
        next = night.NextChange(day.AddHours(12));
        Check(next == day.AddHours(20), "at midday it is 20:00 today",
              next.ToString("yyyy-MM-dd HH:mm"));
        next = night.NextChange(day.AddHours(20));
        Check(next == day.AddDays(1).AddHours(7),
              "and exactly on a boundary it is the NEXT one, not this one again",
              next.ToString("yyyy-MM-dd HH:mm"));

        Check(night.Range == "20:00 – 07:00", "the range prints with an en dash", night.Range);
        Check(night.Status(day.AddHours(23)).StartsWith("On now"),
              "the status says what it is doing", night.Status(day.AddHours(23)));
        Check(night.Status(day.AddHours(12)).StartsWith("Off now"),
              "and in the other state", night.Status(day.AddHours(12)));

        // The editor shows Plan while a range is being typed, INCLUDING a range
        // typed with the schedule switched off, so it must not be in the present
        // tense and must not depend on Active at all.
        Check(night.Plan == "Dimming is set to switch on at 20:00 and off at 07:00.",
              "the plan states the range without claiming to be running", night.Plan);
        var parked = new NightSchedule { Active = false, From = ClockTime.Of(20, 0), To = ClockTime.Of(7, 0) };
        Check(parked.Plan == night.Plan,
              "and reads the same whether the schedule is running or not", parked.Plan);

        // The bug this replaced: a schedule that is switched off was still
        // counting down to a change it was never going to make.
        Check(parked.Status(day.AddHours(12)) == "Schedule is currently off.",
              "a schedule that is off says so, instead of counting down to nothing",
              parked.Status(day.AddHours(12)));
        Check(parked.Status(day.AddHours(23)).IndexOf("until", StringComparison.Ordinal) < 0,
              "at no hour of the day does an off schedule promise a change",
              parked.Status(day.AddHours(23)));

        // --schedule=
        NightSchedule cli = NightSchedule.Parse("20:00-07:00");
        Check(cli != null && cli.Active && cli.From.Hour == 20 && cli.To.Hour == 7,
              "--schedule=20:00-07:00 parses", cli == null ? "(null)" : cli.Range);
        cli = NightSchedule.Parse("20:00 – 07:00");
        Check(cli != null && cli.Active && cli.From.Hour == 20,
              "and so does the en-dash form the menu prints", cli == null ? "(null)" : cli.Range);
        Check(NightSchedule.Parse("off") != null && !NightSchedule.Parse("off").Active,
              "--schedule=off parses", null);
        Check(NightSchedule.Parse("nonsense") == null && NightSchedule.Parse("20:00-") == null,
              "a malformed range is refused rather than half-applied", null);

        // Through the real settings file.
        string realPath = Config.Path;
        string dir = Path.Combine(Path.GetTempPath(),
            "abodenv-sched-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "AbodeNightView.ini");
        try
        {
            Config.UseFile(ini);
            Config.Load();
            Check(!NightSchedule.Load().Active,
                  "a clean first boot has no schedule: nothing switches itself on unasked", null);

            night.Save();
            Config.Save();
            Config.Load();
            NightSchedule back = NightSchedule.Load();
            Check(back.Active && back.From.Minutes == night.From.Minutes &&
                  back.To.Minutes == night.To.Minutes,
                  "a schedule survives a save and reload", back.Range);

            File.WriteAllText(ini, "schema=3\nschedule=1\nschedule.from=banana\nschedule.to=\n");
            Config.Load();
            NightSchedule broken = NightSchedule.Load();
            Check(broken.From.Minutes == NightSchedule.DefaultFrom.Minutes &&
                  broken.To.Minutes == NightSchedule.DefaultTo.Minutes,
                  "a corrupted range falls back to the documented default", broken.Range);
        }
        finally
        {
            Config.UseFile(realPath);
            Config.Load();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // -------------------------------------------------------- notification

    /// <summary>
    /// The balloon carries the artwork for the state it is announcing, which
    /// takes a Win32 field Windows Forms never exposed and two private fields
    /// read by reflection. Every part of that can rot without anything visible
    /// breaking -- the fallback is a perfectly ordinary notification with a blue
    /// "i" on it -- so each part is asserted separately here.
    /// </summary>
    static void Notification()
    {
        Console.WriteLine("  Notification balloon");
        Console.WriteLine("  ------------------------------------------------------------------");

        // 1. The artwork is in the binary at all. A -resource: flag dropped from
        //    the build is invisible until somebody looks at a notification.
        byte[] off = Resource(Balloon.OffResource), on = Resource(Balloon.OnResource);
        Check(off != null && off.Length > 0, "the switched-off artwork is embedded",
              off == null ? "MISSING" : off.Length + " bytes");
        Check(on != null && on.Length > 0, "the switched-on artwork is embedded",
              on == null ? "MISSING" : on.Length + " bytes");
        Check(off != null && on != null && !Same(off, on),
              "and they are two different pictures, not the same file wired up twice", null);

        // 2. It loads as an icon with a live HICON, which is the only form the
        //    shell will take a balloon icon in.
        Icon aOff = Balloon.Art(false), aOn = Balloon.Art(true);
        Check(aOff != null && aOff.Handle != IntPtr.Zero,
              "the switched-off icon loads and has a handle",
              aOff == null ? "null" : aOff.Width + "x" + aOff.Height);
        Check(aOn != null && aOn.Handle != IntPtr.Zero,
              "the switched-on icon loads and has a handle",
              aOn == null ? "null" : aOn.Width + "x" + aOn.Height);
        Check(aOff != null && aOn != null && aOff.Handle != aOn.Handle,
              "and the two states are not the same icon", null);
        Check(aOn != null && aOn.Width >= SystemInformation.IconSize.Width,
              "the icon is at least the size the shell asks for",
              aOn == null ? "null" : aOn.Width + " vs SM_CXICON " + SystemInformation.IconSize.Width);
        Check(Balloon.Artwork.IndexOf("FAILED", StringComparison.Ordinal) < 0 &&
              Balloon.Artwork.IndexOf("NOT IN THIS BUILD", StringComparison.Ordinal) < 0,
              "and --diagnostics has nothing to complain about", Balloon.Artwork);

        // 3. The reflection. This is the part with a real chance of breaking on
        //    a future runtime, and the point of testing it is to find out from
        //    the harness rather than from a user's screenshot.
        string why;
        bool custom = Balloon.CanCustomise(out why);
        Check(custom, "NotifyIcon still exposes the window and id fields the shell call needs",
              custom ? null : why);

        // 4. Truncation. szInfo is a fixed-size field in the struct and the
        //    marshaller THROWS on an over-long string -- at the moment there is
        //    something to say, which is the worst possible moment.
        Check(Balloon.Fit("short", 255) == "short", "a short message is left alone", null);
        Check(Balloon.Fit(null, 255) == "" && Balloon.Fit("", 255) == "",
              "and nothing at all is not a crash", null);
        string big = new string('x', 40) + " " + new string('y', 400);
        string cut = Balloon.Fit(big, 255);
        Check(cut.Length <= 255, "an over-long message is cut to what the field holds",
              cut.Length + " chars");
        Check(cut.EndsWith("\u2026"), "and says that it was cut", null);
        string wordy = string.Join(" ", Words(60));
        string cutWordy = Balloon.Fit(wordy, 100);
        Check(cutWordy.Length <= 100 && !cutWordy.Contains("  "),
              "a cut lands on a word boundary when there is one", cutWordy.Length + " chars");
    }

    static string[] Words(int n)
    {
        var w = new string[n];
        for (int i = 0; i < n; i++) w[i] = "word" + i;
        return w;
    }

    /// <summary>An embedded resource as bytes, or null if it is not there.</summary>
    static byte[] Resource(string name)
    {
        using (Stream s = System.Reflection.Assembly.GetExecutingAssembly()
                                                    .GetManifestResourceStream(name))
        {
            if (s == null) return null;
            var buf = new byte[s.Length];
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return buf;
        }
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// RichLabel lays out its own words, which means it also has to MEASURE its
    /// own words, and the obvious way of doing that is wrong: MeasureText
    /// returns the real width plus a per-font overhang constant, so adding it up
    /// word by word adds the constant once per word and puts a visible double
    /// space between every pair. That is the regression this guards.
    /// </summary>
    static void RichText()
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding |
                                      TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var unbounded = new Size(int.MaxValue, int.MaxValue);

        using (var rl = new RichLabel())
        {
            rl.Text = "one <i>two</i> three";
            Check(rl.PlainText == "one two three",
                  "RichLabel reports its text without the markup in it", rl.PlainText);

            // Word by word must add up to what the line measures in one call.
            rl.Text = "Click a box and press the desired combination.";
            int mine = rl.PreferredSize.Width;
            int whole = TextRenderer.MeasureText(rl.Text, rl.Font, unbounded, flags).Width;
            Check(Math.Abs(mine - whole) <= 3,
                  "and lays out a line to the same width as measuring it in one go",
                  "word by word " + mine + ", whole line " + whole);

            // Wrapping stays inside the width it was given, and pays for it in
            // height rather than by running off the edge.
            int oneLine = rl.PreferredSize.Height;
            rl.MaximumSize = new Size(120, 0);
            Size wrapped = rl.PreferredSize;
            Check(wrapped.Width <= 120, "it wraps inside the width it is given", wrapped.ToString());
            Check(wrapped.Height > oneLine, "and gets taller instead of being cut off",
                  wrapped.Height + " vs " + oneLine);

            // A blank line is a blank line, not a no-op.
            rl.MaximumSize = Size.Empty;
            rl.Text = "a";
            int single = rl.PreferredSize.Height;
            rl.Text = "a\nb";
            int two = rl.PreferredSize.Height;
            rl.Text = "a\n\nb";
            int three = rl.PreferredSize.Height;
            Check(two > single && three > two,
                  "a paragraph break leaves an empty line between the paragraphs",
                  single + " / " + two + " / " + three);
        }
    }

    /// <summary>Every control in a form, however deeply nested.</summary>
    static System.Collections.Generic.List<Control> Walk(Control root)
    {
        var all = new System.Collections.Generic.List<Control>();
        foreach (Control c in root.Controls) { all.Add(c); all.AddRange(Walk(c)); }
        return all;
    }

    // ------------------------------------------------------------ settings

    static void Settings()
    {
        Console.WriteLine("  Settings persistence and corruption tolerance");
        Console.WriteLine("  ------------------------------------------------------------------");

        string path = Config.Path;
        Console.WriteLine("         settings file: " + path);

        // A file full of junk must load as defaults, not throw and not poison a
        // value. The strength case matters most: an out-of-range number used to
        // go straight to the alpha and could produce an opaque black rectangle.
        string tmp = Path.Combine(Path.GetTempPath(), "AbodeNV-selftest.ini");
        File.WriteAllText(tmp,
            "this line has no equals sign\n" +
            "[a section header]\n" +
            "# a comment\n" +
            "; another comment\n" +
            "strength=99999\n" +
            "warm=-40\n" +
            "enabled=banana\n" +
            "mode=\n" +
            "hotkey.toggle=Ctrl+Alt+Nonsense\n" +
            "\0\0\0binary junk\0\0\n");

        // Point the real loader at the junk file. Anything less than this is
        // testing a copy of the logic rather than the logic.
        string realPath = Config.Path;
        try
        {
            Config.UseFile(tmp);
            Config.Load();
            Check(Config.Int("strength", 55, 0, 90) == 90,
                  "an absurd strength clamps to the maximum",
                  "99999 -> " + Config.Int("strength", 55, 0, 90));
            Check(Config.Int("warm", 60, 0, 255) == 0,
                  "a negative value clamps to the minimum",
                  "-40 -> " + Config.Int("warm", 60, 0, 255));
            Check(Config.Int("nosuchkey", 42, 0, 90) == 42,
                  "a missing key returns the default", null);
            // This used to assert that "banana" read as FALSE, which is what the
            // old parser did: anything that was not 1/true/yes was false. A
            // corrupted settings file therefore switched the utility off silently
            // and looked like a broken build. Corruption must fall back to the
            // DEFAULT, not to one particular value.
            Check(Config.Bool("enabled", true) == true,
                  "a non-boolean value falls back to the default rather than to false",
                  "banana -> " + Config.Bool("enabled", true));
            Check(Config.Bool("enabled", false) == false,
                  "...and the default is honoured in both directions", null);
            Check(Config.Str("mode", "neutral") == "neutral",
                  "an empty value falls back to the default", null);
            Check(Config.Str("this line has no equals sign", "?") == "?",
                  "a line with no separator is ignored", null);
            Check(Config.Str("# a comment", "?") == "?" &&
                  Config.Str("[a section header]", "?") == "?",
                  "comments and section headers are ignored", null);

            HotkeySpec s2; string err2;
            bool parsed2 = HotkeySpec.TryParse(Config.Str("hotkey.toggle", "Ctrl+Alt+N"),
                                               out s2, out err2);
            Check(!parsed2, "a corrupted hotkey line is rejected, leaving the action unbound", err2);

            // A file that does not exist at all must load as empty, not throw.
            string gone = Path.Combine(Path.GetTempPath(), "AbodeNV-selftest-absent.ini");
            try { File.Delete(gone); } catch { }
            Config.UseFile(gone);
            Config.Load();
            Check(Config.Int("strength", 55, 0, 90) == 55,
                  "a missing settings file loads as defaults", null);

            // And a save into a directory that cannot exist must be reported.
            Config.UseFile(Path.Combine(@"Z:\nonexistent-drive-for-the-test", "AbodeNightView.ini"));
            Config.Save();
            Check(!Config.LastSaveOk && Config.LastSaveError != null,
                  "a failed save is recorded rather than thrown",
                  Config.LastSaveError);
        }
        finally
        {
            Config.UseFile(realPath);
            Config.Load();
            try { File.Delete(tmp); } catch { }
        }
    }


    // ----------------------------------------------------------- migration

    /// <summary>
    /// A Night View 1.0 settings file must come forward without the user losing
    /// anything, and the one key whose MEANING changed must be translated rather
    /// than carried over verbatim.
    ///
    /// Schema 3 is the other direction: something has to be TAKEN AWAY, and
    /// only from the users who never chose it. That is the whole difficulty of
    /// withdrawing a default, and it is what the second half of this tests.
    /// </summary>
    static void Migration()
    {
        Console.WriteLine("  Settings migration from Night View 1.0");
        Console.WriteLine("  ------------------------------------------------------------------");

        WithdrawnDefaults();

        string realPath = Config.Path;
        string dir = Path.Combine(Path.GetTempPath(),
            "abodenv-migrate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string old = Path.Combine(dir, "NightView.ini");
            File.WriteAllText(old,
                "# Night View settings.\n" +
                "strength=35\n" +
                "mode=warm\n" +
                "warm=77\n" +
                "target=owldoc\n" +
                "enabled=0\n" +
                "captures=0\n" +
                "zmode=above\n" +
                "hotkey.toggle=Ctrl+Alt+J\n" +
                "something.from.the.future=42\n");

            Config.UseFile(Path.Combine(dir, "AbodeNightView.ini"));
            Config.Load();

            Check(Config.MigratedFrom != null, "a Night View 1.0 file next to the new one is found",
                  Config.MigratedFrom);
            Check(Config.Int("strength", 55, 0, 90) == 35, "strength survives migration",
                  "35 -> " + Config.Int("strength", 55, 0, 90));
            Check(Config.Str("mode", "neutral") == "warm" && Config.Int("warm", 60, 0, 255) == 77,
                  "mode and warmth survive migration", null);
            Check(Config.Bool("enabled", true) == false, "the off state survives migration", null);
            Check(Config.Str("zmode", "owned") == "above", "the z-order mode survives migration", null);
            Check(Config.Str("hotkey.toggle", "?") == "Ctrl+Alt+J",
                  "a customised shortcut survives migration", null);

            // The one key that changed meaning: "target" named a region, and there
            // was only one product it could apply to.
            Check(Config.Str("region.indesign", "?") == Region.Document,
                  "target=owldoc becomes region.indesign=document",
                  Config.Str("region.indesign", "(absent)"));
            Check(Config.Str("target", "(gone)") == "(gone)",
                  "the ambiguous old key is not left behind", null);
            Check(Config.Str("something.from.the.future", "?") == "42",
                  "an unknown key is preserved, not discarded", null);

            // Saving must produce the new file and leave the old one alone, so a
            // downgrade is still possible.
            Config.Save();
            Check(Config.LastSaveOk && File.Exists(Path.Combine(dir, "AbodeNightView.ini")),
                  "the migrated settings are written to the new file", null);
            Check(File.Exists(old), "the Night View 1.0 file is left in place", null);

            // And a second load must NOT re-import: the new file wins.
            Config.UseFile(Path.Combine(dir, "AbodeNightView.ini"));
            Config.Load();
            Check(Config.MigratedFrom == null, "migration happens once, not on every launch", null);
            Check(Config.Int("strength", 55, 0, 90) == 35, "the migrated value is what loads", null);
            Check(Config.Int("schema", 0, 0, 99) == Config.Schema,
                  "a schema version is recorded", "schema=" + Config.Int("schema", 0, 0, 99));
        }
        finally
        {
            Config.UseFile(realPath);
            Config.Load();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The 1.2 default shortcuts were Ctrl+Alt+&lt;key&gt;, which IS AltGr on every
    /// layout that has one, so they were withdrawn. Withdrawing a default means
    /// deleting a line out of settings files that already exist, and the line
    /// looks identical whether the user chose it or inherited it. The only
    /// honest discriminator is "is it still exactly what we wrote?" -- which is
    /// what this asserts, in both directions.
    /// </summary>
    static void WithdrawnDefaults()
    {
        string realPath = Config.Path;
        string dir = Path.Combine(Path.GetTempPath(),
            "abodenv-withdraw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "AbodeNightView.ini");
        try
        {
            // A 1.2 file that was never touched: all four are the shipped
            // defaults and all four go.
            File.WriteAllText(ini,
                "schema=2\n" +
                "hotkey.toggle=Ctrl+Alt+N\n" +
                "hotkey.brighter=Ctrl+Alt+Up\n" +
                "hotkey.darker=Ctrl+Alt+Down\n" +
                "hotkey.quit=Ctrl+Alt+Q\n" +
                "strength=55\n");
            Config.UseFile(ini);
            Config.Load();
            Check(Config.Str("hotkey.toggle", "") == "" && Config.Str("hotkey.quit", "") == "",
                  "an untouched 1.2 file loses the four Ctrl+Alt defaults it never chose",
                  "dropped " + Config.DroppedHotkeys);
            Check(Config.DroppedHotkeys == 4,
                  "and the user is told how many, rather than finding out by pressing one",
                  Config.DroppedHotkeys.ToString());
            Check(Config.Int("strength", 0, 0, 90) == 55,
                  "everything else in the file is untouched", null);

            // A file where the user picked their own: theirs is a choice, and a
            // choice is not ours to withdraw.
            File.WriteAllText(ini,
                "schema=2\n" +
                "hotkey.toggle=Ctrl+Shift+F9\n" +
                "hotkey.quit=Ctrl+Alt+Q\n");
            Config.Load();
            Check(Config.Str("hotkey.toggle", "") == "Ctrl+Shift+F9",
                  "a shortcut the user set themselves survives the withdrawal",
                  Config.Str("hotkey.toggle", "(gone)"));
            Check(Config.Str("hotkey.quit", "") == "" && Config.DroppedHotkeys == 1,
                  "and only the one they never touched is dropped", null);

            // Idempotent: a file already at schema 3 is left alone, so somebody
            // who deliberately binds Ctrl+Alt+N back keeps it.
            File.WriteAllText(ini, "schema=3\nhotkey.toggle=Ctrl+Alt+N\n");
            Config.Load();
            Check(Config.Str("hotkey.toggle", "") == "Ctrl+Alt+N" && Config.DroppedHotkeys == 0,
                  "and rebinding one deliberately, at schema 3, is not undone on the next launch",
                  Config.Str("hotkey.toggle", "(gone)"));
        }
        finally
        {
            Config.UseFile(realPath);
            Config.Load();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ------------------------------------------------------------ adapters

    /// <summary>
    /// The target registry is data as much as code, and the failure mode of a
    /// mistake in it is a product that silently never attaches.
    /// </summary>
    static void Adapters()
    {
        Console.WriteLine("  Target adapters");
        Console.WriteLine("  ------------------------------------------------------------------");

        var ids = new System.Collections.Generic.List<string>();
        bool wellFormed = true;
        foreach (var t in TargetRegistry.All)
        {
            if (string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Family) ||
                t.ProcessNames.Length == 0 || t.FrameClasses.Length == 0 ||
                string.IsNullOrEmpty(t.ExpectedStructure)) wellFormed = false;
            if (Region.Normalize(t.DefaultRegion) == null) wellFormed = false;
            if (ids.Contains(t.Id)) wellFormed = false;
            ids.Add(t.Id);
            if (TargetRegistry.ById(t.Id) != t) wellFormed = false;
        }
        Check(wellFormed, "every adapter is well formed and uniquely addressable",
              string.Join(", ", ids.ToArray()));
        Check(TargetRegistry.ById("nosuchproduct") == null,
              "an unknown product id resolves to nothing rather than to the first one", null);

        // Regions, including the one spelling Night View 1.0 wrote.
        Check(Region.Normalize("owldoc") == Region.Document,
              "the legacy region name maps forward", null);
        Check(Region.Normalize("  CANVAS  ") == Region.Canvas,
              "region names are trimmed and case-insensitive", null);
        Check(Region.Normalize("banana") == null,
              "an unknown region is rejected rather than guessed at", null);
        Check(Region.Normalize(null) == null && Region.Normalize("") == null,
              "an absent region is reported as absent, not silently defaulted", null);

        // The runtime adapter the audit harness uses.
        AdobeTarget g = GenericTarget.Parse("id:Family:proc:Cls");
        Check(g != null && g.Id == "id" && g.ProcessNames[0] == "proc" && g.FrameClasses[0] == "Cls",
              "--adapter= parses a four-field descriptor", null);
        AdobeTarget owl = GenericTarget.Parse("h:H:p:C:owl");
        Check(owl is OwlTarget, "--adapter=...:owl builds a real OWL adapter, not a stand-in", null);
        Check(GenericTarget.Parse("too:few") == null &&
              GenericTarget.Parse("") == null &&
              GenericTarget.Parse("a::c:d") == null,
              "a malformed --adapter= descriptor is refused", null);
        Check(TargetRegistry.ById("id") == null,
              "a runtime adapter is not in the shipped registry", null);

        // Photoshop is off by default on purpose, and that decision is part of the
        // product rather than an accident of ordering.
        AdobeTarget ps = TargetRegistry.ById("photoshop");
        Check(ps != null && !ps.DefaultEnabled && ps.SemanticNote != null,
              "Photoshop defaults off and says why", ps == null ? null : ps.SemanticNote);
        AdobeTarget id = TargetRegistry.ById("indesign");
        Check(id != null && id.DefaultEnabled && id.DefaultRegion == Region.Canvas,
              "InDesign keeps its verified default: on, canvas", null);
        Check(ps != null && ps.DefaultRegion == Region.Document,
              "Photoshop's default region is the whole document viewport",
              ps == null ? null : ps.DefaultRegion);

        // The tray menu lists products by the short name. "Adobe" in front of
        // every line sorts nothing and tells the reader nothing.
        bool shortened = true;
        var names = new System.Collections.Generic.List<string>();
        foreach (var t in TargetRegistry.All)
        {
            names.Add(t.ShortName);
            if (t.ShortName.StartsWith("Adobe", StringComparison.OrdinalIgnoreCase)) shortened = false;
            if (t.Family.IndexOf(t.ShortName, StringComparison.Ordinal) < 0) shortened = false;
        }
        Check(shortened, "every product has a short name with no \"Adobe\" in it",
              string.Join(", ", names.ToArray()));
        Check(TargetRegistry.ById("indesign").ShortName == "InDesign" &&
              TargetRegistry.ById("acrobat").ShortName == "Acrobat",
              "and it is the family name minus the word, not a second hand-typed list", null);
        Check(AdobeTarget.Shorten("Adobe InDesign 2026") == "InDesign 2026",
              "a ProductName read from a running executable is shortened the same way",
              AdobeTarget.Shorten("Adobe InDesign 2026"));
        Check(AdobeTarget.Shorten("InCopy") == "InCopy" && AdobeTarget.Shorten("") == "" &&
              AdobeTarget.Shorten(null) == null,
              "and a name with nothing to strip, or no name at all, survives it", null);

        // The full name is still what the reports print: --probe is read by
        // somebody who may not have the context the tray menu has.
        Check(TargetRegistry.ById("indesign").Family == "Adobe InDesign",
              "the full family name is kept for the probe and the diagnostics", null);

        // Statuses. Every product is NotRunning against an empty frame list, so
        // this asserts the shape of the answer rather than the machine's state.
        var survey = TargetRegistry.Survey(TargetRegistry.All,
                                           new System.Collections.Generic.List<DetectedFrame>());
        Check(survey.Count == TargetRegistry.All.Length,
              "the survey answers for every adapter, once each", survey.Count + " entries");
        Check(TargetRegistry.Explain(TargetStatus.Unsupported) == "unsupported version" &&
              TargetRegistry.Explain(TargetStatus.NoDocument) == "no document open" &&
              TargetRegistry.Explain(TargetStatus.NotRunning) == "not running",
              "the four statuses have one set of words between them", null);

        // The caller only tracks frames for products the user has switched ON,
        // so the survey is asked about products it was handed no frame for on
        // every single call. The answer must not depend on that: an unticked
        // Photoshop that is running and has no document open was being reported
        // as an unsupported version, which is a bug report about a version that
        // is fine.
        //
        // On a machine with no Adobe application running this passes trivially
        // -- both sides are all NotRunning -- and on one with any of them open
        // it is the whole of the fault.
        var frames = TargetRegistry.Discover(TargetRegistry.All);
        var withFrames = TargetRegistry.Survey(TargetRegistry.All, frames);
        var withNone = TargetRegistry.Survey(TargetRegistry.All,
                                             new System.Collections.Generic.List<DetectedFrame>());
        string disagreed = null;
        foreach (var t in TargetRegistry.All)
            if (withFrames[t.Id] != withNone[t.Id])
                disagreed = t.Id + ": " + TargetRegistry.Explain(withFrames[t.Id]) +
                            " with its frame, " + TargetRegistry.Explain(withNone[t.Id]) + " without";
        Check(disagreed == null,
              "the survey says the same thing whether a product is being tracked or not",
              disagreed ?? (frames.Count + " frame(s) on this desktop"));

        // And the other half of the same fault: whatever the survey says about a
        // running product, --probe has to agree, because they are the two places
        // a user reads the answer and there is no way to tell which is lying.
        string mismatch = null;
        foreach (var d in frames)
        {
            TargetStatus direct = d.Adapter.Inspect(d.Frame);
            if (Rank(withFrames[d.Adapter.Id]) < Rank(direct))
                mismatch = d.Adapter.Id + ": survey says " +
                           TargetRegistry.Explain(withFrames[d.Adapter.Id]) +
                           ", its own window says " + TargetRegistry.Explain(direct);
        }
        Check(mismatch == null, "and never a worse answer than the window itself gives",
              mismatch ?? "nothing to contradict");
    }

    /// <summary>The survey's own ordering, restated here so the test does not
    /// have to reach into it: attached beats no-document beats unsupported.</summary>
    static int Rank(TargetStatus s)
    {
        if (s == TargetStatus.Attached) return 3;
        if (s == TargetStatus.NoDocument) return 2;
        if (s == TargetStatus.Unsupported) return 1;
        return 0;
    }

    // ---------------------------------------------------- menu state on boot

    /// <summary>
    /// The reported bug was that Strength, Mode and Target all had the right
    /// RUNTIME values after a restart and the tray menu showed nothing ticked.
    /// The fix is structural -- every checkmark is now computed from live state
    /// when the menu opens, and nothing stores a tick -- so what is left to test
    /// is that the state a restart produces is the state the user last chose.
    ///
    /// That is what this does: write a settings file, load it through the real
    /// loader, and assert the exact values the menu will render, for each of the
    /// cases the menu can be wrong in.
    /// </summary>
    static void MenuState()
    {
        Console.WriteLine("  What a restart restores (what the tray menu renders)");
        Console.WriteLine("  ------------------------------------------------------------------");

        string realPath = Config.Path;
        string dir = Path.Combine(Path.GetTempPath(),
            "abodenv-menu-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "AbodeNightView.ini");

        AdobeTarget indesign = TargetRegistry.ById("indesign");
        AdobeTarget photoshop = TargetRegistry.ById("photoshop");
        AdobeTarget acrobat = TargetRegistry.ById("acrobat");

        try
        {
            // --- clean first boot: no file at all ---------------------------
            Config.UseFile(ini);
            Config.Load();
            Check(Config.Int("strength", 55, 0, 90) == 55 &&
                  Config.Str("mode", "neutral") == "neutral" &&
                  Config.Bool("enabled", true),
                  "clean first boot: the documented defaults are what loads", null);
            Check(ProductPrefs.Enabled(indesign) && !ProductPrefs.Enabled(photoshop),
                  "clean first boot: per-product defaults are the adapters' own", null);
            Check(ProductPrefs.RegionOf(indesign) == Region.Canvas &&
                  ProductPrefs.RegionOf(photoshop) == Region.Document,
                  "clean first boot: per-product regions are the adapters' own",
                  "indesign=" + ProductPrefs.RegionOf(indesign) +
                  " photoshop=" + ProductPrefs.RegionOf(photoshop));

            // --- normal restart after the user changed things ----------------
            File.WriteAllText(ini,
                "schema=2\n" +
                "strength=35\n" +
                "mode=warm\n" +
                "enabled=0\n" +
                "target.indesign=0\n" +
                "target.photoshop=1\n" +
                "region.indesign=window\n" +
                "region.photoshop=canvas\n");
            Config.Load();
            Check(Config.Int("strength", 55, 0, 90) == 35, "restart: strength is the last chosen value",
                  "35 -> " + Config.Int("strength", 55, 0, 90));
            Check(Config.Str("mode", "neutral") == "warm", "restart: mode is the last chosen value", null);
            Check(Config.Bool("enabled", true) == false, "restart: the off state is remembered", null);
            Check(!ProductPrefs.Enabled(indesign) && ProductPrefs.Enabled(photoshop),
                  "restart: per-product choices survive, including turning a default around", null);
            Check(ProductPrefs.RegionOf(indesign) == Region.Window &&
                  ProductPrefs.RegionOf(photoshop) == Region.Canvas,
                  "restart: per-product regions survive", null);
            Check(ProductPrefs.Enabled(acrobat) && ProductPrefs.RegionOf(acrobat) == Region.Canvas,
                  "restart: a product the file says nothing about keeps its default", null);

            // --- corrupted values -------------------------------------------
            File.WriteAllText(ini,
                "strength=not-a-number\n" +
                "mode=\n" +
                "enabled=perhaps\n" +
                "target.indesign=maybe\n" +
                "region.indesign=sideways\n");
            Config.Load();
            Check(Config.Int("strength", 55, 0, 90) == 55,
                  "corrupt: a non-numeric strength falls back to the default", null);
            Check(Config.Bool("enabled", true) == true,
                  "corrupt: a non-boolean enabled falls back to the default, not to off", null);
            Check(ProductPrefs.Enabled(indesign) == indesign.DefaultEnabled,
                  "corrupt: a non-boolean product toggle falls back to its default", null);
            Check(ProductPrefs.RegionOf(indesign) == Region.Canvas,
                  "corrupt: an unrecognised region falls back to the adapter's default",
                  ProductPrefs.RegionOf(indesign));

            // --- a file written by a version that does not exist yet ---------
            File.WriteAllText(ini,
                "schema=99\n" +
                "strength=45\n" +
                "mode=hyperspectral\n" +
                "target.indesign=1\n" +
                "target.somethingnew=1\n" +
                "region.indesign=hologram\n" +
                "brandnewkey=whatever\n");
            Config.Load();
            Check(Config.Int("strength", 45, 0, 90) == 45,
                  "future file: keys this version understands are still honoured", null);
            Check(Region.Normalize(Config.Str("region.indesign", "")) == null,
                  "future file: an unknown region is rejected, not guessed at", null);
            Check(ProductPrefs.RegionOf(indesign) == Region.Canvas,
                  "future file: and the product falls back to its own default", null);
            Check(Config.Str("brandnewkey", "?") == "whatever" &&
                  Config.Str("target.somethingnew", "?") == "1",
                  "future file: unknown keys are preserved, not discarded", null);
            Config.Save();
            string written = File.ReadAllText(ini);
            Check(written.Contains("brandnewkey=whatever") &&
                  written.Contains("target.somethingnew=1"),
                  "future file: and they survive a save by this version", null);
            Check(Config.Int("schema", 0, 0, 999) == Config.Schema,
                  "future file: the schema stamp is brought back to this version's",
                  "99 -> " + Config.Int("schema", 0, 0, 999));
        }
        finally
        {
            Config.UseFile(realPath);
            Config.Load();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
    // ----------------------------------------------- the global Enabled item

    /// <summary>
    /// The reported bug was that the top-level item read "Enabled" with no tick,
    /// in both states, so the menu never said whether the utility was actually on.
    /// There were two faults behind that and they are tested separately here:
    /// the item did not change its word, and the menu it lived in had no margin
    /// to draw a tick in, so Checked rendered as nothing at all.
    ///
    /// The second is the interesting one. Every checkmark in the program was
    /// already derived from live state and every value was already correct; the
    /// menu was styled such that none of them could be seen. No assertion about
    /// state could have caught it, which is why there is now an assertion about
    /// the menu being able to show state.
    /// </summary>
    static void GlobalState()
    {
        Console.WriteLine("  The global Enabled/Disabled item");
        Console.WriteLine("  ------------------------------------------------------------------");

        Check(TrayState.EnabledText(true) == "Enabled",
              "enabled=true renders the word \"Enabled\"", TrayState.EnabledText(true));
        Check(TrayState.EnabledText(false) == "Disabled",
              "enabled=false renders the word \"Disabled\"", TrayState.EnabledText(false));
        Check(TrayState.EnabledText(true) != TrayState.EnabledText(false),
              "the two states are distinguishable by the word alone", null);

        var menu = new ContextMenuStrip();
        TrayMenuStyle.Apply(menu);
        Check(TrayMenuStyle.CanDrawChecks(menu),
              "the tray menu has a margin to draw a checkmark in",
              "ShowCheckMargin=" + menu.ShowCheckMargin + " ShowImageMargin=" + menu.ShowImageMargin);

        var plain = new ContextMenuStrip();
        plain.ShowCheckMargin = false; plain.ShowImageMargin = false;
        Check(!TrayMenuStyle.CanDrawChecks(plain),
              "and the styling the bug came from is still recognised as broken", null);

        var item = new ToolStripMenuItem(TrayState.EnabledText(true));
        item.Checked = true;
        Check(item.Text == "Enabled" && item.Checked,
              "an ON item carries both the word and the tick", null);
        item.Text = TrayState.EnabledText(false); item.Checked = false;
        Check(item.Text == "Disabled" && !item.Checked,
              "an OFF item carries neither", null);
        menu.Dispose(); plain.Dispose(); item.Dispose();

        // Tooltip and menu must agree. They are given the same state and asked
        // separately; if the two ever disagree the tray is lying in one of them.
        for (int i = 0; i < 2; i++)
        {
            bool on = i == 0;
            string tip = TrayState.Tooltip(on, 55, 1, 1, 0, 0);
            string word = TrayState.EnabledText(on);
            Check(tip.Contains("[ON]") == (word == "Enabled") &&
                  tip.Contains("[OFF]") == (word == "Disabled"),
                  "tooltip and menu agree for enabled=" + on, tip + "  /  " + word);
        }

        Check(TrayState.Tooltip(true, 55, 1, 1, 0, 0) == "Abode Night View: [ON] | 55%",
              "tooltip is exactly the format asked for", TrayState.Tooltip(true, 55, 1, 1, 0, 0));
        Check(TrayState.Tooltip(false, 55, 1, 1, 0, 0) == "Abode Night View: [OFF] | 55%",
              "and in the off state", TrayState.Tooltip(false, 55, 1, 1, 0, 0));
        Check(!TrayState.Tooltip(true, 55, 1, 1, 0, 0).Contains("Neutral"),
              "the only mode there is is not named: the word never varies", null);
        Check(TrayState.Tooltip(true, 55, 0, 0, 0, 0).EndsWith("| no target"),
              "on with nothing running at all says no target",
              TrayState.Tooltip(true, 55, 0, 0, 0, 0));
        Check(!TrayState.Tooltip(false, 55, 0, 0, 0, 0).Contains("no target"),
              "off does not, because there is nothing to be missing", null);
        Check(TrayState.Tooltip(true, 100, 0, 0, 0, 0).Length <= TrayState.TooltipLimit,
              "the longest tooltip still fits the shell's 63-character limit",
              TrayState.Tooltip(true, 100, 0, 0, 0, 0).Length + " characters");

        // ------------------------------------------------------------------
        // The reported bug, as an assertion. Photoshop is running, selected,
        // and showing its welcome screen: the menu calls that
        // "Photoshop 2026 (no document open)" and the tooltip used to call the
        // same machine "no target". One product, one survey, two renderings --
        // so the two are checked against each other here rather than each
        // against a literal.
        // ------------------------------------------------------------------
        string menuRow = TrayState.Labelled("Photoshop 2026", "no document open");
        string hover   = TrayState.Tooltip(true, 55, 0, 1, 1, 0);

        Check(!hover.Contains("no target"),
              "a running selected product is never called 'no target'", hover);
        Check(hover.EndsWith("| no document open"),
              "the hover gives the menu's reason, not a count of overlays", hover);
        Check(menuRow.Contains(TrayState.IdleReason(1, 1, 0)),
              "hover and menu row use the same words for the same situation",
              menuRow + "  /  " + hover);

        Check(TrayState.IdleReason(0, 0, 0) == "no target",
              "nothing running is the only thing called no target", null);
        Check(TrayState.IdleReason(1, 0, 1) == "unsupported version",
              "a running product this build cannot read says which", null);
        Check(TrayState.IdleReason(1, 0, 0) == "nothing to dim",
              "running, readable and off-screen is neither of the above", null);
        Check(TrayState.IdleReason(2, 1, 1) == "no document open",
              "with several reasons at once the actionable one wins", null);

        foreach (int r in new[] { 0, 1, 4 })
            foreach (int nd in new[] { 0, 1 })
                foreach (int un in new[] { 0, 1 })
                    Check(TrayState.Tooltip(true, 90, 0, r, nd, un).Length <= TrayState.TooltipLimit,
                          "every reason still fits the 63-character budget",
                          TrayState.Tooltip(true, 90, 0, r, nd, un));

        // The notification balloon's first line. Asserted character for
        // character: it is the one line the user reads at launch, and k is the
        // number that means something physical.
        Check(TrayState.StatusLine(true, 55) == "[ON] 55% (k = 0.45)",
              "the balloon states the state, the strength and k, and nothing else",
              TrayState.StatusLine(true, 55));
        Check(TrayState.StatusLine(false, 55) == "[OFF] 55% (k = 0.45)",
              "and the same in the off state", TrayState.StatusLine(false, 55));
        Check(TrayState.StatusLine(true, 0) == "[ON] 0% (k = 1.00)" &&
              TrayState.StatusLine(true, 90) == "[ON] 90% (k = 0.10)",
              "k is 1 - strength/100 at both ends of the range", null);
        Check(!TrayState.StatusLine(true, 55).Contains("Neutral"),
              "and it does not name the mode either", null);
    }

    // ------------------------------------------------------- retired modes

    /// <summary>
    /// Greyscale and Shader were removed in 1.2.0. A settings file naming one --
    /// or naming a mode from some future release -- must start on Neutral rather
    /// than select a filter this build has no code to render.
    /// </summary>
    static void RetiredModes()
    {
        Console.WriteLine("  Modes that no longer exist");
        Console.WriteLine("  ------------------------------------------------------------------");

        Check(Modes.Normalize("greyscale") == Modes.Neutral,
              "mode=greyscale normalizes to neutral", null);
        Check(Modes.Normalize("shader") == Modes.Neutral,
              "mode=shader normalizes to neutral", null);
        Check(Modes.Normalize("warm") == Modes.Neutral,
              "mode=warm normalizes to neutral", null);
        Check(Modes.Normalize("hyperspectral") == Modes.Neutral,
              "an unknown future mode normalizes to neutral", null);
        Check(Modes.Normalize(null) == Modes.Neutral && Modes.Normalize("") == Modes.Neutral,
              "missing and empty normalize to neutral", null);
        Check(Modes.Normalize("  NEUTRAL  ") == Modes.Neutral,
              "and a real mode survives whitespace and case", null);
        Check(Modes.WasRetired("greyscale") && Modes.WasRetired("shader"),
              "the removed modes are still recognised as former modes", null);
        Check(!Modes.WasRetired("hyperspectral"),
              "and something that never existed is not", null);
        Check(Modes.Supported.Length == 1 && Modes.Supported[0] == Modes.Neutral,
              "exactly one mode ships, which is why there is no Mode submenu", null);
        Check(Modes.Pretty("greyscale") == "Neutral",
              "the tooltip names the mode that is actually rendering", null);

        // End to end, through the real settings loader.
        string realPath = Config.Path;
        string dir = Path.Combine(Path.GetTempPath(),
            "abodenv-mode-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string ini = Path.Combine(dir, "AbodeNightView.ini");
        try
        {
            File.WriteAllText(ini, "schema=2\nmode=greyscale\nstrength=55\n");
            Config.UseFile(ini);
            Config.Load();
            string loaded = Modes.Normalize(Config.Str("mode", Modes.Neutral));
            Check(loaded == Modes.Neutral,
                  "a 1.1 settings file naming a removed mode loads as neutral", loaded);
            Config.Set("mode", loaded);
            Config.Save();
            Check(File.ReadAllText(ini).Contains("mode=neutral"),
                  "and is written back normalized, so it is corrected once and stays that way", null);
        }
        finally
        {
            Config.UseFile(realPath);
            Config.Load();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // -------------------------------------------------------------- spacing

    /// <summary>
    /// Every line the user reads, checked for a run of spaces in the middle of
    /// it.
    ///
    /// This was a real defect and an invisible one: "Photoshop 2026  – no
    /// document open" and "Off  – switch it on and off yourself" came from six
    /// separate places that each padded a separator by hand, and no test could
    /// see them because the text was built inside the menu code. The label text
    /// now comes from TrayState, which is a pure function, which is a thing a
    /// harness can read.
    ///
    /// A leading run of spaces is indentation and is left alone; this is about
    /// gaps INSIDE a sentence.
    /// </summary>
    static void Spacing()
    {
        Console.WriteLine("  Text spacing");
        Console.WriteLine("  ------------------------------------------------------------------");

        var lines = new System.Collections.Generic.List<string>();

        // The tray, in both of its states, at both ends of the strength range.
        foreach (bool on in new[] { true, false })
        {
            lines.Add(TrayState.EnabledText(on));
            lines.Add(TrayState.ScheduleItem(on, "20:00 – 07:00"));
            foreach (int v in new[] { 0, 55, 90 })
            {
                lines.Add(TrayState.StrengthItem(v));
                lines.Add(TrayState.StatusLine(on, v));

                // Every hover the tray can produce: attached, and each of the
                // four reasons it can give for dimming nothing.
                lines.Add(TrayState.Tooltip(on, v, 2, 1, 0, 0));
                lines.Add(TrayState.Tooltip(on, v, 0, 0, 0, 0));
                lines.Add(TrayState.Tooltip(on, v, 0, 1, 1, 0));
                lines.Add(TrayState.Tooltip(on, v, 0, 1, 0, 1));
                lines.Add(TrayState.Tooltip(on, v, 0, 1, 0, 0));
            }
        }
        for (int sel = 0; sel <= 5; sel++)
            lines.Add(TrayState.TargetsItem(sel, sel == 0 ? 0 : 1));

        // Product rows, built the way the menu and the notification build them,
        // from a name with the ragged whitespace Adobe's own version resource
        // actually contains.
        string name = AdobeTarget.Shorten(TargetRegistry.Squeeze("Adobe Photoshop 2026 "));
        lines.Add(TrayState.Labelled(name, "no document open"));
        lines.Add(TrayState.Labelled(name, "unsupported version"));
        lines.Add(TrayState.Labelled(name, "unsupported version, cannot attach"));
        lines.Add(TrayState.Labelled(name, (string)null));
        lines.Add(TrayState.Labelled(name, "2 windows", "no document open"));
        lines.Add(TrayState.Labelled(name, null, "no document open"));
        lines.Add(TrayState.Labelled(name, "2 windows", null));
        foreach (var t in TargetRegistry.All)
        {
            lines.Add(TrayState.Labelled(t.ShortName, TargetRegistry.Explain(TargetStatus.NoDocument)));
            lines.Add(TrayState.Labelled(t.ShortName, TargetRegistry.Explain(TargetStatus.Unsupported)));
            lines.Add(TrayState.Labelled(t.ShortName, Region.Pretty(t.DefaultRegion)));
        }

        // The schedule, at every hour of a day, said and unsaid.
        var sch = new NightSchedule { Active = true, From = ClockTime.Of(20, 0), To = ClockTime.Of(7, 0) };
        var idle = new NightSchedule { Active = false, From = ClockTime.Of(20, 0), To = ClockTime.Of(7, 0) };
        lines.Add(sch.Range);
        lines.Add(sch.Plan);
        lines.Add(idle.Plan);
        for (int h = 0; h < 24; h++)
        {
            DateTime at = DateTime.Today.AddHours(h);
            lines.Add(sch.Status(at));
            lines.Add(idle.Status(at));
            lines.Add(sch.Plan + " " + sch.Status(at));
            lines.Add(idle.Plan + " " + idle.Status(at));
        }

        // The About box, paragraph by paragraph.
        foreach (string line in AboutInfo.DialogText("1.3.0").Replace("\r\n", "\n").Split('\n'))
            lines.Add(line);

        string worst = null;
        int checkedLines = 0;
        foreach (string line in lines)
        {
            if (line == null) continue;
            checkedLines++;
            string body = line.TrimStart();
            if (body.IndexOf("  ", StringComparison.Ordinal) < 0) continue;
            if (worst == null) worst = "\"" + line + "\"";
        }
        Check(worst == null, "no run of spaces inside any line the user reads",
              worst ?? (checkedLines + " lines checked"));

        // And the shape the user asked for, stated as itself rather than only
        // as the absence of a fault.
        Check(TrayState.Labelled("Photoshop 2026", "no document open") ==
              "Photoshop 2026 (no document open)",
              "a product row reads \"Photoshop 2026 (no document open)\"",
              TrayState.Labelled("Photoshop 2026", "no document open"));
        Check(TrayState.Labelled("Photoshop 2026", "2 windows", "no document open") ==
              "Photoshop 2026 (2 windows, no document open)",
              "and two things to say share the one parenthesis",
              TrayState.Labelled("Photoshop 2026", "2 windows", "no document open"));
        Check(TrayState.Labelled("Photoshop 2026", (string)null) == "Photoshop 2026" &&
              TrayState.Labelled("Photoshop 2026", "") == "Photoshop 2026",
              "and nothing to say leaves an empty parenthesis off entirely", null);
        Check(TrayState.ScheduleItem(true, "20:00 – 07:00") == "Schedule (20:00 – 07:00)" &&
              TrayState.ScheduleItem(false, "20:00 – 07:00") == "Schedule (off)",
              "the Schedule item carries the state, so the submenu does not have to",
              TrayState.ScheduleItem(false, "20:00 – 07:00"));
        Check(TrayState.StrengthItem(55) == "Strength (55%)" &&
              TrayState.StatusLine(true, 55) == "[ON] 55% (k = 0.45)",
              "and there is no space between the number and the per-cent sign",
              TrayState.StrengthItem(55) + "  /  " + TrayState.StatusLine(true, 55));

        // Where the double space actually came from: Photoshop's ProductName
        // resource ends in a space, so every label built as name + " " + note
        // had a gap in it. Squeezed once, on the way in.
        Check(TargetRegistry.Squeeze("Adobe Photoshop 2026 ") == "Adobe Photoshop 2026" &&
              TargetRegistry.Squeeze("  Adobe   Photoshop\t2026  ") == "Adobe Photoshop 2026",
              "a version resource with ragged whitespace is squeezed on the way in",
              "\"" + TargetRegistry.Squeeze("Adobe Photoshop 2026 ") + "\"");
        Check(TargetRegistry.Squeeze("") == "" && TargetRegistry.Squeeze(null) == null &&
              TargetRegistry.Squeeze("   ") == "",
              "and an empty or unreadable one survives it without throwing", null);
        Check(AdobeTarget.Shorten(TargetRegistry.Squeeze("Adobe Photoshop 2026 ")) == "Photoshop 2026",
              "so the name the menu shows is exactly the name, with nothing after it",
              "\"" + AdobeTarget.Shorten(TargetRegistry.Squeeze("Adobe Photoshop 2026 ")) + "\"");
    }

    // ---------------------------------------------------------------- about

    static void About()
    {
        Console.WriteLine("  About box");
        Console.WriteLine("  ------------------------------------------------------------------");

        Check(AboutInfo.ProductName == "Abode Night View",
              "product name", AboutInfo.ProductName);
        Check(AboutInfo.Attribution == "Vibecoded by Vixen420 in August 2026.",
              "attribution is the exact wording asked for", AboutInfo.Attribution);

        // The version is read from a binary's own version resource. Assert that
        // against the shipped executable rather than against this harness, which
        // is built without the version metadata on purpose.
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AbodeNightView.exe");
        if (File.Exists(exe))
        {
            string v = AboutInfo.VersionFrom(exe);
            Check(v.Length > 0 && v[0] >= '0' && v[0] <= '9' && v.Contains("."),
                  "the version is read out of the executable, not hard-coded", exe + " -> " + v);
        }
        else
        {
            Console.WriteLine("  [SKIP] no AbodeNightView.exe beside the harness to read a version from.");
        }
        Check(AboutInfo.VersionFrom("no-such-file.exe").Length > 0,
              "an unreadable file yields a fallback rather than an exception",
              AboutInfo.VersionFrom("no-such-file.exe"));

        string body = AboutInfo.DialogText("9.9.9");
        Check(body.Contains("Abode Night View") && body.Contains("Version 9.9.9") &&
              body.Contains(AboutInfo.Attribution),
              "the dialog says the product, the version and who wrote it", null);
        Check(body.Contains(AboutInfo.Copyright) && AboutInfo.Copyright.Contains(AboutInfo.Author),
              "and states a copyright naming the same author", AboutInfo.Copyright);

        // ------------------------------------------------------------ links

        // Every URL the dialog offers, checked as a URL rather than as a
        // sentence: a link that is one typo away from correct looks right in a
        // screenshot and goes nowhere when a stranger clicks it.
        foreach (string url in new[] { AboutInfo.AuthorUrl, AboutInfo.RepositoryUrl,
                                       AboutInfo.LicenseUrl, AboutInfo.LicenseTextUrl })
        {
            Uri parsed;
            bool ok = Uri.TryCreate(url, UriKind.Absolute, out parsed) &&
                      parsed.Scheme == Uri.UriSchemeHttps &&
                      url.IndexOf(' ') < 0;
            Check(ok, "a real https URL: " + url, ok ? null : "not a usable absolute https URL");
        }

        Check(AboutInfo.HasRepository &&
              AboutInfo.RepositoryUrl == "https://github.com/VulpesNexus/abode-night-view",
              "the repository link points at the published repository",
              AboutInfo.RepositoryUrl);
        Check(AboutInfo.AuthorUrl == "https://github.com/VulpesNexus",
              "and the author's name links to the author's profile", AboutInfo.AuthorUrl);

        // The dialog turns a name into a link by FINDING it in the sentence, so
        // the name has to be in the sentence. If a rewording ever drops it, the
        // link silently disappears rather than landing on the wrong words --
        // this is what notices that it has gone.
        Check(AboutInfo.Attribution.IndexOf(AboutInfo.Author, StringComparison.Ordinal) >= 0,
              "the author's name appears in the attribution, so it can be linked there",
              AboutInfo.Attribution);

        // ---------------------------------------------------------- licence

        Check(AboutInfo.LicenseId == "GPL-3.0-or-later",
              "the licence is stated as an SPDX identifier", AboutInfo.LicenseId);
        Check(AboutInfo.License.Length == 3,
              "the notice is the GPL's own three paragraphs", AboutInfo.License.Length + " paragraph(s)");
        Check(AboutInfo.License[0].Contains("free software") &&
              AboutInfo.License[0].Contains("GNU General Public License") &&
              AboutInfo.License[0].Contains("version 3 of the License") &&
              AboutInfo.License[0].Contains("any later version"),
              "the grant names the licence, version 3, and the or-later option", null);
        Check(AboutInfo.License[1].Contains("WITHOUT ANY WARRANTY") &&
              AboutInfo.License[1].Contains("MERCHANTABILITY") &&
              AboutInfo.License[1].Contains("FITNESS FOR A PARTICULAR PURPOSE"),
              "the disclaimer is intact and still in capitals, as the licence prints it", null);
        Check(AboutInfo.License[2].Contains("You should have received a copy") &&
              AboutInfo.License[2].Contains(AboutInfo.LicenseUrl),
              "and the last paragraph carries the address the dialog makes clickable", null);

        foreach (string para in AboutInfo.License)
            Check(body.Contains(para), "it is all in what the dialog says",
                  para.Substring(0, Math.Min(40, para.Length)) + "...");

        // The whole point of a notice is that it is the licence's wording and
        // not a summary of it. A stray double space is how a paste turns into a
        // retype, and it is what the rest of this release went looking for.
        string joined = string.Join(" ", AboutInfo.License);
        Check(joined.IndexOf("  ", StringComparison.Ordinal) < 0,
              "with no stray double space anywhere in it", null);

        // The COPYING file the third paragraph promises the reader.
        string copying = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LICENSE");
        if (File.Exists(copying))
        {
            string text = File.ReadAllText(copying);
            Check(text.Contains("GNU GENERAL PUBLIC LICENSE") && text.Contains("Version 3"),
                  "and the full licence text ships beside the binary",
                  text.Length.ToString(CultureInfo.InvariantCulture) + " bytes");
        }
        else
        {
            Console.WriteLine("  [SKIP] no LICENSE beside the harness (it lives in the repository root).");
        }
    }
}
