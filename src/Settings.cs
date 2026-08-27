// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - settings file
// ----------------------------------------------------------------------------
//  Portable first: the .ini lives next to the executable so the whole thing is
//  one folder you can copy to a USB stick. If that folder is not writable --
//  Program Files, a network share, a read-only download -- it falls back to
//  %APPDATA%\AbodeNightView\ rather than silently losing every setting change.
//
//  The file is deliberately trivial (key=value, one per line) and every read is
//  defensive. A corrupted or hand-edited file must degrade to defaults, never
//  to a crash and never to a state the user cannot get out of: an out-of-range
//  strength, for instance, used to be written straight through to the alpha and
//  could produce a fully opaque black rectangle over the canvas.
//
//  Keys are namespaced so the file can grow without ambiguity:
//
//      schema=3                     bumped when a key changes MEANING
//      enabled / strength / mode    global
//      schedule=1|0                 switch on and off by the clock
//      schedule.from / schedule.to  HH:mm, and the range may cross midnight
//      target.<product>=1|0         which products get an overlay
//      region.<product>=canvas|...  what is dimmed inside each one
//      hotkey.<action>=              empty: nothing bound. No defaults ship.
//
//  Unknown keys are preserved verbatim. Load() reads every line into the map
//  and Save() writes the whole map back, so a settings file written by a newer
//  version survives a round trip through an older one instead of being silently
//  truncated to the keys that version happened to know about.
//
//  Migration
//      Night View 1.0 stored NightView.ini with an InDesign-only "target=" key
//      whose values were region names. That file is imported once, in place,
//      and the original is left alone so downgrading is still possible.
//
//      Schema 3 withdrew the default shortcuts. Those were Ctrl+Alt+<key>,
//      which IS AltGr on every layout that has one, so a user on a German or
//      French keyboard was silently losing four characters for as long as the
//      utility ran. A file written by schema 2 or earlier therefore has those
//      exact four lines removed -- and only those exact four. A combination the
//      user typed in themselves was a choice and is left alone; a combination
//      they never touched was never a choice at all.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

internal static class Config
{
    public const string FileName = "AbodeNightView.ini";
    public const string LegacyFileName = "NightView.ini";
    public const string FolderName = "AbodeNightView";
    public const string LegacyFolderName = "NightView";
    public const int Schema = 3;

    private static string _path;
    private static readonly Dictionary<string, string> _values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static bool LastSaveOk = true;
    public static string LastSaveError;

    /// <summary>True when settings are stored beside the exe rather than in AppData.</summary>
    public static bool Portable { get; private set; }

    /// <summary>Set when a Night View 1.0 file was imported, for --diagnostics.</summary>
    public static string MigratedFrom;

    /// <summary>How many withdrawn default shortcuts this load removed, so the
    /// user can be told once why a key they were used to has stopped working.
    /// Silence would be the one unacceptable outcome here.</summary>
    public static int DroppedHotkeys;

    public static string Path
    {
        get { if (_path == null) _path = ChoosePath(); return _path; }
    }

    /// <summary>Point the settings at a specific file. Used by the self-test so it
    /// can exercise the real loader against deliberately corrupted input without
    /// touching the settings the user is living with.</summary>
    public static void UseFile(string path) { _path = path; MigratedFrom = null; }

    // ------------------------------------------------------------ location

    private static string ExeDir()
    {
        try
        {
            string loc = typeof(Config).Assembly.Location;
            string dir = System.IO.Path.GetDirectoryName(loc);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        catch { }
        return AppDomain.CurrentDomain.BaseDirectory ?? ".";
    }

    private static bool Writable(string dir)
    {
        try
        {
            string probe = System.IO.Path.Combine(dir, ".abodenv-write-probe.tmp");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write,
                                           FileShare.None, 8, FileOptions.DeleteOnClose))
                fs.WriteByte(0);
            return true;
        }
        catch { return false; }
    }

    private static string ChoosePath()
    {
        string exeDir = ExeDir();
        string beside = System.IO.Path.Combine(exeDir, FileName);
        string besideOld = System.IO.Path.Combine(exeDir, LegacyFileName);

        // An existing file next to the exe wins, so an established portable
        // install keeps working even if the folder later becomes read-only --
        // we would still read it, and only saves would fail (reported, not hidden).
        if (File.Exists(beside) || File.Exists(besideOld) || Writable(exeDir))
        { Portable = true; return beside; }

        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
            Directory.CreateDirectory(dir);
            Portable = false;
            return System.IO.Path.Combine(dir, FileName);
        }
        catch
        {
            Portable = true;
            return beside;      // hopeless, but Save() reports the failure rather than throwing
        }
    }

    /// <summary>
    /// Where a Night View 1.0 file would be, given where we decided to store ours.
    /// Both the portable and the AppData location are checked, because a user could
    /// have been running 1.0 from Program Files (AppData) and 1.1 from a folder
    /// they can write (portable), or the other way round.
    /// </summary>
    private static IEnumerable<string> LegacyCandidates()
    {
        string dir = null;
        try { dir = System.IO.Path.GetDirectoryName(Path); } catch { }
        if (!string.IsNullOrEmpty(dir))
            yield return System.IO.Path.Combine(dir, LegacyFileName);
        string appdata = null;
        try
        {
            appdata = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LegacyFolderName, LegacyFileName);
        }
        catch { }
        if (appdata != null) yield return appdata;
    }

    // ---------------------------------------------------------------- read

    public static void Load()
    {
        _values.Clear();
        MigratedFrom = null;
        DroppedHotkeys = 0;

        string from = Path;
        if (!SafeExists(from))
        {
            foreach (string legacy in LegacyCandidates())
            {
                if (!SafeExists(legacy)) continue;
                from = legacy;
                MigratedFrom = legacy;
                break;
            }
        }

        ReadInto(from);
        // Before Migrate(), which stamps the current schema onto an imported
        // 1.0 file and would otherwise hide that file's own schema from this.
        Upgrade();
        if (MigratedFrom != null) Migrate();
    }

    /// <summary>
    /// Schema upgrades that are not a file rename. Applied to whatever was just
    /// read, keyed off the schema the file itself declares -- an absent schema
    /// key reads as 0, which is correct: it predates the numbering.
    /// </summary>
    private static void Upgrade()
    {
        int was = 0;
        string v;
        if (_values.TryGetValue("schema", out v))
            int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out was);

        if (was < 3)
        {
            // The 1.2 defaults, dropped only where they are still exactly what
            // 1.2 wrote. See the file header for why they were withdrawn.
            DropDefault("hotkey.toggle", "Ctrl+Alt+N");
            DropDefault("hotkey.brighter", "Ctrl+Alt+Up");
            DropDefault("hotkey.darker", "Ctrl+Alt+Down");
            DropDefault("hotkey.quit", "Ctrl+Alt+Q");
        }
    }

    private static void DropDefault(string key, string retiredDefault)
    {
        string v;
        if (!_values.TryGetValue(key, out v)) return;
        if (!string.Equals(v.Trim(), retiredDefault, StringComparison.OrdinalIgnoreCase)) return;
        _values[key] = "";
        DroppedHotkeys++;
    }

    private static bool SafeExists(string p)
    { try { return !string.IsNullOrEmpty(p) && File.Exists(p); } catch { return false; } }

    private static void ReadInto(string file)
    {
        try
        {
            if (!SafeExists(file)) return;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                _values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        catch (Exception e)
        {
            // Unreadable settings are not a reason to refuse to start.
            _values.Clear();
            LastSaveError = "load failed: " + e.Message;
        }
    }

    /// <summary>
    /// Bring a Night View 1.0 file forward. Only ONE key changed meaning: "target"
    /// used to name a region and applied to InDesign, the only product there was.
    /// Everything else -- strength, mode, warm, enabled, captures, zmode, hotkeys --
    /// keeps both its name and its meaning, so it is left exactly where it is.
    /// </summary>
    private static void Migrate()
    {
        string old;
        if (_values.TryGetValue("target", out old) && old.Length > 0)
        {
            string region = Region.Normalize(old);
            if (region != null) _values["region.indesign"] = region;
            _values.Remove("target");
        }
        _values["schema"] = Schema.ToString(CultureInfo.InvariantCulture);
    }

    public static IEnumerable<KeyValuePair<string, string>> All() { return _values; }

    public static bool Has(string key) { return _values.ContainsKey(key); }

    public static string Str(string key, string dflt)
    {
        string v;
        return _values.TryGetValue(key, out v) && v.Length > 0 ? v : dflt;
    }

    public static int Int(string key, int dflt, int lo, int hi)
    {
        string v; int n;
        if (!_values.TryGetValue(key, out v)) return dflt;
        if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return dflt;
        return n < lo ? lo : (n > hi ? hi : n);      // clamp, never trust the file
    }

    public static bool Bool(string key, bool dflt)
    {
        string v;
        if (!_values.TryGetValue(key, out v)) return dflt;
        v = v.Trim();
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                     || v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                     || v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        // Anything else is corruption, not a preference. Fall back rather than
        // silently reading "banana" as false, which is what the old code did.
        return dflt;
    }

    // --------------------------------------------------------------- write

    public static void Set(string key, object value)
    {
        _values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public static void SetBool(string key, bool value) { _values[key] = value ? "1" : "0"; }

    public static void Save()
    {
        try
        {
            _values["schema"] = Schema.ToString(CultureInfo.InvariantCulture);

            var keys = new List<string>(_values.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Abode Night View settings. Delete this file to return to defaults.");
            foreach (string k in keys) sb.AppendLine(k + "=" + _values[k]);

            // Write beside, then copy over: a power loss mid-write leaves the old
            // file intact instead of a half-written one that loads as garbage.
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Copy(tmp, Path, true);
            try { File.Delete(tmp); } catch { }

            LastSaveOk = true; LastSaveError = null;
        }
        catch (Exception e)
        {
            LastSaveOk = false;
            LastSaveError = e.Message;
        }
    }
}
