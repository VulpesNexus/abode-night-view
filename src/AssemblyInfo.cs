// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - assembly and Win32 version metadata
// ----------------------------------------------------------------------------
//  csc turns these attributes into the Win32 VERSIONINFO resource, which is what
//  Explorer's Details tab, Task Manager, and most antivirus reputation systems
//  read. An unsigned binary with no version block at all looks considerably more
//  suspicious than an unsigned binary that says who and what it is.
//
//  This file is the ONLY place the version is written. Diag.Version, --version,
//  the tray header and the About box all read it back out of the built binary's
//  version resource at run time, so they cannot disagree with each other or with
//  what Explorer shows.
// ============================================================================

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Abode Night View")]
[assembly: AssemblyDescription("Display-only dimmer for Adobe document viewports")]
[assembly: AssemblyProduct("Abode Night View")]
[assembly: AssemblyCompany("Abode Night View")]
[assembly: AssemblyCopyright("Copyright © 2026 Vixen420. GPL-3.0-or-later, no warranty.")]
[assembly: AssemblyVersion("1.4.1.0")]
[assembly: AssemblyFileVersion("1.4.1.0")]
[assembly: AssemblyInformationalVersion("1.4.1")]
[assembly: ComVisible(false)]
