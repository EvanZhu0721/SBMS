# SBMS

SBMS is being rebuilt around one auditable path: create one Windows indirect
display, copy that virtual desktop to one explicitly selected physical display,
and remove every owned resource when the session stops.

The legacy GUI, process supervisors, recovery brokers, XML configuration,
transactional installer framework, and contract-test framework are intentionally
absent. Their last tree is preserved by the `legacy-csharp-eb9d2a1` Git tag.

## Code map

```text
src/main.rs                 CLI and process lifetime
src/display.rs              stable display identity and active topology
src/mapping.rs              mapping start/stop ownership
src/frame_transport.rs      one-session shared frame channel
src/renderer.rs             shared-frame reader and target window
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
3. The IDD publishes each IddCx swapchain surface through two shared BGRA
   slots; a slow renderer drops frames instead of blocking IddCx.
4. Start succeeds only after Rust draws the first valid shared frame to the
   selected physical target.
5. Stop tears down the mirror before closing the uniquely owned `HSWDEVICE`.
6. Closing that handle makes the current devnode non-present; Windows may keep
   its historical device record.

## 0.2.6 capability boundary

Supported:

- Windows 10/11 x64, one process, one virtual source, and one physical target.
- One fixed 1920x1080@60 BGRA virtual display.
- Explicit target selection by active DisplayConfig `monitorDevicePath`.
- First-frame confirmation, bounded stop, five-cycle repeatability, and
  concurrent-session rejection.
- `--version`, `list`, `create`, and `map`; `create` tests only the raw device
  lifetime.

The v2 frame channel uses two slots. Rust leases the published slot and gives it
directly to `StretchDIBits`; the driver writes the other slot or drops that
frame. There is no Rust full-frame copy. Scaling uses performance-first
`COLORONCOLOR`, so a non-1920x1080 target is sharper and more pixelated than the
old halftone output. This is not zero-copy: D3D11 staging readback, one
driver-to-shared-memory copy, and GDI output remain.

Shared objects use a protected ACL for the launching user, SYSTEM, LocalService,
and Administrators. A protected fixed gate carries a 128-bit random session ID;
the frame mapping and event receive unguessable per-session names. This is
Windows identity-level authorization, not process attestation: another process
under one of those trusted identities remains inside the boundary.

Not supported:

- GUI, configuration persistence, background service, or production installer.
- Automatic target choice, multiple mappings, dynamic modes, rotation, HDR, or
  color management.
- Cursor composition, input forwarding, window migration, or topology recovery.
- Native Windows clone-mode semantics, an all-GPU path, or low-latency game
  streaming guarantees.

The selected physical screen is covered by a topmost no-activate window.
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
```

Copy the complete `id=` from a `physical` line. It is a `monitorDevicePath`, not
`\\.\DISPLAYn`; the target must still be active when `map` starts. The command
prints `running=` only after Windows exposes the virtual source and Rust draws
the first valid frame. Press Enter to stop. For bounded unattended use:

```powershell
.\target\release\sbms.exe map `
  --target '<monitor-device-path>' `
  --hold-ms 5000

.\target\release\sbms.exe create --hold-ms 5000
```

Build, install, and run from an elevated terminal. The local package is
test-signed; ordinary distribution still requires production driver signing.
