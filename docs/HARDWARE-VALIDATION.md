# Hardware validation

`test-sbms-hardware.ps1` records evidence for the hardware-sensitive GUI acceptance criteria. It does not install or remove a driver, start or stop SBMS, or change display topology. It writes a local evidence directory and, when available, runs `SBMSNative.exe --list` as a read-only probe.

Driver installation is a separate, explicitly authorized operation. The current installation entry point is:

```powershell
.\build-sbms-driver.ps1
.\install-sbms-driver.ps1 -Force
```

Do not use `tools/install-idd-driver.ps1` for acceptance testing. It is a legacy prototype path with direct certificate, registry, and system-directory mutations.

## Preflight

Run the audit before changing the driver or display session:

```powershell
.\test-sbms-hardware.ps1 -Scenario AuditOnly
```

The audit captures the OS/session, privilege state, display and SBMS-related PnP records, signed display drivers, related processes, native display enumeration, and available GUI logs.

## Scenario matrix

Run each scenario separately so every result has its own evidence package. Start the corresponding configuration in an elevated SBMS GUI, then launch the observer from another elevated PowerShell window.

Single physical output backed by one virtual source:

```powershell
.\test-sbms-hardware.ps1 `
  -Scenario SingleOutput `
  -AcknowledgeSystemChanges `
  -ExpectedVirtualCount 1 `
  -ExpectedNativeCount 1
```

Two physical-output mapping groups backed by two virtual sources:

```powershell
.\test-sbms-hardware.ps1 `
  -Scenario MultiGroup `
  -AcknowledgeSystemChanges `
  -ExpectedVirtualCount 2 `
  -ExpectedNativeCount 2
```

For a mixed multi-group configuration, set `ExpectedNativeCount` to the number of non-stream-only groups while keeping `ExpectedVirtualCount` equal to the total enabled group count.

One stream-only virtual desktop with no native output process:

```powershell
.\test-sbms-hardware.ps1 `
  -Scenario StreamOnly `
  -AcknowledgeSystemChanges `
  -ExpectedVirtualCount 1 `
  -ExpectedNativeCount 0
```

Topology recovery begins from a stable single-output session. After the script prints the recovery prompt, edit the active display topology in Windows Settings. The evidence must show the original native process disappear, a different native PID appear, the device-host PID remain unchanged, the final 1/1 runtime stabilize, and the GUI log contain `Running -> Recovering -> Running`.

```powershell
.\test-sbms-hardware.ps1 `
  -Scenario TopologyRecovery `
  -AcknowledgeSystemChanges `
  -TimeoutSeconds 120
```

There is intentionally no `All` scenario. Combining independently configured sessions into one result makes failures and missing evidence ambiguous.

## Result contract

Each invocation writes `summary.json`, raw native enumeration samples, PnP/driver/process snapshots, and copied GUI logs under the reported evidence directory.

- `PASS` / exit `0`: every required assertion and critical evidence check passed.
- `FAIL` / exit `1`: an observed requirement was contradicted.
- `INCONCLUSIVE` / exit `2`: native enumeration or the current GUI lifecycle log was unavailable, so the scenario cannot be accepted.

A source-only test, an `AuditOnly` result, or an `INCONCLUSIVE` hardware result must not be used to close a hardware-sensitive issue.
