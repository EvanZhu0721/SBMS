[CmdletBinding()]
param(
    [string]$SigningCertificateThumbprint = '590B3FC62702A2056C778C93E4149E7ED0C01FF3',
    [switch]$SkipDriverBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$target = Join-Path $repository 'target\capture-diagnostics'
$manifest = Join-Path $PSScriptRoot 'Cargo.toml'
& cargo.exe build --release --example capture-routing-render-probe --locked -j1 --manifest-path (Join-Path $repository 'Cargo.toml') --target-dir (Join-Path $repository 'target')
if ($LASTEXITCODE -ne 0) { throw 'Production renderer probe build failed.' }
& cargo.exe build --release --locked --manifest-path $manifest --target-dir $target
if ($LASTEXITCODE -ne 0) { throw 'Capture diagnostic build failed.' }
if (-not $SkipDriverBuild) {
    & (Join-Path $repository 'build-driver.ps1') -CaptureDiagnostics -SigningCertificateThumbprint $SigningCertificateThumbprint
    if ($LASTEXITCODE -ne 0) { throw 'Capture diagnostic driver build failed.' }
}
$kitsRoot = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$signTool = Get-ChildItem -LiteralPath (Join-Path $kitsRoot 'bin') -Directory |
    Where-Object { [version]::TryParse($_.Name, [ref]([version]$null)) } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $signTool) { throw 'signtool.exe was not found.' }
$package = Join-Path $repository ('target\SBMS-Capture-Diagnostics-2.2.0-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.Directory]::CreateDirectory($package) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $package 'driver')) | Out-Null
$binary = Join-Path $package 'sbms-capture-diagnostics.exe'
$renderBinary = Join-Path $package 'sbms-capture-render-probe.exe'
Copy-Item -LiteralPath (Join-Path $target 'release\sbms-capture-diagnostics.exe') -Destination $binary
Copy-Item -LiteralPath (Join-Path $repository 'target\release\examples\capture-routing-render-probe.exe') -Destination $renderBinary
foreach ($program in @($binary, $renderBinary)) {
    & $signTool sign /sha1 $SigningCertificateThumbprint /fd SHA256 $program
    if ($LASTEXITCODE -ne 0) { throw ('Diagnostic signing failed: ' + $program) }
}
foreach ($name in @('SBMSCaptureDiagnostics.inf', 'SBMSCaptureDiagnostics.dll', 'SBMSCaptureDiagnostics.cat')) {
    Copy-Item -LiteralPath (Join-Path $repository ('target\driver-capture-diagnostics\' + $name)) -Destination (Join-Path $package 'driver')
}
foreach ($name in @('run.ps1', 'Start-Diagnostics.cmd', 'README.md', 'driver-protocol.md', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $package
}
foreach ($name in @('LICENSE', 'NOTICE.md', 'LICENSES')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $package -Recurse
}
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $SigningCertificateThumbprint)
$signature = Set-AuthenticodeSignature -LiteralPath (Join-Path $package 'run.ps1') -Certificate $certificate -HashAlgorithm SHA256
if ($signature.Status -ne 'Valid') { throw ('Script signing failed: ' + $signature.Status) }
$verified = foreach ($file in @($binary, $renderBinary, (Join-Path $package 'run.ps1'), (Join-Path $package 'driver\SBMSCaptureDiagnostics.dll'), (Join-Path $package 'driver\SBMSCaptureDiagnostics.cat'))) {
    $check = Get-AuthenticodeSignature -LiteralPath $file
    if ($check.Status -ne 'Valid' -or $check.SignerCertificate.Thumbprint -ne $SigningCertificateThumbprint) { throw ('Signature check failed: ' + $file) }
    [pscustomobject]@{ file = $file.Substring($package.Length + 1); bytes = (Get-Item -LiteralPath $file).Length; signature = [string]$check.Status; signer = $check.SignerCertificate.Thumbprint }
}
foreach ($member in @('SBMSCaptureDiagnostics.dll', 'SBMSCaptureDiagnostics.inf')) {
    & $signTool verify /pa /c (Join-Path $package 'driver\SBMSCaptureDiagnostics.cat') (Join-Path $package ('driver\' + $member))
    if ($LASTEXITCODE -ne 0) { throw ('Catalog check failed: ' + $member) }
}
$source = Join-Path $package 'source'
[IO.Directory]::CreateDirectory($source) | Out-Null
$toolSource = Join-Path $source 'tools\capture-diagnostics'
[IO.Directory]::CreateDirectory($toolSource) | Out-Null
foreach ($name in @('Cargo.toml', 'Cargo.lock', 'src', 'build.ps1', 'run.ps1', 'Start-Diagnostics.cmd', 'README.md', 'driver-protocol.md', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $toolSource -Recurse
}
[IO.Directory]::CreateDirectory((Join-Path $source 'driver')) | Out-Null
foreach ($name in @('Driver.cpp', 'CaptureDiagnostics.h', 'SBMSCaptureDiagnostics.inf')) {
    Copy-Item -LiteralPath (Join-Path $repository ('driver\' + $name)) -Destination (Join-Path $source 'driver')
}
foreach ($name in @('Cargo.toml', 'Cargo.lock', 'UPDATE_REVISION', 'build.rs', 'src', 'ui', 'build-driver.ps1', 'LICENSE', 'NOTICE.md', 'LICENSES')) {
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $source -Recurse
}
[IO.Directory]::CreateDirectory((Join-Path $source 'assets')) | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'assets\app.manifest') -Destination (Join-Path $source 'assets')
[IO.Directory]::CreateDirectory((Join-Path $source 'examples')) | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'examples\capture-routing-render-probe.rs') -Destination (Join-Path $source 'examples')
$metadata = [ordered]@{
    version = '2.2.0'; purpose = 'issue50-issue59-capture-render-diagnostics'
    built_at = [DateTimeOffset]::Now.ToString('o'); verified_files = @($verified)
    driver_build = '0x20261105'; installed_or_executed_display_tests = $false
}
[IO.File]::WriteAllText((Join-Path $package 'package.json'), ($metadata | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
Compress-Archive -Path (Join-Path $package '*') -DestinationPath ($package + '.zip')
Write-Output ('package=' + $package)
Write-Output ('archive=' + $package + '.zip')
