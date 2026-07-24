[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

function New-PhysicalPath {
    param(
        [string]$Path,
        [bool]$Active = $true,
        [bool]$Available = $true,
        [string]$Classification = 'physical'
    )
    [pscustomobject]@{
        monitorDevicePath = $Path
        active = $Active
        targetAvailable = $Available
        classification = $Classification
    }
}

function New-TestSupervisorAdapter {
    param(
        [object[]]$Statuses,
        [object[]]$DisplaySnapshots
    )

    $state = [pscustomobject]@{
        statusIndex = 0
        displayIndex = 0
        rollbacks = 0
        sleeps = 0
        reports = New-Object Collections.Generic.List[string]
    }
    $identity = [pscustomobject]@{
        processId = 4242
        executablePath = 'C:\Frozen\SBMS.exe'
        creationDate = '20260725010101.000000+000'
    }
    $adapter = @{
        GetActiveDisplayPaths = {
            $index = [Math]::Min($state.displayIndex, $DisplaySnapshots.Count - 1)
            $state.displayIndex++
            @($DisplaySnapshots[$index])
        }.GetNewClosure()
        StartGui = {
            param([string]$Path)
            if ([string]$Path -ine [string]$identity.executablePath) { throw 'unexpected GUI path' }
            [pscustomobject]@{ identity = $identity }
        }.GetNewClosure()
        GetGuiStatus = {
            param($Gui)
            $index = [Math]::Min($state.statusIndex, $Statuses.Count - 1)
            $state.statusIndex++
            $value = $Statuses[$index]
            if ($null -eq $value.PSObject.Properties['identity'] -and [string]$value.state -ieq 'Running') {
                return [pscustomobject]@{ state = 'Running'; identity = $identity }
            }
            $value
        }.GetNewClosure()
        Rollback = {
            $state.rollbacks++
            [pscustomobject]@{ state = 'RollbackVerified' }
        }.GetNewClosure()
        Sleep = { param([int]$Milliseconds) $state.sleeps++ }.GetNewClosure()
        ReportProgress = { param([string]$Message) $state.reports.Add($Message) }.GetNewClosure()
    }
    [pscustomobject]@{ adapter = $adapter; state = $state; identity = $identity }
}

function New-TestAuditedManifest {
    [pscustomobject]@{
        state = 'InstalledAndVerified'
        planSha256 = 'AUDITED-DIGEST'
        plan = [pscustomobject]@{
            files = @(
                [pscustomobject]@{ name = 'SBMS.exe'; path = 'C:\Frozen\SBMS.exe' },
                [pscustomobject]@{ name = 'SBMS.DisplayConfig.cs'; path = 'C:\Frozen\SBMS.DisplayConfig.cs' }
            )
            baselinePhysicalMonitorPaths = @('DISPLAY-A')
        }
    }
}

. (Join-Path $PSScriptRoot 'lab\Invoke-SBMSIssue4Lab.ps1')

Invoke-Test 'physical baseline matching is case-insensitive' {
    $missing = @(
        Get-SBMSIssue4MissingPhysicalPaths `
            -BaselineMonitorDevicePaths @('\\?\DISPLAY#ABC') `
            -CurrentPaths @((New-PhysicalPath '\\?\display#abc'))
    )
    Assert-True ($missing.Count -eq 0) 'same monitor path with different casing was treated as missing'
}

Invoke-Test 'inactive or unavailable baseline path is missing' {
    $inactive = @(Get-SBMSIssue4MissingPhysicalPaths -BaselineMonitorDevicePaths @('DISPLAY-A') -CurrentPaths @((New-PhysicalPath 'display-a' -Active $false)))
    $unavailable = @(Get-SBMSIssue4MissingPhysicalPaths -BaselineMonitorDevicePaths @('DISPLAY-A') -CurrentPaths @((New-PhysicalPath 'display-a' -Available $false)))
    Assert-True ($inactive.Count -eq 1) 'inactive path was accepted'
    Assert-True ($unavailable.Count -eq 1) 'unavailable path was accepted'
}

Invoke-Test 'process executable path comparison is case-insensitive but creation is exact' {
    $expected = [pscustomobject]@{ processId = 7; executablePath = 'C:\Frozen\SBMS.exe'; creationDate = 'A' }
    $same = [pscustomobject]@{ processId = 7; executablePath = 'c:\frozen\sbms.EXE'; creationDate = 'A' }
    $reused = [pscustomobject]@{ processId = 7; executablePath = 'c:\frozen\sbms.EXE'; creationDate = 'B' }
    Assert-True (Test-SBMSIssue4ProcessIdentityEqual $expected $same) 'case-only executable path difference failed'
    Assert-True (-not (Test-SBMSIssue4ProcessIdentityEqual $expected $reused)) 'PID reuse was accepted'
}

Invoke-Test 'normal GUI exit does not rollback' {
    $healthy = @((New-PhysicalPath 'DISPLAY-A'))
    $fake = New-TestSupervisorAdapter `
        -Statuses @(
            [pscustomobject]@{ state = 'Running' },
            [pscustomobject]@{ state = 'Exited'; identity = $null }
        ) `
        -DisplaySnapshots @($healthy, $healthy)
    $result = Invoke-SBMSIssue4GuiSupervisor `
        -GuiPath 'c:\frozen\sbms.exe' `
        -BaselineMonitorDevicePaths @('display-a') `
        -Adapter $fake.adapter `
        -PollMilliseconds 100
    Assert-True ([string]$result.outcome -ceq 'ExitedNormally') 'normal exit outcome was wrong'
    Assert-True ($fake.state.rollbacks -eq 0) 'normal GUI exit triggered rollback'
}

Invoke-Test 'GUI exit with missing final physical path rolls back' {
    $healthy = @((New-PhysicalPath 'DISPLAY-A'))
    $lost = @((New-PhysicalPath 'DISPLAY-A' -Active $false))
    $fake = New-TestSupervisorAdapter `
        -Statuses @([pscustomobject]@{ state = 'Exited'; identity = $null }) `
        -DisplaySnapshots @($healthy, $lost)
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSupervisor `
            -GuiPath 'C:\Frozen\SBMS.exe' `
            -BaselineMonitorDevicePaths @('DISPLAY-A') `
            -Adapter $fake.adapter `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'missing final physical path was accepted'
    Assert-True ($fake.state.rollbacks -eq 1) 'missing final physical path did not trigger rollback'
}

Invoke-Test 'GUI exit with final observer failure rolls back' {
    $state = [pscustomobject]@{ reads = 0; rollbacks = 0 }
    $identity = [pscustomobject]@{ processId = 8; executablePath = 'C:\Frozen\SBMS.exe'; creationDate = 'A' }
    $adapter = @{
        GetActiveDisplayPaths = {
            $state.reads++
            if ($state.reads -gt 1) { throw 'final observer failure' }
            @((New-PhysicalPath 'DISPLAY-A'))
        }.GetNewClosure()
        StartGui = { param([string]$Path) [pscustomobject]@{ identity = $identity } }.GetNewClosure()
        GetGuiStatus = { param($Gui) [pscustomobject]@{ state = 'Exited'; identity = $null } }
        Rollback = { $state.rollbacks++; [pscustomobject]@{ state = 'RollbackVerified' } }.GetNewClosure()
        Sleep = { param([int]$Milliseconds) }
        ReportProgress = { param([string]$Message) }
    }
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSupervisor `
            -GuiPath 'C:\Frozen\SBMS.exe' `
            -BaselineMonitorDevicePaths @('DISPLAY-A') `
            -Adapter $adapter `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'final observer failure was accepted'
    Assert-True ($state.rollbacks -eq 1) 'final observer failure did not trigger rollback'
}

Invoke-Test 'physical path loss immediately rolls back exact Run' {
    $healthy = @((New-PhysicalPath 'DISPLAY-A'))
    $lost = @((New-PhysicalPath 'DISPLAY-A' -Available $false))
    $fake = New-TestSupervisorAdapter `
        -Statuses @([pscustomobject]@{ state = 'Running' }) `
        -DisplaySnapshots @($healthy, $lost)
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSupervisor `
            -GuiPath 'C:\Frozen\SBMS.exe' `
            -BaselineMonitorDevicePaths @('DISPLAY-A') `
            -Adapter $fake.adapter `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'physical path loss did not fail the supervised launch'
    Assert-True ($fake.state.rollbacks -eq 1) 'physical path loss did not trigger exactly one rollback'
    Assert-True ($caught.Exception.Message -like '*RollbackVerified*') 'rollback completion was not reported'
}

Invoke-Test 'process identity change fails safe and rolls back' {
    $healthy = @((New-PhysicalPath 'DISPLAY-A'))
    $changed = [pscustomobject]@{
        state = 'Running'
        identity = [pscustomobject]@{
            processId = 4242
            executablePath = 'C:\Frozen\SBMS.exe'
            creationDate = 'different'
        }
    }
    $fake = New-TestSupervisorAdapter -Statuses @($changed) -DisplaySnapshots @($healthy)
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSupervisor `
            -GuiPath 'C:\Frozen\SBMS.exe' `
            -BaselineMonitorDevicePaths @('DISPLAY-A') `
            -Adapter $fake.adapter `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'identity change was not rejected'
    Assert-True ($fake.state.rollbacks -eq 1) 'identity change did not trigger exact rollback'
}

Invoke-Test 'GUI start failure fails safe and rolls back' {
    $state = [pscustomobject]@{ rollbacks = 0 }
    $adapter = @{
        GetActiveDisplayPaths = { @((New-PhysicalPath 'DISPLAY-A')) }
        StartGui = { param([string]$Path) throw 'start failure' }
        GetGuiStatus = { param($Gui) throw 'must not be called' }
        Rollback = { $state.rollbacks++; [pscustomobject]@{ state = 'RollbackVerified' } }.GetNewClosure()
        Sleep = { param([int]$Milliseconds) }
        ReportProgress = { param([string]$Message) }
    }
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSupervisor `
            -GuiPath 'C:\Frozen\SBMS.exe' `
            -BaselineMonitorDevicePaths @('DISPLAY-A') `
            -Adapter $adapter `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'GUI start failure was not rejected'
    Assert-True ($state.rollbacks -eq 1) 'GUI start failure did not trigger exact rollback'
}

Invoke-Test 'display observer error fails safe and rolls back' {
    $state = [pscustomobject]@{ reads = 0; rollbacks = 0 }
    $identity = [pscustomobject]@{ processId = 8; executablePath = 'C:\Frozen\SBMS.exe'; creationDate = 'A' }
    $adapter = @{
        GetActiveDisplayPaths = {
            $state.reads++
            if ($state.reads -gt 1) { throw 'observer failure' }
            @((New-PhysicalPath 'DISPLAY-A'))
        }.GetNewClosure()
        StartGui = { param([string]$Path) [pscustomobject]@{ identity = $identity } }.GetNewClosure()
        GetGuiStatus = { param($Gui) [pscustomobject]@{ state = 'Running'; identity = $identity } }.GetNewClosure()
        Rollback = { $state.rollbacks++; [pscustomobject]@{ state = 'RollbackVerified' } }.GetNewClosure()
        Sleep = { param([int]$Milliseconds) }
        ReportProgress = { param([string]$Message) }
    }
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSupervisor `
            -GuiPath 'C:\Frozen\SBMS.exe' `
            -BaselineMonitorDevicePaths @('DISPLAY-A') `
            -Adapter $adapter `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'observer failure was not rejected'
    Assert-True ($state.rollbacks -eq 1) 'observer failure did not trigger exact rollback'
}

Invoke-Test 'unaccepted rollback state is reported as rollback failure' {
    $adapter = @{
        Rollback = { [pscustomobject]@{ state = 'InstalledAndVerified' } }
        ReportProgress = { param([string]$Message) }
    }
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSafetyRollback -Adapter $adapter -Reason 'test trip'
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'unaccepted rollback state did not fail'
    Assert-True ($caught.Exception.Message -like "*unaccepted state 'InstalledAndVerified'*treated as failed*") 'unaccepted rollback state error was unclear'
}

Invoke-Test 'RollbackPendingReboot is an accepted terminal safety response' {
    $adapter = @{
        Rollback = { [pscustomobject]@{ state = 'rollbackpendingreboot' } }
        ReportProgress = { param([string]$Message) }
    }
    $caught = $null
    try {
        Invoke-SBMSIssue4GuiSafetyRollback -Adapter $adapter -Reason 'test trip'
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'safety trip must still terminate the supervised launch'
    Assert-True ($caught.Exception.Message -like "*rollback completed with state 'rollbackpendingreboot'*") 'pending reboot was not accepted case-insensitively'
}

Invoke-Test 'tampered Gate C payload audit prevents adapter creation and GUI launch' {
    $state = [pscustomobject]@{ adapterCreations = 0; starts = 0 }
    $audit = { throw 'Gate C payload hash mismatch' }
    $factory = {
        param([string]$DisplayConfigPath)
        $state.adapterCreations++
        @{
            StartGui = { $state.starts++ }.GetNewClosure()
        }
    }.GetNewClosure()
    $caught = $null
    try {
        Invoke-SBMSIssue4AuditedGuiLaunch -Audit $audit -CreateSupervisorAdapter $factory -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'tampered audit did not fail'
    Assert-True ($state.adapterCreations -eq 0) 'tampered audit reached payload adapter creation'
    Assert-True ($state.starts -eq 0) 'tampered audit launched the GUI'
}

Invoke-Test 'tampered Gate C manifest audit prevents adapter creation and GUI launch' {
    $state = [pscustomobject]@{ adapterCreations = 0 }
    $audit = { throw 'Gate C plan digest mismatch' }
    $factory = { param([string]$DisplayConfigPath) $state.adapterCreations++ }.GetNewClosure()
    $caught = $null
    try {
        Invoke-SBMSIssue4AuditedGuiLaunch -Audit $audit -CreateSupervisorAdapter $factory -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'tampered manifest audit did not fail'
    Assert-True ($state.adapterCreations -eq 0) 'tampered manifest audit reached adapter creation'
}

Invoke-Test 'unaudited manifest state prevents adapter creation and GUI launch' {
    $state = [pscustomobject]@{ adapterCreations = 0 }
    $audit = {
        [pscustomobject]@{
            state = 'RollbackRequired'
            plan = [pscustomobject]@{ files = @(); baselinePhysicalMonitorPaths = @('DISPLAY-A') }
        }
    }
    $factory = { param([string]$DisplayConfigPath) $state.adapterCreations++ }.GetNewClosure()
    $caught = $null
    try {
        Invoke-SBMSIssue4AuditedGuiLaunch -Audit $audit -CreateSupervisorAdapter $factory -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'invalid audited state did not fail'
    Assert-True ($state.adapterCreations -eq 0) 'invalid audited state reached adapter creation'
}

Invoke-Test 'post-audit payload drift cannot block an exact safety rollback' {
    $global:SBMSIssue4DriftTestState = [pscustomobject]@{
        auditCalls = 0
        displayReads = 0
        rollbacks = 0
        capturedDigest = $null
    }
    $audit = {
        $global:SBMSIssue4DriftTestState.auditCalls++
        if ($global:SBMSIssue4DriftTestState.auditCalls -gt 1) { throw 'simulated post-audit payload drift' }
        [pscustomobject]@{
            state = 'InstalledAndVerified'
            planSha256 = 'AUDITED-DIGEST'
            plan = [pscustomobject]@{
                files = @(
                    [pscustomobject]@{ name = 'SBMS.exe'; path = 'C:\Frozen\SBMS.exe' },
                    [pscustomobject]@{ name = 'SBMS.DisplayConfig.cs'; path = 'C:\Frozen\SBMS.DisplayConfig.cs' }
                )
                baselinePhysicalMonitorPaths = @('DISPLAY-A')
            }
        }
    }.GetNewClosure()
    $factory = {
        param([string]$DisplayConfigPath, [string]$AuditedPlanSha256)
        $global:SBMSIssue4DriftTestState.capturedDigest = $AuditedPlanSha256
        @{
            GetActiveDisplayPaths = {
                $global:SBMSIssue4DriftTestState.displayReads++
                if ($global:SBMSIssue4DriftTestState.displayReads -eq 1) { return @((New-PhysicalPath 'DISPLAY-A')) }
                @((New-PhysicalPath 'DISPLAY-A' -Available $false))
            }.GetNewClosure()
            StartGui = {
                param([string]$Path)
                [pscustomobject]@{
                    identity = [pscustomobject]@{
                        processId = 99
                        executablePath = 'C:\Frozen\SBMS.exe'
                        creationDate = 'A'
                    }
                }
            }
            GetGuiStatus = {
                param($Gui)
                [pscustomobject]@{
                    state = 'Running'
                    identity = [pscustomobject]@{
                        processId = 99
                        executablePath = 'C:\Frozen\SBMS.exe'
                        creationDate = 'A'
                    }
                }
            }
            Rollback = {
                $global:SBMSIssue4DriftTestState.rollbacks++
                [pscustomobject]@{ state = 'RollbackVerified' }
            }.GetNewClosure()
            Sleep = { param([int]$Milliseconds) }
            ReportProgress = { param([string]$Message) }
        }
    }.GetNewClosure()
    $caught = $null
    try {
        Invoke-SBMSIssue4AuditedGuiLaunch `
            -Audit $audit `
            -CreateSupervisorAdapter $factory `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'safety trip did not terminate the launch'
    Assert-True ($global:SBMSIssue4DriftTestState.auditCalls -eq 1) "safety rollback attempted a second full Audit (actual $($global:SBMSIssue4DriftTestState.auditCalls)); error: $($caught.Exception.Message)"
    Assert-True ($global:SBMSIssue4DriftTestState.rollbacks -eq 1) "safety rollback was not invoked exactly once (actual $($global:SBMSIssue4DriftTestState.rollbacks)); error: $($caught.Exception.Message)"
    Assert-True ([string]$global:SBMSIssue4DriftTestState.capturedDigest -ceq 'AUDITED-DIGEST') 'initial audited digest was not frozen into the rollback adapter'
    Remove-Variable -Name SBMSIssue4DriftTestState -Scope Global -ErrorAction SilentlyContinue
}

Invoke-Test 'real pipeline Stop triggers exact rollback once with the audited digest' {
    $shared = [hashtable]::Synchronized(@{
        auditCalls = 0
        rollbacks = 0
        capturedDigest = $null
        rollbackReason = $null
        mutexReleased = $false
        armedSignal = New-Object Threading.ManualResetEventSlim($false)
    })
    $nested = [PowerShell]::Create()
    $async = $null
    try {
        $nested.Runspace.SessionStateProxy.SetVariable('WrapperPath', (Join-Path $PSScriptRoot 'lab\Invoke-SBMSIssue4Lab.ps1'))
        $nested.Runspace.SessionStateProxy.SetVariable('SharedEvidence', $shared)
        [void]$nested.AddScript({
            . $WrapperPath
            $audit = {
                $SharedEvidence.auditCalls++
                [pscustomobject]@{
                    state = 'InstalledAndVerified'
                    planSha256 = 'AUDITED-DIGEST'
                    plan = [pscustomobject]@{
                        files = @(
                            [pscustomobject]@{ name = 'SBMS.exe'; path = 'C:\Frozen\SBMS.exe' },
                            [pscustomobject]@{ name = 'SBMS.DisplayConfig.cs'; path = 'C:\Frozen\SBMS.DisplayConfig.cs' }
                        )
                        baselinePhysicalMonitorPaths = @('DISPLAY-A')
                    }
                }
            }
            $factory = {
                param([string]$DisplayConfigPath, [string]$AuditedPlanSha256)
                $SharedEvidence.capturedDigest = $AuditedPlanSha256
                $identity = [pscustomobject]@{
                    processId = 4242
                    executablePath = 'C:\Frozen\SBMS.exe'
                    creationDate = 'A'
                }
                @{
                    GetActiveDisplayPaths = {
                        [pscustomobject]@{
                            monitorDevicePath = 'DISPLAY-A'
                            active = $true
                            targetAvailable = $true
                            classification = 'physical'
                        }
                    }
                    StartGui = {
                        param([string]$Path)
                        [pscustomobject]@{ identity = $identity }
                    }.GetNewClosure()
                    GetGuiStatus = {
                        param($Gui)
                        [pscustomobject]@{ state = 'Running'; identity = $identity }
                    }.GetNewClosure()
                    Rollback = {
                        $SharedEvidence.rollbacks++
                        [pscustomobject]@{ state = 'RollbackVerified' }
                    }
                    Sleep = {
                        param([int]$Milliseconds)
                        $SharedEvidence.armedSignal.Set()
                        Start-Sleep -Seconds 30
                    }
                    ReportProgress = { param([string]$Message) }
                }
            }
            $lifecycle = New-SBMSIssue4SupervisionLifecycle
            $mutex = New-Object Threading.Mutex($false)
            $mutexHeld = $mutex.WaitOne([TimeSpan]::Zero)
            try {
                Invoke-SBMSIssue4InterruptSafeAuditedGuiLaunch `
                    -Audit $audit `
                    -CreateSupervisorAdapter $factory `
                    -Lifecycle $lifecycle `
                    -PollMilliseconds 100 | Out-Null
            } finally {
                $SharedEvidence.rollbackReason = [string]$lifecycle.rollbackReason
                if ($mutexHeld) {
                    $mutex.ReleaseMutex()
                    $SharedEvidence.mutexReleased = $true
                }
                $mutex.Dispose()
            }
        })
        $async = $nested.BeginInvoke()
        if (-not $shared.armedSignal.Wait([TimeSpan]::FromSeconds(5))) {
            $nested.Stop()
            throw 'nested supervisor did not reach the armed polling state'
        }
        $nested.Stop()
        try {
            $nested.EndInvoke($async) | Out-Null
        } catch {
            # Stop() can surface PipelineStoppedException from EndInvoke().
        }
        $nestedState = [string]$nested.InvocationStateInfo.State
    } finally {
        if ([string]$nested.InvocationStateInfo.State -in @('Running','Stopping')) {
            $nested.Stop()
        }
        $nested.Dispose()
        $shared.armedSignal.Dispose()
    }

    Assert-True ($nestedState -ceq 'Stopped') "real pipeline Stop ended in unexpected state '$nestedState'"
    Assert-True ($shared.auditCalls -eq 1) 'pipeline-stop cleanup performed a second full Audit'
    Assert-True ($shared.rollbacks -eq 1) 'pipeline-stop cleanup did not perform exactly one rollback'
    Assert-True ([string]$shared.capturedDigest -ceq 'AUDITED-DIGEST') 'pipeline-stop rollback did not use the first audited digest'
    Assert-True ([string]$shared.rollbackReason -like '*pipeline stopped*') 'pipeline-stop rollback reason was not retained'
    Assert-True ([bool]$shared.mutexReleased) 'pipeline stop did not unwind through the enclosing mutex finally'
}

Invoke-Test 'existing safety trip is not rolled back twice by interruption finally' {
    $state = [pscustomobject]@{ rollbacks = 0 }
    $audit = { New-TestAuditedManifest }
    $factory = {
        param([string]$DisplayConfigPath, [string]$AuditedPlanSha256)
        $rollbackState = $state
        $fake = New-TestSupervisorAdapter `
            -Statuses @([pscustomobject]@{ state = 'Running' }) `
            -DisplaySnapshots @(
                @((New-PhysicalPath 'DISPLAY-A')),
                @((New-PhysicalPath 'DISPLAY-A' -Available $false))
            )
        $fake.adapter.Rollback = {
            $rollbackState.rollbacks++
            [pscustomobject]@{ state = 'RollbackVerified' }
        }.GetNewClosure()
        $fake.adapter
    }.GetNewClosure()
    $lifecycle = New-SBMSIssue4SupervisionLifecycle
    $caught = $null
    try {
        Invoke-SBMSIssue4InterruptSafeAuditedGuiLaunch `
            -Audit $audit `
            -CreateSupervisorAdapter $factory `
            -Lifecycle $lifecycle `
            -PollMilliseconds 100 | Out-Null
    } catch { $caught = $_ }

    Assert-True ($null -ne $caught) 'safety trip did not terminate supervision'
    Assert-True ($state.rollbacks -eq 1) "outer interruption finally duplicated an existing safety rollback (actual $($state.rollbacks))"
    Assert-True ([bool]$lifecycle.rollbackAttempted) 'safety-trip lifecycle was not marked rollback-attempted'
}

Invoke-Test 'normal supervised exit completes lifecycle without rollback' {
    $state = [pscustomobject]@{ rollbacks = 0 }
    $audit = { New-TestAuditedManifest }
    $factory = {
        param([string]$DisplayConfigPath, [string]$AuditedPlanSha256)
        $fake = New-TestSupervisorAdapter `
            -Statuses @([pscustomobject]@{ state = 'Exited' }) `
            -DisplaySnapshots @(
                @((New-PhysicalPath 'DISPLAY-A')),
                @((New-PhysicalPath 'DISPLAY-A')),
                @((New-PhysicalPath 'DISPLAY-A')),
                @((New-PhysicalPath 'DISPLAY-A'))
            )
        $fake.adapter.Rollback = {
            $state.rollbacks++
            [pscustomobject]@{ state = 'RollbackVerified' }
        }.GetNewClosure()
        $fake.adapter
    }.GetNewClosure()
    $lifecycle = New-SBMSIssue4SupervisionLifecycle
    $result = Invoke-SBMSIssue4InterruptSafeAuditedGuiLaunch `
        -Audit $audit `
        -CreateSupervisorAdapter $factory `
        -Lifecycle $lifecycle `
        -PollMilliseconds 100

    Assert-True ([string]$result.outcome -ceq 'ExitedNormally') 'normal exit outcome changed'
    Assert-True ([bool]$lifecycle.completedNormally) 'normal exit did not complete the lifecycle'
    Assert-True (-not [bool]$lifecycle.rollbackAttempted) 'normal exit attempted rollback'
    Assert-True ($state.rollbacks -eq 0) 'normal exit invoked rollback'
}

Invoke-Test 'graceful host-exit cleanup target is rollback-once' {
    $state = [pscustomobject]@{ rollbacks = 0 }
    $lifecycle = New-SBMSIssue4SupervisionLifecycle
    $rollback = {
        $state.rollbacks++
        [pscustomobject]@{ state = 'RollbackVerified' }
    }.GetNewClosure()
    $lifecycle.rollbackAction = $rollback
    $lifecycle.armed = $true

    Invoke-SBMSIssue4InterruptedSupervisionCleanup -Lifecycle $lifecycle -Reason 'the PowerShell host began a graceful shutdown'
    Invoke-SBMSIssue4InterruptedSupervisionCleanup -Lifecycle $lifecycle -Reason 'duplicate host shutdown notification'

    Assert-True ($state.rollbacks -eq 1) 'graceful host-exit target did not enforce exactly-once rollback'
    Assert-True ([string]$lifecycle.rollbackReason -like '*graceful shutdown*') 'graceful host-exit reason was not retained'
}

Invoke-Test 'graceful host exit runs the armed rollback handler' {
    $evidencePath = Join-Path ([IO.Path]::GetTempPath()) ("sbms-issue4-exit-$([guid]::NewGuid().ToString('N')).txt")
    $wrapperPath = Join-Path $PSScriptRoot 'lab\Invoke-SBMSIssue4Lab.ps1'
    $childExe = if ($PSVersionTable.PSEdition -eq 'Core') {
        Join-Path $PSHOME 'pwsh.exe'
    } else {
        Join-Path $PSHOME 'powershell.exe'
    }
    $childScript = @"
`$ErrorActionPreference = 'Stop'
. '$($wrapperPath.Replace("'", "''"))'
`$global:SBMSIssue4HostExitEvidencePath = '$($evidencePath.Replace("'", "''"))'
`$global:SBMSIssue4HostExitIdentity = [pscustomobject]@{ processId = 7; executablePath = 'C:\Frozen\SBMS.exe'; creationDate = 'A' }
`$audit = {
    [pscustomobject]@{
        state = 'InstalledAndVerified'
        planSha256 = 'AUDITED-DIGEST'
        plan = [pscustomobject]@{
            files = @(
                [pscustomobject]@{ name = 'SBMS.exe'; path = 'C:\Frozen\SBMS.exe' },
                [pscustomobject]@{ name = 'SBMS.DisplayConfig.cs'; path = 'C:\Frozen\SBMS.DisplayConfig.cs' }
            )
            baselinePhysicalMonitorPaths = @('DISPLAY-A')
        }
    }
}
`$factory = {
    param([string]`$DisplayConfigPath, [string]`$AuditedPlanSha256)
    @{
        GetActiveDisplayPaths = {
            [pscustomobject]@{ monitorDevicePath = 'DISPLAY-A'; active = `$true; targetAvailable = `$true; classification = 'physical' }
        }
        StartGui = { param([string]`$Path) [pscustomobject]@{ identity = `$global:SBMSIssue4HostExitIdentity } }
        GetGuiStatus = { param(`$Gui) [pscustomobject]@{ state = 'Running'; identity = `$global:SBMSIssue4HostExitIdentity } }
        Rollback = {
            [IO.File]::WriteAllText(`$global:SBMSIssue4HostExitEvidencePath, 'RollbackVerified', (New-Object Text.UTF8Encoding(`$false)))
            [pscustomobject]@{ state = 'RollbackVerified' }
        }
        Sleep = { param([int]`$Milliseconds) exit 0 }
        ReportProgress = { param([string]`$Message) }
    }
}
`$lifecycle = New-SBMSIssue4SupervisionLifecycle
Invoke-SBMSIssue4InterruptSafeAuditedGuiLaunch -Audit `$audit -CreateSupervisorAdapter `$factory -Lifecycle `$lifecycle -PollMilliseconds 100 | Out-Null
"@
    try {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childScript))
        & $childExe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded 2>$null | Out-Null
        $childExit = $LASTEXITCODE
        Assert-True ($childExit -eq 0) "graceful-exit child failed with exit code $childExit"
        Assert-True (Test-Path -LiteralPath $evidencePath -PathType Leaf) 'graceful host exit did not unwind through the armed rollback finally'
        $evidence = [IO.File]::ReadAllText($evidencePath, [Text.Encoding]::UTF8)
        Assert-True ([string]$evidence -ceq 'RollbackVerified') 'graceful host-exit rollback evidence was wrong'
    } finally {
        Remove-Item -LiteralPath $evidencePath -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Test 'supervisor mutex name is exact per Run and stable' {
    $id = [guid]'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE'
    $name = Get-SBMSIssue4SupervisorMutexName -Id $id
    Assert-True ([string]$name -ceq 'Global\SBMSIssue4GuiSupervisor_aaaaaaaabbbbccccddddeeeeeeeeeeee') 'mutex name contract changed'
}

Invoke-Test 'UAC poll forwarding and nonblocking per-Run mutex are statically pinned' {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'lab\Invoke-SBMSIssue4Lab.ps1') -Raw -Encoding UTF8
    Assert-True ($source -match '\$SupervisionPollWasBound\s*=\s*\$PSBoundParameters\.ContainsKey') 'top-level poll binding was not preserved'
    Assert-True ($source -match 'if\s*\(\$SupervisionPollWasBound\)[\s\S]*-SupervisionPollMilliseconds') 'elevated invocation does not forward an explicitly bound poll'
    Assert-True ($source -match '\$supervisorMutex\.WaitOne\(\[TimeSpan\]::Zero\)') 'duplicate supervisor mutex acquisition is not nonblocking'
    Assert-True ($source -match 'finally\s*\{[\s\S]*\$supervisorMutex\.ReleaseMutex\(\)[\s\S]*\$supervisorMutex\.Dispose\(\)') 'supervisor mutex release is not protected by finally'
    Assert-True ($source -match 'Invoke-SBMSGateC\s+-Phase\s+Audit\s+-RunId\s+\$selectedRunId') 'SupervisedLaunch does not use Gate C Audit'
    Assert-True ($source -notmatch '\$auditedForRollback') 'safety rollback still performs a second full Audit'
    Assert-True ($source -match 'Rollback/\$AuditedPlanSha256') 'safety rollback acknowledgement does not use the frozen audited digest'
    Assert-True ($source -match 'finally\s*\{[\s\S]*Invoke-SBMSIssue4InterruptedSupervisionCleanup') 'pipeline-stopping cleanup is not protected by finally'
    Assert-True ($source -match 'graceful host `exit`[\s\S]*finally') 'graceful PowerShell host-exit unwind contract is not documented'
    Assert-True ($source -match 'force-killing powershell\.exe[\s\S]*boot watchdog') 'hard-kill watchdog recovery boundary is not documented'
}

Invoke-Test 'known failing TestSigning hardware Start is suspended before module import or mutation' {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'lab\Invoke-SBMSIssue4Lab.ps1') -Raw -Encoding UTF8
    $block = [regex]::Match(
        $source,
        "if \(\`$Phase -eq 'Start'\) \{\s*throw 'Issue #4 TestSigning hardware Start is suspended[\s\S]*?\}\s*Import-Module"
    )
    Assert-True $block.Success 'the known failing TestSigning Start path is not fail-closed before hardware module import'
    Assert-True ($block.Value -match '7924eb2e-f15d-4c20-8a56-7ff9a59719dc') 'the suspension is not bound to the authoritative failed Gate B Run'
    Assert-True ($block.Value -match 'Issue #18') 'the suspension does not identify the production-signing unlock condition'
}

Write-Host "Tests: $($script:Passed) passed, $($script:Failed) failed"
if ($script:Failed -ne 0) { exit 1 }
