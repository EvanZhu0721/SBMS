# SBMS Notice

SBMS means "SBMS bridges multiple screens".

This repository contains SBMS code plus a substantially adapted copy of Microsoft's Windows driver sample under `Windows-driver-samples/video/IndirectDisplay`.

The Indirect Display Driver sample originates from:

https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay

The active driver package, service, hardware ID, software-device enumerator, endpoint, monitor, trace, and diagnostic identities are SBMS-owned. `IddSampleDriver` remains only in upstream source-directory names, attribution, legacy-residue diagnostics, migration rules, and negative tests.

Repository builds are not automatically production-signed. A distributable driver must pass the documented publisher-signing, Microsoft WHQL-return, integrity, and normal-boot acceptance process.
