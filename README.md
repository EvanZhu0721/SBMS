![SBMS banner](assets/sbms-banner.png)

# SBMS

[简体中文](README.zh-CN.md)

'SBMS' stands for "SBMS bridges multiple screens", Microsoft is a great company and Windows is a good system. (BTW I use Linux for laptop XD)

The original intention of 'SBMS' was to complete the calculation rules for the default logical desktop topology relationship and the physical dimensions of the displays in the Windows multi-screen collaboration system.

'SBMS' turns one Windows virtual desktop into a full-screen mirror on a physical
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
[Geometry API](docs/geometry.md) and [mapping-plan API](docs/mapping-plan.md).

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
sbms plan validate examples\two-streams.json
sbms plan run examples\two-streams.json
sbms config show
sbms shutdown
```

`sbms list` prints the stable ID used by `--target`. Press Enter to stop a
foreground session cleanly. A mapping plan can contain up to eight mirror and
stream-only groups; the current tray UI remains a single-group adapter.

## Build

Requirements: Rust, Visual Studio C++ Build Tools, a matching Windows Driver
Kit, Inno Setup 6, and a code-signing certificate.

```powershell
cargo build --release
.\build-driver.ps1 -SigningCertificateThumbprint <thumbprint>
.\build-installer.ps1 -SigningCertificateThumbprint <thumbprint>
```

The installer is written to `target\installer`.

## Acknowledgments

this is my first github repo which release to public and hope that can help you

thanks for all of my friends, especially Jerry & Tony, who shared their ideas and advices with me. thanks for Mr Berti who is my CSA teacher. thanks for my parents who support me. thanks for my brother Eason who lives in the US and help me recharge my openAI account. thanks for Tibo who reset my token for several times. thanks for all of the agents and subagents who change my ideas into the program. 

$${\color{black}of \space course \space I \space still \space love \space you}$$
