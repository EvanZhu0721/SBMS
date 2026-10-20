# Build

[简体中文](build.zh-CN.md)

Run the following commands from the repository root.

Requirements: Rust 1.92 or later, Visual Studio C++ Build Tools, a matching Windows Driver
Kit, Inno Setup 6, and a code-signing certificate for release packages.

```powershell
.\build-installer.ps1 -SigningCertificateThumbprint <thumbprint>
```

This is the complete release entry point: it builds the locked Rust binaries,
builds and signs the driver, creates the installer, verifies every signature,
and writes the installer to `target\installer`.

For component development, use `cargo build --release --locked --bins` or
`.\build-driver.ps1` separately. The complete unsigned build and packaging chain
can be checked explicitly with `.\build-installer.ps1 -Unsigned`; it still
compiles and validates the driver, then produces an `-unsigned` smoke artifact
which must not be distributed.
