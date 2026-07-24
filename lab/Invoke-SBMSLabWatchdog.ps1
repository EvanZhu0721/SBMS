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
    param([string]$Reason, [switch]$KeepTaskEnabled)

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
    if ((Test-Path -LiteralPath $terminalPath -PathType Leaf) -and -not $KeepTaskEnabled) {
        $schtasks = Join-Path $env:SystemRoot 'System32\schtasks.exe'
        & $schtasks /Change /TN $TaskName /Disable *> $null
    }
}

try {
    $gateCModule = Join-Path $RunDirectory 'gate-c\payload\SBMS.GateC.psm1'
    $gateCManifestPath = Join-Path $RunDirectory 'gate-c\manifest.json'
    if ((Test-Path -LiteralPath $gateCModule -PathType Leaf) -and
        (Test-Path -LiteralPath $gateCManifestPath -PathType Leaf)) {
        $gateCManifest = Get-Content -LiteralPath $gateCManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $plannedGateCModule = @($gateCManifest.plan.files | Where-Object { [string]$_.name -eq 'SBMS.GateC.psm1' })
        if ([string]$gateCManifest.runId -cne $RunId.ToString() -or
            $plannedGateCModule.Count -ne 1 -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath([string]$plannedGateCModule[0].path),
                [IO.Path]::GetFullPath($gateCModule),
                [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $gateCModule -Algorithm SHA256).Hash -cne [string]$plannedGateCModule[0].sha256) {
            throw 'Frozen Gate C watchdog module identity or hash is invalid.'
        }
        Import-Module -Name $gateCModule -Force -ErrorAction Stop
        if ([string]$gateCManifest.state -in @(
                'InstallIntent',
                'PackageOwned',
                'HostStarted',
                'InstalledAndVerified',
                'RollbackRequired',
                'RollbackIntent',
                'RollbackPendingReboot')) {
            $gateCAcknowledgement = "SBMS-GATE-C/$RunId/Rollback/$($gateCManifest.planSha256)"
            $gateCResult = Invoke-SBMSGateC `
                -Phase Rollback `
                -RunId $RunId `
                -Execute `
                -Acknowledgement $gateCAcknowledgement
            if ([string]$gateCResult.state -eq 'RollbackPendingReboot') {
                Request-SBMSFallbackRestartOnce -Reason 'Gate C rollback requires one reboot before final read-back.' -KeepTaskEnabled
                return
            }
        }
    }

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
