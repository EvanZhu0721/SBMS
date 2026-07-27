# Current

## Baseline

- Branch: `main`
- Version: 1.1.2
- Product core: one Rust process owns one virtual display, one mapping worker,
  one reversible window-migration set, and one captured mouse route.
- Driver boundary: the C++/WDK IDD reads one validated session mode from the v4
  gate, reports that monitor/target mode, and publishes its dynamically sized
  IddCx swapchain surfaces through two shared BGRA slots.
- Target identity: active DisplayConfig `monitorDevicePath`.

## Implemented

- CLI `list`, `create`, `map`, `config`, `shutdown`, and `ui`.
- Versioned per-user configuration atomically persists a stable target ID and
  optional sizing request; malformed files are preserved with a warning.
- Public geometry types expose physical measurements, explicit rotation,
  sizing policy, alignment, refresh preference, and shared coordinate mapping.
- `MappingRequest` converts the saved sizing result into a validated
  `VirtualMode`; the v4 gate carries mode, stride, refresh, and session nonce
  before the software device starts.
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
  mode-sized GDI draw worker. A drained message batch injects only its
  newest absolute source position.
- The mirror does not draw a second pointer marker. The IDD keeps the platform
  default software cursor, preserving the native shape and hotspot in-frame.
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
  claimed because the current driver frame channel uses `Global\` objects.
- Graceful uninstall removes the task and every OEM INF that exactly matches the
  SBMS provider, display class, and hardware ID before Inno removes its
  registered application files.

## Verified locally

- The signed 1.1.2 driver exposed exactly one 4640x2610@240 mode; active
  DisplayConfig reported both source and target-native size as 4640x2610 at
  240/1.
- Elevated mapping reached first-frame confirmation and stopped normally.
- Before/during/after capture verified that normal stop returns both physical
  displays to their pre-session coordinates, modes, and refresh rates.
- Clean install, in-place reinstall over a legacy directory, full uninstall,
  and clean reinstall.
- Post-uninstall state: no SBMS tray process, scheduled task, Program Files
  directory, or SBMS OEM driver package.
- Final installed state: 1.1.2 binaries and oem65.inf 1.1.2.0 Best Ranked /
  Installed. Mapping loaded the target and 4640x2610@240 sizing request from
  `%LOCALAPPDATA%\SBMS\config-v1.json`.
- No new UMDF runtime failure was recorded after removing the invalid
  first-arrival `IddCxMonitorUpdateModes` call.

## Deliberately absent

Background service, touch/pen/absolute-pointer forwarding, keyboard remapping,
multi-mapping, active-session mode changes, HDR, general runtime topology
recovery, all-GPU transport, and a general test framework.

## Distribution boundary

The current package is a developer preview. Its binaries, installer, driver DLL,
and catalog use a local test certificate without a public timestamp. A normal
machine will not trust it. Public distribution requires trusted application
code signing and the appropriate Microsoft driver-signing path.

The elevated logon task is a real security boundary, not a convenience. Lowering
the tray to medium integrity requires moving ownership of the cross-session
global frame channel into a privileged broker or the driver; changing the
startup registry entry alone would produce a tray whose Start button always
fails.
