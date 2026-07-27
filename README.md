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
src/frame_transport.rs      one-session shared frame channel
src/renderer.rs             shared-frame reader and target window
src/ui.rs + ui/             tray adapter and Slint quick-access panel
src/virtual_display.rs      SwDeviceCreate and HSWDEVICE ownership
driver/Driver.cpp           IddCx swapchain drain and BGRA publisher
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
4. The IDD publishes each IddCx swapchain surface through two shared BGRA
   slots; a slow renderer drops frames instead of blocking IddCx.
5. Start succeeds only after Rust draws the first valid shared frame to the
   selected physical target.
6. Stop first releases input capture and closes the mirror, then restores
   migrated windows, closes the uniquely owned `HSWDEVICE`, waits for the
   virtual topology to disappear, and reconciles final window placement against
   the physical-only topology.
7. Closing that handle makes the current devnode non-present; Windows may keep
   its historical device record.

## 1.0.0 capability boundary

Supported:

- Windows 10/11 x64, one process, one virtual source, and one physical target.
- One fixed 3840x2160@240 BGRA test virtual mode.
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
  horizontal wheels. SBMS does not draw an additional software pointer; the
  Windows-composited cursor remains authoritative.
  Press F8 to release capture.
- Keyboard input follows normal Windows foreground focus after the injected
  source click; it is not copied or keylogged by SBMS. Print Screen and
  Win+Shift+S release mouse capture before Windows handles the shortcut.
- Raw mouse input and low-level hooks run on a message pump that is independent
  of the synchronous 4K mirror draw worker. Relative movement packets are
  coalesced to the newest absolute source position before injection.
- The mirror does not composite its own pointer marker. This avoids a duplicate
  arrow. The driver leaves IddCx hardware-cursor support disabled, so the
  platform's default software cursor remains part of the virtual-display frame
  with its native shape, hotspot, animation, and visibility.
- Public pure-Rust sizing types expose native pixel size, physical dimensions
  or diagonal, explicit rotation, physical/integer strategy, alignment, and
  preferred refresh. The calculator is planning infrastructure; the fixed IDD
  mode is not changed by it yet.
- One x64 Inno Setup executable that installs the two Rust executables and the
  signed IDD package under `Program Files\SBMS`.
- One per-user Task Scheduler logon entry with `Highest` run level. This is not
  decorative elevation: the current cross-session IDD frame channel uses
  `Global\` kernel objects and cannot be created by a medium-integrity tray.
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

The v3 frame channel uses two slots. Rust leases the published slot and gives it
directly to `StretchDIBits`; the driver writes the other slot or drops that
frame. There is no Rust full-frame copy. Scaling uses performance-first
`COLORONCOLOR`. The advertised 3840x2160@240 mode is a display-mode and
transport stress target, not a 240 fps performance claim. D3D11 CPU staging
readback, a driver-to-shared-memory copy, and GDI output remain, so the current
pipeline is not expected to sustain 240 full 4K frames per second.

Shared objects use a protected ACL for the launching user, SYSTEM, LocalService,
and Administrators. A protected fixed gate carries a 128-bit random session ID;
the frame mapping and event receive unguessable per-session names. This is
Windows identity-level authorization, not process attestation: another process
under one of those trusted identities remains inside the boundary.

Not supported:

- A background service.
- Automatic target choice, multiple mappings, dynamic modes, rotation, HDR, or
  color management.
- Touch, pen, absolute-pointer forwarding, keyboard remapping, or general
  topology recovery.
- A publicly trusted production package. The current preview installer, Rust
  executables, driver DLL, and catalog are signed with a local test certificate
  without a public timestamp. Ordinary machines will not trust it.
- Native Windows clone-mode semantics, an all-GPU path, or low-latency game
  streaming guarantees.

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
`SizingStrategy`, `Rotation`, and `CoordinateTransform`. Input forwarding now
uses that shared transform instead of maintaining a second private scaling
formula. No UI reads or writes these settings in 1.0.0.

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

The output is `target\installer\SBMS-Setup-1.0.0-x64.exe`. The build signs both
Rust executables and the resulting installer with the same test certificate,
verifies all signatures, and prints the package SHA-256.

## Install and run

Run `SBMS-Setup-1.0.0-x64.exe` and approve UAC. Setup installs/updates the
driver, updates installer-owned files, removes only explicitly known legacy
artifacts, registers the elevated logon task for the installing interactive
account, and starts the tray. It does not clear arbitrary files from the install
directory. A fixed Inno `AppId` makes later 0.2.x packages in-place upgrades.

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
