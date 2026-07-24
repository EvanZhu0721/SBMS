Set-StrictMode -Version 2.0

function Assert-SBMSWhqlDriverArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $ReleaseManifest,

        [Parameter(Mandatory = $true)]
        [string] $PayloadRoot
    )

    $payload = [System.IO.Path]::GetFullPath($PayloadRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $prefix = $payload + [System.IO.Path]::DirectorySeparatorChar
    $required = [ordered]@{
        inf = 'driver/SBMSIndirectDisplay/SBMSIndirectDisplay.inf'
        dll = 'driver/SBMSIndirectDisplay/SBMSIndirectDisplay.dll'
        cat = 'driver/SBMSIndirectDisplay/sbmsindirectdisplay.cat'
        identity = 'driver/SBMSIndirectDisplay/driver-identity.json'
        whqlImport = 'driver/SBMSIndirectDisplay/SBMS.driver-whql.json'
    }

    $artifactMap = @{}
    foreach ($artifact in @($ReleaseManifest.artifacts)) {
        $relative = ([string]$artifact.path).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [System.IO.Path]::IsPathRooted($relative) -or
            $relative.Contains(':') -or
            @($relative.Split('/') | Where-Object { $_ -eq '..' }).Count -gt 0) {
            throw "Unsafe release artifact path: $relative"
        }
        $key = $relative.ToLowerInvariant()
        if ($artifactMap.ContainsKey($key)) {
            throw "Duplicate release artifact path: $relative"
        }
        $artifactMap[$key] = $artifact
    }

    $result = [ordered]@{}
    foreach ($name in $required.Keys) {
        $relative = [string]$required[$name]
        $key = $relative.ToLowerInvariant()
        if (-not $artifactMap.ContainsKey($key)) {
            throw "Production release does not list required driver artifact: $relative"
        }
        $full = [System.IO.Path]::GetFullPath(
            (Join-Path $payload $relative.Replace('/', '\'))
        )
        if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Production driver artifact escapes release payload: $relative"
        }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Production driver artifact is missing: $relative"
        }
        $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Production driver artifact cannot be a reparse point: $relative"
        }
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $artifact = $artifactMap[$key]
        if ($hash -cne [string]$artifact.sha256 -or
            [long]$item.Length -ne [long]$artifact.bytes) {
            throw "Production driver artifact metadata mismatch: $relative"
        }
        $result[$name + 'Path'] = $full
    }

    $driverRoot = Split-Path -Parent ([string]$result.infPath)
    $cursor = $payload
    foreach ($segment in @('driver', 'SBMSIndirectDisplay')) {
        $cursor = Join-Path $cursor $segment
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Production driver directory cannot be a reparse point: $cursor"
        }
    }
    foreach ($extension in @('*.inf', '*.dll', '*.cat')) {
        if (@(Get-ChildItem -LiteralPath $driverRoot -Filter $extension -File).Count -ne 1) {
            throw "Production driver payload must contain exactly one $extension artifact."
        }
    }

    $result.driverRoot = $driverRoot
    [pscustomobject]$result
}

function Assert-SBMSWhqlReleaseProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $ReleaseManifest,

        [Parameter(Mandatory = $true)]
        [psobject] $WhqlImportManifest
    )

    if ([int]$ReleaseManifest.schemaVersion -ne 4 -or
        [string]$ReleaseManifest.profile -cne 'Production' -or
        [string]$ReleaseManifest.driverCertification.method -cne 'WHQL' -or
        [string]$ReleaseManifest.source.commit -notmatch '^[0-9a-f]{40,64}$' -or
        [bool]$ReleaseManifest.source.dirty -or
        [string]$ReleaseManifest.driverCertification.sourceCommit -cne
            [string]$ReleaseManifest.source.commit -or
        [string]$ReleaseManifest.driverCertification.candidateManifestSha256 -notmatch
            '^[0-9a-f]{64}$' -or
        [string]$ReleaseManifest.driverCertification.identityFingerprint -notmatch
            '^[0-9a-f]{64}$') {
        throw 'Production release WHQL provenance is invalid.'
    }

    foreach ($field in @('privateProductId', 'sharedProductId', 'submissionId')) {
        if ([string]$ReleaseManifest.driverCertification.partnerCenter.$field -notmatch
            '^[1-9][0-9]*$') {
            throw "Production release Partner Center provenance is invalid: $field"
        }
    }
    if ([string]$ReleaseManifest.driverCertification.partnerCenter.hlkPackageSha256 -notmatch
        '^[0-9a-f]{64}$') {
        throw 'Production release HLK package provenance is invalid.'
    }

    if ([int]$WhqlImportManifest.schemaVersion -ne 3 -or
        [string]$WhqlImportManifest.kind -cne 'SBMS-WHQL-driver-import' -or
        [string]$WhqlImportManifest.certification.method -cne 'WHQL' -or
        [string]$WhqlImportManifest.sourceCommit -cne [string]$ReleaseManifest.source.commit -or
        [string]$WhqlImportManifest.driverVer -cne [string]$ReleaseManifest.product.driverVer -or
        [string]$WhqlImportManifest.candidateManifestSha256 -cne
            [string]$ReleaseManifest.driverCertification.candidateManifestSha256 -or
        [int]$WhqlImportManifest.identitySchema -ne
            [int]$ReleaseManifest.driverCertification.identitySchema -or
        [string]$WhqlImportManifest.identityFingerprint -cne
            [string]$ReleaseManifest.driverCertification.identityFingerprint) {
        throw 'WHQL import provenance does not match the production release manifest.'
    }

    foreach ($field in @(
        'privateProductId',
        'sharedProductId',
        'submissionId',
        'hlkPackageSha256'
    )) {
        if ([string]$WhqlImportManifest.partnerCenter.$field -cne
            [string]$ReleaseManifest.driverCertification.partnerCenter.$field) {
            throw "WHQL Partner Center provenance mismatch: $field"
        }
    }

    [pscustomobject][ordered]@{
        sourceCommit = [string]$ReleaseManifest.source.commit
        candidateManifestSha256 =
            [string]$ReleaseManifest.driverCertification.candidateManifestSha256
        privateProductId =
            [string]$ReleaseManifest.driverCertification.partnerCenter.privateProductId
        sharedProductId =
            [string]$ReleaseManifest.driverCertification.partnerCenter.sharedProductId
        submissionId =
            [string]$ReleaseManifest.driverCertification.partnerCenter.submissionId
        hlkPackageSha256 =
            [string]$ReleaseManifest.driverCertification.partnerCenter.hlkPackageSha256
    }
}
