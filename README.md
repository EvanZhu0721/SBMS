# SBMS

[简体中文](README.zh-CN.md)

SBMS is being rebuilt around one auditable path: create one Windows indirect
display, copy that virtual desktop to one explicitly selected physical display,
and remove every owned resource when the session stops.

The legacy GUI, process supervisors, recovery brokers, XML configuration,
transactional installer framework, and contract-test framework are intentionally
absent. Their last tree is preserved by the `legacy-csharp-eb9d2a1` Git tag.

## Code map

```text
src/main.rs                 CLI and process lifetime
src/controller.rs           non-blocking UI-to-session worker
src/control.rs              tray singleton and graceful shutdown event
src/config.rs               versioned per-user product configuration
src/display.rs              stable display identity and active topology
src/geometry.rs             physical sizing and coordinate transforms
src/input.rs                captured mouse routing and safe release
src/mapping.rs              mapping start/stop ownership
src/window_migration.rs     reversible top-level window migration
src/frame_transport.rs      protected one-session mode gate
src/renderer.rs             Desktop Duplication and target window
src/gpu_renderer.rs         D3D11 scaling and flip-model presentation
src/ui.rs + ui/             tray adapter and Slint quick-access panel
src/virtual_display.rs      SwDeviceCreate and HSWDEVICE ownership
driver/Driver.cpp           IddCx mode reporting and swapchain drain
driver/SBMSIndirectDisplay.inf
build-driver.ps1            build, validation, and optional test signing
installer/                  thin Inno Setup manifest and maintenance script
build-installer.ps1         signed preview-package build
```

The Rust process owns product policy. The C++ driver owns only the WDF/IddCx
boundary that the Windows Driver Kit exposes naturally in C++.

The invariant is deliberately small:

1. A target is selected by DisplayConfig `monitorDevicePath`, never by display
   order or resolution.
2. `sbms map` requests `SBMS\IndirectDisplay` and waits for its active source.
3. Eligible standard top-level windows on the target are moved to the virtual
   source before the mirror is exposed as running.
4. The IDD continuously acquires and finishes IddCx swapchain buffers without
   reading their pixels back to the CPU.
5. Rust locates the exact virtual DXGI output, duplicates it on the GPU, and
   start succeeds only after the first successful presentation to the selected
   physical target.
6. Stop first releases input capture and closes the mirror, then restores
   migrated windows, closes the uniquely owned `HSWDEVICE`, waits for the
   virtual topology to disappear, and reconciles final window placement against
   the physical-only topology.
7. Closing that handle makes the current devnode non-present; Windows may keep
   its historical device record.

## 1.1.3 capability boundary

Supported:

- Windows 10/11 x64, one process, one virtual source, and one physical target.
- One session-specific BGRA virtual mode calculated from the saved sizing
  request. With no sizing request, the fallback is 3840x2160@240.
- Explicit target selection by active DisplayConfig `monitorDevicePath`.
- Versioned configuration at `%LOCALAPPDATA%\SBMS\config-v1.json`. It stores
  only the stable target ID and an optional sizing request. Writes use a
  same-directory temporary file and an atomic Windows replace. Invalid or
  unsupported files are preserved and reported instead of silently overwritten.
- Startup migration of eligible visible standard top-level windows from the
  selected physical target to the virtual source, followed by a 250 ms scan
  that catches newly opened windows or tracked windows moved back to the target.
- Stop-time input release and mirror shutdown before window restoration,
  followed by final placement reconciliation after Windows removes the virtual
  topology.
- A tray quick-access panel with physical-target selection, refresh,
  Start/Stop, status and error reporting, settings information, and Exit. Its
  controller worker owns `MappingSession`; blocking lifecycle work does not run
  on the UI thread.
- Click-to-capture mouse routing from the physical mirror into the virtual
  desktop: relative movement, left/right/middle/X1/X2 buttons, vertical and
  horizontal wheels. SBMS does not draw a synthetic fixed pointer; the
  Desktop Duplication image and pointer metadata remain authoritative.
  Press F8 to release capture.
- Keyboard input follows normal Windows foreground focus after the injected
  source click; it is not copied or keylogged by SBMS. Print Screen and
  Win+Shift+S release mouse capture before Windows handles the shortcut.
- Raw mouse input and low-level hooks run on a message pump that is independent
  of the GPU presentation worker. Relative movement packets are
  coalesced to the newest absolute source position before injection.
- The mirror does not composite a fixed pointer marker. Desktop Duplication
  pointer metadata is rendered only when Windows reports that the pointer is
  not already part of the duplicated image, preserving the current shape,
  hotspot, visibility, and position without drawing two arrows.
- Public pure-Rust sizing types expose native pixel size, physical dimensions
  or diagonal, explicit rotation, physical/integer strategy, alignment, and
  preferred refresh. `MappingRequest` turns the result into the mode requested
  from the IDD; calculation remains pure and independently reviewable.
- One x64 Inno Setup executable that installs the two Rust executables and the
  signed IDD package under `Program Files\SBMS`.
- One per-user Task Scheduler logon entry with `Highest` run level. This is not
  decorative elevation: the protected cross-session mode gate uses a `Global\`
  kernel object and cannot be created by a medium-integrity tray.
- Graceful upgrade and uninstall: signal the tray and wait up to 30 seconds for
  `MappingSession` cleanup. Uninstall then removes verified owned logon tasks
  and every OEM INF that exactly matches the SBMS provider/hardware ID. If
  external cleanup fails, it attempts compensation and retains the registered
  application files instead of reporting a clean uninstall.
- Normal and maximized window round trips, including a window opened after the
  session started, have been exercised locally across different display DPI
  settings.
- First-frame confirmation, bounded stop, five-cycle repeatability, and
  concurrent-session rejection.
- `--version`, `list`, `create`, `map`, and `config`. `map` accepts an explicit
  target or the saved target; `create` tests only the raw device lifetime.

The v4 session gate carries the validated width, height, stride, and rational
refresh rate before `SwDeviceCreate`. The IDD advertises one monitor/target mode
for that session and only drains its IddCx swapchain. Rust matches the virtual
source rectangle to one exact DXGI output, then uses
`Desktop Duplication -> D3D11 texture -> pixel shader -> flip-model swap chain`.
The pixel hot path has no CPU staging readback, shared pixel mapping, or GDI
output.

At a 1:1 scale, the shader bypasses filtering. For a reduction between 1x and
2x on each axis, it performs exact source-pixel area integration with four
bilinear texture fetches, then uses two additional horizontal samples for a
neutral-content chroma low-pass that reduces rescaled ClearType fringes while
preserving luminance. Other scale ratios fall back to bilinear sampling. This
is a lightweight display heuristic, not font recognition, gamma-linear
resampling, HDR, or color management. An advertised 240 Hz mode is not a
sustained 240 fps promise.

On the validated machine, a 12-second cursor-driven 4640x2610@240 to
2560x1440 run held 239-240 fps. WUDFHost used 0% CPU and exposed no GPU engine
above 0.01%; the `sbms` renderer used 0% CPU and averaged 3.36% 3D-engine load
with a 3.44% peak. This is one-machine composition stress evidence, not a
guarantee for every workload or GPU.

Windows can restore an older desktop source resolution for the permanent
virtual-monitor identity. Start therefore enumerates the legal GDI mode,
applies the requested source resolution temporarily, and waits for DisplayConfig
to converge before window migration, input routing, and first-frame
confirmation. Start snapshots the physical topology before creating the virtual
source; failure cleanup and normal stop reapply it after device removal, so
Windows rearrangement does not become the user's final layout.

The fixed session gate uses a protected ACL for the launching user, SYSTEM,
LocalService, and Administrators. There are no shared pixel mappings or frame
events. This is Windows identity-level authorization, not process attestation:
another process under one of those trusted identities remains inside the
boundary.

Not supported:

- A background service.
- Automatic target choice, multiple mappings, active-session mode changes,
  Windows output rotation, HDR, or color management.
- Touch, pen, absolute-pointer forwarding, keyboard remapping, or general
  topology recovery.
- A publicly trusted production package. The current preview installer, Rust
  executables, driver DLL, and catalog are signed with a local test certificate
  without a public timestamp. Ordinary machines will not trust it.
- Native Windows clone-mode semantics, HDR or color management, low-latency
  game-streaming guarantees, or sustained 240 fps.

Window migration is deliberately narrower than a window manager. It targets
eligible live, visible standard top-level windows owned by the current
interactive desktop. Minimized windows are deliberately left in place because
their `WINDOWPLACEMENT` uses workspace coordinates whose DPI/taskbar semantics
are not safe to rewrite generically. Windows blocked by UIPI or a
higher-integrity owner, hung windows, applications that continuously reposition
themselves, and windows that close or are recreated during the session cannot
be restored losslessly and are outside the guarantee.

The selected physical screen is covered by a topmost no-activate window.
Clicking it captures the mouse; F8 returns the pointer to the corresponding
physical-screen position. SBMS restores the previous `ClipCursor` boundary and
releases only buttons that it successfully injected. `SendInput` cannot cross
Windows UIPI into a higher-integrity application; capture is released and an
error is reported instead of pretending forwarding succeeded.

Topology is revalidated during start. Runtime topology changes are not recovered
and have no guaranteed behavior; stop and restart the session.

## Configuration and sizing API

```powershell
sbms config path
sbms config show
sbms config set-target <monitor-device-path>
sbms config clear-target
sbms config reset
sbms map
```

`set-target` accepts only one currently active physical display. A malformed
configuration is left untouched; `reset` is the explicit recovery operation.
The public `geometry` module contains `DisplayGeometry`, `SizingRequest`,
`SizingStrategy`, `Rotation`, and `CoordinateTransform`. Input forwarding uses
that shared transform instead of maintaining a second private scaling formula.
The CLI and tray share the persisted sizing request; frontends must not persist
derived modes or duplicate the calculation/application path.

## Build

Rust:

```powershell
cargo build --release
```

Driver, from PowerShell with Visual Studio C++ Build Tools and a matching WDK
(UMDF 2.25 and IddCx 1.4 build headers/libraries; the INF declares the
IddCx0102 runtime extension):

```powershell
.\build-driver.ps1
```

For local test deployment, pass the SHA-1 thumbprint of a trusted test-signing
certificate. The same command signs the DLL, creates the catalog, signs it, and
verifies both signatures:

```powershell
.\build-driver.ps1 -SigningCertificateThumbprint <thumbprint>
```

Build the complete preview installer with Inno Setup 6:

```powershell
.\build-installer.ps1 `
  -SigningCertificateThumbprint <thumbprint>
```

The output is `target\installer\SBMS-Setup-1.1.3-x64.exe`. The build signs both
Rust executables and the resulting installer with the same test certificate,
verifies all signatures, and prints the package SHA-256.

## Install and run

Run `SBMS-Setup-1.1.3-x64.exe` and approve UAC. Setup installs/updates the
driver, updates installer-owned files, removes only explicitly known legacy
artifacts, registers the elevated logon task for the installing interactive
account, and starts the tray. It does not clear arbitrary files from the install
directory. A fixed Inno `AppId` makes later packages in-place upgrades.

Uninstall from Windows Installed Apps or run:

```powershell
& 'C:\Program Files\SBMS\unins000.exe'
```

The uninstaller refuses to delete files if the active mapping cannot stop
cleanly or the driver package cannot be removed. It never uses
`pnputil /force`.

For developer-mode manual deployment, install a signed package from an elevated
terminal:

```powershell
pnputil /add-driver .\target\driver\SBMSIndirectDisplay.inf /install
```

From the same elevated terminal, run:

```powershell
.\target\release\sbms.exe --version
.\target\release\sbms.exe list
.\target\release\sbms.exe map --target '<monitor-device-path>'
.\target\release\sbms.exe ui
.\target\release\sbms-tray.exe
```

Copy the complete `id=` from a `physical` line. It is a `monitorDevicePath`, not
`\\.\DISPLAYn`; the target must still be active when `map` starts. The command
prints `running=` only after Windows exposes the virtual source and Rust draws
the first valid frame. Eligible target windows are migrated automatically.
Press Enter to take the normal stop path and restore them; forcibly terminating
the process bypasses user-mode restoration. For bounded unattended use:

```powershell
.\target\release\sbms.exe map `
  --target '<monitor-device-path>' `
  --hold-ms 5000

.\target\release\sbms.exe create --hold-ms 5000
```

Manual build, install, and mapping run from an elevated terminal. The packaged
tray receives that elevation through its registered logon task.
