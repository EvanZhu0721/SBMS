# Current

## Baseline

- Branch: `main`
- Version: 1.1.3
- Product core: one Rust process owns one virtual display, one mapping worker,
  one reversible window-migration set, and one captured mouse route.
- Driver boundary: the C++/WDK IDD reads one validated session mode from the v4
  gate, reports that monitor/target mode, and continuously completes its IddCx
  swapchain buffers without reading their pixels back to the CPU.
- Target identity: active DisplayConfig `monitorDevicePath`.

## Implemented

- CLI `list`, `create`, `map`, `config`, `shutdown`, and `ui`.
- Versioned per-user configuration atomically persists a stable target ID and
  optional sizing request; malformed files are preserved with a warning.
- Public geometry types expose physical measurements, explicit rotation,
  sizing policy, alignment, refresh preference, and shared coordinate mapping.
- `MappingRequest` converts the saved sizing result into a validated
  `VirtualMode`; the v4 gate carries mode, stride, and refresh before the
  software device starts.
- When Windows restores an old source resolution for the stable virtual-monitor
  identity, Rust enumerates and applies the requested legal GDI mode, then waits
  for DisplayConfig source convergence.
- Slint/Material 3 taskbar tray panel; its controller worker exclusively owns
  `MappingSession` so the UI thread never performs blocking lifecycle work.
- Startup and 250 ms continuous migration of eligible windows from the selected
  physical target to the virtual source.
- Cross-resolution migration clamps temporary restored rectangles into the
  virtual source; stop restores saved physical placement and state.
- Click-to-capture relative mouse forwarding with five buttons, two wheel axes,
  F8 release, screenshot-shortcut release,
  UIPI failure release, and prior `ClipCursor` restoration.
- Dedicated input message pumping keeps Raw Input and low-level hooks off the
  GPU presentation worker. A drained message batch injects only its newest
  absolute source position.
- The pixel path is Desktop Duplication to a D3D11 texture, a pixel shader, and
  a flip-model swapchain. It performs no CPU staging readback, shared pixel
  mapping, or GDI output.
- A 1:1 mapping bypasses filtering. A 1x..=2x reduction per axis uses exact
  area integration with four bilinear texture fetches plus two horizontal
  samples for neutral-gated, luminance-preserving chroma suppression. Other
  ratios use bilinear sampling.
- The mirror has no fixed pointer marker. It uses Desktop Duplication pointer
  metadata only when the pointer is not already in the duplicated image.
- Stop order: input/mirror, window restoration, virtual device, topology wait,
  and final placement reconciliation.
- Before virtual-device creation, SBMS snapshots the physical topology. Failed
  start cleanup and normal stop restore it after removal so Windows cannot
  leave physical displays rearranged.
- Tray single instance and a local named shutdown event used by upgrade and
  uninstall.
- One Inno Setup x64 preview package containing the two Rust executables, three
  driver-package files, one maintenance script, and the required driver notice
  and MS-PL text.
- Per-installing-user elevated logon task. Medium-integrity auto-start is not
  claimed because the protected session-mode gate uses a `Global\` object.
- Graceful uninstall removes the task and every OEM INF that exactly matches the
  SBMS provider, display class, and hardware ID before Inno removes its
  registered application files.

## Verified locally

- The signed 1.1.3 package installed successfully, including the 1.1.3 IDD.
- All 34 Rust tests pass, Clippy is clean, and driver WDK/API validation passes.
- The configured 4640x2610@240 virtual mode reaches first-frame confirmation
  and stops normally through the GPU-only pixel path.
- During a dynamic-cursor workload, WUDFHost reported no GPU engine above
  0.01% and 0% CPU. The `sbms` renderer reported 3.36% average and 3.44% peak
  3D-engine use with 0% CPU.
- The 4640x2610@240 to 2560x1440 workload presented 239-240 fps for 12
  consecutive seconds. This is one-machine cursor-driven composition stress
  evidence, not a sustained-240 guarantee for all content or hardware.
- Before/during/after capture verified that normal stop returns both physical
  displays to their pre-session coordinates, modes, and refresh rates.

## Deliberately absent

Background service, touch/pen/absolute-pointer forwarding, keyboard remapping,
multi-mapping, active-session mode changes, HDR or color management, general
runtime topology recovery, exact-area kernels outside a 1x..=2x reduction, and
a general test framework. The ClearType-fringe filter is a neutral-content
chroma heuristic, not font recognition or gamma-linear resampling.

## Distribution boundary

The current package is a developer preview. Its binaries, installer, driver DLL,
and catalog use a local test certificate without a public timestamp. A normal
machine will not trust it. Public distribution requires trusted application
code signing and the appropriate Microsoft driver-signing path.

The elevated logon task is a real security boundary, not a convenience. Lowering
the tray to medium integrity requires moving ownership of the cross-session
global session-mode gate into a privileged broker or the driver; changing the
startup registry entry alone would produce a tray whose Start button always
fails.
