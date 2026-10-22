$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..')
)
$installerScript = Join-Path $repository 'installer\SBMS.iss'
$maintenanceScript = Join-Path $repository 'installer\maintenance.ps1'
$launchBroker = Join-Path $repository 'src\launch_broker.rs'

function Get-InnoSection {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $match = [regex]::Match(
        $Source,
        "(?ms)^\[$([regex]::Escape($Name))\]\s*(?<body>.*?)(?=^\[|\z)"
    )
    if (-not $match.Success) {
        throw "Inno Setup section [$Name] was not found."
    }
    $match.Groups['body'].Value
}

Describe 'SBMS launch entry contract' {
    BeforeAll {
        $script:installerSource = [IO.File]::ReadAllText(
            $installerScript,
            [Text.Encoding]::UTF8
        )
        $script:maintenanceSource = [IO.File]::ReadAllText(
            $maintenanceScript,
            [Text.Encoding]::UTF8
        )
        $script:launchBrokerSource = [IO.File]::ReadAllText(
            $launchBroker,
            [Text.Encoding]::UTF8
        )

        $dispatchMarker = 'switch ($Action) {'
        $dispatchIndex = $script:maintenanceSource.LastIndexOf(
            $dispatchMarker,
            [StringComparison]::Ordinal
        )
        if ($dispatchIndex -lt 0) {
            throw 'maintenance.ps1 action dispatcher was not found.'
        }
        $definitions = $script:maintenanceSource.Substring(0, $dispatchIndex)
        $script:loadRoot = Join-Path $TestDrive 'install-root'
        New-Item -ItemType Directory -Path $script:loadRoot -Force | Out-Null
        . ([scriptblock]::Create($definitions)) `
            -Action Stop `
            -InstallRoot $script:loadRoot
    }

    It 'marks the Start menu launch as an explicit open request' {
        $icons = Get-InnoSection `
            -Source $script:installerSource `
            -Name 'Icons'
        $shortcut = @(
            $icons -split "`r?`n" |
                Where-Object {
                    $_ -match '(?i)^\s*Name:\s*"\{group\}\\SBMS"\s*;'
                }
        )

        $shortcut.Count | Should Be 1
        $shortcut[0] | Should Match (
            '(?i);\s*Filename:\s*"\{app\}\\sbms-tray\.exe"\s*;' +
            '\s*Parameters:\s*"--open"(?:\s*;|\s*$)'
        )
    }

    It 'registers the tray task with deferred startup = <DeferStart>' -TestCases @(
        @{ DeferStart = $false }
        @{ DeferStart = $true }
    ) {
        param($DeferStart)
        $script:mockTaskAction =
            [Microsoft.Management.Infrastructure.CimInstance]::new('MSFT_TaskAction')
        $script:mockTrigger =
            [Microsoft.Management.Infrastructure.CimInstance]::new('MSFT_TaskTrigger')
        $script:mockTrigger.CimInstanceProperties.Add(
            [Microsoft.Management.Infrastructure.CimProperty]::Create(
                'StartBoundary', '', [Microsoft.Management.Infrastructure.CimType]::String,
                [Microsoft.Management.Infrastructure.CimFlags]::None))
        $script:mockPrincipal =
            [Microsoft.Management.Infrastructure.CimInstance]::new('MSFT_TaskPrincipal')
        $script:mockSettings =
            [Microsoft.Management.Infrastructure.CimInstance]::new('MSFT_TaskSettings')
        $script:startedProcess = [pscustomobject]@{ Id = 4242 }
        $script:processScan = 0
        $script:taskRegistered = $false
        Mock Assert-InstallIdentity {
            [pscustomobject]@{ Sid = 'S-1-5-21-test' }
        }
        Mock Get-ScheduledTask { $null }
        Mock New-ScheduledTaskAction { $script:mockTaskAction }
        Mock New-ScheduledTaskTrigger { $script:mockTrigger }
        Mock New-ScheduledTaskPrincipal { $script:mockPrincipal }
        Mock New-ScheduledTaskSettingsSet { $script:mockSettings }
        Mock Register-ScheduledTask {
            $marker = [System.Threading.Mutex]::OpenExisting('Local\SBMS-Manual-Launch-Broker-7EB4D7A8')
            $marker.Dispose()
            $script:taskRegistered = $true
        }
        Mock Start-ScheduledTask {}
        $script:readyChecks = 0
        Mock Test-SbmsTrayReady {
            $marker = [System.Threading.Mutex]::OpenExisting('Local\SBMS-Manual-Launch-Broker-7EB4D7A8')
            $marker.Dispose()
            $script:readyChecks++
            $script:readyChecks -ge 2
        }
        Mock Start-Sleep {}
        Mock Get-InstalledSbmsProcesses {
            $script:processScan++
            if ($script:taskRegistered) {
                $script:startedProcess
            }
        }

        Install-SbmsTask -DeferStart:$DeferStart

        Assert-MockCalled New-ScheduledTaskAction -Times 1 -Exactly -Scope It `
            -ParameterFilter {
                $Execute -eq $script:tray -and
                    $Argument -ceq '--background' -and
                    $WorkingDirectory -eq $script:loadRoot
            }
        Assert-MockCalled New-ScheduledTaskTrigger -Times 1 -Exactly -Scope It `
            -ParameterFilter {
                $AtLogOn -and $User -eq 'S-1-5-21-test'
            }
        Assert-MockCalled New-ScheduledTaskPrincipal -Times 1 -Exactly -Scope It `
            -ParameterFilter {
                $UserId -eq 'S-1-5-21-test' -and
                    $LogonType -eq 'Interactive' -and
                    $RunLevel -eq 'Highest'
            }
        Assert-MockCalled New-ScheduledTaskSettingsSet -Times 1 -Exactly -Scope It `
            -ParameterFilter {
                $AllowStartIfOnBatteries -and
                    $DontStopIfGoingOnBatteries -and
                    $ExecutionTimeLimit -eq [TimeSpan]::Zero -and
                    $MultipleInstances.ToString() -eq 'IgnoreNew'
            }
        Assert-MockCalled Register-ScheduledTask -Times 1 -Exactly -Scope It `
            -ParameterFilter {
                $TaskPath -eq '\SBMS\' -and
                    $TaskName -eq 'Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A' -and
                    $Action.Count -eq 1 -and
                    [object]::ReferenceEquals($Action[0], $script:mockTaskAction) -and
                    $Trigger.Count -eq 1 -and
                    [object]::ReferenceEquals($Trigger[0], $script:mockTrigger) -and
                    [object]::ReferenceEquals($Principal, $script:mockPrincipal) -and
                    [object]::ReferenceEquals($Settings, $script:mockSettings)
            }
        $expectedStarts = if ($DeferStart) { 0 } else { 1 }
        Assert-MockCalled Start-ScheduledTask -Times $expectedStarts -Exactly -Scope It `
            -ParameterFilter {
                $TaskPath -eq '\SBMS\' -and
                    $TaskName -eq 'Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A'
            }
        Assert-MockCalled Start-Sleep -Times $expectedStarts -Exactly -Scope It `
            -ParameterFilter { $Milliseconds -eq 200 }
        if ($DeferStart) {
            $script:mockTrigger.StartBoundary | Should Not BeNullOrEmpty
            $script:processScan | Should Be 1
            Assert-MockCalled Test-SbmsTrayReady -Times 0 -Exactly -Scope It
        }
        else {
            $script:processScan | Should Be 3
        }
        { [System.Threading.Mutex]::OpenExisting('Local\SBMS-Manual-Launch-Broker-7EB4D7A8') } | Should Throw
    }

    It 'accepts the legacy empty task arguments during an upgrade' {
        Mock Get-ScheduledTask {
            [pscustomobject]@{
                Actions = @(
                    [pscustomobject]@{
                        Execute = $script:tray
                        Arguments = ''
                        WorkingDirectory = $script:loadRoot
                    }
                )
            }
        }

        $task = Get-VerifiedTask -Path '\SBMS\' -Name 'test'

        $task | Should Not BeNullOrEmpty
    }

    It 'rejects an unexpected action count before indexing the task action' {
        Mock Get-ScheduledTask {
            [pscustomobject]@{ Actions = @() }
        }

        $failure = $null
        try {
            Get-VerifiedTask -Path '\SBMS\' -Name 'test'
        }
        catch {
            $failure = $_
        }

        $failure | Should Not BeNullOrEmpty
        $failure.Exception.Message | Should Match 'Scheduled task collision'
    }

    It 'keeps the registered task name identical in Rust and PowerShell' {
        $rustName = [regex]::Match(
            $script:launchBrokerSource,
            '(?ms)pub const REGISTERED_TASK_NAME:\s*&str\s*=\s*r"([^"]+)";'
        )
        $powerShellPath = [regex]::Match(
            $script:maintenanceSource,
            '(?m)^\$taskPath\s*=\s*''([^'']+)'''
        )
        $powerShellName = [regex]::Match(
            $script:maintenanceSource,
            '(?m)^\$taskName\s*=\s*''([^'']+)'''
        )

        $rustName.Success | Should Be $true
        $powerShellPath.Success | Should Be $true
        $powerShellName.Success | Should Be $true
        ($powerShellPath.Groups[1].Value + $powerShellName.Groups[1].Value) |
            Should Be $rustName.Groups[1].Value
    }
}
