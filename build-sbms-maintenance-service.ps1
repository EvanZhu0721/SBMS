param(
    [string] $OutputName = 'SBMSMaintenanceService.exe'
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
Import-Module (Join-Path $Root 'build\SBMS.Version.psm1') -Force
$metadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$generatedRoot = Join-Path $Root 'obj\version\maintenance-service'
$versionSource = Join-Path $generatedRoot 'SBMS.Version.g.cs'
$manifest = Join-Path $generatedRoot 'SBMSMaintenanceService.manifest'
Write-SBMSGeneratedFile -LiteralPath $versionSource -Content (
    New-SBMSCSharpVersionSource `
        -Metadata $metadata `
        -AssemblyTitle 'SBMSMaintenanceService' `
        -FileDescription 'SBMS maintenance service offline runtime baseline'
) | Out-Null
Write-SBMSGeneratedFile -LiteralPath $manifest -Content (
    New-SBMSApplicationManifest `
        -Metadata $metadata `
        -AssemblyName 'SBMS.MaintenanceService' `
        -ExecutionLevel asInvoker
) | Out-Null

$sources = @(
    (Join-Path $Root 'installer\InstallerTransactionModels.cs'),
    (Join-Path $Root 'installer\InstallerJournal.cs'),
    (Join-Path $Root 'installer\ProtectedPayloadStoreContracts.cs'),
    (Join-Path $Root 'installer\ProtectedPayloadBuildContracts.cs'),
    (Join-Path $Root 'installer\ProtectedPayloadNamespaceOwnerContracts.cs'),
    (Join-Path $Root 'installer\ProtectedPayloadBrokerContracts.cs'),
    (Join-Path $Root 'maintenance-service\MaintenanceServiceRuntimeContracts.cs'),
    (Join-Path $Root 'maintenance-service\MaintenanceClientAuthorization.cs'),
    (Join-Path $Root 'maintenance-service\MaintenanceReplayProductionStore.cs'),
    (Join-Path $Root 'maintenance-service\SBMSMaintenanceService.cs'),
    $versionSource
)
$out = if ([IO.Path]::IsPathRooted($OutputName)) {
    $OutputName
} else {
    Join-Path $Root $OutputName
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

$compilerArgs = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    "/win32manifest:$manifest",
    "/out:$out",
    '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Security.dll',
    '/reference:System.ServiceProcess.dll'
) + $sources
& $csc @compilerArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$versionInfo = (Get-Item -LiteralPath $out).VersionInfo
if ([string]$versionInfo.FileVersion -ne
        [string]$metadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne
        [string]$metadata.SemVer) {
    throw "Maintenance service version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Built: $out"
Write-Host "Version: $($metadata.SemVer) ($($metadata.WindowsVersion))"
