# Development

Building it, proving it works, and where everything lives.

- [Building](#building)
- [Verifying it yourself](#verifying-it-yourself)
- [Performance](#performance)
- [Distributing it](#distributing-it)
- [Still worth doing by hand](#still-worth-doing-by-hand)
- [Repository layout](#repository-layout)

---

## Building

```powershell
git clone https://github.com/VulpesNexus/abode-night-view.git
cd abode-night-view

.\build.ps1              # everything: the app plus the prototypes and harness
.\build.ps1 -Release     # only dist\AbodeNightView.exe
.\build-release.ps1      # clean, regenerate the icon, build, smoke-test, hash
```

The compiler is `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` —
an OS component on every Windows 10 and 11 machine. Nothing to download, no SDK
to version-match. It is a C# 5 compiler, so no string interpolation, no local
functions, no null-conditional operators.

`build.cmd` and `build-release.cmd` are thin wrappers. Note that **cmd.exe cannot
`cd` into a directory whose name contains non-ASCII characters** — it resolves
paths in the OEM codepage — so on such a path, use the PowerShell scripts
directly.

Not bit-for-bit reproducible: the compiler stamps an MVID and a timestamp into
every assembly. Everything else is fixed by the script. The SHA-256 printed at
the end identifies the binary that shipped.

**A clean checkout builds.** No binary is tracked: the `.ico` files are generated
from `assets\*.png` by `tools\make-icon.ps1` on the first build, and the `.exe`
files, the reports, and the settings file are all ignored. Nothing in the
repository is a build output except the words describing one.

---

## Verifying it yourself

Nothing here is on trust.

```powershell
# 1. reference, with Abode Night View OFF
.\AbodeNightView.exe --baseline --focus

# 2. switch it on, then
.\AbodeNightView.exe --verify --focus

# any product:
.\AbodeNightView.exe --verify --focus --product=illustrator
```

`--verify` checks the resolved rectangle, the extended styles, the layered alpha,
that it is not topmost, that it is not the foreground window, that it is above
the frame, that it is owned by the frame, that hit-testing falls through at nine
points, that the application's own popups are above it, that the canvas dimmed to
the predicted k, and that chrome outside the canvas did not change at all.

It **refuses to produce a result it cannot stand behind**: it aborts if you took
the baseline with the overlay already on, and if something is covering the canvas.

```powershell
.\AbodeNightView.exe --watch=25    # then click, drag, type for 25 seconds
```

logs every transition with a duration, and reports count / median / p95 / p99 /
max plus how many exceeded the frame budget at 30, 60, 120, and 144 Hz. Idle with
four applications tracked: **0 transitions, 0 undimmed intervals.**

Development-tree tools:

```powershell
.\Audit.exe               # 56 mechanical checks against windows it owns
.\Audit.exe --selftest    # 310 checks: hotkeys, settings, migration, adapters,
                          #   the schedule, the notification, restart state,
                          #   dialog layout, text measurement, text spacing,
                          #   and the license notice
.\ProtoA.exe dump --proc=Illustrator
.\Transfer.exe off.png on.png
```

`Audit.exe` deliberately does not depend on Adobe. It builds its own windows —
including synthetic frames with real `OWL.Document` hierarchies — so geometry,
photometry, ownership, input transparency, lifecycle, and the whole multi-target
state machine are measured on demand rather than when the canvas happens to be
unobstructed.

---

## Performance

Idle, five Adobe applications tracked, five overlays live, measured over 60 s
against the 1.2.0 release binary:

    0.91 % of one core     49.9 MB working set     288 handles     6 threads

Switched off, with all five applications still open: **0.00 %**, 36.8 MB. Full
table and methodology in
[`measurements/performance.md`](../measurements/performance.md). Both figures
were taken on a machine that was also running a game, so treat them as an upper
bound rather than a floor.

Tracking is event-driven; the 250 ms timer is a safety net, not the mechanism.
There is no capture, no swap chain, and no per-frame work — the overlays are
ordinary layered windows and their cost is a blend DWM was already doing.

---

## Distributing it

`dist\AbodeNightView.exe` is the whole distribution. Hand a tester that one file.

It is **unsigned**, so SmartScreen will warn on first run: *More info* → *Run
anyway*. There is no way around that short of a code-signing certificate. The
binary does carry a full Win32 version block — product, description, version,
copyright — because an unsigned binary that says who and what it is looks
considerably less suspicious than one that says nothing.

Settings land beside the exe, so the folder is portable: copy it to a USB stick
and it keeps its configuration. Put it somewhere unwritable and it falls back to
`%APPDATA%\AbodeNightView\` and says so in `--diagnostics`.

---

## Still worth doing by hand

Things that need a person at the keyboard, which no amount of harness replaces.

Already done, and recorded as **manual visual validation** rather than as a
measurement — a person looked at the screen and reported what they saw:

- multi-Adobe targeting behaves as expected across the four applications open at
  once;
- the tray state and menu behavior were inspected;
- **125 % display scaling showed no visible defect.**

Still outstanding:

- **A real editing session under `--watch=25`.** Click, drag frames, edit text,
  open menus and dialogs, float and dock panels, switch documents, move the
  window between monitors. Anything that flashes leaves a timed line. Idle is
  clean; a working session is the test that matters.
- **A monitor at 150 %, 4K, or a mixed-DPI pair.** The code is scaling-independent
  by construction; 100 % is measured and 125 % has been eyeballed.
- **Toggle InDesign's GPU Performance off** and re-run `--probe` — canvas
  *discovery* in CPU mode is the one thing the renderer-independence measurement
  does not cover.
- `tests\verify-document-safety.ps1` before and after a session, plus the control
  run, to confirm from outside that nothing touched the file.

---

## Repository layout

```
src/
  Common.cs               Win32 layer, DPI cascade, ViewportLocator
  Targets.cs              Adobe adapters, registry, discovery, --probe
  AbodeNightView.cs       overlay window, controller, tray, entry point
  Settings.cs             portable-first .ini, migration from Night View 1.0
  ProductPrefs.cs         what the settings say about one product
  UiState.cs              tray text, mode normalization, About and the license
                          notice - pure functions
  Schedule.cs             wall-clock range arithmetic - pure functions
  Balloon.cs              the notification, and the artwork on it
  Hotkeys.cs              parser, manager, live-layout AltGr probe, editor dialog,
                          and RichLabel - a label that lays out its own words so
                          a key name can be italicised inside a sentence
  Diagnostics.cs          the --diagnostics report
  Verify_Overlay.cs       --verify / --baseline / --watch / --shot
  VerifyMain.cs           entry point for the standalone Verify.exe; the verifier
                          is compiled twice from one source
  AssemblyInfo.cs         assembly and Win32 VERSIONINFO metadata
  Harness.cs              synthetic Adobe-shaped windows (dev only)
  Audit.cs                the mechanical harness (dev only)
  SelfTest.cs             hotkeys, settings, migration, adapters, schedule (dev only)
  ProtoA_HwndInspector.cs window hierarchy dumper (dev only)
  Transfer_Curve.cs       per-level transfer function (dev only)
  TestTarget.cs           single controllable target window (dev only)

experiments/
  ProtoC_MagnifierOverlay.cs   the Magnification API rig that settled Greyscale.
                          Research, not product: nothing here ships

measurements/
  magnification-api.md    what the Magnification API actually does
  performance.md          idle and active overhead
  rulers.md               which products dim their rulers, and why

tests/                    document-safety scripts
tools/make-icon.ps1       regenerates an .ico from a source PNG, at the sizes
                          asked for. Builds three: the application icon from
                          assets/source-icon.png, and the two notification icons
                          from source-icon.png and source-icon-cool.png
docs/                     this documentation and the screenshots it shows
```

`FEASIBILITY.md` is the engineering log — what was tried and what it measured.
`MACOS.md` is a shelved macOS feasibility study. Neither is required reading to
work on the utility; both are the record of why it looks like this.
