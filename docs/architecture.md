# Architecture

SBMS keeps policy in Rust and limits the C++ driver to the WDF/IddCx boundary.

```text
tray / CLI
  -> mapping lifecycle
  -> protected session-mode gate
  -> software display device
  -> minimal UMDF indirect-display driver
  -> Desktop Duplication
  -> D3D11 scaling shader
  -> flip-model target window
```

The v5 gate contains only one session's width, height and rational refresh
rate. Its ACL permits the launching user, SYSTEM, LocalService and
Administrators. The IDD publishes that mode and drains the IddCx swap chain; it
does not copy frames into shared memory.

Pixels remain on the GPU in the Rust renderer. For reductions up to 2:1 per
axis, the shader integrates source-pixel area and suppresses high-frequency
subpixel colour fringing. Other ratios use bilinear sampling.

Shutdown releases input capture and rendering, restores migrated windows,
closes the software-device handle, waits for the virtual source to disappear,
and restores the saved physical topology.
