param(
    [Parameter(Mandatory = $true)]
    [string] $CandidateDirectory,

    [Parameter(Mandatory = $true)]
    [string] $CandidateManifestSha256,

    [Parameter(Mandatory = $true)]
    [string] $ReturnedDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string] $SigningPolicyPath,

    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Import-Module (Join-Path $root 'build\SBMS.Signing.psm1') -Force
Import-Module (Join-Path $root 'build\SBMS.DriverCertification.psm1') -Force
$policy = Read-SBMSSigningPolicy -LiteralPath $SigningPolicyPath

Import-SBMSWhqlDriver `
    -CandidateDirectory $CandidateDirectory `
    -ExpectedCandidateManifestSha256 $CandidateManifestSha256 `
    -ReturnedDirectory $ReturnedDirectory `
    -OutputDirectory $OutputDirectory `
    -SigningPolicy $policy `
    -SignToolPath $SignToolPath
