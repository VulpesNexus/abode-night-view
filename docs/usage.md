# Usage

How to drive Abode Night View. For *why* it behaves this way, see
[Design notes](design-notes.md).

- [The tray menu](#the-tray-menu)
- [The schedule](#the-schedule)
- [Shortcuts](#shortcuts)
- [Command line](#command-line)
- [Settings](#settings)
- [Diagnostics, and what to send with a bug report](#diagnostics-and-what-to-send-with-a-bug-report)

---

## The tray menu

```
Abode Night View 1.4.1            <- click for About
──────────────────────────
✓ Enabled                          <- reads "Disabled", unticked, when off
✓ Schedule (20:00 – 07:00)  >      <- reads "(off)" when there is no schedule
      Off
    ✓ On from 20:00 to 07:00
      ──────────
      Set range...
──────────────────────────
Targets (4 selected, 3 running)  >
      ✓ Acrobat
      ✓ Illustrator 2026
      ✓ InDesign 2026
        Photoshop 2026 (no document open)
      ──────────
      Not running: InCopy

Region  >
      InDesign 2026  >   ✓ Canvas only / Document viewport / ...

Strength (55%)  >   ... ✓ 55% (k = 0.45) ...  Custom...
──────────────────────────
✓ Include in screen captures
──────────────────────────
Shortcuts...
Re-scan for Adobe applications
Diagnostics...
Probe Adobe applications...
──────────────────────────
Exit
```

The tray menu is drawn by Windows into a menu that only exists while a pointer
is held still, so it is written out here rather than photographed. Everything
else on this page is the window itself, rendered by the same code that shows it
to you.

**Targets** lists every product this utility knows about, alphabetically, with
the name read from the running executable's own `ProductName` resource — so a
release from a year nobody here has seen still appears correctly. Whatever there
is to say about a product goes in one parenthesis after its name:
`Photoshop 2026 (no document open)`, `InDesign 2027 (unsupported version)`,
`Illustrator 2026 (2 windows, no document open)`. **Not running:** appears only
when something compatible *is* running.

**Region** sets, per product, how much of the window the overlay covers:
**Canvas only**, **Document viewport** (adds rulers and scrollbars),
**Application client area**, or **Whole window**.

**Strength** puts the setting above the slider and its consequence below it:
`20% (k = 0.80)` over the control, and `255 (pure white) now displays as 204.`
under it. The percentage is what you chose; k is what the compositor multiplies
by.

**Every checkmark is computed when the menu opens**, from the live state.
Nothing stores a tick. The tooltip and the menu are produced by the same two
functions from the same values, so they cannot disagree — asserted mechanically
in `Audit.exe --selftest`.

The top line opens **About**: the version read out of the binary's own version
resource, who wrote it, a link to the repository, and the GNU GPL notice with
the license linked.

<p align="center">
  <img src="strength.png"  alt="The Strength window: a slider, with the percentage and the multiplier k above it and the value white 255 lands on below" width="296">
  <img src="about.png"     alt="The About window: product, version, author, repository, and the GNU GPL notice" width="446">
</p>

### The tray icon, the hover text, and the notification

**The tray icon and the notification both carry the artwork for the state** —
the plain icon when the dimming is off, the same character in sunglasses when it
is on — so the state is readable before any sentence under it has been, and from
the taskbar without hovering over anything at all.

Hover text is exactly:

    Abode Night View: [ON] | 55%
    Abode Night View: [OFF]

Switched on, a reason is appended when nothing is being dimmed *and there is
something worth saying about it*, because "on but doing nothing" is otherwise
indistinguishable from broken:

| what is true                                      | hover says            |
| ------------------------------------------------- | --------------------- |
| nothing selected is running                       | `no target`           |
| running, and this build cannot read its windows   | `unsupported version` |
| running and readable, but minimized or off-screen | `nothing to dim`      |
| running, and showing no document                  | *nothing*             |

The notification is one line of the same state:

    [ON] 55% (k = 0.45)
    [OFF]

It appears at launch, and whenever the switch is thrown **by hand** — the tray
item, a double-click on the icon, or a shortcut. The clock does not raise one: a
scheduled change at 03:00 is not worth waking anybody for.

---

## The schedule

A page of white paper at 2am is a lamp, and "2am" is a fact the machine already
knows. Tray → Schedule switches the dimming on and off by the clock, on a range
you set:

```
tray → Schedule → Set range...      On at [20:00]   Off at [07:00]
AbodeNightView.exe --schedule=20:00-07:00
AbodeNightView.exe --schedule=off
```

<p align="center">
  <img src="schedule.png" alt="The Schedule window: two spin fields for the range, and a sentence saying what the schedule is doing about it" width="436">
</p>

**It is off until you ask for it.** Putting a filter over somebody's artwork
unasked is the one thing a display-only tool must not do, so nothing switches
itself on until you have said so once.

**The range wraps.** 20:00 to 07:00 crosses midnight, which is the normal case
rather than the awkward one. The start minute is included and the end minute is
excluded, so nothing belongs to both ends.

**A manual override stands.** The schedule is *edge* triggered: it acts when the
answer to "should this be on now?" changes, not continuously. Switch it off by
hand at 01:00 and it stays off until 07:00 — a schedule that re-asserted itself
every quarter second would make the tray item look broken for half the night.

**The clock can move.** Daylight saving, a manual correction, a laptop waking up
and resynchronizing with a time server: each of those is a boundary that has
silently gone past. `SystemEvents.TimeChanged` re-evaluates rather than waiting
for the next one.

The Schedule item reads `Schedule  (20:00 – 07:00)` or `Schedule  (off)` before
the pointer ever reaches the drop-down, so the submenu carries the two choices
and the range editor and nothing else. The editor's own sentence is the longer
form:

    Dimming is set to switch on at 20:00 and off at 07:00.  On now, until 07:00 tomorrow.

The first half describes the range being edited and the second describes what
the schedule is doing about it — which, with the schedule switched off, is
`Schedule is currently off.` Those are two separate statements because a range
can be set up for later use while the schedule is not running, and a countdown
to a change that is not going to happen is worse than no sentence at all.

---

## Shortcuts

**Nothing is bound out of the box**, on purpose — see
[Why nothing is bound](design-notes.md#why-no-shortcut-is-bound-out-of-the-box).

Set your own in tray → Shortcuts. *Esc* cancels a row and *Backspace* clears it,
both of which the dialog says. Nothing pre-announces that a combination might be
refused; Apply reports the ones Windows actually refuses, at the moment it
refuses them.

<p align="center">
  <img src="shortcuts.png" alt="The Shortcuts window: four rows to click and press a combination into, all unbound out of the box" width="455">
</p>

The editor asks the **live keyboard layout** (`ToUnicodeEx`) what
`AltGr+<key>` actually types and quotes the character back:

    Ctrl+Alt+Q is AltGr+Q on this keyboard, which types "@". Taking it stops
    that being typable in any program while Abode Night View runs.

A combination that is free on *this* keyboard and is AltGr somewhere else still
gets the older, general warning — a copy passed to someone on another layout is
the case the first check cannot see.

**Upgrading from 1.2 removes the four old defaults.** `Ctrl+Alt+N`,
`Ctrl+Alt+Up`, `Ctrl+Alt+Down`, and `Ctrl+Alt+Q` are deleted from an existing
settings file **only where they are still exactly what 1.2 wrote** — those were
never a choice. A combination you set yourself is left alone, including `Ctrl+Alt+N` if you
deliberately bind it back. The notification at launch says how many were
withdrawn, so a key that stops working is never a silent change.

---

## Command line

```
AbodeNightView.exe                     start, using AbodeNightView.ini
AbodeNightView.exe --on                start switched on regardless of settings
AbodeNightView.exe --off
AbodeNightView.exe --schedule=20:00-07:00    switch on and off by the clock
AbodeNightView.exe --schedule=off      no schedule; switch it by hand
AbodeNightView.exe --strength=55       dim to 55% (k = 0.45)
AbodeNightView.exe --region=canvas     canvas | document | client | window, all products
AbodeNightView.exe --region.photoshop=document
AbodeNightView.exe --products=indesign,illustrator      only these, this run
AbodeNightView.exe --zmode=above       chase the z-order instead of owning the window
AbodeNightView.exe --capture=exclude   keep the overlays out of screenshots (Win10 2004+)
AbodeNightView.exe --pid=1234          track one process only

Diagnostics:
AbodeNightView.exe --version           one line: version | arch | CLR | Windows
AbodeNightView.exe --diagnostics       write and open AbodeNightView-diagnostics.txt
AbodeNightView.exe --probe             why did this Adobe version not attach?
AbodeNightView.exe --probe=photoshop
AbodeNightView.exe --verify            photometric and structural self-test
AbodeNightView.exe --baseline          capture the reference, switched OFF
AbodeNightView.exe --watch=25          log every overlay transition for 25 seconds
```

Product ids: `indesign`, `illustrator`, `incopy`, `photoshop`, `acrobat`.

`--version` is one line of pure ASCII, on purpose. It is the line that gets
pasted into a bug report, and it comes back through whatever codepage the
reporter's console happens to be set to:

    Abode Night View 1.4.1 | x64 | .NET Framework 4.0.30319.42000 | Windows 10.0.26200

**Output from the diagnostic subcommands can look empty from PowerShell.** This
is a GUI-subsystem binary, so double-clicking it never flashes a console — and
the cost of that is that a shell does not *wait* for it. Pipe or redirect and
the shell waits:

```powershell
.\AbodeNightView.exe --probe 2>&1 | Out-String -Width 200
Start-Process .\AbodeNightView.exe --version -NoNewWindow -Wait -RedirectStandardOutput out.txt
```

---

## Settings

`AbodeNightView.ini`, next to the executable. If that folder is not writable —
Program Files, a network share, a read-only download — it falls back to
`%APPDATA%\AbodeNightView\` rather than silently losing every change.

```ini
schema=3
enabled=1
strength=55
mode=neutral
captures=1
zmode=owned

schedule=0
schedule.from=20:00
schedule.to=07:00

target.indesign=1
target.illustrator=1
target.incopy=1
target.photoshop=0
target.acrobat=1

region.indesign=canvas
region.photoshop=document

hotkey.toggle=
hotkey.brighter=
hotkey.darker=
hotkey.quit=
```

An empty `hotkey.` value means nothing is bound, which is what ships. See
[Shortcuts](#shortcuts).

Everything you change through the tray is written here immediately and is the
default next time: enabled state, strength, mode, the schedule, per-product
on/off, per-product region, and the shortcuts.

**Coming from Night View 1.0?** A `NightView.ini` next to the executable (or in
`%APPDATA%\NightView\`) is imported automatically on first run. One key changed
meaning — `target=` named a *region* back when InDesign was the only product, so
it becomes `region.indesign=`. Everything else keeps its name and its value. The
old file is left where it is, so downgrading still works.

Unknown keys are preserved, not discarded: a settings file written by a newer
version survives a round trip through an older one. Invalid values are clamped or
rejected — an out-of-range strength used to go straight to the alpha and could
produce an opaque black rectangle over the canvas.

`mode=` is normalized on load. A file naming a mode this build cannot render —
1.1's `greyscale` or `shader`, a hand-edited value, a mode from some later
release — starts on `neutral` and is written back as `neutral` on the next save,
silently. There is no error, because there is nothing the user needs to do: the
only way either of those could be in a file is by typing it in, since neither was
ever selectable.

---

## Diagnostics, and what to send with a bug report

    AbodeNightView.exe --diagnostics

writes `AbodeNightView-diagnostics.txt`: Windows build, DPI awareness actually
applied, which optional APIs this machine has, every monitor with its rectangle
and scaling, every recognized Adobe application with its version and resolved
viewports, the live overlay slots, and the settings as loaded. No document
names, no file contents, no window titles other than the application frames.

    AbodeNightView.exe --probe

is the one to run when something *should* be dimmed and is not. Per product it
prints the process names and frame class it looks for, the structure it requires,
what it found, and — on failure — a short list of the visible window classes it
saw instead:

```
== Adobe InCopy  (id incopy)
   expected structure   frame class 'incopy' > ... > OWL.TabGroup > OWL.Document > inner view container
   product name         Adobe InCopy 2026
   product version      21.5.1
   viewports found      0
   VALIDATION           FAILED - the expected relationship was not found.
                        Most likely: no document is open. Open one and re-run.
                          visible descendant classes, largest first:
                            OWL.Dock                    x4    largest 42,71 2518x1285
                            ...
```

Attach both to an [issue](https://github.com/VulpesNexus/abode-night-view/issues).
