# SBMS Release Notes

## 2026-06-27 prototype

- Renamed user-facing application and binaries to SBMS.
- Replaced the old top-level `多屏 BETA` checkbox with a tabbed mapping model. `+ 新增组 BETA` now opens a full-window warning overlay before the multi-mapping tab is created.
- Changed multi-mapping from a crowded row table into per-group tabs. Each group has its own enable toggle, output/virtual-only mode, target display, horizontal pixels, aspect ratio, orientation, physical size, sizing strategy, and calculated virtual source.
- Changed streaming into an explicit risk action. Single-screen `串流模式` and per-group `仅虚拟桌面` both require a full-window confirmation overlay before enabling.
- Unified GUI toggle styling: inactive options use a dark-green button with white text, and active options use a light-green button with red text.
- Multi-mapping groups can now mix physical-output groups and virtual-only streaming groups. SBMS only launches `SBMSNative.exe` for groups that actually have a physical target.
- Added the BETA driver path for up to three virtual monitors.
- Added `串流模式`: SBMS can create only the virtual desktop without copying it to a physical output. This is marked as an advanced option in the GUI.
- Added GUI presets for common resolutions, aspect ratios, orientations, and physical sizes.
- Added landscape, portrait, and flipped orientation handling for requested virtual modes.
- Expanded the virtual display driver's advertised mode list for 1080p, 2K, 4K, 5K, 8K, 2880p-style, 16:9, 16:10, and 4:3 modes.
- Kept the terminal-first black/green GUI with language switching, startup task control, tray/lightweight mode, and locked-configuration overlay while running.
- Reworked the configuration page into separate preset and manual modes. Both modes use horizontal pixels, aspect ratio, orientation, and physical size, and the old "primary" wording is now "base".
- Added continuous real-desktop window migration so newly opened topmost windows are moved to the virtual desktop during a running bridge session.
- Added `SBMSSetup.exe`, a lightweight elevated installer for the release directory.
- Built and verified `SBMS.exe`, `SBMSNative.exe`, `SBMSDeviceHost.exe`, and the test-signed IDD driver package.

## Known constraints

- The driver package remains test-signed and is not suitable for normal public release without a real signing path.
- The underlying PnP/driver identity remains `IddSampleDriver` for compatibility with the current prototype.
- Runtime configuration changes remain locked while the bridge is running.
- `多屏 BETA` still uses per-output native processes for pointer/window handling. `多屏 BETA + 串流模式` creates multiple virtual desktops without physical output copying.
