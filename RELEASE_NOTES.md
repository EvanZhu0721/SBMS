# SBMS Release Notes

## 2026-06-27 prototype

- Renamed user-facing application and binaries to SBMS.
- Added `多屏 BETA`: the test driver can expose up to three virtual monitors, and the GUI now configures independent bridge rows instead of one shared target list.
- Changed `多屏配置组` to a default-one-group flow with an explicit `⊕ 新增组 β` button for additional BETA groups.
- In `串流模式`, multi-screen rows no longer select physical displays; they use entered streaming-target parameters to calculate virtual display resolutions.
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
