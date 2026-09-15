# SBMS notices

SBMS means "SBMS bridges multiple screens".

`driver/Driver.cpp` is a substantially reduced derivative of Microsoft's
Indirect Display Driver sample:

https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay

That derived driver source remains subject to the Microsoft Public License.
The complete license is included at `LICENSES/MS-PL.txt`.

The Rust host and other original SBMS files are licensed under the GNU General
Public License, version 3 only (GPL-3.0-only).
Copyright (C) 2026 EvanZhu0721.
The complete license is included in the repository root `LICENSE` file and is
installed as `licenses/GPL-3.0.txt`.
Third-party components remain subject to their respective licenses;
`driver/Driver.cpp` retains the Microsoft Public License described above.

The refresh and edit icon paths in `ui/controls.slint` are adapted from
Google Material Design Icons for use in Slint:

https://github.com/google/material-design-icons

Those icon paths retain the Apache License, version 2.0. The complete license is
included at `LICENSES/Apache-2.0.txt` and installed as `licenses/Apache-2.0.txt`.

Repository builds are development builds. A distributable Windows driver still
requires an appropriate signing and certification process.
