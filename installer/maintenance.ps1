[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Install', 'PrepareUpgrade', 'Stop', 'PreflightUninstall', 'Uninstall')]
    [string]$Action,

    [Parameter(Mandatory)]
    [string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$taskPath = '\SBMS\'
$taskName = 'Tray-7EB4D7A8-16A9-4B6F-82E3-31A77BC81B6A'
$legacyTaskPath = '\'
$legacyTaskName = 'SBMS Tray'
$tray = Join-Path $InstallRoot 'sbms-tray.exe'
$cli = Join-Path $InstallRoot 'sbms.exe'
$driverInf = Join-Path $InstallRoot 'driver\SBMSIndirectDisplay.inf'
$deviceInstance = 'SWD\SBMS\VirtualDisplay-01'
$configRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SBMS'

function Get-InteractiveIdentity {
    $sessionId = (Get-Process -Id $PID).SessionId
    $explorer = Get-Process -Name 'explorer' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId } |
        Select-Object -First 1
    if (-not $explorer) {
        throw 'No interactive Explorer process exists in the installer session.'
    }
    $process = Get-CimInstance -ClassName Win32_Process `
        -Filter "ProcessId = $($explorer.Id)"
    $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner
    if ($owner.ReturnValue -ne 0 -or -not $owner.User) {
        throw 'The interactive user identity could not be resolved.'
    }
    $name = if ($owner.Domain) {
        "$($owner.Domain)\$($owner.User)"
    }
    else {
        $owner.User
    }
    $account = [Security.Principal.NTAccount]::new($name)
    $sid = $account.Translate([Security.Principal.SecurityIdentifier])
    [pscustomobject]@{
        Name = $name
        Sid = $sid.Value
    }
}

function Assert-InstallIdentity {
    $interactive = Get-InteractiveIdentity
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($interactive.Sid -ne $current.User.Value) {
        throw (
            'SBMS must be installed from the intended administrator account. ' +
            'Over-the-shoulder credentials would register auto-start for the wrong user.'
        )
    }
    $principal = [Security.Principal.WindowsPrincipal]::new($current)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'SBMS setup maintenance requires an elevated administrator token.'
    }
    $interactive
}

function Get-InstalledSbmsProcesses {
    $targets = @(
        [IO.Path]::GetFullPath($tray),
        [IO.Path]::GetFullPath($cli)
    )
    $found = @()
    foreach ($process in Get-Process -Name 'sbms-tray', 'sbms' `
        -ErrorAction SilentlyContinue) {
        try {
            $path = $process.Path
        }
        catch {
            throw "Cannot verify path for SBMS-named process $($process.Id)."
        }
        if (-not $path) {
            throw "Cannot verify path for SBMS-named process $($process.Id)."
        }
        $fullPath = [IO.Path]::GetFullPath($path)
        $matchesInstalledPath = $false
        foreach ($target in $targets) {
            if ($target.Equals(
                $fullPath,
                [StringComparison]::OrdinalIgnoreCase)) {
                $matchesInstalledPath = $true
                break
            }
        }
        if ($matchesInstalledPath) {
            $found += $process
        }
    }
    $found
}

function Stop-Sbms {
    $running = @(Get-InstalledSbmsProcesses)
    if ($running.Count -eq 0) {
        return
    }
    if (-not (Test-Path -LiteralPath $cli)) {
        throw 'An installed SBMS process is running but its shutdown CLI is missing.'
    }

    & $cli shutdown | Out-Null
    $shutdownExit = $LASTEXITCODE
    if ($shutdownExit -ne 0) {
        throw (
            'The installed SBMS version has no compatible graceful shutdown channel. ' +
            'Stop the mapping and exit its tray before upgrading.'
        )
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-InstalledSbmsProcesses).Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'An installed SBMS process did not stop within 30 seconds.'
}

function Backup-SbmsConfiguration {
    $persistentNames = @(
        'config-v1.json'
        'config-v2.json'
        'display-overrides-v1.json'
    )
    $sources = @(
        $persistentNames |
            ForEach-Object { Join-Path $configRoot $_ } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    )
    if ($sources.Count -eq 0) {
        return
    }

    $backupRoot = Join-Path $configRoot 'upgrade-backups'
    $stamp = [DateTime]::UtcNow.ToString(
        'yyyyMMddTHHmmssfffZ',
        [Globalization.CultureInfo]::InvariantCulture)
    $snapshot = Join-Path $backupRoot $stamp
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null

    $entries = @()
    foreach ($source in $sources) {
        $name = Split-Path -Leaf $source
        $destination = Join-Path $snapshot $name
        Copy-Item -LiteralPath $source -Destination $destination
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $backupHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($sourceHash -ne $backupHash) {
            throw "Configuration snapshot verification failed for $name."
        }
        $item = Get-Item -LiteralPath $destination
        $entries += [pscustomobject]@{
            name = $name
            bytes = $item.Length
            sha256 = $backupHash
        }
    }

    $manifest = [pscustomobject]@{
        created_utc = [DateTime]::UtcNow.ToString('o')
        files = $entries
    }
    $manifestPath = Join-Path $snapshot 'manifest.json'
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson,
        [Text.UTF8Encoding]::new($false))
}

function Prepare-SbmsUpgrade {
    Assert-InstallIdentity | Out-Null
    Stop-Sbms
    Backup-SbmsConfiguration
}

function Get-VerifiedTask {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $task = Get-ScheduledTask -TaskPath $Path -TaskName $Name `
        -ErrorAction SilentlyContinue
    if (-not $task) {
        return $null
    }
    if ($task.Actions.Count -ne 1 -or
        -not $task.Actions[0].Execute.Equals(
            $tray,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Scheduled task collision at $Path$Name."
    }
    $task
}

function Get-OwnedTask {
    Get-VerifiedTask -Path $taskPath -Name $taskName
}

function Get-LegacyOwnedTask {
    Get-VerifiedTask -Path $legacyTaskPath -Name $legacyTaskName
}

function Remove-SbmsTask {
    $tasks = @(
        [pscustomobject]@{
            Task = Get-OwnedTask
            Path = $taskPath
            Name = $taskName
        },
        [pscustomobject]@{
            Task = Get-LegacyOwnedTask
            Path = $legacyTaskPath
            Name = $legacyTaskName
        }
    )
    foreach ($entry in $tasks) {
        if (-not $entry.Task) {
            continue
        }
        Stop-ScheduledTask -TaskPath $entry.Path -TaskName $entry.Name `
            -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskPath $entry.Path -TaskName $entry.Name `
            -Confirm:$false
    }
}

function Get-OwnedTaskSnapshots {
    $entries = @(
        [pscustomobject]@{
            Task = Get-OwnedTask
            Path = $taskPath
            Name = $taskName
        },
        [pscustomobject]@{
            Task = Get-LegacyOwnedTask
            Path = $legacyTaskPath
            Name = $legacyTaskName
        }
    )
    foreach ($entry in $entries) {
        if (-not $entry.Task) {
            continue
        }
        [pscustomobject]@{
            Path = $entry.Path
            Name = $entry.Name
            Xml = Export-ScheduledTask `
                -TaskPath $entry.Path `
                -TaskName $entry.Name
            WasRunning = $entry.Task.State -eq 'Running'
        }
    }
}

function Restore-SbmsTasks {
    param([object[]]$Snapshots)

    Remove-SbmsTask
    foreach ($snapshot in $Snapshots) {
        Register-ScheduledTask `
            -TaskPath $snapshot.Path `
            -TaskName $snapshot.Name `
            -Xml $snapshot.Xml |
            Out-Null
        if ($snapshot.WasRunning) {
            Start-ScheduledTask `
                -TaskPath $snapshot.Path `
                -TaskName $snapshot.Name
        }
    }
}

function Install-SbmsTask {
    $interactive = Assert-InstallIdentity
    if (Get-OwnedTask) {
        throw "Owned scheduled task already exists at $taskPath$taskName."
    }
    $taskAction = New-ScheduledTaskAction -Execute $tray `
        -WorkingDirectory $InstallRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $interactive.Sid
    $principal = New-ScheduledTaskPrincipal `
        -UserId $interactive.Sid `
        -LogonType Interactive `
        -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew
    Register-ScheduledTask `
        -TaskPath $taskPath `
        -TaskName $taskName `
        -Action $taskAction `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Description 'Starts the SBMS tray with the privileges required by its protected session gate.' |
        Out-Null
    Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-InstalledSbmsProcesses).Count -gt 0) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'The SBMS tray did not start from its registered logon task.'
}

function Find-SbmsDriverPackages {
    $infRoot = Join-Path $env:SystemRoot 'INF'
    foreach ($file in Get-ChildItem -LiteralPath $infRoot -Filter 'oem*.inf' -File) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match '(?i)SBMS\\IndirectDisplay' -and
            $text -match '(?i)Provider\s*=\s*"SBMS"' -and
            $text -match '(?i)4D36E968-E325-11CE-BFC1-08002BE10318') {
            $file
        }
    }
}

function Find-ObsoleteSbmsDriverPackages {
    $sourceText = [IO.File]::ReadAllText($driverInf)
    $sourceMatch = [regex]::Match(
        $sourceText,
        '(?im)^\s*DriverVer\s*=\s*(?<value>.+?)\s*$'
    )
    if (-not $sourceMatch.Success) {
        throw "DriverVer is missing from $driverInf"
    }
    $currentVersion = $sourceMatch.Groups['value'].Value.Trim()

    foreach ($package in Find-SbmsDriverPackages) {
        $packageText = [IO.File]::ReadAllText($package.FullName)
        $packageMatch = [regex]::Match(
            $packageText,
            '(?im)^\s*DriverVer\s*=\s*(?<value>.+?)\s*$'
        )
        if (-not $packageMatch.Success -or
            $packageMatch.Groups['value'].Value.Trim() -ne $currentVersion) {
            $package
        }
    }
}

function Install-SbmsDriver {
    if (-not (Test-Path -LiteralPath $driverInf)) {
        throw "Driver package is missing: $driverInf"
    }
    $nativeArgs = @('/add-driver', $driverInf, '/install')
    & pnputil.exe @nativeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil add-driver failed with exit code $LASTEXITCODE"
    }
}

function Remove-DriverPackages {
    param([IO.FileInfo[]]$Packages)

    foreach ($package in $Packages) {
        $nativeArgs = @('/delete-driver', $package.Name, '/uninstall')
        & pnputil.exe @nativeArgs
        if ($LASTEXITCODE -ne 0) {
            throw "pnputil delete-driver $($package.Name) failed with exit code $LASTEXITCODE"
        }
    }
}

function Remove-SbmsDriver {
    $device = Get-PnpDevice -InstanceId $deviceInstance `
        -ErrorAction SilentlyContinue
    if ($device) {
        & pnputil.exe /remove-device $deviceInstance
        if ($LASTEXITCODE -ne 0) {
            throw "pnputil remove-device failed with exit code $LASTEXITCODE"
        }
    }
    Remove-DriverPackages -Packages @(Find-SbmsDriverPackages)
}

function Test-UninstallPreflight {
    Assert-InstallIdentity | Out-Null
    Get-InstalledSbmsProcesses | Out-Null
    Get-OwnedTask | Out-Null
    Get-LegacyOwnedTask | Out-Null
    Find-SbmsDriverPackages | Out-Null
}

function Install-Sbms {
    Assert-InstallIdentity | Out-Null
    $taskSnapshots = @(Get-OwnedTaskSnapshots)
    Remove-SbmsTask
    $before = @(
        Find-SbmsDriverPackages |
            ForEach-Object { $_.Name }
    )
    try {
        Install-SbmsDriver
        Install-SbmsTask
    }
    catch {
        $failure = $_
        Remove-SbmsTask
        $added = @(Find-SbmsDriverPackages) |
            Where-Object { $_.Name -notin $before }
        if ($added.Count -gt 0) {
            Remove-DriverPackages -Packages $added
        }
        if ($taskSnapshots.Count -gt 0) {
            try {
                Restore-SbmsTasks -Snapshots $taskSnapshots
            }
            catch {
                throw (
                    "$failure Startup-task compensation also failed: " +
                    $_.Exception.Message
                )
            }
        }
        throw $failure
    }

    # The new package and startup task are working. Old SBMS packages are no
    # longer useful for rollback and otherwise accumulate in DriverStore.
    Remove-DriverPackages -Packages @(Find-ObsoleteSbmsDriverPackages)
}

function Uninstall-Sbms {
    Test-UninstallPreflight
    Stop-Sbms
    $taskSnapshots = @(Get-OwnedTaskSnapshots)
    Remove-SbmsTask
    try {
        Remove-SbmsDriver
    }
    catch {
        $failure = $_
        try {
            Install-SbmsDriver
            if ($taskSnapshots.Count -gt 0) {
                Restore-SbmsTasks -Snapshots $taskSnapshots
            }
        }
        catch {
            throw (
                "$failure Compensation also failed: $($_.Exception.Message)"
            )
        }
        throw "$failure External state was restored; application files were retained."
    }
}

switch ($Action) {
    'PrepareUpgrade' {
        Prepare-SbmsUpgrade
    }
    'Stop' {
        Stop-Sbms
    }
    'PreflightUninstall' {
        Test-UninstallPreflight
    }
    'Install' {
        Install-Sbms
    }
    'Uninstall' {
        Uninstall-Sbms
    }
}
