# Current

## Baseline

- Branch: `main`
- Version: 1.0.0
- Product core: one Rust process owns one virtual display, one mapping worker,
  one reversible window-migration set, and one captured mouse route.
- Driver boundary: the C++/WDK IDD reports one 3840x2160@240 test mode and
  publishes IddCx swapchain surfaces through two shared BGRA slots.
- Target identity: active DisplayConfig `monitorDevicePath`.

## Implemented

- CLI `list`, `create`, `map`, `config`, `shutdown`, and `ui`.
- Versioned per-user configuration atomically persists a stable target ID and
  optional sizing request; malformed files are preserved with a warning.
- Public geometry types expose physical measurements, explicit rotation,
  sizing policy, alignment, refresh preference, and shared coordinate mapping.
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
  synchronous 4K GDI draw worker. A drained message batch injects only its
  newest absolute source position.
- The mirror does not draw a second pointer marker. The IDD keeps the platform
  default software cursor, preserving the native shape and hotspot in-frame.
- Stop order: input/mirror, window restoration, virtual device, topology wait,
  and final placement reconciliation.
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

- Three consecutive elevated map/start/stop cycles with the signed test driver.
- Maximized Obsidian and Firefox windows round-tripped from a 5120-wide physical
  screen through the 3840-wide virtual source.
- Clean install, in-place reinstall over a legacy directory, full uninstall,
  and clean reinstall.
- Post-uninstall state: no SBMS tray process, scheduled task, Program Files
  directory, or SBMS OEM driver package.
- Final installed state before this build: 0.2.10 binaries, `Highest` logon task running, and
  `sbms create --hold-ms 2000` returning success.

## Deliberately absent

Background service, touch/pen/absolute-pointer forwarding, keyboard remapping,
multi-mapping, dynamic modes, HDR, general
runtime topology recovery, all-GPU transport, and a general test framework.

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
