$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Source = Join-Path $Root "native-output-demo\SBMSNative.cpp"
$Out = Join-Path $Root "SBMSNative.exe"
$VsDevCmd = "C:\BuildTools\Common7\Tools\VsDevCmd.bat"

if (-not (Test-Path $Source)) {
    throw "Missing source: $Source"
}

if (-not (Test-Path $VsDevCmd)) {
    throw "Missing VsDevCmd: $VsDevCmd"
}

$Command = "`"$VsDevCmd`" -arch=x64 -host_arch=x64 >nul && cl /nologo /std:c++17 /EHsc /O2 /MD /W4 /DUNICODE /D_UNICODE `"$Source`" /Fe:`"$Out`" d3d11.lib dxgi.lib d3dcompiler.lib user32.lib gdi32.lib setupapi.lib advapi32.lib bcrypt.lib"
cmd.exe /d /c $Command

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $Out"
