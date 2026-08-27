# Idle and active overhead

Machine: Windows 11 Pro 26200.8457, 28 logical processors, 3 monitors.
Build: AbodeNightView.exe 1.2.0, x64, .NET Framework 4.x.

Adobe applications open throughout: InDesign 21.5.1, Illustrator 30.7.0,
InCopy 21.5.1, Photoshop 27.9, Acrobat 26.1. The 1.2.0 rows were taken with a
document open in all five, so five viewports were tracked; the 1.1.0 rows below
them had four, because InCopy had nothing open.

The 1.2.0 rows were also taken on a machine simultaneously running a game and a
dozen other applications. Read them as an upper bound.

CPU is measured as the delta in `Process.TotalProcessorTime` over a fixed
wall-clock window and expressed as a percentage of ONE core. The counter has a
~15.6 ms tick, so a 15 s window resolves to about 0.1 %; the 60 s row is the one
to quote.

| state                              | window | CPU (one core) | working set | private | handles | threads |
|------------------------------------|--------|----------------|-------------|---------|---------|---------|
| **1.2.0** 5 targets, 5 overlays, idle | 60 s | **0.91 %**     | 49.9 MB     | --      | 288     | 6       |
| **1.2.0** globally OFF, 5 applications open | 60 s | **0.00 %** | 36.8 MB   | --      | 282     | 6       |
| 4 targets, 4 overlays, idle        | 60 s   | **0.55 %**     | 49.1 MB     | 33.1 MB | 287     | 9       |
| 4 targets, 4 overlays, idle        | 15 s   | 0.63 %         | 49.2 MB     | 33.1 MB | 287     | 9       |
| 1 target, 1 overlay, idle          | 15 s   | 1.04 %         | 47.9 MB     | 32.5 MB | 285     | 7       |
| no product selected (`--products=none`) | 15 s | 0.00 %      | 36.9 MB     | 25.5 MB | 280     | 7       |
| globally OFF, 4 applications open  | 15 s   | 0.00 %         | 36.7 MB     | 26.1 MB | 280     | 7       |

The 1-target row reading *higher* than the 4-target row is measurement noise at
this resolution, not a real inversion; both are under 1 % of one core. What the
table does establish is the shape: tracking is event-driven, so the cost is
roughly flat in the number of targets, and it goes to zero — not to "small" —
when there is nothing to dim.

## Where the work happens

* one 250 ms safety-net timer, always
* one global WinEvent hook (foreground / move-size / minimize)
* **one WinEvent hook per tracked process**, added and removed as applications
  start and quit: 5 processes tracked in the 1.2.0 rows, so 6 hooks in total
* one `EnumWindows` pass per 250 ms tick to notice applications appearing and
  disappearing (~200 top-level windows, class name each)
* a full descendant walk per tracked frame every ~2 s, or immediately when a
  structural event says the viewport may have changed. InDesign has 545
  descendants, Photoshop 657, Illustrator 397, Acrobat 31.

Between those walks the cached viewport handles are revalidated with three cheap
calls each (`IsWindow`, `IsWindowVisible`, `GetWindowRect`).

## GPU

Not separately measurable here: the overlays are ordinary layered windows, so
their cost is a blend DWM was already doing for the desktop. There is no
capture, no swap chain, and no per-frame work of our own — which is precisely
the difference between this and the Magnification-API path measured in
`magnification-api.md`, which redraws a copy 36–38 times a second whether
anything changed or not.

## Overlay count

One overlay per *visible document viewport*, not per product and not per
application:

* Illustrator with one document open -> 1
* an application showing two documents side by side (Window > Arrange) -> 2
* four applications, one document each -> 4
* nothing open -> 0, and the pool keeps at most one spare window

Measured mechanically in `Audit.exe` (Multiple targets), where opening and
closing documents and applications moves the count up and down and back to zero.
