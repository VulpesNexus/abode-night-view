// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

// ============================================================================
//  Abode Night View - Adobe application target adapters
// ----------------------------------------------------------------------------
//  Why adapters and not one clever heuristic
//      The first audit removed a "largest child window" style guess because it
//      picked up transient windows during editing and dragged the overlay onto
//      the wrong rectangle. That lesson does not stop applying just because
//      there are now several products to support. So: the overlay engine is
//      product-agnostic, and everything a product-specific is confined to a
//      small adapter that knows the process name, the frame window class, and
//      the STRUCTURAL RELATIONSHIP its viewport sits in. Each adapter validates
//      that relationship before it will hand back a rectangle, and says why in
//      --probe when it cannot.
//
//  What was measured on this machine, 2026-08-21, with ProtoA.exe
//
//      InDesign 21.5.1 / Illustrator 30.7.0 / InCopy 21.5.1 / Photoshop 27.9
//      all use Adobe's OWL framework and all lay the document out identically:
//
//          <frame class>                 indesign | illustrator | incopy | Photoshop
//            OWL.Dock
//              OWL.TabPane
//                OWL.TabGroup
//                  OWL.Document          <- document viewport, incl. rulers/scrollbars
//                    <inner container>   <- canvas proper, strictly inside
//
//      measured, frame maximized on a 2560x1440 monitor:
//
//          product       OWL.Document        inner container      inner/outer
//          InDesign      43,132 2094x1259    59,148 2063x1227     96 %
//          Illustrator   42,100 1909x1291    42,100 1893x1250     94 %
//          Photoshop    663,1532 1470x939   663,1532 1454x923     97 %
//
//      The inner container's class is NOT shared -- InDesign and Illustrator use
//      "DroverLord - Window Class", Photoshop uses "Static" -- so the canvas is
//      found by the geometric relationship (largest visible descendant strictly
//      inside OWL.Document and at least half its area), which was already the
//      verified InDesign rule and which is re-validated per product here.
//
//      Acrobat 26.1.21771.0 is a different framework entirely and gets its own
//      adapter:
//
//          AcrobatSDIWindow
//            ... AVL_AVView 'AVScrollView'
//                  AVL_AVView 'AVPageView'   <- page area, 652,83 647x949
//
//      Acrobat names its views in the WINDOW TEXT rather than the class, which
//      is a stronger key than a class shared by 23 windows in the same process.
//
//  Version policy
//      There is no version whitelist anywhere in this file. A product is
//      recognized by process name + frame window class, and then the structure
//      is probed and validated. An Adobe release from a year that did not exist
//      when this was written attaches normally if it still looks like this, and
//      is reported in diagnostics as an unverified version rather than refused.
//      The display name comes from the executable's own ProductName resource
//      ("Adobe InDesign 2026"), so the tray menu is right about a product year
//      nobody here has ever seen.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

// ---------------------------------------------------------------------------
//  Regions
// ---------------------------------------------------------------------------
internal static class Region
{
    public const string Canvas = "canvas";       // innermost content area
    public const string Document = "document";   // document viewport incl. rulers/scrollbars
    public const string Client = "client";       // whole application client area
    public const string Window = "window";       // whole application window rect

    /// <summary>
    /// Normalize a region name, including the legacy InDesign-only spelling.
    /// Returns null for absent, empty or unrecognized input -- deliberately NOT
    /// "canvas". This used to fall back to canvas, and the caller that asked
    /// "what did the settings file say for this product?" could not tell an absent
    /// key from a real choice, so every product silently got canvas and the
    /// per-product default was never applied. Photoshop shipped pointing at the
    /// wrong rectangle because of it.
    /// </summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Trim().ToLowerInvariant();
        if (s == "owldoc") return Document;       // Night View 1.0 wrote this
        if (s == Canvas || s == Document || s == Client || s == Window) return s;
        return null;                              // unknown: caller decides
    }

    public static string Pretty(string s)
    {
        switch (Normalize(s))
        {
            case Canvas: return "Canvas only";
            case Document: return "Document viewport";
            case Client: return "Application client area";
            case Window: return "Whole window";
        }
        return s;
    }
}

// ---------------------------------------------------------------------------
//  Why a product is, or is not, being dimmed
// ---------------------------------------------------------------------------
/// <summary>
/// "Nothing is dimmed" has four completely different causes and one symptom,
/// and the difference between them is the whole content of a bug report. They
/// are separated here so the tray menu can say which one it is, rather than
/// leaving the user to infer it from an absence.
/// </summary>
internal enum TargetStatus
{
    /// <summary>No process, or a process with no visible window yet (starting up).</summary>
    NotRunning,

    /// <summary>Attached: the expected structure was found and validated.</summary>
    Attached,

    /// <summary>The application is recognized, and no document is open in it.</summary>
    NoDocument,

    /// <summary>
    /// The application is running and visible, and this build cannot hook into
    /// it: either no top-level window of the expected class exists, or the
    /// frame is there and the viewport hierarchy inside it is not one this
    /// build knows how to read. Both mean a version whose windows have been
    /// rearranged, and both are worth naming rather than being folded into
    /// "not running".
    /// </summary>
    Unsupported,
}

// ---------------------------------------------------------------------------
//  One adapter per Adobe product family
// ---------------------------------------------------------------------------
internal abstract class AdobeTarget
{
    /// <summary>Settings key suffix and --probe argument. Stable forever.</summary>
    public abstract string Id { get; }

    /// <summary>Fallback label, used before a running instance tells us its real name.</summary>
    public abstract string Family { get; }

    /// <summary>
    /// The family name with the "Adobe" taken off the front. Every product this
    /// utility knows about is an Adobe one, so in a list of them the word
    /// carries no information and only makes the list harder to scan. Family
    /// keeps the full name, and that is what the probe and the diagnostics
    /// report print, where the reader may not have that context.
    /// </summary>
    public string ShortName { get { return Shorten(Family); } }

    /// <summary>
    /// The same treatment for a label that came out of the running executable's
    /// own ProductName resource: "Adobe InDesign 2026" becomes "InDesign 2026".
    /// </summary>
    public static string Shorten(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        string s = name.Trim();
        return s.StartsWith("Adobe ", StringComparison.OrdinalIgnoreCase)
            ? s.Substring(6).Trim()
            : s;
    }

    /// <summary>Process names, without ".exe", case-insensitive.</summary>
    public abstract string[] ProcessNames { get; }

    /// <summary>Frame (application) window classes, matched exactly, case-insensitive.</summary>
    public abstract string[] FrameClasses { get; }

    /// <summary>Region used when the user has never chosen one for this product.</summary>
    public virtual string DefaultRegion { get { return Region.Canvas; } }

    /// <summary>
    /// Whether the product is dimmed when the user has never expressed a preference.
    /// The policy is SEMANTIC, not a confidence rating: products whose viewport is
    /// predominantly white paper or artboard default on; products where dimming
    /// changes the artwork you are judging default off. Confidence is reported
    /// separately, in --probe and the compatibility table in docs/compatibility.md.
    /// </summary>
    public virtual bool DefaultEnabled { get { return true; } }

    /// <summary>
    /// Shown by --probe and documented in docs/design-notes.md. Deliberately NOT in
    /// the tray menu: it is a sentence of rationale, and a menu item is a place for
    /// a name and a state, not for an argument. The default it explains still holds
    /// either way -- the note was never what made Photoshop start switched off.
    /// May be null.
    /// </summary>
    public virtual string SemanticNote { get { return null; } }

    /// <summary>Human description of the structure this adapter requires.</summary>
    public abstract string ExpectedStructure { get; }

    /// <summary>Validated document viewports inside one frame window. May be empty.</summary>
    public abstract List<IntPtr> Viewports(IntPtr frame);

    /// <summary>The innermost content window of a viewport. Never zero: falls back to the viewport.</summary>
    public abstract IntPtr Canvas(IntPtr viewport);

    /// <summary>The container that includes rulers and scrollbars. Never zero.</summary>
    public virtual IntPtr Document(IntPtr viewport) { return viewport; }

    /// <summary>
    /// Why this frame is or is not being dimmed, for a frame already matched to
    /// this adapter.
    ///
    /// The distinction that matters is NoDocument against Unsupported. The two
    /// look identical from outside -- nothing is dimmed -- and they need
    /// opposite responses: open a document, or send a bug report naming the
    /// version. Guessing between them is what the old single "validation
    /// failed" line did, and it guessed "no document is open" every time.
    /// </summary>
    public virtual TargetStatus Inspect(IntPtr frame)
    { return Viewports(frame).Count > 0 ? TargetStatus.Attached : TargetStatus.Unsupported; }

    public bool OwnsProcess(string processName)
    {
        foreach (string p in ProcessNames)
            if (string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool OwnsFrameClass(string cls)
    {
        foreach (string c in FrameClasses)
            if (string.Equals(c, cls, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Resolve one viewport to a screen rectangle for the requested region, or an
    /// empty rect. Clipping to the frame client is the caller's job.
    /// </summary>
    public Native.RECT RectOf(IntPtr frame, IntPtr viewport, string region)
    {
        switch (Region.Normalize(region))
        {
            case Region.Window: return Native.RectOf(frame);
            case Region.Client: return Native.ClientRectOnScreen(frame);
            case Region.Document:
                {
                    IntPtr d = Document(viewport);
                    return d == IntPtr.Zero ? new Native.RECT() : Native.RectOf(d);
                }
            default:
                {
                    IntPtr c = Canvas(viewport);
                    return c == IntPtr.Zero ? new Native.RECT() : Native.RectOf(c);
                }
        }
    }
}

// ---------------------------------------------------------------------------
//  Adobe OWL products: InDesign, Illustrator, InCopy, Photoshop
// ---------------------------------------------------------------------------
internal class OwlTarget : AdobeTarget
{
    private readonly string _id, _family, _proc, _frameClass;

    public OwlTarget(string id, string family, string proc, string frameClass)
    { _id = id; _family = family; _proc = proc; _frameClass = frameClass; }

    public override string Id { get { return _id; } }
    public override string Family { get { return _family; } }
    public override string[] ProcessNames { get { return new[] { _proc }; } }
    public override string[] FrameClasses { get { return new[] { _frameClass }; } }

    public override string ExpectedStructure
    {
        get
        {
            return "frame class '" + _frameClass +
                   "' > ... > OWL.TabGroup > OWL.Document > inner view container";
        }
    }

    /// <summary>
    /// Every visible OWL.Document whose PARENT is an OWL.TabGroup. The parent check
    /// is the whole point: it is what stops a panel, a flyout or a transient editing
    /// window from being mistaken for the document, and it is what will fail loudly
    /// if a future Adobe release rearranges the hierarchy, instead of quietly
    /// dimming the wrong thing.
    ///
    /// More than one is normal and correct -- Window > Arrange > 2-up tiles two
    /// documents side by side and both are visible at once.
    /// </summary>
    public override List<IntPtr> Viewports(IntPtr frame)
    {
        var found = new List<IntPtr>();
        if (frame == IntPtr.Zero || !Native.IsWindow(frame)) return found;

        Native.RECT client = Native.ClientRectOnScreen(frame);
        long clientArea = (long)client.W * client.H;
        if (clientArea <= 0) return found;

        foreach (IntPtr h in Native.Descendants(frame))
        {
            if (!Native.IsWindowVisible(h)) continue;
            if (!Native.ClassOf(h).Equals("OWL.Document", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Native.ClassOf(Native.GetParent(h)).Equals("OWL.TabGroup", StringComparison.OrdinalIgnoreCase))
                continue;

            var r = Native.RectOf(h);
            if (r.IsEmpty) continue;
            if (!TargetRegistry.Inside(r, client, 8)) continue;
            if ((long)r.W * r.H * 25 < clientArea) continue;   // < 4 % of the client is not a document
            found.Add(h);
        }
        return found;
    }

    public override IntPtr Canvas(IntPtr viewport)
    {
        IntPtr inner = TargetRegistry.LargestStrictlyInside(viewport, 2);
        return inner == IntPtr.Zero ? viewport : inner;
    }

    /// <summary>
    /// The discriminator is the OWL framework itself. If any OWL.* window is
    /// visible inside the frame then this IS the application we know, built the
    /// way we know, and the only missing piece is a document. If there is no
    /// OWL window at all, the frame is made of something this build has never
    /// seen and no amount of opening documents will help.
    ///
    /// It errs toward NoDocument on purpose: that message costs a user who
    /// does have a document open one confused moment, where a wrong
    /// "unsupported version" costs them the belief that the utility works with
    /// their Adobe release at all.
    /// </summary>
    public override TargetStatus Inspect(IntPtr frame)
    {
        if (Viewports(frame).Count > 0) return TargetStatus.Attached;
        foreach (IntPtr h in Native.Descendants(frame))
        {
            if (!Native.IsWindowVisible(h)) continue;
            if (Native.ClassOf(h).StartsWith("OWL.", StringComparison.OrdinalIgnoreCase))
                return TargetStatus.NoDocument;
        }
        return TargetStatus.Unsupported;
    }
}

// ---------------------------------------------------------------------------
//  Adobe Acrobat
// ---------------------------------------------------------------------------
internal sealed class AcrobatTarget : AdobeTarget
{
    public override string Id { get { return "acrobat"; } }
    public override string Family { get { return "Adobe Acrobat"; } }
    public override string[] ProcessNames { get { return new[] { "Acrobat" }; } }
    public override string[] FrameClasses { get { return new[] { "AcrobatSDIWindow" }; } }
    public override string DefaultRegion { get { return Region.Canvas; } }

    public override string ExpectedStructure
    {
        get { return "frame class 'AcrobatSDIWindow' > ... > AVL_AVView titled 'AVPageView'"; }
    }

    /// <summary>
    /// Acrobat identifies its views by window TEXT, not by class -- there are 23
    /// AVL_AVView windows in a plain single-document session and only one of them
    /// is called AVPageView. Reading the text is a much stronger key than the
    /// class, and it survived from Acrobat's oldest builds into 26.1.
    /// </summary>
    public override List<IntPtr> Viewports(IntPtr frame)
    {
        var found = new List<IntPtr>();
        if (frame == IntPtr.Zero || !Native.IsWindow(frame)) return found;

        Native.RECT client = Native.ClientRectOnScreen(frame);
        long clientArea = (long)client.W * client.H;
        if (clientArea <= 0) return found;

        foreach (IntPtr h in Native.Descendants(frame))
        {
            if (!Native.IsWindowVisible(h)) continue;
            if (!Native.ClassOf(h).Equals("AVL_AVView", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Native.TitleOf(h).Equals("AVPageView", StringComparison.Ordinal)) continue;

            var r = Native.RectOf(h);
            if (r.IsEmpty) continue;
            if (!TargetRegistry.Inside(r, client, 8)) continue;
            if ((long)r.W * r.H * 25 < clientArea) continue;
            found.Add(h);
        }
        return found;
    }

    /// <summary>AVPageView already excludes the scrollbars, so it IS the canvas.</summary>
    public override IntPtr Canvas(IntPtr viewport) { return viewport; }

    /// <summary>
    /// Same reasoning as the OWL adapter: an AVL_AVView anywhere in the frame
    /// means this is Acrobat's own view framework and only the page view is
    /// missing, which is what no open document looks like. None at all means a
    /// frame built out of something else.
    /// </summary>
    public override TargetStatus Inspect(IntPtr frame)
    {
        if (Viewports(frame).Count > 0) return TargetStatus.Attached;
        foreach (IntPtr h in Native.Descendants(frame))
        {
            if (!Native.IsWindowVisible(h)) continue;
            if (Native.ClassOf(h).Equals("AVL_AVView", StringComparison.OrdinalIgnoreCase))
                return TargetStatus.NoDocument;
        }
        return TargetStatus.Unsupported;
    }

    /// <summary>The enclosing AVScrollView adds the scrollbars back.</summary>
    public override IntPtr Document(IntPtr viewport)
    {
        IntPtr p = Native.GetParent(viewport);
        if (p != IntPtr.Zero && Native.ClassOf(p).Equals("AVL_AVView", StringComparison.OrdinalIgnoreCase)
            && Native.TitleOf(p).Equals("AVScrollView", StringComparison.Ordinal))
            return p;
        return viewport;
    }
}

// ---------------------------------------------------------------------------
//  A frame window that belongs to a recognized product
// ---------------------------------------------------------------------------
internal sealed class DetectedFrame
{
    public AdobeTarget Adapter;
    public IntPtr Frame;
    public uint Pid;
    public string ProductName;      // "Adobe InDesign 2026", read from the executable
    public string ProductVersion;   // "21.5.1"

    public string Label
    {
        get { return string.IsNullOrEmpty(ProductName) ? Adapter.Family : ProductName; }
    }

    /// <summary>The label without the leading "Adobe", for the tray menu.</summary>
    public string ShortLabel { get { return AdobeTarget.Shorten(Label); } }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture, "{0} pid {1} frame 0x{2:X8}",
                             Label, Pid, Frame.ToInt64());
    }
}

// ---------------------------------------------------------------------------
//  The registry and the discovery pass
// ---------------------------------------------------------------------------
internal static class TargetRegistry
{
    /// <summary>
    /// Order matters only for presentation. Photoshop's default-off is a semantic
    /// decision documented on the class; see AdobeTarget.DefaultEnabled.
    /// </summary>
    public static readonly AdobeTarget[] All = new AdobeTarget[]
    {
        new OwlTarget("indesign",    "Adobe InDesign",    "InDesign",    "indesign"),
        new OwlTarget("illustrator", "Adobe Illustrator", "Illustrator", "illustrator"),
        new OwlTarget("incopy",      "Adobe InCopy",      "InCopy",      "incopy"),
        new PhotoshopTarget(),
        new AcrobatTarget(),
    };

    public static AdobeTarget ById(string id)
    {
        foreach (var t in All)
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    // ------------------------------------------------------------ geometry --

    public static bool Inside(Native.RECT inner, Native.RECT outer, int slack)
    {
        return inner.Left >= outer.Left - slack && inner.Top >= outer.Top - slack &&
               inner.Right <= outer.Right + slack && inner.Bottom <= outer.Bottom + slack;
    }

    /// <summary>
    /// Largest visible descendant strictly inside <paramref name="parent"/> and at
    /// least 1/<paramref name="minFraction"/> of its area. This is the verified
    /// InDesign canvas rule, lifted out so every OWL product uses the same one
    /// implementation and they cannot drift apart.
    ///
    /// The area floor is what rules out the transient children Adobe applications
    /// spawn while you drag a frame or edit text; without it, "largest inside"
    /// picks one of those up and the overlay jumps to the wrong rectangle.
    /// </summary>
    public static IntPtr LargestStrictlyInside(IntPtr parent, int minFraction)
    {
        if (parent == IntPtr.Zero || !Native.IsWindow(parent)) return IntPtr.Zero;
        Native.RECT pr = Native.RectOf(parent);
        long pa = (long)pr.W * pr.H;
        if (pa <= 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero; long bestArea = -1;
        foreach (IntPtr h in Native.Descendants(parent))
        {
            if (!Native.IsWindowVisible(h)) continue;
            var r = Native.RectOf(h);
            if (r.IsEmpty) continue;
            if (!Inside(r, pr, 0)) continue;
            long a = (long)r.W * r.H;
            if (a >= pa) continue;                       // must be STRICTLY smaller
            if (a * minFraction < pa) continue;           // and not a scrap
            if (a > bestArea) { bestArea = a; best = h; }
        }
        return best;
    }

    // ----------------------------------------------------------- discovery --

    private static readonly Dictionary<uint, string> _pidNames = new Dictionary<uint, string>();
    private static readonly Dictionary<uint, string[]> _pidVersion = new Dictionary<uint, string[]>();

    /// <summary>Forget cached per-process facts. Called when a process exits.</summary>
    public static void Forget(uint pid) { _pidNames.Remove(pid); _pidVersion.Remove(pid); }

    public static void ForgetAll() { _pidNames.Clear(); _pidVersion.Clear(); }

    private static string NameOf(uint pid)
    {
        string n;
        if (_pidNames.TryGetValue(pid, out n)) return n;
        try { using (var p = Process.GetProcessById((int)pid)) n = p.ProcessName; }
        catch { n = ""; }
        _pidNames[pid] = n;
        return n;
    }

    /// <summary>
    /// ProductName and ProductVersion straight out of the running executable, e.g.
    /// "Adobe Illustrator 2026" / "30.7.0". This is how a product year nobody has
    /// tested still gets a correct label without a version table anywhere.
    /// Cached: reading the version resource is not free and it cannot change while
    /// the process lives.
    /// </summary>
    private static string[] VersionOf(uint pid)
    {
        string[] v;
        if (_pidVersion.TryGetValue(pid, out v)) return v;
        v = new string[] { null, null };
        try
        {
            using (var p = Process.GetProcessById((int)pid))
            {
                var fi = p.MainModule.FileVersionInfo;
                v[0] = Squeeze(fi.ProductName);
                v[1] = Squeeze(fi.ProductVersion);
            }
        }
        catch { }      // 32-bit target, protected process, race with exit: all fine
        _pidVersion[pid] = v;
        return v;
    }

    /// <summary>
    /// A version-resource string as something that can be put in a sentence:
    /// trimmed, and with any run of whitespace inside it reduced to one space.
    ///
    /// This is not tidiness for its own sake. Photoshop's ProductName resource
    /// ends in a space, so every label built as name + " " + something came out
    /// with a gap in it -- reported as a double space in the tray menu and in
    /// the diagnostics report, and fixed once here rather than at each of the
    /// six places that print a product name.
    /// </summary>
    public static string Squeeze(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        bool gap = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { gap = sb.Length > 0; continue; }
            if (gap) { sb.Append(' '); gap = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One EnumWindows pass over the whole desktop, classifying top-level windows
    /// by class. This is deliberately NOT Process.GetProcessesByName per product:
    /// that takes a full process snapshot per call, and there are five products.
    /// Enumerating windows is roughly two hundred cheap calls and it also gives us
    /// the frame handle we were going to have to look up anyway.
    ///
    /// A process name check is still applied, so a third-party window that happens
    /// to register the class "illustrator" cannot be mistaken for Illustrator.
    /// </summary>
    public static List<DetectedFrame> Discover(IEnumerable<AdobeTarget> adapters)
    {
        var wanted = new List<AdobeTarget>(adapters);
        var result = new List<DetectedFrame>();
        if (wanted.Count == 0) return result;

        Native.EnumWindows((h, lp) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            string cls = Native.ClassOf(h);
            uint pid; Native.GetWindowThreadProcessId(h, out pid);

            AdobeTarget hit = null;
            foreach (var t in wanted)
            {
                // A frame class of "*" means "any top-level window of that
                // process". No shipped adapter uses it; it exists so the audit
                // harness can point the real engine at a window it controls
                // without the engine needing a test mode.
                bool classOk = t.OwnsFrameClass(cls) || t.OwnsFrameClass("*");
                if (!classOk) continue;
                if (!t.OwnsProcess(NameOf(pid))) continue;
                hit = t; break;
            }
            if (hit == null) return true;

            string[] v = VersionOf(pid);
            result.Add(new DetectedFrame
            {
                Adapter = hit,
                Frame = h,
                Pid = pid,
                ProductName = v[0],
                ProductVersion = v[1],
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // -------------------------------------------------------------- survey --

    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Why each adapter is or is not attached, right now.
    ///
    /// The expensive half is conditional on purpose: the desktop is only
    /// re-enumerated for products that produced no frame at all, because every
    /// remaining question can only arise for those. With every product attached
    /// this costs nothing beyond the frames already in hand.
    ///
    /// The caller passes the frames IT is tracking, and it only tracks products
    /// the user has switched on. So "no frame in the list" does not mean "no
    /// frame" -- an unticked Photoshop that is running perfectly well arrives
    /// here indistinguishable from a version whose windows cannot be read. That
    /// is why the missing ones are re-discovered and INSPECTED rather than
    /// assumed unhookable: an unticked product must report the same state as a
    /// ticked one, and the same state --probe reports, since a user comparing
    /// the two and finding them different has no way to tell which is lying.
    /// </summary>
    public static Dictionary<string, TargetStatus> Survey(
        IEnumerable<AdobeTarget> adapters, List<DetectedFrame> found)
    {
        var wanted = new List<AdobeTarget>(adapters);
        var status = new Dictionary<string, TargetStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in wanted) status[t.Id] = TargetStatus.NotRunning;

        // A product with frames gets the BEST answer any of its frames gives.
        // Two InDesign windows, one with a document open and one without, is
        // "attached" -- something is being dimmed -- not "no document open".
        foreach (var d in found)
        {
            if (!status.ContainsKey(d.Adapter.Id)) continue;
            status[d.Adapter.Id] = Better(status[d.Adapter.Id], d.Adapter.Inspect(d.Frame));
        }

        var missing = new List<AdobeTarget>();
        foreach (var t in wanted) if (status[t.Id] == TargetStatus.NotRunning) missing.Add(t);
        if (missing.Count == 0) return status;

        // Ask the desktop for the ones we were handed no frame for. Anything
        // that comes back has a frame window this build recognizes, so it gets
        // the real answer rather than a guess.
        foreach (var d in Discover(missing))
            if (status.ContainsKey(d.Adapter.Id))
                status[d.Adapter.Id] = Better(status[d.Adapter.Id], d.Adapter.Inspect(d.Frame));

        var unknown = new List<AdobeTarget>();
        foreach (var t in missing) if (status[t.Id] == TargetStatus.NotRunning) unknown.Add(t);
        if (unknown.Count == 0) return status;

        // Still nothing. If the process is nevertheless showing something that
        // looks like an application frame, then it is running and this build
        // cannot read its windows -- which is the one case "unsupported version"
        // is meant to name.
        List<uint> pids = FramedPids();
        foreach (var t in unknown)
            foreach (uint pid in pids)
                if (t.OwnsProcess(NameOf(pid))) { status[t.Id] = TargetStatus.Unsupported; break; }

        return status;
    }

    private static int Rank(TargetStatus s)
    {
        switch (s)
        {
            case TargetStatus.Attached: return 3;
            case TargetStatus.NoDocument: return 2;
            case TargetStatus.Unsupported: return 1;
        }
        return 0;
    }

    private static TargetStatus Better(TargetStatus a, TargetStatus b)
    { return Rank(b) > Rank(a) ? b : a; }

    /// <summary>
    /// Processes that own at least one window that LOOKS like an application
    /// frame. "The process exists" is the wrong question and asking it produces
    /// a false alarm on every launch: an Adobe application spends a long time
    /// starting with a process, a splash screen and no frame, and calling that
    /// an unsupported version would be wrong every single time.
    ///
    /// So the test is structural rather than temporal -- unowned, not a tool
    /// window, and big -- which is what a main window is and what a splash, a
    /// dialog and a background helper are not. It is a heuristic and it is only
    /// used to decide which of two MESSAGES to show; --probe is the
    /// authoritative answer and it lists what it actually found.
    /// </summary>
    private static List<uint> FramedPids()
    {
        var pids = new List<uint>();
        Native.EnumWindows((h, lp) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            long ex = Native.GetWindowLongPtrW(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            Native.RECT r = Native.RectOf(h);
            if (r.W < 640 || r.H < 480) return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (!pids.Contains(pid)) pids.Add(pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    /// <summary>The one place a status is turned into words, so the tray menu,
    /// the balloon and the reports cannot describe the same state differently.</summary>
    public static string Explain(TargetStatus s)
    {
        switch (s)
        {
            case TargetStatus.Attached: return "attached";
            case TargetStatus.NoDocument: return "no document open";
            case TargetStatus.Unsupported: return "unsupported version";
        }
        return "not running";
    }

    // --------------------------------------------------------------- probe --

    /// <summary>
    /// The structured answer to "why did this version fail to attach?". Written
    /// for someone reading it in a bug report with no debugger and no Visual
    /// Studio, so it names each thing it looked for and what it found instead.
    /// </summary>
    public static void Probe(System.IO.TextWriter w, AdobeTarget only)
    {
        var adapters = new List<AdobeTarget>();
        if (only != null) adapters.Add(only); else adapters.AddRange(All);

        foreach (var t in adapters)
        {
            w.WriteLine();
            w.WriteLine("== " + t.Family + " (id " + t.Id + ")");
            w.WriteLine("   process names        " + string.Join(", ", t.ProcessNames));
            w.WriteLine("   frame window class   " + string.Join(", ", t.FrameClasses));
            w.WriteLine("   expected structure   " + t.ExpectedStructure);
            w.WriteLine("   default region       " + t.DefaultRegion);
            w.WriteLine("   default state        " + (t.DefaultEnabled ? "on" : "off") +
                        (t.SemanticNote == null ? "" : " – " + t.SemanticNote));

            var frames = Discover(new[] { t });
            if (frames.Count == 0)
            {
                bool running = false;
                foreach (string pn in t.ProcessNames)
                {
                    try { if (Process.GetProcessesByName(pn).Length > 0) running = true; }
                    catch { }
                }
                if (!running) { w.WriteLine("   RESULT               not running."); continue; }

                w.WriteLine("   RESULT               UNSUPPORTED VERSION – the process is running and no");
                w.WriteLine("                        window of the expected class exists, so there is");
                w.WriteLine("                        nothing for Abode Night View to attach to.");
                w.WriteLine("                        This is what a renamed frame class looks like. Send");
                w.WriteLine("                        this report with the product version above.");
                w.WriteLine("                        Visible top-level classes in that process:");
                foreach (string line in TopLevelClasses(t)) w.WriteLine("                          " + line);
                continue;
            }

            foreach (var f in frames)
            {
                w.WriteLine("   ----");
                w.WriteLine("   product name         " + (f.ProductName ?? "(version resource unreadable)"));
                w.WriteLine("   product version      " + (f.ProductVersion ?? "?"));
                w.WriteLine("   pid                  " + f.Pid);
                w.WriteLine("   frame HWND           " + Hex(f.Frame) + " class " + Native.ClassOf(f.Frame));
                w.WriteLine("   frame rect           " + Native.RectOf(f.Frame) +
                            (Native.IsIconic(f.Frame) ? " (MINIMIZED)" : ""));
                w.WriteLine("   frame client rect    " + Native.ClientRectOnScreen(f.Frame));
                w.WriteLine("   frame DPI            " + Native.DpiOf(f.Frame) + " (" +
                            (Native.DpiOf(f.Frame) * 100 / 96) + "%)");

                var vps = f.Adapter.Viewports(f.Frame);
                w.WriteLine("   viewports found      " + vps.Count);
                if (vps.Count == 0)
                {
                    // Which of the two failures this is, rather than a guess at
                    // the friendlier one. See AdobeTarget.Inspect.
                    TargetStatus st = f.Adapter.Inspect(f.Frame);
                    if (st == TargetStatus.NoDocument)
                    {
                        w.WriteLine("   VALIDATION           NO DOCUMENT OPEN – the application's own window");
                        w.WriteLine("                        framework is there and there is no document view");
                        w.WriteLine("                        inside it. Open a document and re-run.");
                    }
                    else
                    {
                        w.WriteLine("   VALIDATION           UNSUPPORTED VERSION – the frame is there and the");
                        w.WriteLine("                        expected viewport hierarchy inside it is not.");
                        w.WriteLine("                        Expected: " + f.Adapter.ExpectedStructure);
                        w.WriteLine("                        Nothing will be dimmed for this window. Send this");
                        w.WriteLine("                        report with the product version above.");
                    }
                    foreach (string line in Skeleton(f.Frame)) w.WriteLine("                          " + line);
                    continue;
                }

                w.WriteLine("   VALIDATION           passed");
                int i = 0;
                foreach (IntPtr vp in vps)
                {
                    i++;
                    IntPtr canvas = f.Adapter.Canvas(vp);
                    IntPtr doc = f.Adapter.Document(vp);
                    w.WriteLine("     viewport " + i);
                    w.WriteLine("       container          " + Hex(vp) + " " + Native.ClassOf(vp) +
                                " " + Native.RectOf(vp));
                    w.WriteLine("       parent             " + Native.ClassOf(Native.GetParent(vp)));
                    w.WriteLine("       region=document    " + Hex(doc) + " " + Native.ClassOf(doc) +
                                " " + Native.RectOf(doc));
                    w.WriteLine("       region=canvas      " + Hex(canvas) + " " + Native.ClassOf(canvas) +
                                " " + Native.RectOf(canvas) +
                                (canvas == vp ? " (no inner container; using the viewport)" : ""));
                }
            }
        }
    }

    private static List<string> TopLevelClasses(AdobeTarget t)
    {
        var seen = new List<string>();
        Native.EnumWindows((h, lp) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (!t.OwnsProcess(NameOf(pid))) return true;
            string line = Native.ClassOf(h) + " " + Native.RectOf(h) + " '" + Native.TitleOf(h) + "'";
            if (!seen.Contains(line)) seen.Add(line);
            return true;
        }, IntPtr.Zero);
        if (seen.Count == 0) seen.Add("(none visible)");
        return seen;
    }

    /// <summary>
    /// A deliberately SMALL structural summary: the visible descendant classes and
    /// how many of each, biggest first. Dumping every HWND is what ProtoA.exe is
    /// for; this has to stay short enough to paste into a bug report.
    /// </summary>
    private static List<string> Skeleton(IntPtr frame)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var biggest = new Dictionary<string, Native.RECT>(StringComparer.Ordinal);
        foreach (IntPtr h in Native.Descendants(frame))
        {
            if (!Native.IsWindowVisible(h)) continue;
            string c = Native.ClassOf(h);
            var r = Native.RectOf(h);
            int n; counts[c] = counts.TryGetValue(c, out n) ? n + 1 : 1;
            Native.RECT b;
            if (!biggest.TryGetValue(c, out b) || (long)r.W * r.H > (long)b.W * b.H) biggest[c] = r;
        }
        var keys = new List<string>(counts.Keys);
        keys.Sort((a, b) =>
        {
            var ra = biggest[a]; var rb = biggest[b];
            return ((long)rb.W * rb.H).CompareTo((long)ra.W * ra.H);
        });
        var outp = new List<string>();
        outp.Add("visible descendant classes, largest first:");
        int shown = 0;
        foreach (string k in keys)
        {
            if (shown++ >= 12) { outp.Add("  ... and " + (keys.Count - 12) + " more classes"); break; }
            outp.Add(string.Format(CultureInfo.InvariantCulture, "  {0,-32} x{1,-4} largest {2}",
                                   k, counts[k], biggest[k]));
        }
        return outp;
    }

    private static string Hex(IntPtr h)
    { return "0x" + h.ToInt64().ToString("X8", CultureInfo.InvariantCulture); }
}

// ---------------------------------------------------------------------------
//  Photoshop, which is the same machinery and a different product question
// ---------------------------------------------------------------------------
internal sealed class PhotoshopTarget : OwlTarget
{
    public PhotoshopTarget() : base("photoshop", "Adobe Photoshop", "Photoshop", "Photoshop") { }

    /// <summary>
    /// Off unless the user asks for it. Not because it does not work -- it works
    /// exactly as well as Illustrator does, and was measured doing so -- but
    /// because of what it means. InDesign and Illustrator viewports are mostly
    /// white paper you are laying content onto; dimming them dims the glare.
    /// A Photoshop viewport is mostly the IMAGE, and dimming it changes the
    /// apparent brightness of the thing you are editing. Whole-canvas dimming is
    /// a legitimate thing to want at 2am, but it is not a safe default, and the
    /// tool must not pretend it is only dimming "the paper" when there is no paper.
    /// </summary>
    public override bool DefaultEnabled { get { return false; } }

    public override string SemanticNote
    {
        get { return "dims the image itself, not just the surround"; }
    }

    /// <summary>
    /// The whole document viewport rather than the canvas, because in Photoshop
    /// the pasteboard around the image is a large part of what is bright, and the
    /// canvas/pasteboard split is not a distinct window the way rulers are.
    /// </summary>
    public override string DefaultRegion { get { return Region.Document; } }
}

// ---------------------------------------------------------------------------
//  A target described entirely on the command line
// ---------------------------------------------------------------------------
/// <summary>
/// Not a product adapter. This exists so the audit harness can aim the shipping
/// engine at a window it owns and drive it deterministically -- the alternative
/// is a test mode inside the engine, which means the thing being tested is not
/// the thing that ships. The frame is treated as its own viewport, so only the
/// client and window regions are meaningful.
/// </summary>
internal sealed class GenericTarget : AdobeTarget
{
    private readonly string _id, _family, _proc, _frameClass;

    public GenericTarget(string id, string family, string proc, string frameClass)
    { _id = id; _family = family; _proc = proc; _frameClass = frameClass; }

    /// <summary>
    /// Parse an --adapter= value: id:Family:process:frameclass[:owl].
    /// With the optional trailing "owl" the result is a real OwlTarget, so the
    /// harness exercises the SAME viewport validation the Adobe products use
    /// rather than a stand-in that always says yes. Returns null if malformed.
    /// </summary>
    public static AdobeTarget Parse(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        string[] p = spec.Split(':');
        if (p.Length < 4) return null;
        for (int i = 0; i < 4; i++) if (p[i].Length == 0) return null;
        if (p.Length >= 5 && p[4].Equals("owl", StringComparison.OrdinalIgnoreCase))
            return new OwlTarget(p[0], p[1], p[2], p[3]);
        return new GenericTarget(p[0], p[1], p[2], p[3]);
    }

    public override string Id { get { return _id; } }
    public override string Family { get { return _family; } }
    public override string[] ProcessNames { get { return new[] { _proc }; } }
    public override string[] FrameClasses { get { return new[] { _frameClass }; } }
    public override string DefaultRegion { get { return Region.Client; } }
    public override string ExpectedStructure
    { get { return "process '" + _proc + "', frame class '" + _frameClass + "'"; } }

    public override List<IntPtr> Viewports(IntPtr frame)
    {
        var l = new List<IntPtr>();
        if (frame != IntPtr.Zero && Native.IsWindow(frame)) l.Add(frame);
        return l;
    }

    public override IntPtr Canvas(IntPtr viewport) { return viewport; }
}
