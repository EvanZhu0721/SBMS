# Continuous integration and hardware evidence

SBMS uses two deliberately separate Windows automation tiers.

## Hosted pull-request gate

`.github/workflows/windows-ci.yml` is pinned to `windows-2022` with read-only
repository permissions and checkout credentials disabled. Required lanes run:

1. Contract tests under PowerShell 7 and Windows PowerShell 5.1.
2. A clean unsigned build and package verification from a fresh checkout.
3. Process-gate, recovery-broker, and isolated WinForms smoke tests.

`invoke-sbms-ci.ps1` captures each script's stdout and stderr into stable UTF-8
logs. Every job uploads a schema-v1 `summary.json` with commit, shell, exit
code, duration, log name, and SHA-256; a `summary.md`; and raw logs.

The clean package lane passes `-RequireCleanSource`, skips Program Files and
source-copy deployment, rebuilds every component, and never discovers a
certificate by timestamp. Development packages are unsigned.

Every successful Windows CI run retains its exact unsigned candidate, contract
reports, and build/integration reports for 90 days. The separately dispatched
hardware workflow checks out an explicit full candidate SHA, verifies the
installed `SBMS.release.json` and every manifest-bound artifact, and retains the
observation report for 90 days.

`.github/workflows/qualify-release-candidate.yml` is the only path that creates
a qualified candidate bundle. It accepts the candidate SHA plus the exact
Windows CI and hardware workflow run IDs, downloads all three evidence classes,
queries the GitHub Actions API, and rejects workflow-origin drift, unsuccessful
runs, commit drift, dirty source, AuditOnly results, unverified payloads, or a
hardware-tested manifest that differs from the CI-retained candidate. The
trusted run metadata is included in and hash-bound by the evidence index.
The resulting bundle contains the candidate, unit/contract reports,
integration/package reports, hardware report, and a hash-bound evidence index.

Production signing remains a separate protected workflow requiring the real
certificate boundary and commit-matched WHQL return package.

## Local parity

```powershell
./invoke-sbms-ci.ps1 -Suite contracts -OutputDirectory ./artifacts/contracts

./invoke-sbms-ci.ps1 `
  -Suite contracts `
  -OutputDirectory ./artifacts/contracts-winps `
  -PowerShellExecutable powershell.exe
```

After a package build, use `-Suite integration`. The exact clean package gate
is `-Suite package -RequireCleanSource`.

## Clean Windows prerequisites

The supported hosted baseline is Windows Server 2022 with Visual Studio 2022
x64 C++, .NET Framework 4.8, Windows SDK/WDK 10.0.26100, PowerShell 7, and
Windows PowerShell 5.1.

Native builds resolve `VsDevCmd.bat` through explicit input, `VSINSTALLDIR`,
`vswhere`, then standard VS 2022 editions. No personal installation path is
required.

## Real-Windows hardware tier

`.github/workflows/hardware-evidence.yml` is manual-only and requires a
self-hosted runner labeled `sbms-hardware`. It invokes only the observation
harness `test-sbms-hardware.ps1`; it never installs or removes a driver.

Non-AuditOnly runs require the full candidate commit and the absolute path to
the installed candidate `SBMS.release.json`. Missing provenance makes the
observation inconclusive; invalid provenance fails it. The harness hashes every
manifest-bound payload artifact before collecting runtime evidence. It also
requires every running SBMS product process to match the candidate path and
hash, and binds the active published INF plus Driver Store driver DLL to the
candidate manifest.

Dispatch the hardware workflow from a ref whose head is the candidate SHA.
Checking out `candidate_sha` inside the job is not sufficient: the qualifier
independently requires the hardware workflow run's GitHub `head_sha` to equal
the candidate and verifies that the run came from
`.github/workflows/hardware-evidence.yml` with a successful conclusion.

`SingleOutput`, `MultiGroup`, `StreamOnly`, and `TopologyRecovery` remain
separate acknowledged runs. Hosted PR CI must never invoke the driver or
Program Files installers, the setup executable, virtual-display management, or
`lab/Invoke-SBMSHardwareLab.ps1 -Execute`.

Production-signed normal-boot hardware acceptance remains required for
display/driver claims; source-only CI does not replace it.
