$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maintenanceScript = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\maintenance.ps1')
)

Describe 'Install and uninstall compensation contracts' {
    BeforeAll {
        $loadRoot = Join-Path $TestDrive 'load-root'
        New-Item -ItemType Directory -Path $loadRoot -Force | Out-Null

        # Load the real orchestration functions without running the dispatcher.
        $maintenanceSource = [IO.File]::ReadAllText(
            $maintenanceScript,
            [Text.Encoding]::UTF8
        )
        $dispatchMarker = 'switch ($Action) {'
        $dispatchIndex = $maintenanceSource.LastIndexOf(
            $dispatchMarker,
            [StringComparison]::Ordinal
        )
        if ($dispatchIndex -lt 0) {
            throw 'maintenance.ps1 action dispatcher was not found.'
        }
        $definitions = $maintenanceSource.Substring(0, $dispatchIndex)
        . ([scriptblock]::Create($definitions)) `
            -Action Stop `
            -InstallRoot $loadRoot
    }

    BeforeEach {
        $script:events = New-Object System.Collections.ArrayList
        $script:driverScan = 0

        Mock Assert-InstallIdentity {
            [pscustomobject]@{ Sid = 'S-1-5-21-test' }
        }
        Mock Get-OwnedTaskSnapshots {
            @(
                [pscustomobject]@{
                    Path = '\SBMS\'
                    Name = 'old-task'
                    Xml = '<Task />'
                    WasRunning = $true
                }
            )
        }
        Mock Remove-SbmsTask {
            [void]$script:events.Add('remove-task')
        }
        Mock Find-SbmsDriverPackages {
            $script:driverScan++
            if ($script:driverScan -eq 1) {
                [IO.FileInfo]'C:\Windows\INF\oem-old.inf'
            }
            else {
                [IO.FileInfo]'C:\Windows\INF\oem-old.inf'
                [IO.FileInfo]'C:\Windows\INF\oem-new.inf'
            }
        }
        Mock Install-SbmsDriver {
            [void]$script:events.Add('install-driver')
        }
        Mock Install-SbmsTask {
            [void]$script:events.Add('install-task')
            throw 'startup registration failed'
        }
        Mock Remove-DriverPackages {
            param($Packages)
            [void]$script:events.Add(
                'remove-driver:' + (($Packages | ForEach-Object Name) -join ',')
            )
        }
        Mock Restore-SbmsTasks {
            [void]$script:events.Add('restore-task')
        }
        Mock Find-ObsoleteSbmsDriverPackages { @() }
    }

    It 'removes only newly added drivers and restores the previous task after install failure' {
        $caught = $null
        try {
            Install-Sbms
        }
        catch {
            $caught = $_
        }

        $caught | Should Not BeNullOrEmpty
        $caught.Exception.Message | Should Be 'startup registration failed'
        ($script:events -join '|') | Should Be (
            'remove-task|install-driver|install-task|remove-task|' +
            'remove-driver:oem-new.inf|restore-task'
        )
        Assert-MockCalled Remove-DriverPackages -Times 1 -Exactly
        Assert-MockCalled Restore-SbmsTasks -Times 1 -Exactly
    }

    It 'reports both failures when install task restoration also fails' {
        Mock Restore-SbmsTasks {
            throw 'task restore failed'
        }

        $caught = $null
        try {
            Install-Sbms
        }
        catch {
            $caught = $_
        }

        $caught | Should Not BeNullOrEmpty
        $caught.Exception.Message |
            Should Match 'startup registration failed.*Startup-task compensation also failed.*task restore failed'
    }

    It 'reinstalls the driver before restoring tasks after uninstall failure' {
        Mock Test-UninstallPreflight {
            [void]$script:events.Add('preflight')
        }
        Mock Stop-Sbms {
            [void]$script:events.Add('stop')
        }
        Mock Remove-SbmsDriver {
            [void]$script:events.Add('remove-driver')
            throw 'driver removal failed'
        }
        Mock Install-SbmsDriver {
            [void]$script:events.Add('restore-driver')
        }
        Mock Restore-SbmsTasks {
            [void]$script:events.Add('restore-task')
        }

        $caught = $null
        try {
            Uninstall-Sbms
        }
        catch {
            $caught = $_
        }

        $caught | Should Not BeNullOrEmpty
        $caught.Exception.Message |
            Should Match 'driver removal failed.*External state was restored; application files were retained\.'
        ($script:events -join '|') | Should Be (
            'preflight|stop|remove-task|remove-driver|' +
            'restore-driver|restore-task'
        )
    }

    It 'reports both failures when uninstall compensation fails' {
        Mock Test-UninstallPreflight {}
        Mock Stop-Sbms {}
        Mock Remove-SbmsDriver {
            throw 'driver removal failed'
        }
        Mock Install-SbmsDriver {
            throw 'driver restore failed'
        }

        $caught = $null
        try {
            Uninstall-Sbms
        }
        catch {
            $caught = $_
        }

        $caught | Should Not BeNullOrEmpty
        $caught.Exception.Message |
            Should Match 'driver removal failed.*Compensation also failed.*driver restore failed'
    }
}
