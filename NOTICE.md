# SBMS Notice

SBMS means "SBMS bridges multiple screens".

This repository contains local prototype code plus a patched copy of Microsoft's Windows driver sample under `Windows-driver-samples/video/IndirectDisplay`.

The Indirect Display Driver sample originates from:

https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay

The driver identity still uses `IddSampleDriver` in this prototype so existing test-driver install and enumeration paths remain compatible. User-facing binaries and scripts are named SBMS.

This project is not a production-signed driver package. Use it only on systems where test-signing, driver installation, and display-topology recovery are understood.
