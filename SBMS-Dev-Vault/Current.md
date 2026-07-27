# Current

## Baseline

- Branch: `rust-vnext`
- Product core: one Rust process owns one virtual display and one mapping worker.
- Driver boundary: the C++/WDK IDD reports one 3840x2160@240 test mode and publishes
  IddCx swapchain surfaces through two shared BGRA frame slots. It contains no
  target-selection or lifecycle policy.
- Target identity: active DisplayConfig `monitorDevicePath`. `\\.\DISPLAYn` is
  session-local and is never a persisted identity.

## Implemented

- `sbms list` exposes stable active-display IDs and current geometry.
- `sbms create` owns the raw virtual-device lifetime.
- `sbms map --target <id>` validates one physical target, creates the virtual
  source, waits for active topology, migrates eligible visible standard
  top-level windows, starts a one-path mirror, and reports success only after
  the first copied frame. A 250 ms scanner continues migrating newly opened
  windows and tracked windows moved back to the target.
- Stop order is window restoration first, mirror worker second, and
  virtual-device handle third. After a bounded wait for topology removal, SBMS
  performs a final placement reconciliation against the physical-only layout.
- Normal and maximized windows, including one opened after startup, completed
  local round trips across displays with different DPI settings.
- The frame channel and protected session gate use protocol v3.
- Rust draws directly from a leased shared slot; the driver drops instead of
  waiting when the other slot is still being read.
- The channel gate and random per-session objects authorize the launching user,
  SYSTEM, LocalService, and Administrators.

## Deliberately absent

GUI, installer, configuration persistence, input capture, multi-mapping,
automatic topology recovery, and a general test framework.

## Known boundary

The virtual mode is fixed at 3840x2160@240 BGRA, but that mode is not a 240 fps
performance guarantee. CPU staging readback, one full-frame
driver-to-shared-memory copy, and GDI output remain. The ACL is identity-level,
not process-level: other processes under a trusted SID remain trusted.

Window migration covers eligible live standard top-level windows only. UIPI or
higher-integrity boundaries, hung applications, self-positioning windows, and
windows closed or recreated during a session prevent any general lossless
guarantee. Minimized windows are deliberately left untouched because their
workspace-coordinate placement is not safe to rewrite generically across DPI
and taskbar layouts. GPU transport, dynamic modes, rotation, cursor semantics,
input, and general runtime topology recovery remain future work.
