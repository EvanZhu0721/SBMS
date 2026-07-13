[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('Audit', 'Prepare', 'Arm', 'Rollback')]
    [string]$Phase = 'Audit',

    [ValidateSet('RecoveryDrill', 'TestSigning')]
    [string]$Profile = 'RecoveryDrill',

    [string]$RunId = ([guid]::NewGuid().ToString()),
    [string]$RunRoot = 'C:\ProgramData\SBMSLab\Runs',
    [switch]$Execute,
    [string]$Acknowledgement,
    [ValidateRange(3, 30)][int]$WatchdogTimeoutMinutes = 8
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1'
Import-Module -Name $modulePath -Force -ErrorAction Stop

$invokeParameters = @{
    Phase = $Phase
    Profile = $Profile
    RunId = $RunId
    RunRoot = $RunRoot
    Execute = $Execute
    Acknowledgement = $Acknowledgement
    WatchdogTimeoutMinutes = $WatchdogTimeoutMinutes
    Confirm = $false
}

if ($WhatIfPreference) { $invokeParameters.WhatIf = $true }

Invoke-SBMSHardwareLab @invokeParameters
