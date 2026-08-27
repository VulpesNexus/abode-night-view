# Abode Night View on macOS — feasibility study

> ## SHELVED
>
> **Research only. No implementation, no build, no support claim, until there is
> Apple hardware to test on.**
>
> Shipping a macOS build that nobody can validate would create a code path this
> project has no way to stand behind, which is the one thing it has consistently
> refused to do. The production scope is **Windows 10 21H2+ / Windows 11, x64**.
>
> Nothing here is abandoned and nothing here is inaccurate; it is simply not
> started. If a Mac becomes available, this document is where the work resumes.

**Status: research, not tested.** No macOS machine was available while this was
written. Everything below is drawn from Apple's documentation and from what the
Windows implementation had to do; nothing in it has been run. Where a claim is
about behavior rather than about an API's existence, it says so.

The Windows product is finished and measured. This document exists to answer one
question honestly: *what would the same product be on macOS, and is it the same
product at all?*

---

## 1. The short answer

A macOS build is feasible, and it is **not a port**. The mechanism the Windows
version is built on — a click-through layered window made an *owned window* of
another process's frame, so the compositor keeps it above that frame and below
that application's own dialogs — has no macOS equivalent. Three things break:

| Windows                                                | macOS                                                          |
|--------------------------------------------------------|----------------------------------------------------------------|
| the canvas is a child **HWND** with a screen rectangle any process can read | the canvas is an **NSView**; other processes cannot see it at all except through the Accessibility API |
| `SetWindowLongPtr(GWLP_HWNDPARENT)` puts our window in another app's owner group | window ordering is **per application** within a level; you cannot interleave your window between another app's windows |
| reading window geometry needs no permission           | viewport geometry needs **Accessibility** permission          |

So the macOS design has to be different in kind, not merely in API names.

---

## 2. What each piece would be

### Finding the applications

`NSWorkspace.shared.runningApplications` gives bundle identifiers
(`com.adobe.InDesign`, `com.adobe.illustrator`, `com.adobe.Photoshop`) and
process identifiers, with `didLaunchApplicationNotification` /
`didTerminateApplicationNotification` for launch and quit. This replaces the
`EnumWindows` discovery pass and needs no permission. It is a cleaner mechanism
than the Windows one, because Adobe's bundle identifiers are stable and
documented in a way its Win32 window classes are not.

### Finding the window

`CGWindowListCopyWindowInfo` returns, for every on-screen window,
`kCGWindowOwnerPID`, `kCGWindowBounds`, `kCGWindowLayer`, and
`kCGWindowNumber` — **with no permission at all**. Only `kCGWindowName`, the
title, is gated behind Screen Recording. So the *document window's* rectangle is
free.

### Finding the viewport inside the window

This is where it costs something. There is no macOS equivalent of enumerating
another process's child windows, because Adobe's canvas is not a window. The
only supported route is the Accessibility API: `AXUIElementCreateApplication(pid)`
and then walking `kAXWindowsAttribute` → `kAXChildrenAttribute` for the
`AXScrollArea` (or equivalent) that represents the canvas, reading
`kAXPositionAttribute` and `kAXSizeAttribute`.

That requires the user to grant **Accessibility** permission in
System Settings → Privacy & Security. It is a one-off, it is the same permission
window managers and text-expansion tools ask for, and it does **not** require
Screen Recording.

So there are two honest product tiers on macOS:

* **Window-level dimming** — no permission at all, dims the whole Adobe document
  window including its docked panels. Less useful, but installable and usable
  with zero prompts.
* **Viewport-level dimming** — the actual product, needs Accessibility.

The Windows version has no such split, which is worth saying plainly rather than
papering over.

### The overlay

`NSWindow` with `styleMask: .borderless`, `backgroundColor: .black`,
`alphaValue: 1 - k`, `isOpaque = false`, `ignoresMouseEvents = true`,
`collectionBehavior` including `.canJoinAllSpaces` and `.stationary`, and
`hidesOnDeactivate = false`. `ignoresMouseEvents` is the documented equivalent of
`WS_EX_TRANSPARENT` and is the whole of the input-transparency story — there is
no macOS analog of the `WM_NCHITTEST` belt-and-braces the Windows build also
carries.

`alphaValue` composites in the same source-over form Windows uses, so the
measured `out = dst * k` relationship should hold. **Should**: unverified. The
Windows result was measured over 7.6 M pixel pairs precisely because
"should" was not good enough there either.

One flag worth noting for later: recent macOS releases have had reported
regressions where a borderless transparent window intercepts events across its
whole frame rather than only its opaque parts. That is exactly the failure this
product cannot tolerate, and it is a reason to treat input transparency as
something to *measure on each macOS version*, not to assume.

### Z-order — the part that does not port

macOS orders windows by **window level** first, then by application activation,
then within the application. A window belonging to Abode NV cannot be placed
between two of InDesign's windows. The consequences:

* At `.normal` level, our overlay sits below every window of whichever
  application is active — including InDesign's — so it would be invisible
  whenever InDesign is in front. Useless.
* At `.floating` level, it sits above every normal window of *every*
  application. It would dim whatever happens to be underneath, including a
  browser you switch to.

The design that actually works is therefore: **show the overlay only while the
target application is frontmost**, at `.floating` level, and hide it on
`NSWorkspace.didDeactivateApplicationNotification`. Menus draw at
`kCGPopUpMenuWindowLevel`, well above floating, so menus stay undimmed for free —
better than Windows, where that had to be checked.

The cost is a genuine behavioral difference to document: on Windows a
background InDesign window stays dimmed; on macOS it would not. That is a
smaller loss than it sounds — you are not reading a window you are not in — but
it is a difference, not a detail.

Adobe's floating panels are the same class of limitation as on Windows: a panel
floating over the canvas would be dimmed with it.

### Tracking movement

`AXObserverCreate` with `kAXWindowMovedNotification`, `kAXWindowResizedNotification`,
`kAXFocusedWindowChangedNotification` is the direct analog of the WinEvent
hooks, and like them it is out-of-process and involves no injection.

### Global shortcuts

`RegisterEventHotKey` (Carbon, still supported and still the normal answer) or a
`CGEventTap`. `RegisterEventHotKey` needs no permission; an event tap needs
Accessibility. Prefer the former, for the same reason the Windows build uses
`RegisterHotKey` rather than a keyboard hook.

### Greyscale and Shader

Both would be `CIFilter` (`CIColorMatrix`, `CIColorCube`) over captured content,
and capture on macOS means **ScreenCaptureKit**, which means **Screen Recording**
permission. That must stay isolated to those modes: a Neutral-only user should
never see that prompt. This is the same tiering the Windows build ended up with,
arrived at for a different reason.

Note one asymmetry in Apple's favor: `CIColorCube` is a genuine 3D LUT, so the
non-affine tone curve that Windows cannot do without building a whole D3D
pipeline is a few lines of Core Image. The obstacle on macOS is the permission
and the capture latency, not the maths.

---

## 3. Shared code, or two programs?

**Two native programs, with a shared specification rather than shared code.**

The measured reason: strip out the platform layer and what is left is the
settings file format, the mode/strength model, the region vocabulary, the target
identity list, and the state machine. That is a few hundred lines. The Windows
implementation is ~2,900 lines of C#, and essentially all of the interesting part
is HWNDs, owner groups, WinEvent hooks, and DWM behavior — none of which
survives translation.

Wrapping a few hundred lines of shared logic in a cross-platform runtime, and
paying for it with a heavier binary and a less predictable window system, is a
bad trade for this particular program. The Windows binary is 293 KB and needs
nothing installed. An Electron equivalent would be two orders of magnitude
larger and would make z-order *less* controllable, which is the one thing this
product cannot afford.

What should be shared is the **contract**: identical `.ini` keys, identical
region names, identical product ids, identical `--probe` output shape. Then a
settings file, a bug report, and a support answer are the same on both platforms
even though no line of code is.

---

## 4. Targets and deployment

**macOS floor: macOS 14 (Sonoma).** Adobe supports the current macOS and the two
previous majors for its 2026 applications, and InDesign 2026 lists macOS 13
(Ventura) as its own minimum. Supporting anything older means supporting a Mac
that cannot run the applications this attaches to.

**Architecture: Universal 2** (arm64 + x86_64) in one bundle. Nothing in the
design is architecture-specific and Xcode produces both from one build, so this
is close to free.

**Distribution stages:**

| stage              | what the tester sees                                                        |
|--------------------|-----------------------------------------------------------------------------|
| local development  | runs from Xcode, no prompts beyond the permission the chosen mode needs      |
| unsigned build     | Gatekeeper refuses on double-click; right-click → Open, or clear the quarantine attribute. Fine for a handful of testers, not for distribution |
| signed (Developer ID) | opens normally on the machine that built it; other machines still get a first-run prompt until notarised |
| signed + notarised | opens normally everywhere; this is the only honest public build              |

**Permissions, by mode:**

| mode                | Accessibility | Screen Recording |
|---------------------|---------------|------------------|
| Neutral, window-level | no          | no               |
| Neutral, viewport-level | **yes**   | no               |
| Greyscale / Shader  | yes           | **yes**          |

Nothing should be requested at launch. Ask when the user selects a mode that
needs it, and say which mode and why.

---

## 5. What would have to be measured before claiming any of this

The Windows work turned six "obviously fine" assumptions into measurements, and
two of them were wrong. The macOS equivalents, in the order they would settle
the design:

1. Does `alphaValue` composite in sRGB-encoded values, giving `out = dst * k`?
   (Measure with a step wedge and a screen capture, exactly as `Audit.exe` does.)
2. Does `ignoresMouseEvents` really pass every event through on the current
   macOS, including tablet and trackpad gestures?
3. Does the Accessibility hierarchy expose a stable canvas element in
   InDesign, Illustrator, and Photoshop — and is it the same shape in all three,
   the way the OWL hierarchy turned out to be on Windows?
4. How quickly does an `AXObserver` fire during a live window drag, compared
   with the 15–16 ms the WinEvent path measures?
5. Does the frontmost-only design read as correct in use, or as the filter
   flickering off every time you touch another application?

Until at least 1–3 are answered on real hardware, the correct status line for
macOS is **feasible, not started**.
