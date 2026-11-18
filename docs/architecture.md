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

The v9 gate contains up to sixteen connector-indexed modes, one preferred render
adapter LUID and per-connector actual render-adapter receipts (856 bytes total).
Its ACL permits the
launching user, SYSTEM, LocalService and Administrators. One IDD adapter
publishes every requested connector and drains their IddCx swap chains; it does
not copy frames into shared memory. Connector indexes are stable identities and
must not be inferred from Windows enumeration order. After each successful
`IddCxMonitorArrival`, the driver publishes its `OsAdapterLuid` and `OsTargetId`
into that connector's response slot, using a sequence counter to prevent partial
reads during initial publication or driver restart.
Rust matches these identities directly against CCD targets; identifying a virtual
connector does not require a PnP monitor path or Container ID lookup. The driver
holds each monitor's original session mapping until monitor cleanup, and startup
waits for unpublished identity responses within its existing deadline. The
matching v9 driver and application must be deployed together. A v9 product
session also reserves the old v8 name, preventing older applications and
diagnostic packages from starting a conflicting session. The independent
diagnostic driver retains its separate v8 protocol.

Before device creation, SBMS resolves an explicit GPU's persistent device path
to its current LUID. In Automatic mode it requests a LUID only when one known
hardware GPU drives all enabled local mirror targets. The driver passes a
requested LUID to `IddCxAdapterSetRenderAdapter` before reporting monitors. After
successful D3D device and swapchain setup, it publishes the actual adapter LUID.
Reassignment, unassignment and cleanup invalidate that receipt. Requested and
actual GPUs can differ. The controller reads receipts and emits UI/log updates
only when the actual assignment changes.

Actual graphics hosts initialize native output routing before their first
DXGI/D3D operation, including automatic sampling detection. A process-local
MinHook intercepts `win32u!NtGdiDdDDIGetCachedHybridQueryValue` and returns
`D3DKMT_GPU_PREFERENCE_STATE_UNSPECIFIED`, preventing preference-based DXGI output
reparenting. The hook lasts until process exit and does not alter system DLL
files or other processes. It uses an undocumented export; installation failures
remain explicit startup errors. Maintenance commands and forwarding launchers
do not install it. See [rendering.md](rendering.md#gpu-routing) for the diagnostic
escape setting.

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

The whole mapping starts and stops as one transaction. `WindowMigration` captures
an independent original layout before device creation and binds a stable virtual
source ID for each group. Startup creates the renderer before beginning window
migration. The scanner subsequently handles eligible windows found on the
physical target. Windows already minimized are excluded from startup migration.

Valid window layouts map proportionally between stable monitor bounds, preserving
arrangement and minimized/maximized state, including restore-to-maximized flags.
Taskbar work-area changes do not change this proportional mapping. Only invalid
or out-of-bounds layouts use a default cascade within the work area, at three
quarters of its width and height, subject to application size constraints.
Internal layout coordinates are screen coordinates; `WINDOWPLACEMENT` work-area
coordinates are converted at the API boundary.

Shutdown transfers tracking into a single-use `WindowRestorePlan`:

1. Freeze the scene: join scanners, release runtime claims, and capture windows
   currently on each virtual source, including new and minimized windows.
   Separately capture physical-screen windows with their exact geometry and state.
2. Stop rendering, then transfer the selected windows to their physical targets
   while the virtual sources still exist. Record the actual result of each
   successful transfer; failed entries are not added to later repair.
3. Remove the virtual device and restore the saved physical topology. Consume the
   plan to repair only physical windows, including successful transfers, that
   Windows changed during topology restoration. Unchanged windows receive no
   positioning calls. Mapping owns the device lifecycle; the plan owns selection,
   window transfer, preservation, and accumulated errors.

`RestoreMode::StartupRollback` uses the independently saved startup layout,
including captured windows not yet actively moved. Windows currently on another
physical screen are not reclaimed. A missing source retains only historical
windows with no current monitor. Source and target matching use stable display
IDs, never reused desktop coordinates.

Source migration and physical preservation share one execution path. Necessary
monitor/DPI preparation precedes final geometry submission, with bounded
observation of the requested position and state. External window mutations are
asynchronous; an already unresponsive window is rejected before submission.
Application size constraints remain respected. Physical preservation additionally
retains exact geometry and the normal restore rectangle's separate monitor anchor.

There is no blanket temporary minimization, staging/final replay, or batch retry
after work-area changes. Failures remain errors, and one failed window does not
prevent remaining windows or device cleanup from being processed. An asynchronous
request already posted to an application cannot be cancelled if that application
subsequently hangs. Dropping runtime tracking only joins its scanner and releases
claims; it never performs an implicit second window restoration.

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
