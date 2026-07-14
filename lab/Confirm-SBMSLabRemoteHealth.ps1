[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][string]$Challenge,
    [string]$RunRoot = 'C:\ProgramData\SBMSLab\Runs',
    [switch]$BitLockerRecoveryAccessVerified
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SBMS.GateA.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1') -Force

$runDirectory = Join-Path ([IO.Path]::GetFullPath($RunRoot)) $RunId.ToString()
$proof = Confirm-SBMSGateARemoteHealth -RunId $RunId -RunDirectory $runDirectory -Challenge $Challenge -BitLockerRecoveryAccessVerified:$BitLockerRecoveryAccessVerified
$adapter = New-SBMSHardwareLabAdapter
$secured = & $adapter.SecureRunDirectory $runDirectory
if ($null -eq $secured -or -not [bool]$secured.success) { throw 'SSH proof evidence ACL protection failed.' }
$readback = & $adapter.TestRunDirectorySecurity $runDirectory
if ($null -eq $readback -or -not [bool]$readback.success) { throw 'SSH proof evidence ACL structured read-back failed.' }
$proof
