param(
    [string] $OutputName = "SBMSRecoveryBroker.exe"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$GeneratedRoot = Join-Path $Root "obj\version\recovery-broker"
$VersionSource = Join-Path $GeneratedRoot "SBMS.Version.g.cs"
$GeneratedManifest = Join-Path $GeneratedRoot "SBMSRecoveryBroker.manifest"
Write-SBMSGeneratedFile -LiteralPath $VersionSource -Content (
    New-SBMSCSharpVersionSource -Metadata $BuildMetadata -AssemblyTitle "SBMSRecoveryBroker" -FileDescription "SBMS crash recovery broker"
) | Out-Null
Write-SBMSGeneratedFile -LiteralPath $GeneratedManifest -Content (
    New-SBMSApplicationManifest -Metadata $BuildMetadata -AssemblyName "SBMS.RecoveryBroker" -ExecutionLevel asInvoker
) | Out-Null

$Sources = @(
    (Join-Path $Root "gui\Services\WindowMigrationJournal.cs"),
    (Join-Path $Root "recovery-broker\SBMSRecoveryBroker.cs"),
    $VersionSource
)
$Out = if ([IO.Path]::IsPathRooted($OutputName)) { $OutputName } else { Join-Path $Root $OutputName }
$CscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$Csc = $CscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $Csc) {
    throw "Missing .NET Framework csc.exe"
}

& $Csc /nologo /target:exe /optimize+ /win32manifest:$GeneratedManifest /out:$Out @Sources
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$versionInfo = (Get-Item -LiteralPath $Out).VersionInfo
if ([string]$versionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
    throw "Recovery broker version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Built: $Out"
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
