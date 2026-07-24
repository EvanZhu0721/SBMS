param(
    [switch] $SkipProgramFiles,
    [switch] $SkipSourceCopy,
    [switch] $AllowDirtySource,
    [string] $OutputRoot
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$VersionModule = Join-Path $Root "build\SBMS.Version.psm1"
Import-Module $VersionModule -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
if ($BuildMetadata.IsDirty -and -not $AllowDirtySource) {
    throw 'Release packaging requires a clean Git worktree. Commit or remove all source changes before packaging.'
}
if ($BuildMetadata.IsDirty) {
    Write-Warning 'Packaging explicitly allowed from a dirty source tree. The release manifest will record source.dirty=true.'
}

$Documents = [Environment]::GetFolderPath("MyDocuments")
$ReleaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $Documents "SBMS-Release"
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$ReleaseDir = Join-Path $ReleaseRoot $BuildMetadata.PackageBaseName
$CoreDir = Join-Path $Documents "SBMS-Core-Source"
$ZipPath = Join-Path $ReleaseRoot $BuildMetadata.PackageFileName
$ProgramFilesDir = Join-Path $env:ProgramFiles "SBMS"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory=$true)] [string] $Path,
        [Parameter(Mandatory=$true)] [string] $Parent
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside expected parent: $fullPath"
    }
    return $fullPath
}

function Reset-Directory {
    param(
        [Parameter(Mandatory=$true)] [string] $Path,
        [Parameter(Mandatory=$true)] [string] $Parent
    )
    $fullPath = Assert-ChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $fullPath) {
        Get-ChildItem -LiteralPath $fullPath -Force |
            Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    }
    return $fullPath
}

function Reset-DirectoryKeepingGit {
    param(
        [Parameter(Mandatory=$true)] [string] $Path,
        [Parameter(Mandatory=$true)] [string] $Parent
    )
    $fullPath = Assert-ChildPath -Path $Path -Parent $Parent
    if (-not (Test-Path -LiteralPath $fullPath)) {
        New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
        return $fullPath
    }
    Get-ChildItem -LiteralPath $fullPath -Force |
        Where-Object { $_.Name -ne ".git" } |
        Remove-Item -Recurse -Force
    return $fullPath
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory=$true)] [string] $RelativePath,
        [Parameter(Mandatory=$true)] [string] $Destination
    )
    $source = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing required file: $source"
    }
    Copy-Item -LiteralPath $source -Destination $Destination -Force
}

function Copy-RequiredDirectory {
    param(
        [Parameter(Mandatory=$true)] [string] $RelativePath,
        [Parameter(Mandatory=$true)] [string] $DestinationRoot
    )
    $source = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing required directory: $source"
    }
    $destination = Join-Path $DestinationRoot $RelativePath
    $destinationParent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destinationParent -Recurse -Force
}

function New-SBMSZipArchive {
    param(
        [Parameter(Mandatory=$true)] [string] $SourceDirectory,
        [Parameter(Mandatory=$true)] [string] $DestinationPath,
        [Parameter(Mandatory=$true)] [System.DateTimeOffset] $Timestamp
    )

    Add-Type -AssemblyName System.IO.Compression
    $destinationFullPath = [System.IO.Path]::GetFullPath($DestinationPath)
    $sourceFullPath = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
    $topLevelName = [System.IO.Path]::GetFileName($sourceFullPath)
    $zipStream = [System.IO.File]::Open(
        $destinationFullPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None
    )
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $zipStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false
        )
        try {
            foreach ($file in (Get-ChildItem -LiteralPath $sourceFullPath -Recurse -File | Sort-Object FullName)) {
                $relativePath = $file.FullName.Substring($sourceFullPath.Length + 1).Replace('\', '/')
                $entryName = "$topLevelName/$relativePath"
                $entry = $archive.CreateEntry(
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal
                )
                $entry.LastWriteTime = $Timestamp
                $inputStream = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $outputStream = $entry.Open()
                    try {
                        $inputStream.CopyTo($outputStream)
                    } finally {
                        $outputStream.Dispose()
                    }
                } finally {
                    $inputStream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $zipStream.Dispose()
    }
}

function Assert-SBMSBuiltArtifactVersions {
    $artifacts = @(
        @{ Path = (Join-Path $Root 'SBMS.exe'); Name = 'GUI' },
        @{ Path = (Join-Path $Root 'SBMSSetup.exe'); Name = 'Installer' },
        @{ Path = (Join-Path $Root 'SBMSNative.exe'); Name = 'Native' },
        @{ Path = (Join-Path $Root 'SBMSDeviceHost.exe'); Name = 'DeviceHost' },
        @{
            Path = (Join-Path $Root 'Windows-driver-samples\video\IndirectDisplay\x64\Release\IddSampleDriver\IddSampleDriver.dll')
            Name = 'Driver'
        }
    )
    foreach ($artifact in $artifacts) {
        if (-not (Test-Path -LiteralPath $artifact.Path -PathType Leaf)) {
            throw "Missing built $($artifact.Name) artifact: $($artifact.Path)"
        }
        $versionInfo = (Get-Item -LiteralPath $artifact.Path).VersionInfo
        if ([string]$versionInfo.FileVersion -cne [string]$BuildMetadata.WindowsVersion -or
            [string]$versionInfo.ProductVersion -cne [string]$BuildMetadata.SemVer) {
            throw "$($artifact.Name) version mismatch. Expected FileVersion=$($BuildMetadata.WindowsVersion) ProductVersion=$($BuildMetadata.SemVer); found FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion). Rebuild before packaging."
        }
    }

    $driverInf = Join-Path $Root 'Windows-driver-samples\video\IndirectDisplay\x64\Release\IddSampleDriver\IddSampleDriver.inf'
    if (-not (Test-Path -LiteralPath $driverInf -PathType Leaf)) {
        throw "Missing built driver INF: $driverInf"
    }
    $driverVerMatch = Select-String -LiteralPath $driverInf -Pattern '^\s*DriverVer\s*=\s*(.+?)\s*$' |
        Select-Object -First 1
    $actualDriverVer = if ($driverVerMatch) {
        [string]$driverVerMatch.Matches[0].Groups[1].Value.Trim()
    } else {
        ''
    }
    if ($actualDriverVer -cne [string]$BuildMetadata.DriverVer) {
        throw "Driver INF version mismatch. Expected DriverVer=$($BuildMetadata.DriverVer); found DriverVer=$actualDriverVer. Rebuild before packaging."
    }
}

& (Join-Path $Root "build-sbms-device-host.ps1")
& (Join-Path $Root "build-sbms-native.ps1")
& (Join-Path $Root "build-sbms-gui.ps1")
& (Join-Path $Root "build-sbms-setup.ps1")
& (Join-Path $Root "build-sbms-driver.ps1")

Assert-SBMSBuiltArtifactVersions

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
$ReleaseDir = Reset-Directory -Path $ReleaseDir -Parent $ReleaseRoot

$sourceFiles = @(
    ".gitignore",
    "VERSION",
    "CHANGELOG.md",
    "README.md",
    "NOTICE.md",
    "RELEASE_NOTES.md",
    "test-sbms-version.ps1",
    "test-sbms-package.ps1",
    "build-sbms-device-host.ps1",
    "build-sbms-driver.ps1",
    "build-sbms-gui.ps1",
    "build-sbms-native.ps1",
    "build-sbms-setup.ps1",
    "install-sbms-driver.ps1",
    "install-sbms-program-files.ps1",
    "run-sbms-native.ps1",
    "diagnose-sbms.ps1",
    "check-displays.ps1",
    "check-displays.py",
    "package-sbms.ps1"
)
$sourceDirs = @(
    "build",
    "docs",
    "gui",
    "installer",
    "device-host",
    "native-output-demo",
    "driver-stable",
    "Windows-driver-samples\video\IndirectDisplay",
    "msbuild-vctargets-v170"
)

$rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
$coreFull = [System.IO.Path]::GetFullPath($CoreDir).TrimEnd('\')
if ($SkipSourceCopy -or [System.String]::Equals($rootFull, $coreFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Core source copy skipped: $CoreDir"
} else {
    $CoreDir = Reset-DirectoryKeepingGit -Path $CoreDir -Parent $Documents
    foreach ($file in $sourceFiles) {
        Copy-RequiredFile -RelativePath $file -Destination $CoreDir
    }

    foreach ($dir in $sourceDirs) {
        Copy-RequiredDirectory -RelativePath $dir -DestinationRoot $CoreDir
    }

    $generatedDirectoryNames = @(".vs", "x64", "Debug", "Release", "ipch", "__pycache__")
    Get-ChildItem -LiteralPath $CoreDir -Recurse -Directory -Force |
        Where-Object { $generatedDirectoryNames -contains $_.Name } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force

    $generatedExtensions = @(".exe", ".dll", ".obj", ".pdb", ".ilk", ".lib", ".exp", ".cat", ".cer", ".log")
    Get-ChildItem -LiteralPath $CoreDir -Recurse -File -Force |
        Where-Object { $generatedExtensions -contains $_.Extension.ToLowerInvariant() } |
        Remove-Item -Force
}

$releaseFiles = @(
    "VERSION",
    "SBMS.exe",
    "SBMSSetup.exe",
    "SBMSNative.exe",
    "SBMSDeviceHost.exe",
    "README.md",
    "NOTICE.md",
    "RELEASE_NOTES.md",
    "install-sbms-driver.ps1",
    "install-sbms-program-files.ps1",
    "run-sbms-native.ps1",
    "diagnose-sbms.ps1"
)
foreach ($file in $releaseFiles) {
    Copy-RequiredFile -RelativePath $file -Destination $ReleaseDir
}

$driverPackage = Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\x64\Release\IddSampleDriver"
$driverReleaseDir = New-Item -ItemType Directory -Path (Join-Path $ReleaseDir "driver") -Force
if (Test-Path -LiteralPath $driverPackage) {
    Copy-Item -LiteralPath $driverPackage -Destination $driverReleaseDir.FullName -Recurse -Force
} else {
    throw "Missing built driver package: $driverPackage"
}

$driverCer = Get-ChildItem -LiteralPath (Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\x64\Release") -Filter "IddSampleDriver.cer" -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($driverCer) {
    Copy-Item -LiteralPath $driverCer.FullName -Destination (Join-Path $ReleaseDir "driver") -Force
}

$historicalReleaseNotesPath = Join-Path $Root "RELEASE_NOTES.md"
$historicalReleaseNotes = [System.IO.File]::ReadAllText(
    $historicalReleaseNotesPath,
    [System.Text.Encoding]::UTF8
).TrimStart([char[]]"`r`n")
$currentReleaseNotes = @"
# SBMS $($BuildMetadata.SemVer)

- Package: ``$($BuildMetadata.PackageFileName)``
- Runtime: ``$($BuildMetadata.RuntimeIdentifier)``
- Windows file version: ``$($BuildMetadata.WindowsVersion)``
- DriverVer: ``$($BuildMetadata.DriverVer)``
- Source commit: ``$($BuildMetadata.Commit)``
- Source dirty: ``$($BuildMetadata.IsDirty.ToString().ToLowerInvariant())``

This release metadata is generated from the repository ``VERSION`` file.

---

$historicalReleaseNotes
"@
[System.IO.File]::WriteAllText(
    (Join-Path $ReleaseDir "RELEASE_NOTES.md"),
    (($currentReleaseNotes -replace "`r`n", "`n").TrimEnd() + "`n"),
    (New-Object System.Text.UTF8Encoding($false))
)

$artifactEntries = @(
    Get-ChildItem -LiteralPath $ReleaseDir -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($ReleaseDir.TrimEnd('\').Length + 1).Replace('\', '/')
            [pscustomobject][ordered]@{
                path = $relativePath
                bytes = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$releaseManifest = New-SBMSReleaseManifestData -Metadata $BuildMetadata -Artifacts $artifactEntries
$releaseManifestJson = ConvertTo-SBMSReleaseManifestJson -ManifestData $releaseManifest
$null = Write-SBMSGeneratedFile `
    -LiteralPath (Join-Path $ReleaseDir "SBMS.release.json") `
    -Content $releaseManifestJson

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
$zipTimestamp = [System.DateTimeOffset]::Parse(
    $BuildMetadata.CommitDateUtc,
    [System.Globalization.CultureInfo]::InvariantCulture
)
New-SBMSZipArchive `
    -SourceDirectory $ReleaseDir `
    -DestinationPath $ZipPath `
    -Timestamp $zipTimestamp

if (-not $SkipProgramFiles) {
    try {
        $programFilesParent = [System.IO.Path]::GetFullPath($env:ProgramFiles)
        $programFilesPath = Reset-Directory -Path $ProgramFilesDir -Parent $programFilesParent
        Copy-Item -Path (Join-Path $ReleaseDir "*") -Destination $programFilesPath -Recurse -Force
        Write-Host "Program Files copy: $programFilesPath"
    } catch {
        Write-Warning "Program Files copy skipped or failed: $($_.Exception.Message)"
    }
}

Write-Host "Core source: $CoreDir"
Write-Host "Release dir: $ReleaseDir"
Write-Host "Zip: $ZipPath"
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
