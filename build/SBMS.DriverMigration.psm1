Set-StrictMode -Version 2.0

function Get-SBMSDriverMigrationContract {
    param(
        [string] $IdentityPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'driver-identity.json')
    )

    $resolved = [System.IO.Path]::GetFullPath($IdentityPath)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "SBMS driver identity contract not found: $resolved"
    }

    $contract = Get-Content -LiteralPath $resolved -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$contract.schemaVersion -ne 1) {
        throw "Unsupported SBMS driver identity schema: $($contract.schemaVersion)"
    }
    $contract
}

function Test-SBMSOrdinalEqual {
    param([string] $Left, [string] $Right)

    return [string]::Equals(
        [string]$Left,
        [string]$Right,
        [StringComparison]::OrdinalIgnoreCase)
}

function Test-SBMSOrdinalPrefix {
    param([string] $Value, [string] $Prefix)

    return -not [string]::IsNullOrEmpty($Value) -and
        $Value.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-SBMSStringValues {
    param([object] $Value)

    if ($null -eq $Value) {
        return @()
    }
    @($Value | ForEach-Object { [string]$_ })
}

function Test-SBMSAnyExactValue {
    param([string[]] $Values, [string[]] $Expected)

    foreach ($value in $Values) {
        foreach ($candidate in $Expected) {
            if (Test-SBMSOrdinalEqual $value $candidate) {
                return $true
            }
        }
    }
    return $false
}

function Get-SBMSDriverMigrationInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Devices,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $DriverPackages,

        [string] $IdentityPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'driver-identity.json')
    )

    $contract = Get-SBMSDriverMigrationContract -IdentityPath $IdentityPath
    $currentInstancePrefix = "SWD\$($contract.pnp.enumerator)\$($contract.pnp.instancePrefix)"
    $currentHardwareIds = @(
        [string]$contract.pnp.hardwareId,
        [string]$contract.pnp.rootHardwareId
    )
    $legacyInstancePrefixes = @($contract.legacy.instanceIdPrefixes | ForEach-Object { [string]$_ })
    $legacyHardwareIds = @($contract.legacy.hardwareIds | ForEach-Object { [string]$_ })
    $legacyServices = @($contract.legacy.serviceNames | ForEach-Object { [string]$_ })
    $legacyPackageNames = @($contract.legacy.packageOriginalNames | ForEach-Object { [string]$_ })

    $currentDevices = [System.Collections.Generic.List[object]]::new()
    $legacyDevices = [System.Collections.Generic.List[object]]::new()
    $ambiguousDevices = [System.Collections.Generic.List[object]]::new()
    $monitorEvidence = [System.Collections.Generic.List[object]]::new()

    foreach ($device in @($Devices)) {
        $instanceId = [string]$device.InstanceId
        $serviceName = [string]$device.ServiceName
        $hardwareIds = Get-SBMSStringValues $device.HardwareIds
        $currentSignals = [System.Collections.Generic.List[string]]::new()
        $legacySignals = [System.Collections.Generic.List[string]]::new()

        if (Test-SBMSOrdinalPrefix $instanceId $currentInstancePrefix) {
            $currentSignals.Add('instance-prefix')
        }
        if (Test-SBMSAnyExactValue $hardwareIds $currentHardwareIds) {
            $currentSignals.Add('hardware-id')
        }
        if (Test-SBMSOrdinalEqual $serviceName ([string]$contract.pnp.serviceName)) {
            $currentSignals.Add('service')
        }

        foreach ($prefix in $legacyInstancePrefixes) {
            if (Test-SBMSOrdinalPrefix $instanceId $prefix) {
                $legacySignals.Add('instance-prefix')
                break
            }
        }
        if (Test-SBMSAnyExactValue $hardwareIds $legacyHardwareIds) {
            $legacySignals.Add('hardware-id')
        }
        if (Test-SBMSAnyExactValue @($serviceName) $legacyServices) {
            $legacySignals.Add('service')
        }

        $record = [pscustomobject][ordered]@{
            device = $device
            instanceId = $instanceId
            currentSignals = @($currentSignals)
            legacySignals = @($legacySignals)
        }
        if ($currentSignals.Count -gt 0 -and $legacySignals.Count -gt 0) {
            $ambiguousDevices.Add($record)
        } elseif ($currentSignals.Count -gt 0) {
            $currentDevices.Add($record)
        } elseif ($legacySignals.Count -gt 0) {
            $legacyDevices.Add($record)
        }

        if (Test-SBMSAnyExactValue $hardwareIds @($contract.legacy.indicatorOnlyMonitorHardwareIds)) {
            $monitorEvidence.Add([pscustomobject][ordered]@{
                device = $device
                instanceId = $instanceId
                cleanupEligible = $false
                reason = 'Monitor hardware IDs are evidence only; ownership must be traced to an SBMS legacy parent.'
            })
        }
    }

    $currentPackages = [System.Collections.Generic.List[object]]::new()
    $legacyPackages = [System.Collections.Generic.List[object]]::new()
    foreach ($package in @($DriverPackages)) {
        $originalName = [string]$package.OriginalName
        if (Test-SBMSOrdinalEqual $originalName ([string]$contract.package.infName)) {
            $currentPackages.Add($package)
        } elseif (Test-SBMSAnyExactValue @($originalName) $legacyPackageNames) {
            $legacyPackages.Add($package)
        }
    }

    [pscustomobject][ordered]@{
        identitySchema = [int]$contract.schemaVersion
        currentDevices = @($currentDevices)
        legacyDevices = @($legacyDevices)
        ambiguousDevices = @($ambiguousDevices)
        currentPackages = @($currentPackages)
        legacyPackages = @($legacyPackages)
        legacyMonitorEvidence = @($monitorEvidence)
        destructiveMigrationAllowed = ($ambiguousDevices.Count -eq 0)
    }
}

function New-SBMSDriverMigrationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Inventory
    )

    $blockers = [System.Collections.Generic.List[string]]::new()
    if (@($Inventory.ambiguousDevices).Count -gt 0) {
        $blockers.Add('A device matches both current and legacy ownership signals.')
    }
    if (-not [bool]$Inventory.destructiveMigrationAllowed) {
        $blockers.Add('The inventory did not authorize destructive migration.')
    }

    [pscustomobject][ordered]@{
        executable = ($blockers.Count -eq 0)
        blockers = @($blockers)
        phases = @(
            'Stage and verify the production-signed SBMSIndirectDisplay package.',
            'Create and verify the replacement SWD\SBMS\VIRTUALDISPLAY-* device.',
            'Remove only legacy devices proven by legacy instance, hardware-ID, or service ownership.',
            'Verify the SBMS device and physical displays before deleting any legacy package.',
            'Delete only unbound legacy IddSampleDriver.inf packages.',
            'On any failed verification, restore the previous device/package state.'
        )
        nonTargets = @(
            'DISPLAY\DELD0E6 and other monitor IDs without a proven legacy SBMS parent',
            'Any unrelated physical display, display adapter, or driver package'
        )
    }
}

Export-ModuleMember -Function @(
    'Get-SBMSDriverMigrationInventory',
    'New-SBMSDriverMigrationPlan'
)
