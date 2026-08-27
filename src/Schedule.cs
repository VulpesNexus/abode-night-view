// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – the nightly schedule
// ----------------------------------------------------------------------------
//  Why a schedule at all
//      The utility exists because a page of white paper at 2am is a lamp. "2am"
//      is a fact the machine already knows, so reaching for the tray icon every
//      evening and every morning is work the program should be doing itself.
//
//  Why it is off until you ask for it
//      Putting a filter over somebody's artwork without being asked is the one
//      thing a display-only tool must not do. The schedule is therefore opt-in,
//      one click, and it drives nothing except the same on/off state the tray
//      item and the shortcut already drive.
//
//  Why the range wraps
//      A night range is 20:00 to 07:00: it crosses midnight, which is the
//      normal case rather than the awkward one. Covers() is written for the
//      wrapping case first and the same-day case falls out of it.
//
//  Manual override, and why this is edge triggered
//      The schedule acts when the answer to "should this be on now?" CHANGES,
//      not continuously. Switching it off by hand at 01:00 therefore stays off
//      until the range next begins or ends, instead of being undone a quarter
//      of a second later by the resync timer. A level-triggered schedule would
//      make the tray item and the shortcut appear broken for half the night,
//      which is worse than having no schedule at all.
//
//  Everything here is pure and settings-backed, with no window and no timer of
//  its own, for the same reason UiState.cs is: it can then be asserted by
//  Audit.exe --selftest instead of by watching a clock.
// ============================================================================

using System;
using System.Globalization;

// ---------------------------------------------------------------------------
//  A wall-clock time of day, to the minute
// ---------------------------------------------------------------------------
/// <summary>
/// Minutes since midnight, normalized into 0..1439. Deliberately not a
/// TimeSpan: a TimeSpan can be negative, can exceed a day, and prints as
/// "20:00:00", none of which is wanted for a field a user types "20:00" into.
/// </summary>
internal struct ClockTime
{
    public const int MinutesPerDay = 24 * 60;

    private readonly int _m;

    private ClockTime(int minutes)
    { _m = ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay; }

    public static ClockTime FromMinutes(int minutes) { return new ClockTime(minutes); }
    public static ClockTime Of(int hour, int minute) { return new ClockTime(hour * 60 + minute); }
    public static ClockTime Of(DateTime t) { return new ClockTime(t.Hour * 60 + t.Minute); }

    public int Minutes { get { return _m; } }
    public int Hour { get { return _m / 60; } }
    public int Minute { get { return _m % 60; } }

    /// <summary>
    /// Accepts "20:00", "8:05", "8.5", "8h30", "0800" and "8". Returns false
    /// with no value rather than throwing, because the input can come from a
    /// hand-edited settings file and a bad line must cost the user the
    /// schedule, not the launch.
    /// </summary>
    public static bool TryParse(string s, out ClockTime t)
    {
        t = new ClockTime(0);
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();

        int h, m = 0;
        int sep = s.IndexOfAny(new[] { ':', '.', 'h', 'H' });
        if (sep > 0)
        {
            if (!int.TryParse(s.Substring(0, sep), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out h)) return false;
            string rest = s.Substring(sep + 1).Trim();
            if (rest.Length > 0 &&
                !int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
                return false;
        }
        else
        {
            int n;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return false;
            // "0800" and "2015" are how a four-digit field gets typed; "8" and
            // "20" are an hour on its own.
            if (s.Length == 4) { h = n / 100; m = n % 100; } else h = n;
        }

        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        t = Of(h, m);
        return true;
    }

    /// <summary>24-hour, zero-padded, invariant: the same text in the settings
    /// file on every machine, and the same text the parser accepts back.</summary>
    public override string ToString()
    {
        return Hour.ToString("00", CultureInfo.InvariantCulture) + ":" +
               Minute.ToString("00", CultureInfo.InvariantCulture);
    }
}

// ---------------------------------------------------------------------------
//  The schedule itself
// ---------------------------------------------------------------------------
internal sealed class NightSchedule
{
    public const string KeyActive = "schedule";
    public const string KeyFrom = "schedule.from";
    public const string KeyTo = "schedule.to";

    /// <summary>Evening to a working morning. Only a starting point: the whole
    /// point of the feature is that the range is the user's to set.</summary>
    public static readonly ClockTime DefaultFrom = ClockTime.Of(20, 0);
    public static readonly ClockTime DefaultTo = ClockTime.Of(7, 0);

    public bool Active;
    public ClockTime From = DefaultFrom;
    public ClockTime To = DefaultTo;

    public NightSchedule Copy()
    { return new NightSchedule { Active = Active, From = From, To = To }; }

    /// <summary>
    /// Is <paramref name="now"/> inside the range? The start minute is included
    /// and the end minute is excluded, so 20:00 to 07:00 switches on as the
    /// clock reads 20:00 and off as it reads 07:00, with no minute belonging to
    /// both.
    ///
    /// From == To is a zero-length interval, which reads as either "never" or
    /// "always" depending on who is asked. It is taken as the whole day, and
    /// the editor refuses to create one, so the only way to reach it is by
    /// hand-editing the settings file.
    /// </summary>
    public bool Covers(DateTime now)
    {
        int n = now.Hour * 60 + now.Minute;
        int f = From.Minutes, t = To.Minutes;
        if (f == t) return true;
        return f < t ? (n >= f && n < t) : (n >= f || n < t);
    }

    /// <summary>The next boundary strictly after <paramref name="now"/>.</summary>
    public DateTime NextChange(DateTime now)
    {
        DateTime day = now.Date;
        DateTime a = day.AddMinutes(From.Minutes);
        DateTime b = day.AddMinutes(To.Minutes);
        if (a <= now) a = a.AddDays(1);
        if (b <= now) b = b.AddDays(1);
        return a < b ? a : b;
    }

    /// <summary>"20:00 – 07:00", for the menu item and the window title.</summary>
    public string Range { get { return From + " – " + To; } }

    /// <summary>
    /// What the range SAYS, with no claim about whether it is running.
    ///
    /// Kept apart from Status for exactly that reason: the editor shows this
    /// while a range is being typed, including a range typed for later use with
    /// the schedule still switched off, and a sentence in the present tense
    /// ("Dimming switches on at...") would be describing something that is not
    /// happening.
    /// </summary>
    public string Plan
    {
        get { return "Dimming is set to switch on at " + From + " and off at " + To + "."; }
    }

    /// <summary>One sentence saying what the schedule is about to do. Takes the
    /// time as an argument so a test can ask about 03:00 without waiting for
    /// it.</summary>
    public string Status(DateTime now)
    {
        if (!Active)
            return "Schedule is currently off.";
        if (From.Minutes == To.Minutes)
            return "The range covers the whole day, so it never switches off.";

        DateTime next = NextChange(now);
        return (Covers(now) ? "On now, until " : "Off now, until ") + ClockTime.Of(next) +
               (next.Date == now.Date ? "" : " tomorrow") + ".";
    }

    // ------------------------------------------------------------- settings

    public static NightSchedule Load()
    {
        var s = new NightSchedule();
        s.Active = Config.Bool(KeyActive, false);
        ClockTime t;
        s.From = ClockTime.TryParse(Config.Str(KeyFrom, ""), out t) ? t : DefaultFrom;
        s.To = ClockTime.TryParse(Config.Str(KeyTo, ""), out t) ? t : DefaultTo;
        return s;
    }

    public void Save()
    {
        Config.SetBool(KeyActive, Active);
        Config.Set(KeyFrom, From.ToString());
        Config.Set(KeyTo, To.ToString());
    }

    /// <summary>
    /// Parse a --schedule= value: "off", or "20:00-07:00" (an en dash is
    /// accepted too, because that is how the menu prints it). Returns null if
    /// it is neither, so a typo keeps the stored schedule rather than silently
    /// inventing a different one.
    /// </summary>
    public static NightSchedule Parse(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        spec = spec.Trim();
        if (spec.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            spec.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new NightSchedule { Active = false };

        int cut = spec.IndexOf('–');
        if (cut < 0) cut = spec.IndexOf('-', 1);       // not index 0: there are no negative times
        if (cut <= 0) return null;

        ClockTime f, t;
        if (!ClockTime.TryParse(spec.Substring(0, cut), out f)) return null;
        if (!ClockTime.TryParse(spec.Substring(cut + 1), out t)) return null;
        return new NightSchedule { Active = true, From = f, To = t };
    }
}
