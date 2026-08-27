# Windows Magnification API — measured behavior

Machine: Windows 11 Pro 26200.8457 x64, 2026-08-21.
Source window: Adobe InDesign 21.5.1 document canvas (`DroverLord - Window Class`,
59,148 2063x1227, GPU Preview on). Capture region 400,500 800x500 (400,000 px),
entirely inside the canvas. Tool: `ProtoC.exe` (WC_MAGNIFIER at 1.0x in a
click-through layered host) + `Verify.exe --shot` + a pixel differ.

Every run: `--exclude=filter --zmode=topmost --interval=16`, capture taken 5–6 s
after start.

| mode        | matrix asked for                | measured result                                   |
|-------------|---------------------------------|---------------------------------------------------|
| `identity`  | I                               | **0 of 400,000 pixels differ.** Pixel-exact.      |
| `warm k=.45`| diag(0.450, 0.405, 0.248)       | per-channel ratios **0.4468 / 0.4033 / 0.2403**   |
| `invert`    | -k on diag, +k in row 4         | mean luma 113.06 -> 49.48                          |
| `gray k=1`  | Rec.709 mixing, all off-diagonal| **no desaturation**: mean sat 59.19 -> 58.92, 99.15 % of pixels byte-identical |
| `gray k=.45`| Rec.709 mixing x 0.45           | **neutral dim**: RGB x 0.4503/0.4487/0.4501, mean sat 59.19 -> 26.51 (= x0.45) |

## What this establishes

1. **1.0x magnification does not resample.** Identity is byte-for-byte identical
   over 400,000 pixels. Text stays sharp; this was an open question.

2. **The diagonal and the translation row of MAGCOLOREFFECT are honoured exactly.**
   Three different per-channel gains came back within 0.4 % of what was asked,
   and the additive row produced a correct inversion.

3. **Channel mixing did not happen.** A pure Rec.709 grayscale matrix behaved as a
   *neutral* gain of k with saturation fully preserved and scaled by k. Every one
   of the five results is consistent with the implementation collapsing each
   color column to its sum and applying it as a per-channel gain — which is
   exactly "affine per channel, no mixing".

   This is the whole reason Greyscale cannot be built on this API here: the one
   Windows primitive that could produce grayscale without capturing and redrawing
   the viewport did not produce grayscale.

   Scope of the claim: one machine, one GPU, one Windows build. It is enough to
   refuse to ship the mode, not enough to say the API can never do it.

4. **Recursion is solved by `MagSetWindowFilterList(MW_FILTERMODE_EXCLUDE)`.**
   Returned TRUE; no mirror artifact in any capture.
   `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` also works and additionally
   makes the host invisible to GDI screen capture — which is why the first
   attempt at this measurement read "no change" for every mode.

5. **Cost, idle, over a 2063x1227 source:** 36–38 refreshes/s against a 16 ms
   timer, 0.1–0.3 % CPU, 68–71 MB working set.

6. **Latency is structural, not incidental.** There is no "the source repainted"
   notification, so the host is refreshed on a timer. It therefore renders a copy
   that is up to one timer period (measured ~27 ms) behind the window underneath,
   and is not synchronized to it. The Neutral overlay has no such term: DWM
   composites it with the target in the same frame.
