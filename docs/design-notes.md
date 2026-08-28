# Design notes

Why the utility is the way it is. Nothing here is needed to use it — this is the
record of what was tried, what it measured, and what was rejected on the strength
of that.

- [Why Photoshop is off by default](#why-photoshop-is-off-by-default)
- [Why no shortcut is bound out of the box](#why-no-shortcut-is-bound-out-of-the-box)
- [Rendering: why there is one filter](#rendering-why-there-is-one-filter)
  - [Why Warm was removed](#why-warm-was-removed)
  - [Why Greyscale is not shipped](#why-greyscale-is-not-shipped)
  - [Why Shader is not shipped](#why-shader-is-not-shipped)
- [The words in the tray](#the-words-in-the-tray)

---

## Why Photoshop is off by default

Photoshop works exactly as well as Illustrator does; it was measured doing so.
The reason it starts switched off is not technical, it is semantic. An InDesign
or Illustrator viewport is mostly white paper you are placing content *onto*;
dimming it removes glare. A Photoshop viewport is mostly **the image**, and
dimming it changes the apparent brightness of the thing you are editing.

Whole-canvas dimming is a perfectly reasonable thing to want at 2am — it is just
not a safe default, and the tool should not imply it is only dimming "the paper"
when there is no paper. Tick Photoshop in tray → Targets if you want it.

The tray menu lists it by name like every other product. The reasoning is here
and in `--probe`, which prints it next to the default; a menu item is a place
for a name and a state, not for an argument with the person reading it.

---

## Why no shortcut is bound out of the box

`RegisterHotKey` installs a system-wide keyboard grab: the shell dispatches the
keystroke to Abode Night View *before* offering it to whatever has focus, so a
default binding is not a convenience, it is a key taken away from every program
on the machine on the user's behalf.

Every candidate for a default was ruled out, each for a different reason:

| candidate | why not |
|---|---|
| `Ctrl+Alt+<printable>` | AltGr reports itself to Windows as Ctrl+Alt. On German, French, Polish, Portuguese, Nordic, Spanish, Czech, Turkish and US-International layouts this **is** the AltGr character on that key, and taking it stops that character being typable anywhere. This is what 1.2 and earlier shipped. |
| `Win+<anything>` | Windows 10 and 11 reserve the Windows key; the combinations are consumed by the shell and are not reliably registrable. |
| `Ctrl+Shift+…` / `Alt+Shift+…` | Windows' own defaults for switching keyboard layout and input language live on those modifier pairs, and `Ctrl+Shift+<letter>` is heavily used inside every Adobe application. |
| `F1`–`F12`, any modifier | Adobe assigns panels and commands across the whole function row in InDesign, Illustrator and Photoshop, and users remap them freely. A global grab takes the key from every application, not only from Adobe. |
| Numpad, Pause, Scroll Lock | Layout-proof, and absent from most laptop and tenkeyless keyboards, or behind `Fn`. |

Nothing satisfies *free on every layout* **and** *free in Adobe* **and** *present
on every keyboard*, so nothing is bound. The tray menu and the schedule work
without a shortcut.

**The AltGr warning did not become unnecessary when the defaults went away — it
became more necessary.** A custom binding is captured from the user's own
keyboard, which sounds like it should settle the question, and does the opposite:
on a German layout, pressing `AltGr+Q` to type `@` arrives at the editor as
`Ctrl+Alt+Q` and would be accepted as a perfectly ordinary-looking shortcut. The
user would have bound the key they type `@` with and would not find out until the
next time they needed one. So the editor asks the live keyboard layout what
`AltGr+<key>` actually types and quotes the character back — see
[Shortcuts](usage.md#shortcuts) for what that looks like.

*Esc* cancels a row and *Backspace* clears it, both of which the dialog says,
with the key names in italics, because "Esc cancels that row" otherwise reads as
a sentence about escaping.

---

## Rendering: why there is one filter

There is one filter. It is called **Neutral** and it is the only thing the
program can do to a pixel:

    out = src·a + dst·(1-a)      DWM source-over, in sRGB-encoded values
    src = black                  so out = dst·k,  k = 1-a

A per-channel multiply by a single constant. Hue, saturation, and relative
contrast are all preserved; only the level moves. No capture, no second render
path, no frame of latency — DWM composites the overlay with the target window in
the same frame it was already going to draw. The arithmetic and its measured
transfer curve are in [Internals](internals.md#how-it-works).

Three other modes existed in the source at one point. All three are gone from
1.2.0, and each was removed on a measurement rather than an opinion. This is a
design decision, not a to-do list: none of them is "coming soon".

| mode | why it is not shipped |
|---|---|
| **Warm** | Removed in 1.2.0. It was cheap, deterministic and did the wrong thing — see below |
| **Greyscale** | The only route that does not capture the screen did not perform the channel mixing it was asked for, and needs an unsynchronized refresh timer |
| **Shader** | A correct tone curve needs capture plus GPU processing, and therefore a frame of latency by construction |

### Why Warm was removed

Warm set the overlay to an amber source color instead of black. Measured against
Illustrator's canvas at strength 35 with the default tint, the whole transform is
visible in three numbers — the canvas read 96/96/96 undimmed and **83/70/62**
with Warm on, against a prediction of 83.4/70.8/62.4. So it is exactly what the
arithmetic says it is: precise, deterministic, and composition-native.

The problem is what the arithmetic says. With a non-black source, source-over
becomes *multiply by k, then add light*:

    out_R = k·R + (1-k)·60      out_G = k·G + (1-k)·24      out_B = k·B + 0

Three consequences, all measured:

- **It removes no blue.** `out_B` is `k·B` — identical to Neutral. A warm filter
  is supposed to attenuate blue relative to red; this cannot, because a layered
  window applies one alpha to all three channels and an offset can only *add*.
  The name describes an effect the mechanism cannot produce.
- **It emits more light than Neutral, not less.** Same canvas, same strength:
  Neutral gave luminance ratio 0.65, Warm gave **0.75**.
- **It lifts the black floor.** Break-even for red is at level 60: every pixel
  darker than that comes out *brighter* than it started. The measured ruler strip
  went 59.6 → 60.1 in red. Black artwork and Adobe's own dark interface turn a
  dull red, which on a layout tool is a color-fidelity problem rather than a
  matter of taste.

It was kept for a while behind the label "Warm (approximate)". "Approximate"
turned out to mean "does not do the thing it is named after", which is not a
caveat, it is a different feature. Removed.

### Why Greyscale is not shipped

The one Windows primitive that could produce grayscale *without* capturing and
redrawing the viewport is the Magnification API's `MagSetColorEffect`, a
documented 5×5 color matrix applied by a `WC_MAGNIFIER` control at 1.0×.
Prototype C implements exactly that. Measured on this machine:

- **1.0× magnification does not resample.** An identity matrix produced a copy
  that was byte-for-byte identical to the source over 400,000 pixels. Text stays
  sharp. This was an open question and it is now settled.
- **The diagonal and the additive row are honoured exactly.** Three different
  per-channel gains came back within 0.4 % of what was asked, and an inversion
  matrix inverted correctly.
- **Channel mixing did not happen.** A pure Rec.709 grayscale matrix behaved as a
  *neutral* gain of k, with saturation fully preserved and simply scaled by k.
  Every result was consistent with the implementation collapsing each color
  column to its sum and applying it as a per-channel gain.

So the primitive does not do the one thing Greyscale needs. That is a measured
reason to leave the mode disabled, not a guess. Scope of the claim: one machine,
one GPU, one Windows build — enough to refuse to ship it, not enough to say the
API can never do it.

There is a second, independent reason. Even where it works, the magnifier is a
**capture-and-redraw** path: there is no "the source repainted" notification, so
the copy is refreshed on a timer, measured at 36–38 refreshes/s against a 16 ms
timer, unsynchronized with the window underneath. Neutral has no such term — DWM
composites the overlay with the target in the same frame. A filter that trails
the canvas while you scroll is worse than a mathematically imperfect one that
does not.

Full numbers in
[`measurements/magnification-api.md`](../measurements/magnification-api.md).

### Why Shader is not shipped

The highlight-compression curve is not affine, and neither alpha blending nor a
color matrix can express it. The options that can:

| approach | why not |
|---|---|
| `SetDeviceGammaRamp` / DXGI gamma control | genuinely non-affine, but applies to the **whole output**. It would dim the panels, the menus and the other monitor's contents too. Fails the one requirement the product exists for |
| Windows Graphics Capture + D3D11 pixel shader + DirectComposition | can do arbitrary curves. Also: capture permission surface, recursion risk, a GPU dependency, hand-written WinRT interop from .NET Framework, and one frame of latency by construction. That is a different program, not a mode |

Deferred, documented, and the affine limitation is documented as a property of
Neutral instead of hidden behind a mode that does not exist.

The research code is kept, out of the product tree, in
[`experiments/ProtoC_MagnifierOverlay.cs`](../experiments/ProtoC_MagnifierOverlay.cs)
— an answer is only worth as much as the thing that produced it.

---

## The words in the tray

The tray menu, the hover text, and the notification all describe one state. Most
of the work in them is making sure they cannot disagree.

**Targets is alphabetical and drops the word "Adobe".** Every product this
utility knows about is an Adobe one, so the word sorted nothing and told the
reader nothing. The name that is left is still read from the running
executable's own `ProductName` resource, so a year nobody here has seen still
appears correctly.

**"Not running" appears only when something compatible IS running.** With
nothing open, the menu said "No supported Adobe application is running" and then
listed all five of them immediately underneath — a contrast with nothing to
contrast against. Note that *compatible* is doing the work: Premiere Pro is not
one of the five adapters, so no amount of it running brings the list back.

**A version this build cannot hook into says so, by name:**

```
Targets (4 selected, 2 running)  >
      ✓ Illustrator 2026
        InDesign 2027 (unsupported version)
      ──────────
      That version's windows are not ones this build can read.
      Run "Probe Adobe applications..." and send the report.
```

There are four distinct answers to "why is nothing dimmed", and they used to be
one silence: not running, running and unreadable, running with no document open,
and attached. The middle two are told apart structurally rather than guessed at
— an application whose own window framework is present but has no document view
in it has no document open; one with no recognizable framework at all is a
version whose windows have been rearranged. A splash screen is filtered out by
shape, so a slow launch is never reported as an unsupported version.

**A product you have unticked reports the same state as one you have ticked.**
The engine only tracks windows for the products that are switched on, so the
menu is asked about the others with nothing in hand — and an unticked Photoshop
sitting on its welcome screen was answering "unsupported version", which is a bug
report about a version that is perfectly fine. The unticked ones are now looked
up and inspected like the rest, and `Audit.exe --selftest` asserts that the
answer does not depend on whether the product was being tracked, nor differ from
what `--probe` says about the same window.

**Whatever there is to say about a product goes in one parenthesis after its
name** — `Photoshop 2026 (no document open)`, `InDesign 2027 (unsupported
version)`, `Illustrator 2026 (2 windows, no document open)`. It used to be a
padded dash, and a dash needs a gap on each side to read as a separator at all,
which is where the run of spaces in the menu came from. The same rule gives
`Schedule (off)` and `Strength (55%)`, and the text comes from `TrayState` —
pure functions — so `Audit.exe --selftest` can read every line the user reads
and assert there is no gap inside any of them. There is one further trap in
there: Photoshop's own `ProductName` resource ends in a space, so a label built
by concatenation had a hole in it wherever that name went. It is squeezed once,
where the version resource is read.

**A product is called the same thing everywhere.** The engine only tracks
windows for the products that are switched on, so anything unticked had no frame
to take a name from — and the same Photoshop was `Photoshop 2026` in the tray
menu and `Photoshop` in the notification, a few seconds apart, with nothing to
tell a reader which was right. Both now enumerate.

**The global state is given twice**: the item's *word* changes between `Enabled`
and `Disabled`, and the enabled state also carries a checkmark. One or the other
alone was ambiguous — a lone "Enabled" reads equally as a label and as a button.

**The Strength window puts the setting above the slider and its consequence
below it**: `20% (k = 0.80)` over the control, and `255 (pure white) now
displays as 204.` under it, in gray. The percentage is what you chose and k is
what the compositor multiplies by — one fact in two units, so they stay on one
line; the sentence underneath is the only part you can check by looking at the
screen, so it sits next to the thing that changes it. All three used to be
pipe-separated on a single line above the slider, which asked the eye to do the
splitting.

**Switched off, the state is the whole message.** The strength used to be in the
hover text too, and it was a number describing an effect that was not being
applied to anything: the hover read `55%` over a screen that was not dimmed at
all. A stored setting is not a state, and the hover reports the state. The value
is still one click away on the Strength item, where it is being read as a
setting.

**An application with no document open is not reported** in the hover text. It is
the most ordinary thing an Adobe application can be doing — sitting on its own
home screen, between jobs — and the hover said it about the desktop as a whole,
where it distinguished nothing. The Targets menu still says it, per product,
because there it tells one product apart from another. Silence is what "nothing
is wrong" should look like; a fourth phrase would only have been the same noise
under a shorter name.

Those are the Targets menu's own words, and they are not a second opinion: the
hover and the menu are two renderings of one survey of the desktop. They used to
be two ideas of what a target *is* — the menu asked the desktop, the hover
counted overlays and called zero of them "no target" — so a Photoshop
sitting on its welcome screen was `Photoshop 2026 (no document open)` in the
menu and `no target` on hover, at the same moment, about the same program. An
application that is running and selected **is** a target; whether it is currently
offering anything to dim is a different question, and the hover now answers that
one — or says nothing, which is also an answer.

The hover is also rebuilt when the number of overlays or the number of frames
changes, not only when you click something. Before, opening a document left the
tooltip reading `no target` until the next time you touched the menu.

**k leaves the notification with the percentage.** k is the multiplier the
compositor applies; switched off there is no multiply, so `k = 0.45` was naming a
coefficient nothing was multiplying by — on the one notification whose whole job
is to say that the dimming has stopped. The line under it already says which
shortcut switches it back on.

Both formats used to be built by substituting `ON` or `OFF` into a single
composite string, which is why the strength was there in the off state: it was
structurally impossible to leave out. The release build now fails if either of
those format literals is still in the shipped binary.

**Neither names the filter any more.** Neutral is the only mode this build
renders, so the word was identical in every possible state — a third of a
63-character tooltip budget spent saying nothing. Dropping it is also what made
room to write the product's name out in full instead of as "Abode NV".

**The icon and the notification resolve through one function**, because a
notification arriving in a different face from the icon that raised it is the
same class of contradiction as a tooltip disagreeing with its own menu. The pair
carries 16, 20, 24, 32, 48, 64, and 96 px: a balloon asks at `SM_CXICON` and a
notification area at `SM_CXSMICON`, and an `.ico` whose smallest entry is 32 px
is a tray icon the shell has to halve — which is how a two-pixel sunglasses bar
stops being legible. The release build reads those sizes back out of the
generated containers rather than trusting the list that generated them.

Windows Forms only offers four stock pictures for a balloon, but the shell has
taken an arbitrary one since Windows XP SP2 (`NIIF_USER` with `hBalloonIcon`), so
the notification is raised through `Shell_NotifyIcon` directly. Doing that needs
the window handle and id of an icon already in the notification area, and those
are private fields of `NotifyIcon`, read by reflection. Every step of that can
fail — a future runtime renaming a field, a missing resource, the shell refusing
the call — and every step falls back to the ordinary balloon with a stock icon
rather than to an exception. `--diagnostics` prints which path was taken, because
a silent fallback that looks right on the machine it was written on is the exact
failure this project keeps guarding against.

**The About box paints its icon rather than converting it.**
`Icon.ToBitmap()` cannot read a PNG-compressed `.ico` entry — it walks the
payload as though it were a device-independent bitmap and returns noise, which
is exactly what the About box showed for two releases. `Graphics.DrawIcon` goes
through `DrawIconEx`, the same path the shell uses, and `build-release.ps1`
now refuses to ship a source tree that calls `ToBitmap` at all.

**There is no Mode submenu.** This build renders one filter, and a submenu with a
single possible choice is a control that cannot be used for anything. The filter
is named in `--diagnostics`, where "which one did this build actually render?" is
a real question.
