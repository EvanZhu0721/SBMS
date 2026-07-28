# SBMS

[简体中文](README.zh-CN.md)

SBMS turns one Windows virtual desktop into a full-screen mirror on a physical
display. It is useful when Windows' normal extend/duplicate modes cannot provide
the resolution, scaling, or desktop arrangement you need.

Start a mapping from the tray, choose the physical display, and SBMS will:

- create a virtual display at the configured size and refresh rate;
- move eligible windows from the chosen display to the virtual desktop;
- mirror the virtual desktop back to the chosen display;
- forward mouse input to the real Windows pointer on the virtual desktop; and
- restore the windows and physical display layout when mapping stops.

New windows are picked up while the mapping is running. Press **F8** to release
mouse capture.

## How it works

The application and lifecycle code are written in Rust. A small C++ UMDF
indirect-display driver publishes the virtual monitor through WDF/IddCx.
Desktop Duplication and a D3D11 shader keep the mirror on the GPU, including
area-based downscaling and lightweight subpixel-fringe reduction.

The tray panel is built with Slint. Installation and upgrades use Inno Setup.

More implementation detail is available in
[Architecture](docs/architecture.md). Frontend developers can use the
[Geometry API](docs/geometry.md).

## Install

1. Download `SBMS-Setup-1.2.0-x64.exe` from the latest GitHub release.
2. Run it and approve the administrator prompt.
3. Open SBMS from the tray, choose a target display, and select **Start**.
4. Select **Stop** before disconnecting or rearranging displays.

SBMS starts automatically when the installing user signs in. Remove it from
Windows **Installed apps**; the uninstaller also removes the driver and startup
task.

The current package uses a local test-signing certificate. Windows must trust
that certificate, or be configured for test-signed drivers, before the driver
can load. A Microsoft production-signed driver is required for normal public
installation without that setup.

## Command line

The tray is the normal interface. The same lifecycle is also available from an
administrator terminal:

```powershell
sbms list
sbms map --target '<monitor-device-path>'
sbms config show
sbms shutdown
```

`sbms list` prints the stable ID used by `--target`. Press Enter to stop a
foreground `map` session cleanly.

## Build

Requirements: Rust, Visual Studio C++ Build Tools, a matching Windows Driver
Kit, Inno Setup 6, and a code-signing certificate.

```powershell
cargo build --release
.\build-driver.ps1 -SigningCertificateThumbprint <thumbprint>
.\build-installer.ps1 -SigningCertificateThumbprint <thumbprint>
```

The installer is written to `target\installer`.
