$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Source = Join-Path $Root "device-host\SBMSDeviceHost.cpp"
$Out = Join-Path $Root "SBMSDeviceHost.exe"
$VsDevCmd = "C:\BuildTools\Common7\Tools\VsDevCmd.bat"

if (-not (Test-Path $Source)) {
    throw "Missing source: $Source"
}
if (-not (Test-Path $VsDevCmd)) {
    throw "Missing VsDevCmd: $VsDevCmd"
}

$Command = "`"$VsDevCmd`" -arch=x64 -host_arch=x64 >nul && cl /nologo /std:c++17 /EHsc /O2 /MD /W4 /DUNICODE /D_UNICODE `"$Source`" /Fe:`"$Out`" swdevice.lib user32.lib"
cmd.exe /d /c $Command

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $Out"
