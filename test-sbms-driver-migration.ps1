[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw "SBMS driver migration test failed: $Message"
    }
}

$modulePath = Join-Path $PSScriptRoot 'build\SBMS.DriverMigration.psm1'
Import-Module $modulePath -Force

$devices = @(
    [pscustomobject]@{
        InstanceId = 'SWD\SBMS\VIRTUALDISPLAY-01'
        HardwareIds = @('SBMS\IndirectDisplay')
        ServiceName = 'SBMSIndirectDisplay'
    },
    [pscustomobject]@{
        InstanceId = 'SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER'
        HardwareIds = @('IddSampleDriver')
        ServiceName = 'IddSampleDriver'
    },
    [pscustomobject]@{
        InstanceId = 'DISPLAY\DELD0E6\5&PHYSICAL'
        HardwareIds = @('DISPLAY\DELD0E6')
        ServiceName = 'monitor'
    }
)
$packages = @(
    [pscustomobject]@{ PublishedName = 'oem42.inf'; OriginalName = 'SBMSIndirectDisplay.inf' },
    [pscustomobject]@{ PublishedName = 'oem12.inf'; OriginalName = 'IddSampleDriver.inf' },
    [pscustomobject]@{ PublishedName = 'oem7.inf'; OriginalName = 'nv_dispig.inf' }
)

$inventory = Get-SBMSDriverMigrationInventory -Devices $devices -DriverPackages $packages
Assert-True (@($inventory.currentDevices).Count -eq 1) 'current SBMS device classification is not exact.'
Assert-True (@($inventory.legacyDevices).Count -eq 1) 'legacy device classification is not exact.'
Assert-True (@($inventory.currentPackages).Count -eq 1) 'current package classification is not exact.'
Assert-True (@($inventory.legacyPackages).Count -eq 1) 'legacy package classification is not exact.'
Assert-True (@($inventory.legacyMonitorEvidence).Count -eq 1) 'legacy monitor evidence was not reported.'
Assert-True (-not [bool]$inventory.legacyMonitorEvidence[0].cleanupEligible) 'DELD0E6 was treated as a cleanup target.'
Assert-True ([bool]$inventory.destructiveMigrationAllowed) 'unambiguous inventory was blocked.'

$plan = New-SBMSDriverMigrationPlan -Inventory $inventory
Assert-True ([bool]$plan.executable) 'unambiguous migration plan was blocked.'
Assert-True (
    @($plan.nonTargets) -contains 'DISPLAY\DELD0E6 and other monitor IDs without a proven legacy SBMS parent'
) 'the physical-monitor non-target rule is missing.'

$ambiguous = Get-SBMSDriverMigrationInventory -Devices @(
    [pscustomobject]@{
        InstanceId = 'SWD\SBMS\VIRTUALDISPLAY-02'
        HardwareIds = @('IddSampleDriver')
        ServiceName = 'SBMSIndirectDisplay'
    }
) -DriverPackages @()
Assert-True (@($ambiguous.ambiguousDevices).Count -eq 1) 'mixed identity was not classified as ambiguous.'
Assert-True (-not [bool]$ambiguous.destructiveMigrationAllowed) 'ambiguous identity authorized destructive migration.'
Assert-True (-not [bool](New-SBMSDriverMigrationPlan -Inventory $ambiguous).executable) 'ambiguous plan was executable.'

[pscustomobject]@{
    status = 'PASS'
    currentDevices = @($inventory.currentDevices).Count
    legacyDevices = @($inventory.legacyDevices).Count
    physicalMonitorCleanupEligible = [bool]$inventory.legacyMonitorEvidence[0].cleanupEligible
    ambiguousMigrationBlocked = (-not [bool]$ambiguous.destructiveMigrationAllowed)
}
