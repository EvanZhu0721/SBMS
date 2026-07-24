Set-StrictMode -Version 2.0

$signingModule = Join-Path $PSScriptRoot 'SBMS.Signing.psm1'
Import-Module $signingModule -Force

function Write-SBMSUtf8Json {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [object] $Value
    )

    $json = ($Value | ConvertTo-Json -Depth 20) + "`n"
    [System.IO.File]::WriteAllText(
        $LiteralPath,
        $json,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function Get-SBMSFileRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $file = Get-Item -LiteralPath $LiteralPath -ErrorAction Stop
    [pscustomobject][ordered]@{
        path = $RelativePath.Replace('\', '/')
        bytes = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-SBMSSingleDriverFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string] $Filter
    )

    $matches = @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one '$Filter' in '$Directory'; found $($matches.Count)."
    }
    $matches[0]
}

function Get-SBMSDriverIdentityContract {
    $path = Join-Path (Split-Path $PSScriptRoot -Parent) 'driver-identity.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "SBMS driver identity contract not found: $path"
    }
    $contract = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$contract.schemaVersion -ne 1) {
        throw "Unsupported SBMS driver identity schema: $($contract.schemaVersion)"
    }
    [pscustomobject][ordered]@{
        path = $path
        fingerprint = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        contract = $contract
    }
}

function Assert-SBMSDriverInfIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [psobject] $Identity
    )

    $inf = Get-Content -LiteralPath $LiteralPath -Raw -Encoding UTF8
    $contract = $Identity.contract
    $required = @(
        "CatalogFile=$($contract.package.catalogName)",
        "%DeviceName%=SBMS_Install, $($contract.pnp.hardwareId)",
        "%DeviceName%=SBMS_Install, $($contract.pnp.rootHardwareId)",
        "HKR, `"WUDF`", `"DeviceGroupId`", %REG_SZ%, `"$($contract.pnp.deviceGroupId)`"",
        "UmdfService=$($contract.pnp.serviceName),$($contract.pnp.serviceName)_Install",
        "UmdfServiceOrder=$($contract.pnp.serviceName)",
        "ServiceBinary=%12%\UMDF\$($contract.package.dllName)",
        "ManufacturerName=`"$($contract.pnp.provider)`"",
        "DeviceName=`"$($contract.pnp.deviceName)`""
    )
    foreach ($value in $required) {
        if ($inf.IndexOf($value, [StringComparison]::Ordinal) -lt 0) {
            throw "Driver INF does not match identity contract: missing '$value'."
        }
    }
    foreach ($legacy in @(
            '<Your manufacturer name>',
            'TODO: Replace',
            'TODO: edit',
            'Root\IddSampleDriver',
            'IddSampleDriverGroup',
            'UmdfService=IddSampleDriver',
            'ServiceBinary=%12%\UMDF\IddSampleDriver.dll')) {
        if ($inf.IndexOf($legacy, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Driver INF contains legacy sample identity: '$legacy'."
        }
    }
}

function New-SBMSDriverCandidate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $DriverDirectory,

        [Parameter(Mandatory = $true)]
        [string] $OutputDirectory,

        [Parameter(Mandatory = $true)]
        [string] $SourceCommit,

        [Parameter(Mandatory = $true)]
        [bool] $SourceDirty,

        [Parameter(Mandatory = $true)]
        [string] $BuildCommand,

        [Parameter(Mandatory = $true)]
        [psobject] $Toolchain,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedWindowsVersion,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedProductVersion,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedDriverVer,

        [Parameter(Mandatory = $true)]
        [psobject] $SigningPolicy,

        [string] $SignToolPath,

        [scriptblock] $ToolInvoker,

        [psobject] $DllSignature
    )

    if ($SourceCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw "Driver candidate source commit is invalid: '$SourceCommit'."
    }
    if ($SourceDirty) {
        throw 'WHQL driver candidates require a clean source tree.'
    }

    $source = [System.IO.Path]::GetFullPath($DriverDirectory)
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Driver build directory not found: $source"
    }
    $inf = Get-SBMSSingleDriverFile -Directory $source -Filter '*.inf'
    $dll = Get-SBMSSingleDriverFile -Directory $source -Filter '*.dll'
    $identity = Get-SBMSDriverIdentityContract
    if ([string]$inf.Name -cne [string]$identity.contract.package.infName -or
        [string]$dll.Name -cne [string]$identity.contract.package.dllName) {
        throw 'Driver candidate artifact names do not match the identity contract.'
    }
    Assert-SBMSDriverInfIdentity -LiteralPath $inf.FullName -Identity $identity
    $driverVerMatch = Select-String -LiteralPath $inf.FullName -Pattern '^\s*DriverVer\s*=\s*(.+?)\s*$' |
        Select-Object -First 1
    if (-not $driverVerMatch) {
        throw "DriverVer is missing from '$($inf.FullName)'."
    }
    $actualDriverVer = [string]$driverVerMatch.Matches[0].Groups[1].Value.Trim()
    if ($actualDriverVer -cne $ExpectedDriverVer) {
        throw "Driver candidate DriverVer mismatch. Expected '$ExpectedDriverVer', actual '$actualDriverVer'."
    }
    $dllVersion = $dll.VersionInfo
    if ([string]$dllVersion.FileVersion -cne $ExpectedWindowsVersion -or
        [string]$dllVersion.ProductVersion -cne $ExpectedProductVersion) {
        throw "Driver candidate DLL version mismatch. Expected FileVersion=$ExpectedWindowsVersion ProductVersion=$ExpectedProductVersion; actual FileVersion=$($dllVersion.FileVersion) ProductVersion=$($dllVersion.ProductVersion)."
    }
    $dllSignatureResult = Assert-SBMSAuthenticodeSignature `
        -LiteralPath $dll.FullName `
        -Policy $SigningPolicy `
        -SignToolPath $SignToolPath `
        -ToolInvoker $ToolInvoker `
        -Signature $DllSignature

    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) {
        if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
            throw "Driver candidate output must be absent or empty: $output"
        }
    } else {
        New-Item -ItemType Directory -Path $output -Force | Out-Null
    }
    $payload = Join-Path $output 'driver'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -LiteralPath $inf.FullName -Destination (Join-Path $payload $inf.Name)
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $payload $dll.Name)
    Copy-Item -LiteralPath $identity.path -Destination (Join-Path $output 'driver-identity.json')

    $artifacts = @(
        Get-SBMSFileRecord -LiteralPath (Join-Path $payload $inf.Name) -RelativePath "driver/$($inf.Name)"
        Get-SBMSFileRecord -LiteralPath (Join-Path $payload $dll.Name) -RelativePath "driver/$($dll.Name)"
    )
    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 2
        kind = 'SBMS-WHQL-driver-candidate'
        createdUtc = [datetime]::UtcNow.ToString('o')
        source = [pscustomobject][ordered]@{
            commit = $SourceCommit.ToLowerInvariant()
            dirty = $false
            buildCommand = $BuildCommand
        }
        driver = [pscustomobject][ordered]@{
            driverVer = $actualDriverVer
            inf = $inf.Name
            dll = $dll.Name
            identitySchema = [int]$identity.contract.schemaVersion
            identityFingerprint = [string]$identity.fingerprint
            signature = [pscustomobject][ordered]@{
                status = [string]$dllSignatureResult.status
                signerSubject = [string]$dllSignatureResult.signerSubject
                signerThumbprint = [string]$dllSignatureResult.signerThumbprint
                timestampSubject = [string]$dllSignatureResult.timestampSubject
                timestampThumbprint = [string]$dllSignatureResult.timestampThumbprint
            }
        }
        toolchain = $Toolchain
        artifacts = $artifacts
    }
    $manifestPath = Join-Path $output 'SBMS.driver-candidate.json'
    Write-SBMSUtf8Json -LiteralPath $manifestPath -Value $manifest

    [pscustomobject][ordered]@{
        directory = $output
        manifestPath = $manifestPath
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        manifest = $manifest
    }
}

function Import-SBMSWhqlDriver {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $CandidateDirectory,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedCandidateManifestSha256,

        [Parameter(Mandatory = $true)]
        [string] $ReturnedDirectory,

        [Parameter(Mandatory = $true)]
        [string] $OutputDirectory,

        [Parameter(Mandatory = $true)]
        [psobject] $SigningPolicy,

        [Parameter(Mandatory = $true)]
        [string] $PrivateProductId,

        [Parameter(Mandatory = $true)]
        [string] $SharedProductId,

        [Parameter(Mandatory = $true)]
        [string] $SubmissionId,

        [Parameter(Mandatory = $true)]
        [string] $HlkPackagePath,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedHlkPackageSha256,

        [string] $SignToolPath,

        [scriptblock] $ToolInvoker,

        [psobject] $CatalogSignature
    )

    foreach ($entry in @(
        [pscustomobject]@{ Name = 'PrivateProductId'; Value = $PrivateProductId },
        [pscustomobject]@{ Name = 'SharedProductId'; Value = $SharedProductId },
        [pscustomobject]@{ Name = 'SubmissionId'; Value = $SubmissionId }
    )) {
        if ([string]$entry.Value -notmatch '^[1-9][0-9]*$') {
            throw "$($entry.Name) must be a non-zero decimal identifier copied from Partner Center."
        }
    }
    $normalizedExpectedHlkPackageSha256 = $ExpectedHlkPackageSha256.ToLowerInvariant()
    if ($normalizedExpectedHlkPackageSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Expected HLK submission package SHA-256 must contain exactly 64 hexadecimal characters.'
    }
    $resolvedHlkPackagePath = [System.IO.Path]::GetFullPath($HlkPackagePath)
    if (-not (Test-Path -LiteralPath $resolvedHlkPackagePath -PathType Leaf)) {
        throw "Archived HLK submission package not found: $resolvedHlkPackagePath"
    }
    $actualHlkPackageSha256 = (
        Get-FileHash -LiteralPath $resolvedHlkPackagePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualHlkPackageSha256 -cne $normalizedExpectedHlkPackageSha256) {
        throw "HLK submission package hash mismatch. Expected $normalizedExpectedHlkPackageSha256, actual $actualHlkPackageSha256."
    }

    $candidate = [System.IO.Path]::GetFullPath($CandidateDirectory)
    $manifestPath = Join-Path $candidate 'SBMS.driver-candidate.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Driver candidate manifest not found: $manifestPath"
    }
    $expectedManifestHash = $ExpectedCandidateManifestSha256.ToLowerInvariant()
    if ($expectedManifestHash -notmatch '^[0-9a-f]{64}$') {
        throw 'Expected candidate manifest SHA-256 must contain exactly 64 hexadecimal characters.'
    }
    $actualManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualManifestHash -cne $expectedManifestHash) {
        throw "Driver candidate manifest hash mismatch. Expected $expectedManifestHash, actual $actualManifestHash."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 2 -or
        [string]$manifest.kind -cne 'SBMS-WHQL-driver-candidate' -or
        [bool]$manifest.source.dirty) {
        throw 'Driver candidate manifest is not an eligible clean WHQL candidate.'
    }
    $identityPath = Join-Path $candidate 'driver-identity.json'
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            [string]$manifest.driver.identityFingerprint) {
        throw 'Driver candidate identity contract is missing or drifted.'
    }
    $identity = [pscustomobject][ordered]@{
        path = $identityPath
        fingerprint = [string]$manifest.driver.identityFingerprint
        contract = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    if ([int]$identity.contract.schemaVersion -ne [int]$manifest.driver.identitySchema) {
        throw 'Driver candidate identity schema does not match its manifest.'
    }

    $returned = [System.IO.Path]::GetFullPath($ReturnedDirectory)
    $inf = Get-SBMSSingleDriverFile -Directory $returned -Filter '*.inf'
    $dll = Get-SBMSSingleDriverFile -Directory $returned -Filter '*.dll'
    $cat = Get-SBMSSingleDriverFile -Directory $returned -Filter '*.cat'
    if ([string]$inf.Name -cne [string]$identity.contract.package.infName -or
        [string]$dll.Name -cne [string]$identity.contract.package.dllName -or
        [string]$cat.Name -cne [string]$identity.contract.package.catalogName) {
        throw 'Microsoft-returned package artifact names do not match the frozen identity contract.'
    }
    Assert-SBMSDriverInfIdentity -LiteralPath $inf.FullName -Identity $identity
    $returnedByName = @{
        ([string]$inf.Name).ToLowerInvariant() = $inf
        ([string]$dll.Name).ToLowerInvariant() = $dll
    }
    foreach ($artifact in @($manifest.artifacts)) {
        $name = [System.IO.Path]::GetFileName([string]$artifact.path).ToLowerInvariant()
        if (-not $returnedByName.ContainsKey($name)) {
            throw "Microsoft-returned package is missing frozen candidate file '$name'."
        }
        $returnedFile = $returnedByName[$name]
        $actualHash = (Get-FileHash -LiteralPath $returnedFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne [string]$artifact.sha256 -or
            [long]$returnedFile.Length -ne [long]$artifact.bytes) {
            throw "Microsoft-returned file changed after candidate freeze: '$($returnedFile.Name)'."
        }
    }

    $verification = Assert-SBMSWhqlPackage `
        -CatalogPath $cat.FullName `
        -PayloadPaths @($inf.FullName, $dll.FullName) `
        -Policy $SigningPolicy `
        -SignToolPath $SignToolPath `
        -ToolInvoker $ToolInvoker `
        -CatalogSignature $CatalogSignature

    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) {
        if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
            throw "WHQL import output must be absent or empty: $output"
        }
    } else {
        New-Item -ItemType Directory -Path $output -Force | Out-Null
    }
    foreach ($file in @($inf, $dll, $cat)) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $output $file.Name)
    }
    Copy-Item -LiteralPath $identityPath -Destination (Join-Path $output 'driver-identity.json')

    $records = @(
        Get-SBMSFileRecord -LiteralPath (Join-Path $output $inf.Name) -RelativePath $inf.Name
        Get-SBMSFileRecord -LiteralPath (Join-Path $output $dll.Name) -RelativePath $dll.Name
        Get-SBMSFileRecord -LiteralPath (Join-Path $output $cat.Name) -RelativePath $cat.Name
    )
    $importManifest = [pscustomobject][ordered]@{
        schemaVersion = 3
        kind = 'SBMS-WHQL-driver-import'
        importedUtc = [datetime]::UtcNow.ToString('o')
        candidateManifestSha256 = $actualManifestHash
        sourceCommit = [string]$manifest.source.commit
        driverVer = [string]$manifest.driver.driverVer
        identitySchema = [int]$manifest.driver.identitySchema
        identityFingerprint = [string]$manifest.driver.identityFingerprint
        driverSignature = $manifest.driver.signature
        certification = [pscustomobject][ordered]@{
            method = 'WHQL'
            catalog = $cat.Name
            signerSubject = [string]$verification.catalog.signerSubject
            signerThumbprint = [string]$verification.catalog.signerThumbprint
            timestampSubject = [string]$verification.catalog.timestampSubject
        }
        partnerCenter = [pscustomobject][ordered]@{
            privateProductId = $PrivateProductId
            sharedProductId = $SharedProductId
            submissionId = $SubmissionId
            hlkPackageSha256 = $actualHlkPackageSha256
        }
        artifacts = $records
    }
    $importManifestPath = Join-Path $output 'SBMS.driver-whql.json'
    Write-SBMSUtf8Json -LiteralPath $importManifestPath -Value $importManifest

    [pscustomobject][ordered]@{
        directory = $output
        manifestPath = $importManifestPath
        manifest = $importManifest
    }
}

Export-ModuleMember -Function @(
    'New-SBMSDriverCandidate',
    'Import-SBMSWhqlDriver'
)
