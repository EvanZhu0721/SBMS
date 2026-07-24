$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$NativePath = Join-Path $Root "SBMSNative.exe"
$HostPath = Join-Path $Root "SBMSDeviceHost.exe"
foreach ($path in @($NativePath, $HostPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing start-gate test binary: $path"
    }
}

function New-GatedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,
        [Parameter(Mandatory = $true)]
        [string] $Arguments
    )

    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $FilePath
    $process.StartInfo.Arguments = $Arguments
    $process.StartInfo.WorkingDirectory = $Root
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $process.StartInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Failed to start gate probe: $FilePath"
    }
    return $process
}

function Assert-WaitingLine {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $lineTask = $Process.StandardOutput.ReadLineAsync()
    if (-not $lineTask.Wait(5000)) {
        throw "$Label did not report its start-gate wait within 5000ms."
    }
    $line = $lineTask.Result
    if ($line -cne "start_gate=waiting") {
        throw "$Label emitted an unexpected pre-release line: $line"
    }
    if ($Process.HasExited) {
        throw "$Label exited before its start gate was released."
    }
}

function Invoke-NativeGateStressRound {
    param(
        [Parameter(Mandatory = $true)]
        [int] $Iteration
    )

    $gate = $null
    $process = $null
    try {
        $gateName = "Local\SBMS-StartGateStress-" + [guid]::NewGuid().ToString("N")
        $createdNew = $false
        $gate = [Threading.EventWaitHandle]::new(
            $false,
            [Threading.EventResetMode]::ManualReset,
            $gateName,
            [ref]$createdNew)
        if (-not $createdNew) {
            throw "Native stress gate was not unique at iteration $Iteration."
        }

        $process = New-GatedProcess `
            -FilePath $NativePath `
            -Arguments "--list --start-gate $gateName"
        Assert-WaitingLine -Process $process -Label "SBMSNative stress[$Iteration]"

        # Match the supervisor's production order: once the child has acknowledged
        # that it owns an open event handle, signal and immediately close the parent
        # handle. The child must still observe the signaled state.
        $gate.Set() | Out-Null
        $gate.Dispose()
        $gate = $null

        if (-not $process.WaitForExit(10000)) {
            throw "SBMSNative stress[$Iteration] did not finish after gate release."
        }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if ($process.ExitCode -ne 0) {
            throw "SBMSNative stress[$Iteration] failed exit=$($process.ExitCode): $stderr"
        }
        if ($stdout -notlike "*start_gate=released*") {
            throw "SBMSNative stress[$Iteration] did not observe the released gate."
        }
    } finally {
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill()
                    $process.WaitForExit(1000) | Out-Null
                }
            } catch {
            }
            $process.Dispose()
        }
        if ($null -ne $gate) {
            $gate.Dispose()
        }
    }
}

$nativeGate = $null
$native = $null
$hostGate = $null
$hostProcess = $null
try {
    $nativeGateName = "Local\SBMS-StartGateTest-Native-" + [guid]::NewGuid().ToString("N")
    $createdNew = $false
    $nativeGate = [Threading.EventWaitHandle]::new(
        $false,
        [Threading.EventResetMode]::ManualReset,
        $nativeGateName,
        [ref]$createdNew)
    if (-not $createdNew) {
        throw "Native start-gate test event was not unique."
    }
    $native = New-GatedProcess -FilePath $NativePath -Arguments "--list --start-gate $nativeGateName"
    Assert-WaitingLine -Process $native -Label "SBMSNative"
    $nativeGate.Set() | Out-Null
    $nativeGate.Dispose()
    $nativeGate = $null
    if (-not $native.WaitForExit(10000)) {
        throw "SBMSNative did not finish its read-only list probe after gate release."
    }
    $nativeOutput = $native.StandardOutput.ReadToEnd()
    $nativeError = $native.StandardError.ReadToEnd()
    if ($native.ExitCode -ne 0) {
        throw "SBMSNative gate probe failed exit=$($native.ExitCode): $nativeError"
    }
    if ($nativeOutput -notlike "*start_gate=released*") {
        throw "SBMSNative did not confirm gate release."
    }
    1..12 | ForEach-Object {
        Invoke-NativeGateStressRound -Iteration $_
    }

    $hostGateName = "Local\SBMS-StartGateTest-Host-" + [guid]::NewGuid().ToString("N")
    $createdNew = $false
    $hostGate = [Threading.EventWaitHandle]::new(
        $false,
        [Threading.EventResetMode]::ManualReset,
        $hostGateName,
        [ref]$createdNew)
    if (-not $createdNew) {
        throw "Device-host start-gate test event was not unique."
    }
    $hostProcess = New-GatedProcess -FilePath $HostPath -Arguments "--count 1 --start-gate $hostGateName"
    Assert-WaitingLine -Process $hostProcess -Label "SBMSDeviceHost"
    $hostProcess.Kill()
    if (-not $hostProcess.WaitForExit(5000)) {
        throw "Gated device host did not terminate within 5000ms."
    }
    $hostOutput = $hostProcess.StandardOutput.ReadToEnd()
    if ($hostOutput -like "*device_host=created*") {
        throw "Device host created a software device before gate release."
    }

    Write-Host "Start-gate tests passed: bounded waiting ACK, immediate parent-handle disposal, 12 native stress rounds, and host pre-mutation containment."
} finally {
    foreach ($process in @($native, $hostProcess)) {
        if ($null -eq $process) {
            continue
        }
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(1000) | Out-Null
            }
        } catch {
        }
        $process.Dispose()
    }
    if ($null -ne $nativeGate) {
        $nativeGate.Dispose()
    }
    if ($null -ne $hostGate) {
        $hostGate.Dispose()
    }
}
