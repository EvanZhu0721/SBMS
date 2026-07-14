[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$modulePath = Join-Path $root 'lab\SBMS.HardwareLab.psm1'
$fixtureRoot = Join-Path $root 'test\fixtures\hardware-lab'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "Hardware lab module not found: $modulePath"
}

Import-Module $modulePath -Force

$script:CurrentGuid = '{11111111-1111-1111-1111-111111111111}'
$script:DefaultGuid = '{22222222-2222-2222-2222-222222222222}'
$script:CloneGuid = '{33333333-3333-3333-3333-333333333333}'
$script:ExtraGuid = '{44444444-4444-4444-4444-444444444444}'
$script:TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('SBMS-HardwareLabTests-' + [guid]::NewGuid().ToString('N'))
$script:Passed = 0
$script:Failed = 0
$script:RealCommandCalls = [ordered]@{ bcdedit = 0; shutdown = 0; schtasks = 0; pnputil = 0 }

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw ("{0} Expected=<{1}> Actual=<{2}>" -f $Message, $Expected, $Actual)
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if ($null -eq $caught) { throw "$Message Expected an exception." }
    if (-not [string]::IsNullOrEmpty($Pattern) -and $caught.Exception.Message -notmatch $Pattern) {
        throw ("{0} Wrong exception: {1}" -f $Message, $caught.Exception.Message)
    }
    return $caught
}

function Invoke-TestCase {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action
        $script:Passed++
        Write-Host ("[PASS] {0}" -f $Name)
    } catch {
        $script:Failed++
        Write-Host ("[FAIL] {0}: {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    }
}

function Get-FixtureText {
    param([string]$Name)
    return [IO.File]::ReadAllText((Join-Path $fixtureRoot $Name), [Text.Encoding]::UTF8).TrimEnd([char[]]"`r`n")
}

function New-FakeState {
    param([string]$CopyFixture = 'copy-output.zh-CN.txt', [string]$IdentifierLabel = 'identifier')
    return [ordered]@{
        CurrentGuid = $script:CurrentGuid
        DefaultGuid = $script:DefaultGuid
        ExpectedCloneGuid = $script:CloneGuid
        AdditionalCloneGuid = $script:ExtraGuid
        DisplayOrder = @($script:DefaultGuid, $script:CurrentGuid)
        BootSequence = @()
        Clones = @{}
        Tasks = @{}
        Calls = New-Object Collections.Generic.List[object]
        MutationCalls = 0
        RestartCalls = 0
        RestartSuccesses = 0
        RestartFailuresRemaining = 0
        CopyFixture = $CopyFixture
        IdentifierLabel = $IdentifierLabel
        CopyCreatesClone = $true
        CopyCloneCount = 1
        CopyAddsUnexpectedDisplayOrderGuid = $false
        CopyAddsUnexpectedBootSequenceGuid = $false
        WatchdogInstallFails = $false
        WatchdogRemoveFails = $false
        CloneDeleteFails = $false
        CloneEnumFailuresRemaining = 0
        WatchdogReadbackDrifts = $false
        WatchdogArgumentsDrift = $false
        WatchdogDelayDrift = $false
        WatchdogDelayMissing = $false
        WatchdogEnabledDrift = $false
        WatchdogEnabledMissing = $false
        WatchdogBootTriggerEnabledDrift = $false
        WatchdogBootTriggerEnabledMissing = $false
        BootSequenceFailsAfterApply = $false
        LoaderTranscriptHasExtraGuids = $false
        CurrentTestSigningText = $null
        RunDirectorySecurityValid = $true
        RunDirectorySecurityBareBoolean = $false
        RunDirectorySecurityShape = 'Valid'
        SecuredRunDirectories = New-Object Collections.Generic.List[string]
    }
}

function Add-FakeCall {
    param($State, [string]$Kind, [string[]]$Arguments, [bool]$Mutation)
    $State.Calls.Add([pscustomobject][ordered]@{
        index = $State.Calls.Count
        kind = $Kind
        arguments = @($Arguments)
        mutation = $Mutation
    })
    if ($Mutation) { $State.MutationCalls++ }
}

function Get-FakeBootManagerText {
    param($State)
    $text = "Windows Boot Manager`r`n--------------------`r`n$($State.IdentifierLabel)              {bootmgr}`r`ndefault                 $($State.DefaultGuid)`r`n"
    $text += "displayorder            " + (@($State.DisplayOrder) -join "`r`n                        ") + "`r`n"
    if (@($State.BootSequence).Count -gt 0) {
        $text += "bootsequence            " + (@($State.BootSequence) -join "`r`n                        ") + "`r`n"
    }
    return $text
}

function Get-FakeLoaderText {
    param($State, [string]$Guid, [string]$Description, [bool]$TestSigning, [string]$TestSigningText)
    $text = "Windows Boot Loader`r`n--------------------`r`n$($State.IdentifierLabel)              $Guid`r`n"
    if ($State.LoaderTranscriptHasExtraGuids) {
        $text += "recoverysequence        $script:ExtraGuid`r`nresumeobject            {55555555-5555-5555-5555-555555555555}`r`n"
    }
    $renderedTestSigning = if (-not [string]::IsNullOrEmpty($TestSigningText)) { $TestSigningText } elseif ($TestSigning) { 'Yes' } else { 'No' }
    return $text + "description             $Description`r`ntestsigning             $renderedTestSigning`r`n"
}

function Get-TestWatchdogExpectedArguments {
    param($Specification)
    $module = Get-Module -Name SBMS.HardwareLab -ErrorAction Stop
    return & $module { param($Spec) Get-SBMSWatchdogExpectedArguments -Specification $Spec } $Specification
}

function New-FakeAdapter {
    param($State)

    $addFakeCall = ${function:Add-FakeCall}
    $getFakeBootManagerText = ${function:Get-FakeBootManagerText}
    $getFakeLoaderText = ${function:Get-FakeLoaderText}
    $getFixtureText = ${function:Get-FixtureText}
    $getTestWatchdogExpectedArguments = ${function:Get-TestWatchdogExpectedArguments}
    $adapter = @{
        IsReal = $false
        TestAdministrator = { $true }
        InvokeBcd = {
            param([string[]]$ArgumentList)
            $argsCopy = @($ArgumentList)
            $verb = if ($argsCopy.Count -gt 0) { $argsCopy[0] } else { '' }
            $isMutation = $verb -in @('/copy', '/set', '/bootsequence', '/delete')
            & $addFakeCall -State $State -Kind 'bcd' -Arguments $argsCopy -Mutation $isMutation
            $exitCode = 0
            $stdout = ''
            $stderr = ''

            if ($verb -eq '/enum') {
                $id = $argsCopy[1]
                if ($id -eq '{bootmgr}') {
                    $stdout = & $getFakeBootManagerText -State $State
                } elseif ($id -eq '{current}') {
                    if ($State.CurrentGuid -eq $State.ExpectedCloneGuid -and $State.Clones.ContainsKey($State.ExpectedCloneGuid)) {
                        $clone = $State.Clones[$State.ExpectedCloneGuid]
                        $stdout = & $getFakeLoaderText -State $State -Guid $State.CurrentGuid -Description $clone.description -TestSigning ([bool]$clone.testsigning) -TestSigningText $State.CurrentTestSigningText
                    } else {
                        $stdout = & $getFakeLoaderText -State $State -Guid $State.CurrentGuid -Description 'Current production loader' -TestSigning $false
                    }
                } elseif ($id -eq '{default}') {
                    $stdout = & $getFakeLoaderText -State $State -Guid $State.DefaultGuid -Description 'Default production loader' -TestSigning $false
                } elseif ($id -eq 'all') {
                    $stdout = & $getFakeBootManagerText -State $State
                    foreach ($key in @($State.Clones.Keys)) {
                        $clone = $State.Clones[$key]
                        $stdout += "`r`n" + (& $getFakeLoaderText -State $State -Guid $key -Description $clone.description -TestSigning ([bool]$clone.testsigning))
                    }
                } elseif ($id -eq $State.ExpectedCloneGuid -and $State.CloneEnumFailuresRemaining -gt 0) {
                    $State.CloneEnumFailuresRemaining--
                    $exitCode = 1
                    $stderr = 'Injected transient clone enum failure.'
                } elseif ($State.Clones.ContainsKey($id)) {
                    $clone = $State.Clones[$id]
                    $stdout = & $getFakeLoaderText -State $State -Guid $id -Description $clone.description -TestSigning ([bool]$clone.testsigning)
                } else {
                    $exitCode = 1
                    $stderr = 'The specified entry does not exist.'
                }
            } elseif ($verb -eq '/copy') {
                if ($State.CopyCreatesClone) {
                    $State.Clones[$State.ExpectedCloneGuid] = @{ description = $argsCopy[3]; testsigning = $false }
                    $State.DisplayOrder = @($State.DisplayOrder) + $State.ExpectedCloneGuid
                    if ($State.CopyCloneCount -gt 1) {
                        $State.Clones[$State.AdditionalCloneGuid] = @{ description = $argsCopy[3]; testsigning = $false }
                        $State.DisplayOrder = @($State.DisplayOrder) + $State.AdditionalCloneGuid
                    }
                }
                if ($State.CopyAddsUnexpectedDisplayOrderGuid -and -not (@($State.DisplayOrder) -contains $State.AdditionalCloneGuid)) {
                    $State.DisplayOrder = @($State.DisplayOrder) + $State.AdditionalCloneGuid
                }
                if ($State.CopyAddsUnexpectedBootSequenceGuid) {
                    $State.BootSequence = @($State.AdditionalCloneGuid)
                }
                $stdout = & $getFixtureText -Name $State.CopyFixture
            } elseif ($verb -eq '/set') {
                $target = $argsCopy[1]
                if (-not $State.Clones.ContainsKey($target)) {
                    $exitCode = 1
                    $stderr = 'Refusing to set a non-clone loader.'
                } else {
                    $State.Clones[$target].testsigning = $true
                }
            } elseif ($verb -eq '/bootsequence') {
                $target = $argsCopy[1]
                if ($argsCopy.Count -gt 2 -and $argsCopy[2] -eq '/remove') {
                    $State.BootSequence = @($State.BootSequence | Where-Object { $_ -ne $target })
                } else {
                    $State.BootSequence = @($target)
                    if ($State.BootSequenceFailsAfterApply) {
                        $State.BootSequenceFailsAfterApply = $false
                        $exitCode = 1
                        $stderr = 'Injected failure after bootsequence was partially applied.'
                    }
                }
            } elseif ($verb -eq '/delete') {
                if ($State.CloneDeleteFails) {
                    $exitCode = 1
                    $stderr = 'Injected clone deletion failure.'
                } else {
                    [void]$State.Clones.Remove($argsCopy[1])
                    $State.DisplayOrder = @($State.DisplayOrder | Where-Object { $_ -ne $argsCopy[1] })
                }
            } else {
                $exitCode = 1
                $stderr = 'Unexpected fake BCD command.'
            }

            return [pscustomobject]@{ ExitCode = $exitCode; StdOut = $stdout; StdErr = $stderr }
        }
        InstallWatchdog = {
            param($Specification)
            & $addFakeCall -State $State -Kind 'task-install' -Arguments @([string]$Specification.taskName) -Mutation $true
            if ($State.WatchdogInstallFails) {
                return [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'Injected watchdog install failure.' }
            }
            $State.Tasks[[string]$Specification.taskName] = [pscustomobject][ordered]@{
                exists = $true
                command = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')
                arguments = (& $getTestWatchdogExpectedArguments -Specification $Specification)
                userId = 'SYSTEM'
                hasBootTrigger = $true
                bootTriggerEnabled = $true
                bootDelay = ('PT{0}M' -f [int]$Specification.timeoutMinutes)
                enabled = $true
                disabled = $false
            }
            return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
        }
        GetWatchdog = {
            param([string]$TaskName)
            & $addFakeCall -State $State -Kind 'task-read' -Arguments @($TaskName) -Mutation $false
            if (-not $State.Tasks.ContainsKey($TaskName)) {
                return [pscustomobject]@{ exists = $false; exitCode = 1 }
            }
            $task = $State.Tasks[$TaskName]
            if ($State.WatchdogReadbackDrifts) {
                return [pscustomobject][ordered]@{
                    exists = $true
                    command = 'C:\Untrusted\powershell.exe'
                    arguments = $task.arguments
                    userId = $task.userId
                    hasBootTrigger = $task.hasBootTrigger
                    bootTriggerEnabled = $task.bootTriggerEnabled
                    bootDelay = $task.bootDelay
                    enabled = $task.enabled
                }
            }
            if ($State.WatchdogArgumentsDrift) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = ($task.arguments + ' -Unexpected'); userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootTriggerEnabled = $task.bootTriggerEnabled; bootDelay = $task.bootDelay; enabled = $task.enabled }
            }
            if ($State.WatchdogDelayDrift) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = $task.arguments; userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootTriggerEnabled = $task.bootTriggerEnabled; bootDelay = 'PT99M'; enabled = $task.enabled }
            }
            if ($State.WatchdogDelayMissing) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = $task.arguments; userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootTriggerEnabled = $task.bootTriggerEnabled; bootDelay = ''; enabled = $task.enabled }
            }
            if ($State.WatchdogEnabledDrift) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = $task.arguments; userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootTriggerEnabled = $task.bootTriggerEnabled; bootDelay = $task.bootDelay; enabled = $false }
            }
            if ($State.WatchdogEnabledMissing) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = $task.arguments; userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootTriggerEnabled = $task.bootTriggerEnabled; bootDelay = $task.bootDelay }
            }
            if ($State.WatchdogBootTriggerEnabledDrift) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = $task.arguments; userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootTriggerEnabled = $false; bootDelay = $task.bootDelay; enabled = $task.enabled }
            }
            if ($State.WatchdogBootTriggerEnabledMissing) {
                return [pscustomobject][ordered]@{ exists = $true; command = $task.command; arguments = $task.arguments; userId = $task.userId; hasBootTrigger = $task.hasBootTrigger; bootDelay = $task.bootDelay; enabled = $task.enabled }
            }
            return $task
        }
        RemoveWatchdog = {
            param([string]$TaskName)
            & $addFakeCall -State $State -Kind 'task-remove' -Arguments @($TaskName) -Mutation $true
            if ($State.WatchdogRemoveFails) { return [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'Injected watchdog removal failure.' } }
            [void]$State.Tasks.Remove($TaskName)
            return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
        }
        DisableWatchdog = {
            param([string]$TaskName)
            & $addFakeCall -State $State -Kind 'task-disable' -Arguments @($TaskName) -Mutation $true
            if (-not $State.Tasks.ContainsKey($TaskName)) { return [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'Task not found.' } }
            $State.Tasks[$TaskName].disabled = $true
            $State.Tasks[$TaskName].enabled = $false
            return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
        }
        RequestRestart = {
            & $addFakeCall -State $State -Kind 'restart' -Arguments @() -Mutation $true
            $State.RestartCalls++
            if ($State.RestartFailuresRemaining -gt 0) {
                $State.RestartFailuresRemaining--
                return [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'Injected restart failure.' }
            }
            $State.RestartSuccesses++
            return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
        }
        SecureRunDirectory = {
            param([string]$RunDirectory)
            & $addFakeCall -State $State -Kind 'acl-secure' -Arguments @($RunDirectory) -Mutation $true
            $State.SecuredRunDirectories.Add($RunDirectory)
            return [pscustomobject]@{ success = $true }
        }
        TestRunDirectorySecurity = {
            param([string]$RunDirectory)
            & $addFakeCall -State $State -Kind 'acl-read' -Arguments @($RunDirectory) -Mutation $false
            if ($State.RunDirectorySecurityBareBoolean) { return [bool]$State.RunDirectorySecurityValid }
            $valid = [bool]$State.RunDirectorySecurityValid
            if ($State.RunDirectorySecurityShape -eq 'EmptyObjects') { return [pscustomobject]@{ success = $true; objects = @() } }
            $shape = [string]$State.RunDirectorySecurityShape
            return [pscustomobject][ordered]@{
                success = $valid
                objects = @([pscustomobject][ordered]@{
                    path = $RunDirectory
                    ownerAllowed = ($valid -and $shape -ne 'OwnerFalse')
                    inheritanceProtected = ($valid -and $shape -ne 'InheritanceFalse')
                    systemFullControl = ($valid -and $shape -ne 'SystemFalse')
                    administratorsFullControl = ($valid -and $shape -ne 'AdministratorsFalse')
                    unexpectedRules = if (-not $valid -or $shape -eq 'UnexpectedRules') { @('S-1-5-21-test|Allow|Read') } else { @() }
                })
            }
        }
    }
    foreach ($key in @($adapter.Keys)) {
        if ($adapter[$key] -is [scriptblock]) { $adapter[$key] = $adapter[$key].GetNewClosure() }
    }
    return $adapter
}

function New-TestContext {
    param(
        [string]$CopyFixture = 'copy-output.zh-CN.txt',
        [ValidateSet('RecoveryDrill', 'TestSigning')][string]$Profile = 'RecoveryDrill',
        [string]$IdentifierLabel = 'identifier'
    )
    $runId = [guid]::NewGuid().ToString()
    $state = New-FakeState -CopyFixture $CopyFixture -IdentifierLabel $IdentifierLabel
    return [pscustomobject][ordered]@{
        RunId = $runId
        RunRoot = (Join-Path $script:TestRoot ([guid]::NewGuid().ToString('N')))
        State = $state
        Adapter = (New-FakeAdapter -State $state)
        Profile = $Profile
    }
}

function Invoke-LabPhase {
    param($Context, [string]$Phase, [switch]$WhatIf, [string]$Acknowledgement)
    $parameters = @{
        Phase = $Phase
        RunId = $Context.RunId
        RunRoot = $Context.RunRoot
        Adapter = $Context.Adapter
        Profile = $Context.Profile
        Execute = $true
        Acknowledgement = if ([string]::IsNullOrEmpty($Acknowledgement)) { "SBMS-HARDWARE-LAB/$($Context.RunId)/$($Context.Profile)/$Phase" } else { $Acknowledgement }
        Confirm = $false
    }
    if ($WhatIf) { $parameters.WhatIf = $true }
    return Invoke-SBMSHardwareLab @parameters
}

function Get-TestManifest {
    param($Context)
    $path = Join-Path (Join-Path $Context.RunRoot $Context.RunId) 'manifest.json'
    return Get-Content -LiteralPath $path -Encoding UTF8 -Raw | ConvertFrom-Json
}

function Invoke-TestWatchdog {
    param($Context)
    $manifest = Get-TestManifest -Context $Context
    return Invoke-SBMSHardwareLabWatchdog -RunDirectory ([string]$manifest.runDirectory) -RunId ([string]$manifest.runId) -TaskName ([string]$manifest.watchdogPlan.taskName) -Profile ([string]$manifest.profile) -Adapter $Context.Adapter -Execute -Acknowledgement ([string]$manifest.watchdogPlan.acknowledgement)
}

function Get-MutationCalls {
    param($State)
    return @($State.Calls | Where-Object { $_.mutation })
}

function Assert-ProductionLoadersUntouched {
    param($State)
    Assert-Equal $script:DefaultGuid $State.DefaultGuid 'bootmgr default changed.'
    Assert-Equal $script:CurrentGuid $State.CurrentGuid 'current loader changed.'
    foreach ($call in (Get-MutationCalls -State $State)) {
        if ($call.kind -ne 'bcd') { continue }
        $verb = $call.arguments[0]
        if ($verb -eq '/copy') {
            Assert-Equal $script:CurrentGuid $call.arguments[1] 'Clone was not copied from the resolved current loader.'
            continue
        }
        if ($verb -in @('/set', '/delete', '/bootsequence')) {
            Assert-True ($call.arguments[1] -eq $script:CloneGuid) "A $verb mutation targeted current/default instead of the clone."
        }
        Assert-True (-not (@($call.arguments) -contains '{current}')) 'A mutation targeted {current}.'
        Assert-True (-not (@($call.arguments) -contains '{default}')) 'A mutation targeted {default}.'
        Assert-True (-not (@($call.arguments) -contains $script:CurrentGuid)) 'A mutation targeted the resolved current GUID.'
        Assert-True (-not (@($call.arguments) -contains $script:DefaultGuid)) 'A mutation targeted the resolved default GUID.'
    }
}

function Assert-NoRealCommands {
    foreach ($name in @('bcdedit', 'shutdown', 'schtasks', 'pnputil')) {
        Assert-Equal 0 $script:RealCommandCalls[$name] "Real $name invocation count must remain zero."
    }
}

function Convert-TestWindowsArgument {
    param([AllowEmptyString()][string]$Argument)
    $module = Get-Module -Name SBMS.HardwareLab -ErrorAction Stop
    return & $module { param([AllowEmptyString()][string]$Value) ConvertTo-SBMSWindowsCommandLineArgument -Argument $Value } $Argument
}

try {
    New-Item -ItemType Directory -Path $script:TestRoot -Force | Out-Null

    Invoke-TestCase 'Windows argv escaping handles plain, empty, whitespace, quotes, and backslashes' {
        $quote = [string][char]34
        $slash = [string][char]92
        $cases = @(
            [pscustomobject]@{ name = 'plain'; input = 'alpha'; expected = 'alpha' },
            [pscustomobject]@{ name = 'empty'; input = ''; expected = ($quote + $quote) },
            [pscustomobject]@{ name = 'space'; input = 'hello world'; expected = ($quote + 'hello world' + $quote) },
            [pscustomobject]@{ name = 'embedded quote'; input = ('a' + $quote + 'b'); expected = ($quote + 'a' + $slash + $quote + 'b' + $quote) },
            [pscustomobject]@{ name = 'backslash before quote'; input = ('a' + $slash + $quote + 'b'); expected = ($quote + 'a' + ($slash * 3) + $quote + 'b' + $quote) },
            [pscustomobject]@{ name = 'trailing backslash with space'; input = ('C:' + $slash + 'path with space' + $slash); expected = ($quote + 'C:' + $slash + 'path with space' + ($slash * 2) + $quote) }
        )
        foreach ($case in $cases) {
            Assert-Equal $case.expected (Convert-TestWindowsArgument -Argument $case.input) "Windows argv escaping failed for $($case.name)."
        }
        Assert-NoRealCommands
    }

    Invoke-TestCase 'Real ACL adapter returns structured evidence on Windows PowerShell 5.1' {
        $probeRoot = Join-Path $script:TestRoot 'real-acl-adapter-probe'
        New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $probeRoot 'probe.txt') -Value 'probe' -Encoding UTF8
        $realAdapter = New-SBMSHardwareLabAdapter
        $result = & $realAdapter.TestRunDirectorySecurity $probeRoot
        Assert-True ($result -isnot [array]) 'Real ACL adapter emitted pipeline noise or an object array instead of one structured result.'
        Assert-True ($null -ne $result.PSObject.Properties['success']) 'Real ACL adapter omitted success evidence.'
        Assert-True ($null -ne $result.PSObject.Properties['objects']) 'Real ACL adapter omitted per-object evidence.'
        Assert-Equal 2 @($result.objects).Count 'Real ACL adapter did not return evidence for both the directory and file.'
        foreach ($object in @($result.objects)) {
            Assert-True ($null -ne $object.PSObject.Properties['unexpectedRules']) 'Real ACL adapter omitted unexpected-rule evidence.'
        }
        Assert-NoRealCommands
    }

    Invoke-TestCase 'Default phase is Audit and performs zero mutation' {
        $ctx = New-TestContext
        $result = Invoke-SBMSHardwareLab -RunId $ctx.RunId -RunRoot $ctx.RunRoot -Adapter $ctx.Adapter
        Assert-Equal 'Audit' $result.phase 'Default phase was not Audit.'
        Assert-Equal $false $result.mutationPerformed 'Audit reported a mutation.'
        Assert-Equal 0 $ctx.State.MutationCalls 'Audit called a fake mutation.'
        Assert-NoRealCommands
    }

    Invoke-TestCase 'Prepare WhatIf performs zero mutation' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare' -WhatIf
        Assert-Equal 0 $ctx.State.MutationCalls 'WhatIf called a fake mutation.'
        Assert-Equal 0 $ctx.State.Clones.Count 'WhatIf created a clone.'
        Assert-NoRealCommands
    }

    Invoke-TestCase 'Default and explicit RecoveryDrill Prepare never enable Test Signing' {
        foreach ($explicit in @($false, $true)) {
            $ctx = New-TestContext -Profile 'RecoveryDrill'
            if ($explicit) {
                $result = Invoke-SBMSHardwareLab -Phase Prepare -Profile RecoveryDrill -RunId $ctx.RunId -RunRoot $ctx.RunRoot -Adapter $ctx.Adapter -Execute -Acknowledgement "SBMS-HARDWARE-LAB/$($ctx.RunId)/RecoveryDrill/Prepare" -Confirm:$false
            } else {
                $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
            }
            Assert-Equal 'RecoveryDrill' $result.profile 'RecoveryDrill profile was not preserved.'
            $setCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/set' })
            Assert-Equal 0 $setCalls.Count 'RecoveryDrill attempted to enable Test Signing.'
        }
    }

    Invoke-TestCase 'TestSigning profile enables Test Signing only on the owned clone' {
        $ctx = New-TestContext -Profile 'TestSigning'
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        Assert-Equal 'TestSigning' $result.profile 'TestSigning profile was not preserved.'
        $setCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/set' })
        Assert-Equal 1 $setCalls.Count 'TestSigning did not issue exactly one /set.'
        Assert-Equal $script:CloneGuid $setCalls[0].arguments[1] 'TestSigning targeted a loader other than the owned clone.'
        Assert-Equal 'testsigning' $setCalls[0].arguments[2] 'TestSigning issued the wrong BCD setting.'
        Assert-Equal 'on' $setCalls[0].arguments[3] 'TestSigning did not enable the clone setting.'
        Assert-ProductionLoadersUntouched -State $ctx.State
    }

    Invoke-TestCase 'Real TestSigning Prepare and Arm are hard-blocked before adapter or filesystem mutation' {
        foreach ($phase in @('Prepare', 'Arm')) {
            $ctx = New-TestContext -Profile 'TestSigning'
            $ctx.Adapter.IsReal = $true
            $runRoot = 'C:\ProgramData\SBMSLab\Runs'
            $runDirectory = Join-Path $runRoot $ctx.RunId
            Assert-True (-not (Test-Path -LiteralPath $runDirectory)) 'The unique hard-lock test run directory unexpectedly existed before the test.'
            $null = Assert-Throws -Action {
                Invoke-SBMSHardwareLab -Phase $phase -Profile TestSigning -RunId $ctx.RunId -RunRoot $runRoot -Adapter $ctx.Adapter -Execute -Acknowledgement "SBMS-HARDWARE-LAB/$($ctx.RunId)/TestSigning/$phase" -Confirm:$false
            } -Pattern 'Real TestSigning Prepare/Arm is blocked' -Message "Real TestSigning $phase escaped the hard lock."
            Assert-Equal 0 $ctx.State.Calls.Count "Real TestSigning $phase reached an adapter seam."
            Assert-True (-not (Test-Path -LiteralPath $runDirectory)) "Real TestSigning $phase created a run directory before the hard lock."
        }
    }

    Invoke-TestCase 'Mutation acknowledgement is profile-bound' {
        $ctx = New-TestContext -Profile 'RecoveryDrill'
        $before = $ctx.State.MutationCalls
        $wrong = "SBMS-HARDWARE-LAB/$($ctx.RunId)/TestSigning/Prepare"
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' -Acknowledgement $wrong } -Pattern 'acknowledgement mismatch' -Message 'Cross-profile acknowledgement was accepted.'
        Assert-Equal $before $ctx.State.MutationCalls 'Cross-profile acknowledgement reached mutation.'
    }

    Invoke-TestCase 'Manifest profile is immutable for a run' {
        $ctx = New-TestContext -Profile 'RecoveryDrill'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $before = $ctx.State.MutationCalls
        $null = Assert-Throws -Action {
            Invoke-SBMSHardwareLab -Phase Arm -Profile TestSigning -RunId $ctx.RunId -RunRoot $ctx.RunRoot -Adapter $ctx.Adapter -Execute -Acknowledgement "SBMS-HARDWARE-LAB/$($ctx.RunId)/TestSigning/Arm" -Confirm:$false
        } -Pattern 'schema/profile mismatch' -Message 'Run profile drift was accepted.'
        Assert-Equal $before $ctx.State.MutationCalls 'Profile drift reached mutation.'
    }

    Invoke-TestCase 'Localized copy output yields exactly one clone GUID' {
        $ctx = New-TestContext -CopyFixture 'copy-output.zh-CN.txt'
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        Assert-Equal 'Prepared' $result.state 'Prepare did not complete.'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'Localized copy output parsed the wrong GUID.'
        Assert-Equal 1 $ctx.State.Clones.Count 'Prepare did not create exactly one clone.'
        Assert-Equal 1 $ctx.State.Tasks.Count 'Prepare did not install exactly one verified watchdog.'
        Assert-ProductionLoadersUntouched -State $ctx.State
    }

    Invoke-TestCase 'Copy-added owned clone is the only accepted displayorder delta and deletion restores baseline' {
        $ctx = New-TestContext
        $baselineDisplayOrder = @($ctx.State.DisplayOrder)
        $prepared = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        Assert-Equal (($baselineDisplayOrder + $script:CloneGuid) -join '|') (@($ctx.State.DisplayOrder) -join '|') 'Prepare did not preserve baseline displayorder plus exactly the owned clone.'
        $cleaned = Invoke-LabPhase -Context $ctx -Phase 'Rollback'
        Assert-Equal 'Cleaned' $cleaned.state 'Rollback did not clean the copy-added displayorder entry.'
        Assert-Equal ($baselineDisplayOrder -join '|') (@($ctx.State.DisplayOrder) -join '|') 'Clone deletion did not restore baseline displayorder.'
        Assert-Equal 0 $ctx.State.Clones.Count 'Rollback left the owned clone behind.'
    }

    Invoke-TestCase 'Unexpected displayorder GUID blocks clone cleanup without guessing' {
        $ctx = New-TestContext
        $ctx.State.CopyAddsUnexpectedDisplayOrderGuid = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'displayorder changed unexpectedly' -Message 'Prepare accepted an unrelated displayorder addition.'
        $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
        Assert-Equal 0 $deleteCalls.Count 'Cleanup guessed and deleted a clone while displayorder contained an unrelated GUID.'
        Assert-True ($ctx.State.Clones.ContainsKey($script:CloneGuid)) 'Fail-closed cleanup removed the owned clone despite unrelated displayorder drift.'
        Assert-Equal 0 $ctx.State.Tasks.Count 'Prepare installed a watchdog after unrelated displayorder drift.'
    }

    Invoke-TestCase 'Unexpected bootsequence change blocks clone cleanup without guessing' {
        $ctx = New-TestContext
        $ctx.State.CopyAddsUnexpectedBootSequenceGuid = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'bootsequence changed unexpectedly' -Message 'Prepare accepted an unrelated bootsequence change.'
        $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
        Assert-Equal 0 $deleteCalls.Count 'Cleanup guessed and deleted a clone while bootsequence contained an unrelated GUID.'
        Assert-True ($ctx.State.Clones.ContainsKey($script:CloneGuid)) 'Fail-closed cleanup removed the owned clone despite unrelated bootsequence drift.'
    }

    Invoke-TestCase 'Audit parses zh-CN identifier labels for bootmgr current and default' {
        $identifierLabel = Get-FixtureText -Name 'identifier-label.zh-CN.txt'
        $ctx = New-TestContext -IdentifierLabel $identifierLabel
        $result = Invoke-SBMSHardwareLab -Phase Audit -Profile RecoveryDrill -RunId $ctx.RunId -RunRoot $ctx.RunRoot -Adapter $ctx.Adapter
        Assert-Equal $script:CurrentGuid $result.current.currentGuid 'Audit did not parse the zh-CN current identifier.'
        Assert-Equal $script:DefaultGuid $result.current.defaultGuid 'Audit did not parse the zh-CN bootmgr default.'
        Assert-Equal $script:DefaultGuid $result.current.resolvedDefaultGuid 'Audit did not parse the zh-CN default-entry identifier.'
        Assert-Equal 0 $ctx.State.MutationCalls 'zh-CN Audit performed a mutation.'
    }

    Invoke-TestCase 'Prepare parses zh-CN identifier labels across all BCD read-backs' {
        $identifierLabel = Get-FixtureText -Name 'identifier-label.zh-CN.txt'
        $ctx = New-TestContext -IdentifierLabel $identifierLabel
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        Assert-Equal 'Prepared' $result.state 'Prepare failed with zh-CN identifier labels.'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'Prepare parsed the wrong clone under zh-CN identifier labels.'
        Assert-Equal 1 $ctx.State.Clones.Count 'Prepare did not preserve exactly one zh-CN-enumerated clone.'
        Assert-ProductionLoadersUntouched -State $ctx.State
    }

    Invoke-TestCase 'Ownership reconcile parses zh-CN identifier labels in enum all' {
        $identifierLabel = Get-FixtureText -Name 'identifier-label.zh-CN.txt'
        $ctx = New-TestContext -CopyFixture 'copy-output.zero.txt' -IdentifierLabel $identifierLabel
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        Assert-Equal 'Prepared' $result.state 'Ownership reconcile failed with zh-CN identifier labels.'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'Ownership reconcile selected the wrong zh-CN-enumerated clone.'
        Assert-Equal 1 $ctx.State.Clones.Count 'Ownership reconcile did not retain exactly one clone.'
        Assert-ProductionLoadersUntouched -State $ctx.State
    }

    Invoke-TestCase 'English copy output is also accepted' {
        $ctx = New-TestContext -CopyFixture 'copy-output.en-US.txt'
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'English copy output parsed the wrong GUID.'
    }

    Invoke-TestCase 'Copy output with zero GUIDs is diagnostic when enum uniquely reconciles the clone' {
        $ctx = New-TestContext -CopyFixture 'copy-output.zero.txt'
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $setCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/set' })
        Assert-Equal 'Prepared' $result.state 'Zero-GUID stdout blocked an enum-reconciled clone.'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'Zero-GUID stdout reconciled the wrong clone.'
        Assert-Equal 0 $setCalls.Count 'RecoveryDrill unexpectedly configured Test Signing.'
    }

    Invoke-TestCase 'Copy output with multiple GUIDs is diagnostic when enum uniquely reconciles the clone' {
        $ctx = New-TestContext -CopyFixture 'copy-output.multiple.txt'
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $setCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/set' })
        Assert-Equal 'Prepared' $result.state 'Multi-GUID stdout blocked an enum-reconciled clone.'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'Multi-GUID stdout reconciled the wrong clone.'
        Assert-Equal 0 $setCalls.Count 'RecoveryDrill unexpectedly configured Test Signing.'
    }

    Invoke-TestCase 'Copy output containing only source GUID is diagnostic when enum reconciles the clone' {
        $ctx = New-TestContext -CopyFixture 'copy-output.source-only.txt'
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $setCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/set' })
        Assert-Equal 'Prepared' $result.state 'Source-only stdout blocked an enum-reconciled clone.'
        Assert-Equal $script:CloneGuid $result.cloneGuid 'Source-only stdout reconciled the wrong clone.'
        Assert-Equal 0 $setCalls.Count 'RecoveryDrill unexpectedly configured Test Signing.'
    }

    Invoke-TestCase 'Unique enum-reconciled orphan is removed after later failure for anomalous copy outputs' {
        foreach ($fixture in @('copy-output.zero.txt', 'copy-output.multiple.txt', 'copy-output.source-only.txt')) {
            $ctx = New-TestContext -CopyFixture $fixture
            $ctx.State.WatchdogInstallFails = $true
            $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'watchdog task creation failed' -Message "Injected post-copy failure was not observed for $fixture."
            Assert-Equal 0 $ctx.State.Clones.Count "Failed Prepare left a uniquely owned orphan clone for $fixture."
            $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
            Assert-Equal 1 $deleteCalls.Count "Uniquely reconciled orphan was not deleted exactly once for $fixture."
            Assert-Equal $script:CloneGuid $deleteCalls[0].arguments[1] "Rollback deleted a non-owned entry for $fixture."
        }
    }

    Invoke-TestCase 'No enum ownership match fails closed without deleting any BCD entry' {
        $ctx = New-TestContext -CopyFixture 'copy-output.zero.txt'
        $ctx.State.CopyCreatesClone = $false
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'ownership read-back found 0 entries' -Message 'Prepare accepted a copy result with no owned clone.'
        Assert-Equal 0 $ctx.State.Clones.Count 'No-match reconcile unexpectedly created a clone.'
        $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
        Assert-Equal 0 $deleteCalls.Count 'No-match reconcile guessed and deleted a BCD entry.'
    }

    Invoke-TestCase 'Ambiguous exact-description ownership requires manual cleanup and deletes nothing' {
        $ctx = New-TestContext -CopyFixture 'copy-output.zero.txt'
        $ctx.State.CopyCloneCount = 2
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'Ownership is ambiguous' -Message 'Ambiguous ownership did not fail closed.'
        Assert-Equal 2 $ctx.State.Clones.Count 'Ambiguous reconcile removed or lost an owned candidate.'
        Assert-True ($ctx.State.Clones.ContainsKey($script:CloneGuid)) 'First ambiguous clone was removed.'
        Assert-True ($ctx.State.Clones.ContainsKey($script:ExtraGuid)) 'Second ambiguous clone was removed.'
        $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
        Assert-Equal 0 $deleteCalls.Count 'Ambiguous reconcile guessed and deleted a candidate.'
    }

    Invoke-TestCase 'Identifier parsing ignores recoverysequence and resumeobject GUIDs' {
        $ctx = New-TestContext
        $ctx.State.LoaderTranscriptHasExtraGuids = $true
        $result = Invoke-SBMSHardwareLab -RunId $ctx.RunId -RunRoot $ctx.RunRoot -Adapter $ctx.Adapter
        Assert-Equal $script:CurrentGuid $result.current.currentGuid 'Current identifier was polluted by another GUID-bearing element.'
        Assert-Equal $script:DefaultGuid $result.current.resolvedDefaultGuid 'Default identifier was polluted by another GUID-bearing element.'
        Assert-Equal $script:DefaultGuid $result.current.defaultGuid 'bootmgr default was parsed incorrectly.'
        Assert-Equal 0 $ctx.State.MutationCalls 'Identifier parsing audit mutated state.'
    }

    Invoke-TestCase 'Prepare freezes watchdog assets, secures them, and binds task action to frozen script' {
        $ctx = New-TestContext
        $result = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $manifest = Get-Content -LiteralPath (Join-Path $result.runDirectory 'manifest.json') -Encoding UTF8 -Raw | ConvertFrom-Json
        $frozenScript = [string]$manifest.watchdogPlan.scriptPath
        $frozenModule = [string]$manifest.watchdogPlan.modulePath
        Assert-True (Test-Path -LiteralPath $frozenScript -PathType Leaf) 'Frozen watchdog script is missing.'
        Assert-True (Test-Path -LiteralPath $frozenModule -PathType Leaf) 'Frozen watchdog module is missing.'
        Assert-Equal ([string]$manifest.watchdogPlan.scriptSha256) (Get-FileHash -LiteralPath $frozenScript -Algorithm SHA256).Hash 'Frozen script hash read-back mismatched.'
        Assert-Equal ([string]$manifest.watchdogPlan.moduleSha256) (Get-FileHash -LiteralPath $frozenModule -Algorithm SHA256).Hash 'Frozen module hash read-back mismatched.'
        Assert-True ($ctx.State.SecuredRunDirectories.Count -ge 1) 'Run directory was never secured.'
        $task = @($ctx.State.Tasks.Values)[0]
        $encoded = ([string]$task.arguments -split ' ')[-1]
        $decodedAction = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded))
        Assert-True ($decodedAction.Contains($frozenScript)) 'Scheduled task encoded action is not bound to the frozen script.'
        Assert-True (-not $decodedAction.Contains((Join-Path $root 'lab\Invoke-SBMSLabWatchdog.ps1'))) 'Scheduled task encoded action remained bound to the mutable source script.'
        Assert-True ($decodedAction.Contains('/r /f /t 5 /d p:0:0')) 'Inline watchdog fallback does not use the fixed five-second restart timeout.'
        Assert-True ($decodedAction.Contains('$LASTEXITCODE -eq 0')) 'Inline watchdog fallback can persist the terminal marker without a successful restart request.'
    }

    Invoke-TestCase 'Prepare blocks before clone creation when ACL read-back fails' {
        $ctx = New-TestContext
        $ctx.State.RunDirectorySecurityValid = $false
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'ACL structured read-back failed' -Message 'Prepare accepted an insecure run directory.'
        $copyCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/copy' })
        Assert-Equal 0 $copyCalls.Count 'Prepare cloned a loader before ACL read-back passed.'
    }

    Invoke-TestCase 'Prepare rejects bare-boolean ACL self-attestation' {
        $ctx = New-TestContext
        $ctx.State.RunDirectorySecurityBareBoolean = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'structured read-back failed' -Message 'Prepare accepted a bare boolean ACL result.'
        $copyCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/copy' })
        Assert-Equal 0 $copyCalls.Count 'Prepare cloned a loader after non-structured ACL attestation.'
    }

    Invoke-TestCase 'Prepare rejects incomplete or unsafe per-object ACL evidence' {
        foreach ($shape in @('EmptyObjects', 'OwnerFalse', 'InheritanceFalse', 'SystemFalse', 'AdministratorsFalse', 'UnexpectedRules')) {
            $ctx = New-TestContext
            $ctx.State.RunDirectorySecurityShape = $shape
            $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'structured read-back failed' -Message "Prepare accepted ACL evidence shape $shape."
            $copyCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/copy' })
            Assert-Equal 0 $copyCalls.Count "Prepare cloned a loader after unsafe ACL evidence $shape."
        }
    }

    Invoke-TestCase 'Prepare rejects scheduled-task action drift during installation read-back' {
        $ctx = New-TestContext
        $ctx.State.WatchdogReadbackDrifts = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'read-back failed' -Message 'Prepare accepted a task action not bound to the frozen script.'
        Assert-Equal 0 $ctx.State.Clones.Count 'Task read-back failure left the clone behind.'
    }

    Invoke-TestCase 'Prepare requires exact scheduled-task arguments and BootTrigger delay read-back' {
        foreach ($mode in @('arguments', 'delay', 'missing-delay')) {
            $ctx = New-TestContext
            if ($mode -eq 'arguments') { $ctx.State.WatchdogArgumentsDrift = $true }
            if ($mode -eq 'delay') { $ctx.State.WatchdogDelayDrift = $true }
            if ($mode -eq 'missing-delay') { $ctx.State.WatchdogDelayMissing = $true }
            $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'read-back failed' -Message "Prepare accepted watchdog $mode drift."
            Assert-Equal 0 $ctx.State.Clones.Count "Prepare left a clone after watchdog $mode drift."
        }
    }

    Invoke-TestCase 'Prepare requires watchdog task Enabled read-back' {
        foreach ($mode in @('false', 'missing')) {
            $ctx = New-TestContext
            if ($mode -eq 'false') { $ctx.State.WatchdogEnabledDrift = $true } else { $ctx.State.WatchdogEnabledMissing = $true }
            $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'enabled|read-back failed' -Message "Prepare accepted watchdog Enabled=$mode."
            Assert-Equal 0 $ctx.State.Clones.Count "Prepare left a clone after watchdog Enabled=$mode."
        }
    }

    Invoke-TestCase 'Prepare requires BootTrigger Enabled read-back' {
        foreach ($mode in @('false', 'missing')) {
            $ctx = New-TestContext
            if ($mode -eq 'false') { $ctx.State.WatchdogBootTriggerEnabledDrift = $true } else { $ctx.State.WatchdogBootTriggerEnabledMissing = $true }
            $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'bootTriggerEnabled|read-back failed' -Message "Prepare accepted BootTrigger Enabled=$mode."
            Assert-Equal 0 $ctx.State.Clones.Count "Prepare left a clone after BootTrigger Enabled=$mode."
        }
    }

    Invoke-TestCase 'Arm rechecks exact BootTrigger delay' {
        $ctx = New-TestContext
        $prepared = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $manifest = Get-TestManifest -Context $ctx
        $task = $ctx.State.Tasks[[string]$manifest.watchdogPlan.taskName]
        Assert-Equal 'PT8M' $task.bootDelay 'Fake task did not preserve the expected BootTrigger delay.'
        $ctx.State.WatchdogDelayDrift = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Arm' } -Pattern 'installed watchdog' -Message 'Arm accepted BootTrigger delay drift.'
        $armCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/bootsequence' -and $_.arguments.Count -eq 2 })
        Assert-Equal 0 $armCalls.Count 'Arm mutated bootsequence after delay read-back failed.'
    }

    Invoke-TestCase 'Arm rechecks watchdog Enabled state' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $ctx.State.WatchdogEnabledDrift = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Arm' } -Pattern 'installed watchdog' -Message 'Arm accepted a disabled watchdog.'
        $armCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/bootsequence' -and $_.arguments.Count -eq 2 })
        Assert-Equal 0 $armCalls.Count 'Arm mutated bootsequence with a disabled watchdog.'
    }

    Invoke-TestCase 'Arm rechecks BootTrigger Enabled state' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $ctx.State.WatchdogBootTriggerEnabledDrift = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Arm' } -Pattern 'installed watchdog' -Message 'Arm accepted a disabled BootTrigger.'
        $armCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/bootsequence' -and $_.arguments.Count -eq 2 })
        Assert-Equal 0 $armCalls.Count 'Arm mutated bootsequence with a disabled BootTrigger.'
    }

    Invoke-TestCase 'Watchdog install failure prevents Arm-ready state and rolls clone back' {
        $ctx = New-TestContext
        $ctx.State.WatchdogInstallFails = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'watchdog task creation failed' -Message 'Watchdog failure was not fatal.'
        Assert-Equal 0 $ctx.State.Clones.Count 'Failed watchdog left the known clone behind.'
        Assert-Equal 0 @($ctx.State.BootSequence).Count 'Failed watchdog armed bootsequence.'
    }

    Invoke-TestCase 'Prepare cleanup failure persists RecoveryRequired instead of reporting clean rollback' {
        $ctx = New-TestContext
        $ctx.State.WatchdogInstallFails = $true
        $ctx.State.CloneDeleteFails = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Prepare' } -Pattern 'Cleanup failures' -Message 'Prepare cleanup failure was not surfaced.'
        $manifest = Get-TestManifest -Context $ctx
        Assert-Equal 'RecoveryRequired' $manifest.state 'Cleanup failure did not persist RecoveryRequired.'
        Assert-Equal 1 $ctx.State.Clones.Count 'Deletion failure unexpectedly removed the owned clone.'
    }

    Invoke-TestCase 'Rollback reconciles an empty manifest clone GUID before cleanup' {
        $ctx = New-TestContext
        $prepared = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $manifestPath = Join-Path $prepared.runDirectory 'manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
        $manifest.clone.guid = $null
        $json = $manifest | ConvertTo-Json -Depth 20
        [IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding($false)))
        $result = Invoke-LabPhase -Context $ctx -Phase 'Rollback'
        Assert-Equal 'Cleaned' $result.state 'Rollback did not recover from an empty clone GUID.'
        Assert-Equal 0 $ctx.State.Clones.Count 'Rollback left the enum-reconciled clone behind.'
        Assert-Equal 0 $ctx.State.Tasks.Count 'Rollback left the watchdog behind.'
    }

    Invoke-TestCase 'Arm requires verified task read-back, not an operator boolean' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $ctx.State.WatchdogReadbackDrifts = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Arm' } -Pattern 'installed watchdog' -Message 'Arm accepted drifted task content.'
        $armCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/bootsequence' -and $_.arguments.Count -eq 2 })
        Assert-Equal 0 $armCalls.Count 'Arm mutated bootsequence after watchdog read-back failed.'
        Assert-Equal 0 @($ctx.State.BootSequence).Count 'Arm planned a boot with a drifted watchdog.'
    }

    Invoke-TestCase 'Partial bootsequence failure rolls back in reverse order' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $ctx.State.BootSequenceFailsAfterApply = $true
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Arm' } -Pattern 'Arming one-time bootsequence failed' -Message 'Injected arm failure was not observed.'
        Assert-True (@($ctx.State.BootSequence) -contains $script:CloneGuid) 'Failure injection did not model a partial bootsequence write.'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Rollback'
        $removeBoot = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/bootsequence' -and $_.arguments.Count -gt 2 -and $_.arguments[2] -eq '/remove' } | Select-Object -Last 1)
        $removeTask = @($ctx.State.Calls | Where-Object { $_.kind -eq 'task-remove' } | Select-Object -Last 1)
        $deleteClone = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' } | Select-Object -Last 1)
        Assert-Equal 1 $removeBoot.Count 'Rollback did not remove bootsequence.'
        Assert-Equal 1 $removeTask.Count 'Rollback did not remove watchdog.'
        Assert-Equal 1 $deleteClone.Count 'Rollback did not delete clone.'
        Assert-True ($removeBoot[0].index -lt $removeTask[0].index) 'Watchdog was removed before bootsequence was disarmed.'
        Assert-True ($removeTask[0].index -lt $deleteClone[0].index) 'Clone was deleted before watchdog cleanup.'
        Assert-ProductionLoadersUntouched -State $ctx.State
    }

    Invoke-TestCase 'Arm is idempotent and plans the clone only once' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $first = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        $second = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        Assert-Equal 'Armed' $first.state 'First Arm failed.'
        Assert-Equal 'Armed' $second.state 'Second Arm failed.'
        $armCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/bootsequence' -and $_.arguments.Count -eq 2 })
        Assert-Equal 1 $armCalls.Count 'Clone was placed in one-time bootsequence more than once.'
        Assert-Equal 1 @($ctx.State.BootSequence).Count 'Bootsequence is not exactly one entry.'
        Assert-Equal $script:CloneGuid $ctx.State.BootSequence[0] 'Bootsequence targeted a non-clone loader.'
    }

    Invoke-TestCase 'Watchdog does not restart on default and restarts clone once' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        $defaultResult = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $false $defaultResult.restartRequested 'Watchdog restarted the unchanged default loader.'
        Assert-Equal 0 $ctx.State.RestartCalls 'Default loader produced a restart call.'
        $ctx.State.CurrentGuid = $script:CloneGuid
        $cloneResult = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $true $cloneResult.restartRequested 'Watchdog did not request recovery from the test-signed clone.'
        Assert-Equal 1 $ctx.State.RestartCalls 'One watchdog invocation did not request exactly one restart.'
        $second = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $false $second.restartRequested 'Second watchdog invocation requested another restart.'
        Assert-Equal 1 $ctx.State.RestartCalls 'Watchdog requested restart more than once.'
        Assert-NoRealCommands
    }

    Invoke-TestCase 'Watchdog retries after restart failure and terminal marker suppresses only later invocations' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        $ctx.State.CurrentGuid = $script:CloneGuid
        $ctx.State.RestartFailuresRemaining = 1
        $manifest = Get-TestManifest -Context $ctx
        $terminalMarker = Join-Path ([string]$manifest.runDirectory) 'watchdog-restart.requested'

        $null = Assert-Throws -Action { Invoke-TestWatchdog -Context $ctx } -Pattern 'restart request failed' -Message 'The injected restart failure was not surfaced.'
        Assert-Equal 1 $ctx.State.RestartCalls 'The first watchdog invocation did not make exactly one restart attempt.'
        Assert-Equal 0 $ctx.State.RestartSuccesses 'A failed restart attempt was counted as successful.'
        Assert-True (-not (Test-Path -LiteralPath $terminalMarker -PathType Leaf)) 'A failed restart request persisted the terminal marker.'

        $second = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $true $second.restartRequested 'The watchdog did not retry after the first restart request failed.'
        Assert-Equal 2 $ctx.State.RestartCalls 'The retry did not make exactly one additional restart attempt.'
        Assert-Equal 1 $ctx.State.RestartSuccesses 'The retry did not produce exactly one successful restart request.'
        Assert-True (Test-Path -LiteralPath $terminalMarker -PathType Leaf) 'A successful restart request did not persist the terminal marker.'

        $third = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $false $third.restartRequested 'The terminal marker did not suppress a third restart request.'
        Assert-Equal 2 $ctx.State.RestartCalls 'The terminal marker allowed another restart attempt.'
        Assert-Equal 1 $ctx.State.RestartSuccesses 'The terminal marker allowed another successful restart request.'
    }

    Invoke-TestCase 'Watchdog fail-safe restarts exact clone when testsigning parse is unknown' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        $ctx.State.CurrentGuid = $script:CloneGuid
        $ctx.State.CurrentTestSigningText = 'UNKNOWN'
        $result = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $true $result.restartRequested 'Unknown testsigning parse suppressed fail-safe recovery.'
        Assert-Equal 1 $ctx.State.RestartCalls 'Unknown testsigning state did not request exactly one restart.'
    }

    Invoke-TestCase 'Watchdog fail-safe restarts exact clone when frozen script hash is anomalous' {
        $ctx = New-TestContext
        $prepared = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        $ctx.State.CurrentGuid = $script:CloneGuid
        $manifest = Get-Content -LiteralPath (Join-Path $prepared.runDirectory 'manifest.json') -Encoding UTF8 -Raw | ConvertFrom-Json
        Add-Content -LiteralPath ([string]$manifest.watchdogPlan.scriptPath) -Value "`r`n# injected test-only hash anomaly" -Encoding UTF8
        $result = Invoke-TestWatchdog -Context $ctx
        Assert-Equal $true $result.restartRequested 'Frozen script hash anomaly suppressed fail-safe recovery from the exact clone.'
        Assert-Equal 1 $ctx.State.RestartCalls 'Hash anomaly did not request exactly one restart.'
    }

    Invoke-TestCase 'Rollback cleanup is idempotent and preserves default/current' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $null = Invoke-LabPhase -Context $ctx -Phase 'Arm'
        $first = Invoke-LabPhase -Context $ctx -Phase 'Rollback'
        $afterFirst = $ctx.State.MutationCalls
        $second = Invoke-LabPhase -Context $ctx -Phase 'Rollback'
        Assert-Equal 'Cleaned' $first.state 'First cleanup failed.'
        Assert-Equal 'Cleaned' $second.state 'Second cleanup was not idempotent.'
        Assert-Equal $afterFirst $ctx.State.MutationCalls 'Second cleanup repeated a mutation.'
        Assert-Equal 0 $ctx.State.Clones.Count 'Cleanup left a clone.'
        Assert-Equal 0 $ctx.State.Tasks.Count 'Cleanup left a watchdog.'
        Assert-Equal 0 @($ctx.State.BootSequence).Count 'Cleanup left bootsequence armed.'
        Assert-ProductionLoadersUntouched -State $ctx.State
    }

    Invoke-TestCase 'Rollback refuses clone deletion after external bootmgr default drift' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $manifest = Get-TestManifest -Context $ctx
        $taskName = [string]$manifest.watchdogPlan.taskName
        Assert-True $ctx.State.Tasks.ContainsKey($taskName) 'Prepared watchdog task was absent before the default-drift test.'
        $ctx.State.DefaultGuid = $script:ExtraGuid
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Rollback' } -Pattern 'bootmgr default changed unexpectedly' -Message 'Rollback accepted external bootmgr default drift.'
        $taskRemoveCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'task-remove' })
        $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
        Assert-Equal 0 $taskRemoveCalls.Count 'Rollback removed the watchdog before rejecting bootmgr default drift.'
        Assert-True $ctx.State.Tasks.ContainsKey($taskName) 'Rollback lost the watchdog before rejecting bootmgr default drift.'
        Assert-Equal 0 $deleteCalls.Count 'Rollback deleted the clone after bootmgr default drift.'
        Assert-True $ctx.State.Clones.ContainsKey($script:CloneGuid) 'Rollback removed the clone after bootmgr default drift.'
    }

    Invoke-TestCase 'Rollback preserves watchdog when initial clone enum fails during default drift' {
        $ctx = New-TestContext
        $null = Invoke-LabPhase -Context $ctx -Phase 'Prepare'
        $manifest = Get-TestManifest -Context $ctx
        $taskName = [string]$manifest.watchdogPlan.taskName
        $ctx.State.DefaultGuid = $script:ExtraGuid
        $ctx.State.CloneEnumFailuresRemaining = 1
        $null = Assert-Throws -Action { Invoke-LabPhase -Context $ctx -Phase 'Rollback' } -Pattern 'bootmgr default changed unexpectedly|safety gate failed' -Message 'Rollback accepted default drift after a transient clone enum failure.'
        $taskRemoveCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'task-remove' })
        $deleteCalls = @($ctx.State.Calls | Where-Object { $_.kind -eq 'bcd' -and $_.arguments[0] -eq '/delete' })
        Assert-Equal 0 $taskRemoveCalls.Count 'Rollback removed the watchdog after the initial clone enum failed during default drift.'
        Assert-True $ctx.State.Tasks.ContainsKey($taskName) 'Rollback lost the watchdog after the initial clone enum failed during default drift.'
        Assert-Equal 0 $deleteCalls.Count 'Rollback deleted the clone after the initial enum failed during default drift.'
        Assert-True $ctx.State.Clones.ContainsKey($script:CloneGuid) 'Rollback removed the clone after the initial enum failed during default drift.'
    }

    Invoke-TestCase 'All command seams remained fake' {
        Assert-NoRealCommands
    }
} finally {
    if (Test-Path -LiteralPath $script:TestRoot) {
        Remove-Item -LiteralPath $script:TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ("Hardware lab tests: {0} passed, {1} failed." -f $script:Passed, $script:Failed)
Assert-NoRealCommands
if ($script:Failed -ne 0) { exit 1 }
exit 0
