[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$TaskName,

    [ValidateSet('RecoveryDrill', 'TestSigning')]
    [string]$Profile = 'RecoveryDrill',

    [switch]$Execute,

    [Parameter(Mandatory = $true)]
    [string]$Acknowledgement
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Request-SBMSFallbackRestartOnce {
    param([string]$Reason)

    if (-not $Execute) { throw $Reason }
    $expected = "SBMS-HARDWARE-LAB-WATCHDOG/$RunId/$Profile"
    if ($Acknowledgement -cne $expected) { throw $Reason }

    if (-not (Test-Path -LiteralPath $RunDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $RunDirectory -Force | Out-Null
    }
    $terminalPath = Join-Path $RunDirectory 'watchdog-restart.requested'
    if (Test-Path -LiteralPath $terminalPath -PathType Leaf) { return }
    $intentPath = Join-Path $RunDirectory 'watchdog-restart.intent'
    if (-not (Test-Path -LiteralPath $intentPath -PathType Leaf)) {
        try {
            $intent = New-Object IO.FileStream($intentPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(([DateTime]::UtcNow.ToString('o')) + " fallback: $Reason`n")
                $intent.Write($bytes, 0, $bytes.Length); $intent.Flush($true)
            } finally { $intent.Dispose() }
        } catch [IO.IOException] {}
    }

    $shutdown = Join-Path $env:SystemRoot 'System32\shutdown.exe'
    & $shutdown /r /f /t 5 /d p:0:0 /c 'SBMS watchdog fail-safe recovery' *> $null
    if ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $terminalPath)) {
        try {
            $terminal = New-Object IO.FileStream($terminalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(([DateTime]::UtcNow.ToString('o')) + " requested`n")
                $terminal.Write($bytes, 0, $bytes.Length); $terminal.Flush($true)
            } finally { $terminal.Dispose() }
        } catch [IO.IOException] {}
    }
    if (Test-Path -LiteralPath $terminalPath -PathType Leaf) {
        $schtasks = Join-Path $env:SystemRoot 'System32\schtasks.exe'
        & $schtasks /Change /TN $TaskName /Disable *> $null
    }
}

try {
    $modulePath = Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1'
    Import-Module -Name $modulePath -Force -ErrorAction Stop
    Invoke-SBMSHardwareLabWatchdog `
        -RunDirectory $RunDirectory `
        -RunId $RunId `
        -TaskName $TaskName `
        -Profile $Profile `
        -Execute:$Execute `
        -Acknowledgement $Acknowledgement
} catch {
    Request-SBMSFallbackRestartOnce -Reason $_.Exception.Message
}
