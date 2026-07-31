[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$SigningCertificateThumbprint,

    [string]$InnoCompiler = (
        Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($LiteralPath)
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($stream)
        ).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

$repository = $PSScriptRoot
$release = Join-Path $repository 'target\release'
$driver = Join-Path $repository 'target\driver'
$installer = Join-Path $repository 'target\installer'
$manifest = Join-Path $repository 'installer\SBMS.iss'
$cargoManifest = Join-Path $repository 'Cargo.toml'
$cargoText = [IO.File]::ReadAllText($cargoManifest, [Text.Encoding]::UTF8)
$versionMatch = [regex]::Match(
    $cargoText,
    '(?m)^version\s*=\s*"([^"]+)"'
)
if (-not $versionMatch.Success) {
    throw 'The package version could not be read from Cargo.toml.'
}
$version = $versionMatch.Groups[1].Value
$kitsRoot = (Get-ItemProperty -LiteralPath `
    'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$signTool = Get-ChildItem -LiteralPath (Join-Path $kitsRoot 'bin') -Directory |
    Where-Object { [version]::TryParse($_.Name, [ref]([version]$null)) } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $signTool) {
    throw 'signtool.exe was not found in the Windows SDK.'
}

& cargo.exe build --release --bins
if ($LASTEXITCODE -ne 0) {
    throw "cargo build failed with exit code $LASTEXITCODE"
}

& (Join-Path $repository 'build-driver.ps1') `
    -SigningCertificateThumbprint $SigningCertificateThumbprint
if ($LASTEXITCODE -ne 0) {
    throw "build-driver.ps1 failed with exit code $LASTEXITCODE"
}

$required = @(
    (Join-Path $release 'sbms.exe'),
    (Join-Path $release 'sbms-tray.exe'),
    (Join-Path $driver 'SBMSIndirectDisplay.inf'),
    (Join-Path $driver 'SBMSIndirectDisplay.dll'),
    (Join-Path $driver 'SBMSIndirectDisplay.cat'),
    $manifest,
    (Join-Path $repository 'installer\manage-sunshine-instance.ps1'),
    $InnoCompiler
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required installer input is missing: $path"
    }
}
$infText = [IO.File]::ReadAllText(
    (Join-Path $driver 'SBMSIndirectDisplay.inf'),
    [Text.Encoding]::UTF8
)
if ($infText -notmatch (
    '(?m)^DriverVer=\d{2}/\d{2}/\d{4},' +
    [regex]::Escape("$version.0") +
    '\s*$'
)) {
    throw "Driver version does not match Cargo package version $version."
}

foreach ($binary in @(
    (Join-Path $release 'sbms.exe'),
    (Join-Path $release 'sbms-tray.exe')
)) {
    & $signTool sign /sha1 $SigningCertificateThumbprint /fd SHA256 $binary
    if ($LASTEXITCODE -ne 0) {
        throw "Signing $binary failed with exit code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Path $installer -Force | Out-Null
$package = Join-Path $installer "SBMS-Setup-$version-x64.exe"
if (Test-Path -LiteralPath $package) {
    Remove-Item -LiteralPath $package -Force
}
$signDefinition = '/Ssbmssign=$q{0}$q sign /sha1 {1} /fd SHA256 $f' -f `
    $signTool, $SigningCertificateThumbprint
$versionDefinition = "/DAppVersion=$version"
& $InnoCompiler $signDefinition $versionDefinition $manifest
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $package)) {
    throw "Installer output is missing: $package"
}
foreach ($binary in @(
    (Join-Path $release 'sbms.exe'),
    (Join-Path $release 'sbms-tray.exe'),
    $package
)) {
    & $signTool verify /pa $binary
    if ($LASTEXITCODE -ne 0) {
        throw "Signature verification failed for $binary"
    }
}
$hash = Get-Sha256Hex -LiteralPath $package
$hashFile = "$package.sha256"
$hashLine = "$hash  $([IO.Path]::GetFileName($package))`n"
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($hashFile, $hashLine, $utf8)
Write-Host "installer_package=$package"
Write-Host "installer_sha256=$hash"
Write-Host "installer_sha256_file=$hashFile"
