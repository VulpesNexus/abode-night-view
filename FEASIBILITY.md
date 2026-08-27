# Abode Night View — technical feasibility report

**Windows 11 Pro 26200, measured on the target machine.**

Phase 1 established the concept against InDesign. Phase 2 (section 12) tried to
break it. Phase 3 (section 13) generalised it to five Adobe products and renamed
the product **Abode Night View**. Phase 4 (section 14) is the release pass for
1.2.0: what was removed, what the rulers turned out to be, and what shipped.

Sections 0–12 are phases 1 and 2 and are left as written, which is why they call
the product "Night View" — that was its name at the time, and rewriting an
engineering log to match a later decision makes it useless as a record. Where a
name, a number, or a conclusion has changed since, the current one is in section
13 or 14.

---

## 0. Executive summary

Seven things are now settled, most of them by measurement rather than by reading documentation.

1. **The InDesign document canvas is a real, distinct, stable child HWND.** It is
   `OWL.Document`, there is exactly one of it, and it nests one further
   `DroverLord - Window Class` container that excludes the rulers and scrollbars.
   No pixel coordinates need to be hard-coded and no InDesign plugin is needed.

2. **`MAGCOLOREFFECT` cannot do a non-linear curve.** This is not a suspicion —
   Microsoft's own GDI+ page defines the 5×5 color matrix as *"a linear
   transformation followed by a translation … called an **affine**
   transformation."* Affine means `out = a·in + b` and nothing else.

3. **A plain click-through layered window already computes the exact transform
   you asked to prototype first.** DWM's alpha compositing of a black window is
   `out = dst·(1−α)`, which *is* `R' = R·k, G' = G·k, B' = B·k` with `k = 1−α`.
   It does it in the compositor, per pixel, with no screen capture, no
   duplicated rendering, and no added frame of latency. For **Neutral Dim, the
   Magnification API buys literally nothing over the trivial overlay** — it
   produces the same pixels, more expensively.

4. **Your target tone curve is a Reinhard shoulder, and I solved it.** The six
   anchor points you wrote out are fitted to within 0.6/255 by
   `y = 1.093·x/(x + 1.144)`. That is a two-line pixel shader. See §5.

5. **Point 3 is now confirmed on the screen, not just on paper.** The measured
   transfer function over 7.6 million pixel pairs is `out = in × 0.4510` against
   a requested `k` of 0.4500, with a mean error of **0.21 levels** — and 31.28
   levels against a linear-light model, so **DWM composites layered windows in
   sRGB-encoded values**. The canvas dims by exactly `k`; a region a few pixels
   outside it is unchanged to two decimal places. See §3.2 and §3.3.

6. **An overlay must not chase the z-order.** Clicking inside an
   already-foreground InDesign raises it above the overlay while firing no
   hookable event, so recovery waits on a timer — measured at **187–218 ms of
   full-brightness white**. Making the overlay an *owned* window of the InDesign
   frame makes "above the owner" an invariant Windows maintains: measured
   16 transitions → **0**. See §3.4.

7. **Ownership is necessary but not sufficient, and the gap was silent.** "An
   owned window is above its owner" is enforced when the *owner is activated* —
   not when it is re-ordered some other way. Showing another owned window raises
   the owner and leaves the overlay behind it, and the original code had hard-coded
   "ownership handles the z-order", so there was no path back: the dimming just
   stopped. Found by the audit in §12, fixed by keeping ownership *and* checking
   the invariant, repairing to **directly above the frame** rather than to the
   top. Recovery measured at 189 ms; previously it never recovered. See §12.2.

**Recommendation: ship the layered tint overlay as the engine.** Keep
the Magnification API as an *optional* second engine, used only for the modes
alpha blending genuinely cannot express (true Warm Dim, Grayscale, Invert).
Defer the shader path until linear dimming has been used for a week and found
wanting — and if it is needed, use **Desktop Duplication, not Windows Graphics
Capture** (§5.3 explains why WGC is disqualified for an unpackaged app).

Not one of these approaches writes to the InDesign DOM. The `[Paper]` fallback
is not implemented and is not needed.

---

## 1. Comparison table

| Approach | Document writes | True dimming | Non-linear curve | Added latency | Complexity | Main risks |
|---|---|---|---|---|---|---|
| **Transparent black overlay** (Proto B) | **None** | **Yes — exactly `out = dst·k`** | No | **Zero** (same compositor frame) | **Low** (~450 LOC) | Geometry tracking lags a window drag by a frame or two; no per-channel gain; overlay must be z-ordered, not topmost |
| **Magnification API + color matrix** (Proto C) | **None** | Yes | **No — affine only, confirmed** | ≥1 frame; content is re-composited | Medium | Recursion when the host covers its own source; requires a ~16 ms polling timer forever; x64 only (WOW64 unsupported); possible resampling softness at 1.0× |
| **Desktop Duplication + pixel shader** | **None** | Yes | **Yes** | 1–2 frames | High | Ghosting/tearing during fast scroll; must handle `DXGI_ERROR_ACCESS_LOST`; one duplication per output per process; ~1 GPU pass per frame |
| **Windows Graphics Capture + shader** | **None** | Yes | Yes | 1–2 frames | High | **Yellow capture border unless the app is MSIX-packaged and the user consents** — see §5.3. Disqualifying as-is |
| **Composition backdrop brush** | None | — | — | — | — | **Dead end.** `CreateHostBackdropBrush` returns black in a Win32 `DesktopWindowTarget`, is blurred by design, and *"the app cannot read the pixel data back"* |
| **Per-output gamma LUT** (f.lux-style) | **None** | Yes | **Yes, arbitrary 256-entry LUT** | **Zero** | **Very low** | **Applies to the whole monitor**, InDesign's own panels included. Excellent *if* InDesign owns one display — see §5.4 |
| **InDesign UXP plugin overlay** | None | No | No | — | — | **Not possible.** UXP has no API to draw over the native canvas and no viewport→screen mapping. Adds nothing Win32 does not already give us |

---

## 2. Viewport detection — solved, with measurements

Prototype A was run against the live InDesign 2026 process on this machine
(`C:\Program Files\Adobe\Adobe InDesign 2026\InDesign.exe`, one document open,
maximized on the 2560×1440 primary at 96 DPI).

### Window hierarchy

InDesign's frame window has class **`indesign`**. Below it sit **486 descendant
windows** built on Adobe's OWL/DroverLord widget toolkit:

```
  285  DroverLord - Window Class      (generic view container)
  125  Edit                           (real Win32 edit controls in panels)
   26  OWL.Palette
   19  OWL.ResizeGripper
   11  OWL.TabGroup
    5  OWL.Dock
    3  OWL.TabPane
    1  OWL.Document          <-- exactly one, and it is the canvas
    1  OWL.MenuBar / OWL.Toolbar / OWL.ControlBar / OWL.ApplicationBar / ...
```

The document is itself a dockable tab group — which is precisely why InDesign's
tabbed-document UI works the way it does:

```
indesign                      -8,-8   2576x1408   (maximized; client = 0,0 2560x1392)
└─ OWL.Dock                   42,102  2096x1290
   └─ OWL.TabPane             42,102  2096x1290
      └─ OWL.TabGroup         42,102  2096x1290
         └─ OWL.Document      43,132  2094x1259   <-- canvas + rulers + scrollbars
            └─ DroverLord     43,132  2094x1259   <-- hit-test surface
               └─ DroverLord  59,148  2063x1227   <-- canvas proper, no rulers
```

The inner container is inset by exactly **16 px left, 16 px top** (the rulers)
and **15 px right, 16 px bottom** (the scrollbars).

### Two usable targets

| Target | Rect measured | Covers |
|---|---|---|
| `--target=owldoc` | `43,132 2094×1259` | canvas + rulers + scrollbars |
| `--target=canvas` | `59,148 2063×1227` | pages + pasteboard only |

Both are resolved **by class name at runtime**, never by hard-coded coordinates.
Prototype B's `ViewportLocator` finds `OWL.Document` by class, then for
`canvas` picks the largest visible descendant strictly inside it. When Prototype
B was launched with `--target=canvas`, its overlay window materialised at
**`59,148 2063×1227`** — an exact match. Detection works.

### Hit-test confirmation

A 60×24 `WindowFromPoint` grid over the client area returns the canvas container
`0x15A1740` for 501 of the probed points, with a contiguous hit bounding box of
`64,145 … 2112,1363`. The docks report separately (`OWL.Dock` left at
`0,102 42×1290`, right at `2138,102 422×1290`), and the app bar at `0,0 2560×40`.
The canvas is cleanly separable from the chrome.

### Still to check (Prototype A `watch` mode does this)

Multiple documents as tabs, floating document windows, panels collapsed,
Presentation mode, and a second monitor. `ProtoA.exe watch` prints one line
every time the canvas rect or handle changes, so a two-minute session covering
all of those produces the whole answer.

---

## 3. Verified behavior of Prototype B

Measured with `Verify.exe` against the live InDesign. Read §3.1 before trusting
this table — it passed in full while the overlay was rendering nothing at all:

| Check | Result |
|---|---|
| Overlay window created | `NightView Overlay`, rect `59,148 2063×1227` |
| Extended styles | `0x080900A0` = `LAYERED \| TRANSPARENT \| TOOLWINDOW \| NOACTIVATE` |
| `WS_EX_APPWINDOW` clear | yes — never in Alt+Tab, never on the taskbar |
| **`WindowFromPoint` at the overlay center** | returns `0xF114C8 DroverLord - Window Class`, **InDesign's canvas — not the overlay** |
| Owning process of that window | InDesign's PID |

That last row is the click-through test, and it passes. The system's own hit
testing routes the point straight past the overlay into InDesign, which is
exactly what `WS_EX_TRANSPARENT` is documented to do:

> *"if the layered window has the **WS_EX_TRANSPARENT** extended window style,
> the shape of the layered window will be ignored and the mouse events will be
> passed to other windows underneath the layered window."*
> — [Window Features → Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)

### 3.1 The invisible-overlay bug, and what it says about verification

Every check in the table above passed while **the overlay was drawing nothing at
all.** On screen there was no change whatsoever.

The cause was one line in the form's constructor:

```csharp
SetStyle(ControlStyles.Opaque, true);
```

`ControlStyles.Opaque` suppresses the background erase *without supplying a
replacement*. With no `OnPaint` override, the window painted zero pixels, its
redirection surface stayed empty, and an empty surface blended through
`LWA_ALPHA` is exactly invisible. Correct handle, correct rectangle, correct
extended styles, correct alpha, correct hit-testing — and no photons.

The fix is `UserPaint | AllPaintingInWmPaint | Opaque` plus an `OnPaint` that
fills the client rectangle.

The lesson is about the test suite, not the bug. Every check in §3 is
*structural* — it interrogates window metadata. Not one of them can see whether
anything was rendered, so the entire suite passed a completely non-functional
build. The one check that would have caught it, the luminance ratio, was the one
skipped because the displays were asleep. **A rendering feature needs at least
one test that reads pixels**, and if that test cannot run, the result is
"unverified", not "passed".

`Verify.exe` now includes it, and refuses to fake it: if the screen changed
between the two captures it reports a stale baseline rather than a ratio.

### 3.2 Measured results

With the monitors awake, `Verify.exe` against the live InDesign, strength 55:

| Check | Result |
|---|---|
| Canvas rect | `59,148 2063×1227` |
| Overlay rect | identical, to the pixel |
| Extended styles | `0x080900A0`, `WS_EX_APPWINDOW` clear |
| Layered alpha | 140 (55 %), `LWA_ALPHA` |
| Above the InDesign frame, not topmost | yes |
| `WindowFromPoint` at the center | InDesign's canvas, not the overlay |
| InDesign popups above the overlay | 2 of 2 — menus and panels stay undimmed |
| **Canvas luminance** | **162.81 → 73.26, ratio 0.450** (predicted `k` = 0.451) |
| **Chrome outside the canvas** | **62.75 → 62.75, ratio 1.000** — untouched |

The chrome row is the containment proof: a region a few pixels outside the
canvas is unchanged to two decimal places, so the dimming stops exactly at the
canvas boundary.

### 3.3 The transfer function, measured

`Transfer.exe` recovers the real per-level mapping from 7.6 million pixel pairs
rather than inferring it from averages:

```
   in    out     in*k    linear-blend    target
     0    -        0.0            0.0          0
    32   14.0     14.4           19.0         28
    64   29.0     28.9           42.0         50
   128   58.0     57.7           87.9         85
   192   87.0     86.6          133.8        110
   255  115.0    115.0          179.0        130

  best-fit k = 0.4510   (requested 0.4500)
  mean |error| vs  out = in*k      :  0.21 levels
  mean |error| vs  blend in linear : 31.28 levels
```

Two results follow.

**DWM composites a layered window in sRGB-encoded values — a plain 8-bit
multiply.** The 150× difference between the two error columns leaves no room for
doubt. So `k = 1 − α` maps directly onto the strength slider with no gamma
correction, and the linear-light concern raised for HDR displays does not apply
to this SDR setup. It is worth re-measuring if Advanced Color is ever enabled on
the InDesign monitor.

**The affine limitation of §4 is now measured, not argued.** Compare the `out`
and `target` columns: the multiply lands whites at 115 against a target of 130,
and midtones at 58 against 85. Raising `k` until whites reach 130 lifts midtones
only to about 65. The gap in the middle of the range is the part no single
multiply can close.

### 3.4 Flicker — why chasing the z-order cannot work

In use, the overlay flashed on almost every click that moved a frame or edited
text. `Verify.exe --watch` samples the overlay every 8 ms and logs every change
of visibility, rectangle, and z-position, which turned four guesses into four
measurements:

| Cause | Signature in the log |
|---|---|
| Hidden for the duration of a window drag | `HIDDEN` … `SHOWN` |
| Hidden on one failed canvas lookup during a tab switch | `HIDDEN` … `SHOWN` |
| Retargeted onto a transient child window | `MOVED->…` |
| Raised above by InDesign, recovered late | `FELL-BEHIND` … `RAISED` |

The third is the interesting one. The canvas was found by taking the largest
window strictly inside `OWL.Document`, and the cached handle was discarded on
*every* show/hide event anywhere in InDesign. Dragging a frame or editing text
spawns transient children continuously, so the cache was thrown away many times
a second — and while a drag is in progress, one of those transients is large
enough to win "largest window inside the document view". The overlay retargeted
onto it and jumped. Fixed by invalidating only when the tracked window itself
disappears, and by requiring a candidate to be at least half the area of the
document view.

The fourth is structural. Clicking inside an **already-foreground** InDesign
raises it above the overlay without firing `EVENT_SYSTEM_FOREGROUND` or anything
else hookable, so recovery falls to the 250 ms safety-net timer. Measured gaps
were 15–16 ms when a hook caught it and **187–218 ms when only the timer did**.
Two hundred milliseconds of full-brightness white at night is precisely the
complaint.

No amount of tuning fixes this, because the notification does not exist. The
answer is to stop chasing: set the InDesign frame as the overlay's **owner**
(`GWLP_HWNDPARENT`). Windows then maintains "above the owner" as an invariant,
with no polling and no gap. Ownership is not parenting — it sets the owner of a
top-level window and does not attach input queues, so it costs nothing in input
behavior.

It is also more correct than chasing. Owned windows are ordered among themselves
by recency, so any popup InDesign raises later automatically lands above the
overlay — which is exactly what keeps menus and floating panels undimmed. A run
that happened to catch two open popups confirmed both were above the overlay.

Measured over 25 s of ordinary editing:

```
chasing the z-order (--zmode=above):  16 transitions, 2 of them ~200 ms
owned window        (--zmode=owned):   0 transitions
```

`owned` is now the default, with automatic fallback to `above` if the ownership
call is refused.

---

## 4. The color maths

### 4.1 What alpha blending gives you

DWM composites a layered window with `SetLayeredWindowAttributes(…, LWA_ALPHA)`
as ordinary source-over:

```
out = src·α + dst·(1−α)
```

With `src = black`, the first term vanishes:

```
out = dst · (1−α)          →   k = 1 − α
```

| Strength | α | k | white 255 → | mid 128 → | black 0 → |
|---|---|---|---|---|---|
| 0 % | 0.00 | 1.00 | 255 | 128 | 0 |
| 20 % | 0.20 | 0.80 | 204 | 102 | 0 |
| 40 % | 0.40 | 0.60 | 153 | 77 | 0 |
| **55 %** | **0.55** | **0.45** | **115** | **58** | **0** |
| 75 % | 0.75 | 0.25 | 64 | 32 | 0 |

This is your requested first transform, exactly, with black staying black.

**The one thing it cannot do is per-channel gain.** With a non-black tint color
`c` the result is `out = dst·(1−α) + c·α` — an affine map with a *positive*
offset. You can *add* to a channel, never *subtract* from it. So "Warm Dim" via
a tint overlay is uniform dimming plus a warm **lift** (blacks glow faintly
warm), not a blue **cut**. At `c = (60,24,0)`, `α = 0.5`: white → `(158,139,128)`,
black → `(30,12,0)`. Perfectly usable, but it is not the same operation as
reducing blue. Prototype C does it properly.

### 4.2 The exact `MAGCOLOREFFECT` matrices

Confirmed convention, from [MAGCOLOREFFECT](https://learn.microsoft.com/en-us/windows/win32/api/magnification/ns-magnification-magcoloreffect)
which delegates to [Using a Color Matrix to Transform a Single Color](https://learn.microsoft.com/en-us/windows/win32/gdiplus/-gdiplus-using-a-color-matrix-to-transform-a-single-color-use):
the color is the **row vector `[R G B A 1]` multiplied on the left**, element
`M[i][j]` is "how much of input channel *i* goes into output channel *j*", **row
4 holds the additive offsets**, and column 4 must be `(0,0,0,0,1)`. Microsoft's
own grayscale sample confirms the orientation.

**Neutral Dim** — the alpha row is left as identity, so alpha stays correct:

```
 k    0    0    0    0
 0    k    0    0    0
 0    0    k    0    0
 0    0    0    1    0
 0    0    0    0    1
```

**Warm Dim** — the real thing, per-channel gain (`w` = warmth 0…1):

```
 k                 0                  0                 0    0
 0    k·(1 − 0.10w)                    0                 0    0
 0                 0    k·(1 − 0.45w)                    0    0
 0                 0                  0                 1    0
 0                 0                  0                 0    1
```

**Grayscale Dim** — Rec.709 luma into all three outputs:

```
 0.2126k  0.2126k  0.2126k   0   0
 0.7152k  0.7152k  0.7152k   0   0
 0.0722k  0.0722k  0.0722k   0   0
 0        0        0         1   0
 0        0        0         0   1
```

**Invert** (`out = k·(1−in)`) — note the offsets in row 4:

```
-k    0    0    0    0
 0   -k    0    0    0
 0    0   -k    0    0
 0    0    0    1    0
 k    k    k    0    1
```

All four are implemented in `Mag.MAGCOLOREFFECT` in `ProtoC_MagnifierOverlay.cs`
and printed to the console at startup so you can read back what was applied.

### 4.3 Why the matrix cannot do highlight compression — proof

Affine means `out = a·in + b`. Your target table:

| in | wanted | affine with `b = 0`<br>(black stays black) | affine fitted to the midtones<br>`a = 0.354, b = 40` |
|---:|---:|---:|---:|
| 0 | 0 | 0 ✅ | **40 ❌** |
| 32 | 28 | 16 ❌ | 51 ❌ |
| 64 | 50 | 33 ❌ | 63 ❌ |
| 128 | 85 | 65 ❌ | 85 ✅ |
| 192 | 110 | 98 ❌ | 108 ✅ |
| 255 | 130 | 130 ✅ | 130 ✅ |

Anchoring black at black forces `b = 0`, and then white forces `a = 130/255 =
0.51`, which undershoots every midtone by 12–20 levels. Matching the midtones
instead lifts black to 40/255 — exactly the failure mode you ruled out ("black
text remains dark and legible").

**A 5×5 color matrix cannot express your curve. Stated clearly, as requested.**

---

## 5. Non-linear options

### 5.1 Your curve, solved

Your six anchors are not arbitrary — they are a Reinhard shoulder:

```
y = c · x / (x + a)        with  a = 1.144,  c = 1.093     (x, y in 0…1)
```

| in | you wanted | this curve | error |
|---:|---:|---:|---:|
| 0 | 0 | 0.0 | 0.0 |
| 32 | 28 | 27.6 | −0.4 |
| 64 | 50 | 50.1 | +0.1 |
| 128 | 85 | 85.0 | −0.0 |
| 192 | 110 | 110.6 | +0.6 |
| 255 | 130 | 130.0 | 0.0 |

Maximum error 0.6 out of 255. Two knobs drive it — target white `w` and target
midtone `v` (both 0…1, `m = 0.5`):

```
a = m(w − v) / (v − m·w)
c = w(1 + a)
```

| Preset | white → | mid → | a | c |
|---|---:|---:|---:|---:|
| gentle | 166 | 107 | 1.211 | 1.437 |
| **default (yours)** | **130** | **85** | **1.145** | **1.094** |
| deep | 102 | 66 | 1.167 | 0.867 |

Shader, luminance-preserving so hues do not shift:

```hlsl
float3 NightView(float3 rgb, float a, float c)
{
    float L  = dot(rgb, float3(0.2126, 0.7152, 0.0722));
    float L2 = c * L / (L + a);
    return rgb * (L2 / max(L, 1e-5));
}
```

Keep the whole thing in sRGB-encoded values — that is the space your table is
written in, and the space DWM hands you.

### 5.2 Desktop Duplication + shader — the recommended heavy path

```
IDXGIOutputDuplication (whole monitor)
    → D3D11 texture
    → crop to the canvas rect
    → the shader above
    → swapchain on a click-through layered overlay
```

Recursion is handled by
`SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE)`, documented as
*"The window is displayed only on a monitor. Everywhere else, the window does
not appear at all."* (Windows 10 2004+; this machine is 26200, so it is
available.) The overlay excludes itself from the duplication, so it never
photographs its own output.

Costs and risks: 1–2 frames of latency on canvas content, visible as smear
during fast scroll; must re-acquire on `DXGI_ERROR_ACCESS_LOST` (mode change,
resolution change, UAC secure desktop, GPU reset); one duplication per output
per process; unavailable in some remote-session configurations.

### 5.3 Windows Graphics Capture — disqualified, and here is why

WGC would be the obvious modern choice, and it has a specific, documented
blocker. To suppress the yellow "this window is being captured" border you must
set `GraphicsCaptureSession.IsBorderRequired = false`, and
[the documentation](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired) says:

> *"your app must get consent from the user by calling
> `GraphicsCaptureAccess.RequestAccessAsync`, passing in the value
> `GraphicsCaptureAccessKind.Borderless` … To call `RequestAccessAsync` with
> `GraphicsCaptureAccessKind.Borderless`, you must declare the
> **`graphicsCaptureWithoutBorder`** capability in your **app's package
> manifest**."*

A plain unpackaged `NightView.exe` has no package manifest. Without it, enabling
Night View draws a permanent yellow rectangle around InDesign — unacceptable for
a comfort tool. Shipping an MSIX/sparse package plus a consent prompt to get
*less* capability than Desktop Duplication already offers is the wrong trade.

(`IsCursorCaptureEnabled = false`, Windows 10 2004+, would have handled the
doubled-cursor problem cleanly. That part was fine.)

### 5.4 The option worth taking seriously first: a per-output gamma LUT

You have three displays: `\\.\DISPLAY1` 2560×1440 (primary, where InDesign is
maximized), `\\.\DISPLAY2` 1440×2560 portrait, `\\.\DISPLAY3` 1920×1080.

If InDesign lives on one display, `SetDeviceGammaRamp` /
`IDXGIOutput::SetGammaControl` gives you an **arbitrary per-channel 256-entry
LUT** on that output — your exact shoulder curve, in hardware, at zero runtime
cost, zero latency, zero capture, and zero recursion. This is how f.lux and
Night Light work.

The trade is that it applies to the **entire monitor**: InDesign's own panels
dim too, as does anything else you put on that screen. Whether that is a bug or
a feature at 2 AM is your call — but it is by far the cheapest route to true
non-linear tone mapping and it deserves a try before anyone writes a shader.

Caveat to verify on your machine: Windows clamps the range
`SetDeviceGammaRamp` will accept unless
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\GdiIcmGammaRange` is
raised to `256`. This is the well-known f.lux/Redshift workaround rather than a
documented API contract, so treat it as "test it" rather than "it will work".

### 5.5 Composition backdrop brush — dead end, for the record

`Compositor.CreateHostBackdropBrush` looks like it should be perfect: sample
what is behind the window, run an effect graph over it, no capture at all. It is
not usable:

- Microsoft's docs state *"The app cannot read the pixel data back"* and that
  its transparency is *"a property the user can control from Settings or by
  using power policies"*.
- It is blurred by design, for security reasons — you cannot get crisp text.
- In a Win32 `DesktopWindowTarget` it renders black. This is reported against
  Microsoft's own `Windows.UI.Composition-Win32-Samples` and
  `WindowsCompositionSamples` repositories.

`CreateBackdropBrush` (the non-host variant) only samples within your own
window's visual tree, which for a transparent overlay contains nothing.

---

## 6. Magnification API — what the documentation actually says

Everything below is quoted or paraphrased from current Microsoft Learn pages,
not from memory.

| Claim | Verdict | Source |
|---|---|---|
| `MAGCOLOREFFECT` is `float transform[5][5]` with GDI+ semantics | Confirmed | [MAGCOLOREFFECT](https://learn.microsoft.com/en-us/windows/win32/api/magnification/ns-magnification-magcoloreffect) |
| The matrix can only express affine transforms | **Confirmed** | [GDI+ color matrix](https://learn.microsoft.com/en-us/windows/win32/gdiplus/-gdiplus-using-a-color-matrix-to-transform-a-single-color-use) — *"a linear transformation followed by a translation is called an affine transformation"* |
| The host window must be `WS_EX_LAYERED` | Confirmed | [Magnification API Overview](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro) — *"The magnifier control must be hosted in a window created with the WS_EX_LAYERED extended style"* |
| The host should be set fully opaque | Confirmed | same — `SetLayeredWindowAttributes(hwndHost, 0, 255, LWA_ALPHA)` *"to prevent the underlying screen content from showing through"* |
| `WS_EX_TRANSPARENT` on the host passes clicks through | **Confirmed, explicitly for magnifier hosts** | same — *"mouse clicks are passed to whatever object is behind the host window"* |
| The magnifier window excludes itself from capture | Confirmed | [MagSetWindowFilterList](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist) — *"The magnification window itself is automatically excluded"* |
| …but the **host** is a *different* HWND | **Yes — this is the recursion risk** | inference; Prototype C tests it both ways |
| `MW_FILTERMODE_INCLUDE` is usable | **No** | same page — *"This value is not supported on Windows 7 or newer"* |
| The control refreshes itself | **No — you must drive it** | [Overview](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro) — the sample calls `MagSetWindowSource` + `InvalidateRect` *"at intervals"*, ~16 ms |
| Works in a 32-bit process on 64-bit Windows | **No** | [Magnification API](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/entry-magapi-sdk) — *"not supported under WOW64"* |
| Needs a WDDM video card | Confirmed | [MagSetColorEffect](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetcoloreffect) |
| Magnified cursor can be suppressed | Confirmed | omit `MS_SHOWMAGNIFIEDCURSOR`; the real system cursor is composited above everything anyway |
| `MagSetInputTransform` needs UIAccess | Confirmed | [Overview](https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-intro). **We never need it** — at 1.0× with the destination on top of the source, screen coordinates are already identity |

That last row matters: because we magnify by exactly 1.0 and place the output
precisely over the input, there is no coordinate mapping to fix, so the one
Magnification API feature that would have required elevated privileges is
irrelevant to us.

---

## 7. Windows pitfalls found (several the hard way)

**Geometry**

1. A maximized InDesign has window rect `-8,-8 2576×1408` but client rect
   `0,0 2560×1392`. Always use `GetClientRect` + `ClientToScreen`, and clip the
   overlay to the client rect so a stale canvas rect can never spill onto the
   desktop. Prototype B does both.
2. Call `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` **before any HWND
   exists**, or every rect you read is DPI-virtualised. All three prototypes do
   this as the first statement in `Main`.

**Enumeration**

3. `EnumChildWindows` returns *all descendants*, not direct children. Rebuild
   the tree from `GetParent` (Prototype A does) or the depth is meaningless.
4. 486 child windows: do **not** re-enumerate at frame rate. Cache the canvas
   handle, validate it with `IsWindow`/`IsWindowVisible`/class check, and
   invalidate only on `EVENT_OBJECT_SHOW/HIDE/DESTROY`.

**Tracking**

5. `EVENT_OBJECT_LOCATIONCHANGE` is extremely chatty — the text caret alone
   fires it constantly. Filter on `idObject == OBJID_WINDOW`.
6. Install hooks `WINEVENT_OUTOFCONTEXT` so no DLL is injected into InDesign.
   This is also what makes the safety argument in §8 airtight.
7. Keep the `WinEventProc` delegate in a field. If it is collected, the hook
   faults the process.

**Z-order — the one that matters most**

8. Do **not** make the overlay topmost. Place it directly above the InDesign
   top-level window with `SetWindowPos(overlay, hwndInDesign, …, SWP_NOACTIVATE)`.
   Then: InDesign's own owned popups (menus, tooltips, floating panels) stay
   *above* the overlay because owned windows always outrank their owner; and
   when you Alt-Tab to another app, that app goes above both, so you never dim
   Chrome. A topmost overlay gets both of these wrong. Re-assert the z-order on
   `EVENT_SYSTEM_FOREGROUND`.
9. Hide the overlay between `EVENT_SYSTEM_MOVESIZESTART` and `…END`, or the dim
   rectangle visibly trails the window while you drag it.

**Environment**

10. `cmd.exe` resolves paths in the OEM codepage and **cannot `cd` into a
    directory whose name is outside it** — under a path with CJK characters in
    it, `build.cmd` fails with "not recognized as an internal or external
    command". Use `build.ps1`. (This is why it exists.)
11. PowerShell 5.1 reads `.ps1` files as ANSI unless they carry a UTF-8 BOM, so
    a non-ASCII path baked into a script becomes mojibake. All the test scripts
    here are deliberately ASCII-only and take paths as parameters.
12. GDI screen capture returns a **stale frame when the displays are asleep**.
    Any automated photometric check must detect that (grab twice, compare) or it
    will silently report "no change".
13. `BitBlt` from the screen DC omits layered windows unless you pass
    `CAPTUREBLT` — relevant to both testing and §9.

**Interaction**

14. If InDesign is ever run elevated, a non-elevated hook receives no events
    from it (UIPI). Night View must not require admin, so document this as
    "don't run InDesign as administrator".
15. An overlapping window disables InDesign's independent-flip present path.
    Negligible for a page-layout app, but it is a real (small) cost.

**Free win, no code required**

16. InDesign's **Preferences → Interface → Appearance → "Match Pasteboard to
    Theme Color"** is an *application* preference, not a document one. Turning it
    on plus the dark UI theme makes everything except the pages themselves dark,
    with zero document risk and zero software. Do this first; it shrinks the
    problem Night View has to solve to just the white pages.

---

## 8. Document safety

No approach in this report touches the document, and the external ones cannot.
`NightView.exe`:

- never opens the `.indd` file — it does not know its path and never asks;
- never sends or posts a message to any InDesign window;
- never runs an InDesign script, ExtendScript or UXP;
- calls only **read-only** Win32 queries against InDesign's windows —
  `EnumWindows`, `EnumChildWindows`, `GetClassName`, `GetWindowRect`,
  `GetClientRect`, `ClientToScreen`, `IsWindowVisible`, `IsIconic`,
  `GetWindowLongPtr`, `GetForegroundWindow`;
- installs `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT`, so **no DLL is
  injected into InDesign** — events are marshalled to our process;
- makes writing calls only against **its own window**: `SetWindowPos`,
  `SetLayeredWindowAttributes`, `SetWindowDisplayAffinity`.

There is no code path from any of that to the document model. The document
cannot become dirty because Night View is running, for the same reason it cannot
become dirty because Notepad is running.

Two verification tools are provided:

- **`tests/verify-document-safety.ps1`** — external SHA-256 comparison of the
  INDD (and optionally an exported PDF) across a full work session. Deliberately
  does not script InDesign, so it cannot perturb what it measures. Includes a
  control protocol that separates "InDesign rewrote the file" from "Night View
  did something", because InDesign legitimately rewrites files on save for its
  own reasons.
- **`tests/report-indesign-state.jsx`** — a read-only ExtendScript probe. It
  reports `document.modified`, **`document.undoName`** (the label Edit → Undo
  would show — if anything wrote to the DOM, this changes), and the `[Paper]`
  swatch's exact color value. Every line is a getter; the only side effect is
  an alert.

The `[Paper]` fallback is **not implemented** and there is no setting to enable
it. Nothing in this codebase can modify a swatch.

---

## 9. Screen capture behavior

| Capture method | Overlay visible? |
|---|---|
| `PrintScreen` / Snipping Tool / most display capture | **Yes** (they composite the desktop) |
| `BitBlt` from the screen DC **without** `CAPTUREBLT` | No |
| `BitBlt` **with** `CAPTUREBLT` | Yes |
| OBS "Display Capture" | Yes |
| OBS / Teams / Discord **window** capture of InDesign | **No** — the overlay is a separate HWND |
| Anything, with `WDA_EXCLUDEFROMCAPTURE` set | **No, everywhere** |

So the *"Include Night View in captures: Yes / No"* switch you asked for is a
one-line call, and it is already wired to a tray checkbox in Prototype B.
`WDA_EXCLUDEFROMCAPTURE` needs Windows 10 2004+; this machine is 26200.

Note the useful asymmetry: even with the overlay left visible, a *window*
capture of InDesign is undimmed, so screen-sharing InDesign to a client shows
them the real document.

---

## 10. Recommended architecture

```
NightView.exe   (single ~40 KB executable, no admin, no installer)
│
├── ViewportLocator      class-name lookup: indesign → OWL.Document → canvas
│                        cached, invalidated on SHOW/HIDE/DESTROY
├── Tracker              SetWinEventHook (OUTOFCONTEXT) + 250 ms safety timer
│                        FOREGROUND / MOVESIZE / MINIMIZE / LOCATIONCHANGE
├── Engine  (swappable)
│   ├── TintEngine       layered window + LWA_ALPHA          [default]
│   │                    Neutral Dim, approximate Warm Dim
│   └── MagEngine        WC_MAGNIFIER @1.0x + MAGCOLOREFFECT [opt-in]
│                        true Warm Dim, Grayscale, Invert
│                        (later: ShaderEngine for Highlight Compression)
└── Tray UI              enable, strength, mode, target, capture visibility,
                         global hotkeys, INI persistence
```

The engine boundary is the important design decision. `ViewportLocator` and
`Tracker` are the hard, fiddly, InDesign-specific parts and are shared; the
engine is a small pluggable renderer. Prototypes B and C already share
`Common.cs` for exactly this reason, so promoting them to two engines behind one
interface is a refactor, not a rewrite.

### Mode → engine mapping

| Mode | Engine | Notes |
|---|---|---|
| Neutral Dim | Tint | exact `out = dst·k`, zero latency |
| Warm Dim | Tint (approximate) or Mag (true) | tint = dim + warm lift; matrix = real per-channel gain |
| Grayscale Dim | Mag only | needs channel mixing |
| Invert | Mag only | experimental, not default |
| Highlight Compression | Shader — or a per-output LUT if InDesign owns a monitor | §5 |

### Language

**C# on .NET Framework, compiled with the in-box `csc.exe`.** Not because C# is
better than C++ here, but because of what it costs: zero toolchain to install
(the compiler is already on every Windows machine), full P/Invoke access to
every API this needs — including `Magnification.dll`, which is a flat C API that
marshals trivially — a 22 KB single-file executable with no runtime to ship, and
`NotifyIcon`/`Form` for free instead of hand-rolled tray and window code.

The one hard constraint is `-platform:x64`, because the Magnification API does
not work under WOW64. The build scripts enforce it.

If the shader path is ever built, that stage is better in C++/WinRT (D3D11 +
Desktop Duplication) as a small companion, or via SharpDX/Vortice from the same
C# process. Rust buys nothing here — this is 95% Win32 interop, which is exactly
where C#'s ergonomics are strongest and Rust's are weakest.

---

## 11. What to do next

1. Turn on **Match Pasteboard to Theme Color** (30 seconds, no code, no risk).
2. Run `ProtoA.exe watch` and exercise tabs, floating windows, panel collapse,
   maximize/restore, and the portrait monitor. Confirm the canvas rect tracks.
3. Run `NightView.exe --target=canvas --strength=55` and **just work in it for
   an evening.** This is the real experiment. Adjust with Ctrl+Alt+↑/↓, and run
   `NightView.exe --watch=25` while you do it to get numbers rather than
   impressions. See §12 for the audit that followed this step.
4. Run `ProtoC.exe --diag=700` to see the magnifier pipeline side by side, then
   `ProtoC.exe --mode=warm` and `--mode=gray` to judge whether true per-channel
   gain is worth a second engine.
5. Only then decide whether linear dimming is actually insufficient. My
   prediction is that at `k ≈ 0.45` with a dark pasteboard it is not — the
   painful thing is a 2000×1300 rectangle of 255-white, and multiplying it by
   0.45 fixes exactly that.

If it *is* insufficient, §5.4 (per-output LUT) before §5.2 (shader).

---

## 12. Adversarial and compatibility audit

Everything above §11 was written while establishing that the approach works.
This section is the opposite exercise: an attempt to **disprove** the shipping
behavior, and an inventory of where it is actually known to work.

### 12.1 The harness — why the audit is not "reasoned about"

Testing an overlay against InDesign alone has a structural problem: the
measurement only works when the canvas happens to be unobstructed and nobody
touches the machine. Two of the first three verification attempts in this phase
aborted because Chrome was covering the canvas. That is not a test, it is a
coincidence.

So the audit drives a window it **owns**: `Audit.exe` creates a plain GDI window
painted with a step wedge of the exact levels of interest, and moves it to any
rectangle on demand. That converts several "strongly expected" claims into
measurements:

| Question | How the harness answers it |
|---|---|
| Does the overlay land on the exact client rect at any size/position? | move the window to 17 rectangles, compare `GetWindowRect` |
| Does it survive a monitor at negative desktop coordinates? | this machine has one at `y = −497`; move there |
| Does it depend on the target's renderer? | the target is GDI with no Direct3D anywhere |
| What is the real per-level transfer function? | the wedge contains all six levels in one capture |
| Do dialogs stay above it? | create owned windows, dump the z-order |
| Can it be pushed below its owner? | `SWP_NOOWNERZORDER` reproduces it deterministically |

`Audit.exe --selftest` covers the parts with no visible output at all — hotkey
parsing, real `RegisterHotKey` collisions, settings corruption tolerance.

### 12.2 The defect this found

**In `--zmode=owned`, the overlay could end up below the InDesign frame and
nothing would ever notice.**

The owned-window architecture rests on "an owned window is above its owner".
That invariant is enforced when the **owner is activated** — which is the case
that used to flicker, and which ownership solves completely. It is *not*
enforced when the owner is re-ordered some other way. Showing another owned
window raises the owner without taking the overlay with it.

The old code encoded the wrong half of that:

```csharp
after = IntPtr.Zero;
zNeedsWork = false;        // "ownership handles it"
```

so once the overlay sank, there was no path back. The failure is silent and
total: the dimming simply stops, with no event, no error, and nothing in the UI.

The fix keeps ownership and adds the check back, with a repair that inserts the
overlay **directly above the frame** rather than at the top — so anything
legitimately between them stays above it:

```csharp
zNeedsWork = !IsAbove(_overlay.Handle, _loc.MainWindow);
IntPtr abv = Native.GetWindow(_loc.MainWindow, Native.GW_HWNDPREV);
after = abv == IntPtr.Zero ? Native.HWND_TOP : abv;
```

This is not a return to `--zmode=above`. Chasing the z-order failed because it
was the *only* mechanism, and clicking into an already-foreground InDesign
raises it while firing no hookable event — recovery then waited for the 250 ms
timer. Ownership still handles that case with no gap; the check now only fires
when the invariant is genuinely broken, which measured as never during editing.

Regression test, deterministic:

```
[PASS] overlay repairs itself after being pushed below its owner   189 ms
```

Before the fix, that test never recovered at all.

### 12.3 Owned-window ordering, characterised

The audit separates two cases that had been conflated, because they behave
differently and only one of them is under our control:

| The owning application is | An owned window it shows lands | Asserted? |
|---|---|---|
| **foreground** (opening a menu/dialog while you work in it) | **above** the overlay — undimmed | yes, gate |
| **background** (an alert raised while you are in another app) | **below** the overlay — dimmed | no, characterised |

The second is a Windows rule, not a Night View decision: a process that is not
in the foreground cannot take the top of the z-order. Raising the window
explicitly does not help either — that was measured too. It corrects itself the
moment the window is activated, which is unavoidable if you are going to
interact with it, and the overlay is click-through so the dialog is fully usable
meanwhile. It is listed as a known limitation rather than fixed, because the fix
would require the *adjacency* rule that §3.4 measured as a cause of flicker.

Menus are unaffected in both cases: `#32768` windows carry `WS_EX_TOPMOST`,
which outranks the entire non-topmost band regardless of who is active.

### 12.4 The transfer function, measured twice on different targets

§3.3 measured this over the InDesign canvas. The audit repeated it over a
GDI-only window with a synthetic step wedge — a completely different renderer,
a different window, a different monitor:

```
                InDesign canvas        GDI step wedge
  best-fit k        0.4510                 0.4517          (requested 0.4500)
  vs out = in*k     0.21 levels            0.22 levels
  vs linear blend  31.13 levels           26.32 levels
  samples           7 593 903              2 845 152
```

Two independent measurements, same answer. This settles §4's open question in
both directions: DWM composites layered windows in **sRGB-encoded values**, and
the result **does not depend on how the window underneath was drawn**. There is
therefore no reason to apply gamma correction, and no reason to expect
InDesign's GPU Performance setting to change the dimming.

### 12.5 Geometry, measured

17 rectangles, every one exact, on a three-monitor desktop spanning
`0,−497 4000×3017`:

```
  centered / full on each of three monitors      exact, 15-17 ms
  1280x720, 1920x1080, 2560x1440 windows        exact, 15-17 ms
  tiny (160x120), wide-and-thin, tall-and-thin  exact, 15-17 ms
  straddling a vertical monitor edge            exact
  straddling a horizontal monitor edge          exact
  at the virtual-desktop origin                 exact
  negative Y coordinates (portrait monitor)     exact
```

15–17 ms is the harness's own polling granularity, not the tracking latency —
the real path is a `WinEvent` hook, and the 250 ms timer is only a safety net.
The one slow number is the **first** placement after Night View starts
(≈1.0–1.4 s), which is process startup: JIT, tray icon, hook installation. The
overlay is parked off-screen until then, so it is invisible, not misplaced.

### 12.6 Security surface

Complete inventory of the shipped binary's Win32 imports: 50 functions, all in
`user32`, `gdi32`, `shcore`, `dwmapi`, `kernel32`, `ntdll`. Grepping the shipped
sources for the techniques antivirus heuristics actually look for:

```
CreateRemoteThread 0   WriteProcessMemory 0   ReadProcessMemory 0
VirtualAllocEx     0   OpenProcess        0   SetWindowsHookEx   0
WH_KEYBOARD        0   WH_MOUSE           0   LoadLibrary        0
QueueUserAPC       0   SetThreadContext   0   AdjustTokenPrivileges 0
RegOpenKey         0   CurrentVersion.Run 0   schtasks           0
keybd_event        0   SendInput          0   mouse_event        0
PostMessage        0   SendMessage        0   AttachThreadInput  0
```

Three things deserve to be named rather than buried:

- **`SetWinEventHook` is not `SetWindowsHookEx`.** It is called with
  `WINEVENT_OUTOFCONTEXT` and a null module handle, so no DLL is injected into
  any process; callbacks are delivered to our own thread.
- **Global hotkeys use `RegisterHotKey`, not a low-level keyboard hook.** Night
  View never sees any keystroke other than the combinations it registered.
- **`BitBlt` from the screen DC** exists only in the verifier
  (`--verify` / `--baseline`), and `SetForegroundWindow` / `ShowWindow` only in
  its opt-in `--focus`. The overlay itself never captures the screen and never
  touches another application's windows.

`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` is the one call that looks
like an evasion technique. It is opt-in (`--capture=exclude`), off by default,
and exists so a screen-shared or recorded demo shows the real page brightness.
Microsoft document that on Windows before 10 version 2004 this value "will
behave as if `WDA_MONITOR` is applied" — the call **succeeds** and does
something else — so support is gated on the build number rather than on the
return value, which would otherwise tell the user their screenshots were clean
when they were not.

### 12.7 What the audit did not settle

- **Display scaling above 100 %.** All three monitors on the test machine report
  96 DPI, so per-monitor scaling could not be exercised for real. What *is*
  established mechanically: there is no resolution or scale constant anywhere in
  the shipped sources — a grep for `1920|1080|2560|1440|3840|2160|1280|720`
  returns nothing outside comments, and the only occurrences of `96` are
  DPI→percentage arithmetic in the diagnostics text. Every coordinate in the
  path is a physical-pixel `GetWindowRect` fed to `SetWindowPos`, with no unit
  conversion between them, under `PerMonitorAwareV2`.
- **InDesign with GPU Performance disabled.** The current state is observable in
  the frame title (`… [GPU Preview]`), and the audit ran with it on. Since the
  transfer function is identical over a GDI window, the *dimming* cannot depend
  on it; what is unverified is whether canvas **discovery** finds the same child
  window in CPU mode. `--target=owldoc` does not depend on that inner child.
- **InDesign versions other than 21.5.1.** See the README compatibility table.
- **Interactive editing.** `--watch` is idle-clean, but the click / drag / type
  matrix needs hands on the keyboard.

---

## 13. Multi-Adobe expansion — Abode Night View

**Phase 3. Same machine, 2026-08-21. InDesign 21.5.1, Illustrator 30.7.0,
InCopy 21.5.1, Photoshop 27.9, Acrobat 26.1.21771.0, all installed and running.**

Phase 2 hardened one product. This phase asked whether the concept generalises,
and the answer turned out to be more favorable than expected structurally and
less favorable than expected semantically.

### 13.1 The structural finding

`ProtoA.exe dump` on four running Adobe applications produced the same hierarchy
four times:

```
<frame class>                indesign | illustrator | incopy | Photoshop
  OWL.Dock
    OWL.TabPane
      OWL.TabGroup
        OWL.Document         <- the document viewport
          <inner container>  <- the canvas, strictly inside it
```

| product | frame class | OWL.Document | inner container | inner/outer |
|---|---|---|---|---|
| InDesign 21.5.1 | `indesign` | 43,132 2094×1259 | 59,148 2063×1227 | 96 % |
| Illustrator 30.7.0 | `illustrator` | 42,100 1909×1291 | 42,100 1893×1250 | 94 % |
| Photoshop 27.9 | `Photoshop` | 663,1532 1470×939 | 663,1532 1454×923 | 97 % |
| InCopy 21.5.1 | `incopy` | (no document open) | — | — |

Adobe's OWL framework is shared across the page-layout and image applications,
and it lays the document out identically in all of them. The InDesign canvas
rule — *largest visible descendant strictly inside `OWL.Document`, at least half
its area* — was verified in phase 1 and turns out to be the correct rule for
Illustrator and Photoshop too, without modification.

The inner container's **class** is not shared: InDesign and Illustrator use
`DroverLord - Window Class`, Photoshop uses `Static`. So the canvas cannot be
found by class, only by the geometric relationship. That is exactly why the rule
was written geometrically in the first place, and it is now in one place
(`TargetRegistry.LargestStrictlyInside`) used by every product and by the
verifier, rather than in two copies that can drift.

Acrobat is a different framework entirely:

```
AcrobatSDIWindow
  ... AVL_AVView 'AVScrollView'
        AVL_AVView 'AVPageView'      <- 588,83 711×949
```

Acrobat identifies its views by **window text**, not by class — 23 windows in a
plain single-document session share the class `AVL_AVView` and exactly one is
called `AVPageView`. Reading the text is a much stronger key than the class, and
it has survived from Acrobat's oldest builds into 26.1.

### 13.2 Version policy: no whitelist anywhere

Products are recognized by process name plus frame window class, then the
structure is *validated*. There is no version number in any decision. The
display name comes from the running executable's `ProductName` version resource:

| process | ProductName | ProductVersion |
|---|---|---|
| InDesign | Adobe InDesign 2026 | 21.5.1 |
| Photoshop | Adobe Photoshop 2026 | 27.9 |
| Illustrator | Adobe Illustrator 2026 | 30.7.0 |
| InCopy | Adobe InCopy 2026 | 21.5.1 |
| Acrobat | Adobe Acrobat | 26.1.21771.0 |

So the tray menu is correct about a product year nobody here has ever seen, and
a 2028 release attaches if it still looks the same. When it does not, `--probe`
names the step that failed and lists the visible window classes it found
instead. That is the whole difference between capability detection and a version
table: the failure is diagnosable without a rebuild.

InCopy is the worked example. It was launched, it was recognized, its frame class
`incopy` and OWL hierarchy were confirmed — and validation failed cleanly with
"most likely: no document is open", plus a twelve-line structural summary. That
is what an unfamiliar future version will look like, and it is legible.

### 13.3 The defect multi-target support exposed

Two Adobe applications maximized on the same monitor overlap completely. With
Illustrator in front, its canvas measured **k = 0.176 against a requested
0.451** — while *every structural check passed*: the overlay was on the exact
rectangle, owned by the right frame, above its owner, click-through, correct
alpha.

The cause is that "above my owner" is not a sufficient invariant once there is
more than one owner. InDesign's overlay was above the Illustrator frame, and
legitimately above its own owner the whole time; nothing in the code had any
reason to move it. Illustrator's canvas was therefore dimmed twice.

The fix is a third condition, not a stricter version of the existing one:

```
repair when   the overlay is not above its owner            (the phase-2 defect)
       or     a FOREIGN window sits between overlay and owner (this defect)
       or     the application was just activated              (see 13.4)
```

What is deliberately still not checked is *adjacency*, continuously. Phase 1
measured that as the flicker cause: the owner's own transient windows appear
between overlay and frame many times a second while you edit, and every
correction is a visible flash. Windows belonging to the owner are ignored; only
a foreign one is a fault. Cost when correct — the normal case — is one
`GetWindow` step, because the overlay is normally adjacent anyway.

Cloaked windows are skipped. Modern Windows keeps a great many windows in the
z-order that it does not draw — suspended packaged applications, windows on
another virtual desktop — and they are visible to `IsWindowVisible`. Anything
that walks the z-order and reasons about what covers what has to ask
`DWMWA_CLOAKED` or it will react to windows that are not on the screen.

### 13.4 Re-seating on activation, and what it bought

Activating an application raises its frame together with every window it owns —
and Windows puts ours at the **top** of that group, above the application's own
floating panels and toolbars. Left alone they stay dimmed for as long as the
application is active.

Correcting this continuously is the flicker rule again. Correcting it **once per
activation** is one `SetWindowPos` per Alt+Tab and nothing at all while editing.
Measured effect: Acrobat's floating page toolbar (`AVL_AVPopup`, 41×293, sitting
over the page) went from *below* the overlay to *above* it, and the verifier's
popup assertion went from FAIL to PASS. Idle transition count stayed at zero.

### 13.5 What each product measures

`--verify` against a baseline captured with the utility switched off, per
product, canvas region as shipped:

| product | canvas before → after | ratio | expected | chrome | result |
|---|---|---|---|---|---|
| InDesign | 159.32 → 71.84 | 0.451 | 0.451 | 1.000 | 12 / 0 |
| Illustrator | 103.83 → 46.77 | 0.450 | 0.451 | 1.000 | 12 / 0 |
| Photoshop | 181.81 → 81.77 | 0.450 | 0.451 | 1.000 | 13 / 0 |
| Acrobat | 180.86 → 81.90 | 0.453 | 0.451 | 1.000 | 13 / 0 |

Four applications, two UI frameworks, one compositor behavior. The InDesign
figures are identical to phase 2's, which is the regression baseline holding.

### 13.6 Greyscale and Shader — measured, not argued

The Magnification API was the only route to Greyscale that does not require
capturing and redrawing the viewport. Prototype C was finally run to conclusion
against InDesign's canvas, with a screen capture and a pixel differ:

| mode | matrix asked for | measured |
|---|---|---|
| identity | I | **0 of 400,000 pixels differ** — 1.0× does not resample |
| per-channel gain | diag(0.450, 0.405, 0.248) | 0.4468 / 0.4033 / 0.2403 |
| invert | −k diagonal, +k translation | mean luma 113.06 → 49.48 |
| grayscale k=1 | Rec.709 mixing | **no desaturation**: mean saturation 59.19 → 58.92 |
| grayscale k=0.45 | Rec.709 mixing × 0.45 | **neutral dim**: RGB × 0.4503/0.4487/0.4501, saturation × 0.45 |

The diagonal and the additive row are honoured exactly. Channel mixing is not.
Every one of the five results is consistent with the implementation collapsing
each color column to its sum and applying it as a per-channel gain — affine per
channel, no mixing. The one primitive that could have produced grayscale without
capture did not produce grayscale.

Two other things fell out of the same run, both of which had been open questions
since phase 1:

* **1.0× magnification is pixel-exact.** Text does not soften. That had been
  listed as "unknown" for two phases.
* **The latency is structural.** There is no source-repainted notification, so
  the host is refreshed on a timer: 36–38 refreshes/s against a 16 ms timer,
  unsynchronized with the window underneath, 0.1–0.3 % CPU, 68–71 MB. Neutral
  has no such term at all.

Shader mode needs the non-affine curve. `SetDeviceGammaRamp` can express it but
applies to the whole output, which fails the requirement the product exists for.
Windows Graphics Capture plus a D3D11 pixel shader can do it properly and is a
different program: WinRT interop from .NET Framework, a GPU dependency, a
capture permission surface, recursion risk, and one frame of latency by
construction. Deferred, with the affine limitation documented as a property of
Neutral instead.

### 13.7 The harness grew a fake Adobe

Phase 2's lesson was that a test which depends on Adobe being unobstructed is a
coincidence, not a test. Phase 3 extends that: `Harness.cs` registers real Win32
classes named `OWL.Dock`, `OWL.TabPane`, `OWL.TabGroup`, and `OWL.Document` and
builds windows in exactly the Adobe shape.

WinForms cannot do this — `NativeWindow` rewrites a requested class name into
`WindowsForms10.<name>.app.0.<hash>`, so a Control claiming to be `OWL.Document`
registers as something else and the code under test is never exercised.
`RegisterClassW` takes the name it is given. Window classes are per-process
without `CS_GLOBALCLASS`, so nothing leaks outside the audit process.

The harness then drives the **shipping binary** — via `--adapter=id:Name:proc:class:owl`,
which builds a real `OwlTarget`, so the validation being tested is the shipping
validation and not a stand-in. Twenty mechanical checks cover: one frame one
overlay, exact canvas rect through the OWL rule, two documents in one frame, a
second application, moving one frame without disturbing the other, burial and
repair with several targets tracked, minimize and restore, closing an
application, opening a new one afterwards, closing one document of two, every
target closing, and global OFF.

Two failures in that suite turned out to be the harness rather than the product
— the audit's own WinForms dialogs shared a class with its target window and were
being tracked as extra applications. That is worth recording because it is the
same class of mistake as the "largest child window" heuristic: a matcher that is
too loose finds things nobody meant it to find.

### 13.8 Cost

Idle, four applications tracked, four overlays live, 60 s window:
**0.55 % of one core, 49.1 MB working set, 287 handles, 9 threads.** With nothing
selected or globally off: 0.00 % and 36.7 MB. Tracking is event-driven; the
250 ms timer is a safety net rather than the mechanism.

### 13.9 What phase 3 did not settle

* Display scaling above 100 %, 4K, and mixed-DPI. Established mechanically
  instead: no hard-coded coordinates, physical pixels throughout, PerMonitorV2
  applied before any window exists.
* InCopy with a document open.
* Adobe versions other than the five measured. Structural probing is the
  mitigation, not a claim.
* CPU-rendering mode in InDesign — canvas *discovery* only; renderer
  independence itself is settled by the GDI-window measurement.
* Remote Desktop, HDR, ARM64.
* macOS. [MACOS.md](MACOS.md) is research, not a prototype, and says so on its
  first line.
* The interactive editing matrix, which needs a person at the keyboard.

---

## 14. Finalization pass — Abode Night View 1.2.0

Phase 4. No architecture was touched: targeting, discovery, z-order, click-through,
and photometry are byte-for-byte the behavior phase 3 measured. What changed is
what the program *offers*, and one thing that turned out to be a real bug.

### 14.1 The reported checkmark bug had two causes, not one

The tray item read `Enabled` with no tick in both states. Phase 3 had already
made every `Checked` flag derive from live state, and it did — correctly, and
invisibly.

    var menu = new ContextMenuStrip();
    menu.ShowImageMargin = false;        // and ShowCheckMargin defaults to false

A `ToolStripDropDownMenu` draws a check mark only if it has a margin to draw it
in. With both margins off, `Checked = true` renders as nothing at all. Every
state assertion in the harness passed, because the state was right; the pixels
were empty. `Include in screen captures` had been silently broken the same way
for as long as it had existed.

Fixed by turning the check margin on, and the fix is now itself asserted:
`TrayMenuStyle.CanDrawChecks` is checked mechanically, so a menu that cannot show
state fails a test rather than shipping.

The second cause was the wording. `Enabled` alone reads equally as a label and as
a button. The item now names the state it is *in* — `Enabled` / `Disabled` — and
carries the tick as well, so the state is given twice.

### 14.2 Warm was measured, and then removed

Warm set the overlay's source color to amber instead of black. Against
Illustrator's canvas at strength 35: undimmed 96/96/96, Warm 83/70/62, predicted
83.4/70.8/62.4. It is precise, deterministic, composition-native, and free.

It is also, by the arithmetic it is built on, the wrong thing:

    out_R = k·R + (1-k)·60     out_G = k·G + (1-k)·24     out_B = k·B + 0

* Blue is attenuated by exactly k — the same as Neutral. A warm filter is meant
  to attenuate blue *relative* to red; a layered window applies one alpha to all
  three channels and an offset can only add. The mechanism cannot do what the
  name claims.
* Luminance ratio 0.75 against Neutral's 0.65 at the same strength: it emits
  more light, not less.
* Break-even for red is at level 60, so every darker pixel comes out brighter
  than it started. The measured ruler strip went 59.6 → 60.1.

"Warm (approximate)" turned out to mean "does not do the thing it is named
after". Removed, and with only one filter left the Mode submenu went with it — a
submenu offering a single choice is a control that cannot be used.

### 14.3 Greyscale and Shader removed from the product

Both were already unavailable and shown greyed out with their reason. A greyed-out
row is a permanent advertisement for something that is not coming; the
investigation is finished and the answer is no. Removed from the UI, from the
settings vocabulary, and from the source. `ModeBackends` is gone;
`experiments/ProtoC_MagnifierOverlay.cs` — the rig that produced the answer — is
kept out of the product tree.

`mode=greyscale` and `mode=shader` in an existing settings file normalize to
`neutral` and are written back normalized, silently. Neither was ever selectable,
so the only way to have one is to have typed it.

### 14.4 Rulers

Investigated on the report *"the side ruler is dimmed along with the document
viewport"*. Full measurements in [measurements/rulers.md](measurements/rulers.md).

The rulers are not separate windows in any of the five products. What decides the
question is where each product puts its canvas *child* window:

| product | canvas child inset from the document container | rulers dimmed |
|---|---|---|
| InDesign | L 16, T 16, R 15, B 16 | **no** — measured at ratio 1.000 |
| Illustrator | L 0, T 0, R 16, B 41 | yes — measured at 0.657 against a canvas of 0.646 |
| Photoshop | L 0, T 0, R 16, B 16 | yes, if shown |
| InCopy | no inner container at all | yes, if shown |
| Acrobat | R 17 (scrollbar) | n/a |

InDesign's canvas child is inset by exactly the ruler strips, so dimming the
canvas excludes them for free — and the exclusion tracks View → Show/Hide Rulers
with no code involved: hiding them grows the same HWND from `59,148 2063x1227` to
`43,132 2079x1243`.

For the others the only available fix was a fixed inset, rejected because Adobe
scales the ruler with its own UI Scaling preference, independent of Windows DPI.
Deriving it from the scrollbar width is a coincidence dressed as geometry.
Everything else on the option list — pixel-color detection, screenshot analysis,
capture-based masking — means reading rendered content, which is the thing
Neutral exists to avoid. Documented as a cosmetic limitation.

What the pass did add is a regression check: `--verify` photographs every strip
the canvas rectangle excludes, off and on, and fails if any of them has been
multiplied by k. Its first version used a 3 % band around 1.000 and failed on a
run where nothing was wrong — ruler and scrollbar content genuinely moves between
two captures. The threshold is now the midpoint between 1.0 and k, which jitter
cannot cross and dimming cannot avoid.

### 14.5 InCopy promoted to tested

Phase 3 left InCopy as *structurally expected* because no document had ever been
open in it. One was, this time:

    viewport   OWL.Document  1111,398 1253x702
    canvas     no inner container; the viewport is used directly
    verify     12/12, canvas 147.15 -> 66.13, ratio 0.449 (expected 0.451)

Promoted on the measurement, not on the shared adapter.

### 14.6 macOS shelved

No Apple hardware to validate against, so no implementation. [MACOS.md](MACOS.md)
carries a SHELVED banner and remains accurate research. Production scope is
Windows 10 21H2+ / Windows 11, x64.

### 14.7 Version

1.1.0 → **1.2.0**. Two user-visible features removed and a dialog added, with no
change to targeting or rendering behavior: a minor bump, not a patch. The
version now lives in exactly one place, `AssemblyInfo.cs`, and `Diag.Version`,
`--version`, the tray header, and the About box all read it back out of the built
binary's own version resource.

### 14.8 What phase 4 did not settle

Everything phase 3 left open except InCopy, which is now measured, and 125 %
display scaling, which the user checked by hand and reported free of visible
defects — recorded as manual visual validation, not as a measurement. Still
untested: 150 %, 4K, mixed-DPI, HDR, Remote Desktop, ARM64, Adobe versions other
than the five measured, InDesign in CPU-rendering mode, macOS, and the
interactive editing matrix.
