[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulePath = Join-Path $root 'build\SBMS.Version.psm1'
Import-Module $modulePath -Force

$script:Passed = 0
$script:Failed = 0

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [string] $Message)
    if ($Expected -ne $Actual) {
        throw "$Message Expected=[$Expected] Actual=[$Actual]"
    }
}

function Assert-Throws {
    param([scriptblock] $Action, [string] $Message)
    $threw = $false
    try { & $Action } catch { $threw = $true }
    if (-not $threw) { throw $Message }
}

function Invoke-TestCase {
    param([string] $Name, [scriptblock] $Action)
    try {
        & $Action
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name"
        Write-Host "  $($_.Exception.Message)"
    }
}

Invoke-TestCase 'Stable mapping uses CLR-compatible revision 65534' {
    $result = ConvertFrom-SBMSVersion '1.2.3'
    Assert-Equal '1.2.3' $result.SemVer 'SemVer changed.'
    Assert-Equal 'stable' $result.Channel 'Stable channel mismatch.'
    Assert-Equal $false $result.IsPrerelease 'Stable version marked prerelease.'
    Assert-Equal 65534 $result.WindowsRevision 'Stable revision mismatch.'
    Assert-Equal '1.2.3.65534' $result.WindowsVersion 'Stable Windows version mismatch.'
    Assert-Equal '1,2,3,65534' $result.WindowsVersionCsv 'Stable CSV mismatch.'
    Assert-Equal 'SBMS-1.2.3-windows-x64.zip' $result.PackageFileName 'Stable package name mismatch.'
}

$mappingCases = @(
    @('0.1.0-dev.0', 'dev', 0, '0.1.0.0'),
    @('9.8.7-dev.9999', 'dev', 9999, '9.8.7.9999'),
    @('9.8.7-alpha.0', 'alpha', 10000, '9.8.7.10000'),
    @('9.8.7-alpha.9999', 'alpha', 19999, '9.8.7.19999'),
    @('9.8.7-beta.0', 'beta', 20000, '9.8.7.20000'),
    @('9.8.7-beta.9999', 'beta', 29999, '9.8.7.29999'),
    @('9.8.7-rc.0', 'rc', 30000, '9.8.7.30000'),
    @('9.8.7-rc.9999', 'rc', 39999, '9.8.7.39999')
)
foreach ($case in $mappingCases) {
    $version = $case[0]
    Invoke-TestCase "Prerelease mapping $version" {
        $result = ConvertFrom-SBMSVersion $version
        Assert-Equal $case[1] $result.Channel 'Channel mismatch.'
        Assert-Equal $case[2] $result.WindowsRevision 'Revision mismatch.'
        Assert-Equal $case[3] $result.WindowsVersion 'Windows version mismatch.'
        Assert-Equal $true $result.IsPrerelease 'Prerelease flag mismatch.'
        Assert-Equal "v$version" $result.TagName 'Tag mismatch.'
        Assert-Equal "SBMS-$version-windows-x64.zip" $result.PackageFileName 'Package mismatch.'
    }.GetNewClosure()
}

Invoke-TestCase 'All numeric component boundaries are accepted' {
    $result = ConvertFrom-SBMSVersion '9999.9999.9999-rc.9999'
    Assert-Equal 9999 $result.Major 'Major upper bound mismatch.'
    Assert-Equal 9999 $result.Minor 'Minor upper bound mismatch.'
    Assert-Equal 9999 $result.Patch 'Patch upper bound mismatch.'
    Assert-Equal 9999 $result.PrereleaseNumber 'Prerelease upper bound mismatch.'
}

$invalidVersions = @(
    '',
    ' ',
    '1',
    '1.2',
    '1.2.3.4',
    '1.2.3-rc',
    '1.2.3-rc.',
    '1.2.3-preview.1',
    '1.2.3-RC.1',
    '1.2.3-dev.-1',
    '1.2.3-dev.10000',
    '10000.0.0',
    '0.10000.0',
    '0.0.10000',
    '01.2.3',
    '1.02.3',
    '1.2.03',
    '1.2.3-dev.00',
    '1.2.3+metadata',
    'v1.2.3',
    '1.2.3 ',
    "1.2.3`n"
)
foreach ($invalid in $invalidVersions) {
    Invoke-TestCase "Reject invalid version [$($invalid.Replace("`n", '\n'))]" {
        Assert-Throws { ConvertFrom-SBMSVersion $invalid } 'Invalid version was accepted.'
    }.GetNewClosure()
}

Invoke-TestCase 'VERSION reader allows one final line ending' {
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-' + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    try {
        $path = Join-Path $tempRoot 'VERSION'
        [System.IO.File]::WriteAllText($path, "2.3.4-beta.5`r`n", (New-Object Text.UTF8Encoding($false)))
        $result = Read-SBMSVersion -LiteralPath $path
        Assert-Equal '2.3.4-beta.5' $result.SemVer 'VERSION line ending was not removed.'
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Invoke-TestCase 'VERSION reader rejects multiple lines' {
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-' + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    try {
        $path = Join-Path $tempRoot 'VERSION'
        [System.IO.File]::WriteAllText($path, "1.2.3`nextra`n", (New-Object Text.UTF8Encoding($false)))
        Assert-Throws { Read-SBMSVersion -LiteralPath $path } 'Multiline VERSION was accepted.'
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Invoke-TestCase 'Build metadata normalizes commit and UTC date into DriverVer' {
    $version = ConvertFrom-SBMSVersion '2.4.6-beta.8'
    $commit = 'ABCDEF0123456789ABCDEF0123456789ABCDEF01'
    $metadata = New-SBMSBuildMetadata -VersionInfo $version -Commit $commit -CommitDateUtc '2026-07-25T01:02:03+08:00'
    Assert-Equal 'abcdef0123456789abcdef0123456789abcdef01' $metadata.Commit 'Commit was not normalized.'
    Assert-Equal 'abcdef012345' $metadata.ShortCommit 'Short commit mismatch.'
    Assert-Equal '2026-07-24T17:02:03Z' $metadata.CommitDateUtc 'UTC commit date mismatch.'
    Assert-Equal $false $metadata.IsDirty 'Default dirty state mismatch.'
    Assert-Equal '07/24/2026' $metadata.DriverDate 'Driver date mismatch.'
    Assert-Equal '07/24/2026,2.4.6.20008' $metadata.DriverVer 'DriverVer mismatch.'
}

Invoke-TestCase 'Build metadata accepts an explicit dirty state' {
    $metadata = New-SBMSBuildMetadata `
        -VersionInfo (ConvertFrom-SBMSVersion '2.4.6') `
        -Commit '0123456789abcdef0123456789abcdef01234567' `
        -CommitDateUtc '2026-07-25T00:00:00Z' `
        -IsDirty $true
    Assert-Equal $true $metadata.IsDirty 'Explicit dirty state was not preserved.'
}

Invoke-TestCase 'Invalid commit identifier is rejected' {
    $version = ConvertFrom-SBMSVersion '1.0.0'
    Assert-Throws {
        New-SBMSBuildMetadata -VersionInfo $version -Commit 'not-a-commit' -CommitDateUtc '2026-01-01T00:00:00Z'
    } 'Invalid commit was accepted.'
}

Invoke-TestCase 'Commit date without an explicit time zone is rejected' {
    $version = ConvertFrom-SBMSVersion '1.0.0'
    Assert-Throws {
        New-SBMSBuildMetadata `
            -VersionInfo $version `
            -Commit '0123456789abcdef0123456789abcdef01234567' `
            -CommitDateUtc '2026-07-25T00:00:00'
    } 'Offset-free commit date was accepted.'
}

Invoke-TestCase 'Repository metadata uses VERSION and Git commit date' {
    $metadata = Get-SBMSBuildMetadata -RepositoryRoot $root
    $expected = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw -Encoding UTF8).Trim()
    Assert-Equal $expected $metadata.SemVer 'Repository SemVer mismatch.'
    Assert-True ($metadata.Commit -match '^[0-9a-f]{40}$') 'Repository commit is not a SHA-1.'
    Assert-True ($metadata.CommitDateUtc -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') 'Commit date is not canonical UTC.'
    Assert-True ($metadata.DriverDate -match '^\d{2}/\d{2}/\d{4}$') 'Repository driver date is not canonical.'
    $expectedDriverVer = '{0},{1}' -f $metadata.DriverDate, $metadata.WindowsVersion
    Assert-Equal $expectedDriverVer $metadata.DriverVer 'Repository DriverVer mismatch.'
}

Invoke-TestCase 'Git metadata distinguishes clean, ignored, untracked, and tracked changes' {
    $gitCommand = @(Get-Command git -CommandType Application -ErrorAction Stop) | Select-Object -First 1
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-git-' + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    try {
        $gitOperations = @(
            [pscustomobject]@{ Arguments = @('init') },
            [pscustomobject]@{
                Arguments = @('-c', 'core.autocrlf=false', 'add', 'VERSION', '.gitignore')
            },
            [pscustomobject]@{
                Arguments = @(
                    '-c',
                    'user.name=SBMS Version Test',
                    '-c',
                    'user.email=sbms-version-test@example.invalid',
                    'commit',
                    '-m',
                    'initial'
                )
            }
        )
        [System.IO.File]::WriteAllText(
            (Join-Path $tempRoot 'VERSION'),
            "1.2.3`n",
            (New-Object System.Text.UTF8Encoding($false))
        )
        [System.IO.File]::WriteAllText(
            (Join-Path $tempRoot '.gitignore'),
            "ignored.tmp`n",
            (New-Object System.Text.UTF8Encoding($false))
        )
        foreach ($operation in $gitOperations) {
            $operationArguments = @($operation.Arguments)
            $gitOutput = & $gitCommand.Source -C $tempRoot @operationArguments 2>&1
            $gitExitCode = $LASTEXITCODE
            if ($gitExitCode -ne 0) {
                throw "git $($operationArguments[0]) failed with exit code $gitExitCode`: $($gitOutput -join [Environment]::NewLine)"
            }
        }

        $clean = Get-SBMSGitMetadata -RepositoryRoot $tempRoot
        Assert-Equal $false $clean.IsDirty 'Clean repository was reported dirty.'

        [System.IO.File]::WriteAllText(
            (Join-Path $tempRoot 'ignored.tmp'),
            'ignored',
            (New-Object System.Text.UTF8Encoding($false))
        )
        $ignoredOnly = Get-SBMSGitMetadata -RepositoryRoot $tempRoot
        Assert-Equal $false $ignoredOnly.IsDirty 'Ignored-only repository was reported dirty.'

        $untrackedPath = Join-Path $tempRoot 'untracked.txt'
        [System.IO.File]::WriteAllText(
            $untrackedPath,
            'untracked',
            (New-Object System.Text.UTF8Encoding($false))
        )
        $untracked = Get-SBMSGitMetadata -RepositoryRoot $tempRoot
        Assert-Equal $true $untracked.IsDirty 'Untracked file was not reported dirty.'
        Remove-Item -LiteralPath $untrackedPath -Force

        [System.IO.File]::WriteAllText(
            (Join-Path $tempRoot 'VERSION'),
            "1.2.4`n",
            (New-Object System.Text.UTF8Encoding($false))
        )
        $tracked = Get-SBMSGitMetadata -RepositoryRoot $tempRoot
        Assert-Equal $true $tracked.IsDirty 'Tracked modification was not reported dirty.'
    } finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

Invoke-TestCase 'C# source contains deterministic product constants' {
    $metadata = New-SBMSBuildMetadata `
        -VersionInfo (ConvertFrom-SBMSVersion '1.2.3-rc.4') `
        -Commit '0123456789abcdef0123456789abcdef01234567' `
        -CommitDateUtc '2026-07-25T00:00:00Z'
    $source = New-SBMSCSharpVersionSource -Metadata $metadata -AssemblyTitle 'SBMS GUI' -FileDescription 'SBMS display control'
    Assert-True $source.Contains('[assembly: AssemblyTitle("SBMS GUI")]') 'C# AssemblyTitle missing.'
    Assert-True $source.Contains('[assembly: AssemblyDescription("SBMS display control")]') 'C# AssemblyDescription missing.'
    Assert-True $source.Contains('[assembly: AssemblyProduct("SBMS")]') 'C# AssemblyProduct missing.'
    Assert-True $source.Contains('[assembly: AssemblyVersion("1.2.3.30004")]') 'C# AssemblyVersion missing.'
    Assert-True $source.Contains('[assembly: AssemblyFileVersion("1.2.3.30004")]') 'C# AssemblyFileVersion missing.'
    Assert-True $source.Contains('[assembly: AssemblyInformationalVersion("1.2.3-rc.4")]') 'C# InformationalVersion missing.'
    Assert-True $source.Contains('namespace SBMSBuild') 'C# namespace missing.'
    Assert-True $source.Contains('internal const string SemVer = "1.2.3-rc.4";') 'C# SemVer missing.'
    Assert-True $source.Contains('internal const string WindowsVersion = "1.2.3.30004";') 'C# Windows version missing.'
    Assert-True $source.Contains($metadata.Commit) 'C# commit missing.'
}

Invoke-TestCase 'Generated C# metadata compiles and ProductVersion reads as SemVer' {
    $csc = Join-Path ([System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    }
    Assert-True (Test-Path -LiteralPath $csc -PathType Leaf) 'C# compiler not found.'

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-' + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    try {
        $metadata = New-SBMSBuildMetadata `
            -VersionInfo (ConvertFrom-SBMSVersion '1.2.3-rc.4') `
            -Commit '0123456789abcdef0123456789abcdef01234567' `
            -CommitDateUtc '2026-07-25T00:00:00Z'
        $sourcePath = Join-Path $tempRoot 'ProductVersionInfo.g.cs'
        $outputPath = Join-Path $tempRoot 'VersionProbe.dll'
        $source = New-SBMSCSharpVersionSource `
            -Metadata $metadata `
            -AssemblyTitle 'SBMS Probe' `
            -FileDescription 'SBMS version probe'
        Write-SBMSGeneratedFile -LiteralPath $sourcePath -Content $source | Out-Null

        $compilerArguments = @(
            '/nologo',
            '/target:library',
            "/out:$outputPath",
            $sourcePath
        )
        $compilerOutput = & $csc @compilerArguments 2>&1
        $compilerExitCode = $LASTEXITCODE
        if ($compilerExitCode -ne 0) {
            throw "csc failed with exit code $compilerExitCode`: $($compilerOutput -join [Environment]::NewLine)"
        }

        $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($outputPath)
        Assert-Equal '1.2.3.30004' $fileInfo.FileVersion 'Compiled FileVersion mismatch.'
        Assert-Equal '1.2.3-rc.4' $fileInfo.ProductVersion 'Compiled ProductVersion mismatch.'
        # The CLR maps AssemblyTitle to Win32 FileDescription and
        # AssemblyDescription to Win32 Comments.
        Assert-Equal 'SBMS Probe' $fileInfo.FileDescription 'Compiled FileDescription mismatch.'
        Assert-Equal 'SBMS version probe' $fileInfo.Comments 'Compiled assembly description mismatch.'
        Assert-Equal 'SBMS' $fileInfo.ProductName 'Compiled ProductName mismatch.'
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Invoke-TestCase 'Stable generated C# compiles and preserves FileVersion and ProductVersion' {
    $csc = Join-Path ([System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    }
    Assert-True (Test-Path -LiteralPath $csc -PathType Leaf) 'C# compiler not found.'

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-stable-' + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    try {
        $metadata = New-SBMSBuildMetadata `
            -VersionInfo (ConvertFrom-SBMSVersion '1.2.3') `
            -Commit '0123456789abcdef0123456789abcdef01234567' `
            -CommitDateUtc '2026-07-25T00:00:00Z'
        $sourcePath = Join-Path $tempRoot 'ProductVersionInfo.g.cs'
        $outputPath = Join-Path $tempRoot 'StableVersionProbe.dll'
        $source = New-SBMSCSharpVersionSource `
            -Metadata $metadata `
            -AssemblyTitle 'SBMS Stable Probe' `
            -FileDescription 'SBMS stable version probe'
        Write-SBMSGeneratedFile -LiteralPath $sourcePath -Content $source | Out-Null

        $compilerArguments = @(
            '/nologo',
            '/target:library',
            "/out:$outputPath",
            $sourcePath
        )
        $compilerOutput = & $csc @compilerArguments 2>&1
        $compilerExitCode = $LASTEXITCODE
        if ($compilerExitCode -ne 0) {
            throw "csc failed with exit code $compilerExitCode`: $($compilerOutput -join [Environment]::NewLine)"
        }

        $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($outputPath)
        Assert-Equal '1.2.3.65534' $fileInfo.FileVersion 'Stable compiled FileVersion mismatch.'
        Assert-Equal '1.2.3' $fileInfo.ProductVersion 'Stable compiled ProductVersion mismatch.'
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Invoke-TestCase 'Win32 resource contains numeric and display versions' {
    $metadata = New-SBMSBuildMetadata `
        -VersionInfo (ConvertFrom-SBMSVersion '1.2.3-alpha.4') `
        -Commit '0123456789abcdef0123456789abcdef01234567' `
        -CommitDateUtc '2026-07-25T00:00:00Z'
    $resource = New-SBMSWin32VersionResource -Metadata $metadata -InternalName 'SBMSNative' -OriginalFilename 'SBMSNative.exe'
    Assert-True $resource.Contains('FILEVERSION 1,2,3,10004') 'RC FILEVERSION missing.'
    Assert-True $resource.Contains('PRODUCTVERSION 1,2,3,10004') 'RC PRODUCTVERSION missing.'
    Assert-True $resource.Contains('VALUE "ProductVersion", "1.2.3-alpha.4\0"') 'RC SemVer missing.'
    Assert-True $resource.Contains('VALUE "OriginalFilename", "SBMSNative.exe\0"') 'RC filename missing.'
    Assert-True $resource.Contains('FILETYPE VFT_APP') 'RC application file type missing.'
    $driverResource = New-SBMSWin32VersionResource -Metadata $metadata -InternalName 'IddSampleDriver' -OriginalFilename 'IddSampleDriver.dll' -FileType Dll
    Assert-True $driverResource.Contains('FILETYPE VFT_DLL') 'RC driver DLL file type missing.'
}

Invoke-TestCase 'Application manifest renders assembly version and elevation' {
    $metadata = New-SBMSBuildMetadata `
        -VersionInfo (ConvertFrom-SBMSVersion '3.2.1') `
        -Commit '0123456789abcdef0123456789abcdef01234567' `
        -CommitDateUtc '2026-07-25T00:00:00Z'
    $manifest = New-SBMSApplicationManifest -Metadata $metadata -AssemblyName 'SBMS.Gui' -ExecutionLevel requireAdministrator
    Assert-True $manifest.Contains('assemblyIdentity version="3.2.1.65534" name="SBMS.Gui"') 'Manifest identity mismatch.'
    Assert-True $manifest.Contains('level="requireAdministrator" uiAccess="false"') 'Manifest privilege mismatch.'
    [xml]$xml = $manifest
    Assert-True ($null -ne $xml.DocumentElement) 'Manifest is not valid XML.'
}

Invoke-TestCase 'Release manifest contains reproducible source and package data' {
    $metadata = New-SBMSBuildMetadata `
        -VersionInfo (ConvertFrom-SBMSVersion '4.5.6-dev.7') `
        -Commit '0123456789abcdef0123456789abcdef01234567' `
        -CommitDateUtc '2026-07-25T00:00:00Z' `
        -IsDirty $true
    $data = New-SBMSReleaseManifestData -Metadata $metadata -Artifacts @(
        [pscustomobject][ordered]@{ path = 'SBMS.exe'; sha256 = ('a' * 64) }
    )
    Assert-Equal 2 $data.schemaVersion 'Release schema mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.product.version 'Release version mismatch.'
    Assert-Equal '4.5.6.7' $data.product.windowsVersion 'Release product Windows version mismatch.'
    Assert-Equal 'SBMS.exe' $data.components.gui.artifactName 'GUI artifact name mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.components.gui.productVersion 'GUI product version mismatch.'
    Assert-Equal '4.5.6.7' $data.components.gui.fileVersion 'GUI file version mismatch.'
    Assert-Equal 'SBMSSetup.exe' $data.components.installer.artifactName 'Installer artifact name mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.components.installer.productVersion 'Installer product version mismatch.'
    Assert-Equal '4.5.6.7' $data.components.installer.fileVersion 'Installer file version mismatch.'
    Assert-Equal 'SBMSNative.exe' $data.components.native.artifactName 'Native artifact name mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.components.native.productVersion 'Native product version mismatch.'
    Assert-Equal '4.5.6.7' $data.components.native.fileVersion 'Native file version mismatch.'
    Assert-Equal 'SBMSDeviceHost.exe' $data.components.deviceHost.artifactName 'DeviceHost artifact name mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.components.deviceHost.productVersion 'DeviceHost product version mismatch.'
    Assert-Equal '4.5.6.7' $data.components.deviceHost.fileVersion 'DeviceHost file version mismatch.'
    Assert-Equal 'IddSampleDriver.dll' $data.components.driver.artifactName 'Driver artifact name mismatch.'
    Assert-Equal 'IddSampleDriver.inf' $data.components.driver.infName 'Driver INF name mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.components.driver.productVersion 'Driver product version mismatch.'
    Assert-Equal '4.5.6.7' $data.components.driver.fileVersion 'Driver file version mismatch.'
    Assert-Equal '07/25/2026' $data.components.driver.driverDate 'Driver date mismatch.'
    Assert-Equal '07/25/2026,4.5.6.7' $data.components.driver.driverVer 'Component DriverVer mismatch.'
    Assert-Equal '4.5.6-dev.7' $data.package.version 'Package version mismatch.'
    Assert-Equal 'windows-x64' $data.package.runtimeIdentifier 'Release RID mismatch.'
    Assert-Equal 'x64' $data.package.architecture 'Release architecture mismatch.'
    Assert-Equal 'SBMS-4.5.6-dev.7-windows-x64.zip' $data.package.fileName 'Release package mismatch.'
    Assert-Equal '07/25/2026,4.5.6.7' $data.windows.driverVer 'Release DriverVer mismatch.'
    Assert-Equal 1 $data.artifacts.Count 'Release artifact count mismatch.'

    $json = ConvertTo-SBMSReleaseManifestJson -ManifestData $data
    $roundTrip = $json | ConvertFrom-Json
    Assert-Equal $metadata.Commit $roundTrip.source.commit 'Release JSON commit mismatch.'
    Assert-Equal $true $roundTrip.source.dirty 'Release JSON dirty state mismatch.'
    Assert-Equal '4.5.6-dev.7' $roundTrip.product.version 'Diagnostic product version field missing.'
    Assert-Equal '4.5.6-dev.7' $roundTrip.components.installer.productVersion 'Diagnostic installer version field missing.'
    Assert-Equal '07/25/2026,4.5.6.7' $roundTrip.components.driver.driverVer 'Diagnostic driver version field missing.'
    Assert-Equal '4.5.6-dev.7' $roundTrip.package.version 'Diagnostic package version field missing.'
    Assert-Equal 'SBMS-4.5.6-dev.7-windows-x64.zip' $roundTrip.package.fileName 'Diagnostic package name field missing.'
    Assert-True $json.EndsWith("`n") 'Release JSON lacks final LF.'
    Assert-True (-not $json.Contains("`r")) 'Release JSON contains CR.'
}

Invoke-TestCase 'Current repository satisfies the active version source contract' {
    Assert-SBMSVersionSourceContract -RepositoryRoot $root
}

Invoke-TestCase 'Source contract rejects a temporary hard-coded active GUI version' {
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-contract-' + [guid]::NewGuid().ToString('N'))
    $contractFiles = @(
        'gui\SBMSGui.cs',
        'installer\SBMSSetup.cs',
        'gui\SBMSGui.manifest',
        'installer\SBMSSetup.manifest',
        'Windows-driver-samples\video\IndirectDisplay\IddSampleDriver\IddSampleDriver.inf',
        'build-sbms-driver.ps1'
    )
    try {
        foreach ($relativePath in $contractFiles) {
            $sourcePath = Join-Path $root $relativePath
            $destinationPath = Join-Path $tempRoot $relativePath
            $destinationDirectory = Split-Path -Parent $destinationPath
            [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
            [System.IO.File]::Copy($sourcePath, $destinationPath, $true)
        }

        $guiPath = Join-Path $tempRoot 'gui\SBMSGui.cs'
        $guiSource = [System.IO.File]::ReadAllText($guiPath, [System.Text.Encoding]::UTF8)
        $tampered = $guiSource.Replace(
            'private const string BuildLabel = SBMSBuild.ProductVersionInfo.SemVer;',
            'private const string BuildLabel = "9.9.9-active-drift";'
        )
        Assert-True ($tampered -ne $guiSource) 'GUI contract fixture did not contain the expected generated version reference.'
        [System.IO.File]::WriteAllText($guiPath, $tampered, (New-Object System.Text.UTF8Encoding($false)))

        Assert-Throws {
            Assert-SBMSVersionSourceContract -RepositoryRoot $tempRoot
        } 'Hard-coded active GUI version was accepted.'
    } finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

Invoke-TestCase 'Generated files are UTF-8 without BOM and use LF' {
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sbms-version-' + [guid]::NewGuid().ToString('N'))
    try {
        $path = Join-Path $tempRoot 'nested\generated.txt'
        Write-SBMSGeneratedFile -LiteralPath $path -Content "one`r`ntwo`r`n" | Out-Null
        $bytes = [System.IO.File]::ReadAllBytes($path)
        Assert-True ($bytes.Length -ge 3) 'Generated file is unexpectedly empty.'
        Assert-True (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) 'Generated file has a UTF-8 BOM.'
        Assert-Equal "one`ntwo`n" ([System.IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)) 'Generated newline normalization failed.'
    } finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

Write-Host "RESULT passed=$script:Passed failed=$script:Failed"
if ($script:Failed -ne 0) {
    exit 1
}
