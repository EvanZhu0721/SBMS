[CmdletBinding()]
param(
    [ValidateSet('Start','Install','Rollback','Finalize','Status')]
    [string]$Phase = 'Status',

    [guid]$RunId,
    [switch]$NoRestart,
    [switch]$NoLaunchGui
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RunRoot = 'C:\ProgramData\SBMSLab\Runs'
$ActiveRunPath = 'C:\ProgramData\SBMSLab\issue4-active.json'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
$RunIdWasBound = $PSBoundParameters.ContainsKey('RunId')
$ErrorLogPath = Join-Path $env:TEMP 'SBMS-Issue4Lab.error.log'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-WindowsArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append([char]34)
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) { $slashes++; continue }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $slashes) + 1)
            [void]$builder.Append([char]34)
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append([char]92, $slashes); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append([char]92, 2 * $slashes) }
    [void]$builder.Append([char]34)
    $builder.ToString()
}

function Restart-Elevated {
    $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Remove-Item -LiteralPath $ErrorLogPath -Force -ErrorAction SilentlyContinue
    $arguments = New-Object Collections.Generic.List[string]
    foreach ($item in @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Phase',$Phase)) {
        $arguments.Add((ConvertTo-WindowsArgument ([string]$item)))
    }
    if ($RunIdWasBound) {
        $arguments.Add((ConvertTo-WindowsArgument '-RunId'))
        $arguments.Add((ConvertTo-WindowsArgument $RunId.ToString()))
    }
    if ($NoRestart) { $arguments.Add('-NoRestart') }
    if ($NoLaunchGui) { $arguments.Add('-NoLaunchGui') }
    $process = Start-Process -FilePath $powerShell -ArgumentList ($arguments -join ' ') -Verb RunAs -PassThru -Wait
    if (Test-Path -LiteralPath $ErrorLogPath -PathType Leaf) {
        $errorText = Get-Content -LiteralPath $ErrorLogPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($errorText)) { Write-Error $errorText }
    }
    exit $process.ExitCode
}

function Write-ActiveRun {
    param([guid]$Id)
    $parent = Split-Path -Parent $ActiveRunPath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $value = [pscustomobject][ordered]@{
        schemaVersion = 1
        contractVersion = 'issue4-lab/1'
        runId = $Id.ToString()
        repositoryRoot = $RepositoryRoot
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    [IO.File]::WriteAllText($ActiveRunPath, ($value | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
}

function Resolve-RunId {
    if ($RunIdWasBound -and $RunId -ne [guid]::Empty) { return $RunId }
    if (-not (Test-Path -LiteralPath $ActiveRunPath -PathType Leaf)) { throw 'No active Issue #4 lab Run ID was found.' }
    $active = Get-Content -LiteralPath $ActiveRunPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$active.schemaVersion -ne 1 -or [string]$active.contractVersion -cne 'issue4-lab/1') {
        throw 'Active Issue #4 lab pointer is invalid.'
    }
    [guid]$active.runId
}

if (-not (Test-Administrator)) { Restart-Elevated }

trap {
    $diagnostic = [pscustomobject][ordered]@{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        phase = $Phase
        runId = if ($RunIdWasBound) { $RunId.ToString() } else { $null }
        message = $_.Exception.Message
        position = $_.InvocationInfo.PositionMessage
        scriptStackTrace = $_.ScriptStackTrace
    }
    [IO.File]::WriteAllText($ErrorLogPath, ($diagnostic | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    exit 1
}

Import-Module (Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1') -Force
if ($Phase -eq 'Start') {
    Import-Module (Join-Path $PSScriptRoot 'SBMS.GateC.psm1') -Force
} else {
    $selectedRunId = Resolve-RunId
    $runDirectory = Join-Path $RunRoot $selectedRunId.ToString()
    $frozenGateCModule = Join-Path $runDirectory 'gate-c\payload\SBMS.GateC.psm1'
    if (-not (Test-Path -LiteralPath $frozenGateCModule -PathType Leaf)) {
        throw "Frozen Gate C module is missing: $frozenGateCModule"
    }
    Import-Module $frozenGateCModule -Force
}

if ($Phase -eq 'Start') {
    $selectedRunId = if ($PSBoundParameters.ContainsKey('RunId') -and $RunId -ne [guid]::Empty) { $RunId } else { [guid]::NewGuid() }
    $runDirectory = Join-Path $RunRoot $selectedRunId.ToString()
    if (Test-Path -LiteralPath $runDirectory) { throw "Run directory already exists: $runDirectory" }

    $driverPackage = Join-Path $RepositoryRoot 'Windows-driver-samples\video\IndirectDisplay\x64\Release\IddSampleDriver'
    foreach ($required in @('SBMS.exe','SBMSNative.exe','SBMSDeviceHost.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $required) -PathType Leaf)) {
            throw "Build output is missing: $required"
        }
    }
    $gateAEntry = Join-Path $PSScriptRoot 'Invoke-SBMSGateARealAudit.ps1'
    $firstGateA = & $gateAEntry -RunId $selectedRunId -RepositoryRoot $RepositoryRoot -PayloadRoot $RepositoryRoot
    if ([string]$firstGateA.status -cne 'PASS') { throw 'Initial authoritative Gate A did not PASS.' }
    $secondGateA = & $gateAEntry -RunId $selectedRunId -RepositoryRoot $RepositoryRoot -PayloadRoot $RepositoryRoot
    if ([string]$secondGateA.status -cne 'PASS' -or [string]$secondGateA.stableDigest -cne [string]$firstGateA.stableDigest) {
        throw 'Gate A no-drift recapture did not PASS with the same stable digest.'
    }
    # The invoked Gate A entry imports HardwareLab in its child script scope.
    # Re-import it here so the parent transaction retains the exported commands.
    Import-Module (Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1') -Force

    $gateC = Initialize-SBMSGateC `
        -RunId $selectedRunId `
        -RunDirectory $runDirectory `
        -DriverPackagePath $driverPackage `
        -ProductRoot $RepositoryRoot `
        -VerificationDeviceCount 2

    $prepareAck = "SBMS-HARDWARE-LAB/$($selectedRunId.ToString())/TestSigning/Prepare"
    Invoke-SBMSHardwareLab `
        -Phase Prepare `
        -Profile TestSigning `
        -RunId $selectedRunId `
        -Execute `
        -Acknowledgement $prepareAck `
        -WatchdogTimeoutMinutes 30 `
        -Confirm:$false | Out-Null
    $armAck = "SBMS-HARDWARE-LAB/$($selectedRunId.ToString())/TestSigning/Arm"
    $armed = Invoke-SBMSHardwareLab `
        -Phase Arm `
        -Profile TestSigning `
        -RunId $selectedRunId `
        -Execute `
        -Acknowledgement $armAck `
        -WatchdogTimeoutMinutes 30 `
        -Confirm:$false
    Write-ActiveRun -Id $selectedRunId
    [pscustomobject]@{
        phase = 'Start'
        runId = $selectedRunId
        gateAStableDigest = $secondGateA.stableDigest
        gateCPlanSha256 = $gateC.planSha256
        hardwareState = $armed.state
        watchdogMinutes = 30
        restartRequested = (-not $NoRestart)
    }
    if (-not $NoRestart) {
        & (Join-Path $env:SystemRoot 'System32\shutdown.exe') /r /f /t 5 /d p:0:0 /c 'SBMS Issue 4 local hardware lab'
        if ($LASTEXITCODE -ne 0) { throw "Restart request failed with $LASTEXITCODE." }
    }
    return
}

if ($Phase -eq 'Install') {
    $manifest = Read-SBMSGateCManifest -RunDirectory $runDirectory
    $ack = "SBMS-GATE-C/$($selectedRunId.ToString())/Install/$($manifest.planSha256)"
    try {
        $installed = Invoke-SBMSGateC -Phase Install -RunId $selectedRunId -Execute -Acknowledgement $ack
    } catch {
        $installError = $_
        $failed = Read-SBMSGateCManifest -RunDirectory $runDirectory
        $rollbackAck = "SBMS-GATE-C/$($selectedRunId.ToString())/Rollback/$($failed.planSha256)"
        try {
            Invoke-SBMSGateC -Phase Rollback -RunId $selectedRunId -Execute -Acknowledgement $rollbackAck | Out-Null
        } catch {
            throw "Gate C Install failed: $($installError.Exception.Message) Rollback also failed: $($_.Exception.Message) Run directory: $runDirectory"
        }
        throw $installError
    }
    $guiPath = [string](@($installed.plan.files | Where-Object name -eq 'SBMS.exe')[0].path)
    [pscustomobject]@{
        phase = 'Install'
        runId = $selectedRunId
        state = $installed.state
        publishedName = $installed.ownedPublishedName
        guiPath = $guiPath
        rebootRequired = $installed.rebootRequired
    }
    if (-not $NoLaunchGui) {
        Start-Process -FilePath $guiPath -WorkingDirectory (Split-Path -Parent $guiPath)
    }
    return
}

if ($Phase -in @('Rollback','Finalize')) {
    $manifest = Read-SBMSGateCManifest -RunDirectory $runDirectory
    $gateCAck = "SBMS-GATE-C/$($selectedRunId.ToString())/Rollback/$($manifest.planSha256)"
    $gateC = Invoke-SBMSGateC -Phase Rollback -RunId $selectedRunId -Execute -Acknowledgement $gateCAck
    $hardware = Get-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $current = & (Join-Path $env:SystemRoot 'System32\bcdedit.exe') /enum '{current}' /v
    $onClone = (($current -join [Environment]::NewLine) -match [regex]::Escape([string]$hardware.clone.guid))
    if ($onClone) {
        [pscustomobject]@{ phase = $Phase; runId = $selectedRunId; gateCState = $gateC.state; defaultRestartRequested = (-not $NoRestart) }
        if (-not $NoRestart) {
            & (Join-Path $env:SystemRoot 'System32\shutdown.exe') /r /f /t 5 /d p:0:0 /c 'SBMS Issue 4 lab rollback'
            if ($LASTEXITCODE -ne 0) { throw "Rollback restart request failed with $LASTEXITCODE." }
        }
        return
    }
    $hardwareAck = "SBMS-HARDWARE-LAB/$($selectedRunId.ToString())/TestSigning/Rollback"
    $cleaned = Invoke-SBMSHardwareLab `
        -Phase Rollback `
        -Profile TestSigning `
        -RunId $selectedRunId `
        -Execute `
        -Acknowledgement $hardwareAck `
        -Confirm:$false
    if (Test-Path -LiteralPath $ActiveRunPath) { Remove-Item -LiteralPath $ActiveRunPath -Force }
    [pscustomobject]@{ phase = $Phase; runId = $selectedRunId; gateCState = $gateC.state; hardwareState = $cleaned.state }
    return
}

$gateCStatus = Invoke-SBMSGateC -Phase Audit -RunId $selectedRunId
$hardwareStatus = Get-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
[pscustomobject]@{
    phase = 'Status'
    runId = $selectedRunId
    gateCState = $gateCStatus.state
    hardwareState = $hardwareStatus.state
    publishedName = $gateCStatus.ownedPublishedName
    watchdogTask = $hardwareStatus.watchdogPlan.taskName
}
