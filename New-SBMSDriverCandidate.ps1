param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $SigningPolicyPath,

    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Import-Module (Join-Path $root 'build\SBMS.DriverCertification.psm1') -Force
Import-Module (Join-Path $root 'build\SBMS.Version.psm1') -Force
Import-Module (Join-Path $root 'build\SBMS.Signing.psm1') -Force

$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the repository commit.'
}
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $root status --porcelain | Out-String))
if ($dirty) {
    throw 'WHQL candidate build requires a clean source tree.'
}
$metadata = Get-SBMSBuildMetadata -RepositoryRoot $root
$policy = Read-SBMSSigningPolicy -LiteralPath $SigningPolicyPath
$null = Resolve-SBMSSigningCertificate -Policy $policy
& (Join-Path $root 'build-sbms-driver.ps1') `
    -Production `
    -SigningPolicyPath $SigningPolicyPath `
    -SignToolPath $SignToolPath
$driverDirectory = Join-Path $root 'Windows-driver-samples\video\IndirectDisplay\x64\Release\IddSampleDriver'
$msbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue
$toolchain = [pscustomobject][ordered]@{
    powershell = $PSVersionTable.PSVersion.ToString()
    operatingSystem = [Environment]::OSVersion.VersionString
    msbuild = if ($msbuild) { [string]$msbuild.Source } else { $null }
}

New-SBMSDriverCandidate `
    -DriverDirectory $DriverDirectory `
    -OutputDirectory $OutputDirectory `
    -SourceCommit $commit `
    -SourceDirty $dirty `
    -BuildCommand '.\build-sbms-driver.ps1 -Production -SigningPolicyPath <external>' `
    -Toolchain $toolchain `
    -ExpectedWindowsVersion $metadata.WindowsVersion `
    -ExpectedProductVersion $metadata.SemVer `
    -ExpectedDriverVer $metadata.DriverVer `
    -SigningPolicy $policy `
    -SignToolPath $SignToolPath
