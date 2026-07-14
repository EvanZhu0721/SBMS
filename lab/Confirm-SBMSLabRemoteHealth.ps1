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

function Test-SshdAncestor {
    $id = $PID
    for ($depth = 0; $depth -lt 32 -and $id -gt 0; $depth++) {
        $process = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $id) -ErrorAction Stop
        if ([string]$process.Name -ieq 'sshd.exe') { return $true }
        $id = [int]$process.ParentProcessId
    }
    $false
}

function Get-RemoteSessionEvidence {
    $parts = @(([string]$env:SSH_CONNECTION).Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -ne 4) { throw 'SSH_CONNECTION is absent or malformed.' }
    $client = [Net.IPAddress]::Parse($parts[0])
    $nonLoopback = -not [Net.IPAddress]::IsLoopback($client)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $admin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $runDirectory = Join-Path ([IO.Path]::GetFullPath($RunRoot)) $RunId.ToString()
    $manifestPath = Join-Path $runDirectory 'gate-a\manifest.json'
    $readable = $false
    try { $null = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 -ErrorAction Stop; $readable = $true } catch {}
    $displaySource = Join-Path $PSScriptRoot 'SBMS.DisplayConfig.cs'
    Add-Type -TypeDefinition (Get-Content -LiteralPath $displaySource -Raw -Encoding UTF8) -Language CSharp -ErrorAction Stop
    $paths = @([SBMSDisplayConfig]::GetActivePaths())
    $physical = @($paths | Where-Object { $_.Active -and $_.TargetAvailable -and $_.Classification -eq 'physical' }).Count -gt 0
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    [pscustomobject][ordered]@{
        sshdAncestor = Test-SshdAncestor; nonLoopbackClient = $nonLoopback; adminCapable = $admin
        evidenceReadable = $readable; activePhysicalDisplay = $physical; computerName = $env:COMPUTERNAME
        lastBootUtc = ([DateTime]$os.LastBootUpTime).ToUniversalTime().ToString('o'); clientAddress = $client.ToString()
    }
}

$runDirectory = Join-Path ([IO.Path]::GetFullPath($RunRoot)) $RunId.ToString()
$capture = { Get-RemoteSessionEvidence }
Confirm-SBMSGateARemoteHealth -RunId $RunId -RunDirectory $runDirectory -Challenge $Challenge -CaptureSession $capture -BitLockerRecoveryAccessVerified:$BitLockerRecoveryAccessVerified
