# Current

## Baseline

- Branch: `rust-vnext`
- Product core: one Rust process owns one virtual display and one mapping worker.
- Driver boundary: the C++/WDK IDD reports one 1920x1080@60 monitor and publishes
  IddCx swapchain surfaces through two shared BGRA frame slots. It contains no
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
- Signed driver `0.2.6.0` completed a real source-to-physical pixel check, five
  consecutive start/stop cycles, and concurrent-session rejection.
- Rust draws directly from a leased shared slot; the driver drops instead of
  waiting when the other slot is still being read.
- The channel gate and random per-session objects authorize the launching user,
  SYSTEM, LocalService, and Administrators.

## Deliberately absent

GUI, installer, configuration persistence, input capture, window migration,
multi-mapping, automatic recovery, and a general test framework.

## Known boundary

The frame channel is fixed at 1920x1080 BGRA. GPU readback, one full-frame
driver copy, and GDI output remain. `COLORONCOLOR` scaling trades smoothness for
substantially lower CPU use. The ACL is identity-level, not process-level:
other processes under a trusted SID remain trusted. GPU transport, dynamic
modes, rotation, cursor semantics, and input are future features.
