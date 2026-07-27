# Start-stop lifecycle

Status: complete.

## Start

1. Resolve one active physical target by stable monitor device path.
2. Acquire and populate the protected one-session mode gate.
3. Create and own the software-device handle.
4. Wait at most 15 seconds for exactly one active SBMS source.
5. Re-resolve the target after the topology change.
6. Start the GPU mirror and wait at most 10 seconds for its first successful
   presentation.

Any failure unwinds owned resources in reverse order.

## Stop

1. Signal the mirror worker.
2. Wait at most 2 seconds; detach rather than hang the caller forever.
3. Close the software-device handle.
4. Wait at most 15 seconds for the virtual source to leave active topology.

`Drop` performs the same cleanup as a last resort. Repeated `stop` is safe.

Signed driver `0.2.6.0` completed five consecutive sessions. Every
cycle printed `running`, printed `stopped`, exited zero, and restored the
two-display baseline. A simultaneous second session failed before device
creation with an explicit ownership error. A separate 30-second session exited
cleanly and left no virtual display active.

Signed 1.1.3 also completed first-frame confirmation and normal stop through
the Desktop Duplication/D3D11 presentation path.
