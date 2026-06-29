param(
    [switch] $SkipBuild,
    [switch] $SkipProgramFiles,
    [switch] $SkipSourceCopy
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Documents = [Environment]::GetFolderPath("MyDocuments")
$ReleaseRoot = Join-Path $Documents "SBMS-Release"
$ReleaseDir = Join-Path $ReleaseRoot "SBMS"
$CoreDir = Join-Path $Documents "SBMS-Core-Source"
$ZipPath = Join-Path $ReleaseRoot "SBMS.zip"
$ProgramFilesDir = Join-Path $env:ProgramFiles "SBMS"
$ExistingDriverDir = Join-Path $ReleaseDir "driver"
$DriverBackupDir = Join-Path ([System.IO.Path]::GetTempPath()) ("SBMS-driver-backup-" + [System.Guid]::NewGuid().ToString("N"))

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

if (-not $SkipBuild) {
    & (Join-Path $Root "build-sbms-device-host.ps1")
    & (Join-Path $Root "build-sbms-native.ps1")
    & (Join-Path $Root "build-sbms-gui.ps1")
    & (Join-Path $Root "build-sbms-setup.ps1")
    & (Join-Path $Root "build-sbms-driver.ps1")
}

if (Test-Path -LiteralPath $ExistingDriverDir) {
    New-Item -ItemType Directory -Path $DriverBackupDir -Force | Out-Null
    Copy-Item -LiteralPath $ExistingDriverDir -Destination $DriverBackupDir -Recurse -Force
}

New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
$ReleaseDir = Reset-Directory -Path $ReleaseDir -Parent $ReleaseRoot

$sourceFiles = @(
    ".gitignore",
    "README.md",
    "NOTICE.md",
    "RELEASE_NOTES.md",
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
    $driverBackupPackage = Join-Path $DriverBackupDir "driver\IddSampleDriver"
    if (-not (Test-Path -LiteralPath $driverBackupPackage)) {
        throw "Missing built driver package: $driverPackage"
    }
    Copy-Item -LiteralPath $driverBackupPackage -Destination $driverReleaseDir.FullName -Recurse -Force
    Write-Host "Driver package reused from previous release."
}

$driverCer = Get-ChildItem -LiteralPath (Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\x64\Release") -Filter "IddSampleDriver.cer" -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($driverCer) {
    Copy-Item -LiteralPath $driverCer.FullName -Destination (Join-Path $ReleaseDir "driver") -Force
} else {
    $driverCerBackup = Join-Path $DriverBackupDir "driver\IddSampleDriver.cer"
    if (Test-Path -LiteralPath $driverCerBackup) {
        Copy-Item -LiteralPath $driverCerBackup -Destination (Join-Path $ReleaseDir "driver") -Force
    }
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -Path (Join-Path $ReleaseDir "*") -DestinationPath $ZipPath -Force

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
