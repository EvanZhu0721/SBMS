$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SBMS-maintenance-service-contracts-' +
    [guid]::NewGuid().ToString('N')
)
$serviceOut = Join-Path $testRoot 'SBMSMaintenanceService.exe'
$testOut = Join-Path $testRoot 'MaintenanceServiceRuntimeContractTests.exe'
[void](New-Item -ItemType Directory -Path $testRoot)

try {
    & (Join-Path $root 'build-sbms-maintenance-service.ps1') `
        -OutputName $serviceOut
    if ($LASTEXITCODE -ne 0) {
        throw "Maintenance service build failed with exit code $LASTEXITCODE."
    }

    $csc = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    ) | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    } | Select-Object -First 1
    if (-not $csc) {
        throw 'Missing .NET Framework csc.exe.'
    }
    $sources = @(
        (Join-Path $root 'installer\InstallerTransactionModels.cs'),
        (Join-Path $root 'installer\InstallerJournal.cs'),
        (Join-Path $root 'installer\WindowsHandleRelativeJournalFileSystem.cs'),
        (Join-Path $root 'installer\ProtectedEscrowManifestStore.cs'),
        (Join-Path $root 'installer\ProtectedPayloadWorkspaceCheckpointStore.cs'),
        (Join-Path $root 'installer\ProtectedPayloadBuildStateMachine.cs'),
        (Join-Path $root 'installer\DurableProtectedPayloadBuildWorkspaceModel.cs'),
        (Join-Path $root 'installer\FileTransactionJournalStore.cs'),
        (Join-Path $root 'installer\ProtectedPayloadStoreContracts.cs'),
        (Join-Path $root 'installer\ProtectedPayloadBuildContracts.cs'),
        (Join-Path $root 'installer\ProtectedPayloadNamespaceOwnerContracts.cs'),
        (Join-Path $root 'installer\ProtectedPayloadBrokerContracts.cs'),
        (Join-Path $root 'maintenance-service\MaintenanceServiceRuntimeContracts.cs'),
        (Join-Path $root 'maintenance-service\MaintenanceClientAuthorization.cs'),
        (Join-Path $root 'maintenance-service\MaintenanceReplayProductionStore.cs'),
        (Join-Path $root 'maintenance-service\MaintenanceReplayFileTransactionJournalFactory.cs'),
        (Join-Path $root 'tests\MaintenanceServiceRuntimeContractTests.cs')
    )
    $compilerArgs = @(
        '/nologo',
        '/target:exe',
        '/platform:x64',
        '/optimize+',
        "/out:$testOut",
        '/reference:System.Runtime.Serialization.dll',
        '/reference:System.Security.dll'
    ) + $sources
    & $csc @compilerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Maintenance runtime test compilation failed with exit code $LASTEXITCODE."
    }
    & $testOut
    if ($LASTEXITCODE -ne 0) {
        throw "Maintenance runtime tests failed with exit code $LASTEXITCODE."
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$global:LASTEXITCODE = 0
