$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..')
)
$installerScript = Join-Path $repository 'installer\SBMS.iss'
$maintenanceScript = Join-Path $repository 'installer\maintenance.ps1'
$trayMain = Join-Path $repository 'src\bin\sbms-tray.rs'
$cliMain = Join-Path $repository 'src\main.rs'
$uiSource = Join-Path $repository 'src\ui.rs'

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
        $script:trayMainSource = [IO.File]::ReadAllText(
            $trayMain,
            [Text.Encoding]::UTF8
        )
        $script:cliMainSource = [IO.File]::ReadAllText(
            $cliMain,
            [Text.Encoding]::UTF8
        )
        $script:uiSourceText = [IO.File]::ReadAllText(
            $uiSource,
            [Text.Encoding]::UTF8
        )
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

    It 'registers an interactive highest-privilege tray task in its install directory' {
        $script:maintenanceSource | Should Match (
            '(?s)function\s+Install-SbmsTask\b.*?' +
            'New-ScheduledTaskAction\s+-Execute\s+\$tray\s+`\s*' +
            '-Argument\s+''--background''\s+`\s*' +
            '-WorkingDirectory\s+\$InstallRoot'
        )
        $script:maintenanceSource | Should Match (
            '(?s)function\s+Install-SbmsTask\b.*?' +
            'New-ScheduledTaskPrincipal\b.*?' +
            '-LogonType\s+Interactive\b.*?' +
            '-RunLevel\s+Highest\b'
        )
    }

    It 'accepts the legacy empty task arguments during an upgrade' {
        $script:maintenanceSource | Should Match (
            '(?s)\$ownedArguments\s*=\s*\[string\]::IsNullOrEmpty' +
            '\(\$action\.Arguments\)\s+-or\s*' +
            '\$action\.Arguments\s+-ceq\s+''--background'''
        )
        $script:maintenanceSource | Should Match '-not\s+\$ownedArguments'
    }

    It 'routes every tray entry through the launch broker before starting the UI' {
        $brokerIndex = $script:trayMainSource.IndexOf(
            'launch_broker::route_tray(',
            [StringComparison]::Ordinal
        )
        $uiIndex = $script:trayMainSource.IndexOf(
            'ui::run()',
            [StringComparison]::Ordinal
        )

        ($brokerIndex -ge 0) | Should Be $true
        ($uiIndex -gt $brokerIndex) | Should Be $true

        $uiCommandIndex = $script:cliMainSource.IndexOf(
            'Some("ui")',
            [StringComparison]::Ordinal
        )
        $cliBrokerIndex = $script:cliMainSource.IndexOf(
            'launch_broker::route_tray(true)',
            $uiCommandIndex,
            [StringComparison]::Ordinal
        )
        $cliUiIndex = $script:cliMainSource.IndexOf(
            'ui::run',
            $uiCommandIndex,
            [StringComparison]::Ordinal
        )

        ($uiCommandIndex -ge 0) | Should Be $true
        ($cliBrokerIndex -gt $uiCommandIndex) | Should Be $true
        ($cliUiIndex -gt $cliBrokerIndex) | Should Be $true
        $script:uiSourceText | Should Match 'TrayInstance::acquire\(\)'
    }
}
