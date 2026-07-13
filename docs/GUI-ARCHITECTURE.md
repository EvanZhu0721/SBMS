# GUI architecture

The WinForms executable is a composition shell. `MainForm` owns controls, translates user input into bridge requests, and renders state; it does not own child-process lifetimes or topology polling.

## Ownership

| Component | Owns | Must not own |
| --- | --- | --- |
| `MainForm` | controls, mapping rows, native arguments, user-visible logs, service coordination | child `Process` instances, stop-event handles, topology polling loops |
| `BridgeLifecycle` | `Idle` / `Starting` / `Running` / `Recovering` / `Stopping` / `Error`, generation tokens, last error | Windows resources or controls |
| `NativeProcessSupervisor` | primary and BETA native processes, UTF-8 output, graceful close and kill fallback | lifecycle policy or UI |
| `DeviceHostSupervisor` | device-host process, output snapshot, named stop event, four-second stop timeout | topology/recovery decisions |
| `TopologyDiscoveryService` | parsing and classifying `--list` output, stable signatures | controls or process creation |
| `DisplayModeService` | Win32 mode discovery, supported-mode selection, orientation normalization, and mode application | controls, process ownership, or recovery policy |
| `TopologyRecoveryService` | cancellable topology/source/mode polling | mapping-row mutation or process restart policy |
| `XmlConfigurationStore` | versioned XML load/save and atomic temporary-file replacement | control state |
| `ResolutionMath` | parsing and resolution calculations | UI formatting decisions beyond resolution text |

## Runtime flow

1. A start request creates a new lifecycle generation and enters `Starting`.
2. `MainForm` validates the mapping and asks the host/native supervisors to create the required resources.
3. Successful single-output, multi-group, and stream-only starts all enter `Running`.
4. Native exit codes 100/101 enter `Recovering`. Recovery polling keeps the device host alive, re-discovers current display identities, then rebuilds native output without a retry-count fuse.
5. Stop increments the generation before touching processes. Every delayed process callback and polling loop checks its captured generation, so work from an older session cannot mutate a newer one.
6. A recovery failure enters `Error`. If the host is intentionally kept alive, configuration remains locked and Stop remains available for deterministic cleanup.

All lifecycle transitions are written to the session log with the previous state, new state, generation, and reason.

## Preserved product behavior

- The mapping-first cards and calculate/configure/start-stop interaction remain the primary workflow.
- Stream-only mode is a valid `Running` state with only the device host alive.
- Multi-group recovery preserves the virtual-display host while native outputs restart.
- Tray/lightweight behavior remains resource-aware.
- Native graceful shutdown remains `CloseMainWindow` plus `WM_CLOSE`, a three-second wait, then kill fallback.
- Device-host shutdown remains the `Local\SBMSDeviceHostStop` event, a four-second wait, then kill fallback.

## Verification boundaries

- `test-sbms-gui-core.ps1` covers lifecycle generations, resolution math, topology parsing/recovery, and configuration round trips without administrator privileges.
- `test-sbms-gui.ps1` compiles an as-invoker probe host and checks the configuration, risk, stream, and lock surfaces.
- Hardware-sensitive topology/driver behavior still requires a real Windows display session; source-only tests do not replace that evidence.
