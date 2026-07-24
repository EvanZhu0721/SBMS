# SBMS

Production driver certification, signing, package integrity and installer
verification are documented in
[`docs/PRODUCTION-SIGNING.md`](docs/PRODUCTION-SIGNING.md).

Start with [CONTEXT.md](CONTEXT.md) for the project workflow constraints.

SBMS means **SBMS bridges multiple screens**.

SBMS is Windows display-control software that creates a controllable virtual desktop through the Indirect Display Driver model, then presents that desktop on a physical monitor with a native D3D11 output path. It is designed for mixed-size, mixed-DPI monitor setups where Windows' resolution-based desktop geometry feels physically wrong.

The current target case is a 27-inch 5K display plus a high-refresh 24-inch 2K display. SBMS lets Windows see a virtual display whose logical size is chosen from real physical size instead of the 2K panel's native pixel count, then maps that virtual desktop back to the real 2K panel.

## Status

The repository has a production-owned driver identity and a fail-closed production packaging pipeline. Public release remains blocked until the package receives the required publisher signature and Microsoft WHQL return, then passes normal-boot hardware acceptance.

- Windows 11
- Visual Studio 2022 Build Tools
- Windows Driver Kit
- Administrator rights for the IDD software device host and Driver Store staging
- A valid production driver-signing path for release builds; explicit test certificates are for isolated development only

## Components

- `SBMS.exe`
  - WinForms controller with multi-display list, terminal log, tabbed mapping configuration, presets, tray/lightweight mode, per-group virtual-only streaming mode, startup task, language switch, and run controls.
- `SBMSNative.exe`
  - Native D3D11 Desktop Duplication output path. Captures the virtual source display and presents it on the physical target display.
- `SBMSDeviceHost.exe`
  - Creates and owns the software display device with `SwDeviceCreate`.
- `SBMSRecoveryBroker.exe`
  - Runs outside the GUI child-process Job and restores journaled window moves after abrupt GUI termination.
- `Windows-driver-samples/video/IndirectDisplay`
  - Patched Microsoft IndirectDisplay sample. Each software device instance exposes one virtual monitor, and the host creates only the number of instances requested by the current mapping configuration. The driver advertises common SBMS virtual modes including 1080p, 2K, 4K, 5K, 8K, 2880p-style modes, 16:9, 16:10, 4:3, landscape, and portrait sizes.

## Build

Run from an elevated PowerShell when Driver Store staging or the device host is needed:

```powershell
.\build-sbms-driver.ps1
.\build-sbms-device-host.ps1
.\build-sbms-native.ps1
.\build-sbms-recovery-broker.ps1
.\build-sbms-gui.ps1
```

The driver build supports `Release|x64` and `Debug|x64`, discovers the installed
Windows SDK/UMDF versions, and never falls back to a prebuilt driver. If WDK
Visual Studio integration is missing but the WDK itself is installed, Issue #9
allows the script to stage the installed Visual C++ v170 targets temporarily
and overlay only the repository's minimal WDK platform-toolset metadata. The
temporary targets tree is removed after both successful and failed builds.

Outputs:

```text
SBMS.exe
SBMSSetup.exe
SBMSNative.exe
SBMSDeviceHost.exe
SBMSRecoveryBroker.exe
```

## Validation

Run the source-level GUI checks without changing the driver or display topology:

```powershell
.\test-sbms-gui-core.ps1
.\test-sbms-configuration.ps1
.\test-sbms-process-job.ps1
.\test-sbms-start-gate.ps1
.\test-sbms-supervisors.ps1
.\test-sbms-recovery-broker.ps1
.\test-sbms-gui.ps1
.\test-sbms-hardware.ps1 -Scenario AuditOnly
```

Real virtual-display acceptance is documented in [docs/HARDWARE-VALIDATION.md](docs/HARDWARE-VALIDATION.md). Hardware scenarios are recorded separately and never pass when critical native-enumeration or GUI lifecycle evidence is missing.

For an isolated development environment, stage the explicitly test-signed
driver package without activating or rebinding a display device:

```powershell
.\install-sbms-driver.ps1 -Force -AllowTestSigned
```

Production Setup passes independently verified WHQL provenance to the same
staging script. Active-device transition is deliberately deferred to the
transactional installer tracked by Issue #19.

## Run

```powershell
.\SBMS.exe
```

Default workflow:

1. Open `设置 > 配置`.
2. Pick `预设` or `手动`.
3. Set the base and target using horizontal pixels, aspect ratio, orientation, and physical size.
4. Pick a sizing strategy:
   - `真实尺寸比例`: virtual source follows physical-size ratio.
   - `文字清晰优先`: virtual source favors an integer multiple of the output display.
   - `直接使用源`: use the selected or typed source directly.
5. Click `启动`.

`轻量模式` is a top-level menu item. When it is checked and SBMS is running, closing the window hides SBMS to the tray and keeps the bridge running. The tray menu has `打开`, `停止`, and `退出`.

`串流模式` creates and keeps only the SBMS virtual desktop alive. It does not start `SBMSNative.exe`, does not copy the virtual desktop to a physical output, does not migrate windows, and does not capture pointer input. This is intended for external streaming or capture workflows. Enabling it now opens a full-window warning overlay and requires explicit confirmation.

When `迁移窗口` is enabled, SBMS continuously moves movable top-level windows from the real target desktop to the virtual source desktop while the bridge is running. This keeps topmost windows such as Task Manager from blocking interaction on the physical output panel.

SBMS can enumerate and select any active physical target display without relying
on ambiguous resolution matching. The current `\\.\DISPLAY2` name is retained
for diagnostics, while persisted bindings use the monitor-derived Sunshine UUID
so a Windows display-number reorder cannot silently redirect output to another
physical panel.

GUI settings are stored as schema-v2 XML under `%LOCALAPPDATA%\SBMS`. Existing
schema-v1 files migrate automatically. Saves use a durable same-directory
temporary file and atomic replacement while retaining a validated
last-known-good copy. Malformed or semantically invalid files are preserved
with a unique `.invalid` name before recovery. A missing saved display is not
treated as corruption: its saved device label and persistent identity remain visible and SBMS
blocks Start until the user explicitly selects a replacement, rather than
silently mapping to another monitor.

## Multi-Screen BETA

The BETA build can create up to two virtual source displays by starting multiple one-monitor software device instances. In `设置 > 配置`, use `+ 新增组 BETA` to enter multi-mapping. SBMS shows a full-window warning overlay saying `多组映射支持为BETA功能, 不保证稳定性`; only after explicit confirmation does the multi-mapping tab appear.

Each group is edited in its own tab. It has its own enable state, output mode, target display, horizontal pixels, aspect ratio, orientation, physical size, sizing strategy, and calculated virtual source resolution. In normal output mode, choose a physical target display for that group, then let SBMS calculate the matching virtual display resolution.

When a group's `仅虚拟桌面` toggle is enabled, that group no longer chooses a physical target display. Instead, enter the streaming target's real horizontal pixels, aspect ratio, orientation, and physical size, and SBMS calculates the virtual display resolution for that virtual-only target. The toggle also opens the same full-window confirmation overlay used by single-screen streaming mode.

In normal multi-screen mode, SBMS assigns one virtual source to each enabled row and launches one `SBMSNative.exe` process per physical target. This keeps the proven single-output renderer intact while testing real multi-display topology.

Notes:

- BETA currently supports up to two mapping groups.
- Multi-mapping groups can mix normal physical-output groups and virtual-only streaming groups.
- Pointer mapping and window migration still run per native process. This is usable for testing, but multi-display pointer capture may need a later unified input scheduler.
- If the BETA topology behaves badly, stop SBMS first; the GUI will close all native output processes and then close the virtual display host.

## Presets

Resolution presets:

- 1080p
- 2K / 1440p
- 4K / 2160p
- 5K / 2880p
- 8K / 4320p
- 2880p

Aspect presets:

- 16:9
- 16:10
- 4:3

Orientation presets:

- 横屏
- 竖屏
- 横屏反向
- 竖屏反向

Size presets:

- 13.3 inch
- 14 inch
- 15.3 inch
- 15.6 inch
- 16 inch
- 18 inch
- 24 inch
- 27 inch
- 32 inch

The preset controls write into the canonical resolution and physical-size fields. The text fields remain editable for unusual panels.

## Native CLI

List active displays:

```powershell
.\SBMSNative.exe --list
```

Run the bridge without the GUI:

```powershell
.\run-sbms-native.ps1 -ManageVirtualDisplay
```

Useful options:

```powershell
.\run-sbms-native.ps1 -Source 5120x2880 -Target 2560x1440 -Filter box2x
.\run-sbms-native.ps1 -Source 4550x2560 -Target 2560x1440 -Filter linear
.\run-sbms-native.ps1 -NoInput
.\run-sbms-native.ps1 -NoWindowMove
.\run-sbms-native.ps1 -Vsync
```

Filters:

- `linear`: default GPU linear sampling.
- `point`: sharp nearest-neighbor sampling.
- `box2x`: exact 2:1 average for source modes such as `5120x2880 -> 2560x1440`.

## Safety Notes

- SBMS refuses to use a physical display as the source unless `--allow-physical-source` is explicitly passed to the native executable.
- Legacy `IddSampleDriver` identities are reported only for migration and residue diagnosis; they cannot satisfy the current SBMS device-success checks.
- `SBMSDeviceHost.exe` and every `SBMSNative.exe` wait on a launch gate until the GUI assigns them to its kill-on-close Windows Job.
- Only one SBMS GUI instance may own a Windows user session, preventing one instance from sweeping another instance's journals or signalling its host stop event.
- Before moving a window, native output flushes its durable rectangle and `WINDOWPLACEMENT`. Normal Stop and the out-of-job recovery broker serialize replay through a per-session lease, preserve minimized/maximized state, and clamp restored placement to a current physical work area.
- Recovery is bounded. Repeated failures enter terminal cleanup instead of restarting indefinitely, while the first and last failures remain visible in the session log.
- These safeguards recover SBMS-owned processes and journaled window moves; they do not claim to repair arbitrary Windows DisplayConfig, GPU-driver, or boot failures.

## Packaging

Create the local release folders and zip:

```powershell
.\package-sbms.ps1
```

The package script writes:

- `%USERPROFILE%\Documents\SBMS-Core-Source`
- `%USERPROFILE%\Documents\SBMS-Release\SBMS-<version>-windows-x64`
- `%USERPROFILE%\Documents\SBMS-Release\SBMS-<version>-windows-x64.zip`
- `C:\Program Files\SBMS` when the shell has permission

The release directory also contains `SBMSSetup.exe`, an elevated installer that
copies SBMS to Program Files, can stage the verified driver package in Driver
Store without activating it, and can create a Start Menu shortcut and startup
task.

If the current shell is not elevated, run this from the release directory in an administrator PowerShell:

```powershell
.\install-sbms-program-files.ps1
```

## Development

Diagnostics:

```powershell
.\diagnose-sbms.ps1
.\diagnose-sbms.ps1 -TryHost
```

The diagnostic script reports current `SBMSIndirectDisplay` devices/packages separately from read-only legacy `IddSampleDriver` residue.

## Upstream

SBMS is based on Microsoft's Windows driver sample:

- https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay

See `NOTICE.md` for attribution.
