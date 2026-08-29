// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View – things the tray shows, expressed as pure functions
// ----------------------------------------------------------------------------
//  Everything a user reads in the tray -- the hover text, the Enabled/Disabled
//  item, the About box -- is derived here from plain arguments and returned as
//  plain strings. Nothing in this file touches a control, a setting or a window.
//
//  That is deliberate, and it is the fix for a whole class of bug this project
//  has now hit twice. A tray menu is the one part of the program with no
//  mechanical test: it is drawn by Windows, into a menu that only exists while a
//  human is holding the mouse still. Both times the state was correct and what
//  the user saw was not, and both times it was invisible to every test we had.
//
//  So the state and the text are computed by functions the harness can call
//  directly, and the menu code is reduced to putting the answers on screen.
// ============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
//  Which filters this build can actually render
// ---------------------------------------------------------------------------
/// <summary>
/// This build renders exactly one filter, so Normalize is a total function onto
/// it. It still earns its place: a settings file written by 1.1 can say
/// mode=greyscale or mode=shader, a hand-edited one can say anything, and a file
/// written by some later version can name a mode that does not exist here. All
/// of them must start cleanly on Neutral rather than select a mode the renderer
/// does not implement -- which, for a program whose whole job is to draw a
/// rectangle over somebody's work, is the difference between a dim page and a
/// black one.
///
/// Greyscale and Shader were investigated, measured and removed in 1.2.0;
/// see docs/design-notes.md "Rendering: why there is one filter" for the
/// measurements.
/// </summary>
internal static class Modes
{
    public const string Neutral = "neutral";

    /// <summary>Every mode this build can render. Deliberately a list: adding a
    /// second one should not mean rediscovering where the strings live.</summary>
    public static readonly string[] Supported = { Neutral };

    /// <summary>Modes earlier releases knew about and this one does not. Only
    /// used to answer "was this a real mode once?", which is the difference
    /// between a settings file to migrate quietly and one that is corrupt.</summary>
    public static readonly string[] Retired = { "greyscale", "shader" };

    public static string Normalize(string m)
    {
        if (m != null)
        {
            string t = m.Trim();
            foreach (string s in Supported)
                if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return Neutral;
    }

    public static bool WasRetired(string m)
    {
        if (m == null) return false;
        string t = m.Trim();
        foreach (string s in Retired)
            if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The display name of a mode. Nothing in the user interface calls this any
    /// more -- with exactly one mode left, naming it in the tooltip and in the
    /// startup balloon was a word that never changed and therefore carried no
    /// information. It is kept because it is what the diagnostics report and
    /// the migration tests print, where "which filter did this build actually
    /// render?" is a real question.
    /// </summary>
    public static string Pretty(string m)
    {
        string n = Normalize(m);
        return char.ToUpperInvariant(n[0]) + n.Substring(1);
    }
}

// ---------------------------------------------------------------------------
//  What the tray says
// ---------------------------------------------------------------------------
internal static class TrayState
{
    /// <summary>
    /// The global item names the state it is in, rather than naming the action it
    /// performs. "Enabled" with nothing beside it is ambiguous -- it reads equally
    /// as a label and as a button -- and that ambiguity was the reported bug.
    /// The caller pairs this with Checked, so the state is given twice: once in
    /// the word and once in the tick.
    /// </summary>
    public static string EnabledText(bool enabled) { return enabled ? "Enabled" : "Disabled"; }

    /// <summary>Shell tray tooltips are capped at 64 characters including the
    /// terminator; anything longer is silently dropped by some shells and
    /// truncated by others.</summary>
    public const int TooltipLimit = 63;

    /// <summary>
    /// Why nothing is being dimmed, in the words the Targets menu uses for the
    /// same situation -- or null when the answer is not worth the reader's
    /// attention. The counts come from one survey of the desktop, so the hover
    /// text and the menu cannot contradict each other.
    ///
    /// The reported bug was the tooltip saying "no target" while the menu said
    /// "Photoshop 2026 (no document open)" -- both true of their own inputs and
    /// contradictory on screen, because the tooltip was counting attached
    /// overlays and calling zero of them "no target". An application that is
    /// running and selected IS a target; whether it currently offers anything
    /// to dim is a different question, and this is where it gets answered.
    ///
    /// "No document open" is not one of the answers any more. It is the single
    /// most ordinary thing an Adobe application can be doing -- sitting on its
    /// own home screen, between jobs -- and a hover that reports it is spending
    /// the line on the one state the user is least likely to be asking about.
    /// The Targets menu still says it, per product, where it distinguishes one
    /// product from another; here it distinguished nothing.
    ///
    /// Returning null rather than a fourth phrase is deliberate: silence is
    /// what "nothing is wrong" should look like, and a phrase like "idle" would
    /// only be the same noise under a shorter name. The two answers left are
    /// the two a user would actually want interrupting them.
    /// </summary>
    public static string IdleReason(int running, int noDocument, int unsupported)
    {
        if (running <= 0) return "no target";

        // Ahead of "no document open", which used to win: a version this build
        // cannot read is the anomaly, and it is the one that survives.
        if (unsupported > 0) return "unsupported version";

        if (noDocument > 0) return null;

        // Selected, running, readable, with something open, and still nothing
        // on screen: minimized, or its window is off the visible desktop. Rare,
        // so worth a word.
        return "nothing to dim";
    }

    /// <summary>
    /// Switched on: "Abode Night View: [ON] | 55%", with the reason nothing is
    /// dimmed appended when there is one. Derived from the same survey the menu
    /// is built from, so the two cannot disagree -- which the three-argument
    /// version of this function claimed in this comment and did not do.
    ///
    /// Switched off: "Abode Night View: [OFF]", and nothing else. The strength
    /// was still being shown, which is a number describing an effect that is
    /// not being applied -- the hover said 55% over a screen that was not dimmed
    /// at all. A stored setting is not a state, and the hover reports the state.
    /// The value is still one click away, on the Strength item, where it is
    /// being read as a setting rather than as a description of the screen.
    ///
    /// The filter name used to sit between the state and the number. It was
    /// removed because it is the same word in every possible state -- Neutral is
    /// the only mode this build renders -- so it spent a third of a 63-character
    /// budget saying nothing. Dropping it is also what let the product be named
    /// in full here instead of as "Abode NV", and it is what leaves room for the
    /// reason above.
    /// </summary>
    public static string Tooltip(bool enabled, int strength, int liveOverlays,
                                 int running, int noDocument, int unsupported)
    {
        if (!enabled) return "Abode Night View: [OFF]";

        string s = string.Format(CultureInfo.InvariantCulture,
            "Abode Night View: [ON] | {0}%", strength);
        if (liveOverlays == 0)
        {
            string why = IdleReason(running, noDocument, unsupported);
            if (!string.IsNullOrEmpty(why)) s += " | " + why;
        }
        return s.Length > TooltipLimit ? s.Substring(0, TooltipLimit) : s;
    }

    /// <summary>
    /// A thing, and whatever there is to say about it, in one parenthesis:
    /// "Photoshop 2026 (no document open)", "Schedule (20:00 – 07:00)".
    ///
    /// One function because it was six ad-hoc concatenations, and every one of
    /// them padded the separator by hand -- which is where the run of spaces
    /// the user reported in the tray menu and in the notification came from.
    /// Nothing here pads anything: the note is either there, after exactly one
    /// space, or the name stands alone.
    /// </summary>
    public static string Labeled(string name, string note)
    {
        if (name == null) name = "";
        return string.IsNullOrEmpty(note) ? name : name + " (" + note + ")";
    }

    /// <summary>Same idea, for several things to say at once. They share the
    /// one parenthesis: "Photoshop 2026 (2 windows, no document open)". Two
    /// parentheses in a row read as two labels on one row.</summary>
    public static string Labeled(string name, params string[] notes)
    {
        var kept = new System.Collections.Generic.List<string>();
        if (notes != null)
            foreach (string n in notes)
                if (!string.IsNullOrEmpty(n)) kept.Add(n);
        return Labeled(name, string.Join(", ", kept.ToArray()));
    }

    /// <summary>The Schedule item: the range if it is running, "off" if it is
    /// not. The submenu under it carries the two choices and the range editor
    /// and nothing else, so this line is where the state is read.</summary>
    public static string ScheduleItem(bool active, string range)
    {
        return Labeled("Schedule", active ? range : "off");
    }

    public static string StrengthItem(int strength)
    {
        return Labeled("Strength", strength.ToString(CultureInfo.InvariantCulture) + "%");
    }

    /// <summary>
    /// The Strength window's headline, above the slider: "20% (k = 0.80)".
    ///
    /// The setting and the coefficient it means, together, because they are one
    /// fact stated in two units -- the number the user chose, and the number the
    /// compositor multiplies by. Neither is useful without the other: 20% does
    /// not say what happens to a pixel, and k = 0.80 is not what the slider is
    /// labeled in.
    ///
    /// The word "dim" is gone from it. The window is titled Strength, the
    /// slider is the only control in it, and the sentence below says what the
    /// dimming does; "20% dim" was the third place on one small dialog to say
    /// which direction the number runs.
    /// </summary>
    public static string StrengthHeadline(int strength)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0}% (k = {1:0.00})",
                             strength, 1.0 - strength / 100.0);
    }

    /// <summary>
    /// The line under the slider: "255 (pure white) now displays as 204."
    ///
    /// The consequence, in the only units anybody can check by looking at the
    /// screen. It names its input as well as its output, and it is a sentence
    /// with a full stop, because it is one -- the headline above is a reading,
    /// this is what the reading means. The window draws it in gray for the same
    /// reason: it is the explanation, not the setting.
    /// </summary>
    public static string StrengthNote(int strength)
    {
        return string.Format(CultureInfo.InvariantCulture,
                             "255 (pure white) now displays as {0}.",
                             (int)Math.Round(255 * (1.0 - strength / 100.0)));
    }

    public static string TargetsItem(int selected, int running)
    {
        return Labeled("Targets", string.Format(CultureInfo.InvariantCulture,
                                                 "{0} selected, {1} running", selected, running));
    }

    /// <summary>
    /// The state as one line, for the notification balloon: "[ON] 55% (k = 0.45)",
    /// and "[OFF]" on its own.
    ///
    /// k is the multiplier the compositor actually applies, and it is the only
    /// number in the product that means anything physical -- 55 % dim is
    /// k = 0.45, and white 255 lands on 115. It is stated next to the
    /// percentage so the two are never separated. Which is exactly why both
    /// leave together: switched off there is no multiply, so "k = 0.45" was
    /// naming a coefficient nothing was multiplying by, on the one notification
    /// whose whole job is to say that the dimming has stopped. The line under it
    /// already tells the user how to switch it back on, and the strength they
    /// will get when they do is on the Strength item.
    /// </summary>
    public static string StatusLine(bool enabled, int strength)
    {
        if (!enabled) return "[OFF]";
        return string.Format(CultureInfo.InvariantCulture, "[ON] {0}% (k = {1:0.00})",
            strength, 1.0 - strength / 100.0);
    }
}

// ---------------------------------------------------------------------------
//  About
// ---------------------------------------------------------------------------
internal static class AboutInfo
{
    public const string ProductName = "Abode Night View";

    /// <summary>The author, as a name that appears inside a sentence. Kept
    /// separate from Attribution so the About box can find it in that sentence
    /// and turn it into a link, rather than being handed a character offset
    /// that a later rewording would silently move.</summary>
    public const string Author = "Vixen420";
    public const string AuthorUrl = "https://github.com/VulpesNexus";

    public const string Attribution = "Vibecoded by Vixen420 in August 2026.";
    public const string Copyright = "Copyright © 2026 Vixen420.";

    /// <summary>
    /// The About box asks whether there is a link before offering one, so an
    /// empty value here means no link rather than a dead one. That is how this
    /// shipped before the repository existed.
    /// </summary>
    public const string RepositoryUrl = "https://github.com/VulpesNexus/abode-night-view";

    public static bool HasRepository
    {
        get { return RepositoryUrl != null && RepositoryUrl.Trim().Length > 0; }
    }

    // ------------------------------------------------------------- license

    /// <summary>The SPDX identifier, which is what a machine reads: the LICENSE
    /// file, the .csproj if there ever is one, and GitHub's own detection.</summary>
    public const string LicenseId = "GPL-3.0-or-later";
    public const string LicenseName = "GNU General Public License, version 3";

    /// <summary>Where the license itself lives. The bare /licenses/ form is the
    /// one the GPL's own boilerplate tells you to print, so it is the one in the
    /// last paragraph; the full text is one click further in.</summary>
    public const string LicenseUrl = "https://www.gnu.org/licenses/";
    public const string LicenseTextUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    /// <summary>
    /// The notice the GPL asks every program to be able to show, in the wording
    /// the license itself supplies ("How to Apply These Terms to Your New
    /// Programs"). It is here rather than in the dialog because it is text, not
    /// layout: the harness asserts the three paragraphs are present and intact
    /// without opening a window, and the dialog's job is only to wrap them.
    /// </summary>
    public static readonly string[] License = new string[]
    {
        ProductName + " is free software: you can redistribute it and/or modify " +
        "it under the terms of the GNU General Public License as published by " +
        "the Free Software Foundation, either version 3 of the License, or (at " +
        "your option) any later version.",

        "This program is distributed in the hope that it will be useful, but " +
        "WITHOUT ANY WARRANTY; without even the implied warranty of " +
        "MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU " +
        "General Public License for more details.",

        "You should have received a copy of the GNU General Public License " +
        "along with this program. If not, see " + LicenseUrl,
    };

    /// <summary>
    /// The version comes from the binary's own Win32 version resource rather than
    /// from a constant in the source, so --version, the About box, the tray header
    /// and Explorer's Details tab cannot drift apart: there is one place to change
    /// it, AssemblyInfo.cs, and everything else reads the result.
    /// </summary>
    public static string VersionFrom(string exePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exePath);
                if (fvi != null && !string.IsNullOrEmpty(fvi.ProductVersion))
                {
                    string v = fvi.ProductVersion.Trim();
                    if (v.Length > 0) return v;
                }
            }
        }
        catch (Exception) { }        // unreadable resource, deleted file, odd host

        // Nothing here is worth failing to start over. Fall back to the managed
        // metadata, then to a string that is obviously a fallback rather than a
        // plausible wrong version number.
        try
        {
            Assembly a = Assembly.GetExecutingAssembly();
            object[] at = a.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (at.Length > 0)
            {
                string v = ((AssemblyInformationalVersionAttribute)at[0]).InformationalVersion;
                if (!string.IsNullOrEmpty(v)) return v.Trim();
            }
            return a.GetName().Version.ToString();
        }
        catch (Exception) { }
        return "(version unavailable)";
    }

    public static string Version
    {
        get
        {
            string loc = "";
            try { loc = Assembly.GetExecutingAssembly().Location; } catch (Exception) { }
            return VersionFrom(loc);
        }
    }

    /// <summary>
    /// The body of the About box, as text. Pure, so the harness can assert what it
    /// says without opening a window and without anyone hand-checking a screenshot.
    /// </summary>
    public static string DialogText(string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ProductName);
        sb.AppendLine("Version " + version);
        sb.AppendLine();
        sb.AppendLine(Attribution);
        sb.AppendLine(Copyright);
        if (HasRepository) sb.AppendLine(RepositoryUrl);
        sb.AppendLine();
        foreach (string para in License)
        {
            sb.AppendLine(para);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
//  Tray menu style
// ---------------------------------------------------------------------------
internal static class TrayMenuStyle
{
    /// <summary>
    /// A ToolStripDropDownMenu draws a check mark only if it has a margin to draw
    /// it in. This menu had ShowImageMargin = false and ShowCheckMargin left at
    /// its default of false, which means Checked was being set correctly on every
    /// item and rendering as nothing at all -- the reported "Enabled has no tick",
    /// and the same for "Include in screen captures".
    ///
    /// The check margin rather than the image margin because there are no icons
    /// here: it is the narrower of the two and it is the one that exists for this.
    /// </summary>
    public static void Apply(ToolStripDropDownMenu m)
    {
        m.ShowCheckMargin = true;
        m.ShowImageMargin = false;
    }

    /// <summary>Whether a menu styled like this can render a tick at all. The
    /// harness asserts it, because the failure mode is silent by construction:
    /// the state is right, the property is right, and the pixels are empty.</summary>
    public static bool CanDrawChecks(ToolStripDropDownMenu m)
    {
        return m.ShowCheckMargin || m.ShowImageMargin;
    }
}
