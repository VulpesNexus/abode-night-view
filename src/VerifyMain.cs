// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - entry point for the standalone Verify.exe
// ----------------------------------------------------------------------------
//  The verifier is compiled twice from the same source: once into AbodeNightView.exe
//  (reached as --verify / --watch / --baseline) so testers have a single file,
//  and once into this console exe for the development tree, where a real console
//  subsystem binary is more pleasant than a GUI binary borrowing a console.
//
//  Only this file differs between the two builds, which is what keeps the
//  shipped verifier and the tested verifier the same code.
// ============================================================================

internal static class VerifyEntry
{
    private static int Main(string[] argv)
    {
        // Paths on this machine contain non-ASCII characters; without this they
        // print as mojibake in the console OEM codepage, which is the one place
        // a report most needs to be readable.
        try { System.Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }
        return Verify.Run(argv);
    }
}
