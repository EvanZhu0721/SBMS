[CmdletBinding()]
param(
    [ValidateSet('Start','Install','SupervisedLaunch','Rollback','Finalize','Status')]
    [string]$Phase = 'Status',

    [guid]$RunId,
    [switch]$NoRestart,
    [switch]$NoLaunchGui,

    [ValidateRange(100, 60000)]
    [int]$SupervisionPollMilliseconds = 1000
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RunRoot = 'C:\ProgramData\SBMSLab\Runs'
$ActiveRunPath = 'C:\ProgramData\SBMSLab\issue4-active.json'
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
$RunIdWasBound = $PSBoundParameters.ContainsKey('RunId')
$SupervisionPollWasBound = $PSBoundParameters.ContainsKey('SupervisionPollMilliseconds')
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
    if ($SupervisionPollWasBound) {
        $arguments.Add((ConvertTo-WindowsArgument '-SupervisionPollMilliseconds'))
        $arguments.Add((ConvertTo-WindowsArgument ([string]$SupervisionPollMilliseconds)))
    }
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
    if ([int]$active.schemaVersion -ne 1 -or [string]$active.contractVersion -ine 'issue4-lab/1') {
        throw 'Active Issue #4 lab pointer is invalid.'
    }
    [guid]$active.runId
}

function Get-SBMSIssue4MissingPhysicalPaths {
    param(
        [string[]]$BaselineMonitorDevicePaths,
        [object[]]$CurrentPaths
    )

    @(
        foreach ($baselinePath in @($BaselineMonitorDevicePaths)) {
            $matches = @(
                $CurrentPaths | Where-Object {
                    [string]$_.monitorDevicePath -ieq [string]$baselinePath -and
                    [bool]$_.active -and
                    [bool]$_.targetAvailable -and
                    [string]$_.classification -ieq 'physical'
                }
            )
            if ($matches.Count -ne 1) { [string]$baselinePath }
        }
    )
}

function Test-SBMSIssue4ProcessIdentityEqual {
    param($Expected, $Current)

    if ($null -eq $Expected -or $null -eq $Current) { return $false }
    [int]$Expected.processId -eq [int]$Current.processId -and
        [string]::Equals(
            [IO.Path]::GetFullPath([string]$Expected.executablePath),
            [IO.Path]::GetFullPath([string]$Current.executablePath),
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        [string]$Expected.creationDate -ceq [string]$Current.creationDate
}

function Get-SBMSIssue4SupervisorMutexName {
    param([guid]$Id)
    "Global\SBMSIssue4GuiSupervisor_$($Id.ToString('N').ToLowerInvariant())"
}

function New-SBMSIssue4SupervisionLifecycle {
    [pscustomobject]@{
        armed = $false
        completedNormally = $false
        rollbackAttempted = $false
        rollbackAction = $null
        rollbackReason = $null
        rollbackResult = $null
        rollbackError = $null
    }
}

function Invoke-SBMSIssue4SupervisionRollbackOnce {
    param(
        [Parameter(Mandatory=$true)]$Lifecycle,
        [Parameter(Mandatory=$true)][string]$Reason
    )

    if (-not [bool]$Lifecycle.armed -or [bool]$Lifecycle.completedNormally) {
        return $null
    }
    if ([bool]$Lifecycle.rollbackAttempted) {
        if ($null -ne $Lifecycle.rollbackError) {
            throw "The exact supervised rollback was already attempted and failed: $([string]$Lifecycle.rollbackError)"
        }
        return $Lifecycle.rollbackResult
    }
    $Lifecycle.rollbackAttempted = $true
    $Lifecycle.rollbackReason = $Reason

    try {
        $result = & $Lifecycle.rollbackAction
        $Lifecycle.rollbackResult = $result
        return $result
    } catch {
        $Lifecycle.rollbackError = $_.Exception.Message
        throw
    }
}

function Invoke-SBMSIssue4InterruptedSupervisionCleanup {
    param(
        [Parameter(Mandatory=$true)]$Lifecycle,
        [Parameter(Mandatory=$true)][string]$Reason,
        [scriptblock]$ReportFailure = { param([string]$Message) Write-Warning $Message }
    )

    if (-not [bool]$Lifecycle.armed -or
        [bool]$Lifecycle.completedNormally -or
        [bool]$Lifecycle.rollbackAttempted) {
        return
    }
    try {
        Invoke-SBMSIssue4SupervisionRollbackOnce -Lifecycle $Lifecycle -Reason $Reason | Out-Null
    } catch {
        & $ReportFailure "Exact same-Run Gate C rollback failed during interrupted supervision: $($_.Exception.Message) The armed boot watchdog remains the recovery path."
    }
}

function Invoke-SBMSIssue4GuiSafetyRollback {
    param(
        [hashtable]$Adapter,
        [string]$Reason
    )

    & $Adapter.ReportProgress "SAFETY TRIP: $Reason"
    try {
        $rolledBack = & $Adapter.Rollback
    } catch {
        throw "Supervised GUI safety check failed: $Reason Exact same-Run Gate C rollback also failed: $($_.Exception.Message)"
    }
    $state = if ($null -ne $rolledBack -and $null -ne $rolledBack.PSObject.Properties['state']) {
        [string]$rolledBack.state
    } else {
        'unknown'
    }
    if ($state -notin @('RollbackVerified', 'RollbackPendingReboot')) {
        throw "Supervised GUI safety check failed: $Reason Exact same-Run Gate C rollback returned unaccepted state '$state' and is treated as failed."
    }
    throw "Supervised GUI safety check failed: $Reason Exact same-Run Gate C rollback completed with state '$state'."
}

function Invoke-SBMSIssue4GuiSupervisor {
    param(
        [Parameter(Mandatory=$true)][string]$GuiPath,
        [Parameter(Mandatory=$true)][string[]]$BaselineMonitorDevicePaths,
        [Parameter(Mandatory=$true)][hashtable]$Adapter,
        [ValidateRange(100, 60000)][int]$PollMilliseconds = 1000
    )

    if ($BaselineMonitorDevicePaths.Count -eq 0) {
        throw 'Supervised GUI launch requires at least one frozen baseline physical monitor path.'
    }

    & $Adapter.ReportProgress 'Checking frozen physical display baseline before GUI launch...'
    try {
        $before = @(& $Adapter.GetActiveDisplayPaths)
    } catch {
        Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "physical display baseline could not be read before launch: $($_.Exception.Message)"
    }
    $missingBefore = @(Get-SBMSIssue4MissingPhysicalPaths -BaselineMonitorDevicePaths $BaselineMonitorDevicePaths -CurrentPaths $before)
    if ($missingBefore.Count -gt 0) {
        Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "baseline physical display path was unavailable before launch: $($missingBefore -join ', ')"
    }

    & $Adapter.ReportProgress 'Launching the frozen SBMS GUI under supervision...'
    try {
        $gui = & $Adapter.StartGui $GuiPath
    } catch {
        Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "the GUI process could not be started with an exact identity: $($_.Exception.Message)"
    }
    if ($null -eq $gui -or $null -eq $gui.PSObject.Properties['identity']) {
        Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason 'the GUI process identity could not be captured'
    }
    & $Adapter.ReportProgress "GUI PID $([int]$gui.identity.processId) is supervised. Close the GUI normally to finish; physical display safety is checked every $PollMilliseconds ms."
    & $Adapter.ReportProgress 'Keep this PowerShell window open while testing. Ctrl+C or a graceful PowerShell exit requests exact same-Run rollback; a force-killed process falls back to the armed boot watchdog.'

    while ($true) {
        try {
            $status = & $Adapter.GetGuiStatus $gui
        } catch {
            Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "GUI status could not be read: $($_.Exception.Message)"
        }
        if ($null -eq $status -or $null -eq $status.PSObject.Properties['state']) {
            Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason 'GUI status adapter returned no state.'
        }
        if ([string]$status.state -ieq 'Exited') {
            & $Adapter.ReportProgress 'GUI exited. Confirming the physical display baseline three consecutive times before accepting a normal exit...'
            for ($confirmation = 1; $confirmation -le 3; $confirmation++) {
                try {
                    $finalPaths = @(& $Adapter.GetActiveDisplayPaths)
                } catch {
                    Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "final physical display confirmation $confirmation could not be read: $($_.Exception.Message)"
                }
                $finalMissing = @(
                    Get-SBMSIssue4MissingPhysicalPaths `
                        -BaselineMonitorDevicePaths $BaselineMonitorDevicePaths `
                        -CurrentPaths $finalPaths
                )
                if ($finalMissing.Count -gt 0) {
                    Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "final physical display confirmation $confirmation found an unavailable baseline path: $($finalMissing -join ', ')"
                }
                if ($confirmation -lt 3) {
                    & $Adapter.Sleep ([Math]::Min($PollMilliseconds, 1000))
                }
            }
            & $Adapter.ReportProgress 'GUI exited normally and the physical display baseline passed three final confirmations; no rollback was requested.'
            return [pscustomobject][ordered]@{
                outcome = 'ExitedNormally'
                processId = [int]$gui.identity.processId
                rollbackPerformed = $false
            }
        }
        if ([string]$status.state -ine 'Running' -or
            -not (Test-SBMSIssue4ProcessIdentityEqual -Expected $gui.identity -Current $status.identity)) {
            Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "GUI process identity changed or became unverifiable (state '$([string]$status.state)')."
        }

        try {
            $current = @(& $Adapter.GetActiveDisplayPaths)
        } catch {
            Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "physical display observer failed while the GUI was running: $($_.Exception.Message)"
        }
        $missing = @(Get-SBMSIssue4MissingPhysicalPaths -BaselineMonitorDevicePaths $BaselineMonitorDevicePaths -CurrentPaths $current)
        if ($missing.Count -gt 0) {
            Invoke-SBMSIssue4GuiSafetyRollback -Adapter $Adapter -Reason "baseline physical display path became unavailable: $($missing -join ', ')"
        }
        & $Adapter.Sleep $PollMilliseconds
    }
}

function Invoke-SBMSIssue4AuditedGuiLaunch {
    param(
        [Parameter(Mandatory=$true)][scriptblock]$Audit,
        [Parameter(Mandatory=$true)][scriptblock]$CreateSupervisorAdapter,
        $Lifecycle,
        [ValidateRange(100, 60000)][int]$PollMilliseconds = 1000
    )

    # Nothing capable of launching the GUI is called until Gate C's signed/hash
    # invariant audit has returned a complete InstalledAndVerified manifest.
    $manifest = & $Audit
    if ($null -eq $manifest -or [string]$manifest.state -ine 'InstalledAndVerified') {
        $state = if ($null -eq $manifest) { 'null' } else { [string]$manifest.state }
        throw "SupervisedLaunch requires an audited Gate C state 'InstalledAndVerified'; current state is '$state'."
    }
    $guiFile = @($manifest.plan.files | Where-Object { [string]$_.name -ieq 'SBMS.exe' })
    $displayConfigFile = @($manifest.plan.files | Where-Object { [string]$_.name -ieq 'SBMS.DisplayConfig.cs' })
    if ($guiFile.Count -ne 1 -or $displayConfigFile.Count -ne 1) {
        throw 'The audited frozen Gate C manifest does not uniquely identify the GUI and display observer.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.planSha256)) {
        throw 'The audited frozen Gate C manifest does not contain a plan digest.'
    }
    $baselinePaths = @($manifest.plan.baselinePhysicalMonitorPaths | ForEach-Object { [string]$_ })
    $adapter = & $CreateSupervisorAdapter ([string]$displayConfigFile[0].path) ([string]$manifest.planSha256)
    if ($null -ne $Lifecycle) {
        $Lifecycle.rollbackAction = $adapter.Rollback
        $Lifecycle.armed = $true
        $onceRollback = {
            Invoke-SBMSIssue4SupervisionRollbackOnce `
                -Lifecycle $Lifecycle `
                -Reason 'the physical display supervisor requested a safety rollback'
        }.GetNewClosure()
        $adapter.Rollback = $onceRollback
    }
    $supervised = Invoke-SBMSIssue4GuiSupervisor `
        -GuiPath ([string]$guiFile[0].path) `
        -BaselineMonitorDevicePaths $baselinePaths `
        -Adapter $adapter `
        -PollMilliseconds $PollMilliseconds
    if ($null -ne $Lifecycle -and [string]$supervised.outcome -ieq 'ExitedNormally') {
        if ([bool]$Lifecycle.rollbackAttempted) {
            throw 'The supervised launch cannot be accepted as normal after safety rollback began.'
        }
        $Lifecycle.completedNormally = $true
    }
    [pscustomobject][ordered]@{
        outcome = $supervised.outcome
        processId = $supervised.processId
        rollbackPerformed = $supervised.rollbackPerformed
    }
}

function Invoke-SBMSIssue4InterruptSafeAuditedGuiLaunch {
    param(
        [Parameter(Mandatory=$true)][scriptblock]$Audit,
        [Parameter(Mandatory=$true)][scriptblock]$CreateSupervisorAdapter,
        [Parameter(Mandatory=$true)]$Lifecycle,
        [ValidateRange(100, 60000)][int]$PollMilliseconds = 1000,
        [scriptblock]$ReportCleanupFailure = { param([string]$Message) Write-Warning $Message }
    )

    try {
        Invoke-SBMSIssue4AuditedGuiLaunch `
            -Audit $Audit `
            -CreateSupervisorAdapter $CreateSupervisorAdapter `
            -Lifecycle $Lifecycle `
            -PollMilliseconds $PollMilliseconds
    } finally {
        # This same runspace executes finally during a managed pipeline unwind,
        # including Ctrl+C/Stop(). A hard process kill cannot execute managed
        # cleanup; the already-armed boot watchdog remains the recovery boundary.
        Invoke-SBMSIssue4InterruptedSupervisionCleanup `
            -Lifecycle $Lifecycle `
            -Reason 'the PowerShell supervision pipeline stopped before normal GUI completion' `
            -ReportFailure $ReportCleanupFailure
    }
}

function New-SBMSIssue4GuiSupervisorRealAdapter {
    param(
        [string]$DisplayConfigSource,
        [scriptblock]$Rollback
    )

    if (-not ('SBMSDisplayConfig' -as [type])) {
        Add-Type -LiteralPath $DisplayConfigSource -ErrorAction Stop
    }

    @{
        GetActiveDisplayPaths = {
            @(
                [SBMSDisplayConfig]::GetActivePaths() | ForEach-Object {
                    [pscustomobject][ordered]@{
                        monitorDevicePath = [string]$_.MonitorDevicePath
                        active = [bool]$_.Active
                        targetAvailable = [bool]$_.TargetAvailable
                        classification = [string]$_.Classification
                    }
                }
            )
        }
        StartGui = {
            param([string]$Path)
            $fullPath = [IO.Path]::GetFullPath($Path)
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $fullPath
            $info.WorkingDirectory = Split-Path -Parent $fullPath
            $info.UseShellExecute = $false
            $process = New-Object Diagnostics.Process
            $process.StartInfo = $info
            if (-not $process.Start()) { throw 'The frozen SBMS GUI process did not start.' }

            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            $cim = $null
            while ($null -eq $cim -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
                $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction SilentlyContinue
                if ($null -eq $cim) { Start-Sleep -Milliseconds 100 }
            }
            if ($null -eq $cim -or
                -not [string]::Equals(
                    [IO.Path]::GetFullPath([string]$cim.ExecutablePath),
                    $fullPath,
                    [StringComparison]::OrdinalIgnoreCase
                )) {
                try { if (-not $process.HasExited) { $process.Kill() } } catch {}
                throw 'The frozen SBMS GUI exact process identity could not be captured.'
            }
            [pscustomobject][ordered]@{
                process = $process
                identity = [pscustomobject][ordered]@{
                    processId = [int]$cim.ProcessId
                    executablePath = [string]$cim.ExecutablePath
                    creationDate = [string]$cim.CreationDate
                }
            }
        }
        GetGuiStatus = {
            param($Gui)
            try {
                $Gui.process.Refresh()
                if ($Gui.process.HasExited) {
                    return [pscustomobject]@{ state = 'Exited'; identity = $null }
                }
            } catch {
                return [pscustomobject]@{ state = 'IdentityChanged'; identity = $null }
            }
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$Gui.identity.processId)" -ErrorAction SilentlyContinue
            if ($null -eq $cim) {
                return [pscustomobject]@{ state = 'IdentityChanged'; identity = $null }
            }
            [pscustomobject]@{
                state = 'Running'
                identity = [pscustomobject]@{
                    processId = [int]$cim.ProcessId
                    executablePath = [string]$cim.ExecutablePath
                    creationDate = [string]$cim.CreationDate
                }
            }
        }
        Rollback = $Rollback
        Sleep = { param([int]$Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
        ReportProgress = { param([string]$Message) Write-Host "[SBMS Issue #4] $Message" }
    }
}

# Dot-sourcing exposes the pure supervisor functions to the local fake-adapter
# tests without entering the elevated hardware transaction.
if ([string]$MyInvocation.InvocationName -ceq '.') { return }

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

if ($Phase -eq 'Start') {
    throw 'Issue #4 TestSigning hardware Start is suspended after Gate B failed before any Gate C install in Run 7924eb2e-f15d-4c20-8a56-7ff9a59719dc. Do not re-enable this path until Issue #18 provides a reviewed Microsoft-signed driver test route that does not require this workstation to boot with Test Signing.'
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
    Write-Host '[SBMS Issue #4] Capturing and validating the authoritative Gate A baseline...'
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
        -VerificationDeviceCount 1
    $securityAdapter = New-SBMSHardwareLabAdapter
    $secured = & $securityAdapter.SecureRunDirectory $runDirectory
    $securityReadback = & $securityAdapter.TestRunDirectorySecurity $runDirectory
    if ($null -eq $secured -or -not [bool]$secured.success -or
        $null -eq $securityReadback -or -not [bool]$securityReadback.success) {
        throw 'Gate C payload freeze did not preserve the protected run-directory ACL.'
    }

    $prepareAck = "SBMS-HARDWARE-LAB/$($selectedRunId.ToString())/TestSigning/Prepare"
    Write-Host '[SBMS Issue #4] Preparing the one-boot TestSigning clone and watchdog...'
    Invoke-SBMSHardwareLab `
        -Phase Prepare `
        -Profile TestSigning `
        -RunId $selectedRunId `
        -Execute `
        -Acknowledgement $prepareAck `
        -WatchdogTimeoutMinutes 30 `
        -Confirm:$false | Out-Null
    $armAck = "SBMS-HARDWARE-LAB/$($selectedRunId.ToString())/TestSigning/Arm"
    Write-Host '[SBMS Issue #4] Arming the exact one-boot clone transaction...'
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
    Write-Host '[SBMS Issue #4] Installing and verifying the frozen Gate C payload. Physical display paths are checked inside Gate C...'
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
        guiLaunched = $false
        nextPhase = 'SupervisedLaunch'
    }
    Write-Host "[SBMS Issue #4] Gate C Install completed with state '$($installed.state)'."
    Write-Host '[SBMS Issue #4] The GUI was not launched. Run this script with -Phase SupervisedLaunch; it will remain attached until the GUI exits.'
    return
}

if ($Phase -eq 'SupervisedLaunch') {
    $supervisorMutex = New-Object Threading.Mutex($false, (Get-SBMSIssue4SupervisorMutexName -Id $selectedRunId))
    $hasSupervisorMutex = $false
    $supervisionLifecycle = New-SBMSIssue4SupervisionLifecycle
    try {
        try {
            $hasSupervisorMutex = $supervisorMutex.WaitOne([TimeSpan]::Zero)
        } catch [Threading.AbandonedMutexException] {
            $hasSupervisorMutex = $true
        }
        if (-not $hasSupervisorMutex) {
            throw 'Another SupervisedLaunch is already active for this exact Run ID.'
        }

        Write-Host '[SBMS Issue #4] Auditing the frozen Gate C manifest and payload before loading any GUI code...'
        $auditAction = {
            Invoke-SBMSGateC -Phase Audit -RunId $selectedRunId
        }.GetNewClosure()
        $createAdapter = {
            param([string]$DisplayConfigPath, [string]$AuditedPlanSha256)
            # Freeze the authorization digest returned by the one successful
            # pre-launch Audit. A later payload drift must not block emergency
            # cleanup by forcing another full Audit before Rollback.
            $rollbackAction = {
                $rollbackAck = "SBMS-GATE-C/$($selectedRunId.ToString())/Rollback/$AuditedPlanSha256"
                Invoke-SBMSGateC -Phase Rollback -RunId $selectedRunId -Execute -Acknowledgement $rollbackAck
            }.GetNewClosure()
            New-SBMSIssue4GuiSupervisorRealAdapter `
                -DisplayConfigSource $DisplayConfigPath `
                -Rollback $rollbackAction
        }.GetNewClosure()
        # Ctrl+C, PipelineStoppedException, and a graceful host `exit` unwind
        # through the interrupt-safe function's finally block. Closing or
        # force-killing powershell.exe cannot be guaranteed to run managed
        # cleanup; the already-armed boot watchdog covers that boundary.
        $supervised = Invoke-SBMSIssue4InterruptSafeAuditedGuiLaunch `
            -Audit $auditAction `
            -CreateSupervisorAdapter $createAdapter `
            -Lifecycle $supervisionLifecycle `
            -PollMilliseconds $SupervisionPollMilliseconds
        [pscustomobject][ordered]@{
            phase = 'SupervisedLaunch'
            runId = $selectedRunId
            outcome = $supervised.outcome
            processId = $supervised.processId
            rollbackPerformed = $supervised.rollbackPerformed
            nextPhase = 'Finalize'
        }
        Write-Host '[SBMS Issue #4] Supervised GUI verification completed. Run this script with -Phase Finalize to remove the driver, devices, watchdog, and test boot clone.'
        return
    } finally {
        if ($hasSupervisorMutex) { $supervisorMutex.ReleaseMutex() }
        $supervisorMutex.Dispose()
    }
}

if ($Phase -in @('Rollback','Finalize')) {
    Write-Host "[SBMS Issue #4] Running exact same-Run $Phase cleanup..."
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
