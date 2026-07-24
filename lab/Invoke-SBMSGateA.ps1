[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$RunId,
    [string]$RunRoot = 'C:\ProgramData\SBMSLab\Runs',
    [Parameter(Mandatory)][string]$EvidenceFixturePath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SBMS.GateA.psm1') -Force

# The fixture entrypoint deliberately accepts only previously captured structured
# evidence. Real collectors are added behind the same contract before TestSigning
# can be unlocked; this command cannot change BCD, drivers, tasks, or topology.
$fixture = [IO.Path]::GetFullPath($EvidenceFixturePath)
$capture = { Get-Content -LiteralPath $fixture -Raw -Encoding UTF8 | ConvertFrom-Json }.GetNewClosure()
Invoke-SBMSGateA -RunId $RunId -RunDirectory (Join-Path $RunRoot $RunId.ToString()) -CaptureEvidence $capture
