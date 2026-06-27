# SBMS

SBMS means **SBMS bridges multiple screens**.

SBMS is a Windows prototype that creates a controllable virtual desktop through the Indirect Display Driver model, then presents that desktop on a physical monitor with a native D3D11 output path. It is designed for mixed-size, mixed-DPI monitor setups where Windows' resolution-based desktop geometry feels physically wrong.

The current target case is a 27-inch 5K display plus a high-refresh 24-inch 2K display. SBMS lets Windows see a virtual display whose logical size is chosen from real physical size instead of the 2K panel's native pixel count, then maps that virtual desktop back to the real 2K panel.

## Status

This is a local engineering prototype, not a signed production driver.

- Windows 11
- Visual Studio 2022 Build Tools
- Windows Driver Kit
- Administrator rights for the IDD software device host and driver install
- Test signing or a valid driver-signing path

## Components

- `SBMS.exe`
  - WinForms controller with multi-display list, terminal log, tabbed mapping configuration, presets, tray/lightweight mode, per-group virtual-only streaming mode, startup task, language switch, and run controls.
- `SBMSNative.exe`
  - Native D3D11 Desktop Duplication output path. Captures the virtual source display and presents it on the physical target display.
- `SBMSDeviceHost.exe`
  - Creates and owns the software display device with `SwDeviceCreate`.
- `Windows-driver-samples/video/IndirectDisplay`
  - Patched Microsoft IndirectDisplay sample. The BETA build exposes up to three virtual monitors and advertises common SBMS virtual modes including 1080p, 2K, 4K, 5K, 8K, 2880p-style modes, 16:9, 16:10, 4:3, landscape, and portrait sizes.

## Build

Run from an elevated PowerShell when driver install or the device host is needed:

```powershell
.\build-sbms-driver.ps1
.\build-sbms-device-host.ps1
.\build-sbms-native.ps1
.\build-sbms-gui.ps1
```

Outputs:

```text
SBMS.exe
SBMSSetup.exe
SBMSNative.exe
SBMSDeviceHost.exe
```

Install or refresh the test driver:

```powershell
.\install-sbms-driver.ps1 -Force
```

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

SBMS can enumerate and select any active physical target display by its Windows device id such as `\\.\DISPLAY2`, so duplicate resolutions and 3+ monitor layouts do not have to rely on ambiguous resolution matching. The stable bridge path still runs one virtual source to one physical output per native process; the BETA multi-mapping tab starts multiple bridge groups on top of that model.

## Multi-Screen BETA

The BETA build can create up to three virtual source displays. In `设置 > 配置`, use `+ 新增组 BETA` to enter multi-mapping. SBMS shows a full-window warning overlay saying `多组映射支持为BETA功能, 不保证稳定性`; only after explicit confirmation does the multi-mapping tab appear.

Each group is edited in its own tab. It has its own enable state, output mode, target display, horizontal pixels, aspect ratio, orientation, physical size, sizing strategy, and calculated virtual source resolution. In normal output mode, choose a physical target display for that group, then let SBMS calculate the matching virtual display resolution.

When a group's `仅虚拟桌面` toggle is enabled, that group no longer chooses a physical target display. Instead, enter the streaming target's real horizontal pixels, aspect ratio, orientation, and physical size, and SBMS calculates the virtual display resolution for that virtual-only target. The toggle also opens the same full-window confirmation overlay used by single-screen streaming mode.

In normal multi-screen mode, SBMS assigns one virtual source to each enabled row and launches one `SBMSNative.exe` process per physical target. This keeps the proven single-output renderer intact while testing real multi-display topology.

Notes:

- BETA currently supports up to three mapping groups.
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
- SBMS still recognizes `IddSampleDriver` because the current prototype is based on Microsoft's sample driver identity.
- Stopping SBMS normally lets the native process restore cursor clipping, input capture, and migrated windows before the software display host exits.
- Do not force-kill the native process unless the desktop is already recovered.

## Packaging

Create the local release folders and zip:

```powershell
.\package-sbms.ps1
```

The package script writes:

- `%USERPROFILE%\Documents\SBMS-Core-Source`
- `%USERPROFILE%\Documents\SBMS-Release\SBMS`
- `%USERPROFILE%\Documents\SBMS-Release\SBMS.zip`
- `C:\Program Files\SBMS` when the shell has permission

The release directory also contains `SBMSSetup.exe`, an elevated installer that copies SBMS to Program Files, optionally installs the test driver, and can create a Start Menu shortcut and startup task.

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

The diagnostic script still mentions the underlying `IddSampleDriver` package because that is the driver package name used by the prototype.

## Upstream

SBMS is based on Microsoft's Windows driver sample:

- https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay

See `NOTICE.md` for attribution.
