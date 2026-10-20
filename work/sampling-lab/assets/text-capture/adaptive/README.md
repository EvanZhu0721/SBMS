# Native text at constant output size

These 14 PNGs are independent captures of the native GDI text window used by the sampling lab. Each sampling scenario selects a freshly rasterized source, with font and layout DPI computed from its vertical output/source ratio:

```text
sourceDpi = 96 / outputScaleY
nominalSourceEm = 9 pt × sourceDpi / 72
actualSourceEm = round(nominalSourceEm)
actualOutputEm = actualSourceEm × outputScaleY
```

The nominal output font is always 9 pt at 96 DPI, or a 12-pixel em height. No source PNG is an enlarged or reduced copy of another. The lab should upload the selected PNG at its original pixel dimensions and apply its normal sampling pipeline only once.

| `scaleKey` | Vertical sampling ratio | Source DPI | Native PNG | Source em, nominal → GDI integer | Actual output em | ClearType colored pixels |
|---|---:|---:|---:|---:|---:|---:|
| `50` | 2× | 48 | 280 × 120 | 6 → 6 px | 12 px | 2,438 |
| `66_667` | 1.5× | 64 | 373 × 160 | 8 → 8 px | 12 px | 3,850 |
| `100` | 1× | 96 | 560 × 240 | 12 → 12 px | 12 px | 6,306 |
| `133_333` | 0.75× | 128 | 747 × 320 | 16 → 16 px | 12 px | 8,580 |
| `200` | 0.5× | 192 | 1120 × 480 | 24 → 24 px | 12 px | 16,186 |
| `240` | 5/12× | 230.4 | 1344 × 576 | 28.8 → 29 px | 12.0833 px | 18,696 |
| `300` | 1/3× | 288 | 1680 × 720 | 36 → 36 px | 12 px | 24,340 |

`100` also serves horizontal-only 0.75× sampling, where vertical scale remains 1×. These mappings preserve nominal output height; they do not cancel an intentionally anisotropic horizontal transform.

The `240` source necessarily rounds a 28.8-pixel requested em to 29 pixels in this GDI implementation. Its output is therefore 0.0833 px, or about 0.694%, larger than the nominal 12 px. Glyph hinting, integer advances, and rounded layout coordinates may also vary between source rasterizations; equal nominal em height is not a promise of identical letter widths or identical final glyph pixels.

## Capture and evidence

`capture-native-text.cs` creates a non-activating, offscreen WinForms window and draws the same six short rows through native `CreateFontW`/`DrawTextW`: Segoe UI English/numerals, Microsoft YaHei Chinese, and Consolas code, on light and dark backgrounds. `PrintWindow(PW_CLIENTONLY)` captures each real window. The helper does not resize captured bitmaps, manually construct RGB subpixels, change system display DPI, change global ClearType settings, or use mouse/keyboard input.

The unchanged host window DPI is 192. `targetDpi` describes this capture's font/layout rasterization, not a changed desktop setting. `dpi` is the corresponding source percentage. `fontHeightPx` at asset level is the positive integer source em height; `regions[].fontHeightPx` retains the negative GDI `lfHeight` convention. `focus` comes from GDI text measurements around `Il1|`, not the blank center of the canvas.

Each ClearType PNG has an independently rendered `ANTIALIASED_QUALITY` grayscale reference. These references contain zero pixels with unequal R, G, and B; they are not desaturated copies. GDI can choose different hinting/weight for grayscale and ClearType, so the grayscale result is a no-color comparison, not an exact per-pixel luminance target.

All 14 window captures succeeded and match their manifest dimensions. All seven ClearType captures contain genuine unequal-channel pixels; the table counts pixels whose maximum-minus-minimum RGB channel value is at least 4. The 50%, 133.333%, 240%, and 300% ClearType captures were visually checked and are unclipped. Text in the 50% source really is only a 6-pixel em, so its limited detail is an input-resolution limit rather than a screenshot resize. The new 100% ClearType and grayscale captures match the corresponding retained fixed-size captures pixel for pixel.

## Reproduce

From the parent `text-capture` directory:

```powershell
powershell.exe -NoLogo -NoProfile -NonInteractive -File .\capture-native-text.ps1 -Adaptive
```

This mode writes only this `adaptive` directory. The default script mode and `Runner.Run()` still produce the original fixed 100%/125%/150% captures; `Runner.RunAdaptive()` produces this set. No original fixed PNG, manifest, or README was regenerated for this addition.

Windows PowerShell 5.1 is intentional because the capture helper uses the installed .NET Framework WinForms/System.Drawing assemblies. Regenerated pixels may depend on installed fonts, Windows version, and local rendering settings. Every helper window is closed and the process exits after capture.
