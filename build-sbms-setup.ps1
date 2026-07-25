param(
    [string] $OutputName = "SBMSSetup.exe",
    [switch] $Production,
    [string] $SigningPolicyPath,
    [string] $SignToolPath
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$GeneratedRoot = Join-Path $Root "obj\version\setup"
$VersionSource = Join-Path $GeneratedRoot "SBMS.Version.g.cs"
$SigningSource = Join-Path $GeneratedRoot "SBMS.Signing.g.cs"
$GeneratedManifest = Join-Path $GeneratedRoot "SBMSSetup.manifest"
Write-SBMSGeneratedFile -LiteralPath $VersionSource -Content (
    New-SBMSCSharpVersionSource -Metadata $BuildMetadata -AssemblyTitle "SBMS Setup" -FileDescription "SBMS installer"
) | Out-Null
Write-SBMSGeneratedFile -LiteralPath $GeneratedManifest -Content (
    New-SBMSApplicationManifest -Metadata $BuildMetadata -AssemblyName "SBMS.Setup" -ExecutionLevel requireAdministrator
) | Out-Null
$publisherThumbprint = ''
$whqlCatalogSubjects = ''
if ($Production) {
    if ([string]::IsNullOrWhiteSpace($SigningPolicyPath)) {
        throw 'Production setup build requires -SigningPolicyPath.'
    }
    Import-Module (Join-Path $Root 'build\SBMS.Signing.psm1') -Force
    $SigningPolicy = Read-SBMSSigningPolicy -LiteralPath $SigningPolicyPath
    $null = Resolve-SBMSSigningCertificate -Policy $SigningPolicy
    $publisherThumbprint = [string]$SigningPolicy.publisher.thumbprint
    $whqlCatalogSubjects = (@(
        $SigningPolicy.driverCertification.allowedCatalogSubjects |
            ForEach-Object { [string]$_ }
    ) -join "`n").Replace('\', '\\').Replace('"', '\"').Replace("`r", '').Replace("`n", '\n')
}
$signingSourceText = @"
namespace SBMSBuild
{
    internal static class ProductionSigningInfo
    {
        internal const bool IntegrityRequired = $($Production.IsPresent.ToString().ToLowerInvariant());
        internal const string PublisherThumbprint = "$publisherThumbprint";
        internal const string WhqlCatalogSubjects = "$whqlCatalogSubjects";
    }
}
"@
Write-SBMSGeneratedFile -LiteralPath $SigningSource -Content $signingSourceText | Out-Null
$Source = Join-Path $Root "installer\SBMSSetup.cs"
$TransactionSource = Join-Path $Root 'installer\InstallTransaction.cs'
$TransactionModelsSource = Join-Path $Root 'installer\InstallerTransactionModels.cs'
$TransactionEngineSource = Join-Path $Root 'installer\InstallerTransactionEngine.cs'
$TransactionJournalSource = Join-Path $Root 'installer\InstallerJournal.cs'
$FileTransactionJournalStoreSource = Join-Path $Root 'installer\FileTransactionJournalStore.cs'
$ProtectedEscrowManifestStoreSource = Join-Path $Root 'installer\ProtectedEscrowManifestStore.cs'
$ProtectedPayloadStoreContractsSource = Join-Path $Root 'installer\ProtectedPayloadStoreContracts.cs'
$ProtectedPayloadRecoveryPlannerSource = Join-Path $Root 'installer\ProtectedPayloadRecoveryPlanner.cs'
$WindowsHandleRelativeJournalSource = Join-Path $Root 'installer\WindowsHandleRelativeJournalFileSystem.cs'
$WindowsInventorySource = Join-Path $Root 'installer\WindowsInstallerInventory.cs'
$OwnershipSource = Join-Path $Root 'installer\InstallerOwnership.cs'
$AuditOnlySource = Join-Path $Root 'installer\InstallerAuditOnly.cs'
$WindowsTransactionPlatformSource = Join-Path $Root 'installer\WindowsInstallerTransactionPlatform.cs'
$WindowsMutationExecutionSource = Join-Path $Root 'installer\WindowsInstallerMutationExecution.cs'
$VerifierSource = Join-Path $Root 'installer\ReleaseIntegrityVerifier.cs'
$DriverVerifierSource = Join-Path $Root 'installer\DriverCatalogVerifier.cs'
$Manifest = $GeneratedManifest
if ([System.IO.Path]::IsPathRooted($OutputName)) {
    $Out = $OutputName
} else {
    $Out = Join-Path $Root $OutputName
}

$CscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$Csc = $CscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Csc) {
    throw "Missing .NET Framework csc.exe"
}
if (-not (Test-Path $Source)) {
    throw "Missing source: $Source"
}
if (-not (Test-Path $Manifest)) {
    throw "Missing manifest: $Manifest"
}

& $Csc /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:$Manifest /out:$Out /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll /reference:System.Xml.dll /reference:System.Security.dll $Source $TransactionSource $TransactionModelsSource $TransactionEngineSource $TransactionJournalSource $WindowsHandleRelativeJournalSource $ProtectedEscrowManifestStoreSource $ProtectedPayloadStoreContractsSource $ProtectedPayloadRecoveryPlannerSource $FileTransactionJournalStoreSource $WindowsInventorySource $OwnershipSource $AuditOnlySource $WindowsTransactionPlatformSource $WindowsMutationExecutionSource $VerifierSource $DriverVerifierSource $VersionSource $SigningSource
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Production) {
    $null = Invoke-SBMSSignAuthenticode `
        -LiteralPath $Out `
        -Policy $SigningPolicy `
        -SignToolPath $SignToolPath
    $null = Assert-SBMSAuthenticodeSignature `
        -LiteralPath $Out `
        -Policy $SigningPolicy `
        -SignToolPath $SignToolPath
}

Write-Host "Built: $Out"
$versionInfo = (Get-Item -LiteralPath $Out).VersionInfo
if ([string]$versionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
    throw "Setup version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
