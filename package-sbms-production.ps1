param(
    [Parameter(Mandatory = $true)]
    [string] $SigningPolicyPath,

    [Parameter(Mandatory = $true)]
    [string] $WhqlDriverDirectory,

    [string] $SignToolPath,
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Import-Module (Join-Path $root 'build\SBMS.Version.psm1') -Force
Import-Module (Join-Path $root 'build\SBMS.Signing.psm1') -Force

function Write-Utf8Json {
    param([string] $LiteralPath, [object] $Value)
    [System.IO.File]::WriteAllText(
        $LiteralPath,
        (($Value | ConvertTo-Json -Depth 30) + "`n"),
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function Get-FileRecord {
    param([string] $LiteralPath, [string] $RelativePath)
    $item = Get-Item -LiteralPath $LiteralPath -ErrorAction Stop
    [pscustomobject][ordered]@{
        path = $RelativePath.Replace('\', '/')
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-SingleFile {
    param([string] $Directory, [string] $Filter)
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File)
    if ($files.Count -ne 1) {
        throw "Expected exactly one '$Filter' in '$Directory'; found $($files.Count)."
    }
    $files[0]
}

function New-ReleaseCatalog {
    param([string] $PayloadDirectory, [string] $CatalogPath)
    $script = @'
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
$result = New-FileCatalog -Path $env:SBMS_CATALOG_PAYLOAD -CatalogFilePath $env:SBMS_CATALOG_PATH -CatalogVersion 2.0
if (-not $result -or -not (Test-Path -LiteralPath $env:SBMS_CATALOG_PATH -PathType Leaf)) {
    throw 'New-FileCatalog did not create the release catalog.'
}
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $previousPayload = $env:SBMS_CATALOG_PAYLOAD
    $previousCatalog = $env:SBMS_CATALOG_PATH
    try {
        $env:SBMS_CATALOG_PAYLOAD = $PayloadDirectory
        $env:SBMS_CATALOG_PATH = $CatalogPath
        & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded
        if ($LASTEXITCODE -ne 0) {
            throw "New-FileCatalog failed with exit code $LASTEXITCODE."
        }
    } finally {
        $env:SBMS_CATALOG_PAYLOAD = $previousPayload
        $env:SBMS_CATALOG_PATH = $previousCatalog
    }
}

function New-ReleaseZip {
    param([string] $SourceDirectory, [string] $DestinationPath, [datetimeoffset] $Timestamp)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::Create)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false
        )
        try {
            $base = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
            $top = Split-Path -Leaf $base
            foreach ($file in @(Get-ChildItem -LiteralPath $base -Recurse -File | Sort-Object FullName)) {
                $relative = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    "$top/$relative",
                    [System.IO.Compression.CompressionLevel]::Optimal
                )
                $entry.LastWriteTime = $Timestamp
                $input = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $output = $entry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                } finally { $input.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

$metadata = Get-SBMSBuildMetadata -RepositoryRoot $root
Assert-SBMSVersionSourceContract -RepositoryRoot $root
if ($metadata.IsDirty) {
    throw 'Production packaging requires a clean Git worktree.'
}
$policy = Read-SBMSSigningPolicy -LiteralPath $SigningPolicyPath
$certificate = Resolve-SBMSSigningCertificate -Policy $policy
$signTool = Resolve-SBMSSignTool -LiteralPath $SignToolPath

$whql = [System.IO.Path]::GetFullPath($WhqlDriverDirectory)
$whqlManifestPath = Join-Path $whql 'SBMS.driver-whql.json'
if (-not (Test-Path -LiteralPath $whqlManifestPath -PathType Leaf)) {
    throw "WHQL import manifest not found: $whqlManifestPath"
}
$whqlManifest = Get-Content -LiteralPath $whqlManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$whqlManifest.schemaVersion -ne 1 -or
    [string]$whqlManifest.kind -cne 'SBMS-WHQL-driver-import' -or
    [string]$whqlManifest.certification.method -cne 'WHQL') {
    throw 'Driver directory is not a verified SBMS WHQL import.'
}
if ([string]$whqlManifest.sourceCommit -cne [string]$metadata.Commit) {
    throw "WHQL driver commit '$($whqlManifest.sourceCommit)' does not match release commit '$($metadata.Commit)'."
}
if ([string]$whqlManifest.driverVer -cne [string]$metadata.DriverVer) {
    throw "WHQL DriverVer '$($whqlManifest.driverVer)' does not match release DriverVer '$($metadata.DriverVer)'."
}
foreach ($artifact in @($whqlManifest.artifacts)) {
    $path = Join-Path $whql ([string]$artifact.path).Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "WHQL import artifact is missing: $($artifact.path)"
    }
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne [string]$artifact.sha256 -or [long]$item.Length -ne [long]$artifact.bytes) {
        throw "WHQL import artifact drifted: $($artifact.path)"
    }
}
$driverInf = Get-SingleFile -Directory $whql -Filter '*.inf'
$driverDll = Get-SingleFile -Directory $whql -Filter '*.dll'
$driverCat = Get-SingleFile -Directory $whql -Filter '*.cat'
$driverVerification = Assert-SBMSWhqlPackage `
    -CatalogPath $driverCat.FullName `
    -PayloadPaths @($driverInf.FullName, $driverDll.FullName) `
    -Policy $policy `
    -SignToolPath $signTool
$driverEmbeddedSignature = Assert-SBMSAuthenticodeSignature `
    -LiteralPath $driverDll.FullName `
    -Policy $policy `
    -SignToolPath $signTool

& (Join-Path $root 'build-sbms-device-host.ps1')
& (Join-Path $root 'build-sbms-native.ps1')
& (Join-Path $root 'build-sbms-gui.ps1')
& (Join-Path $root 'build-sbms-setup.ps1') `
    -Production `
    -SigningPolicyPath $SigningPolicyPath `
    -SignToolPath $signTool

$executables = @(
    (Join-Path $root 'SBMS.exe'),
    (Join-Path $root 'SBMSNative.exe'),
    (Join-Path $root 'SBMSDeviceHost.exe')
)
foreach ($executable in $executables) {
    $null = Invoke-SBMSSignAuthenticode `
        -LiteralPath $executable `
        -Policy $policy `
        -SignToolPath $signTool
}
$signatureRecords = @{}
foreach ($executable in @($executables + (Join-Path $root 'SBMSSetup.exe'))) {
    $signature = Assert-SBMSAuthenticodeSignature `
        -LiteralPath $executable `
        -Policy $policy `
        -SignToolPath $signTool
    $signatureRecords[(Split-Path -Leaf $executable)] = [pscustomobject][ordered]@{
        status = [string]$signature.status
        signerSubject = [string]$signature.signerSubject
        signerThumbprint = [string]$signature.signerThumbprint
        timestampSubject = [string]$signature.timestampSubject
        timestampThumbprint = [string]$signature.timestampThumbprint
    }
    $versionInfo = (Get-Item -LiteralPath $executable).VersionInfo
    if ([string]$versionInfo.FileVersion -cne [string]$metadata.WindowsVersion -or
        [string]$versionInfo.ProductVersion -cne [string]$metadata.SemVer) {
        throw "Signed executable version mismatch: $executable"
    }
}
$driverVersionInfo = $driverDll.VersionInfo
if ([string]$driverVersionInfo.FileVersion -cne [string]$metadata.WindowsVersion -or
    [string]$driverVersionInfo.ProductVersion -cne [string]$metadata.SemVer) {
    throw "WHQL driver DLL version does not match release VERSION: $($driverDll.FullName)"
}

$documents = [Environment]::GetFolderPath('MyDocuments')
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $documents 'SBMS-Release'
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$releaseDirectory = Join-Path $releaseRoot $metadata.PackageBaseName
$zipPath = Join-Path $releaseRoot $metadata.PackageFileName
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $releaseDirectory) {
    throw "Production output already exists; refusing to overwrite: $releaseDirectory"
}
if (Test-Path -LiteralPath $zipPath) {
    throw "Production ZIP already exists; refusing to overwrite: $zipPath"
}

try {
New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
$payload = New-Item -ItemType Directory -Path (Join-Path $releaseDirectory 'payload')
Copy-Item -LiteralPath (Join-Path $root 'SBMSSetup.exe') -Destination $releaseDirectory

foreach ($name in @(
        'VERSION',
        'SBMS.exe',
        'SBMSNative.exe',
        'SBMSDeviceHost.exe',
        'README.md',
        'NOTICE.md',
        'RELEASE_NOTES.md',
        'install-sbms-driver.ps1',
        'install-sbms-program-files.ps1',
        'run-sbms-native.ps1',
        'diagnose-sbms.ps1'
    )) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $payload.FullName
}
$driverOutput = New-Item -ItemType Directory -Path (Join-Path $payload.FullName 'driver\IddSampleDriver') -Force
foreach ($file in @($driverInf, $driverDll, $driverCat)) {
    Copy-Item -LiteralPath $file.FullName -Destination $driverOutput.FullName
}
Copy-Item -LiteralPath $whqlManifestPath -Destination $driverOutput.FullName

$filesBeforeSbom = @(Get-ChildItem -LiteralPath $payload.FullName -Recurse -File | Sort-Object FullName)
$spdxFiles = @()
$spdxSha1 = @()
for ($index = 0; $index -lt $filesBeforeSbom.Count; $index++) {
    $file = $filesBeforeSbom[$index]
    $relative = $file.FullName.Substring($payload.FullName.TrimEnd('\').Length + 1).Replace('\', '/')
    $spdxFiles += [pscustomobject][ordered]@{
        fileName = "./$relative"
        SPDXID = "SPDXRef-File-$($index + 1)"
        checksums = @([pscustomobject][ordered]@{
            algorithm = 'SHA256'
            checksumValue = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        copyrightText = 'NOASSERTION'
    }
    $spdxSha1 += (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
}
$verificationInput = (@($spdxSha1 | Sort-Object) -join '')
$sha1 = [Security.Cryptography.SHA1]::Create()
try {
    $packageVerificationCode = (
        $sha1.ComputeHash([Text.Encoding]::ASCII.GetBytes($verificationInput)) |
            ForEach-Object { $_.ToString('x2') }
    ) -join ''
} finally {
    $sha1.Dispose()
}
$spdxRelationships = @(
    [pscustomobject][ordered]@{
        spdxElementId = 'SPDXRef-DOCUMENT'
        relationshipType = 'DESCRIBES'
        relatedSpdxElement = 'SPDXRef-Package-SBMS'
    }
)
foreach ($spdxFile in $spdxFiles) {
    $spdxRelationships += [pscustomobject][ordered]@{
        spdxElementId = 'SPDXRef-Package-SBMS'
        relationshipType = 'CONTAINS'
        relatedSpdxElement = [string]$spdxFile.SPDXID
    }
}
$sbom = [pscustomobject][ordered]@{
    spdxVersion = 'SPDX-2.2'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "SBMS-$($metadata.SemVer)"
    documentNamespace = "https://github.com/EvanZhu0721/SBMS/releases/$($metadata.Commit)/spdx"
    creationInfo = [pscustomobject][ordered]@{
        created = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: SBMS-production-packager')
    }
    packages = @([pscustomobject][ordered]@{
        name = 'SBMS'
        SPDXID = 'SPDXRef-Package-SBMS'
        versionInfo = $metadata.SemVer
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $true
        packageVerificationCode = [pscustomobject][ordered]@{
            packageVerificationCodeValue = $packageVerificationCode
        }
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    })
    files = $spdxFiles
    relationships = $spdxRelationships
}
Write-Utf8Json -LiteralPath (Join-Path $payload.FullName 'SBMS.spdx.json') -Value $sbom

$artifactRecords = @(
    Get-ChildItem -LiteralPath $payload.FullName -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($payload.FullName.TrimEnd('\').Length + 1)
            Get-FileRecord -LiteralPath $_.FullName -RelativePath $relative
        }
)
$manifest = [pscustomobject][ordered]@{
    schemaVersion = 3
    profile = 'Production'
    product = [pscustomobject][ordered]@{
        name = 'SBMS'
        version = $metadata.SemVer
        windowsVersion = $metadata.WindowsVersion
        driverVer = $metadata.DriverVer
        architecture = 'x64'
    }
    source = [pscustomobject][ordered]@{
        commit = $metadata.Commit
        commitDateUtc = $metadata.CommitDateUtc
        dirty = $false
    }
    signing = [pscustomobject][ordered]@{
        digest = 'SHA256'
        timestamp = 'RFC3161-SHA256'
        publisherSubject = [string]$certificate.Subject
        publisherThumbprint = [string]$policy.publisher.thumbprint
        executables = $signatureRecords
    }
    installer = [pscustomobject][ordered]@{
        path = 'SBMSSetup.exe'
        bytes = [long](Get-Item -LiteralPath (Join-Path $releaseDirectory 'SBMSSetup.exe')).Length
        sha256 = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory 'SBMSSetup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        fileVersion = $metadata.WindowsVersion
        productVersion = $metadata.SemVer
        signature = $signatureRecords['SBMSSetup.exe']
    }
    driverCertification = [pscustomobject][ordered]@{
        method = 'WHQL'
        sourceCommit = [string]$whqlManifest.sourceCommit
        candidateManifestSha256 = [string]$whqlManifest.candidateManifestSha256
        catalog = $driverCat.Name
        signerSubject = [string]$driverVerification.catalog.signerSubject
        signerThumbprint = [string]$driverVerification.catalog.signerThumbprint
        embeddedSignature = [pscustomobject][ordered]@{
            status = [string]$driverEmbeddedSignature.status
            signerSubject = [string]$driverEmbeddedSignature.signerSubject
            signerThumbprint = [string]$driverEmbeddedSignature.signerThumbprint
            timestampSubject = [string]$driverEmbeddedSignature.timestampSubject
            timestampThumbprint = [string]$driverEmbeddedSignature.timestampThumbprint
        }
    }
    integrity = [pscustomobject][ordered]@{
        catalog = 'SBMS.release.cat'
        catalogVersion = '2.0'
        hashAlgorithm = 'SHA256'
    }
    sbom = [pscustomobject][ordered]@{
        path = 'SBMS.spdx.json'
        format = 'SPDX'
        specVersion = '2.2'
    }
    toolchain = [pscustomobject][ordered]@{
        powershell = $PSVersionTable.PSVersion.ToString()
        operatingSystem = [Environment]::OSVersion.VersionString
        signTool = (Get-Item -LiteralPath $signTool).VersionInfo.FileVersion
    }
    artifacts = $artifactRecords
}
Write-Utf8Json -LiteralPath (Join-Path $payload.FullName 'SBMS.release.json') -Value $manifest

$releaseCatalog = Join-Path $releaseDirectory 'SBMS.release.cat'
New-ReleaseCatalog -PayloadDirectory $payload.FullName -CatalogPath $releaseCatalog
$null = Invoke-SBMSSignAuthenticode `
    -LiteralPath $releaseCatalog `
    -Policy $policy `
    -SignToolPath $signTool
$null = Assert-SBMSAuthenticodeSignature `
    -LiteralPath $releaseCatalog `
    -Policy $policy `
    -SignToolPath $signTool

$zipTimestamp = [datetimeoffset]::Parse(
    $metadata.CommitDateUtc,
    [Globalization.CultureInfo]::InvariantCulture
)
New-ReleaseZip -SourceDirectory $releaseDirectory -DestinationPath $zipPath -Timestamp $zipTimestamp

Write-Host "Production release: $releaseDirectory"
Write-Host "Production ZIP: $zipPath"
Write-Host "Commit: $($metadata.Commit)"
} catch {
    if (Test-Path -LiteralPath $releaseDirectory) {
        Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    throw
}
