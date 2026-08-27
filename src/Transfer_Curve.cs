// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - Transfer: measure what the compositor ACTUALLY does to pixels.
// ----------------------------------------------------------------------------
//  Takes two captures of the identical canvas, one with the overlay off and one
//  with it on, and recovers the real per-level transfer function.
//
//  This answers two questions that cannot be settled by arithmetic on means:
//
//   1. Does DWM blend a layered window in sRGB-encoded values or in linear
//      light? A plain 8-bit multiply gives out = in * k at every level. Linear
//      blending gives a visibly bowed curve that sits well above in * k.
//
//   2. How far is the achievable curve from the highlight-compression target
//      (0->0, 32->28, 64->50, 128->85, 192->110, 255->130)?
//
//  Mismatched pixel counts at a level are reported so a capture taken while the
//  screen was changing is obvious rather than silently averaged in.
//
//  Usage:  Transfer.exe off.png on.png
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

static class Transfer
{
    static int Main(string[] argv)
    {
        if (argv.Length < 2)
        {
            Console.WriteLine("usage: Transfer.exe <off.png> <on.png>");
            return 2;
        }

        using (var a = new Bitmap(argv[0]))
        using (var b = new Bitmap(argv[1]))
        {
            if (a.Width != b.Width || a.Height != b.Height)
            {
                Console.WriteLine("captures differ in size: " + a.Width + "x" + a.Height +
                                  " vs " + b.Width + "x" + b.Height);
                return 2;
            }

            var sum = new double[256];
            var cnt = new long[256];
            long total = 0, changed = 0;

            var ra = new Rectangle(0, 0, a.Width, a.Height);
            var da = a.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var db = b.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            unsafe
            {
                for (int y = 0; y < a.Height; y++)
                {
                    byte* pa = (byte*)da.Scan0 + (long)y * da.Stride;
                    byte* pb = (byte*)db.Scan0 + (long)y * db.Stride;
                    for (int x = 0; x < a.Width; x++)
                    {
                        // Sample all three channels independently: a neutral tint
                        // must move them identically, and a warm tint must not.
                        for (int c = 0; c < 3; c++)
                        {
                            int i = pa[x * 4 + c], o = pb[x * 4 + c];
                            sum[i] += o; cnt[i]++;
                            total++; if (i != o) changed++;
                        }
                    }
                }
            }
            a.UnlockBits(da); b.UnlockBits(db);

            Console.WriteLine("Abode Night View - measured transfer function");
            Console.WriteLine("=======================================");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0} samples, {1:0.0}% changed", total, 100.0 * changed / total));
            Console.WriteLine();

            // Least-squares k over well-populated levels, forced through the origin.
            double num = 0, den = 0;
            for (int i = 1; i < 256; i++)
                if (cnt[i] > 200) { double m = sum[i] / cnt[i]; num += i * m; den += (double)i * i; }
            double k = den > 0 ? num / den : 0;

            Console.WriteLine("   in    out     in*k    linear-blend    target");
            Console.WriteLine("  ----  -----   ------   ------------   --------");
            int[] anchors = { 0, 32, 64, 128, 192, 255 };
            int[] target = { 0, 28, 50, 85, 110, 130 };
            for (int t = 0; t < anchors.Length; t++)
            {
                int i = anchors[t];
                string got = cnt[i] > 50
                    ? (sum[i] / cnt[i]).ToString("0.0", CultureInfo.InvariantCulture)
                    : "  -  ";
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,4}  {1,5}   {2,6:0.0}   {3,12:0.0}   {4,8}",
                    i, got, i * k, LinearBlend(i, k), target[t]));
            }

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  best-fit k = {0:0.0000}", k));

            // Which model fits: a straight 8-bit multiply, or blending in light?
            double eSrgb = 0, eLin = 0; int n = 0;
            for (int i = 8; i < 256; i++)
                if (cnt[i] > 200)
                {
                    double m = sum[i] / cnt[i];
                    eSrgb += Math.Abs(m - i * k);
                    eLin += Math.Abs(m - LinearBlend(i, k));
                    n++;
                }
            if (n > 0)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  mean |error| vs  out = in*k        : {0:0.00} levels\n" +
                    "  mean |error| vs  blend in linear   : {1:0.00} levels\n" +
                    "  => DWM composites this overlay in {2}",
                    eSrgb / n, eLin / n,
                    eSrgb < eLin ? "SRGB-ENCODED VALUES (a plain multiply)"
                                 : "LINEAR LIGHT"));
        }
        return 0;
    }

    /// <summary>What out = in*k would look like if the multiply happened in linear light.</summary>
    static double LinearBlend(int v, double k)
    {
        double s = v / 255.0;
        double lin = s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        lin *= k;
        double o = lin <= 0.0031308 ? lin * 12.92 : 1.055 * Math.Pow(lin, 1 / 2.4) - 0.055;
        return o * 255.0;
    }
}
