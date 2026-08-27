// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - TestTarget: a controllable stand-in for the InDesign frame
// ----------------------------------------------------------------------------
//  Why this exists
//      Most of the compatibility audit is about the OVERLAY -- does it land on
//      the exact rectangle, does it follow a window across monitors, does it
//      survive negative desktop coordinates, does the multiply come out at k --
//      and none of that is specific to InDesign. Testing it against InDesign
//      means the measurement only works when the canvas happens to be
//      unobstructed and nobody touches the machine, which is not a test, it is
//      a coincidence.
//
//      This is a plain GDI window with a known rectangle and a known pixel
//      pattern that can be driven from a script. It answers three questions
//      InDesign cannot be made to answer on demand:
//
//        1. the per-level transfer function, exactly, from one capture -- the
//           client area is a step wedge of the six levels we care about;
//        2. geometry tracking, by moving and resizing to arbitrary rectangles,
//           including onto a monitor at negative desktop coordinates;
//        3. whether the overlay depends on the TARGET's renderer -- this window
//           is drawn by GDI with no Direct3D anywhere, so if the dimming is
//           identical over it and over InDesign's GPU-composited canvas, the
//           answer is no.
//
//  Not shipped. Development tree only.
//
//  Usage
//      TestTarget.exe --rect=100,100,1200,800 [--seconds=60] [--title=NAME]
//      TestTarget.exe --rect=... --wedge=0,32,64,128,192,255
// ============================================================================

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class TargetForm : Form
{
    private int[] _levels = { 0, 32, 64, 128, 192, 255 };

    public TargetForm(string title, Rectangle rect, int[] levels)
    {
        if (levels != null && levels.Length > 0) _levels = levels;
        Text = title;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = true;
        BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.Opaque, true);
        Bounds = rect;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>
    /// Horizontal bands of exact 8-bit grey. A single screen capture of the client
    /// area therefore contains every input level we want to measure, and the output
    /// for each is the mean of a large uniform region rather than a single pixel.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var r = ClientRectangle;
        int n = _levels.Length;
        for (int i = 0; i < n; i++)
        {
            int y0 = r.Height * i / n, y1 = r.Height * (i + 1) / n;
            using (var b = new SolidBrush(Color.FromArgb(_levels[i], _levels[i], _levels[i])))
                e.Graphics.FillRectangle(b, 0, y0, r.Width, y1 - y0);
        }
    }
}

internal static class TestTarget
{
    [STAThread]
    private static int Main(string[] argv)
    {
        Native.ApplyBestDpiAwareness();

        var rect = new Rectangle(200, 200, 1000, 700);
        string title = "NightView Test Target";
        int seconds = 0;
        int[] levels = null;

        foreach (var a in argv)
        {
            try
            {
                if (a.StartsWith("--rect="))
                {
                    var p = a.Substring(7).Split(',');
                    if (p.Length == 4)
                        rect = new Rectangle(int.Parse(p[0], CultureInfo.InvariantCulture),
                                             int.Parse(p[1], CultureInfo.InvariantCulture),
                                             int.Parse(p[2], CultureInfo.InvariantCulture),
                                             int.Parse(p[3], CultureInfo.InvariantCulture));
                }
                else if (a.StartsWith("--seconds=")) seconds = int.Parse(a.Substring(10));
                else if (a.StartsWith("--title=")) title = a.Substring(8);
                else if (a.StartsWith("--wedge="))
                {
                    var p = a.Substring(8).Split(',');
                    levels = new int[p.Length];
                    for (int i = 0; i < p.Length; i++) levels[i] = int.Parse(p[i], CultureInfo.InvariantCulture);
                }
            }
            catch (FormatException) { }
        }

        Application.EnableVisualStyles();
        var form = new TargetForm(title, rect, levels);

        if (seconds > 0)
        {
            var t = new Timer { Interval = seconds * 1000 };
            t.Tick += (s, e) => { t.Stop(); form.Close(); };
            t.Start();
        }

        Console.WriteLine("TestTarget pid " + System.Diagnostics.Process.GetCurrentProcess().Id);
        Application.Run(form);
        return 0;
    }
}
