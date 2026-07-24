param(
    [string] $Configuration = "Release",
    [string] $Platform = "x64",
    [string] $TestCertificateThumbprint,
    [switch] $Production,
    [string] $SigningPolicyPath,
    [string] $SignToolPath
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
if ($Production -and -not [string]::IsNullOrWhiteSpace($TestCertificateThumbprint)) {
    throw 'Production signing and test signing are mutually exclusive.'
}
$ProductionSigningPolicy = $null
if ($Production) {
    if ([string]::IsNullOrWhiteSpace($SigningPolicyPath)) {
        throw 'Production driver build requires -SigningPolicyPath.'
    }
    Import-Module (Join-Path $Root 'build\SBMS.Signing.psm1') -Force
    $ProductionSigningPolicy = Read-SBMSSigningPolicy -LiteralPath $SigningPolicyPath
    $null = Resolve-SBMSSigningCertificate -Policy $ProductionSigningPolicy
}
$Solution = Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\IddSampleDriver.sln"
$DriverProjectDir = Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\IddSampleDriver"
$DriverOutputRoot = Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\$Platform\$Configuration"
$DriverPackageDir = Join-Path $DriverOutputRoot "IddSampleDriver"
$GeneratedRoot = Join-Path $Root "obj\version\driver"
$VersionResource = Join-Path $GeneratedRoot "IddSampleDriver.version.rc"
Write-SBMSGeneratedFile -LiteralPath $VersionResource -Content (
    New-SBMSWin32VersionResource -Metadata $BuildMetadata -InternalName "IddSampleDriver" -OriginalFilename "IddSampleDriver.dll" -FileDescription "SBMS indirect display driver" -FileType Dll
) | Out-Null

if (-not (Test-Path $Solution)) {
    throw "Solution not found: $Solution"
}

function Find-FirstExistingPath {
    param([string[]] $Paths)
    $Paths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

function Find-WdkTool {
    param(
        [string] $Name,
        [string[]] $PreferredArchitectures
    )

    foreach ($arch in $PreferredArchitectures) {
        $candidate = Join-Path $WdkBinRoot "$SdkVersion\$arch\$Name"
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $tool = Get-ChildItem $WdkBinRoot -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($tool) {
        return $tool.FullName
    }

    return $null
}

function Get-LatestDirectoryName {
    param([string] $Path)
    Get-ChildItem $Path -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
}

function Reset-ChildDirectory {
    param(
        [string] $Parent,
        [string] $Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\')
    $childFull = [System.IO.Path]::GetFullPath((Join-Path $Parent $Child))
    if (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset unexpected path: $childFull"
    }

    if (Test-Path -LiteralPath $childFull) {
        Remove-Item -LiteralPath $childFull -Recurse -Force
    }

    New-Item -ItemType Directory -Path $childFull -Force | Out-Null
    return $childFull
}

function New-DriverPackage {
    param(
        [string] $WdfVersion
    )

    $dll = Join-Path $DriverOutputRoot "IddSampleDriver.dll"
    $infSource = Join-Path $DriverProjectDir "IddSampleDriver.inf"
    if (-not (Test-Path $dll)) {
        throw "Built driver DLL not found: $dll"
    }
    if (-not (Test-Path $infSource)) {
        throw "Driver INF not found: $infSource"
    }

    Reset-ChildDirectory -Parent $DriverOutputRoot -Child "IddSampleDriver" | Out-Null
    $packageDll = Join-Path $DriverPackageDir "IddSampleDriver.dll"
    Copy-Item -LiteralPath $dll -Destination $packageDll -Force
    Copy-Item -LiteralPath $infSource -Destination (Join-Path $DriverPackageDir "IddSampleDriver.inf") -Force

    $signingCert = $null
    if (-not [string]::IsNullOrWhiteSpace($TestCertificateThumbprint)) {
        $normalizedThumbprint = ($TestCertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        if ($normalizedThumbprint.Length -ne 40) {
            throw 'TestCertificateThumbprint must contain exactly 40 hexadecimal characters.'
        }
        $signingCert = @(
            Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
                Where-Object {
                    ([string]$_.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() -ceq $normalizedThumbprint
                }
        )
        if ($signingCert.Count -ne 1) {
            throw "Expected exactly one explicit test-signing certificate '$normalizedThumbprint'; found $($signingCert.Count)."
        }
        $signingCert = $signingCert[0]
    }

    if ($Production) {
        $null = Invoke-SBMSSignAuthenticode `
            -LiteralPath $packageDll `
            -Policy $ProductionSigningPolicy `
            -SignToolPath $SignToolPath
        $null = Assert-SBMSAuthenticodeSignature `
            -LiteralPath $packageDll `
            -Policy $ProductionSigningPolicy `
            -SignToolPath $SignToolPath
    } elseif ($signingCert) {
        & $SignTool sign /v /fd SHA256 /sha1 $signingCert.Thumbprint $packageDll
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for driver DLL with exit code $LASTEXITCODE"
        }
        $dllSignature = Get-AuthenticodeSignature $packageDll
        if ($dllSignature.Status -ne "Valid") {
            throw "Driver DLL signature is not valid after signing: $($dllSignature.StatusMessage)"
        }
    }

    $infArch = switch ($Platform.ToLowerInvariant()) {
        "x64" { "amd64" }
        "win32" { "x86" }
        "arm64" { "arm64" }
        default { throw "Unsupported driver package platform: $Platform" }
    }

    $umdfVersion = if ($WdfVersion -match '^\d+\.\d+$') { "$WdfVersion.0" } else { $WdfVersion }
    $packageInf = Join-Path $DriverPackageDir "IddSampleDriver.inf"
    & $StampInf -f $packageInf -a $infArch -d $BuildMetadata.DriverDate -v $BuildMetadata.WindowsVersion -u $umdfVersion
    if ($LASTEXITCODE -ne 0) {
        throw "stampinf failed with exit code $LASTEXITCODE"
    }
    $driverVerLine = Select-String -LiteralPath $packageInf -Pattern '^\s*DriverVer\s*=\s*(.+)\s*$' |
        Select-Object -First 1
    if (-not $driverVerLine -or [string]$driverVerLine.Matches[0].Groups[1].Value.Trim() -ne [string]$BuildMetadata.DriverVer) {
        throw "DriverVer metadata mismatch. Expected '$($BuildMetadata.DriverVer)'."
    }

    $osList = switch ($Platform.ToLowerInvariant()) {
        "x64" { "10_CO_X64,10_NI_X64,10_GE_X64" }
        "arm64" { "10_CO_ARM64,10_NI_ARM64,10_GE_ARM64" }
        default { "10_X86" }
    }
    & $Inf2Cat "/driver:$DriverPackageDir" "/os:$osList" "/uselocaltime"
    if ($LASTEXITCODE -ne 0) {
        throw "inf2cat failed with exit code $LASTEXITCODE"
    }

    $cat = Get-ChildItem -LiteralPath $DriverPackageDir -Filter "*.cat" -File | Select-Object -First 1
    if (-not $cat) {
        throw "Catalog was not generated in $DriverPackageDir"
    }

    if ($Production) {
        $null = Invoke-SBMSSignAuthenticode `
            -LiteralPath $cat.FullName `
            -Policy $ProductionSigningPolicy `
            -SignToolPath $SignToolPath
        $null = Assert-SBMSAuthenticodeSignature `
            -LiteralPath $cat.FullName `
            -Policy $ProductionSigningPolicy `
            -SignToolPath $SignToolPath
    } elseif ($signingCert) {
        & $SignTool sign /v /fd SHA256 /sha1 $signingCert.Thumbprint $cat.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed with exit code $LASTEXITCODE"
        }
        Export-Certificate -Cert $signingCert -FilePath (Join-Path $DriverOutputRoot "IddSampleDriver.cer") -Force | Out-Null

        $signature = Get-AuthenticodeSignature $cat.FullName
        if ($signature.Status -ne "Valid") {
            throw "Driver catalog signature is not valid: $($signature.StatusMessage)"
        }
    } else {
        Write-Warning "Driver package was generated unsigned. Pass -TestCertificateThumbprint explicitly only for isolated development."
    }
}

$VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$MSBuild = $null
$InstallPath = $null

if (Test-Path $VsWhere) {
    $InstallPath = & $VsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($InstallPath) {
        $Candidate = Join-Path $InstallPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $Candidate) {
            $MSBuild = $Candidate
        }
    }
}

if (-not $MSBuild) {
    $Candidates = @(
        "C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    $MSBuild = Find-FirstExistingPath -Paths $Candidates
}

if (-not $MSBuild) {
    $Command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($Command) {
        $MSBuild = $Command.Source
    } else {
        Write-Host "MSBuild not found. Install Visual Studio 2022 with C++ workload and Windows Driver Kit, then rerun this script."
        exit 2
    }
}

$WdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10"
$WdkIncludeRoot = Join-Path $WdkRoot "Include"
$WdkBinRoot = Join-Path $WdkRoot "bin"
if (-not (Test-Path $WdkIncludeRoot)) {
    Write-Host "Windows Driver Kit include directory not found: $WdkIncludeRoot"
    exit 3
}

$SdkVersion = Get-ChildItem $WdkIncludeRoot -Directory |
    Where-Object {
        $candidateVersion = $_.Name
        (Test-Path (Join-Path $_.FullName "um\windows.h")) -and
        (Test-Path (Join-Path $WdkRoot "Lib\$candidateVersion")) -and
        (Test-Path (Join-Path $WdkRoot "build\$candidateVersion\WindowsDriver.Common.targets")) -and
        (Test-Path (Join-Path $WdkRoot "build\$candidateVersion\WindowsDriver.Default.props")) -and
        (Test-Path (Join-Path $WdkRoot "build\$candidateVersion\$Platform\WindowsUserModeDriver\WDK.$Platform.WindowsUserModeDriver.props")) -and
        (Test-Path (Join-Path $WdkRoot "build\$candidateVersion\$Platform\WindowsApplicationForDrivers\WDK.$Platform.WindowsApplicationForDrivers.props"))
    } |
    Sort-Object Name -Descending |
    Select-Object -First 1 -ExpandProperty Name

if (-not $SdkVersion) {
    Write-Host "Windows SDK headers/libs not found under $WdkRoot."
    exit 5
}

$StampInf = Find-WdkTool -Name "stampinf.exe" -PreferredArchitectures @("x64", "x86")
$Inf2Cat = Find-WdkTool -Name "Inf2Cat.exe" -PreferredArchitectures @("x64", "x86")
$SignTool = Find-WdkTool -Name "signtool.exe" -PreferredArchitectures @("x64", "x86")
$TraceWpp = Find-WdkTool -Name "tracewpp.exe" -PreferredArchitectures @("x64", "x86")
foreach ($requiredTool in @("StampInf", "Inf2Cat", "SignTool", "TraceWpp")) {
    if (-not (Get-Variable $requiredTool -ValueOnly)) {
        Write-Host "Windows Driver Kit tool $requiredTool not found. Install WDK, then rerun this script."
        exit 4
    }
}

$preferredWdfVersion = "2.25"
$wdfIncludeRoot = Join-Path $WdkIncludeRoot "wdf\umdf"
$WdfVersion = if (Test-Path (Join-Path $wdfIncludeRoot $preferredWdfVersion)) {
    $preferredWdfVersion
} else {
    Get-LatestDirectoryName -Path $wdfIncludeRoot
}
if (-not $WdfVersion) {
    throw "UMDF headers not found under $wdfIncludeRoot"
}

$VCTargetsCandidates = @()
if ($InstallPath) {
    $VCTargetsCandidates += Join-Path $InstallPath "MSBuild\Microsoft\VC\v170"
}
$MSBuildRoot = Split-Path (Split-Path (Split-Path $MSBuild -Parent) -Parent) -Parent
$VCTargetsCandidates += Join-Path $MSBuildRoot "Microsoft\VC\v170"
$VCTargetsRoot = Find-FirstExistingPath -Paths $VCTargetsCandidates

$LocalVCTargetsPath = Join-Path $Root "msbuild-vctargets-v170"
$LocalPlatformPath = Join-Path $LocalVCTargetsPath "Platforms\$Platform"
$hasLocalWdkToolset = $false
if ((Test-Path $LocalVCTargetsPath) -and (Test-Path $LocalPlatformPath)) {
    $localUserModeToolset = Join-Path $LocalPlatformPath "PlatformToolsets\WindowsUserModeDriver10.0"
    $localAppDriverToolset = Join-Path $LocalPlatformPath "PlatformToolsets\WindowsApplicationForDrivers10.0"
    $hasLocalWdkToolset =
        (Test-Path (Join-Path $localUserModeToolset "Toolset.props")) -and
        (Test-Path (Join-Path $localUserModeToolset "Toolset.targets")) -and
        (Test-Path (Join-Path $localAppDriverToolset "Toolset.props")) -and
        (Test-Path (Join-Path $localAppDriverToolset "Toolset.targets"))
}
$hasIntegratedWdkToolset = $false
if ($VCTargetsRoot) {
    $userModeToolset = Join-Path $VCTargetsRoot "Platforms\$Platform\PlatformToolsets\WindowsUserModeDriver10.0"
    $appDriverToolset = Join-Path $VCTargetsRoot "Platforms\$Platform\PlatformToolsets\WindowsApplicationForDrivers10.0"
    $hasIntegratedWdkToolset = (Test-Path $userModeToolset) -and (Test-Path $appDriverToolset)
}
$canStageLocalWdkToolset = $hasLocalWdkToolset -and (Test-Path $VCTargetsRoot)
$missingWdkToolset = -not ($hasIntegratedWdkToolset -or $canStageLocalWdkToolset)

Write-Host "Using MSBuild: $MSBuild"
Write-Host "Building: $Solution"
Write-Host "Using Windows SDK: $SdkVersion"
Write-Host "Using UMDF: $WdfVersion"
if ($missingWdkToolset) {
    throw "WDK Visual Studio platform toolsets are missing for '$Platform'. Install the WDK VS integration or keep the minimal msbuild-vctargets-v170\Platforms\$Platform overlay in this source tree. SBMS intentionally does not fall back to a prebuilt driver DLL."
}
if ($canStageLocalWdkToolset -and -not $hasIntegratedWdkToolset) {
    Write-Host "Using installed Visual C++ targets with the local WDK PlatformToolsets overlay."
}

$BuildArgs = @(
    $Solution,
    "/m",
    "/t:Build",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:WindowsTargetPlatformVersion=$SdkVersion",
    "/p:TargetPlatformVersion=$SdkVersion",
    "/p:WDKBuildFolder=$SdkVersion",
    "/p:SkipPackageVerification=true",
    "/p:ApiValidator_Enable=false",
    "/p:RunCodeAnalysis=false",
    "/p:EnablePREfast=false",
    "/p:SBMSVersionResource=$VersionResource",
    "/p:SBMSDriverDate=$($BuildMetadata.DriverDate)",
    "/p:SBMSDriverVersion=$($BuildMetadata.WindowsVersion)",
    "/p:Inf2CatUseLocalTime=true",
    "/nr:false"
)

$StagedVCTargetsPath = $null
$PreviousVCTargetsPath = [System.Environment]::GetEnvironmentVariable(
    'VCTargetsPath',
    [System.EnvironmentVariableTarget]::Process
)
$PreviousWdkContentRoot = [System.Environment]::GetEnvironmentVariable(
    'WDKContentRoot',
    [System.EnvironmentVariableTarget]::Process
)
try {
    [System.Environment]::SetEnvironmentVariable(
        'WDKContentRoot',
        ($WdkRoot.TrimEnd('\') + '\'),
        [System.EnvironmentVariableTarget]::Process
    )
    if ($canStageLocalWdkToolset -and -not $hasIntegratedWdkToolset) {
        # Issue #9: VCTargetsPath must remain a complete, version-matched Visual
        # C++ tree. Stage the installed tree and overlay only the two WDK
        # PlatformToolsets instead of vendoring Visual Studio task assemblies.
        $StagedVCTargetsPath = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-VCTargets-" + [guid]::NewGuid().ToString("N"))
        Copy-Item -LiteralPath $VCTargetsRoot -Destination $StagedVCTargetsPath -Recurse -Force
        $stagedToolsetsPath = Join-Path $StagedVCTargetsPath "Platforms\$Platform\PlatformToolsets"
        foreach ($toolsetPath in @($localUserModeToolset, $localAppDriverToolset)) {
            Copy-Item -LiteralPath $toolsetPath -Destination $stagedToolsetsPath -Recurse -Force
        }
        # MSBuild requires VCTargetsPath to end in a separator, but Windows
        # PowerShell 5.1 can escape the closing quote of a native argument that
        # ends in a backslash. A process-scoped MSBuild property avoids that
        # lossy native-command boundary.
        [System.Environment]::SetEnvironmentVariable(
            'VCTargetsPath',
            ($StagedVCTargetsPath.TrimEnd('\') + '\'),
            [System.EnvironmentVariableTarget]::Process
        )
    }

    $buildStarted = Get-Date
    & $MSBuild @BuildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE."
    }

    New-DriverPackage -WdfVersion $WdfVersion
    $driverVersionInfo = (Get-Item -LiteralPath (Join-Path $DriverPackageDir "IddSampleDriver.dll")).VersionInfo
    if ([string]$driverVersionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
        [string]$driverVersionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
        throw "Driver DLL version metadata mismatch. FileVersion=$($driverVersionInfo.FileVersion) ProductVersion=$($driverVersionInfo.ProductVersion)"
    }
} finally {
    [System.Environment]::SetEnvironmentVariable(
        'VCTargetsPath',
        $PreviousVCTargetsPath,
        [System.EnvironmentVariableTarget]::Process
    )
    [System.Environment]::SetEnvironmentVariable(
        'WDKContentRoot',
        $PreviousWdkContentRoot,
        [System.EnvironmentVariableTarget]::Process
    )
    if ($StagedVCTargetsPath -and (Test-Path $StagedVCTargetsPath)) {
        Remove-Item -LiteralPath $StagedVCTargetsPath -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $StagedVCTargetsPath) {
            Write-Warning "Temporary Visual C++ targets could not be removed: $StagedVCTargetsPath"
        }
    }
}

Write-Host ""
Write-Host "Build finished. Driver package found under:"
Get-ChildItem -LiteralPath $DriverPackageDir -Force | Select-Object FullName,Length,LastWriteTime
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.DriverVer))"
