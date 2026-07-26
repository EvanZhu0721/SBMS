# Start-stop lifecycle

Status: complete.

## Start

1. Resolve one active physical target by stable monitor device path.
2. Acquire the one-session lock and wait at most 5 seconds for the previous
   driver's shared frame objects to close.
3. Create and own the software-device handle.
4. Wait at most 15 seconds for exactly one active SBMS source.
5. Re-resolve the target after the topology change.
6. Start the mirror and wait at most 10 seconds for its first frame.

Any failure unwinds owned resources in reverse order.

## Stop

1. Signal the mirror worker.
2. Wait at most 2 seconds; detach rather than hang the caller forever.
3. Close the software-device handle.
4. Wait at most 15 seconds for the virtual source to leave active topology.

`Drop` performs the same cleanup as a last resort. Repeated `stop` is safe.

Signed driver `0.2.5.0` completed five consecutive one-second sessions. Every
cycle printed `running`, printed `stopped`, exited zero, and restored the
two-display baseline. A simultaneous second session failed before device
creation with an explicit ownership error.
