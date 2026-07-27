# 0001 Core boundaries

Accepted: 2026-07-27

Pixel transport and presentation were superseded by
`0002 GPU mirror path.md` in 1.1.3. The single-session ownership and topology
boundaries below remain in force.

The core owns one mapping session. Rust owns display discovery, lifecycle,
errors, target rendering, and concurrency policy. C++ owns only the WDF/IddCx
boundary and publishes the swapchain's latest BGRA frame.

External GDI capture and DXGI Desktop Duplication are not the authoritative
pixel boundary for this IDD. The driver therefore copies the surface handed to
it by IddCx into two shared-memory slots. Rust leases the currently published
slot and draws it with `StretchDIBits`; the driver never waits on Rust.

Topology is validated during startup. Runtime topology changes are not detected
or recovered; the session must be stopped and restarted. The product does not
import the old generation counters, brokers, supervisors, retry state machines,
or multi-group logic. Recovery becomes justified only after the single-session
path is stable.

Since 0.2.6, a protected Global file-mapping gate authorizes the launching user,
SYSTEM, LocalService, and Administrators. Its random 128-bit session ID names
the actual frame mapping and event. This is identity-level authorization, not
process attestation.
