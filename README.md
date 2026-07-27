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
src/display.rs              stable display identity and active topology
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

## 0.2.8 capability boundary

Supported:

- Windows 10/11 x64, one process, one virtual source, and one physical target.
- One fixed 3840x2160@240 BGRA test virtual mode.
- Explicit target selection by active DisplayConfig `monitorDevicePath`.
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
  horizontal wheels, and the real Windows software cursor carried by the IDD
  frame. Press F8 to release capture.
- Keyboard input follows normal Windows foreground focus after the injected
  source click; it is not copied or keylogged by SBMS. Print Screen and
  Win+Shift+S release mouse capture before Windows handles the shortcut.
- Normal and maximized window round trips, including a window opened after the
  session started, have been exercised locally across different display DPI
  settings.
- First-frame confirmation, bounded stop, five-cycle repeatability, and
  concurrent-session rejection.
- `--version`, `list`, `create`, and `map`; `create` tests only the raw device
  lifetime.

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

- Configuration persistence, background service, or production installer.
- Automatic target choice, multiple mappings, dynamic modes, rotation, HDR, or
  color management.
- Touch, pen, absolute-pointer forwarding, keyboard remapping, or general
  topology recovery.
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

## Run

Install a signed package from an elevated terminal:

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

Build, install, and run from an elevated terminal. The local package is
test-signed; ordinary distribution still requires production driver signing.
