# SBMS Release Notes

## Unreleased

### v0.2.0 reliability

- Repeated Start and Stop requests now coalesce safely, and stale callbacks from an earlier session cannot mutate a newer run.
- If SBMS closes unexpectedly, its native and host children are terminated automatically. When window migration is enabled, a separate recovery broker replays the durable journal to return pending windows to the physical desktop.
- Topology and source recovery now use bounded backoff and a terminal cleanup path instead of retrying indefinitely. Logs retain transition, retry, timeout, exit-code, and terminal-failure details.
- Existing settings migrate to schema v2 without losing valid mappings. Configuration writes are durable and atomic, retain a validated last-known-good copy, and recover from malformed files or interrupted writes with explicit diagnostics.
- Saved physical targets use a monitor-derived persistent UUID rather than the transient `\\.\DISPLAYn` number. A disconnected, renamed, or renumbered target remains unresolved; SBMS preserves it and requires an explicit replacement before Start.

### v0.3.0 production-driver work

- Replaced the Microsoft indirect-display sample package, service, hardware ID, SWD instance, trace provider, endpoint, and monitor identities with a frozen SBMS-owned contract.
- Made monitor container IDs and EDID serials deterministic per SBMS device instance and connector, with 1920x1080@60 as the preferred mode.
- Bound build, WHQL candidate/import, production packaging, installer integrity, diagnostics, and hardware acceptance to the same `driver-identity.json` fingerprint.
- Added a fail-closed legacy migration inventory and plan. Physical monitor IDs such as `DISPLAY\DELD0E6` are evidence only and can never be cleanup targets without a proven legacy SBMS parent.
- Removed obsolete sample install resources and renamed the active solution, project, INF, DLL, catalog, package, service, and Driver Store identity to `SBMSIndirectDisplay`.

## 2026-06-29.069-beta

- Removed the cumulative 5-recovery fuse from single native and multi-screen BETA topology/source recovery, so repeated Windows Settings layout or orientation edits no longer stop bridge recovery just because earlier recoveries succeeded in the same run.
- Kept recoverable native display/source exits on the existing restart/rebind path while leaving the virtual display host alive during BETA topology recovery.
- Added Issue #8 comments near the single-output and BETA recovery logic.
- Bumped the GUI and setup build labels to `2026-06-29.069-beta`.

## 2026-06-29.068-beta

- Added rollbackable `跟随Windows BETA` behavior for BETA topology recovery, defaulting on for this release while keeping the old strict SBMS-restore path available by unchecking it.
- During running BETA recovery, SBMS now absorbs valid Windows-side virtual display mode changes, including resolution, refresh rate, and orientation, instead of immediately reverting a user rotation/layout edit.
- Persisted absorbed Windows virtual-mode changes back into the active mapping configuration so a Windows Settings rotation can survive the next launch.
- Added Issue #7 comments near the persisted rollback switch and follow-Windows recovery logic.
- Bumped the GUI and setup build labels to `2026-06-29.068-beta`.

## 2026-06-29.067-beta

- Added Sunshine-compatible display id discovery to `SBMSNative.exe --list` by resolving each active `\\.\DISPLAYxx` through DisplayConfig and monitor interface data.
- Printed copy-ready Sunshine display ids for single stream-only mode and multi-screen BETA stream-only mapping groups after the managed virtual source is created and confirmed.
- Stopped native output from writing per-second `present_fps` lines to the terminal during bridge rendering.
- Added Issue #6 comments near the Sunshine id discovery and stream-only logging logic.
- Bumped the GUI and setup build labels to `2026-06-29.067-beta`.

## 2026-06-29.066-beta

- Added a dedicated native exit code for transient virtual-source enumeration misses so the GUI can recover when Windows briefly hides or renumbers a BETA virtual display during mode/topology commits.
- Stabilized multi-screen BETA startup after virtual mode changes by waiting for repeated stable display-list samples, rebinding target rows, rediscovering current virtual `\\.\DISPLAYxx` ids, and restoring requested virtual modes before launching native output.
- Reused the host-stable native restart path for BETA startup source misses, avoiding immediate virtual-display host shutdown when native reports a recoverable source-selector race.
- Added Issue #5 comments near the native error mapping, BETA startup settle/rebind logic, and BETA recoverable-exit handling.
- Bumped the GUI and setup build labels to `2026-06-29.066-beta`.

## 2026-06-29.065-beta

- Kept the virtual display host alive during multi-screen BETA topology-change recovery so Windows layout edits no longer lose the software display devices while the topology is being applied.
- Reworked multi-screen BETA recovery to stop and restart only the native DXGI output processes after display enumeration settles.
- Rebound BETA target rows to the current physical display ids before restarting native output, avoiding stale `\\.\DISPLAYxx` selectors after Windows renumbers displays.
- Added Issue #4 logic comments near the host-stable topology recovery path.
- Bumped the GUI and setup build labels to `2026-06-29.065-beta`.

## 2026-06-29.064-beta

- Added root `CONTEXT.md` as the first-read project context and made the GitHub issue-first workflow mandatory for every bug fix and feature.
- Normalized the source/release boundary: the GitHub repository should contain source, build scripts, documentation, issue-linked logic comments, and minimal required build metadata only.
- Removed the prebuilt stable-driver fallback path from the driver build script; missing WDK driver platform toolsets now fail explicitly instead of falling back to a binary payload.
- Added the minimal x64 WDK MSBuild platform overlay exception needed for this Build Tools environment while keeping generated release binaries and local validation artifacts out of source control.
- Bumped the GUI and setup build labels to `2026-06-29.064-beta`.

## 2026-06-29.063-beta

- Fixed topology-change recovery after Windows display layout edits: the GUI now re-discovers the managed virtual display, reapplies the requested mode when needed, rebuilds native arguments with the current `\\.\DISPLAYxx` ids, and restarts native output instead of timing out and stopping the host.
- Stopped default fallback packaging from overwriting the source-built driver DLL with the older stable asset that exposes multiple virtual monitors from one host-created device.
- Documented the driver monitor-count and mode-table ownership rules in code so the one-host-device/one-virtual-monitor invariant is explicit.
- Bumped the GUI and setup build labels to `2026-06-29.063-beta`.

## 2026-06-29.062-beta

- Fixed the fallback driver packaging path so it always signs and catalogs the known-good WDK-built `IddSampleDriver.dll` when the Visual Studio WDK platform toolsets are missing.
- Prevented the locally produced v143 fallback DLL from being shipped after it compiled successfully but failed Windows UMDF/IddCx startup with Kernel-PnP problem `0x1f` / status `0xc0000001`.
- Made `run-sbms-native.ps1 -ManageVirtualDisplay` resolve a requested virtual resolution to a concrete `\\.\DISPLAYxx` source before launching `SBMSNative.exe`, avoiding ambiguous-source failures when several virtual outputs expose the same mode.
- Bumped the GUI and setup build labels to `2026-06-29.062-beta`.

## 2026-06-29.061-beta

- Made driver active-binding verification independent of localized `pnputil /enum-drivers` output by reading the active `C:\Windows\INF\oem*.inf` `DriverVer` directly.
- The driver installer now attempts to remove the stale `SWD\IddSampleDriver\IddSampleDriver` phantom device instance before refreshing the driver package, reducing cached PnP metadata after reinstall.

## 2026-06-29.060-beta

- Fixed installer active-binding verification when Windows reuses the same `oem*.inf` name and leaves `DEVPKEY_Device_DriverVersion` stale after reinstall.
- The driver installer now accepts the install when the active INF resolves through `pnputil /enum-drivers` to the expected package version, while still warning about stale PnP version data.

## 2026-06-29.059-beta

- Rebuilt the UMDF indirect-display driver with a static C/C++ runtime so the packaged `IddSampleDriver.dll` no longer depends on `VCRUNTIME140.dll` during WUDF startup.
- Bumped the GUI and setup build labels to `2026-06-29.059-beta` so stale runs are visible in logs.
- Verified the rebuilt driver package is signed and cataloged after removing the dynamic VC runtime dependency.

## 2026-06-29.058-beta

- Signed the packaged `IddSampleDriver.dll` before catalog generation, so installer diagnostics no longer see the rebuilt driver DLL as `NotSigned`.
- Relaxed `install-sbms-driver.ps1` to treat the catalog signature as the hard driver-package gate while still logging DLL signature status.
- Added persistent UTF-8 setup logs under `%LOCALAPPDATA%\SBMS\logs\setup-*.log`; localized `pnputil` and PowerShell output is now saved instead of being suppressed.
- Signed `SBMSSetup.exe` during setup builds and logged the setup build label at startup, making stale installer runs easier to identify.

## 2026-06-29.057-beta

- Added an explicit setup self-elevation guard. If `SBMSSetup.exe` is started without an administrator token, it now relaunches itself with UAC before copying files or running the driver installer.

## 2026-06-29.056-beta

- Changed the virtual display model to one monitor per software device instance. Normal single-group startup now asks the host for one virtual display, while multi-mapping BETA asks for the enabled group count.
- Added `SBMSDeviceHost.exe --count N` so the GUI can create only the required number of virtual display instances instead of exposing unused logical desktops.
- Released input capture on screenshot hotkeys including Print Screen and Win+Shift+S, reducing cases where Windows screenshot tools cannot be started or used while SBMS is running.

## 2026-06-29.055-beta

- Added DriverStore payload diagnostics and recent Kernel-PnP event summaries to `diagnose-sbms.ps1`, so stale `IddSampleDriver` packages with unsigned DLLs are visible without manual event-log digging.
- Added driver payload signature checks before `install-sbms-driver.ps1` calls `pnputil`, refusing to install invalid `IddSampleDriver.dll` or catalog files.
- Added post-install active binding verification, so the installer fails loudly if Windows still binds the SBMS software device to an older `oem*.inf` instead of the release payload.
- Enhanced GUI virtual-display load failure logs with structured Kernel-PnP 411 details such as active driver INF, service, upper filter, problem code, and problem status.

## 2026-06-29.054-beta

- Added automatic GUI configuration load/save at `%LOCALAPPDATA%\SBMS\config.xml` for language, lightweight mode, mapping inputs, stream/BETA rows, refresh rates, and selected tabs.
- Added file diagnostics under `%LOCALAPPDATA%\SBMS\logs`: per-session log, `latest.log`, and filtered `error.log` for failures, timeouts, nonzero exits, PnP/driver problem details, and unhandled exceptions.
- Preserved saved BETA target labels when display enumeration temporarily cannot resolve a monitor, so debug state is not silently erased during refresh.

## 2026-06-29.053-beta

- Added setup payload validation for `install-sbms-driver.ps1`, so the cleanup installer path cannot be launched from an incomplete copied setup executable.

## 2026-06-29.052-beta

- Changed `SBMSSetup.exe` driver installation to use the cleanup installer path. It now stops the host, removes existing `iddsampledriver.inf` packages, and installs the current release INF instead of only adding another package with `pnputil`.
- This prevents failed packages such as the `.050-beta` fallback driver from staying in the driver store and being reused during user-environment installs.

## 2026-06-29.051-beta

- Recovered the release driver package by substituting the last known-good WDK-built `IddSampleDriver.dll` when the local machine must use the v143 fallback build path.
- Added a SHA256 guard for the stable fallback driver asset so packaging cannot silently ship a different fallback DLL.
- Kept the current GUI and native flow intact while restoring the driver binary shape that previously started successfully on the target machine.

## 2026-06-29.050-beta

- Repaired the fallback driver build path so it targets the same IDDCX extension generation declared by the INF (`IddCx0102`) instead of compiling and linking against a newer IddCx surface.
- Restored Windows 10 driver DLL link characteristics in fallback builds: subsystem version, image version, checksum, and Control Flow Guard.
- Removed the extra WPP recorder runtime dependency from fallback driver builds to keep the UMDF package closer to the last known-good driver binary shape.

## 2026-06-29.049-beta

- Limited the BETA virtual-display driver to the two validated EDID-backed monitors. This avoids the failed-add path triggered by advertising extra monitors with reused or incomplete monitor identity.
- Temporarily capped GUI multi-mapping groups at 2 to match the validated driver surface.
- Broadened GUI PnP failure detection to catch `Status: Error` and `DEVPKEY_Device_HasProblem` cases, not only explicit `Problem Code` lines.

## 2026-06-29.048-beta

- Fixed the BETA virtual-display startup loop that could wait 30 seconds, stop the host, and repeat when the IDD device failed to materialize.
- Hardened GUI waits so `SBMSNative --list` and helper tools have explicit timeouts instead of blocking the WinForms UI thread.
- Added PnP failure surfacing during virtual-display waits, including `Problem Code` / `Problem Status` details when Windows reports a failed IDD add.
- Fixed the driver monitor exposure path so extra BETA connectors reuse known-good EDID templates instead of passing an empty EDID to IddCx.
- Removed 7680-series virtual modes from the driver mode table to prevent Windows from falling back to an unintended 7K virtual desktop.

## 2026-06-29.047-beta

- Closed the local WDK development-environment gap with an automatic v143 fallback driver build path when the Visual Studio WDK platform toolsets are missing.
- The driver build now generates WPP output, links UMDF/IddCx dependencies, stamps the INF, creates the CAT, signs it with the local WDK test certificate, and exports the certificate for packaging.
- Reworked the driver mode table to derive monitor mode counts from the actual mode array, so adding exact modes such as `4552x2560` no longer overflows a stale fixed-size list.

## 2026-06-29.046-beta

- Fixed virtual-mode fallback when a calculated source such as `4552x2560` is not advertised by the currently installed driver. SBMS now snaps to the closest supported mode before applying it.
- Hardened bridge startup rollback: mode-apply failures now stop the device host, wait for virtual displays to clear, refresh the display list, and return the GUI to stopped state.
- Added device-host exit handling so the GUI no longer keeps a stale "running" state when the virtual-display host stops during a session.
- Added exact `4552x2560` and `2560x4552` modes to the driver source for future driver package builds.
- Updated the driver build script to avoid the MSBuild restore/code-analysis path that can fail with an empty `PlatformToolsetVersion`.

## 2026-06-29.045-beta

- Fixed stale multi-mapping virtual-source cache that could keep a previous 7K virtual mode after changing strategy.
- Added per-mapping refresh-rate configuration for normal and virtual-only groups.
- Hardened startup: SBMS now stops instead of launching native output when a requested virtual display mode cannot be applied and confirmed.
- Improved BETA multi-mapping recovery after Windows display topology/layout changes by rebuilding the bridge on native topology-change exits.
- Classified DXGI acquire device reset/removed events as topology changes in `SBMSNative.exe`.
- Updated the release packaging script for separated source and release directories.

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
