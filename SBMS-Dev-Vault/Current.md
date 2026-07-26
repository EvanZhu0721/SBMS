# Current

## Baseline

- Branch: `rust-vnext`
- Product core: one Rust process owns one virtual display and one mapping worker.
- Driver boundary: the C++/WDK IDD reports one 1920x1080@60 monitor and copies
  IddCx swapchain surfaces into one shared BGRA frame slot. It contains no
  target-selection or lifecycle policy.
- Target identity: active DisplayConfig `monitorDevicePath`. `\\.\DISPLAYn` is
  session-local and is never a persisted identity.

## Implemented

- `sbms list` exposes stable active-display IDs and current geometry.
- `sbms create` owns the raw virtual-device lifetime.
- `sbms map --target <id>` validates one physical target, creates the virtual
  source, waits for active topology, starts a one-path mirror, and reports
  success only after the first copied frame.
- Stop order is mirror worker first, virtual-device handle second, then a
  bounded wait for topology removal.
- Signed driver `0.2.5.0` completed a real source-to-physical pixel check, five
  consecutive start/stop cycles, and concurrent-session rejection.

## Deliberately absent

GUI, installer, configuration persistence, input capture, window migration,
multi-mapping, automatic recovery, and a general test framework.

## Known boundary

The frame channel is fixed at 1920x1080 BGRA, CPU-copied, and has a permissive
DACL for the elevated Rust process and UMDF host. A product release must tighten
the ACL. GPU transport, dynamic modes, rotation, cursor semantics, and input are
future features, not hidden claims of this baseline.
