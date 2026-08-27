// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vixen420
//
// Abode Night View is free software: you may redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. It comes with ABSOLUTELY NO WARRANTY. See the file LICENSE, or
// <https://www.gnu.org/licenses/>, for the full text.

/*
 * ============================================================================
 *  Abode Night View - read-only InDesign state probe   (ExtendScript, .jsx)
 * ----------------------------------------------------------------------------
 *  Run this from InDesign: Window > Utilities > Scripts, or double-click it
 *  in the Scripts panel. It reads properties and reports them. It calls NO
 *  setter, creates NO object, and starts NO undo step.
 *
 *  Protocol
 *    1. Open your document.
 *    2. Run this script.               -> note "modified" and "next undo"
 *    3. Start Abode Night View, change strength repeatedly, switch modes.
 *    4. Run this script again.         -> both values must be IDENTICAL
 *    5. Stop Abode Night View.
 *    6. Run this script again.         -> still identical
 *
 *  "next undo" is the label InDesign would show on Edit > Undo. If Abode Night View
 *  had touched the DOM in any way, that label would change. It does not,
 *  because Abode Night View is a separate process that never scripts InDesign.
 *
 *  Everything below is a getter. The one and only side effect of this script
 *  is a modal alert, and (optionally) a log file written next to the script.
 * ============================================================================
 */

#target indesign

(function () {

    var WRITE_LOG = true;   // set false if you want the alert only

    function safe(fn, fallback) {
        try { var v = fn(); return (v === undefined || v === null) ? fallback : v; }
        catch (e) { return fallback + "  [" + e.message + "]"; }
    }

    var lines = [];
    function add(k, v) { lines.push(pad(k, 22) + ": " + v); }
    function pad(s, n) { while (s.length < n) { s += " "; } return s; }

    add("timestamp", new Date().toString());
    add("app version", safe(function () { return app.version; }, "?"));
    add("open documents", safe(function () { return app.documents.length; }, "?"));

    if (app.documents.length === 0) {
        lines.push("");
        lines.push("No document open - open one and run again.");
    } else {
        var doc = app.activeDocument;
        lines.push("");
        lines.push("--- DOCUMENT ---");
        add("name",            safe(function () { return doc.name; }, "?"));
        add("full path",       safe(function () { return doc.saved ? doc.fullName.fsName : "(never saved)"; }, "?"));
        add("saved",           safe(function () { return String(doc.saved); }, "?"));

        // THE key value: false means the document is clean on disk.
        add("MODIFIED",        safe(function () { return String(doc.modified); }, "?"));

        // THE second key value: the top of the undo stack. If this changes,
        // something wrote to the DOM.
        add("next undo",       safe(function () { return doc.undoName; }, "(none)"));
        add("next redo",       safe(function () { return doc.redoName; }, "(none)"));

        add("pages",           safe(function () { return doc.pages.length; }, "?"));
        add("spreads",         safe(function () { return doc.spreads.length; }, "?"));
        add("swatches",        safe(function () { return doc.swatches.length; }, "?"));
        add("layers",          safe(function () { return doc.layers.length; }, "?"));

        // [Paper] is the swatch a document-mutating fallback would have changed.
        // Recording its colour value here makes any such change impossible to miss.
        add("[Paper] model",   safe(function () { return String(doc.swatches.item("Paper").model); }, "?"));
        add("[Paper] space",   safe(function () { return String(doc.swatches.item("Paper").space); }, "?"));
        add("[Paper] value",   safe(function () { return doc.swatches.item("Paper").colorValue.join(", "); }, "?"));

        lines.push("");
        lines.push("--- VIEW (display-only state, not stored in the INDD) ---");
        add("windows",         safe(function () { return doc.windows.length; }, "?"));
        add("zoom %",          safe(function () { return app.activeWindow.zoomPercentage; }, "n/a"));
        add("screen mode",     safe(function () { return String(app.activeWindow.screenMode); }, "n/a"));
        add("window bounds",   safe(function () { return app.activeWindow.bounds.join(", "); }, "n/a"));

        lines.push("");
        lines.push("--- COLOUR MANAGEMENT / PROOFING (must never change) ---");
        add("cms active",      safe(function () { return String(app.colorSettings.cmsSettingsEnabled); }, "?"));
        add("rgb policy",      safe(function () { return String(app.colorSettings.rgbPolicy); }, "?"));
        add("cmyk policy",     safe(function () { return String(app.colorSettings.cmykPolicy); }, "?"));
        add("doc rgb profile", safe(function () { return String(doc.viewPreferences.parent.name); }, "?"));
        add("proofing on",     safe(function () { return String(app.activeWindow.proofColors); }, "n/a"));
        add("overprint prev",  safe(function () { return String(app.activeWindow.overprintPreview); }, "n/a"));
    }

    var report = "Abode Night View - read-only state probe\n" +
                 "==================================\n" + lines.join("\n");

    if (WRITE_LOG) {
        try {
            var f = new File(File($.fileName).parent.fsName + "/nightview-state-log.txt");
            f.open("a");
            f.writeln("");
            f.writeln("========================================================");
            f.writeln(report);
            f.close();
            report += "\n\n(appended to " + f.fsName + ")";
        } catch (e) { report += "\n\n(could not write log: " + e.message + ")"; }
    }

    alert(report);
})();
