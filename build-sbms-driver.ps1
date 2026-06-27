param(
    [string] $Configuration = "Release",
    [string] $Platform = "x64"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Solution = Join-Path $Root "Windows-driver-samples\video\IndirectDisplay\IddSampleDriver.sln"

if (-not (Test-Path $Solution)) {
    throw "Solution not found: $Solution"
}

$VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$MSBuild = $null

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
    $MSBuild = $Candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
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

$WdkIncludeRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
if (-not (Test-Path $WdkIncludeRoot)) {
    Write-Host "Windows Driver Kit include directory not found: $WdkIncludeRoot"
    exit 3
}

$StampInf = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter stampinf.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $StampInf) {
    Write-Host "Windows Driver Kit tool stampinf.exe not found. Install WDK, then rerun this script."
    exit 4
}

$SdkVersion = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "um\windows.h") } |
    Sort-Object Name -Descending |
    Select-Object -First 1 -ExpandProperty Name

if (-not $SdkVersion) {
    Write-Host "Windows SDK headers not found under ${env:ProgramFiles(x86)}\Windows Kits\10\Include."
    exit 5
}

Write-Host "Using MSBuild: $MSBuild"
Write-Host "Building: $Solution"
Write-Host "Using Windows SDK: $SdkVersion"
$LocalVCTargetsPath = Join-Path $Root "msbuild-vctargets-v170\"
if (Test-Path $LocalVCTargetsPath) {
    Write-Host "Using local VCTargetsPath overlay: $LocalVCTargetsPath"
    & $MSBuild $Solution /m /restore "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:WindowsTargetPlatformVersion=$SdkVersion" "/p:TargetPlatformVersion=$SdkVersion" "/p:VCTargetsPath=$LocalVCTargetsPath" "/p:SkipPackageVerification=true" "/p:ApiValidator_Enable=false"
} else {
    & $MSBuild $Solution /m /restore "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:WindowsTargetPlatformVersion=$SdkVersion" "/p:TargetPlatformVersion=$SdkVersion" "/p:SkipPackageVerification=true" "/p:ApiValidator_Enable=false"
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Build finished. Driver/app outputs found under:"
Get-ChildItem (Join-Path $Root "Windows-driver-samples\video\IndirectDisplay") -Recurse -Include IddSampleDriver.inf,IddSampleDriver.dll,IddSampleApp.exe,IddSampleDriver.cat |
    Select-Object FullName
