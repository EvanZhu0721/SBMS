# Safe hardware lab

This document defines the safety boundary for SBMS driver and hardware validation. It is a design and operating contract, not an instruction to change the current machine.

Phase 1 delivers a boot-lab foundation, simulated tests, and a reviewable safety contract only. It does **not** implement the full Gate A inventory, remote health acknowledgement, display/driver rollback, or immutable evidence store described as later-stage requirements below. Do not enable Test Signing, install or remove a display driver, change a boot entry, register a SYSTEM task, or reboot while reviewing Phase 1.

## Why this exists

The July 13 incident must not be attributed to Test Signing without evidence:

- The elevation helper was created at 22:25 and executed only `bcdedit.exe /set testsigning on`, then wrote its result JSON. It did not touch displays, drivers, PnP devices, services, or processes.
- A BCD Test Signing change affects Code Integrity only after the next boot.
- The user later confirmed that the discrete-GPU displays had already lost signal before the first recorded reboot after the BCD change. Persistent Windows logs do not timestamp that first visual symptom, so this is user-observed evidence rather than an independently logged event. Because the setting takes effect only after reboot, Test Signing was not active at the reported onset and cannot directly explain that pre-reboot onset.
- The initial operation that deactivated the physical display paths was not captured by the available logs. Later evidence places the symptom at the Windows DisplayConfig/CCD path layer, but does not identify the caller.

Test Signing and later dirty reboots remain relevant to the subsequent timeline, but correlation after those reboots is not evidence for the initial trigger.

## Lab boundary

Use a dedicated test PC whenever a boot-policy or display-driver change is required. A VM is suitable for installer, state-machine, rollback, and evidence-contract testing, but it cannot close acceptance criteria that require real GPU outputs, EDID, monitor orientation, or Indirect Display hardware behavior.

The lab PC must have:

- a one-time clone of the repository at a fixed path, pinned to a recorded commit with a clean worktree;
- an evidence directory outside `%TEMP%`;
- a second computer with tested SSH access to the lab PC;
- a separately usable recovery display path, preferably a physical monitor connected to an independent iGPU output;
- the BitLocker recovery key verified from the second computer before any BCD change;
- no unrelated virtual-display driver, stale test display package, or automatic SBMS startup outside the run allow-list.

Do not pull, rebuild, edit, or replace the payload between Gate A and Gate C. The INF, CAT, DLL, scripts, commit, and plan are hashed once and bound to one Run ID.

## Boot sequence

Each boot has one purpose. Never combine a BCD change and driver installation in the same boot transition.

1. **Boot 0 — baseline:** create the one-time clone, capture the baseline, verify the clean worktree and payload hashes, and pass Gate A.
2. **Recovery rehearsal:** create a one-time clone with no Test Signing delta, arm the fixed-deadline watchdog, select that clone with one-time `bootsequence`, and reboot. Prove that the watchdog requests at most one return reboot and that the original boot order resumes. This deliberately exercises BCD clone/selection state, but does not install a driver or enable Test Signing.
3. **Boot 1 — boot-policy isolation:** after Gate B approval, apply only the planned BCD change and reboot. Do not install a driver. Verify physical output, SSH, Code Integrity state, PnP state, and DisplayConfig paths before proceeding.
4. **Boot 2 — driver isolation:** after Gate C approval, install only the exact hashed SBMS package owned by the Run ID and reboot only if the plan requires it.
5. **Acceptance boots:** run the four hardware scenarios independently as described in [Hardware validation](HARDWARE-VALIDATION.md).
6. **Rollback boot:** restore the recorded BCD and startup-task state, remove only the package installed by this Run ID, reboot, and compare the final state with the baseline.

Any failed or inconclusive gate stops the sequence. A later gate cannot waive an earlier failure.

## SYSTEM watchdog target

The complete lab design must eventually create a one-run watchdog under `SYSTEM` and bind it to the Run ID and hashed rollback plan. The target watchdog must:

- contain the pre-change BCD values and the exact startup-task state;
- start at boot independently of user logon;
- wait for a fresh health acknowledgement tied to the same Run ID;
- restore only run-owned changes and request one rollback reboot if acknowledgement does not arrive before the deadline;
- write an append-only journal for arm, boot, acknowledgement, rollback attempt, command exit code, and verification;
- expire and remove itself after a verified healthy acknowledgement or verified rollback.

The health acknowledgement must require both an active physical display path and a successful remote check. A GUI process starting is not proof of display health.

Phase 1 is intentionally narrower: its watchdog is a fixed-deadline, one-shot return-reboot guard for the cloned loader. It has no health acknowledgement/disarm path and does not restore display, PnP, or driver state. Its minimum recovery action must remain self-contained so a missing module or manifest cannot turn the deadline into a fail-open path. This foundation is not authorization for a Test Signing or driver test.

The Phase 1 command surface exposes a `TestSigning` profile only for adapter-based state-machine tests. The real Windows adapter hard-blocks `TestSigning` `Prepare` and `Arm` until Gate A, second-computer SSH proof, and BitLocker recovery proof are implemented and verified. The only profile eligible for the first reviewed recovery exercise is the default `RecoveryDrill` profile.

The watchdog must never delete every matching display package, rewrite arbitrary Enum registry keys, restore an unverified display topology, or stop unrelated processes.

## SSH and recovery proof

An SSH configuration is not proof of recovery. Before Gate B, test it from the second computer after a no-change reboot:

- connect using the intended key and host address;
- confirm the lab computer name, boot time, Run ID, and administrator capability;
- read the watchdog state and evidence directory;
- execute the non-mutating health command used for acknowledgement;
- record the remote command, exit code, and timestamp in the run evidence.

If SSH depends on the same GPU session, user logon, VPN, or network service being tested, it is not an independent recovery path.

## BitLocker recovery

Before Gate B:

- record BitLocker protection and encryption state;
- verify that the recovery key is accessible from the second computer without relying on the lab PC;
- record whether the planned BCD operation is expected to trigger recovery;
- define a time-bounded suspension only when the reviewed plan requires it;
- verify after rollback that protection is enabled again.

The automation must not print or copy the recovery key into repository logs. Evidence records only that an authorized operator verified access.

## Gate A — baseline target (not implemented in Phase 1)

Gate A is read-only and must pass before any watchdog or system mutation:

- repository commit, clean worktree, script hashes, and driver payload hashes are recorded;
- `AuditOnly` evidence is complete;
- BCD, Code Integrity, Secure Boot, BitLocker, pending-reboot, PnP, Driver Store, display adapters, monitor endpoints, active DisplayConfig paths, processes, services, and startup tasks are captured;
- all high-privilege `ONLOGON` and `ONSTART` entries that can launch SBMS or a virtual-display tool are identified;
- every display-class package and present virtual display is classified as allowed or blocking;
- at least one healthy physical output and the independent recovery route are proven;
- the rollback plan is generated before any apply plan.

Unknown or stale display state is a failure, not a warning.

## Gate B — boot-policy change

Gate B authorizes one global boot-policy change, not driver installation. It requires:

- a passing Gate A from the same machine and Run ID;
- no state drift since Gate A;
- a successful no-change watchdog and SSH rehearsal;
- verified BitLocker recovery readiness;
- an armed SYSTEM watchdog and a reviewed rollback plan;
- explicit acknowledgement of the exact BCD delta and target reboot.

After reboot, Gate B passes only if the physical display path, SSH, watchdog, BCD, Code Integrity, and PnP checks all match the plan. Otherwise rollback begins and Gate C remains locked.

## Gate C — driver change

Gate C authorizes only the exact hashed SBMS driver payload. It requires:

- a passing post-boot Gate B result;
- no unexpected display, driver, task, or process change;
- a valid catalog and a reviewed signer policy;
- an install plan that names the expected published INF and device instance;
- rollback ownership that distinguishes the package installed by this Run ID from pre-existing packages.

The current `install-sbms-driver.ps1` is a low-level mutation script, not a safe lab orchestrator. It must not be invoked by the lab until its destructive package/process behavior is placed behind the reviewed plan and ownership checks.

## Automatic recovery boundary

Software recovery can act only after Windows reaches the point where the SYSTEM watchdog and required services can run. It can restore a recorded BCD value, restore a recorded task state, stop a run-owned process, and remove a package installed by the same Run ID.

Software recovery cannot guarantee recovery from:

- firmware or POST failure;
- a BitLocker recovery prompt without an operator;
- failure before Task Scheduler and networking start;
- a GPU or monitor path that remains dark while Windows is otherwise healthy;
- loss of power, network, storage, or the SSH host key;
- an incorrect baseline or a display topology that Windows refuses to restore.

These cases require the independent physical output, SSH from a second computer when available, BitLocker recovery material, and manual access. The scripts must report this boundary instead of claiming fail-safe recovery.

## Evidence and result contract target

The completed lab must write hashed, Run-ID-bound artifacts: baseline, plan, payload manifest, BCD state, startup state, PnP and Driver Store inventory, DisplayConfig paths, watchdog journal, SSH proof, post-boot checks, rollback plan, rollback verification, and final summary. Phase 1 currently writes only its boot-lab manifest, snapshots, and application-level journal; these are ACL-protected review artifacts, not cryptographically immutable records.

`PASS` means the current gate's required evidence agrees with its plan. `FAIL` means a requirement was contradicted. `INCONCLUSIVE` means required evidence is missing. Only `PASS` unlocks the next gate.

## 2026-07-14 RecoveryDrill evidence

The first real `RecoveryDrill` completed on the development host under Run ID `2c129d7d-677e-401c-b495-0367ab060dda`:

- BitLocker was fully decrypted with protection off, and a live second-computer SSH session was established before Arm.
- Prepare created one exact-description clone and one Run-ID-bound SYSTEM boot task. Arm set `bootsequence` to that clone only; Test Signing and display-driver state were not changed.
- The first reboot entered the clone. At the three-minute deadline the emergency inline watchdog requested one return reboot and disabled its task. The final boot used the unchanged default loader.
- Post-boot evidence contained both restart-intent and restart-requested markers. The RX 7900 XTX physical output was healthy at `5120x2880 @ 165 Hz`.
- Final Rollback removed the exact watchdog and clone. Read-back proved `current == default == {55c7dfa7-c7ae-11ef-92f3-e5427153df1d}`, the original single-entry `displayorder`, an empty `bootsequence`, no task, and a `Cleaned` manifest with no error.

This rehearsal exposed and fixed Windows PowerShell 5.1 collection binding, localized BCD parsing, `bcdedit /copy` display-order behavior, localized missing-entry exit semantics, Task Scheduler SYSTEM XML normalization, and omitted schema-default `Enabled` nodes. The reboot used the emergency inline fallback because SYSTEM's effective Windows PowerShell policy blocked the frozen `.ps1`; commit `d00bc34` now adds process-scoped `ExecutionPolicy Bypass` only after the outer encoded action proves both ACL-locked frozen files match their pinned SHA-256 values. That rich-path fix has 54/54 tests in both Windows PowerShell 5.1 and PowerShell 7, but still requires a separate real reboot qualification before it can replace the fallback evidence above.

This result qualifies the one-time BCD return-reboot mechanism only. Gate B Test Signing and Gate C driver mutation remain hard-blocked.
