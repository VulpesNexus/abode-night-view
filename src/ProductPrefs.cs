// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - per-product preferences
// ----------------------------------------------------------------------------
//  Split out from both Targets.cs and the tray code on purpose. It needs the
//  adapter registry AND the settings file, and it is exactly what the tray menu
//  renders as checkmarks after a restart -- so it is kept as ordinary static
//  functions that a self-test can drive against a real settings file, rather
//  than living inside a Form where the only way to check it is to look at it.
// ============================================================================

/// <summary>
/// The two per-product questions -- is it on, and what region does it dim --
/// answered from the settings file with the adapter's own default as the
/// fallback.
///
/// These live here, outside the tray code, for one reason: they are exactly what
/// the tray menu renders as checkmarks after a restart, and a checkmark that is
/// wrong is invisible until somebody notices the utility is dimming the wrong
/// thing. Being ordinary static functions, they can be tested against a real
/// settings file rather than against a screenshot.
/// </summary>
internal static class ProductPrefs
{
    public static bool Enabled(AdobeTarget t)
    {
        string key = "target." + t.Id;
        return Config.Has(key) ? Config.Bool(key, t.DefaultEnabled) : t.DefaultEnabled;
    }

    /// <summary>
    /// An ABSENT key means "use the adapter's default", which is not the same as
    /// an empty or unrecognized value. Collapsing those two cases is how every
    /// product silently ended up on "canvas" and Photoshop pointed at the wrong
    /// rectangle; Region.Normalize returns null for absent so they stay distinct.
    /// </summary>
    public static string RegionOf(AdobeTarget t)
    {
        string key = "region." + t.Id;
        if (!Config.Has(key)) return t.DefaultRegion;
        string v = Region.Normalize(Config.Str(key, ""));
        return v ?? t.DefaultRegion;
    }
}
