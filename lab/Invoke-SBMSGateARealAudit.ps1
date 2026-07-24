[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$RunId,
    [string]$RunRoot = 'C:\ProgramData\SBMSLab\Runs',
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$PayloadRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$PolicyPath = (Join-Path $PSScriptRoot 'gate-a-policy.json'),
    [switch]$DiagnosticOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SBMS.GateA.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SBMS.GateA.Collectors.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1') -Force

$runDirectory = Join-Path ([IO.Path]::GetFullPath($RunRoot)) $RunId.ToString()
$adapter = New-SBMSHardwareLabAdapter
$administrator = [bool](& $adapter.TestAdministrator)
if (-not $DiagnosticOnly -and -not $administrator) { throw 'Authoritative Gate A requires an elevated administrator session. Use -DiagnosticOnly only for an explicitly non-authoritative dry audit.' }
if (-not $DiagnosticOnly) {
    $fixedRoot = [IO.Path]::GetFullPath('C:\ProgramData\SBMSLab\Runs')
    if ([IO.Path]::GetFullPath($RunRoot).TrimEnd('\') -cne $fixedRoot.TrimEnd('\')) { throw 'Authoritative Gate A is restricted to C:\ProgramData\SBMSLab\Runs.' }
}
if (-not (Test-Path -LiteralPath $runDirectory)) { New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null }
$runItem = Get-Item -LiteralPath $runDirectory -Force -ErrorAction Stop
if (($runItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Gate A Run directory must not be a reparse point.' }
$evidenceProtected = $false
if (-not $DiagnosticOnly) {
    $secured = & $adapter.SecureRunDirectory $runDirectory
    if ($null -eq $secured -or -not [bool]$secured.success) { throw 'Initial Gate A evidence ACL protection failed.' }
    $evidenceProtected = $true
}
$parameters = @{ RunId=$RunId; RunDirectory=$runDirectory; RepositoryRoot=$RepositoryRoot; PayloadRoot=$PayloadRoot; PolicyPath=$PolicyPath; EvidenceProtected=$evidenceProtected }
$capture = { Get-SBMSGateARealEvidence @parameters }.GetNewClosure()
$result = Invoke-SBMSGateA -RunId $RunId -RunDirectory $runDirectory -CaptureEvidence $capture
if (-not $DiagnosticOnly) {
    $secured = & $adapter.SecureRunDirectory $runDirectory
    if ($null -eq $secured -or -not [bool]$secured.success) { throw 'Final Gate A evidence ACL protection failed.' }
    $readback = & $adapter.TestRunDirectorySecurity $runDirectory
    if ($null -eq $readback -or -not [bool]$readback.success) { throw 'Final Gate A evidence ACL structured read-back failed.' }
}
$result
