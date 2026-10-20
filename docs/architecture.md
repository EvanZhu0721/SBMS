# Architecture

SBMS keeps policy in Rust and limits the C++ driver to the WDF/IddCx boundary.

```text
tray / CLI
  -> mapping plan lifecycle
  -> protected multi-output session gate
  -> software display device
  -> minimal UMDF indirect-display driver (one stable connector per group)
  -> mirror groups: Desktop Duplication -> D3D11 scaling -> target window
  -> stream-only groups: virtual desktop stays available to an external capturer
```

The v7 gate contains up to sixteen connector-indexed modes. Its ACL permits the
launching user, SYSTEM, LocalService and Administrators. One IDD adapter
publishes every requested connector and drains their IddCx swap chains; it does
not copy frames into shared memory. Connector indexes are stable identities and
must not be inferred from Windows enumeration order.

Pixels remain on the GPU in the Rust renderer. Quality sampling first applies
selective subpixel colour-fringe suppression. A reduction on either axis uses
separable, scale-aware Lanczos3; enlargement uses EWA Lanczos4Sharpest for
luminance, bilinear chroma and anti-ringing. At 1:1, only fringe suppression is
applied, without resampling.

The optional high-performance path retains the previous sampler: when both axes
shrink by more than 1:1 and at most 2:1, it integrates source-pixel area and
suppresses subpixel colour fringing; other ratios use bilinear sampling. The
automatic default selects this path unless a hardware adapter explicitly reports
non-unified memory. Per-group settings can override that default, enable full-frame
copying, or keep presenting static content at the target refresh rate. See
[configuration.md](configuration.md#rendering-options) for the settings and
automatic-selection limits.

The whole plan starts and stops as one transaction. Startup creates and
confirms every virtual source before any mirror renderer starts. Shutdown
releases all input capture and rendering, restores migrated windows, closes the
single software-device handle, waits for every planned connector to disappear,
and restores the saved physical topology once.

User configuration is stored as at most three named profiles in
`%LOCALAPPDATA%\SBMS\config-profiles-v1.json`. The collection, active profile
and per-profile revisions are committed atomically under a cross-process lock.
On first use, the existing `config-v2.json` is preserved and imported as the
`default` profile. The tray supports revision-checked live reloads without
changing a mapping that is already running. See
[configuration.md](configuration.md) for the storage, CLI, JSON and Rust API
contracts.

An overwrite upgrade first asks the installed tray to exit cleanly so its
in-memory edits are persisted. Setup then copies the current configuration and
display overrides into a SHA-256-verified timestamped snapshot under
`%LOCALAPPDATA%\SBMS\upgrade-backups`. Failure to stop the tray or verify the
snapshot cancels the overwrite; normal uninstall remains responsible for
removing user data.

See [mapping-plan.md](mapping-plan.md) for the Rust and JSON interfaces.
