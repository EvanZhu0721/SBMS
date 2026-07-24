# GUI architecture

The WinForms executable is a composition shell. `MainForm` owns controls, translates user input into bridge requests, and renders state; it does not own child-process lifetimes or topology polling.

## Ownership

| Component | Owns | Must not own |
| --- | --- | --- |
| `MainForm` | controls, mapping rows, native arguments, user-visible logs, service coordination | child `Process` instances, stop-event handles, topology polling loops |
| `BridgeLifecycle` | `Idle` / `Starting` / `Running` / `Recovering` / `Stopping` / `Error`, generation tokens, last error | Windows resources or controls |
| `LifecycleRecoveryPolicy` | one bounded recovery episode: three attempts, 250/500/1000 ms backoff, 30-second failure window, first/last failure | Windows resources, controls, or process creation |
| `ChildProcessJob` | launch gates and kill-on-close containment for host/native children | lifecycle policy or window restoration |
| `NativeProcessSupervisor` | primary and BETA native processes, UTF-8 output, graceful close and kill fallback | lifecycle policy or UI |
| `DeviceHostSupervisor` | device-host process, output snapshot, named stop event, four-second stop timeout | topology/recovery decisions |
| `WindowMigrationJournal` / `SBMSRecoveryBroker` | durable PREPARE/RESOLVED rectangle and `WINDOWPLACEMENT` records, per-session recovery lease, current-work-area clamping, and out-of-job recovery after GUI death | arbitrary DisplayConfig, driver, or boot recovery |
| `TopologyDiscoveryService` | parsing and classifying `--list` output, stable signatures | controls or process creation |
| `DisplayModeService` | Win32 mode discovery, supported-mode selection, orientation normalization, and mode application | controls, process ownership, or recovery policy |
| `TopologyRecoveryService` | cancellable topology/source/mode polling | mapping-row mutation or process restart policy |
| `XmlConfigurationStore` | versioned XML load/save and atomic temporary-file replacement | control state |
| `ResolutionMath` | parsing and resolution calculations | UI formatting decisions beyond resolution text |

## Runtime flow

1. A start request creates a new lifecycle generation and enters `Starting`.
   A session-local singleton mutex prevents a second GUI from sharing the global host stop event or recovery root.
2. `MainForm` validates the mapping and asks the host/native supervisors to create the required resources.
3. Successful single-output, multi-group, and stream-only starts all enter `Running`.
4. Native exit codes 100/101 enter `Recovering`. One episode permits at most three delayed attempts (250/500/1000 ms) inside a 30-second failure window; duplicate triggers coalesce.
5. Terminal failure advances the lifecycle generation, cancels delayed work, stops all native processes, restores pending window journals, stops the device host, waits up to five seconds for virtual displays to clear, then records `Error` with causal and cleanup failures.
6. Every host/native launch waits on a per-process gate until the GUI assigns it to the kill-on-close Job.
7. With window migration enabled, the broker acknowledges readiness before bridge resources start. Native output flushes PREPARE before each move and RESOLVED after restoration; the broker validates GUI PID plus start time and gets five seconds to recover after GUI death.
8. Stop increments the generation before touching processes. Every delayed callback and polling loop checks its captured generation, so work from an older session cannot mutate a newer one.

All lifecycle transitions are written to the session log with the previous state, new state, generation, and reason.

## Preserved product behavior

- The mapping-first cards and calculate/configure/start-stop interaction remain the primary workflow.
- Stream-only mode is a valid `Running` state with only the device host alive.
- Multi-group recovery preserves the virtual-display host while native outputs restart.
- Tray/lightweight behavior remains resource-aware.
- Native graceful shutdown remains `CloseMainWindow` plus `WM_CLOSE`, a three-second wait, then a recorded kill fallback.
- Device-host shutdown remains the `Local\SBMSDeviceHostStop` event, a four-second wait, then a recorded kill fallback.

## Verification boundaries

- `test-sbms-gui-core.ps1` covers lifecycle generations, resolution math, topology parsing/recovery, and configuration round trips without administrator privileges.
- `test-sbms-process-job.ps1` proves normal and abrupt-owner Job cleanup with harmless child processes.
- `test-sbms-start-gate.ps1` proves native and host processes cannot perform their first desktop/device action before Job assignment.
- `test-sbms-supervisors.ps1` drives the real supervisors through Job assignment, duplicate Start rejection, ACK timeout cleanup, and bounded forced Stop using a harmless gated child.
- `test-sbms-recovery-broker.ps1` hard-kills a harmless owner and verifies a real Win32 window returns from simulated virtual coordinates to its original rectangle.
- `test-sbms-gui.ps1` compiles an as-invoker probe host and checks the configuration, risk, stream, and lock surfaces.
- Hardware-sensitive topology/driver behavior still requires a real Windows display session; source-only tests do not replace that evidence.
