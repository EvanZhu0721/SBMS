# Production signing and driver certification

SBMS production releases use two independently auditable signing stages.

1. Build and freeze a driver candidate from one clean commit. The candidate
   manifest records the exact INF and DLL hashes, version, commit and toolchain.
2. Run HLK and submit the resulting package through the Windows Hardware
   Developer Program for WHQL/WHCP signing.
3. Import the Microsoft-returned driver package only if its INF and DLL still
   match the frozen candidate. The returned catalog must pass Windows
   kernel-policy verification.
4. Build and sign the five user-mode executables with the explicit publisher
   certificate, SHA-256 and an RFC3161 SHA-256 timestamp.
5. Generate the SPDX SBOM and schema-v4 release manifest, then create a
   CatalogVersion 2.0 SHA-256 catalog over the complete release payload.
6. Sign and timestamp the release catalog. The installer verifies the catalog,
   manifest, exact payload allow-list, hashes, component signatures and WHQL
   provenance before any file, PnP, shortcut or scheduled-task mutation.

Attestation signing is not the SBMS retail release path. It may be used only for
an explicitly labelled internal test artifact and must never satisfy the
production profile.

## Secret boundary

- The repository contains no private key, PFX file or certificate password.
- A production signing policy selects one exact certificate thumbprint from a
  Windows certificate store, HSM or managed signing provider.
- Commands and logs may record the public subject and thumbprint, but never key
  material, PINs, tokens or passwords.
- `build/signing-policy.template.json` is intentionally invalid until the
  release owner replaces every placeholder with the reviewed legal publisher
  identity and trusted RFC3161 service.

## Fail-closed rules

- SignTool exit code `1` (failure) and `2` (warning) both fail the release.
- Automatic certificate selection is forbidden.
- Missing or untrusted timestamps fail production verification.
- A valid signature from the wrong publisher fails verification.
- A locally generated or test-signed driver catalog cannot satisfy WHQL.
- The Microsoft-returned driver DLL or INF must never be rebuilt, modified or
  re-signed after certification.
- Production packaging never enables TestSigning and never installs a package
  as a side effect of building it.

## Release commands

Build the driver normally from a clean commit, then freeze the exact INF and
DLL that will enter HLK:

```powershell
.\New-SBMSDriverCandidate.ps1 `
  -OutputDirectory C:\SBMS-WHQL\candidate `
  -SigningPolicyPath C:\SBMS-Secrets\signing-policy.json
```

The wrapper requires a clean tree, builds the driver itself, signs and
timestamps the driver DLL using the explicit production policy, and validates
the INF `DriverVer` plus DLL file/product versions against `VERSION` before it
records the current commit. It does not accept an arbitrary prebuilt driver
directory.

Candidate creation fails while the INF still contains Microsoft sample
manufacturer, hardware-ID or TODO placeholders. Those identities must be
reviewed and frozen before HLK because changing them after certification
invalidates the returned catalog.

Record the printed `manifestSha256` outside the candidate directory. Submit the
candidate through HLK and Partner Center. When Microsoft returns the signed
package, import it with the independently recorded hash:

```powershell
.\Import-SBMSWhqlDriver.ps1 `
  -CandidateDirectory C:\SBMS-WHQL\candidate `
  -CandidateManifestSha256 <64-hex-sha256> `
  -ReturnedDirectory C:\SBMS-WHQL\microsoft-returned `
  -OutputDirectory C:\SBMS-WHQL\verified `
  -SigningPolicyPath C:\SBMS-Secrets\signing-policy.json `
  -PrivateProductId <partner-center-private-product-id> `
  -SharedProductId <partner-center-shared-product-id> `
  -SubmissionId <partner-center-submission-id> `
  -HlkPackagePath C:\SBMS-WHQL\archive\submission.hlkx `
  -ExpectedHlkPackageSha256 <independently-recorded-sha256>
```

The import checks that the returned INF and DLL are byte-for-byte identical to
the frozen candidate, verifies the Microsoft kernel-policy catalog and verifies
that both files are members of that catalog. It never modifies the returned
files. It also hashes the archived HLK upload and rejects it unless that value
matches the independently recorded SHA-256. The three operator-supplied Partner
Center identifiers are stored as decimal strings, not floating-point numbers.
The script records but does not query or authenticate Partner Center; retain the
portal/API export beside the package as the authority for those IDs. Microsoft
assigns private, shared, and submission IDs to each hardware submission; the
Hardware API addresses a submission by product ID and submission ID:

- https://learn.microsoft.com/windows-hardware/drivers/dashboard/hardware-submission-ids
- https://learn.microsoft.com/windows-hardware/drivers/dashboard/manage-product-submissions

Create the retail package only from the verified import:

```powershell
.\package-sbms-production.ps1 `
  -SigningPolicyPath C:\SBMS-Secrets\signing-policy.json `
  -WhqlDriverDirectory C:\SBMS-WHQL\verified
```

The production packager refuses dirty source, never calls
`build-sbms-driver.ps1`, signs and timestamps every user-mode executable,
generates an SPDX 2.2 SBOM and schema-v4 manifest, then covers the complete
`payload` directory with a signed CatalogVersion 2.0 catalog. Partial output is
removed if any gate fails. Both the embedded verifier and standalone staging
path resolve the driver only from that verified payload and re-check the fixed
INF, DLL, CAT, identity, and WHQL-import paths against the manifest length and
SHA-256 records before any Driver Store mutation.

The production installer first verifies its own Authenticode publisher and
timestamp against the identity embedded at build time, then verifies the
publisher-pinned catalog before any machine change. It copies the release into
an administrator-controlled staging directory under Program Files, verifies it
again, validates the WHQL CAT and INF/DLL membership through Windows
`WinVerifyTrust` using the driver-policy action, and only then atomically swaps
the installed payload and stages the verified driver package in Driver Store.
SBMS-owned shortcut and scheduled-task integration runs afterward as
best-effort work; failure is recorded but cannot roll the completed core
installation back into a file/Driver Store mismatch.

Issue #18 never requests an active-device update and never removes an existing
driver package. In particular, its PnPUtil call omits `/install`, `/uninstall`
and `/force`. Activating a new package on an existing software device, proving
the exact active Driver Store identity and rolling back a failed binding are
owned by the transactional installer in Issue #19. This boundary prevents a
signing or packaging change from disturbing the live display topology.

The previous Program Files directory is retained until the complete install
transaction succeeds and is restored if a pre-staging step fails. Cleanup of a
backup or verified staging directory is best-effort after success; cleanup
residue cannot turn a completed Driver Store staging operation into a reported
installation failure.

## External release gate

Repository tests cannot manufacture production evidence. A release remains
blocked until the release owner supplies:

- the reviewed legal publisher certificate and RFC3161 service;
- legal publisher display name and PNP manufacturer code confirmation for the
  permanent SBMS identity already frozen in source;
- a clean candidate manifest hash recorded independently of the candidate;
- passing HLK results and the Microsoft-returned WHQL package;
- a normal-boot install on supported Windows with TestSigning disabled;
- archived Partner Center private/shared/submission IDs and the exact uploaded
  HLK package SHA-256. Schema-v3 WHQL imports and schema-v4 production releases
  carry these values end to end.
