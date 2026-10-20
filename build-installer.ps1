[CmdletBinding(DefaultParameterSetName = 'Signed')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Signed')]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$SigningCertificateThumbprint,

    [Parameter(Mandatory = $true, ParameterSetName = 'Unsigned')]
    [switch]$Unsigned,

    [ValidatePattern('^[-a-zA-Z0-9.]*$')]
    [string]$OutputSuffix = '',

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
& (Join-Path $repository 'installer\generate-ui-assets.ps1')
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
$signedBuild = $PSCmdlet.ParameterSetName -eq 'Signed'
if (-not $signedBuild -and -not $Unsigned) {
    throw 'Unsigned packaging must be explicitly enabled with -Unsigned.'
}
$signTool = $null
if ($signedBuild) {
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
}

& cargo.exe build --release --bins --locked -j 1 --manifest-path $cargoManifest
if ($LASTEXITCODE -ne 0) {
    throw "cargo build failed with exit code $LASTEXITCODE"
}

$driverArguments = @{}
if ($signedBuild) {
    $driverArguments.SigningCertificateThumbprint =
        $SigningCertificateThumbprint
}
& (Join-Path $repository 'build-driver.ps1') @driverArguments
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

if ($signedBuild) {
    foreach ($binary in @(
        (Join-Path $release 'sbms.exe'),
        (Join-Path $release 'sbms-tray.exe')
    )) {
        & $signTool sign /sha1 $SigningCertificateThumbprint /fd SHA256 $binary
        if ($LASTEXITCODE -ne 0) {
            throw "Signing $binary failed with exit code $LASTEXITCODE"
        }
    }
}

New-Item -ItemType Directory -Path $installer -Force | Out-Null
$packageSuffix = if ($signedBuild) { $OutputSuffix } else { '-unsigned' + $OutputSuffix }
$package = Join-Path $installer "SBMS-Setup-$version-x64$packageSuffix.exe"
if (Test-Path -LiteralPath $package) {
    Remove-Item -LiteralPath $package -Force
}
$versionDefinition = "/DAppVersion=$version"
$compilerArguments = @($versionDefinition)
$compilerArguments += "/DOutputSuffix=$packageSuffix"
if ($signedBuild) {
    $driverSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $driver 'SBMSIndirectDisplay.cat')
    $signer = $driverSignature.SignerCertificate
    if ($signer.Subject -eq $signer.Issuer) {
        # Ship only the public certificate; Setup requires explicit trust consent.
        [IO.File]::WriteAllBytes(
            (Join-Path $driver 'SBMS.cer'),
            $signer.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $compilerArguments += "/DLocalCertificateThumbprint=$($signer.Thumbprint)"
    }
    $compilerArguments += (
        '/Ssbmssign=$q{0}$q sign /sha1 {1} /fd SHA256 $f' -f
            $signTool, $SigningCertificateThumbprint
    )
}
else {
    $compilerArguments += '/DUnsignedBuild=1'
}
$compilerArguments += $manifest
& $InnoCompiler @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $package)) {
    throw "Installer output is missing: $package"
}
if ($signedBuild) {
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
}
Write-Host "installer_package=$package"
Write-Host "installer_signed=$signedBuild"
