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

Invoke-Test 'Production payload uses a signed CatalogVersion 2.0 boundary' {
    Assert-True ($productionSource -match 'New-FileCatalog.+CatalogVersion 2\.0') 'CatalogVersion 2.0 generation is missing.'
    $catalogStage = $productionSource.Substring(
        $productionSource.LastIndexOf('$releaseCatalog =', [StringComparison]::Ordinal)
    )
    Assert-True ($catalogStage -match 'Invoke-SBMSSignAuthenticode') 'Release catalog is not signed.'
    Assert-True ($catalogStage -match 'Assert-SBMSAuthenticodeSignature') 'Release catalog signature is not verified.'
    Assert-True ($productionSource -match "schemaVersion = 3") 'Production manifest is not schema v3.'
    Assert-True ($productionSource -match "spdxVersion = 'SPDX-2\.2'") 'SPDX 2.2 SBOM generation is missing.'
    Assert-True ($productionSource -match 'packageVerificationCodeValue') 'SPDX package verification code is missing.'
    Assert-True ($productionSource -match "relationshipType = 'DESCRIBES'") 'SPDX DESCRIBES relationship is missing.'
    Assert-True ($productionSource -match "relationshipType = 'CONTAINS'") 'SPDX CONTAINS relationships are missing.'
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
