param(
    [switch] $RequireCleanSource
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0
$Root = $PSScriptRoot
$ExpectedVersion = (
    [System.IO.File]::ReadAllText(
        (Join-Path $Root 'VERSION'),
        [System.Text.Encoding]::UTF8
    )
).Trim()
$ExpectedBaseName = "SBMS-$ExpectedVersion-windows-x64"
$ExpectedZipName = "$ExpectedBaseName.zip"
$TestRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ("SBMS-package-contract-" + [System.Guid]::NewGuid().ToString('N'))

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $Condition,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Test {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Body
    )
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

try {
    $packageArguments = @{
        SkipProgramFiles = $true
        SkipSourceCopy = $true
        OutputRoot = $TestRoot
    }
    if (-not $RequireCleanSource) {
        $packageArguments.AllowDirtySource = $true
    }
    & (Join-Path $Root 'package-sbms.ps1') @packageArguments

    $ReleaseDir = Join-Path $TestRoot $ExpectedBaseName
    $ZipPath = Join-Path $TestRoot $ExpectedZipName
    $ManifestPath = Join-Path $ReleaseDir 'SBMS.release.json'
    $Manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 |
        ConvertFrom-Json

    Invoke-Test 'Package directory and ZIP derive from VERSION and architecture' {
        Assert-True (Test-Path -LiteralPath $ReleaseDir -PathType Container) 'Versioned release directory is missing.'
        Assert-True (Test-Path -LiteralPath $ZipPath -PathType Leaf) 'Versioned ZIP is missing.'
    }

    Invoke-Test 'Release manifest exposes product, component, commit, and package versions' {
        Assert-True ($Manifest.schemaVersion -eq 2) 'Unexpected release manifest schema.'
        Assert-True ($Manifest.product.version -ceq $ExpectedVersion) 'Product version drifted.'
        Assert-True ($Manifest.components.installer.productVersion -ceq $ExpectedVersion) 'Installer version drifted.'
        Assert-True ($Manifest.components.recoveryBroker.artifactName -ceq 'SBMSRecoveryBroker.exe') 'Recovery broker component is absent.'
        Assert-True ($Manifest.components.recoveryBroker.productVersion -ceq $ExpectedVersion) 'Recovery broker version drifted.'
        Assert-True ($Manifest.components.driver.productVersion -ceq $ExpectedVersion) 'Driver version drifted.'
        Assert-True ($Manifest.package.version -ceq $ExpectedVersion) 'Package version drifted.'
        Assert-True ($Manifest.package.fileName -ceq $ExpectedZipName) 'Package filename drifted.'
        Assert-True ($Manifest.package.architecture -ceq 'x64') 'Package architecture drifted.'
        Assert-True ([string]$Manifest.source.commit -match '^[0-9a-f]{40,64}$') 'Commit provenance is invalid.'
        Assert-True ($null -ne $Manifest.source.dirty) 'Dirty-source provenance is absent.'
    }

    Invoke-Test 'Every manifest artifact hash matches the packaged payload' {
        Assert-True (Test-Path -LiteralPath (Join-Path $ReleaseDir 'SBMSRecoveryBroker.exe') -PathType Leaf) 'Packaged recovery broker is missing.'
        Assert-True (@($Manifest.artifacts | Where-Object { $_.path -ceq 'SBMSRecoveryBroker.exe' }).Count -eq 1) 'Recovery broker artifact record is missing.'
        Assert-True (@($Manifest.artifacts).Count -gt 0) 'Release manifest has no artifacts.'
        foreach ($artifact in @($Manifest.artifacts)) {
            $artifactPath = Join-Path $ReleaseDir ([string]$artifact.path).Replace('/', '\')
            Assert-True (Test-Path -LiteralPath $artifactPath -PathType Leaf) "Missing artifact $($artifact.path)."
            $file = Get-Item -LiteralPath $artifactPath
            $hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-True ($file.Length -eq [long]$artifact.bytes) "Length mismatch for $($artifact.path)."
            Assert-True ($hash -ceq [string]$artifact.sha256) "SHA-256 mismatch for $($artifact.path)."
        }
    }

    Invoke-Test 'Version-only diagnostics report release provenance without live probes' {
        $output = & (Join-Path $ReleaseDir 'diagnose-sbms.ps1') -VersionOnly *>&1 |
            Out-String
        $outputLines = @($output -split '\r?\n')
        Assert-True ($output -match "ProductVersion=$([regex]::Escape($ExpectedVersion))") 'ProductVersion is absent.'
        Assert-True ($output -match "InstallerVersion=$([regex]::Escape($ExpectedVersion))") 'InstallerVersion is absent.'
        Assert-True ($output -match "PackageVersion=$([regex]::Escape($ExpectedVersion))") 'PackageVersion is absent.'
        Assert-True ($output -match "PackageName=$([regex]::Escape($ExpectedZipName))") 'PackageName is absent.'
        Assert-True (@($outputLines | Where-Object { $_ -match '^DriverVersion=.+$' }).Count -eq 1) 'DriverVersion is absent.'
        Assert-True (@($outputLines | Where-Object { $_ -match '^Commit=[0-9a-f]{40,64}$' }).Count -eq 1) 'Commit is absent.'
        Assert-True (@($outputLines | Where-Object { $_ -ceq 'Architecture=x64' }).Count -eq 1) 'Architecture is absent.'
        Assert-True ($output -notmatch '== processes ==') 'VersionOnly continued into live process probes.'
    }

    Invoke-Test 'Diagnostics reject stale release metadata' {
        $originalManifest = [System.IO.File]::ReadAllText(
            $ManifestPath,
            [System.Text.Encoding]::UTF8
        )
        try {
            $tampered = $originalManifest | ConvertFrom-Json
            $tampered.product.version = '9.9.9-rc.9'
            [System.IO.File]::WriteAllText(
                $ManifestPath,
                (($tampered | ConvertTo-Json -Depth 20) + "`n"),
                (New-Object System.Text.UTF8Encoding($false))
            )
            $rejected = $false
            try {
                & (Join-Path $ReleaseDir 'diagnose-sbms.ps1') -VersionOnly *>&1 | Out-Null
            } catch {
                $rejected = $true
            }
            Assert-True $rejected 'Diagnostics accepted a stale product version.'
        } finally {
            [System.IO.File]::WriteAllText(
                $ManifestPath,
                $originalManifest,
                (New-Object System.Text.UTF8Encoding($false))
            )
        }
    }

    Invoke-Test 'ZIP preserves the versioned top-level directory' {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
        try {
            $prefix = "$ExpectedBaseName/"
            $entries = @($archive.Entries)
            Assert-True ($entries.Count -gt 0) 'ZIP is empty.'
            Assert-True (@($entries | Where-Object {
                -not $_.FullName.StartsWith($prefix, [System.StringComparison]::Ordinal)
            }).Count -eq 0) 'ZIP contains files outside the versioned top-level directory.'
            Assert-True (@($entries | Where-Object {
                $_.FullName -ceq "${prefix}SBMS.release.json"
            }).Count -eq 1) 'ZIP does not contain exactly one release manifest.'
        } finally {
            $archive.Dispose()
        }
    }
} finally {
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $fullTestRoot = [System.IO.Path]::GetFullPath($TestRoot)
    if ($fullTestRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $fullTestRoot)) {
        Remove-Item -LiteralPath $fullTestRoot -Recurse -Force
    }
}

Write-Host "RESULT passed=$script:Passed failed=$script:Failed"
if ($script:Failed -ne 0) {
    exit 1
}
