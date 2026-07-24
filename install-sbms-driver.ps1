param(
    [switch] $Force,
    [switch] $KeepOld,
    [switch] $AllowTestSigned,
    [string] $VerifiedReleaseRoot,
    [switch] $VerifiedByInstaller
)

$ErrorActionPreference = "Stop"

$Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
$IsAdmin = $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $IsAdmin) {
    throw "Run this script from an elevated PowerShell."
}

if (-not $Force) {
    Write-Host "This stages the SBMS display driver package in Driver Store."
    Write-Host "Re-run with -Force for a Microsoft WHQL package."
    Write-Host "Use -AllowTestSigned only for an isolated development package."
    exit 10
}

$VerifiedProductionRoot = $null
$VerifiedProductionPayload = $null
if (-not $AllowTestSigned) {
    if (-not $VerifiedByInstaller -or [string]::IsNullOrWhiteSpace($VerifiedReleaseRoot)) {
        throw 'Production driver installation is restricted to the publisher-verified SBMS installer.'
    }
    $VerifiedProductionRoot = [System.IO.Path]::GetFullPath($VerifiedReleaseRoot)
    $VerifiedProductionPayload = Join-Path $VerifiedProductionRoot 'payload'
    if (-not (Test-Path -LiteralPath $VerifiedProductionPayload -PathType Container)) {
        throw "Publisher-verified release payload is missing: $VerifiedProductionPayload"
    }
}

function Assert-SBMSPayloadPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $PayloadRoot
    )

    $root = [System.IO.Path]::GetFullPath($PayloadRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $candidate = [System.IO.Path]::GetFullPath($Path)
    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Driver payload escapes the publisher-verified release payload: $candidate"
    }

    $relative = $candidate.Substring($prefix.Length)
    $cursor = $root
    foreach ($segment in $relative.Split(
        @(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        ),
        [System.StringSplitOptions]::RemoveEmptyEntries
    )) {
        $cursor = Join-Path $cursor $segment
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Publisher-verified driver payload contains a reparse point: $cursor"
        }
    }
}

function Assert-DriverPayload {
    param(
        [System.IO.FileInfo] $Inf
    )

    $Dir = $Inf.DirectoryName
    $Dll = Join-Path $Dir "SBMSIndirectDisplay.dll"
    $IdentityPath = Join-Path $Dir 'driver-identity.json'
    if (-not (Test-Path -LiteralPath $IdentityPath -PathType Leaf)) {
        throw "Driver identity contract is missing: $IdentityPath"
    }
    $Identity = Get-Content -LiteralPath $IdentityPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$Identity.schemaVersion -ne 1 -or
        [string]$Identity.package.infName -cne [string]$Inf.Name -or
        [string]$Identity.package.dllName -cne 'SBMSIndirectDisplay.dll' -or
        [string]$Identity.package.catalogName -cne 'sbmsindirectdisplay.cat') {
        throw 'Driver payload does not match the SBMS identity contract.'
    }
    $Cat = Get-ChildItem -LiteralPath $Dir -Filter "*.cat" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not (Test-Path -LiteralPath $Dll)) {
        throw "Driver DLL not found next to INF: $Dll"
    }
    if (-not $Cat) {
        throw "Driver catalog not found next to INF: $Dir"
    }

    $catSignature = Get-AuthenticodeSignature -LiteralPath $Cat.FullName
    if ($catSignature.Status -ne "Valid") {
        throw "Refusing to install invalid driver catalog: $($Cat.FullName) signature=$($catSignature.Status) $($catSignature.StatusMessage)"
    }
    if (-not $AllowTestSigned) {
        Assert-SBMSPayloadPath -Path $Inf.FullName -PayloadRoot $VerifiedProductionPayload
        $releaseCatalog = Join-Path $VerifiedProductionRoot 'SBMS.release.cat'
        $releasePayload = $VerifiedProductionPayload
        $releaseManifestPath = Join-Path $releasePayload 'SBMS.release.json'
        $releaseSignature = Get-AuthenticodeSignature -LiteralPath $releaseCatalog
        if ([string]$releaseSignature.Status -cne 'Valid' -or
            -not $releaseSignature.SignerCertificate -or
            -not $releaseSignature.TimeStamperCertificate) {
            throw 'Publisher release catalog is not valid and timestamped.'
        }
        $releaseResult = Test-FileCatalog `
            -Path $releasePayload `
            -CatalogFilePath $releaseCatalog `
            -Detailed
        if ([string]$releaseResult.Status -cne 'Valid') {
            throw "Publisher release payload is no longer catalog-valid: $($releaseResult.Status)"
        }
        $releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
        $releaseThumbprint = (
            [string]$releaseSignature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', ''
        ).ToUpperInvariant()
        if ([int]$releaseManifest.schemaVersion -ne 4 -or
            [string]$releaseManifest.profile -cne 'Production' -or
            [string]$releaseManifest.driverCertification.method -cne 'WHQL' -or
            [string]$releaseManifest.signing.publisherThumbprint -cne $releaseThumbprint) {
            throw 'Publisher release manifest does not authorize this production WHQL install.'
        }
        $provenanceVerifierPath = Join-Path $releasePayload 'Verify-SBMSWhqlProvenance.ps1'
        if (-not (Test-Path -LiteralPath $provenanceVerifierPath -PathType Leaf)) {
            throw 'Publisher-verified WHQL provenance verifier is missing.'
        }
        . $provenanceVerifierPath
        $driverArtifacts = Assert-SBMSWhqlDriverArtifacts `
            -ReleaseManifest $releaseManifest `
            -PayloadRoot $releasePayload
        if ([System.IO.Path]::GetFullPath($Inf.FullName) -ine
            [System.IO.Path]::GetFullPath([string]$driverArtifacts.infPath)) {
            throw 'Selected driver INF is not the artifact bound to the publisher-verified release.'
        }
        $whqlImport = Get-Content `
            -LiteralPath ([string]$driverArtifacts.whqlImportPath) `
            -Raw `
            -Encoding UTF8 |
            ConvertFrom-Json
        $null = Assert-SBMSWhqlReleaseProvenance `
            -ReleaseManifest $releaseManifest `
            -WhqlImportManifest $whqlImport
        if (-not $catSignature.SignerCertificate -or
            [string]$catSignature.SignerCertificate.Subject -cne
                [string]$releaseManifest.driverCertification.signerSubject) {
            throw 'Driver catalog signer does not match publisher-verified WHQL provenance.'
        }
        $dllSignature = Get-AuthenticodeSignature -LiteralPath $Dll
        $dllThumbprint = (
            [string]$dllSignature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', ''
        ).ToUpperInvariant()
        if ([string]$dllSignature.Status -cne 'Valid' -or
            $dllThumbprint -cne $releaseThumbprint -or
            -not $dllSignature.TimeStamperCertificate) {
            throw 'Driver DLL does not carry the publisher signature and timestamp authorized by the release.'
        }
        if (-not $catSignature.TimeStamperCertificate) {
            throw 'Refusing a WHQL catalog without a trusted timestamp.'
        }
    }

    $dllSignature = Get-AuthenticodeSignature -LiteralPath $Dll
    if ($dllSignature.Status -ne "Valid") {
        Write-Warning "Driver DLL embedded signature is $($dllSignature.Status); its bytes are covered by the verified catalog."
    }

    $DllHash = (Get-FileHash -LiteralPath $Dll -Algorithm SHA256).Hash
    $CatHash = (Get-FileHash -LiteralPath $Cat.FullName -Algorithm SHA256).Hash
    Write-Host "Driver payload DLL: $Dll"
    Write-Host "Driver payload DLL SHA256=$DllHash"
    Write-Host "Driver payload DLL signature=$($dllSignature.Status)"
    Write-Host "Driver payload CAT: $($Cat.FullName)"
    Write-Host "Driver payload CAT SHA256=$CatHash"
    Write-Host "Driver payload CAT signature=$($catSignature.Status)"
}

$DriverSearchRoots = if ($AllowTestSigned) {
    @(
        (Join-Path $PSScriptRoot "driver"),
        (Join-Path $PSScriptRoot "Windows-driver-samples\video\IndirectDisplay"),
        $PSScriptRoot
    ) | Where-Object { Test-Path $_ }
} else {
    @(Join-Path $VerifiedProductionPayload 'driver') |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }
}

$Inf = $DriverSearchRoots |
    ForEach-Object { Get-ChildItem $_ -Recurse -Filter "SBMSIndirectDisplay.inf" -ErrorAction SilentlyContinue } |
    Where-Object {
        $Dir = $_.DirectoryName
        (Test-Path (Join-Path $Dir "SBMSIndirectDisplay.dll")) -and
        (Test-Path (Join-Path $Dir "sbmsindirectdisplay.cat"))
    } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $Inf) {
    throw "Certified driver package not found beside the installer."
}

# Trust and payload checks must complete before making any machine mutation.
Assert-DriverPayload -Inf $Inf

# Issue #18 deliberately stages the verified package without requesting an
# active-device update. Rebinding and stale-package cleanup require the fully
# transactional PnP rollback owned by Issue #19.
$pnputil = Join-Path $env:SystemRoot 'System32\pnputil.exe'
$nativeArgs = @('/add-driver', $Inf.FullName)
Write-Host "Staging verified driver package without activating it: $($Inf.FullName)"
& $pnputil @nativeArgs | Out-Host
$addDriverExitCode = $LASTEXITCODE
if ($addDriverExitCode -ne 0) {
    throw "PnPUtil failed to stage the verified driver package (exit $addDriverExitCode). Active devices and existing packages were not intentionally changed."
}

if ($KeepOld) {
    Write-Verbose '-KeepOld is retained for command-line compatibility; staging now always preserves existing packages.'
}

Write-Host 'Driver package staged. Active-device transition is deferred to the transactional activation path.'
