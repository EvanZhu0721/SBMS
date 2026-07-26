# SBMS

SBMS is being rebuilt around one auditable path: create one Windows indirect
display, keep it alive for one process, and remove it when that process exits.

The legacy GUI, process supervisors, recovery brokers, XML configuration,
transactional installer framework, and contract-test framework are intentionally
absent. Their last tree is preserved by the `legacy-csharp-eb9d2a1` Git tag.

## Code map

```text
src/main.rs                 CLI and process lifetime
src/virtual_display.rs      SwDeviceCreate and HSWDEVICE ownership
driver/Driver.cpp           minimal UMDF/IddCx display driver
driver/SBMSIndirectDisplay.inf
build-driver.ps1            build, validation, and optional test signing
```

The Rust process owns product policy. The C++ driver owns only the WDF/IddCx
boundary that the Windows Driver Kit exposes naturally in C++.

The first-stage invariant is deliberately small:

1. `sbms create` requests `SBMS\IndirectDisplay`.
2. The asynchronous creation HRESULT decides success.
3. A successful process uniquely owns one `HSWDEVICE`.
4. Closing that handle makes the current devnode non-present; Windows may keep
   its historical device record.
5. The driver exposes one stable 1920x1080@60 monitor and drains every assigned
   IddCx swapchain.

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
.\target\release\sbms.exe create
```

The command prints the device instance ID only after Windows reports successful
creation. Press Enter to close the handle and remove the device.
For unattended lifecycle checks, `--hold-ms <milliseconds>` keeps the device
alive for a bounded interval and then closes it.

This stage does not mirror pixels to a physical monitor and does not claim a
production-signed package.
