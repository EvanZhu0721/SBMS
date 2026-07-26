# 0001 Core boundaries

Accepted: 2026-07-27

The core owns one mapping session. Rust owns display discovery, lifecycle,
errors, target rendering, and concurrency policy. C++ owns only the WDF/IddCx
boundary and publishes the swapchain's latest BGRA frame.

External GDI capture and DXGI Desktop Duplication are not the authoritative
pixel boundary for this IDD. The driver therefore copies the surface handed to
it by IddCx into one fixed shared-memory slot. Rust validates that slot and
draws it with `StretchDIBits`. This is intentionally CPU-heavy but exposes the
entire data path without a second capture stack.

The current product fails fast on topology change. It does not import the old
generation counters, brokers, supervisors, retry state machines, or multi-group
logic. Recovery becomes justified only after the single-session path is stable.

The shared channel currently uses a null DACL and exists only for an elevated
mapping session. This is accepted for local test signing, not for distribution.
