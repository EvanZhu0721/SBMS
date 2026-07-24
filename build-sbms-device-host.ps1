$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$Source = Join-Path $Root "device-host\SBMSDeviceHost.cpp"
$Out = Join-Path $Root "SBMSDeviceHost.exe"
$VsDevCmd = "C:\BuildTools\Common7\Tools\VsDevCmd.bat"
$GeneratedRoot = Join-Path $Root "obj\version\device-host"
$VersionResource = Join-Path $GeneratedRoot "SBMSDeviceHost.version.rc"
$VersionResourceBinary = Join-Path $GeneratedRoot "SBMSDeviceHost.version.res"
Write-SBMSGeneratedFile -LiteralPath $VersionResource -Content (
    New-SBMSWin32VersionResource -Metadata $BuildMetadata -InternalName "SBMSDeviceHost" -OriginalFilename "SBMSDeviceHost.exe" -FileDescription "SBMS indirect display device host"
) | Out-Null

if (-not (Test-Path $Source)) {
    throw "Missing source: $Source"
}
if (-not (Test-Path $VsDevCmd)) {
    throw "Missing VsDevCmd: $VsDevCmd"
}

$Command = "`"$VsDevCmd`" -arch=x64 -host_arch=x64 >nul && rc /nologo /fo`"$VersionResourceBinary`" `"$VersionResource`" && cl /nologo /std:c++17 /EHsc /O2 /MD /W4 /DUNICODE /D_UNICODE `"$Source`" `"$VersionResourceBinary`" /Fe:`"$Out`" swdevice.lib user32.lib"
cmd.exe /d /c $Command

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $Out"
$versionInfo = (Get-Item -LiteralPath $Out).VersionInfo
if ([string]$versionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
    throw "DeviceHost version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
