[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$modulePath = Join-Path $root 'lab\SBMS.GateC.psm1'
Import-Module -Name $modulePath -Force -ErrorAction Stop

$script:Passed = 0
$script:Failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw "$Message Expected=[$Expected] Actual=[$Actual]"
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "$Message Unexpected error: $($_.Exception.Message)"
        }
        return
    }
    throw "$Message No error was thrown."
}

function Invoke-TestCase {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action
        $script:Passed++
        Write-Host "[PASS] $Name"
    } catch {
        $script:Failed++
        Write-Host "[FAIL] $Name"
        Write-Host "       $($_.Exception.Message)"
        Write-Host "       $($_.ScriptStackTrace)"
    }
}

function New-FakeAdapter {
    param([ValidateRange(1, 2)][int]$PackagesAdded = 1)

    $state = @{
        Packages = New-Object Collections.ArrayList
        Bindings = @{}
        Mutations = New-Object Collections.Generic.List[string]
        Calls = New-Object Collections.Generic.List[string]
        PackagesAdded = $PackagesAdded
        InstalledPublishedNames = New-Object Collections.Generic.List[string]
        RemoveDeviceExitCode = 0
        DeleteDriverExitCode = 0
        RetainDeviceOnRemove = $false
        RetainPackageOnDelete = $false
        AddExternalBindingOnStage = $false
        DropPhysicalPathsOnHost = $false
        AddUnexpectedBindingOnScan = $false
        DeviceQueryFailure = $false
        DropPhysicalPathsOnStage = $false
        PhysicalPaths = New-Object Collections.ArrayList
    }

    $testAdministrator = {
        $state.Calls.Add('TestAdministrator')
        return $true
    }.GetNewClosure()
    $getDriverPackages = {
        $state.Calls.Add('GetDriverPackages')
        return @($state.Packages)
    }.GetNewClosure()
    $addDriver = {
        param([string]$InfPath)
        $state.Calls.Add("AddDriver:$InfPath")
        $state.Mutations.Add('AddDriver')
        for ($i = 1; $i -le $state.PackagesAdded; $i++) {
            $publishedName = "oem$($i + 40).inf"
            [void]$state.Packages.Add([pscustomobject]@{
                publishedName = $publishedName
                originalName = 'iddsampledriver.inf'
                version = '1.0.0.0'
                provider = 'SBMS test'
                className = 'Display'
            })
            $state.InstalledPublishedNames.Add($publishedName)
            if ([bool]$state.AddExternalBindingOnStage) {
                $state.Bindings['SWD\EXTERNAL\PREBOUND'] = [pscustomobject]@{
                    exists = $true
                    instanceId = 'SWD\EXTERNAL\PREBOUND'
                    driverInf = $publishedName
                    hasProblem = $false
                    status = 'OK'
                }
            }
        }
        if ([bool]$state.DropPhysicalPathsOnStage) {
            $state.PhysicalPaths.Clear()
        }
        [pscustomobject]@{ ExitCode = 0; StdOut = 'fake add-driver'; StdErr = '' }
    }.GetNewClosure()
    $startVerificationHost = {
        param($Manifest)
        $state.Calls.Add('StartVerificationHost')
        $state.Mutations.Add('StartVerificationHost')
        $publishedName = [string]$state.InstalledPublishedNames[0]
        foreach ($instanceId in @($Manifest.plan.expectedDeviceIds)) {
            $state.Bindings[[string]$instanceId] = [pscustomobject]@{
                exists = $true
                instanceId = [string]$instanceId
                driverInf = $publishedName
                hasProblem = $false
                status = 'OK'
            }
        }
        if ([bool]$state.DropPhysicalPathsOnHost) {
            $state.PhysicalPaths.Clear()
        }
        [pscustomobject]@{
            process = [pscustomobject]@{ Id = 4242 }
            output = @("device_host=ready run_id=$($Manifest.runId)")
            arguments = @()
        }
    }.GetNewClosure()
    $stopVerificationHost = {
        param($HostResult)
        $state.Calls.Add('StopVerificationHost')
        $state.Mutations.Add('StopVerificationHost')
    }.GetNewClosure()
    $scanDevices = {
        $state.Calls.Add('ScanDevices')
        $state.Mutations.Add('ScanDevices')
        if ([bool]$state.AddUnexpectedBindingOnScan) {
            $unexpectedId = 'SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER2'
            $state.Bindings[$unexpectedId] = [pscustomobject]@{
                exists = $true
                instanceId = $unexpectedId
                driverInf = [string]$state.InstalledPublishedNames[0]
                hasProblem = $false
                status = 'OK'
            }
        }
        [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
    }.GetNewClosure()
    $getDeviceBinding = {
        param([string]$InstanceId)
        $state.Calls.Add("GetDeviceBinding:$InstanceId")
        if ([bool]$state.DeviceQueryFailure) {
            throw 'fake PnP infrastructure failure'
        }
        if ($state.Bindings.ContainsKey($InstanceId)) { return $state.Bindings[$InstanceId] }
        [pscustomobject]@{ exists = $false; instanceId = $InstanceId; driverInf = ''; hasProblem = $false; status = 'Absent' }
    }.GetNewClosure()
    $getBindingsByPublishedInf = {
        param([string]$PublishedName)
        $state.Calls.Add("GetBindingsByPublishedInf:$PublishedName")
        @(
            foreach ($entry in $state.Bindings.GetEnumerator()) {
                if ([bool]$entry.Value.exists -and [string]$entry.Value.driverInf -ieq $PublishedName) {
                    [string]$entry.Key
                }
            }
        )
    }.GetNewClosure()
    $getActiveDisplayPaths = {
        $state.Calls.Add('GetActiveDisplayPaths')
        @($state.PhysicalPaths)
    }.GetNewClosure()
    $removeDevice = {
        param([string]$InstanceId)
        $state.Calls.Add("RemoveDevice:$InstanceId")
        $state.Mutations.Add("RemoveDevice:$InstanceId")
        if (-not [bool]$state.RetainDeviceOnRemove) {
            [void]$state.Bindings.Remove($InstanceId)
        }
        [pscustomobject]@{ ExitCode = [int]$state.RemoveDeviceExitCode; StdOut = ''; StdErr = '' }
    }.GetNewClosure()
    $deleteDriver = {
        param([string]$PublishedName)
        $state.Calls.Add("DeleteDriver:$PublishedName")
        $state.Mutations.Add("DeleteDriver:$PublishedName")
        if (-not [bool]$state.RetainPackageOnDelete) {
            for ($i = $state.Packages.Count - 1; $i -ge 0; $i--) {
                if ([string]$state.Packages[$i].publishedName -ieq $PublishedName) {
                    $state.Packages.RemoveAt($i)
                }
            }
        }
        [pscustomobject]@{ ExitCode = [int]$state.DeleteDriverExitCode; StdOut = ''; StdErr = '' }
    }.GetNewClosure()
    $getProductProcesses = {
        param([string[]]$ExactPaths)
        $state.Calls.Add('GetProductProcesses')
        @()
    }.GetNewClosure()
    $stopProcess = {
        param([int]$ProcessId)
        $state.Calls.Add("StopProcess:$ProcessId")
        $state.Mutations.Add("StopProcess:$ProcessId")
        [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
    }.GetNewClosure()

    $adapter = @{
        IsReal = $false
        TestAdministrator = $testAdministrator
        GetDriverPackages = $getDriverPackages
        AddDriver = $addDriver
        StartVerificationHost = $startVerificationHost
        StopVerificationHost = $stopVerificationHost
        ScanDevices = $scanDevices
        GetDeviceBinding = $getDeviceBinding
        GetBindingsByPublishedInf = $getBindingsByPublishedInf
        GetActiveDisplayPaths = $getActiveDisplayPaths
        RemoveDevice = $removeDevice
        DeleteDriver = $deleteDriver
        GetProductProcesses = $getProductProcesses
        StopProcess = $stopProcess
    }
    [pscustomobject]@{ Adapter = $adapter; State = $state }
}

function New-TestContext {
    param(
        [ValidateRange(1, 2)][int]$PackagesAdded = 1,
        [ValidateRange(1, 3)][int]$VerificationDeviceCount = 1,
        [switch]$SkipInitialize
    )

    $runId = [guid]::NewGuid()
    $runRoot = Join-Path ([IO.Path]::GetTempPath()) ('SBMS-GateC-Test-' + [guid]::NewGuid().ToString('N'))
    $runDirectory = Join-Path $runRoot $runId.ToString()
    $driverDirectory = Join-Path $runRoot 'source-driver'
    $productDirectory = Join-Path $runRoot 'source-product'
    $gateADirectory = Join-Path $runDirectory 'gate-a'
    foreach ($directory in @($driverDirectory, $productDirectory, $gateADirectory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [IO.File]::WriteAllText(
        (Join-Path $driverDirectory 'IddSampleDriver.inf'),
        "[Version]`r`nDriverVer=06/30/2026,1.0.0.0`r`n",
        (New-Object Text.UTF8Encoding($false)))
    foreach ($name in @('IddSampleDriver.dll', 'iddsampledriver.cat')) {
        [IO.File]::WriteAllText((Join-Path $driverDirectory $name), "fake-$name", (New-Object Text.UTF8Encoding($false)))
    }
    foreach ($name in @('SBMS.exe', 'SBMSNative.exe', 'SBMSDeviceHost.exe')) {
        [IO.File]::WriteAllText((Join-Path $productDirectory $name), "fake-$name", (New-Object Text.UTF8Encoding($false)))
    }

    $stableDigest = ('A' * 64)
    $gateA = [pscustomobject][ordered]@{
        schemaVersion = 4
        contractVersion = 'gate-a/2'
        runId = $runId.ToString()
        status = 'PASS'
        stableDigest = $stableDigest
    }
    [IO.File]::WriteAllText(
        (Join-Path $gateADirectory 'manifest.json'),
        ($gateA | ConvertTo-Json -Depth 5),
        (New-Object Text.UTF8Encoding($false)))
    $gateAPhysicalPath = [pscustomobject][ordered]@{
        adapterLuid = '111111111'
        targetId = 7
        monitorDevicePath = '\\?\DISPLAY#SBMSPHYSICAL#BASELINE'
        active = $true
        targetAvailable = $true
        classification = 'physical'
    }
    $gateACurrentEvidence = [pscustomobject][ordered]@{
        displayConfig = [pscustomobject][ordered]@{
            status = 'Captured'
            data = [pscustomobject][ordered]@{ activePaths = @($gateAPhysicalPath) }
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $gateADirectory 'current-evidence.json'),
        ($gateACurrentEvidence | ConvertTo-Json -Depth 8),
        (New-Object Text.UTF8Encoding($false)))

    $fake = New-FakeAdapter -PackagesAdded $PackagesAdded
    $currentBootPhysicalPath = [pscustomobject][ordered]@{
        adapterLuid = '222222222'
        targetId = 9
        monitorDevicePath = '\\?\DISPLAY#SBMSPHYSICAL#BASELINE'
        active = $true
        targetAvailable = $true
        classification = 'physical'
    }
    [void]$fake.State.PhysicalPaths.Add($currentBootPhysicalPath)
    $context = [pscustomobject]@{
        RunId = $runId
        RunRoot = $runRoot
        RunDirectory = $runDirectory
        DriverDirectory = $driverDirectory
        ProductDirectory = $productDirectory
        Manifest = $null
        Adapter = $fake.Adapter
        State = $fake.State
    }
    if (-not $SkipInitialize) {
        $initializeParameters = @{
            RunId = $runId
            RunDirectory = $runDirectory
            DriverPackagePath = $driverDirectory
            ProductRoot = $productDirectory
            Adapter = $fake.Adapter
        }
        if ($VerificationDeviceCount -ne 1) {
            $initializeParameters.VerificationDeviceCount = $VerificationDeviceCount
        }
        $context.Manifest = Initialize-SBMSGateC @initializeParameters
    }
    $context
}

function Get-Acknowledgement {
    param($Context, [string]$Phase)
    "SBMS-GATE-C/$($Context.RunId.ToString())/$Phase/$($Context.Manifest.planSha256)"
}

function Save-TestManifest {
    param($Context, $Manifest)
    $path = Join-Path (Join-Path $Context.RunDirectory 'gate-c') 'manifest.json'
    [IO.File]::WriteAllText(
        $path,
        ($Manifest | ConvertTo-Json -Depth 20),
        (New-Object Text.UTF8Encoding($false)))
}

function Remove-TestContext {
    param($Context)
    if ($null -eq $Context) { return }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath([string]$Context.RunRoot)
    if (-not $target.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($target) -notlike 'SBMS-GateC-Test-*') {
        throw "Refusing to remove unexpected test path: $target"
    }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
}

Invoke-TestCase 'default plan owns exactly one device and rolls it back' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $installed = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        Assert-Equal 'InstalledAndVerified' $installed.state 'Install did not reach its verified state.'
        Assert-Equal 'oem41.inf' $installed.ownedPublishedName 'Install captured the wrong package.'
        Assert-Equal 1 @($installed.ownedDeviceIds).Count 'Default Install did not own exactly one expected device.'
        Assert-Equal 1 ([int]$installed.plan.verificationDeviceCount) 'Default verification count was not one.'

        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Rollback did not verify.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Rollback left the owned package installed.'
        Assert-Equal 0 $ctx.State.Bindings.Count 'Rollback left an owned device binding.'
        Assert-Equal 1 @($ctx.State.Mutations | Where-Object { $_ -eq 'DeleteDriver:oem41.inf' }).Count 'Rollback did not delete exactly one owned package.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'Initialize rejects an exact stale expected device before filesystem mutation' {
    $ctx = $null
    try {
        $ctx = New-TestContext -SkipInitialize
        $expectedId = 'SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER3'
        $ctx.State.Bindings[$expectedId] = [pscustomobject]@{
            exists = $true
            instanceId = $expectedId
            driverInf = ''
            hasProblem = $true
            status = 'Unknown'
        }
        Assert-Throws -Action {
            Initialize-SBMSGateC -RunId $ctx.RunId -RunDirectory $ctx.RunDirectory `
                -DriverPackagePath $ctx.DriverDirectory -ProductRoot $ctx.ProductDirectory -Adapter $ctx.Adapter
        } -Pattern 'refuses stale expected device' -Message 'Initialize accepted a stale expected SWD device.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $ctx.RunDirectory 'gate-c'))) 'Initialize mutated the gate-c filesystem before rejecting the stale device.'
        Assert-Equal 0 $ctx.State.Mutations.Count 'Initialize stale-device rejection reached a mutation adapter.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'Install rechecks and rejects a stale expected device before mutation' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $expectedId = 'SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER2'
        $ctx.State.Bindings[$expectedId] = [pscustomobject]@{
            exists = $true
            instanceId = $expectedId
            driverInf = ''
            hasProblem = $true
            status = 'Unknown'
        }
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'refuses stale expected device' -Message 'Install accepted a stale expected SWD device.'
        Assert-Equal 0 $ctx.State.Mutations.Count 'Install stale-device rejection reached a mutation adapter.'
        Assert-Equal 0 @($ctx.State.Calls | Where-Object { $_ -like 'AddDriver:*' }).Count 'Install staged the driver before rejecting the stale device.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'PnP infrastructure failure is never treated as device absence' {
    foreach ($phase in @('Initialize', 'Install')) {
        $ctx = $null
        try {
            $ctx = New-TestContext -SkipInitialize:($phase -eq 'Initialize')
            $ctx.State.DeviceQueryFailure = $true
            if ($phase -eq 'Initialize') {
                Assert-Throws -Action {
                    Initialize-SBMSGateC -RunId $ctx.RunId -RunDirectory $ctx.RunDirectory `
                        -DriverPackagePath $ctx.DriverDirectory -ProductRoot $ctx.ProductDirectory -Adapter $ctx.Adapter
                } -Pattern 'fake PnP infrastructure failure' -Message 'Initialize folded a PnP query failure into absence.'
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $ctx.RunDirectory 'gate-c'))) 'Initialize wrote gate-c state after PnP query failure.'
            } else {
                Assert-Throws -Action {
                    Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                        -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
                } -Pattern 'fake PnP infrastructure failure' -Message 'Install folded a PnP query failure into absence.'
                Assert-Equal 0 @($ctx.State.Calls | Where-Object { $_ -like 'AddDriver:*' }).Count 'Install staged a driver after PnP query failure.'
            }
            Assert-Equal 0 $ctx.State.Mutations.Count "$phase PnP query failure reached a mutation adapter."
            Assert-Equal 0 $ctx.State.Packages.Count "$phase PnP query failure changed the fake Driver Store."
        } finally { Remove-TestContext $ctx }
    }
}

Invoke-TestCase 'unrecoverable physical path fails before Driver Store mutation' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $ctx.State.PhysicalPaths.Clear()
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'cannot uniquely resolve' -Message 'Install staged a driver before resolving its current-boot physical baseline.'
        Assert-Equal 0 @($ctx.State.Calls | Where-Object { $_ -like 'AddDriver:*' }).Count 'Physical baseline failure reached AddDriver.'
        Assert-Equal 0 $ctx.State.Mutations.Count 'Physical baseline failure reached a mutation adapter.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Physical baseline failure changed the fake Driver Store.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'staged published name with any binding fails before host start' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $ctx.State.AddExternalBindingOnStage = $true
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'staged package already has device bindings' -Message 'Install accepted a staged package with an existing binding.'
        Assert-Equal 0 @($ctx.State.Mutations | Where-Object { $_ -eq 'StartVerificationHost' }).Count 'Gate C started the host after detecting a published-name binding.'
        $readback = Read-SBMSGateCManifest -RunDirectory $ctx.RunDirectory
        Assert-Equal 'RollbackRequired' $readback.state 'Existing published-name binding did not require rollback.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'physical path drift after stage fails before host and rolls back' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $ctx.State.DropPhysicalPathsOnStage = $true
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'physical recovery path disappeared' -Message 'Install ignored physical path drift between baseline capture and host start.'
        Assert-Equal 1 @($ctx.State.Mutations | Where-Object { $_ -eq 'AddDriver' }).Count 'Stage-drift test did not reach driver staging.'
        Assert-Equal 0 @($ctx.State.Mutations | Where-Object { $_ -eq 'StartVerificationHost' }).Count 'Gate C started the host after stage-time physical drift.'
        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Stage-time physical drift could not roll back the package.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Stage-time physical drift rollback left the package.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'missing physical baseline after host fails and remains rollback-capable' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $ctx.State.DropPhysicalPathsOnHost = $true
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'physical recovery path disappeared' -Message 'Install verified after losing its physical recovery path.'
        Assert-Equal 1 @($ctx.State.Mutations | Where-Object { $_ -eq 'StartVerificationHost' }).Count 'Physical-path test never reached the post-host gate.'
        $failed = Read-SBMSGateCManifest -RunDirectory $ctx.RunDirectory
        Assert-Equal 'RollbackRequired' $failed.state 'Physical-path loss did not persist RollbackRequired.'
        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Physical-path failure could not execute exact rollback.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Physical-path rollback left the staged package.'
        Assert-Equal 0 $ctx.State.Bindings.Count 'Physical-path rollback left the verification device.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'cross-reboot adapter identity changes but monitor path remains authorized' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        Assert-Equal '\\?\DISPLAY#SBMSPHYSICAL#BASELINE' ([string]$ctx.Manifest.plan.baselinePhysicalMonitorPaths[0]) 'Plan did not freeze the stable monitor path.'
        Assert-True ($null -eq $ctx.Manifest.plan.PSObject.Properties['baselinePhysicalPaths']) 'Plan retained reboot-local adapter identity.'
        $installed = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        Assert-Equal 'InstalledAndVerified' $installed.state 'Install rejected the same monitor after reboot-local LUID/target change.'
        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Cross-reboot identity test did not roll back.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'unplanned reserved device binding after scan fails and rolls back' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $ctx.State.AddUnexpectedBindingOnScan = $true
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'bindings do not exactly match' -Message 'Install accepted an unplanned reserved device binding.'
        $failed = Read-SBMSGateCManifest -RunDirectory $ctx.RunDirectory
        Assert-Equal 'RollbackRequired' $failed.state 'Unexpected reserved binding did not require rollback.'
        Assert-Equal 2 @($failed.ownedDeviceIds).Count 'Rollback ownership did not retain both transaction-safe reserved bindings.'
        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Unexpected reserved binding could not be rolled back.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Unexpected binding rollback left the staged package.'
        Assert-Equal 0 $ctx.State.Bindings.Count 'Unexpected binding rollback left a reserved device.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'real adapter stages the package without install semantics' {
    $source = Get-Content -LiteralPath $modulePath -Raw -Encoding UTF8
    $addDriverBlock = [regex]::Match(
        $source,
        '(?s)AddDriver\s*=\s*\{(?<body>.*?)\r?\n\s*\}\r?\n\s*ScanDevices\s*=').Groups['body'].Value
    Assert-True ($addDriverBlock -match "'/add-driver'") 'Real AddDriver no longer stages through pnputil.'
    Assert-True ($addDriverBlock -notmatch "'/install'") 'Real AddDriver still uses implicit install semantics.'
}

Invoke-TestCase 'canonical digest is fixed across PowerShell runtimes and angle brackets' {
    $sample = [pscustomobject][ordered]@{
        publishedName = 'oem56.inf'
        originalName = 'iddsampledriver.inf'
        version = '22.19.49.737'
        provider = '<Your manufacturer name>'
        className = 'Display'
    }
    $module = Get-Module -Name 'SBMS.GateC'
    $digest = & $module { param($Value) Get-SBMSGateCPlanDigest -Plan $Value } $sample
    Assert-Equal '0721CD581A1C38A4C54E72FAEA42492657CA719E9A7F80A37B53EAB0FD49B83F' $digest 'Canonical ownership digest changed across runtime or serialization rules.'
}

Invoke-TestCase 'canonical digest defines object order dictionary keys arrays and scalar types' {
    $firstMap = @{}
    $firstMap['z'] = 2
    $firstMap['a'] = 1
    $secondMap = @{}
    $secondMap['a'] = 1
    $secondMap['z'] = 2
    $first = [pscustomobject][ordered]@{
        name = 'canonical'
        map = $firstMap
        items = @($null, $true, 42)
    }
    $equivalent = [pscustomobject][ordered]@{
        name = 'canonical'
        map = $secondMap
        items = @($null, $true, [long]42)
    }
    $reordered = [pscustomobject][ordered]@{
        items = @($null, $true, 42)
        map = $secondMap
        name = 'canonical'
    }
    $module = Get-Module -Name 'SBMS.GateC'
    $firstDigest = & $module { param($Value) Get-SBMSGateCPlanDigest -Plan $Value } $first
    $equivalentDigest = & $module { param($Value) Get-SBMSGateCPlanDigest -Plan $Value } $equivalent
    $reorderedDigest = & $module { param($Value) Get-SBMSGateCPlanDigest -Plan $Value } $reordered
    Assert-Equal $firstDigest $equivalentDigest 'Dictionary insertion order or integer runtime type changed the canonical digest.'
    Assert-True ($firstDigest -cne $reorderedDigest) 'Canonical digest ignored object property order.'
}

Invoke-TestCase 'wrong acknowledgement performs zero mutations' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement 'wrong'
        } -Pattern 'acknowledgement mismatch' -Message 'Gate C accepted a wrong acknowledgement.'
        Assert-Equal 0 $ctx.State.Mutations.Count 'Wrong acknowledgement reached a mutation adapter.'
        $readback = Read-SBMSGateCManifest -RunDirectory $ctx.RunDirectory
        Assert-Equal 'Planned' $readback.state 'Wrong acknowledgement changed manifest state.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'payload hash drift fails before adapter mutation' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $frozenDll = [string](@($ctx.Manifest.plan.files | Where-Object { $_.name -eq 'IddSampleDriver.dll' })[0].path)
        [IO.File]::AppendAllText($frozenDll, 'drift', (New-Object Text.UTF8Encoding($false)))
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Audit -RunId $ctx.RunId -RunRoot $ctx.RunRoot -Adapter $ctx.Adapter
        } -Pattern 'payload hash drifted' -Message 'Gate C accepted a drifted frozen payload.'
        Assert-Equal 0 $ctx.State.Mutations.Count 'Payload drift reached a mutation adapter.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'multiple new packages fail closed without starting verification host' {
    $ctx = $null
    try {
        $ctx = New-TestContext -PackagesAdded 2
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        } -Pattern 'found 2 matching new packages' -Message 'Gate C guessed ownership among multiple packages.'
        Assert-Equal 0 @($ctx.State.Mutations | Where-Object { $_ -eq 'StartVerificationHost' }).Count 'Gate C started the host after ambiguous ownership.'
        $readback = Read-SBMSGateCManifest -RunDirectory $ctx.RunDirectory
        Assert-Equal 'RollbackRequired' $readback.state 'Ambiguous ownership did not demand rollback.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'external binding blocks package and device deletion' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $installed = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        $ctx.State.Bindings['SWD\EXTERNAL\DISPLAY'] = [pscustomobject]@{
            exists = $true
            instanceId = 'SWD\EXTERNAL\DISPLAY'
            driverInf = [string]$installed.ownedPublishedName
            hasProblem = $false
            status = 'OK'
        }
        $removeBefore = @($ctx.State.Mutations | Where-Object { $_ -like 'RemoveDevice:*' }).Count
        $deleteBefore = @($ctx.State.Mutations | Where-Object { $_ -like 'DeleteDriver:*' }).Count
        Assert-Throws -Action {
            Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        } -Pattern 'external devices use' -Message 'Gate C deleted a package with an external binding.'
        Assert-Equal $removeBefore @($ctx.State.Mutations | Where-Object { $_ -like 'RemoveDevice:*' }).Count 'External binding did not block device deletion.'
        Assert-Equal $deleteBefore @($ctx.State.Mutations | Where-Object { $_ -like 'DeleteDriver:*' }).Count 'External binding did not block package deletion.'
        Assert-Equal 1 $ctx.State.Packages.Count 'External binding path removed the package.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'verified rollback is idempotent' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $null = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        $first = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $first.state 'First rollback did not verify.'
        $mutationCount = $ctx.State.Mutations.Count
        $second = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $second.state 'Second rollback changed terminal state.'
        Assert-Equal $mutationCount $ctx.State.Mutations.Count 'Second rollback repeated a mutation.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase 'tampered published-name or ownership record performs zero delete mutations' {
    foreach ($mode in @('PublishedName', 'Ownership')) {
        $ctx = $null
        try {
            $ctx = New-TestContext
            $installed = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
            if ($mode -eq 'PublishedName') {
                $installed.ownedPublishedName = 'oem999.inf'
            } else {
                $installed.ownership.provider = 'tampered provider'
            }
            Save-TestManifest -Context $ctx -Manifest $installed
            $removeBefore = @($ctx.State.Mutations | Where-Object { $_ -like 'RemoveDevice:*' }).Count
            $deleteBefore = @($ctx.State.Mutations | Where-Object { $_ -like 'DeleteDriver:*' }).Count
            Assert-Throws -Action {
                Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                    -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
            } -Pattern 'ownership record is invalid' -Message "Gate C accepted tampered $mode ownership."
            Assert-Equal $removeBefore @($ctx.State.Mutations | Where-Object { $_ -like 'RemoveDevice:*' }).Count "$mode tamper reached remove-device."
            Assert-Equal $deleteBefore @($ctx.State.Mutations | Where-Object { $_ -like 'DeleteDriver:*' }).Count "$mode tamper reached delete-driver."
            Assert-Equal 1 $ctx.State.Packages.Count "$mode tamper removed the installed package."
        } finally { Remove-TestContext $ctx }
    }
}

Invoke-TestCase 'non-module payload drift still permits exact ownership rollback' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $installed = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        $frozenNative = [string](@($installed.plan.files | Where-Object { $_.name -eq 'SBMSNative.exe' })[0].path)
        [IO.File]::AppendAllText($frozenNative, 'post-install drift', (New-Object Text.UTF8Encoding($false)))
        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Payload drift blocked exact ownership rollback.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Payload-drift rollback left the exact owned package.'
        Assert-Equal 1 @($ctx.State.Mutations | Where-Object { $_ -eq 'DeleteDriver:oem41.inf' }).Count 'Payload-drift rollback did not delete exactly the owned package.'
    } finally { Remove-TestContext $ctx }
}

Invoke-TestCase '3010 retained device or package converges after reboot readback is absent' {
    foreach ($stage in @('Device', 'Package')) {
        $ctx = $null
        try {
            $ctx = New-TestContext
            $null = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
            if ($stage -eq 'Device') {
                $ctx.State.RemoveDeviceExitCode = 3010
                $ctx.State.RetainDeviceOnRemove = $true
            } else {
                $ctx.State.DeleteDriverExitCode = 3010
                $ctx.State.RetainPackageOnDelete = $true
            }

            $pending = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
            Assert-Equal 'RollbackPendingReboot' $pending.state "$stage 3010 did not enter pending-reboot state."
            Assert-True ([bool]$pending.rebootRequired) "$stage 3010 did not persist rebootRequired."

            if ($stage -eq 'Device') {
                $ctx.State.Bindings.Clear()
                $ctx.State.RemoveDeviceExitCode = 0
                $ctx.State.RetainDeviceOnRemove = $false
            } else {
                $ctx.State.Packages.Clear()
                $ctx.State.DeleteDriverExitCode = 0
                $ctx.State.RetainPackageOnDelete = $false
            }
            $verified = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
                -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
            Assert-Equal 'RollbackVerified' $verified.state "$stage post-reboot absence did not close rollback."
            Assert-Equal 0 $ctx.State.Packages.Count "$stage post-reboot convergence left a package."
            Assert-Equal 0 $ctx.State.Bindings.Count "$stage post-reboot convergence left a binding."
        } finally { Remove-TestContext $ctx }
    }
}

Write-Host ""
Write-Host "SBMS Gate C tests: passed=$script:Passed failed=$script:Failed"
if ($script:Failed -ne 0) { exit 1 }
