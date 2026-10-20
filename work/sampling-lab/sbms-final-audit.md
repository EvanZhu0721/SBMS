# Current SBMS baseline and final sampling proposal audit

2026-10-20. Read-only source and evidence audit; this report is the only file written by this subtask. Product code was not changed. Sources were read from the current worktree, not inferred from earlier conclusions.

## Decision

- Keep **1:1 as a pixel-preserving bypass**, including bypassing the defringing prepass. At zero motion this must equal the raw source. Integer motion may relocate pixels; it must not recolor them.
- For the experiment's non-identity cases, keep the existing candidate rather than retuning again: selective source defringing; pure enlargement uses EWA Lanczos4sharpest with its existing 0.8 soft-extrema anti-ringing; any shrinking axis uses scale-expanded separable Lanczos3.
- This finalizes the candidate being compared, not a universal production-quality guarantee. Existing false positives, the bounded downscale support, and native GPU cost remain material limitations.

## Current product versus experiment

| Property | Current product evidence | Experiment evidence | Conclusion |
|---|---|---|---|
| Area gate | `src/gpu_renderer.rs:150`: both axes satisfy `1.0001 < source/target <= 2` | `dynamic-filter.js:121`, `sbms()` at approximately line 370, and `sampleAt()` at approximately line 625 | Same gate mathematically; only one shrinking axis does not trigger current SBMS area filtering. |
| Pixel phase | Full-screen vertex UVs at `gpu_renderer.rs:62`; pixel centers and `floor(SV_POSITION)` at lines 148–155 | `(target + .5) * scale - .5 + motion`, approximately line 464; same scalar formula in `sampleAt()` | Same center alignment when motion is zero, preview origin is zero, and preview zoom is one. Preview zoom relocates already selected output pixels; it is not the product transform. |
| Area weights | `gpu_renderer.rs:77–116`: three possible coverage intervals per axis, represented using two bilinear lookups per axis | `dynamic-filter.js` `area()`: explicit up-to-3×3 texel coverage; CPU loops over actual covered texels | Same mathematical integral in the supported scale interval. Arithmetic and hardware interpolation are not bit-identical implementations. |
| Fringe suppression | `gpu_renderer.rs:119–145`: horizontal source offsets ±0.5, 0.2126/0.7152/0.0722 luma, 0.04/0.18 neutral gate, delta multiplier 8, strength 0.85 | `dynamic-filter.js` `sbms()` and scalar `sampleAt('sbms')` | Formula and offsets agree. This is the **old product** suppression; it is separate from the new source prepass. |
| Border | `gpu_renderer.rs:460–465`: hardware linear sampler, U/V/W CLAMP | `dynamic-filter.js:340`: modulo indexing; scalar fetch likewise wraps | **Not equivalent at image edges.** Experiment repeats a periodic moving atlas. It must not be described as a full-frame byte-exact product reproduction. |
| Source/output encoding | Product back buffer `B8G8R8A8_UNORM`, alpha ignored, `gpu_renderer.rs:415–424`; source SRV is created from acquired texture description at line 650 onwards | Byte RGBA source, color conversion disabled on upload; `dynamic.js:402–404`; source prepass rendered to `RGBA8` at line 369 | Encoded RGB/8-bit storage model agrees for the tested opaque SDR assets. BGRA versus RGBA storage ordering is not a different RGB formula. HDR, compositor, driver presentation, and color-management equivalence are not established. |
| Numerical precision | HLSL float32 and hardware bilinear sample | GLSL highp/manual texel interpolation; JavaScript scalar uses double precision | Do not promise bit equality with D3D hardware. RGBA8 prepass introduces an additional quantization step in the final candidate. |
| Displayed baseline wiring | Product calls its actual `pixel_main` | Earlier `dynamic-filter.js` main called HQ on both sides. During this audit the parent added `u_compareSbms` and an explicit `sbms(target, center, scale)` call at line 470 | The former raw-HQ left side was not SBMS. New screenshots must be generated after the explicit SBMS branch is built. |

Line numbers refer to the file version read during the audit. The parent is actively editing the experiment, so function names are the stable locator.

## Independent scalar cross-check

Ran Node against the current `DynamicFilter.sampleAt('sbms')`, using a separately written emulation of the HLSL's **two weighted bilinear lookups per axis**, with clamp addressing. No helper from the experimental area function was reused.

Input: 24×18 opaque random RGBA bytes; LCG seed 20261020, recurrence `seed = (1664525 * seed + 1013904223) >>> 0`, each byte `seed >>> 24`, then alpha forced to 255. Motion zero. Error is maximum absolute normalized RGB difference. Interior excludes two output pixels on each side.

| Source → target | Gate | Whole-image max difference | Interior max difference | Pixels with difference > 1e-10 |
|---|---|---:|---:|---:|
| 24×18 → 24×18 | Bilinear | 0 | 0 | 0 |
| 24×18 → 48×36 | Bilinear | 0.2149509804 | 0 | 164 |
| 24×18 → 36×27 | Bilinear | 0.1445533769 | 0 | 122 |
| 24×18 → 18×14 | Area + old fringe suppression | 0.0325113435 | 2.56e-15 | 8 |
| 24×18 → 12×9 | Area + old fringe suppression | 1.12e-16 | 1.12e-16 | 0 |
| 24×18 → 8×6 | Bilinear | 0 | 0 | 0 |
| 24×18 → 18×18 | Bilinear | 0 | 0 | 0 |
| 24×18 → 18×27 | Bilinear | 0.1071895425 | 0 | 36 |

The large differences are repeat-versus-clamp edge samples, not internal kernel disagreements. This is a mathematical cross-check, not execution of the product shader. GDI text rows well inside the atlas can be used for an explicitly bounded interior comparison.

## Why identity should bypass defringing

1:1 introduces no scaling-related RGB subpixel displacement. A ClearType source that matches its destination panel can legitimately depend on those RGB coverages. Changing them at 1:1 is a separate image treatment, not required resampling.

The existing `native-dpi-verification.json:3249–3388` records the previous identity treatment changing 3,363 pixels in the selected six GDI rows; chroma sum changes from 603,368 to 300,158. That shows real recoloring, not a free correction. These are historical pre-bypass measurements and must not be copied into the new final comparison as current behavior.

Current false-positive evidence is also nonzero: `stroke-verification.json:1–40` records six `newStrong` pixels for the 16 px VLC icon on dark background. The same number in old/new means no added regression from the latest refinement; it does **not** mean the treatment preserved all authored colors. The 114-case summary at line 18483 likewise reports no additional changed cases, not zero source modification. The inherited astronaut CPU/GPU difference is documented at line 18442.

A destination with a different physical subpixel arrangement can still make 1:1 ClearType undesirable, but the current experiment has no trustworthy detector for that condition. It does not justify applying selective desaturation to every 1:1 image by default.

## What the comparison can establish

- **Residual color on known neutral text:** use the same complete GDI row ROI, same input bytes, same scale, and same output pixel phase. Report `max(R,G,B)-min(R,G,B)` sum and maximum as residual chroma, not as a universal quality score.
- **Gray preservation:** a grayscale source should remain grayscale; at identity it should also remain byte-identical. For scaled gray text, luminance differences between baseline and final are expected from different kernels and are not themselves recoloring.
- **Authored-color cost:** inspect and measure real icons and photos against their own input or the same kernel without the prepass. “No additional regressions” must stay separate from “no false positives.”
- **Ringing:** measure dark/bright excursions around an isolated, known step and inspect halos on text. Final 0…1 clipping hides out-of-range overshoot but does not remove in-range gray/color rings.
- **Stroke cost:** show aligned row crops, thin-stroke survival, apparent width/contrast, and background contamination. The prepass preserves encoded Y′ before quantization, not guaranteed perceptual or linear-light luminance; the resampling kernel can change the stroke profile.
- **DPI:** independently rasterized GDI sources are appropriate. Different DPI values can change hinting, glyph width, shape, and phase. A direct 100% GDI raster is a visual reference, not a per-pixel truth image for SSIM/PSNR ranking of a raster created at another DPI.

## Remaining limits for a product port

- `prefiltered()` has 20 weights per axis (`dynamic-filter.js:440`) and `maxDownscale: 3`. It is a bounded experimental implementation, not support for arbitrary shrink factors.
- The source prepass contains a connected-stroke walk up to 16 source pixels. Successful static GPU rendering does not establish acceptable full-frame cost, latency, or motion stability.
- Product border addressing should remain clamp. Repeating the atlas is useful for the demo but must not leak into a product shader.
- Keep new SBMS-vs-final evidence separate from previous raw-HQ-vs-clean-HQ and previous-refinement comparisons.

No product source, UI source, tests, or generated images were modified by this audit.

## Final wiring and documentation recheck

Re-read the parent's updated files after the decision above:

- `dynamic-filter.js:467` sets `useCleanSource` false on the right at native scale when `u_nativePassthrough` is enabled.
- `dynamic-filter.js:468` restricts `useLegacySource` to the left side. The native right side therefore reads the original source, not an old cleaned texture, even when the selected reference is legacy.
- `dynamic-filter.js:470` explicitly routes the left side to `sbms()` when `u_compareSbms` is enabled. Both source-selection flags are false for a normal SBMS reference, so this uses the original source.
- `dynamic.js:483–485` uploads mutually selected legacy/SBMS reference flags and enables native passthrough for normal pages; historical test pages retain their historical treatment.
- `docs/sampling-quality.md` agrees with the recommended rules, distinguishes the formula comparison from product GPU execution, states the repeat/clamp difference, bounds the current shrink factor, and avoids interpreting cross-DPI glyph differences as error truth.
- Direct string comparison found `sbms-current-baseline.hlsl` equal to the current `src/gpu_renderer.rs` shader body after stripping outer whitespace. No hash was used.

These checks establish source wiring and documentation consistency. The parent still needs its newly generated screenshots and browser verification to establish executed behavior.
