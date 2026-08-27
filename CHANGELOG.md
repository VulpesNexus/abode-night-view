# Changelog

All notable changes to Abode Night View. The engineering log — what was tried,
what it measured, and what was rejected on the strength of that — is in
[FEASIBILITY.md](FEASIBILITY.md); this file is the short version.

Versions follow [semantic versioning](https://semver.org/). The version is
written in exactly one place, `src/AssemblyInfo.cs`, and everything else —
`--version`, the About box, the tray header, Explorer's Details tab — reads it
back out of the built binary's own version resource.

---

## Unreleased

Nothing in the program changed. This is the build and the documents: the first
day the build ran anywhere but the machine it was written on, and a pass over
what the repository would say about that machine once it was published.

### Fixed

- **The About screenshot showed the stock Windows application icon.** The
  program was never at fault. `AppIcon` reads the picture out of the running
  executable's own Win32 icon group, and the screenshot harness is built by
  hand, without `-win32icon` — so it had no icon group, `LoadImage` returned
  NULL, `ExtractAssociatedIcon` fell back to the stock picture, and the About
  box faithfully drew what it was handed. Established by reading the shipped
  binary's resources directly (icon group 32512, entries at every size asked
  for) and by rendering what `AppIcon` actually returns, rather than by
  inspecting the code and believing it. The harness now embeds the icon.
  `about.png` is the only screenshot that changed: it is the only window that
  paints the icon into its body rather than leaving it to the title bar.
- **The release build's portable-settings check failed on every GitHub-hosted
  Windows runner**, whose `%TEMP%` is an 8.3 short path: `C:\Users\RUNNER~1\`
  `AppData\Local\Temp`. The check built the path it expected out of `%TEMP%`
  and compared it as a string against the path the program reports — and
  Windows hands a process the *long* form of its own module path. Two spellings
  of one directory, and the program was right in both of them. The directory is
  now established by identity rather than by spelling: a marker file is written
  beside the executable and looked for in the directory `--diagnostics` names.
  The failure quotes both paths now, too, because the first machine it ever
  failed on is one nobody can log into.

### Changed

- **`assets/source-icon-cool.png` no longer carries an XMP packet.** Saving it
  out of Photoshop wrote the editing software and its version, two timestamps
  carrying a UTC offset, and the document lineage identifiers into the file.
  None of that is artwork, and all of it would have been published along with
  the picture. The metadata chunk was cut out of the PNG stream rather than the
  image being re-encoded, so the pixels are provably untouched rather than
  probably untouched: 0 of 50,304 differ, and the `.ico` built from it after is
  byte for byte the one built before. `source-icon.png` never had a packet.
- Five double hyphens standing in for em dashes, in prose, are em dashes.
- Six numeric ranges written with a hyphen are en dashes, which is how every
  other range in these documents is written — `36-38 refreshes/s` in
  `measurements\` was the same measurement as `36–38 refreshes/s` in
  FEASIBILITY.md, spelled two ways in two files.

---

## 1.4.0

The tray icon carries the state; the two places that report the state stop
reporting a strength when nothing is being dimmed; and the Strength window and
the hover text each say one thing less.

### Added

- **The tray icon follows the state**: the plain artwork when the dimming is
  off, the same character in sunglasses when it is on. The notification balloon
  has carried that pair since 1.3.0; the icon that raised the notification did
  not, so the toast and the tray could be showing two different faces for one
  state. Both now resolve through the same function, and the two `.ico`
  resources are named `state-off` / `state-on` for what they are rather than for
  the one thing that used to read them.
- The state artwork gained 16, 20 and 24 px entries. It carried nothing below
  32, which is what a balloon asks for; a notification area asks for
  SM_CXSMICON, and an `.ico` whose smallest entry is 32 px is a tray icon the
  shell has to halve — and halving a two-pixel sunglasses bar is how the on
  state stops being distinguishable at a glance. The release build reads the
  sizes back out of the generated containers rather than trusting the list that
  generated them.
- **The README shows what the utility does**, rather than only what its windows
  look like: the same InDesign page white and then grey with the toolbars,
  panels and rulers pixel-identical either side, and the other four products
  beside it. Sampling those captures gives 254.8 → 204.2, a ratio of 0.802
  against the 0.800 the Strength window states.

### Changed

- **Switched off, the hover text is `Abode Night View: [OFF]` and nothing
  else.** It used to carry the strength as well, which is a number describing an
  effect that is not being applied to anything: the hover read `55%` over a
  screen that was not dimmed at all. A stored setting is not a state. The value
  is still one click away on the Strength item, where it is being read as a
  setting rather than as a description of the screen.
- **Switched off, the notification is `[OFF]` and nothing else** — no
  percentage, and no `k`. k is the multiplier the compositor applies, which is
  exactly why it leaves with the percentage: switched off there is no multiply,
  so `k = 0.45` was naming a coefficient nothing was multiplying by, on the one
  notification whose whole job is to say the dimming has stopped.
- Both of those came from one composite format string with `ON`/`OFF`
  substituted into it, so the strength was structurally impossible to omit. The
  release build now fails if either format literal is still in the artifact.
- **The hover no longer reports an application with no document open.** It is
  the most ordinary thing an Adobe application can be doing — sitting on its own
  home screen, between jobs — and where the Targets menu says it about one named
  product among several, the hover was saying it about the desktop as a whole,
  where it distinguished nothing. `no target`, `unsupported version` and
  `nothing to dim` remain; the fourth case is now silence, which is what
  "nothing is wrong" should look like. A running product this build cannot read
  also now outranks it, where it used to lose to it.
- **The Strength window puts the setting above the slider and its consequence
  below it**: `20% (k = 0.80)` over the control, `255 (pure white) now displays
  as 204.` under it, in grey. All three readings used to sit on one line above
  the slider, pipe-separated, which asks the eye to do the splitting; and the
  percentage and k are one fact in two units, so they belong together while the
  sentence about a pixel belongs next to the thing that changes it. "20% dim"
  lost the word "dim" — the sentence underneath already says which way the
  number runs, on a window titled Strength with one control in it. The window is
  a third narrower as a result, because its width was set by that long line.
- Both Strength strings moved into `TrayState`, which is where every other line
  the user reads already lives. They were the last two still being formatted at
  the point of display, and therefore the last two no test could read.
- `--diagnostics` calls its icon rows `window icon` and `state artwork`, and
  adds `tray icon shows`. The row labelled `tray icon` reported the source the
  tray no longer uses.
- The release script deletes the generated `.ico` files and lets `build.ps1`
  regenerate them, instead of regenerating them itself with its own copy of the
  size list. The two lists had already been written down twice; the release
  script's copy would have silently overridden the build's and shipped 1.4.0
  without the sizes the tray icon needs.

---

## 1.3.0

First public release.

### Added

- **A schedule.** On and off by the clock, on a range you set, and edge
  triggered — switching it off by hand at 01:00 stays off until the range next
  begins or ends, instead of being undone by the resync timer a quarter of a
  second later.
- **Configurable shortcuts**, with an editor that tells you what each
  combination would cost you on *your* keyboard: whether it is AltGr on this
  layout, and which character you would stop being able to type.
- **Notification artwork that carries the state** it is announcing — the plain
  icon when the dimming is off, the same character in sunglasses when it is on.
  Windows Forms offers four stock pictures for a balloon; the shell has taken an
  arbitrary one since Windows XP SP2, so the notification is raised through
  `Shell_NotifyIcon` directly, with a fall back to the ordinary balloon at every
  step and `--diagnostics` reporting which path was taken.
- **An About box** with the version, the author, the repository and the GNU GPL
  notice.
- **Four distinct answers to "why is nothing dimmed"** — not running, running
  and unreadable, running with no document open, attached — told apart
  structurally rather than guessed at, in the tray menu, the notification and
  `--probe`.

### Changed

- **No keyboard shortcut is bound out of the box.** The old `Ctrl+Alt` defaults
  were withdrawn: they are AltGr on a great many keyboards, and a global hotkey
  is taken from every program on the machine rather than only from Adobe.
- The tray tooltip and the notification no longer name the filter. With one
  filter left it was the same word in every possible state, and a third of a
  63-character budget. The room went to writing the product's name out in full.
- Everything a product has to say about itself now goes in one parenthesis after
  its name — `Photoshop 2026 (no document open)` — and the tray text is produced
  by pure functions, so every line the user reads can be asserted mechanically.
- **The Strength window reads `20% dim | 255 (pure white) displays as 204 | k =
  0.80`.** Three readings of one number, and the middle one now names its input
  as well as its output: `white 255 becomes 204` left the reader to work out
  which way the arithmetic ran. The window grew to fit the longest of them.

### Fixed

- **`--diagnostics` could stop on a message box nobody was there to click.**
  Writing the report and opening it were caught together, so a machine with no
  handler registered for `.txt` — a build agent, a stripped server install —
  was told the report could not be *written* when it had been, and told so in a
  modal dialog, on a command-line switch that runs unattended. The two are now
  caught separately: a failed open is silent, and only a failed write interrupts.
- **An overlay kept the owner link after it stopped using it.** Attaching makes
  the overlay an owned window of the Adobe frame; nothing ever took that back.
  A released overlay therefore stayed owned by a window it was no longer on, and
  since Windows destroys a window's owned windows along with the owner, the
  pooled spare was a window volunteered for destruction the next time that
  application quit — paid back as a create/destroy cycle the pool exists to
  avoid. The decision to re-own also trusted a cached handle, and window handles
  are recycled: a stale one matching a brand-new frame would skip the re-own and
  leave the overlay owned by whatever else had been given that handle. The link
  is now dropped on detach and before the window is destroyed, and the re-own
  asks the window rather than the cache. `Audit.exe` asserts both directions in
  its lifecycle section.
- **The hover text and the tray menu contradicted each other.** A Photoshop that
  was running, selected and showing no document was `Photoshop 2026 (no document
  open)` in the menu and `no target` on hover, at the same moment, about the same
  program. The two were answering different questions — the menu surveyed the
  desktop, the hover counted attached overlays and called zero of them "no
  target" — and the comment above the tooltip claimed they could not disagree.
  There is now one survey and two renderings of it, and the hover names the same
  four states the menu does: `no target`, `no document open`, `unsupported
  version`, `nothing to dim`.
- **The hover text went stale.** It was rebuilt only when the user clicked
  something, so opening a document left it reading `no target` until the next
  visit to the menu. It now also refreshes when the number of overlays or of
  detected frames changes.
- **An unticked product reported a different state from a ticked one.** The
  engine only tracks windows for products that are switched on, and anything
  else was assumed unhookable — so a perfectly healthy Photoshop sitting on its
  welcome screen was reported as an unsupported version.
- **The same product was called two different things** in the tray menu and in
  the notification, seconds apart, for the same reason.
- **The Schedule window counted down to something that was not going to
  happen.** Opening it with the schedule switched off read "Off now, until
  20:00". The range and what the schedule is doing about it are now two separate
  sentences, and the second one says "Schedule is currently off."
- **Runs of spaces inside labels.** Six places padded a separator by hand, and
  Photoshop's own `ProductName` resource ends in a space, which put a hole in
  every label built by concatenating it.
- **The About box icon was noise.** `Icon.ToBitmap()` cannot read a
  PNG-compressed `.ico` entry; it walks the payload as a device-independent
  bitmap. The icon is now painted through `Graphics.DrawIcon`, and the release
  build refuses a source tree that calls `ToBitmap` at all.
- Text in the Shortcuts window was cut off rather than wrapped, because an
  `AutoSize` label is exactly as wide as its longest hard-coded line.

---

## Earlier releases

Reconstructed from the notes and the code rather than from a changelog kept at
the time, so this is what can be established, not a complete record.

### 1.2.0 — removed

- **Greyscale** and **Shader**, after measurement rather than opinion. Neither
  can be done without capturing the screen, and capture costs a frame of latency
  by construction. See README, *Rejected rendering approaches*.
- **Warm.** Cheap, deterministic, and it did the wrong thing to colour.

Neutral is what is left, and it is what the compositor can do for free: a black
layered window at alpha *a*, composited as `out = src*a + dst*(1-a)`, which for
a black source is a per-channel multiply by `k = 1-a`.

A settings file naming `greyscale`, `shader` or `warm` still loads: the value is
normalised to `neutral` and written back, silently, because there is nothing the
user has to do about it.

### 1.1.0

`greyscale` and `shader` were selectable modes in this release. Settings carry
`schema=`, bumped whenever a key changes meaning rather than whenever a key is
added, and unknown keys are preserved through a round trip.

### 1.0.0 — Night View

One product, InDesign. Its `NightView.ini` is still imported on first run, and
its `target=` key — which named a *region* back when there was only one product
— becomes `region.indesign=`. The old file is left where it is, so downgrading
still works.
