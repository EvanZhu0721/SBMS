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
    'SBMS-file-transaction-journal-store-' + [guid]::NewGuid().ToString('N')
)
$output = Join-Path $testRoot 'FileTransactionJournalStoreTests.exe'
[void](New-Item -ItemType Directory -Path $testRoot)

try {
    $sourcePaths = @(
        (Join-Path $root 'installer\InstallerTransactionModels.cs'),
        (Join-Path $root 'installer\InstallerJournal.cs'),
        (Join-Path $root 'installer\FileTransactionJournalStore.cs'),
        (Join-Path $root 'tests\FileTransactionJournalStoreTests.cs')
    )
    foreach ($sourcePath in $sourcePaths) {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Missing file transaction journal source: $sourcePath"
        }
    }

    $compilerArgs = @(
        '/nologo',
        '/target:exe',
        '/optimize+',
        "/out:$output",
        '/reference:System.Runtime.Serialization.dll',
        '/reference:System.Security.dll'
    ) + $sourcePaths
    & $csc @compilerArgs
    $compileExitCode = $LASTEXITCODE
    if ($compileExitCode -ne 0) {
        throw "File transaction journal test compilation failed with exit code $compileExitCode."
    }

    & $output
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "File transaction journal tests failed with exit code $testExitCode."
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$global:LASTEXITCODE = 0
