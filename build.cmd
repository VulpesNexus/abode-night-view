@echo off
rem SPDX-License-Identifier: GPL-3.0-or-later
rem Copyright (C) 2026 Vixen420
rem
rem Abode Night View is free software: you may redistribute it and/or modify it
rem under the terms of the GNU General Public License as published by the Free
rem Software Foundation, either version 3 of the License, or (at your option) any
rem later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
rem <https://www.gnu.org/licenses/>, for the full text.

rem ===========================================================================
rem  Abode Night View - build
rem ---------------------------------------------------------------------------
rem  A thin wrapper. build.ps1 is the canonical script and holds the one copy
rem  of the source list; this file exists so a normal machine can build with a
rem  double-click.
rem
rem  NOTE: cmd.exe resolves paths in the OEM codepage and cannot cd into a
rem  directory whose name contains non-ASCII characters. If this repository
rem  lives under such a path -- it does on the development machine -- run
rem
rem      .\build.ps1
rem
rem  from PowerShell instead. This file is for ASCII paths and CI.
rem ===========================================================================
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
