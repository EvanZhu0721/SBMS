![SBMS banner](assets/sbms-banner.png?v=75f6b3b)

# SBMS

[简体中文](README.zh-CN.md)

BrainController

▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄

● ChatGPT&Codex 100%


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

New windows are picked up while the mapping is running.

The tray can keep up to sixteen independent mirror or stream-only groups. Their
display, sizing, resolution, refresh-rate, aspect-ratio and rotation settings
survive normal restarts and installer upgrades. Existing single-output
configuration is imported automatically as **Output 1**.

Quick Access can keep up to three named configuration profiles. Create a profile
from the current setup, switch between profiles, rename or delete them, and open
the JSON storage directory from the profile menu. Switching while mapping is
running asks for confirmation, then restarts the mapping with the selected profile.

## Streaming

Set an output to **Stream only**, choose its resolution, refresh rate and
orientation, then start the mapping. SBMS creates the virtual desktop without
mirroring it to a physical display and starts a dedicated Sunshine instance for
that output. The tray shows the LAN address and port; use them to connect from
Moonlight. **Open Sunshine** opens that instance's web panel for pairing and
other Sunshine settings. Stopping the mapping also stops the managed instance.

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

1. Download `SBMS-Setup-1.5.1-x64.exe` from the latest GitHub release.
2. Run it and approve the administrator prompt.
3. Open SBMS from the tray, choose a target display, and select **Start mapping**.
4. Select **Stop and restore** when you want to end the mapping and restore the
   original display layout. If the display topology changes unexpectedly, SBMS
   attempts to reconnect automatically.

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
sbms config list
sbms config show
sbms config import gaming .\gaming.json --activate
sbms shutdown
```

`sbms list` prints the stable ID used by `--target`. Press Enter to stop a
foreground session cleanly. A mapping plan can contain up to sixteen mirror and
stream-only groups. Up to three persistent configuration profiles can coexist;
see [Configuration profiles](docs/configuration.md) for import, export,
activation and hot-reload interfaces.

## Build

Requirements: Rust, Visual Studio C++ Build Tools, a matching Windows Driver
Kit, Inno Setup 6, and a code-signing certificate for release packages.

```powershell
.\build-installer.ps1 -SigningCertificateThumbprint <thumbprint>
```

This is the complete release entry point: it builds the locked Rust binaries,
builds and signs the driver, creates the installer, verifies every signature,
and writes the installer plus its SHA-256 file to `target\installer`.

For component development, use `cargo build --release --locked --bins` or
`.\build-driver.ps1` separately. The complete unsigned build and packaging chain
can be checked explicitly with `.\build-installer.ps1 -Unsigned`; it still
compiles and validates the driver, then produces an `-unsigned` smoke artifact
which must not be distributed.

## Acknowledgments

this is my first github repo which release to public and hope that can help you

thanks for all of my friends, especially Jerry & Tony, who shared their ideas and advices with me. thanks for Mr Berti who is my CSA teacher. thanks for my parents who support me. thanks for my brother Eason who lives in the US and help me recharge my openAI account. thanks for Tibo who reset my token for several times. thanks for all of the agents and subagents who change my ideas into the program. thanks for the Chinese GFW which really trained me to become an expert in communication engineering. 

$${\color{black}of \space course \space I \space still \space love \space you}$$
