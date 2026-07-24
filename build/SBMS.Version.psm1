Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:InvariantCulture = [System.Globalization.CultureInfo]::InvariantCulture

function ConvertFrom-SBMSVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Version
    )

    # Numeric identifiers are canonical SemVer identifiers: zero, or a
    # non-zero digit followed by at most three further digits.
    $pattern = '\A(?<major>0|[1-9][0-9]{0,3})\.(?<minor>0|[1-9][0-9]{0,3})\.(?<patch>0|[1-9][0-9]{0,3})(?:-(?<label>dev|alpha|beta|rc)\.(?<number>0|[1-9][0-9]{0,3}))?\z'
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Version,
        $pattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
    if (-not $match.Success) {
        throw "Invalid SBMS version '$Version'. Expected M.m.p, or M.m.p-(dev|alpha|beta|rc).N, with canonical numeric components in the range 0..9999."
    }

    $major = [int]::Parse($match.Groups['major'].Value, $script:InvariantCulture)
    $minor = [int]::Parse($match.Groups['minor'].Value, $script:InvariantCulture)
    $patch = [int]::Parse($match.Groups['patch'].Value, $script:InvariantCulture)
    $label = $match.Groups['label'].Value
    $isPrerelease = $match.Groups['label'].Success
    $prereleaseNumber = $null

    if ($isPrerelease) {
        $prereleaseNumber = [int]::Parse($match.Groups['number'].Value, $script:InvariantCulture)
        $revisionBase = switch ($label) {
            'dev'   { 0 }
            'alpha' { 10000 }
            'beta'  { 20000 }
            'rc'    { 30000 }
            default { throw "Unsupported prerelease label '$label'." }
        }
        $revision = $revisionBase + $prereleaseNumber
        $channel = $label
    } else {
        # 65535 is rejected by the CLR AssemblyVersion validator. Keep the
        # highest CLR-compatible UInt16 value as the stable sentinel.
        $revision = 65534
        $channel = 'stable'
    }

    $windowsVersion = '{0}.{1}.{2}.{3}' -f $major, $minor, $patch, $revision
    $packageBaseName = 'SBMS-{0}-windows-x64' -f $Version

    [pscustomobject][ordered]@{
        SemVer             = $Version
        Major              = $major
        Minor              = $minor
        Patch              = $patch
        PrereleaseLabel    = if ($isPrerelease) { $label } else { $null }
        PrereleaseNumber   = $prereleaseNumber
        IsPrerelease       = $isPrerelease
        Channel            = $channel
        WindowsRevision    = $revision
        WindowsVersion     = $windowsVersion
        WindowsVersionCsv  = '{0},{1},{2},{3}' -f $major, $minor, $patch, $revision
        RuntimeIdentifier  = 'windows-x64'
        PackageBaseName    = $packageBaseName
        PackageFileName    = "$packageBaseName.zip"
        TagName            = "v$Version"
    }
}

function Read-SBMSVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    if (-not [System.IO.File]::Exists($LiteralPath)) {
        throw "SBMS VERSION file not found: $LiteralPath"
    }

    $raw = [System.IO.File]::ReadAllText($LiteralPath, [System.Text.Encoding]::UTF8)
    $value = $raw -replace '(?:\r\n|\n|\r)\z', ''
    if ($value -match '[\r\n]') {
        throw "SBMS VERSION file must contain exactly one version line: $LiteralPath"
    }

    ConvertFrom-SBMSVersion -Version $value
}

function ConvertTo-SBMSUtcTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Value
    )

    if ($Value -is [System.DateTimeOffset]) {
        $timestamp = [System.DateTimeOffset]$Value
    } elseif ($Value -is [System.DateTime]) {
        $dateTime = [System.DateTime]$Value
        if ($dateTime.Kind -eq [System.DateTimeKind]::Unspecified) {
            throw 'CommitDateUtc must include an explicit UTC or offset designation.'
        }
        $timestamp = [System.DateTimeOffset]::new($dateTime)
    } else {
        $timestampText = [string]$Value
        if ($timestampText -notmatch '(?:Z|[+-][0-9]{2}:[0-9]{2})\z') {
            throw 'CommitDateUtc must include an explicit UTC or offset designation.'
        }
        $styles = [System.Globalization.DateTimeStyles]::AllowWhiteSpaces -bor
            [System.Globalization.DateTimeStyles]::AssumeUniversal
        $timestamp = [System.DateTimeOffset]::MinValue
        if (-not [System.DateTimeOffset]::TryParse(
            $timestampText,
            $script:InvariantCulture,
            $styles,
            [ref]$timestamp
        )) {
            throw "Invalid commit timestamp '$Value'."
        }
    }

    $timestamp.ToUniversalTime()
}

function Get-SBMSGitMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    if (-not [System.IO.Directory]::Exists($RepositoryRoot)) {
        throw "Repository root not found: $RepositoryRoot"
    }

    $gitCommand = @(Get-Command git -CommandType Application -ErrorAction Stop) |
        Select-Object -First 1
    $commitOutput = & $gitCommand.Source -C $RepositoryRoot rev-parse --verify HEAD 2>&1
    $commitExitCode = $LASTEXITCODE
    if ($commitExitCode -ne 0) {
        throw "git rev-parse failed with exit code $commitExitCode`: $($commitOutput -join [Environment]::NewLine)"
    }
    $commit = ([string]($commitOutput | Select-Object -First 1)).Trim().ToLowerInvariant()
    if ($commit -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw "git returned an invalid commit identifier '$commit'."
    }

    $dateOutput = & $gitCommand.Source -C $RepositoryRoot show -s '--format=%cI' HEAD 2>&1
    $dateExitCode = $LASTEXITCODE
    if ($dateExitCode -ne 0) {
        throw "git show failed with exit code $dateExitCode`: $($dateOutput -join [Environment]::NewLine)"
    }
    $commitDateUtc = ConvertTo-SBMSUtcTimestamp -Value ([string]($dateOutput | Select-Object -First 1))

    $statusOutput = & $gitCommand.Source -C $RepositoryRoot status --porcelain --untracked-files=normal 2>&1
    $statusExitCode = $LASTEXITCODE
    if ($statusExitCode -ne 0) {
        throw "git status failed with exit code $statusExitCode`: $($statusOutput -join [Environment]::NewLine)"
    }
    $isDirty = ($null -ne ($statusOutput | Select-Object -First 1))

    [pscustomobject][ordered]@{
        Commit        = $commit
        ShortCommit   = $commit.Substring(0, 12)
        CommitDateUtc = $commitDateUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $script:InvariantCulture)
        IsDirty       = $isDirty
    }
}

function New-SBMSBuildMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $VersionInfo,

        [Parameter(Mandatory = $true)]
        [string] $Commit,

        [Parameter(Mandatory = $true)]
        [object] $CommitDateUtc,

        [bool] $IsDirty = $false
    )

    $normalizedCommit = $Commit.Trim().ToLowerInvariant()
    if ($normalizedCommit -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw "Invalid Git commit identifier '$Commit'."
    }
    $utc = ConvertTo-SBMSUtcTimestamp -Value $CommitDateUtc
    $utcText = $utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $script:InvariantCulture)
    $driverDate = $utc.ToString('MM/dd/yyyy', $script:InvariantCulture)
    $driverVer = '{0},{1}' -f $driverDate, $VersionInfo.WindowsVersion

    [pscustomobject][ordered]@{
        SchemaVersion      = 1
        Product            = 'SBMS'
        SemVer             = [string]$VersionInfo.SemVer
        Major              = [int]$VersionInfo.Major
        Minor              = [int]$VersionInfo.Minor
        Patch              = [int]$VersionInfo.Patch
        Channel            = [string]$VersionInfo.Channel
        IsPrerelease       = [bool]$VersionInfo.IsPrerelease
        WindowsRevision    = [int]$VersionInfo.WindowsRevision
        WindowsVersion     = [string]$VersionInfo.WindowsVersion
        WindowsVersionCsv  = [string]$VersionInfo.WindowsVersionCsv
        Commit             = $normalizedCommit
        ShortCommit        = $normalizedCommit.Substring(0, 12)
        CommitDateUtc      = $utcText
        IsDirty            = $IsDirty
        DriverDate         = $driverDate
        DriverVer          = $driverVer
        RuntimeIdentifier  = [string]$VersionInfo.RuntimeIdentifier
        PackageBaseName    = [string]$VersionInfo.PackageBaseName
        PackageFileName    = [string]$VersionInfo.PackageFileName
        TagName            = [string]$VersionInfo.TagName
    }
}

function Get-SBMSBuildMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [string] $VersionPath
    )

    if ([string]::IsNullOrWhiteSpace($VersionPath)) {
        $VersionPath = [System.IO.Path]::Combine($RepositoryRoot, 'VERSION')
    }
    $versionInfo = Read-SBMSVersion -LiteralPath $VersionPath
    $git = Get-SBMSGitMetadata -RepositoryRoot $RepositoryRoot
    New-SBMSBuildMetadata `
        -VersionInfo $versionInfo `
        -Commit $git.Commit `
        -CommitDateUtc $git.CommitDateUtc `
        -IsDirty $git.IsDirty
}

function Assert-SBMSVersionSourceContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    $contracts = @(
        @{
            Path = 'gui\SBMSGui.cs'
            Pattern = 'private\s+const\s+string\s+BuildLabel\s*=\s*SBMSBuild\.ProductVersionInfo\.SemVer\s*;'
            Message = 'GUI BuildLabel must derive from generated ProductVersionInfo.SemVer.'
        },
        @{
            Path = 'installer\SBMSSetup.cs'
            Pattern = 'private\s+const\s+string\s+SetupBuildLabel\s*=\s*SBMSBuild\.ProductVersionInfo\.SemVer\s*;'
            Message = 'SetupBuildLabel must derive from generated ProductVersionInfo.SemVer.'
        },
        @{
            Path = 'gui\SBMSGui.manifest'
            Pattern = '<assemblyIdentity\s+version="0\.0\.0\.0"\s+name="SBMS\.Gui"'
            Message = 'GUI source manifest must retain the generated-version placeholder.'
        },
        @{
            Path = 'installer\SBMSSetup.manifest'
            Pattern = '<assemblyIdentity\s+version="0\.0\.0\.0"\s+name="SBMS\.Setup"'
            Message = 'Setup source manifest must retain the generated-version placeholder.'
        },
        @{
            Path = 'Windows-driver-samples\video\IndirectDisplay\IddSampleDriver\SBMSIndirectDisplay.inf'
            Pattern = '(?m)^\s*DriverVer\s*=\s*07/01/2026,0\.0\.0\.0\s*$'
            Message = 'Driver source INF must retain the deterministic DriverVer placeholder.'
        }
    )

    foreach ($contract in $contracts) {
        $path = [System.IO.Path]::Combine($RepositoryRoot, [string]$contract.Path)
        if (-not [System.IO.File]::Exists($path)) {
            throw "Version source contract file is missing: $path"
        }
        $content = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
        if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
            $content,
            [string]$contract.Pattern,
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
        )) {
            throw [string]$contract.Message
        }
    }

    $driverBuildPath = [System.IO.Path]::Combine($RepositoryRoot, 'build-sbms-driver.ps1')
    $driverBuild = [System.IO.File]::ReadAllText($driverBuildPath, [System.Text.Encoding]::UTF8)
    if ($driverBuild -match '(?i)-d\s+\*\s+-v\s+\*') {
        throw 'Driver build must not use clock-derived StampInf wildcard metadata.'
    }
    if ($driverBuild -notmatch '-d\s+\$BuildMetadata\.DriverDate\s+-v\s+\$BuildMetadata\.WindowsVersion') {
        throw 'Driver build must stamp DriverVer from generated VERSION metadata.'
    }
}

function ConvertTo-SBMSCSharpStringLiteral {
    param([Parameter(Mandatory = $true)][string] $Value)
    '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function New-SBMSCSharpVersionSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Metadata,

        [string] $Namespace = 'SBMSBuild',

        [string] $AssemblyTitle = 'SBMS',

        [string] $FileDescription = 'SBMS'
    )

    if ($Namespace -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$') {
        throw "Invalid C# namespace '$Namespace'."
    }
    if ([string]::IsNullOrWhiteSpace($AssemblyTitle)) {
        throw 'AssemblyTitle must not be empty.'
    }
    if ([string]::IsNullOrWhiteSpace($FileDescription)) {
        throw 'FileDescription must not be empty.'
    }

    $semVer = ConvertTo-SBMSCSharpStringLiteral ([string]$Metadata.SemVer)
    $windowsVersion = ConvertTo-SBMSCSharpStringLiteral ([string]$Metadata.WindowsVersion)
    $commit = ConvertTo-SBMSCSharpStringLiteral ([string]$Metadata.Commit)
    $commitDateUtc = ConvertTo-SBMSCSharpStringLiteral ([string]$Metadata.CommitDateUtc)
    $packageName = ConvertTo-SBMSCSharpStringLiteral ([string]$Metadata.PackageFileName)
    $title = ConvertTo-SBMSCSharpStringLiteral $AssemblyTitle
    $description = ConvertTo-SBMSCSharpStringLiteral $FileDescription

    @"
// <auto-generated />
using System.Reflection;

[assembly: AssemblyTitle($title)]
[assembly: AssemblyDescription($description)]
[assembly: AssemblyProduct("SBMS")]
[assembly: AssemblyVersion($windowsVersion)]
[assembly: AssemblyFileVersion($windowsVersion)]
[assembly: AssemblyInformationalVersion($semVer)]

namespace $Namespace
{
    internal static class ProductVersionInfo
    {
        internal const string SemVer = $semVer;
        internal const string WindowsVersion = $windowsVersion;
        internal const string Commit = $commit;
        internal const string CommitDateUtc = $commitDateUtc;
        internal const string PackageName = $packageName;
    }
}
"@ -replace "`r`n", "`n"
}

function New-SBMSWin32VersionResource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Metadata,

        [Parameter(Mandatory = $true)]
        [string] $InternalName,

        [Parameter(Mandatory = $true)]
        [string] $OriginalFilename,

        [string] $FileDescription = 'SBMS',

        [string] $ProductName = 'SBMS',

        [ValidateSet('Application', 'Dll')]
        [string] $FileType = 'Application'
    )

    foreach ($value in @($InternalName, $OriginalFilename, $FileDescription, $ProductName)) {
        if ($value.Contains("`0") -or $value.Contains("`r") -or $value.Contains("`n") -or $value.Contains('"')) {
            throw 'Win32 version resource values must not contain NUL, quotes, or line breaks.'
        }
    }

    $csv = [string]$Metadata.WindowsVersionCsv
    $windows = [string]$Metadata.WindowsVersion
    $semVer = [string]$Metadata.SemVer
    $commit = [string]$Metadata.Commit
    $fileTypeValue = if ($FileType -eq 'Dll') { 'VFT_DLL' } else { 'VFT_APP' }

    @"
#include <windows.h>

1 VERSIONINFO
 FILEVERSION $csv
 PRODUCTVERSION $csv
 FILEFLAGSMASK VS_FFI_FILEFLAGSMASK
 FILEFLAGS 0x0L
 FILEOS VOS_NT_WINDOWS32
 FILETYPE $fileTypeValue
 FILESUBTYPE VFT2_UNKNOWN
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904B0"
        BEGIN
            VALUE "CompanyName", "SBMS\0"
            VALUE "FileDescription", "$FileDescription\0"
            VALUE "FileVersion", "$windows\0"
            VALUE "InternalName", "$InternalName\0"
            VALUE "OriginalFilename", "$OriginalFilename\0"
            VALUE "ProductName", "$ProductName\0"
            VALUE "ProductVersion", "$semVer\0"
            VALUE "BuildCommit", "$commit\0"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x0409, 1200
    END
END
"@ -replace "`r`n", "`n"
}

function New-SBMSApplicationManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Metadata,

        [Parameter(Mandatory = $true)]
        [string] $AssemblyName,

        [ValidateSet('asInvoker', 'highestAvailable', 'requireAdministrator')]
        [string] $ExecutionLevel = 'asInvoker',

        [bool] $UiAccess = $false
    )

    if ($AssemblyName -notmatch '^[A-Za-z0-9_.-]+$') {
        throw "Invalid Win32 assembly name '$AssemblyName'."
    }
    $escapedName = [System.Security.SecurityElement]::Escape($AssemblyName)
    $escapedVersion = [System.Security.SecurityElement]::Escape([string]$Metadata.WindowsVersion)
    $uiAccessText = ([string]$UiAccess).ToLowerInvariant()

    @"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="$escapedVersion" name="$escapedName" type="win32" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="$ExecutionLevel" uiAccess="$uiAccessText" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
"@ -replace "`r`n", "`n"
}

function New-SBMSReleaseManifestData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Metadata,

        [object[]] $Artifacts = @()
    )

    $binaryVersion = [pscustomobject][ordered]@{
        productVersion = [string]$Metadata.SemVer
        fileVersion = [string]$Metadata.WindowsVersion
    }

    [pscustomobject][ordered]@{
        schemaVersion = 2
        product = [pscustomobject][ordered]@{
            name = [string]$Metadata.Product
            version = [string]$Metadata.SemVer
            windowsVersion = [string]$Metadata.WindowsVersion
            channel = [string]$Metadata.Channel
            prerelease = [bool]$Metadata.IsPrerelease
            tag = [string]$Metadata.TagName
        }
        source = [pscustomobject][ordered]@{
            commit = [string]$Metadata.Commit
            shortCommit = [string]$Metadata.ShortCommit
            commitDateUtc = [string]$Metadata.CommitDateUtc
            dirty = [bool]$Metadata.IsDirty
        }
        windows = [pscustomobject][ordered]@{
            fileVersion = [string]$Metadata.WindowsVersion
            driverVer = [string]$Metadata.DriverVer
        }
        components = [pscustomobject][ordered]@{
            gui = [pscustomobject][ordered]@{
                artifactName = 'SBMS.exe'
                productVersion = [string]$binaryVersion.productVersion
                fileVersion = [string]$binaryVersion.fileVersion
            }
            installer = [pscustomobject][ordered]@{
                artifactName = 'SBMSSetup.exe'
                productVersion = [string]$binaryVersion.productVersion
                fileVersion = [string]$binaryVersion.fileVersion
            }
            native = [pscustomobject][ordered]@{
                artifactName = 'SBMSNative.exe'
                productVersion = [string]$binaryVersion.productVersion
                fileVersion = [string]$binaryVersion.fileVersion
            }
            deviceHost = [pscustomobject][ordered]@{
                artifactName = 'SBMSDeviceHost.exe'
                productVersion = [string]$binaryVersion.productVersion
                fileVersion = [string]$binaryVersion.fileVersion
            }
            recoveryBroker = [pscustomobject][ordered]@{
                artifactName = 'SBMSRecoveryBroker.exe'
                productVersion = [string]$binaryVersion.productVersion
                fileVersion = [string]$binaryVersion.fileVersion
            }
            driver = [pscustomobject][ordered]@{
                artifactName = 'SBMSIndirectDisplay.dll'
                infName = 'SBMSIndirectDisplay.inf'
                productVersion = [string]$binaryVersion.productVersion
                fileVersion = [string]$binaryVersion.fileVersion
                driverDate = [string]$Metadata.DriverDate
                driverVer = [string]$Metadata.DriverVer
            }
        }
        package = [pscustomobject][ordered]@{
            version = [string]$Metadata.SemVer
            runtimeIdentifier = [string]$Metadata.RuntimeIdentifier
            architecture = 'x64'
            baseName = [string]$Metadata.PackageBaseName
            fileName = [string]$Metadata.PackageFileName
        }
        artifacts = @($Artifacts)
    }
}

function ConvertTo-SBMSReleaseManifestJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $ManifestData
    )

    (($ManifestData | ConvertTo-Json -Depth 20) -replace "`r`n", "`n") + "`n"
}

function Write-SBMSGeneratedFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Content
    )

    $fullPath = [System.IO.Path]::GetFullPath($LiteralPath)
    $parent = [System.IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "Generated file path must have a parent directory: $LiteralPath"
    }
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllText(
        $fullPath,
        ($Content -replace "`r`n", "`n"),
        (New-Object System.Text.UTF8Encoding($false))
    )
    Get-Item -LiteralPath $fullPath
}

Export-ModuleMember -Function @(
    'ConvertFrom-SBMSVersion',
    'Read-SBMSVersion',
    'Get-SBMSGitMetadata',
    'New-SBMSBuildMetadata',
    'Get-SBMSBuildMetadata',
    'Assert-SBMSVersionSourceContract',
    'New-SBMSCSharpVersionSource',
    'New-SBMSWin32VersionResource',
    'New-SBMSApplicationManifest',
    'New-SBMSReleaseManifestData',
    'ConvertTo-SBMSReleaseManifestJson',
    'Write-SBMSGeneratedFile'
)
