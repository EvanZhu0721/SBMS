# SBMS notices

SBMS means "SBMS bridges multiple screens".

`driver/Driver.cpp` is a substantially reduced derivative of Microsoft's
Indirect Display Driver sample:

https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay

That derived driver source remains subject to the Microsoft Public License.
The complete license is included at `LICENSES/MS-PL.txt`.

The Rust host and other original SBMS files do not currently carry an explicit
open-source license. Visibility of the source does not grant redistribution or
reuse rights beyond applicable law.

Repository builds are development builds. A distributable Windows driver still
requires an appropriate signing and certification process.
