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
                originalName = 'IddSampleDriver.inf'
                version = '1.0.0.0'
                provider = 'SBMS test'
                className = 'Display'
            })
            $state.InstalledPublishedNames.Add($publishedName)
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
        [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
    }.GetNewClosure()
    $getDeviceBinding = {
        param([string]$InstanceId)
        $state.Calls.Add("GetDeviceBinding:$InstanceId")
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
        RemoveDevice = $removeDevice
        DeleteDriver = $deleteDriver
        GetProductProcesses = $getProductProcesses
        StopProcess = $stopProcess
    }
    [pscustomobject]@{ Adapter = $adapter; State = $state }
}

function New-TestContext {
    param([ValidateRange(1, 2)][int]$PackagesAdded = 1)

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

    $fake = New-FakeAdapter -PackagesAdded $PackagesAdded
    $manifest = Initialize-SBMSGateC `
        -RunId $runId `
        -RunDirectory $runDirectory `
        -DriverPackagePath $driverDirectory `
        -ProductRoot $productDirectory `
        -VerificationDeviceCount 2 `
        -Adapter $fake.Adapter

    [pscustomobject]@{
        RunId = $runId
        RunRoot = $runRoot
        RunDirectory = $runDirectory
        Manifest = $manifest
        Adapter = $fake.Adapter
        State = $fake.State
    }
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

Invoke-TestCase 'happy install and rollback own exactly one package and two devices' {
    $ctx = $null
    try {
        $ctx = New-TestContext
        $installed = Invoke-SBMSGateC -Phase Install -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Install')
        Assert-Equal 'InstalledAndVerified' $installed.state 'Install did not reach its verified state.'
        Assert-Equal 'oem41.inf' $installed.ownedPublishedName 'Install captured the wrong package.'
        Assert-Equal 2 @($installed.ownedDeviceIds).Count 'Install did not own both expected devices.'

        $rolledBack = Invoke-SBMSGateC -Phase Rollback -RunId $ctx.RunId -RunRoot $ctx.RunRoot `
            -Adapter $ctx.Adapter -Execute -Acknowledgement (Get-Acknowledgement $ctx 'Rollback')
        Assert-Equal 'RollbackVerified' $rolledBack.state 'Rollback did not verify.'
        Assert-Equal 0 $ctx.State.Packages.Count 'Rollback left the owned package installed.'
        Assert-Equal 0 $ctx.State.Bindings.Count 'Rollback left an owned device binding.'
        Assert-Equal 1 @($ctx.State.Mutations | Where-Object { $_ -eq 'DeleteDriver:oem41.inf' }).Count 'Rollback did not delete exactly one owned package.'
    } finally { Remove-TestContext $ctx }
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
