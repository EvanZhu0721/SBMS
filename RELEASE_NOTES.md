# SBMS Release Notes

## 2026-06-27 prototype

- Renamed user-facing application and binaries to SBMS.
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
