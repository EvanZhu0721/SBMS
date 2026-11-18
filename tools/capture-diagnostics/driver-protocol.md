# Capture diagnostics driver protocol v1

This opt-in driver uses hardware ID `SBMS\CaptureDiagnostics`, package/service/DLL
name `SBMSCaptureDiagnostics`, its own monitor container GUID, and the unchanged
592-byte v8 gate layout under `Global\SBMSCaptureSession-v8`. Build with
`build-driver.ps1 -CaptureDiagnostics`; output is `target/driver-capture-diagnostics`.
The product package, gate and build output remain separate.

The host creates `Global\SBMSCaptureDiagnostics-v1` before creating the software
device, grants its UMDF host read/write access, and holds the mapping for the whole
run. Treat `ERROR_ALREADY_EXISTS` as a live previous run and stop; never reset an
existing mapping. Create only one software device per mapping. Initialize 18016
zero bytes, then set the header inputs below. The driver opens once per device;
device, adapter, monitor and drain references pin this exact mapping. It never
reopens by name from a callback. `driverPid` atomically reserves it for one device
and is cleared only after the final reference is released.

All structures use native x64 C layout (`repr(C)` in Rust), little endian, with
8-byte alignment. Exact declarations and enums are in `driver/CaptureDiagnostics.h`.
Header inputs are immutable after software-device creation. Driver outputs use
`sequence`: read an even value, copy the entire mapping, acquire-fence and reread;
accept only equal even values. A nonzero even value confirms at least one driver
publication. Do not spin forever on an odd sequence after a driver crash.

## Layout

Header: 96 bytes, starting at 0.

| Offset | Type | Field |
| --- | --- | --- |
| 0, 4, 8, 12 | u32 each | magic=0x53434431, version=1, size=18016, eventCapacity=256 |
| 16 | u64 | sessionId: host-generated nonzero run identity |
| 24, 28, 32 | u32, u32, i32 | requestedAdapterSet (0 default, 1 explicit), requestedLow, requestedHigh |
| 36 | i32 | sequence |
| 40, 48, 56, 64 | u64 each | writtenEvents, overwrittenEvents, nextGeneration, deviceGeneration |
| 72, 76 | i32 each | driverPid, deviceStatus (latest negative device-level result) |
| 80 | u64 | lastEventTick |
| 88, 92 | u32 each | deviceAttached, droppedEvents |

Monitor snapshots: 16 entries of 96 bytes starting at 96, indexed by connector.

| Offset in entry | Type | Field |
| --- | --- | --- |
| 0, 8, 16, 24 | u64 each | generation, assignTick, firstFrameTick, lastEventTick |
| 32, 36 | u32 each | active, state |
| 40, 44, 48, 52 | u32, i32, u32, i32 | assignLow, assignHigh, actualLow, actualHigh |
| 56, 60, 64 | i32 each | createDeviceResult, setDeviceResult, lastError |
| 68 | u32 | lastStage |
| 72, 76, 80, 84 | u32 each | assignCount, unassignCount, stopCount, firstFrameCount |
| 88, 92 | u32 each | flags, driverBuild=0x20261105 |

Flags: 1=D3D creation attempted, 2=SetDevice attempted, 4=actual D3D LUID obtained,
8=first frame processed. A zero result without its attempted flag is not success.
States: 0=None, 1=Assigned, 2=Draining, 3=FirstFrame, 4=Stopping, 5=Stopped,
6=Failed, 7=Unassigned, 8=Removed. Historical adapter LUIDs remain visible after
unassignment; only `active != 0` indicates a currently active assignment. A fresh
assignment clears its predecessor's adapter/result/timing fields. Delayed events
from old assignment generations cannot update the current snapshot.
`driverBuild` is written by the driver on attach and retained across assignments;
the host must check it against `0x20261105` to detect an older installed package.
`DeviceAttach.detail` carries the same build identity.

Events: 256 entries of 64 bytes starting at 1632.

| Offset in entry | Type | Field |
| --- | --- | --- |
| 0, 8, 16, 24 | u64 each | serial, tick, deviceGeneration, generation |
| 32, 36, 40, 44 | u32, u32, i32, u32 | connector, kind, result, stage |
| 48, 52, 56 | u32, i32, u64 | adapterLow, adapterHigh, detail |

All ticks are `GetTickCount64()` milliseconds, comparable to host ticks on the
same machine. `serial` is one-based; ring slot is `(serial - 1) % 256`. Retained
serials are `max(1, writtenEvents - 255)..writtenEvents`. `overwrittenEvents`
counts overwritten historical entries; `droppedEvents` is zero with this blocking
single-writer implementation. Export both counts and any reader-observed serial
gaps. Snapshots remain current even when history overflows. Connector `0xffffffff`
marks device-wide events; assignment generation zero means not assignment-bound.

Kind numbers: 1=DeviceAttach, 2=DeviceDetach, 3=AdapterInit, 4=RenderPreference,
5=MonitorCreate, 6=MonitorArrival, 7=MonitorCleanup, 8=Assign, 9=D3DDevice,
10=ActualAdapter, 11=SetDevice, 12=DrainStart, 13=FirstFrame, 14=Unassign,
15=StopRequested, 16=DrainStopped, 17=Error, 18=StopTimeout.

Stage numbers: 0=None, 1=DeviceInitialize, 2=AdapterInit, 3=MonitorCreate,
4=MonitorArrival, 5=Factory, 6=EnumAdapter, 7=D3DCreate, 8=QueryDevice,
9=ActualAdapter, 10=SetDevice, 11=Allocate, 12=StopEvent, 13=Thread,
14=Acquire, 15=FinishFrame, 16=WaitFrame, 17=SessionConfig.

`RenderPreference.detail=1` means `IddCxAdapterSetRenderAdapter` was called before
any monitor arrived; 0 means the default preference was retained. This is a void
API and a preference, not proof of adapter selection. `Assign` reports the OS's
render LUID; `ActualAdapter` reads back the actual D3D device's DXGI adapter LUID.
`MonitorArrival.detail` is the OS target ID. `AdapterInit.detail` is 0 for the
init call and 1 for its completion callback. Negative HRESULT/NTSTATUS values
retain their raw 32-bit bit pattern. `FirstFrame` is emitted once per assignment,
only after both acquiring a frame and finishing its processing succeed. It is a
driver drain observation, not proof that the host's separate capture succeeded.
