# Internals

How the dimming is produced, and how the windows that produce it are found and
kept in place.

- [How it works](#how-it-works)
- [The limitation this leaves](#the-limitation-this-leaves)
- [Architecture](#architecture)
- [Z-order](#z-order)
- [Input](#input)
- [Tracking](#tracking)

---

## How it works

The overlay is a black window with `WS_EX_LAYERED` and
`SetLayeredWindowAttributes(alpha)`. DWM composites it source-over:

    out = src·α + dst·(1−α)
        = 0·α   + dst·(1−α)
        = dst · k                 where k = 1 − α

A black source turns alpha blending into an exact per-channel multiply, computed
by the compositor, with no screen capture, no duplicated rendering, and no added
latency. 55 % strength is k = 0.45.

**Measured, not assumed.** `Transfer.exe` recovers the per-level curve from a
before/after capture of a six-step wedge:

```
   in    out     in·k    linear-blend
     0    0.0      0.0            0.0
    32   14.0     14.5           19.0
    64   29.0     28.9           42.0
   128   58.0     57.8           88.0
   192   87.0     86.7          133.9
   255  115.0    115.2          179.2

  best-fit k = 0.4517
  mean |error| vs  out = in·k      : 0.22 levels
  mean |error| vs  blend in linear : 26.32 levels
```

DWM composites layered windows in **sRGB-encoded values** — a plain 8-bit
multiply. No gamma correction belongs anywhere in the path. This was measured
twice on unrelated targets: 7,593,903 pixel pairs over InDesign's
GPU-composited canvas (k = 0.4510, 0.21 levels from a pure multiply) and
2,845,152 over a plain GDI window with no Direct3D anywhere (k = 0.4517, 0.22
levels). The two agreeing is what establishes that **the dimming does not depend
on the target's renderer** — it is identical over a window Adobe did not draw.

---

## The limitation this leaves

Alpha blending gives you `out = k·dst + c·α` and nothing else. One shared `k`,
no per-channel gain, no channel mixing, no curve. So white lands at 115 rather
than the 130 a compressed-highlight curve would give, and the midtones sit lower
than ideal:

| in | Neutral gives | a tone curve would give |
|---|---|---|
| 0 | 0 | 0 |
| 32 | 14 | 28 |
| 128 | 58 | 85 |
| 255 | 115 | 130 |

That is a real cost, honestly paid for zero latency. The modes that would have
avoided it, and the measurements that removed them, are in
[Design notes](design-notes.md#rendering-why-there-is-one-filter).

---

## Architecture

```
                 discovery: one EnumWindows pass
                            |
        +-------------------+-------------------+
        |                   |                   |
   InDesignTarget    IllustratorTarget    AcrobatTarget      <- adapters
        |                   |                   |
        +-------------------+-------------------+
                            |
                   validated viewports
                            |
                    overlay controller
                            |
        +-----------+-------+-------+-----------+
     overlay     overlay        overlay      overlay         <- one per viewport
```

**Product-specific knowledge lives only in the adapters.** Each one knows its
process names, its frame window class, and the *structural relationship* its
viewport sits in — and validates that relationship before it will return a
rectangle. The overlay engine below them knows nothing about Adobe.

The four OWL products share one adapter class parameterised by name, because
they measurably share one hierarchy:

```
<frame class>                indesign | illustrator | incopy | Photoshop
  OWL.Dock
    OWL.TabPane
      OWL.TabGroup
        OWL.Document         <- the document viewport
          <inner container>  <- the canvas, strictly inside it
```

| product | OWL.Document | inner container | inner/outer |
|---|---|---|---|
| InDesign | 43,132 2094×1259 | 59,148 2063×1227 | 96 % |
| Illustrator | 42,100 1909×1291 | 42,100 1893×1250 | 94 % |
| Photoshop | 663,1532 1470×939 | 663,1532 1454×923 | 97 % |

The inner container's *class* is not shared — InDesign and Illustrator use
`DroverLord - Window Class`, Photoshop uses `Static` — so the canvas is found by
the geometric relationship (largest visible descendant strictly inside
`OWL.Document`, at least half its area). That was already the verified InDesign
rule; it now lives in one place and every product uses the same code.

Acrobat is a different framework and gets its own adapter:
`AcrobatSDIWindow` → an `AVL_AVView` whose **window text** is `AVPageView`.
Acrobat names its views in the title rather than the class, which is a far
stronger key than a class shared by 23 windows in the same process.

**What this deliberately is not:** a "largest child window" heuristic applied to
every Adobe application. That is the exact mistake the first audit removed — it
picked up transient windows during editing and dragged the overlay onto the wrong
rectangle. Generalising meant *one lifecycle and one overlay engine plus small
validated adapters*, not one clever guess.

---

## Z-order

Each overlay is made an **owned window** of its application frame
(`SetWindowLongPtr(GWLP_HWNDPARENT)`). Windows then keeps an owned window above
its owner and raises the pair together, so activating the application cannot
produce even the one-frame gap that chasing the z-order leaves behind. Ownership
is not parenting: it does not attach input queues.

The link is also **given back**. It is the only state this program leaves inside
another process's window tree, so it is dropped the moment an overlay stops being
attached — when the document goes away, and before the window is destroyed at
exit. Two reasons, neither cosmetic: Windows destroys a window's owned windows
along with the owner, so a pooled spare left owned is a window volunteered for
destruction the next time the application quits; and the check that decides
whether to re-own asks the window (`GetWindow(GW_OWNER)`) rather than a cached
handle, because window handles are recycled and a stale one can match a
brand-new frame. `Audit.exe` asserts both halves — that detaching returns the
owner to `NULL`, and that reacquiring takes it back rather than silently
degrading to `zmode=above`.

Ownership is necessary but **not sufficient**, and the invariant is re-checked
every sync. Three separate faults are corrected:

1. **Not above the owner at all.** "An owned window is above its owner" is
   enforced when the owner is *activated*, not when it is re-ordered some other
   way. Showing another owned window raises the owner without taking the overlay
   with it, and the dimming then silently stops. Regression-tested: reproduced
   deterministically, recovery measured at 59–185 ms single-target and 135–250 ms
   with several targets tracked, over four consecutive runs.
2. **A foreign window sandwiched between overlay and owner.** With two maximized
   Adobe applications on one monitor, InDesign's overlay can be left above
   Illustrator's frame — legitimately above *its own* owner the whole time — and
   Illustrator's canvas is then dimmed twice. Measured before the fix:
   Illustrator's canvas came out at k = 0.176 against a requested 0.451, with
   every structural check passing.
3. **Just activated.** Activating an application raises its frame with every
   window it owns, and Windows puts ours at the *top* of that group — above the
   application's own floating panels. Re-seated once per activation.

What is deliberately **not** checked is "is the overlay adjacent to its owner",
continuously. That was tried and measured as the flicker cause: the owner's own
transient windows appear between the two many times a second while you edit, and
every correction is a visible flash. Windows belonging to the owner are therefore
ignored; only a foreign window, or an activation, is a fault.

---

## Input

`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `WS_EX_APPWINDOW`
cleared, `WM_NCHITTEST` → `HTTRANSPARENT`, `WM_MOUSEACTIVATE` → `MA_NOACTIVATE`,
`ShowWithoutActivation`. Measured: `WindowFromPoint` never returns the overlay at
any of 81 probe points; the overlay is never the foreground window; it never
appears in Alt+Tab or the taskbar.

---

## Tracking

One global WinEvent hook (foreground, move/size, minimize) plus **one per tracked
process**, added and removed as applications start and quit. A process-scoped
hook dies with the process it was created for, so it is re-armed — without that,
quitting and restarting an Adobe application silently dropped tracking back to
the 250 ms safety-net timer.

`WINEVENT_OUTOFCONTEXT` with a null module handle: **no DLL enters any Adobe
process.**
