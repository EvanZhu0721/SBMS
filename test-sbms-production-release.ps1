Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0
$root = $PSScriptRoot

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
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

$productionSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'package-sbms-production.ps1'),
    [System.Text.Encoding]::UTF8
)
$driverBuildSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'build-sbms-driver.ps1'),
    [System.Text.Encoding]::UTF8
)
$candidateWrapperSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'New-SBMSDriverCandidate.ps1'),
    [System.Text.Encoding]::UTF8
)
$importWrapperSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'Import-SBMSWhqlDriver.ps1'),
    [System.Text.Encoding]::UTF8
)
$certificationSource = [System.IO.File]::ReadAllText(
    (Join-Path $root 'build\SBMS.DriverCertification.psm1'),
    [System.Text.Encoding]::UTF8
)

Invoke-Test 'Production packager never rebuilds the WHQL driver' {
    Assert-True (
        $productionSource -notmatch '(?i)build-sbms-driver\.ps1'
    ) 'Production package still invokes the driver build.'
    Assert-True (
        $productionSource -match 'Assert-SBMSWhqlPackage'
    ) 'Production package does not reverify the imported WHQL package.'
}

Invoke-Test 'Production preflight completes before user-mode builds' {
    $dirtyGate = $productionSource.IndexOf('if ($metadata.IsDirty)', [StringComparison]::Ordinal)
    $policyGate = $productionSource.IndexOf('Read-SBMSSigningPolicy', [StringComparison]::Ordinal)
    $certGate = $productionSource.IndexOf('Resolve-SBMSSigningCertificate', [StringComparison]::Ordinal)
    $whqlGate = $productionSource.IndexOf('$driverVerification = Assert-SBMSWhqlPackage', [StringComparison]::Ordinal)
    $firstBuild = $productionSource.IndexOf("build-sbms-device-host.ps1", [StringComparison]::Ordinal)
    Assert-True ($dirtyGate -ge 0 -and $dirtyGate -lt $firstBuild) 'Dirty-source gate is absent or late.'
    Assert-True ($policyGate -ge 0 -and $policyGate -lt $firstBuild) 'Signing-policy gate is absent or late.'
    Assert-True ($certGate -ge 0 -and $certGate -lt $firstBuild) 'Certificate gate is absent or late.'
    Assert-True ($whqlGate -ge 0 -and $whqlGate -lt $firstBuild) 'WHQL verification is absent or late.'
    Assert-True ($productionSource -match 'whqlManifest\.sourceCommit.+metadata\.Commit') 'WHQL source commit is not pinned to the release.'
    Assert-True ($productionSource -match 'whqlManifest\.driverVer.+metadata\.DriverVer') 'WHQL DriverVer is not pinned to the release.'
}

Invoke-Test 'Production release builds, signs, and packages the recovery broker' {
    Assert-True ($productionSource -match "build-sbms-recovery-broker\.ps1") 'Production release does not build the recovery broker.'
    Assert-True ($productionSource -match '(?s)\$executables\s*=\s*@\(.+SBMSRecoveryBroker\.exe') 'Recovery broker is absent from the signing set.'
    Assert-True ($productionSource -match '(?s)foreach \(\$name in @\(.+?''SBMSRecoveryBroker\.exe''') 'Recovery broker is absent from the production payload.'
}

Invoke-Test 'Production release excludes the offline maintenance baseline' {
    Assert-True ($productionSource -notmatch "build-sbms-maintenance-service\.ps1") 'Production release must not build the offline maintenance baseline.'
    Assert-True ($productionSource -notmatch 'SBMSMaintenanceService\.exe') 'Production release must not sign or package the offline maintenance baseline.'
    Assert-True ($productionSource -notmatch 'New-Service|sc\.exe|Start-Service') 'Production packaging must not register or start the maintenance service.'
}

Invoke-Test 'Production payload uses a signed CatalogVersion 2.0 boundary' {
    Assert-True ($productionSource -match 'New-FileCatalog.+CatalogVersion 2\.0') 'CatalogVersion 2.0 generation is missing.'
    $catalogStage = $productionSource.Substring(
        $productionSource.LastIndexOf('$releaseCatalog =', [StringComparison]::Ordinal)
    )
    Assert-True ($catalogStage -match 'Invoke-SBMSSignAuthenticode') 'Release catalog is not signed.'
    Assert-True ($catalogStage -match 'Assert-SBMSAuthenticodeSignature') 'Release catalog signature is not verified.'
    Assert-True ($productionSource -match "schemaVersion = 4") 'Production manifest is not schema v4.'
    Assert-True ($productionSource -match "spdxVersion = 'SPDX-2\.2'") 'SPDX 2.2 SBOM generation is missing.'
    Assert-True ($productionSource -match 'packageVerificationCodeValue') 'SPDX package verification code is missing.'
    Assert-True ($productionSource -match "relationshipType = 'DESCRIBES'") 'SPDX DESCRIBES relationship is missing.'
    Assert-True ($productionSource -match "relationshipType = 'CONTAINS'") 'SPDX CONTAINS relationships are missing.'
}

Invoke-Test 'WHQL provenance binds Partner Center submission to the HLK package' {
    foreach ($parameter in @('PrivateProductId', 'SharedProductId', 'SubmissionId', 'HlkPackagePath', 'ExpectedHlkPackageSha256')) {
        Assert-True ($importWrapperSource -match ([regex]::Escape('$' + $parameter))) "WHQL import CLI is missing $parameter."
    }
    Assert-True ($certificationSource -match "schemaVersion = 3") 'WHQL import manifest is not schema v3.'
    Assert-True ($certificationSource -match 'partnerCenter =') 'WHQL import manifest omits Partner Center provenance.'
    foreach ($field in @('privateProductId', 'sharedProductId', 'submissionId', 'hlkPackageSha256')) {
        Assert-True ($productionSource -match ([regex]::Escape('whqlManifest.partnerCenter.' + $field))) "Production release does not propagate $field."
    }
}

Invoke-Test 'Driver development build never auto-selects a certificate' {
    Assert-True ($driverBuildSource -notmatch 'WDKTestCert') 'Driver build still auto-selects a WDK test certificate.'
    Assert-True ($driverBuildSource -notmatch 'Sort-Object NotAfter') 'Driver build still selects an arbitrary newest certificate.'
    Assert-True ($driverBuildSource -match 'TestCertificateThumbprint') 'Explicit development certificate input is missing.'
    Assert-True ($driverBuildSource -match 'if \(\$Production\)[\s\S]+Invoke-SBMSSignAuthenticode') 'Production driver DLL signing is missing.'
}

Invoke-Test 'WHQL candidate wrapper builds and verifies its own clean-tree artifact' {
    Assert-True ($candidateWrapperSource -notmatch '\[string\] \$DriverDirectory') 'Candidate wrapper still accepts an arbitrary prebuilt driver directory.'
    Assert-True ($candidateWrapperSource -match 'build-sbms-driver\.ps1') 'Candidate wrapper does not build the driver itself.'
    Assert-True ($candidateWrapperSource -match '-ExpectedWindowsVersion \$metadata\.WindowsVersion') 'Candidate wrapper does not bind DLL version to VERSION metadata.'
    Assert-True ($candidateWrapperSource -match '-ExpectedDriverVer \$metadata\.DriverVer') 'Candidate wrapper does not bind INF DriverVer to VERSION metadata.'
}

Invoke-Test 'Production schema v4 diagnostics resolve release-root provenance' {
    $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'SBMS-production-diagnostics-' + [guid]::NewGuid().ToString('N')
    )
    try {
        $releaseRoot = Join-Path $fixtureRoot 'SBMS-1.2.3-production-x64'
        $payload = Join-Path $releaseRoot 'payload'
        $driver = Join-Path $payload 'driver\SBMSIndirectDisplay'
        [void](New-Item -ItemType Directory -Path $driver -Force)

        $engine = (Get-Process -Id $PID).Path
        $copiedEngine = Join-Path $payload 'SBMS.exe'
        Copy-Item -LiteralPath $engine -Destination $copiedEngine
        Copy-Item -LiteralPath $engine -Destination (Join-Path $releaseRoot 'SBMSSetup.exe')
        $engineVersion = (Get-Item -LiteralPath $copiedEngine).VersionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace([string]$engineVersion)) {
            $engineVersion = (Get-Item -LiteralPath $copiedEngine).VersionInfo.FileVersion
        }
        Copy-Item -LiteralPath (Join-Path $root 'diagnose-sbms.ps1') -Destination $payload
        Copy-Item -LiteralPath (Join-Path $root 'driver-identity.json') -Destination $payload
        [System.IO.File]::WriteAllText(
            (Join-Path $driver 'SBMSIndirectDisplay.inf'),
            "[Version]`r`nDriverVer=07/24/2026,0.3.0.0`r`n",
            [System.Text.UTF8Encoding]::new($false)
        )

        $manifest = [pscustomobject][ordered]@{
            schemaVersion = 4
            profile = 'Production'
            product = [pscustomobject][ordered]@{
                version = [string]$engineVersion
                driverVer = '07/24/2026,0.3.0.0'
                architecture = 'x64'
            }
            source = [pscustomobject][ordered]@{
                commit = ('a' * 40)
                dirty = $false
            }
            installer = [pscustomobject][ordered]@{
                productVersion = [string]$engineVersion
            }
            driverCertification = [pscustomobject][ordered]@{
                partnerCenter = [pscustomobject][ordered]@{
                    privateProductId = '13635057453741329'
                    sharedProductId = '29963920'
                    submissionId = '1152921504621441930'
                    hlkPackageSha256 = ('b' * 64)
                }
            }
        }
        $manifestPath = Join-Path $payload 'SBMS.release.json'
        [System.IO.File]::WriteAllText(
            $manifestPath,
            ($manifest | ConvertTo-Json -Depth 10),
            [System.Text.UTF8Encoding]::new($false)
        )

        $diagnostic = Join-Path $payload 'diagnose-sbms.ps1'
        $output = @(& $engine -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $diagnostic -VersionOnly 2>&1)
        Assert-True ($LASTEXITCODE -eq 0) "Valid production diagnostics failed: $($output -join ' ')"
        Assert-True ($output -contains "PackageName=$([System.IO.Path]::GetFileName($releaseRoot))") 'Production diagnostics reported payload instead of the release-root package name.'
        Assert-True ($output -contains "InstallerVersion=$engineVersion") 'Production diagnostics did not resolve SBMSSetup.exe from the release root.'

        $manifest.driverCertification.partnerCenter.submissionId = 'invalid'
        [System.IO.File]::WriteAllText(
            $manifestPath,
            ($manifest | ConvertTo-Json -Depth 10),
            [System.Text.UTF8Encoding]::new($false)
        )
        $rejected = @()
        $rejectedExitCode = 0
        try {
            $rejected = @(& $engine -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $diagnostic -VersionOnly 2>&1)
            $rejectedExitCode = $LASTEXITCODE
        } catch {
            $rejected = @($_.Exception.Message)
            $rejectedExitCode = 1
        }
        Assert-True ($rejectedExitCode -ne 0) 'Production diagnostics accepted malformed schema v4 submission provenance.'
        Assert-True (
            ($rejected -join "`n") -match 'invalid Partner Center provenance'
        ) 'Production diagnostics rejected malformed provenance for an unexpected reason.'
    } finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
    }
}

Invoke-Test 'Repository signing template is tracked outside ignored Release output' {
    $template = Join-Path $root 'build\signing-policy.template.json'
    Assert-True (Test-Path -LiteralPath $template -PathType Leaf) 'Signing policy template is missing.'
    & git -C $root check-ignore --quiet -- $template
    Assert-True ($LASTEXITCODE -ne 0) 'Signing policy template is ignored by Git.'
}

Write-Host "Production release contract: $script:Passed passed, $script:Failed failed"
if ($script:Failed -ne 0) {
    exit 1
}
$global:LASTEXITCODE = 0
