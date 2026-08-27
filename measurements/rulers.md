# Rulers: are they inside the region we dim?

Reported: *"the side ruler is dimmed along with the document viewport."*

The answer turned out to be product-specific, and the geometry decides it without
anybody having to choose a policy. This is the measurement.

Machine: Windows 11 Pro 26200, 96 DPI, 100 % scaling.
Products: InDesign 21.5.1, Illustrator 30.7.0, Photoshop 27.9, InCopy 21.5.1,
Acrobat 26.1.21771.0.

---

## 1. Are the rulers separate windows?

No. Not in any of them. `ProtoA.exe` dumps the full hierarchy; under InDesign's
canvas container there are exactly seven child windows and every one of them is a
hidden text-entry overlay (`OS_EditTextContainer` + `Edit`) for the page-number
and measurement fields. Under Illustrator's there is one hidden 27 px bar.
Nothing is positioned or sized like a ruler, and nothing is visible.

So the rulers are painted by the parent, into whatever part of it no child
window covers. There is no HWND to query and no window text to match.

## 2. Which means the only thing that matters is where the canvas child sits

    InDesign        OWL.Document   43,132  2094x1259
                    canvas child   59,148  2063x1227
                    insets         L 16   T 16   R 15   B 16

    Illustrator     OWL.Document   42,100  1909x1291
                    canvas child   42,100  1893x1250
                    insets         L  0   T  0   R 16   B 41

    Photoshop       OWL.Document  663,1532 1470x939
                    canvas child  663,1532 1454x923
                    insets         L  0   T  0   R 16   B 16

    InCopy          OWL.Document 1111,398  1253x702
                    canvas child   -- none --

    Acrobat         AVPageView    907,235   711x949   (plus a 17 px scrollbar)

InDesign's canvas child is inset by 16 px on the left and top — which is exactly
where the rulers are — and by 15/16 px on the right and bottom, which is exactly
where the scrollbars are. Everything else starts at the document origin.

## 3. Measured, not inferred

Screen captures of the top-left 260x180 of each document container, with the
overlay off and on, mean luminance per region.

**InDesign**, strength 35 (k = 0.65):

| region | off | on | ratio |
|---|---|---|---|
| horizontal ruler | 52.1 | 52.1 | **1.0000** |
| vertical ruler | 52.9 | 52.9 | **1.0000** |
| ruler corner box | 39.5 | 39.5 | **1.0000** |
| canvas just inside | 186.0 | 121.0 | 0.6505 |

**Illustrator**, strength 35 (k = 0.65):

| region | off | on | ratio |
|---|---|---|---|
| horizontal ruler | 59.6 | 39.1 | **0.6562** |
| vertical ruler | 57.3 | 37.6 | **0.6567** |
| ruler corner box | 53.1 | 34.9 | **0.6567** |
| canvas just inside | 96.0 | 62.0 | 0.6458 |

InDesign's rulers do not move at all. Illustrator's are dimmed by exactly the
same factor as the canvas. The report was about Illustrator.

## 4. Ruler show/hide

InDesign's canvas child is not merely inset; it is inset *because the rulers are
there*. View → Show/Hide Rulers (Ctrl+R), with the keystroke gated on a direct
`GetForegroundWindow` comparison so it could only ever reach InDesign:

    rulers SHOWN     canvas   59,148  2063x1227
    rulers HIDDEN    canvas   43,132  2079x1243     <- same HWND, grown into the strip
    rulers SHOWN     canvas   59,148  2063x1227

Same window handle throughout. So the exclusion is not a rule anybody wrote: it
follows the user's own ruler setting for free, and Abode NV picks the change up
on the next tick like any other geometry change.

Ctrl+R is an application view setting. It does not alter document content and it
does not mark a document modified.

## 5. Why Illustrator and Photoshop are not fixed

The only candidate was a fixed inset — start the overlay 16 px right of and below
the canvas origin. It was rejected:

* Adobe scales the ruler with its own **UI Scaling** preference (Preferences →
  User Interface), which is independent of the Windows DPI. A constant that is
  right here is wrong for anyone who has moved that slider.
* Deriving the ruler thickness from the scrollbar thickness — they are both about
  16 px at 100 % — is a coincidence dressed up as geometry. Adobe's OWL theme
  specifies them separately and nothing guarantees they stay equal.
* Being wrong in either direction is visible: too small leaves an undimmed band
  inside the canvas, too large puts a dimmed band across the artwork.

Everything else on the list was already ruled out by the project's own
constraints: pixel-color detection, screenshot analysis, and capture-based
masking all mean reading rendered content, which is the thing Neutral exists to
avoid.

Left as a documented cosmetic limitation.

## 6. The regression check this produced

`--verify` now computes the strips of the document container that the canvas
rectangle excludes, photographs each one with the overlay off and on, and fails
if any of them has been multiplied by k.

    InDesign      4 strips   left, top (rulers), right, bottom (scrollbars)
    Illustrator   2 strips   right, bottom
    Acrobat       1 strip    right
    Photoshop     none       the canvas fills the container on every side
    InCopy        none       there is no inner container at all

The threshold is the midpoint between 1.0 and k, not a tight band around 1.0.
These strips are thin and high-contrast and their content genuinely moves between
two captures — a scrollbar thumb slides, a ruler's cursor indicator follows the
mouse, tick labels change with the scroll position. The first version of the
check used a 3 % band and failed on a run where nothing was wrong, reporting
ratios of 1.158, 0.937, 1.088, and 0.955: jitter, in both directions, not dimming.
What must never happen is a strip reading 0.45, and that is unmistakable.
