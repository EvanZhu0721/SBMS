param(
    [switch] $Live
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $csc) {
    throw 'Missing .NET Framework csc.exe.'
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'SBMS-installer-audit-' + [guid]::NewGuid().ToString('N')
)
$output = Join-Path $testRoot 'WindowsInstallerInventoryTests.exe'
$fixture = Join-Path $root 'tests\fixtures\pnputil-enum-drivers.xml'
$deviceFixture = Join-Path $root 'tests\fixtures\pnputil-enum-devices.xml'
$validSignatureFixture = Join-Path $root 'tests\fixtures\driver-signature-evidence-valid.txt'
$invalidSignatureFixture = Join-Path $root 'tests\fixtures\driver-signature-evidence-invalid.txt'
$missingEkuCertificateFixture = Join-Path $root 'tests\fixtures\timestamp-certificate-missing-eku.txt'
$timestampPfxFixture = Join-Path $root 'tests\fixtures\timestamp-certificate-valid.pfx.txt'
[void](New-Item -ItemType Directory -Path $testRoot)

try {
    $sourcePaths = @(
        (Join-Path $root 'installer\WindowsInstallerInventory.cs'),
        (Join-Path $root 'installer\DriverCatalogVerifier.cs'),
        (Join-Path $root 'installer\InstallerOwnership.cs'),
        (Join-Path $root 'installer\InstallerAuditOnly.cs'),
        (Join-Path $root 'tests\WindowsInstallerInventoryTests.cs')
    )
    foreach ($sourcePath in $sourcePaths) {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Missing installer audit source: $sourcePath"
        }
    }
    if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) {
        throw "Missing PnPUtil XML fixture: $fixture"
    }
    if (-not (Test-Path -LiteralPath $deviceFixture -PathType Leaf)) {
        throw "Missing PnPUtil device XML fixture: $deviceFixture"
    }
    if (-not (Test-Path -LiteralPath $validSignatureFixture -PathType Leaf) -or
        -not (Test-Path -LiteralPath $invalidSignatureFixture -PathType Leaf)) {
        throw 'Missing driver signature evidence fixture.'
    }
    if (-not (Test-Path -LiteralPath $missingEkuCertificateFixture -PathType Leaf)) {
        throw 'Missing timestamp certificate policy fixture.'
    }
    if (-not (Test-Path -LiteralPath $timestampPfxFixture -PathType Leaf)) {
        throw 'Missing valid timestamp certificate fixture.'
    }

    $compilerArgs = @(
        '/nologo',
        '/target:exe',
        '/platform:x64',
        '/optimize+',
        "/out:$output",
        '/reference:System.Xml.dll',
        '/reference:System.Security.dll'
    ) + $sourcePaths
    & $csc @compilerArgs
    $compileExitCode = $LASTEXITCODE
    if ($compileExitCode -ne 0) {
        throw "Installer audit test compilation failed with exit code $compileExitCode."
    }

    $testArguments = @(
        $fixture,
        $deviceFixture,
        $validSignatureFixture,
        $invalidSignatureFixture,
        $missingEkuCertificateFixture,
        $timestampPfxFixture
    )
    if ($Live) {
        $testArguments += '--live'
    }
    & $output @testArguments
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "Installer audit tests failed with exit code $testExitCode."
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$global:LASTEXITCODE = 0
