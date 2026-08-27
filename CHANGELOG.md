# Changelog

All notable changes to Abode Night View. The engineering log — what was tried,
what it measured, and what was rejected on the strength of that — is in
[FEASIBILITY.md](FEASIBILITY.md); this file is the short version.

Versions follow [semantic versioning](https://semver.org/). The version is
written in exactly one place, `src/AssemblyInfo.cs`, and everything else —
`--version`, the About box, the tray header, Explorer's Details tab — reads it
back out of the built binary's own version resource.

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
