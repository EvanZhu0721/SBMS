#requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$Source = $PSScriptRoot
$Destination = Join-Path $env:ProgramFiles "SBMS"

$required = @("SBMS.exe", "SBMSNative.exe", "SBMSDeviceHost.exe", "SBMSRecoveryBroker.exe", "driver")
foreach ($item in $required) {
    $path = Join-Path $Source $item
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing release item: $path"
    }
}

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

Get-ChildItem -LiteralPath $Source -Force |
    Where-Object { $_.Name -ne ".git" } |
    Copy-Item -Destination $Destination -Recurse -Force

Write-Host "Installed SBMS to $Destination"
