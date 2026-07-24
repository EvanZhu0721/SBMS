Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0
$root = $PSScriptRoot

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock] $Body, [string] $Pattern)
    try {
        & $Body
    } catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected error matching '$Pattern'; actual '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected error matching '$Pattern', but no error was thrown."
}

function Invoke-Test {
    param([string] $Name, [scriptblock] $Body)
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

$transactionSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'installer\InstallTransaction.cs'),
    [System.Text.Encoding]::UTF8
)
$verifierSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'installer\ReleaseIntegrityVerifier.cs'),
    [System.Text.Encoding]::UTF8
)
$driverVerifierSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'installer\DriverCatalogVerifier.cs'),
    [System.Text.Encoding]::UTF8
)
$provenanceVerifierPath = Join-Path $root 'Verify-SBMSWhqlProvenance.ps1'
$provenanceVerifierSource = [System.IO.File]::ReadAllText(
    $provenanceVerifierPath,
    [System.Text.Encoding]::UTF8
)
. $provenanceVerifierPath
$probeSource = @'
using System;
using System.Collections.Generic;

namespace SBMSSetup
{
    public static class IntegrityTestProbe
    {
        public static int RunRejectedTransaction()
        {
            int mutations = 0;
            try
            {
                InstallTransaction.Execute(
                    delegate { throw new InvalidOperationException("rejected"); },
                    delegate { mutations++; },
                    delegate { mutations++; },
                    delegate { mutations++; },
                    delegate { mutations++; },
                    delegate { mutations++; },
                    delegate { mutations++; });
            }
            catch (InvalidOperationException)
            {
            }
            return mutations;
        }

        public static string RunAcceptedTransaction()
        {
            List<string> calls = new List<string>();
            InstallTransaction.Execute(
                delegate { calls.Add("verify"); },
                delegate { calls.Add("stage"); },
                delegate { calls.Add("process"); },
                delegate { calls.Add("copy"); },
                delegate { calls.Add("driver"); },
                delegate { calls.Add("shortcut"); },
                delegate { calls.Add("task"); });
            return String.Join(",", calls.ToArray());
        }

        public static int RunRejectedStaging()
        {
            int machineMutations = 0;
            try
            {
                InstallTransaction.Execute(
                    delegate { },
                    delegate { throw new InvalidOperationException("staged payload rejected"); },
                    delegate { machineMutations++; },
                    delegate { machineMutations++; },
                    delegate { machineMutations++; },
                    delegate { machineMutations++; },
                    delegate { machineMutations++; });
            }
            catch (InvalidOperationException)
            {
            }
            return machineMutations;
        }

        public static void VerifyRelease(string root, string thumbprint)
        {
            ReleaseIntegrityVerifier.VerifyOrThrow(
                root,
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                thumbprint,
                "CN=Microsoft Windows Hardware Compatibility Publisher");
        }

        public static void VerifyDriver(string catalog, string inf, string dll)
        {
            DriverCatalogVerifier.VerifyPackageOrThrow(catalog, inf, dll);
        }
    }
}
'@
$combinedSource = @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

"@ + (($transactionSource + "`n" + $verifierSource + "`n" + $driverVerifierSource + "`n" + $probeSource) -replace '(?m)^using [^;]+;\s*$', '')
Add-Type -TypeDefinition $combinedSource -Language CSharp

Invoke-Test 'Rejected integrity verification causes zero installer mutations' {
    Assert-True (
        [SBMSSetup.IntegrityTestProbe]::RunRejectedTransaction() -eq 0
    ) 'An installer mutation ran after integrity verification failed.'
}

Invoke-Test 'Accepted installer transaction preserves verify-first order' {
    $order = [SBMSSetup.IntegrityTestProbe]::RunAcceptedTransaction()
    Assert-True (
        $order -ceq 'verify,stage,process,copy,driver,shortcut,task'
    ) "Unexpected install order: '$order'."
}

Invoke-Test 'Rejected staged copy causes zero machine mutations' {
    Assert-True (
        [SBMSSetup.IntegrityTestProbe]::RunRejectedStaging() -eq 0
    ) 'A machine mutation ran after staged payload verification failed.'
}

Invoke-Test 'Missing production catalog fails without changing source payload' {
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'SBMS-integrity-reject-' + [guid]::NewGuid().ToString('N')
    )
    $payload = Join-Path $testRoot 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $marker = Join-Path $payload 'marker.bin'
    [System.IO.File]::WriteAllBytes($marker, [byte[]](1, 2, 3, 4))
    $before = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    try {
        $threw = $false
        try {
            [SBMSSetup.IntegrityTestProbe]::VerifyRelease(
                $testRoot,
                '0123456789ABCDEF0123456789ABCDEF01234567'
            )
        } catch {
            $threw = $true
        }
        Assert-True $threw 'Verifier accepted a release with no catalog.'
        Assert-True (Test-Path -LiteralPath $marker -PathType Leaf) 'Verifier removed source payload.'
        $after = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
        Assert-True ($before -ceq $after) 'Verifier changed source payload.'
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}

Invoke-Test 'Native driver policy rejects a fabricated catalog before PnP' {
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'SBMS-driver-policy-reject-' + [guid]::NewGuid().ToString('N')
    )
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $catalog = Join-Path $testRoot 'fake.cat'
    $inf = Join-Path $testRoot 'fake.inf'
    $dll = Join-Path $testRoot 'fake.dll'
    [System.IO.File]::WriteAllBytes($catalog, [byte[]](1, 2, 3))
    [System.IO.File]::WriteAllBytes($inf, [byte[]](4, 5, 6))
    [System.IO.File]::WriteAllBytes($dll, [byte[]](7, 8, 9))
    try {
        $threw = $false
        try {
            [SBMSSetup.IntegrityTestProbe]::VerifyDriver($catalog, $inf, $dll)
        } catch {
            $threw = $true
        }
        Assert-True $threw 'WinVerifyTrust accepted a fabricated driver catalog.'
    } finally {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Invoke-Test 'Production verifier rejects missing or mismatched WHQL submission provenance' {
    Assert-True ($verifierSource -match 'Production release manifest schema must be 4') 'Installer verifier does not require release schema v4.'
    Assert-True (
        $verifierSource -match 'driverArtifacts\.whqlImportPath' -and
        $provenanceVerifierSource -match 'SBMS\.driver-whql\.json'
    ) 'Installer verifier does not read the release-bound WHQL import manifest.'
    Assert-True ($verifierSource -match 'Assert-SBMSWhqlReleaseProvenance') 'Installer verifier does not execute the provenance contract.'
    foreach ($field in @('privateProductId', 'sharedProductId', 'submissionId', 'hlkPackageSha256')) {
        Assert-True ($provenanceVerifierSource -match $field) "Provenance contract does not require $field."
    }
}

Invoke-Test 'Installer requires, verifies, and blocks a running recovery broker' {
    $setupSource = [System.IO.File]::ReadAllText(
        (Join-Path $root 'installer\SBMSSetup.cs'),
        [System.Text.Encoding]::UTF8
    )
    Assert-True ($verifierSource -match "SBMSRecoveryBroker\.exe") 'Production integrity verifier does not verify the recovery broker.'
    Assert-True ($setupSource -match 'RequireFile\("SBMSRecoveryBroker\.exe"\)') 'Installer does not require the recovery broker payload.'
    Assert-True ($setupSource -match '"SBMSRecoveryBroker"') 'Installer does not block a running recovery broker.'
}

Invoke-Test 'WHQL provenance contract accepts exact values and rejects drift' {
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $candidateHash = ('a' * 64)
    $identityHash = ('b' * 64)
    $partnerCenter = [pscustomobject]@{
        privateProductId = '13635057453741329'
        sharedProductId = '29963920'
        submissionId = '1152921504621441930'
        hlkPackageSha256 = ('c' * 64)
    }
    $release = [pscustomobject]@{
        schemaVersion = 4
        profile = 'Production'
        product = [pscustomobject]@{ driverVer = '07/25/2026,0.3.0.0' }
        source = [pscustomobject]@{ commit = $commit; dirty = $false }
        driverCertification = [pscustomobject]@{
            method = 'WHQL'
            sourceCommit = $commit
            candidateManifestSha256 = $candidateHash
            identitySchema = 1
            identityFingerprint = $identityHash
            partnerCenter = $partnerCenter
        }
    }
    $import = [pscustomobject]@{
        schemaVersion = 3
        kind = 'SBMS-WHQL-driver-import'
        sourceCommit = $commit
        driverVer = '07/25/2026,0.3.0.0'
        candidateManifestSha256 = $candidateHash
        identitySchema = 1
        identityFingerprint = $identityHash
        certification = [pscustomobject]@{ method = 'WHQL' }
        partnerCenter = [pscustomobject]@{
            privateProductId = $partnerCenter.privateProductId
            sharedProductId = $partnerCenter.sharedProductId
            submissionId = $partnerCenter.submissionId
            hlkPackageSha256 = $partnerCenter.hlkPackageSha256
        }
    }
    $accepted = Assert-SBMSWhqlReleaseProvenance `
        -ReleaseManifest $release `
        -WhqlImportManifest $import
    Assert-True ($accepted.submissionId -ceq $partnerCenter.submissionId) 'Valid submission provenance was not returned.'

    $import.partnerCenter.submissionId = '1152921504621441931'
    Assert-Throws {
        Assert-SBMSWhqlReleaseProvenance `
            -ReleaseManifest $release `
            -WhqlImportManifest $import
    } 'submissionId'
    $import.partnerCenter.submissionId = $partnerCenter.submissionId
    $release.driverCertification.partnerCenter.privateProductId = 'invalid'
    Assert-Throws {
        Assert-SBMSWhqlReleaseProvenance `
            -ReleaseManifest $release `
            -WhqlImportManifest $import
    } 'privateProductId'
}

Invoke-Test 'WHQL driver artifacts are bound to the verified release payload' {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'SBMS-whql-artifacts-' + [guid]::NewGuid().ToString('N')
    )
    try {
        $driverRoot = Join-Path $fixtureRoot 'driver\SBMSIndirectDisplay'
        [void](New-Item -ItemType Directory -Path $driverRoot -Force)
        $files = [ordered]@{
            'driver/SBMSIndirectDisplay/SBMSIndirectDisplay.inf' = [byte[]](1, 2, 3)
            'driver/SBMSIndirectDisplay/SBMSIndirectDisplay.dll' = [byte[]](4, 5, 6)
            'driver/SBMSIndirectDisplay/sbmsindirectdisplay.cat' = [byte[]](7, 8, 9)
            'driver/SBMSIndirectDisplay/driver-identity.json' = [byte[]](10, 11, 12)
            'driver/SBMSIndirectDisplay/SBMS.driver-whql.json' = [byte[]](13, 14, 15)
        }
        $artifacts = foreach ($entry in $files.GetEnumerator()) {
            $path = Join-Path $fixtureRoot $entry.Key.Replace('/', '\')
            [System.IO.File]::WriteAllBytes($path, $entry.Value)
            $item = Get-Item -LiteralPath $path
            [pscustomobject]@{
                path = $entry.Key
                bytes = [long]$item.Length
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        $manifest = [pscustomobject]@{ artifacts = @($artifacts) }
        $accepted = Assert-SBMSWhqlDriverArtifacts `
            -ReleaseManifest $manifest `
            -PayloadRoot $fixtureRoot
        Assert-True (
            [System.IO.Path]::GetFullPath($accepted.infPath) -ieq
            [System.IO.Path]::GetFullPath((Join-Path $driverRoot 'SBMSIndirectDisplay.inf'))
        ) 'Artifact contract did not return the release-owned INF.'

        [System.IO.File]::WriteAllBytes(
            (Join-Path $driverRoot 'SBMSIndirectDisplay.dll'),
            [byte[]](99, 98, 97)
        )
        Assert-Throws {
            Assert-SBMSWhqlDriverArtifacts `
                -ReleaseManifest $manifest `
                -PayloadRoot $fixtureRoot
        } 'artifact metadata mismatch'
    } finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
    }
}

Invoke-Test 'Development installer still compiles with integrity gate disabled' {
    $output = Join-Path ([System.IO.Path]::GetTempPath()) (
        'SBMSSetup-dev-' + [guid]::NewGuid().ToString('N') + '.exe'
    )
    try {
        & (Join-Path $root 'build-sbms-setup.ps1') -OutputName $output | Out-Null
        Assert-True (Test-Path -LiteralPath $output -PathType Leaf) 'Development setup executable was not built.'
        $generated = [System.IO.File]::ReadAllText(
            (Join-Path $root 'obj\version\setup\SBMS.Signing.g.cs'),
            [System.Text.Encoding]::UTF8
        )
        Assert-True ($generated -match 'IntegrityRequired = false;') 'Development setup unexpectedly requires production catalog.'
        Assert-True ($generated -match 'PublisherThumbprint = "";') 'Development setup embeds an arbitrary publisher.'
        Assert-True ($generated -match 'WhqlCatalogSubjects = "";') 'Development setup embeds an arbitrary WHQL publisher.'
    } finally {
        if (Test-Path -LiteralPath $output) {
            Remove-Item -LiteralPath $output -Force
        }
    }
}

Invoke-Test 'Standalone driver installer stages without changing active PnP state' {
    $source = [System.IO.File]::ReadAllText(
        (Join-Path $root 'install-sbms-driver.ps1'),
        [System.Text.Encoding]::UTF8
    )
    $assertIndex = $source.LastIndexOf('Assert-DriverPayload -Inf $Inf', [System.StringComparison]::Ordinal)
    Assert-True ($assertIndex -ge 0) 'Driver payload assertion call is missing.'
    Assert-True (
        $source -match 'Join-Path \$VerifiedProductionPayload ''driver'''
    ) 'Production staging does not select its driver from the verified release payload.'
    Assert-True (
        $source -match 'Assert-SBMSWhqlDriverArtifacts' -and
        $source -match 'Selected driver INF is not the artifact bound'
    ) 'Production staging does not bind the selected INF to release artifact metadata.'
    Assert-True (
        $verifierSource -match 'Assert-SBMSWhqlDriverArtifacts'
    ) 'Embedded verification does not use the shared driver artifact contract.'
    $addIndex = $source.LastIndexOf("'/add-driver'", [System.StringComparison]::Ordinal)
    Assert-True ($addIndex -gt $assertIndex) 'Driver Store staging occurs before payload validation.'
    foreach ($forbidden in @(
        '/install',
        '/delete-driver',
        '/remove-device',
        '/restart-device',
        '/scan-devices',
        'Stop-Process -Force',
        'Assert-ActiveDriverBinding -Inf'
    )) {
        Assert-True (
            $source.IndexOf($forbidden, [System.StringComparison]::Ordinal) -lt 0
        ) "Driver staging path still contains active-state mutation '$forbidden'."
    }
    Assert-True (
        $source -match 'Active-device transition is deferred'
    ) 'Driver staging path does not make its non-activation boundary explicit.'
    $setupSource = [System.IO.File]::ReadAllText(
        (Join-Path $root 'installer\SBMSSetup.cs'),
        [System.Text.Encoding]::UTF8
    )
    Assert-True ($setupSource -match 'IntegrityRequired[\s\S]+VerifiedReleaseRoot') 'Production setup does not pass verified release provenance to the driver installer.'
    Assert-True ($setupSource -match 'VerifiedByInstaller') 'Production setup does not identify its kernel-policy verified driver invocation.'
    Assert-True ($setupSource -match 'else[\s\S]+AllowTestSigned') 'Development setup does not explicitly authorize its test-signed driver.'
    $finalVerify = $setupSource.IndexOf('ReleaseIntegrityVerifier.VerifyPayloadOrThrow(', [StringComparison]::Ordinal)
    $atomicCommit = $setupSource.IndexOf('Directory.Move(candidate, installRoot)', [StringComparison]::Ordinal)
    Assert-True ($finalVerify -ge 0 -and $atomicCommit -gt $finalVerify) 'Final Program Files payload is not verified before atomic commit.'
    Assert-True ($setupSource -match 'Directory\.Move\(installBackupRoot, installRoot\)') 'Failed install cannot restore the prior Program Files payload.'
    Assert-True (
        $setupSource -match 'BestEffortDeleteDirectory\(installBackupRoot' -and
        $setupSource -match 'BestEffortDeleteDirectory\(stagedReleaseRoot'
    ) 'Successful install can still be reported as failed by residue cleanup.'
    Assert-True (
        $setupSource -match 'CreateShortcutBestEffort' -and
        $setupSource -match 'CreateStartupTaskBestEffort' -and
        $setupSource -match 'RunBestEffort\("shortcut", CreateShortcut\)' -and
        $setupSource -match 'RunBestEffort\("startup task", CreateStartupTask\)'
    ) 'Post-install shortcut or task integration can still fail the completed core install.'
    Assert-True ($driverVerifierSource -match 'DriverActionVerify') 'Native Windows driver-policy verification is missing.'
    Assert-True ($driverVerifierSource -match 'WinVerifyTrust') 'Driver catalog membership is not verified by WinVerifyTrust.'
}

Write-Host "Installer integrity contract: $script:Passed passed, $script:Failed failed"
if ($script:Failed -ne 0) {
    exit 1
}
$global:LASTEXITCODE = 0
