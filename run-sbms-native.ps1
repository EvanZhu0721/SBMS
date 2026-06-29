param(
    [string] $Source = "4550x2560",
    [string] $Target = "2560x1440",
    [ValidateSet("linear", "point", "box2x")]
    [string] $Filter = "linear",
    [switch] $List,
    [switch] $Vsync,
    [switch] $NoInput,
    [switch] $NoWindowMove,
    [switch] $ManageVirtualDisplay,
    [int] $Seconds = 0
)

$ErrorActionPreference = "Stop"

$Exe = Join-Path $PSScriptRoot "SBMSNative.exe"
$DeviceHost = Join-Path $PSScriptRoot "SBMSDeviceHost.exe"
if (-not (Test-Path $Exe)) {
    & (Join-Path $PSScriptRoot "build-sbms-native.ps1")
}

if ($List) {
    & $Exe --list
    exit $LASTEXITCODE
}

function Signal-SBMSDeviceHostStop {
    try {
        $event = [System.Threading.EventWaitHandle]::OpenExisting("Local\SBMSDeviceHostStop")
        try {
            [void] $event.Set()
        } finally {
            $event.Dispose()
        }
    } catch {
    }
}

function Wait-SBMSSource {
    param(
        [string] $Selector,
        [int] $TimeoutMs = 30000,
        [System.Diagnostics.Process] $HostProcess = $null
    )
    $deadline = [Environment]::TickCount + $TimeoutMs
    while ([Environment]::TickCount -lt $deadline) {
        if ($HostProcess -and $HostProcess.HasExited) {
            return $false
        }
        $displayListText = & $Exe --list 2>&1 | Out-String
        if (Test-SBMSVirtualSource -Selector $Selector -ListOutput $displayListText) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Test-SBMSVirtualSource {
    param(
        [string] $Selector,
        [string] $ListOutput
    )
    return -not [string]::IsNullOrWhiteSpace((Get-SBMSVirtualSourceDevice -Selector $Selector -ListOutput $ListOutput))
}

function Get-SBMSVirtualSourceDevice {
    param(
        [string] $Selector,
        [string] $ListOutput
    )
    $devices = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($ListOutput -split "`r?`n")) {
        if ($line -notmatch '^\s*(\\\\\.\\DISPLAY\d+)(?: primary)?: pos=\S+ mode=(\d+x\d+)@\d+ name=(.+)$') {
            continue
        }
        $device = $Matches[1]
        $resolution = $Matches[2]
        $name = $Matches[3]
        $isVirtual = $name -match '(?i)(iddsample|displaybridge|sbms)'
        if (-not $isVirtual) {
            continue
        }
        if ($device -ieq $Selector -or $resolution -ieq $Selector) {
            $devices.Add($device)
        }
    }

    if ($devices.Count -eq 0) {
        return $null
    }

    return $devices |
        Sort-Object { if ($_ -match 'DISPLAY(\d+)$') { [int]$Matches[1] } else { [int]::MaxValue } } |
        Select-Object -First 1
}

$Args = @("--source", $Source, "--target", $Target, "--filter", $Filter)
if ($Vsync) {
    $Args += "--vsync"
}
if ($NoInput) {
    $Args += "--no-input"
}
if ($NoWindowMove) {
    $Args += "--no-window-move"
}
if ($Seconds -gt 0) {
    $Args += @("--seconds", "$Seconds")
}

if (-not $ManageVirtualDisplay) {
    & $Exe $Args
    exit $LASTEXITCODE
}

if (-not (Test-Path $DeviceHost)) {
    & (Join-Path $PSScriptRoot "build-sbms-device-host.ps1")
}

Signal-SBMSDeviceHostStop
Start-Sleep -Milliseconds 300
$HostStdOut = Join-Path $env:TEMP "SBMSDeviceHost.out.log"
$HostStdErr = Join-Path $env:TEMP "SBMSDeviceHost.err.log"
Remove-Item -LiteralPath $HostStdOut, $HostStdErr -Force -ErrorAction SilentlyContinue
$HostProcess = Start-Process -FilePath $DeviceHost -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput $HostStdOut -RedirectStandardError $HostStdErr -PassThru
$ExitCode = 0
try {
    if (-not (Wait-SBMSSource -Selector $Source -HostProcess $HostProcess)) {
        if ($HostProcess -and $HostProcess.HasExited) {
            $hostLog = ((Get-Content -LiteralPath $HostStdOut, $HostStdErr -ErrorAction SilentlyContinue) -join "`n")
            if ($hostLog) {
                Write-Error $hostLog
            }
        }
        throw "Timed out waiting for source display: $Source"
    }
    $displayListText = & $Exe --list 2>&1 | Out-String
    $resolvedSource = Get-SBMSVirtualSourceDevice -Selector $Source -ListOutput $displayListText
    if (-not [string]::IsNullOrWhiteSpace($resolvedSource)) {
        if ($resolvedSource -ine $Source) {
            Write-Host "Resolved virtual source $Source -> $resolvedSource"
        }
        $Args[1] = $resolvedSource
    }
    & $Exe $Args
    $ExitCode = $LASTEXITCODE
} finally {
    Signal-SBMSDeviceHostStop
    if ($HostProcess -and -not $HostProcess.HasExited) {
        if (-not $HostProcess.WaitForExit(4000)) {
            Stop-Process -Id $HostProcess.Id -Force
        }
    }
}
exit $ExitCode
