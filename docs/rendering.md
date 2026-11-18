# Rendering

Each mirror group has three options under the tune icon. Full-frame copy and
Static refresh are off by default. High-performance sampling is selected
automatically unless the profile explicitly sets it.
Changes are saved with that group's profile and apply when mapping starts.
Stop mapping before editing these options.

| Option | Off | On |
|---|---|---|
| High-performance sampling | Final quality filter described below | Original SBMS filter: area averaging and its existing chroma suppression for simultaneous reductions of up to 2×; bilinear otherwise |
| Full-frame copy | When a source copy is needed, copy changed regions; copy the whole frame on initialization or when region metadata is unavailable | Copy the entire acquired desktop frame |
| Static refresh | Present when new desktop pixels arrive | Also present the cached output at the target display's refresh rate while the desktop is unchanged |

These settings control SBMS's local mirror renderer. Stream-only groups send the
virtual screen directly to Sunshine and do not use these three rendering options.
Manual reference editing remains available through the pencil beside the shared
reference.

The automatic sampling default enumerates DXGI hardware adapters and queries
D3D11's `D3D11_FEATURE_DATA_D3D11_OPTIONS2.UnifiedMemoryArchitecture`. It uses the
quality filter only if at least one hardware adapter reports `false`; otherwise
it enables high-performance sampling, including when detection fails or remains
unknown. This is a driver-reported heuristic, not a check of the GPU actually
rendering the output. The result is cached for the process lifetime, so hybrid
systems that render on the integrated GPU and GPU hot-plug changes may require
a manual choice. Restart SBMS to repeat detection.

With high-performance sampling, exact 1:1 output copies the acquired frame
directly to the output, using the output cache if static refresh is enabled.
For other sizes, when Full-frame copy is off and the acquired texture supports
a shader-resource view, the renderer samples it directly without a source copy.
Otherwise it uses the source-copy policy above.

## GPU routing

**GPU · All groups** selects the preferred GPU for the profile's virtual
displays. Automatic leaves that choice to Windows. SBMS captures and scales on
the source output's native adapter; the **Active** value comes from the driver's
actual device, not the saved preference. Change the preference while mapping is
stopped. A missing explicitly selected GPU blocks startup instead of silently
substituting another card.

The process-local native-routing hook prevents Windows application GPU
preferences from moving DXGI outputs away from their native adapters. Windows
Settings' per-application **Power saving / High performance** choice therefore
no longer forces SBMS's capture GPU; use SBMS's preference instead. Other
processes, including games and Sunshine, keep their own GPU selection behavior.
This does not change the sampling algorithm or its automatic default.

The hook uses an undocumented Windows export and has no OS compatibility
guarantee. Hook installation failures stop the graphics host with an error.
For troubleshooting, `SBMS_NATIVE_OUTPUT_ROUTING=0` restores the old routing
behavior on a fresh process. Set it in the actual host's environment: an existing
tray or an elevated host launched by a scheduled task does not inherit a
forwarding launcher's temporary environment. Mapping stop/start alone does not
change this process-lifetime policy.

## Quality filter

- Exact 1:1: selective source defringing without resampling.
- Any reduced axis: selective source defringing, then separable Lanczos3 with
  support widened independently for each axis.
- Enlargement only: selective source defringing, then EWA Lanczos4Sharpest
  luminance, bilinear chroma and antiringing strength 0.8.

The source cleanup is the finalized `refine=2` experiment, with strength 0.96
and a maximum 16-source-pixel connected-stroke search. It is quantized to RGBA8.
The separable reduction uses a floating-point intermediate to preserve negative
filter lobes. Edges clamp to the display boundary. Calculations use encoded RGB,
matching the experiment; this is not an HDR or linear-light pipeline.

The quality filter costs more GPU time and memory. Thin genuine color details
can become softer, and negative-lobe filters can produce brightness ringing.
The legacy switch remains available for workloads where that cost is undesirable.
Static refresh reuses the final GPU texture rather than rerunning the filter.

With the quality filter and changed-region copying, unchanged pixels also reuse
the previous filtered result. Changed rectangles include move destinations.
Defringing expands them by four source pixels horizontally and sixteen
vertically; each resampling pass then expands the affected area by its kernel
support. All passes retain the full-frame pixel coordinates and filter weights.
This changes the work performed, not the filtering algorithm.

The first frame, a source/target size change, unavailable region metadata, or
metadata exceeding 128 regions rerenders the whole frame.
The copy policy still copies the full source when reported rectangles cover
at least half its area, but retains those rectangles for partial filtering.
Enabling the Full-frame copy option explicitly keeps full-frame filtering.
Partial filtering uses a persistent output texture before copying to the
flip-discard presentation buffer; keeping that texture does not enable static
refresh. With static refresh off, full-frame updates render directly to the
presentation buffer without the extra copy. The first partial update after
that rebuilds the persistent result once. Small scrolling regions can benefit,
while full-screen scrolling still needs full-frame filtering.

## Verification

The opt-in native tests compare the production D3D11 pipeline with independently
rendered WebGL reference images, using real DPI-specific GDI text, true-color
icons, RGB/BGR edges, grayscale and clamped boundaries. Test data and detailed
reports are generated under `work/native-sampling/`.

```powershell
python work/native-sampling/prepare-inputs.py
node work/native-sampling/browser-reference.cjs
cargo test native_sampling_matches_browser -- --ignored --nocapture
cargo test native_partial_sampling_scroll -- --ignored --nocapture
```

The partial-update test replays scrolling regions of the real GDI fixtures,
compares every output byte against full-frame filtering, and covers cache
invalidation, empty updates, resize, boundaries and mixed-axis scaling.
Its GPU timings include the additional output copy where needed, but exclude
capture, upload, readback and presentation; they are not total application GPU
utilization measurements.

The experiment's content comparisons and tradeoffs are retained in
[sampling-quality.md](sampling-quality.md). Those historical measurements are
not an end-to-end product benchmark.
