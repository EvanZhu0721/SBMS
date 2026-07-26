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
3. The IDD copies each IddCx swapchain surface into one shared BGRA slot.
4. Start succeeds only after Rust draws the first valid shared frame to the
   selected physical target.
5. Stop tears down the mirror before closing the uniquely owned `HSWDEVICE`.
6. Closing that handle makes the current devnode non-present; Windows may keep
   its historical device record.

## Build

Rust:

```powershell
cargo build --release
```

Driver, from PowerShell with Visual Studio C++ Build Tools and the WDK:

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
.\target\release\sbms.exe list
.\target\release\sbms.exe map --target '<monitor-device-path>'
```

Copy the exact physical `id=` printed by `list`. The mapping command prints
`running=` only after Windows exposes the virtual source and the renderer copies
its first frame. Press Enter to stop. For unattended lifecycle checks,
`--hold-ms <milliseconds>` performs the same bounded cleanup.

The current transport is deliberately fixed at 1920x1080 BGRA and copies pixels
through CPU-visible shared memory. It favors transparent behavior over
performance. The channel currently uses a permissive DACL so an elevated Rust
process and the UMDF host can share it; tightening that ACL is required before
non-test distribution. The package is not production-signed.
