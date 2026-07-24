$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$Source = Join-Path $Root "native-output-demo\SBMSNative.cpp"
$Out = Join-Path $Root "SBMSNative.exe"
$VsDevCmd = "C:\BuildTools\Common7\Tools\VsDevCmd.bat"
$GeneratedRoot = Join-Path $Root "obj\version\native"
$VersionResource = Join-Path $GeneratedRoot "SBMSNative.version.rc"
$VersionResourceBinary = Join-Path $GeneratedRoot "SBMSNative.version.res"
Write-SBMSGeneratedFile -LiteralPath $VersionResource -Content (
    New-SBMSWin32VersionResource -Metadata $BuildMetadata -InternalName "SBMSNative" -OriginalFilename "SBMSNative.exe" -FileDescription "SBMS native display output"
) | Out-Null

if (-not (Test-Path $Source)) {
    throw "Missing source: $Source"
}

if (-not (Test-Path $VsDevCmd)) {
    throw "Missing VsDevCmd: $VsDevCmd"
}

$Command = "`"$VsDevCmd`" -arch=x64 -host_arch=x64 >nul && rc /nologo /fo`"$VersionResourceBinary`" `"$VersionResource`" && cl /nologo /std:c++17 /EHsc /O2 /MD /W4 /DUNICODE /D_UNICODE `"$Source`" `"$VersionResourceBinary`" /Fe:`"$Out`" d3d11.lib dxgi.lib d3dcompiler.lib user32.lib gdi32.lib setupapi.lib advapi32.lib bcrypt.lib"
cmd.exe /d /c $Command

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $Out"
$versionInfo = (Get-Item -LiteralPath $Out).VersionInfo
if ([string]$versionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
    throw "Native version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
