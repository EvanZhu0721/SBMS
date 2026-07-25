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
    'SBMS-protected-payload-transaction-executor-' +
    [guid]::NewGuid().ToString('N')
)
$output = Join-Path $testRoot 'ProtectedPayloadTransactionExecutorTests.exe'
[void](New-Item -ItemType Directory -Path $testRoot)

try {
    $sourcePaths = @(
        (Join-Path $root 'installer\InstallerTransactionModels.cs'),
        (Join-Path $root 'installer\ProtectedPayloadStoreContracts.cs'),
        (Join-Path $root 'installer\ProtectedPayloadRecoveryPlanner.cs'),
        (Join-Path $root 'installer\ProtectedPayloadTransactionExecutor.cs'),
        (Join-Path $root 'tests\ProtectedPayloadTransactionExecutorTests.cs')
    )
    foreach ($sourcePath in $sourcePaths) {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Missing protected payload transaction executor source: $sourcePath"
        }
    }

    $compilerArgs = @(
        '/nologo',
        '/target:exe',
        '/platform:x64',
        '/optimize+',
        "/out:$output",
        '/reference:System.Runtime.Serialization.dll',
        '/reference:System.Security.dll'
    ) + $sourcePaths
    & $csc @compilerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Protected payload transaction executor compilation failed with exit code $LASTEXITCODE."
    }

    & $output
    if ($LASTEXITCODE -ne 0) {
        throw "Protected payload transaction executor tests failed with exit code $LASTEXITCODE."
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$global:LASTEXITCODE = 0
