Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Utf8NoBom = New-Object Text.UTF8Encoding($false)

function Get-SBMSCollectorUtc { [DateTime]::UtcNow.ToString('o') }

function Get-SBMSCollectorHash {
    param([Parameter(Mandatory)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { return $null }
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Write-SBMSCollectorFile {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][string]$Text)
    $parent = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($LiteralPath, $Text, $script:Utf8NoBom)
}

function Invoke-SBMSCollector {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$ArtifactDirectory)
    $started = Get-SBMSCollectorUtc
    try {
        $data = & $Action
        $envelope = [pscustomobject][ordered]@{ schemaVersion=1; collectorId=$Id; status='Captured'; capturedUtc=$started; data=$data; error=$null }
    } catch {
        $envelope = [pscustomobject][ordered]@{
            schemaVersion=1; collectorId=$Id; status='Failed'; capturedUtc=$started; data=$null
            error=[pscustomobject][ordered]@{ type=$_.Exception.GetType().FullName; message=$_.Exception.Message; hresult=$_.Exception.HResult }
        }
    }
    Write-SBMSCollectorFile (Join-Path $ArtifactDirectory ($Id + '.json')) ($envelope | ConvertTo-Json -Depth 30)
    $envelope
}

function Invoke-SBMSGitText {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string[]]$Arguments)
    $git = (Get-Command git.exe -ErrorAction Stop).Source
    $nativeArgs = @('-C', $RepositoryRoot) + $Arguments
    $output = @(& $git @nativeArgs 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "git $($Arguments -join ' ') failed with exit code ${exitCode}: $($output -join ' ')" }
    ($output -join [Environment]::NewLine).Trim()
}

function Get-SBMSClassification {
    param([string]$Identity, [string]$Provider, $Policy, [switch]$DisplayProvider)
    if ($Identity -match [string]$Policy.blockingNamePattern) { return 'blocking' }
    if ($DisplayProvider -and $Provider -match [string]$Policy.allowedDisplayProviderPattern) { return 'allowed' }
    'unknown'
}

function Get-SBMSGateARealEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][guid]$RunId,
        [Parameter(Mandatory)][string]$RunDirectory,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][bool]$EvidenceProtected,
        [string]$PolicyPath = (Join-Path $PSScriptRoot 'gate-a-policy.json')
    )
    $repo = [IO.Path]::GetFullPath($RepositoryRoot)
    $payload = [IO.Path]::GetFullPath($PayloadRoot)
    $artifactDirectory = Join-Path ([IO.Path]::GetFullPath($RunDirectory)) 'gate-a\collectors'
    if (-not (Test-Path -LiteralPath $artifactDirectory)) { New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null }
    $policy = Get-Content -LiteralPath $PolicyPath -Raw -Encoding UTF8 | ConvertFrom-Json

    $machine = Invoke-SBMSCollector 'machine' {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $boot = ([DateTime]$os.LastBootUpTime).ToUniversalTime()
        [pscustomobject][ordered]@{
            computerName=$env:COMPUTERNAME; osBuild=[string]$os.BuildNumber; osVersion=[string]$os.Version
            lastBootUtc=$boot.ToString('o'); lastBootUnixSeconds=([DateTimeOffset]$boot).ToUnixTimeSeconds()
        }
    } $artifactDirectory

    $evidenceSecurity = Invoke-SBMSCollector 'evidenceSecurity' {
        [pscustomobject][ordered]@{ protected=$EvidenceProtected; structuredReadbackRequired=$true }
    } $artifactDirectory

    $repository = Invoke-SBMSCollector 'repository' {
        $payloads = foreach ($entry in @($policy.payloads)) {
            $base = if ([string]$entry.base -ceq 'payload') { $payload } else { $repo }
            $path = [IO.Path]::GetFullPath((Join-Path $base ([string]$entry.path)))
            $exists = Test-Path -LiteralPath $path -PathType Leaf
            $signature = $null
            if ($exists -and [IO.Path]::GetExtension($path) -in @('.cat','.dll','.exe')) {
                $signature = [string](Get-AuthenticodeSignature -LiteralPath $path).Status
            }
            [pscustomobject][ordered]@{ role=[string]$entry.role; path=$path; exists=$exists; length=if($exists){(Get-Item -LiteralPath $path).Length}else{0}; sha256=if($exists){Get-SBMSCollectorHash $path}else{$null}; signature=$signature }
        }
        $status = Invoke-SBMSGitText $repo @('status','--porcelain=v1','--untracked-files=all')
        [pscustomobject][ordered]@{
            root=(Invoke-SBMSGitText $repo @('rev-parse','--show-toplevel')); commit=(Invoke-SBMSGitText $repo @('rev-parse','HEAD'))
            branch=(Invoke-SBMSGitText $repo @('branch','--show-current')); worktreeClean=[string]::IsNullOrWhiteSpace($status)
            status=$status; payloads=@($payloads)
        }
    } $artifactDirectory

    $auditOnly = Invoke-SBMSCollector 'auditOnly' {
        $observerDirectory = Join-Path $artifactDirectory 'audit-only'
        $observer = Join-Path $repo 'test-sbms-hardware.ps1'
        $winps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $nativeArgs = @('-NoLogo','-NoProfile','-NonInteractive','-File',$observer,'-Scenario','AuditOnly','-EvidenceDirectory',$observerDirectory)
        & $winps @nativeArgs | Out-Null
        $observerExitCode = $LASTEXITCODE
        $summaryPath = Join-Path $observerDirectory 'summary.json'
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) { throw "AuditOnly summary missing after exit $observerExitCode." }
        $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
        [pscustomobject][ordered]@{
            result=[string]$summary.result; exitCode=$observerExitCode; observationOnly=[bool]$summary.observationOnly
            driverInstallOrRemovalAttempted=[bool]$summary.driverInstallOrRemovalAttempted
            checks=@($summary.checks)
        }
    } $artifactDirectory

    $bcd = Invoke-SBMSCollector 'bcd' {
        $hardwareModule = Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1'
        Import-Module $hardwareModule -Force -ErrorAction Stop
        $snapshot = Get-SBMSHardwareLabSnapshot -Adapter (New-SBMSHardwareLabAdapter) -RunDirectory (Join-Path $artifactDirectory 'bcd-raw')
        [pscustomobject][ordered]@{
            currentGuid=[string]$snapshot.bcd.currentGuid; defaultGuid=[string]$snapshot.bcd.defaultGuid
            resolvedDefaultGuid=[string]$snapshot.bcd.resolvedDefaultGuid; displayOrder=@($snapshot.bcd.displayOrder)
            bootSequence=@($snapshot.bcd.bootSequence); testSigning=([regex]::IsMatch([string]$snapshot.bcd.currentText,'(?im)^\s*testsigning\s+(yes|on|true|是|开)\s*$'))
            bcdAllSha256=[string]$snapshot.bcdAllSha256
        }
    } $artifactDirectory

    $systemIntegrity = Invoke-SBMSCollector 'systemIntegrity' {
        $secureBoot = Confirm-SecureBootUEFI -ErrorAction Stop
        $deviceGuard = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard -ErrorAction Stop
        $running = @($deviceGuard.SecurityServicesRunning | ForEach-Object { [int]$_ })
        [pscustomobject][ordered]@{
            secureBootEnabled=[bool]$secureBoot; codeIntegrityKnown=$true
            virtualizationBasedSecurityStatus=[int]$deviceGuard.VirtualizationBasedSecurityStatus
            securityServicesConfigured=@($deviceGuard.SecurityServicesConfigured | ForEach-Object { [int]$_ })
            securityServicesRunning=$running; testSigningCompatible=(-not [bool]$secureBoot -and $running -notcontains 1)
        }
    } $artifactDirectory

    $bitLocker = Invoke-SBMSCollector 'bitLocker' {
        $volume = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop
        [pscustomobject][ordered]@{
            mountPoint=[string]$volume.MountPoint; volumeStatus=[string]$volume.VolumeStatus; protectionStatus=[string]$volume.ProtectionStatus
            lockStatus=[string]$volume.LockStatus; encryptionPercentage=[double]$volume.EncryptionPercentage
            protectionOn=([string]$volume.ProtectionStatus -eq 'On')
        }
    } $artifactDirectory

    $pendingReboot = Invoke-SBMSCollector 'pendingReboot' {
        $cbs = Test-Path -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
        $wu = Test-Path -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
        $rename = @((Get-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations)
        $renameOperations = @()
        for ($index = 0; $index -lt $rename.Count; $index += 2) {
            $source = [string]$rename[$index]
            $destination = if ($index + 1 -lt $rename.Count) { [string]$rename[$index + 1] } else { '' }
            $classification = if (($source + ' ' + $destination) -match [string]$policy.blockingNamePattern) { 'blocking' } else { 'external' }
            $renameOperations += [pscustomobject][ordered]@{ source=$source; destination=$destination; classification=$classification }
        }
        $blockingRenameCount = @($renameOperations | Where-Object classification -eq 'blocking').Count
        [pscustomobject][ordered]@{
            cbs=$cbs; windowsUpdate=$wu; sccm=$false
            pendingFileRenameCount=$renameOperations.Count
            blockingPendingFileRenameCount=$blockingRenameCount
            externalPendingFileRenameCount=($renameOperations.Count - $blockingRenameCount)
            pendingFileRenames=$renameOperations
            any=($cbs -or $wu -or $blockingRenameCount -gt 0)
        }
    } $artifactDirectory

    $pnp = Invoke-SBMSCollector 'pnp' {
        $devices = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.Class -in @('Display','Monitor') -or $_.FriendlyName -match [string]$policy.blockingNamePattern } | ForEach-Object {
            $identity = @([string]$_.FriendlyName,[string]$_.InstanceId) -join ' '
            $classification = if ($identity -match [string]$policy.blockingNamePattern) { 'blocking' } elseif ($_.Class -in @('Display','Monitor')) { 'allowed' } else { 'unknown' }
            [pscustomobject][ordered]@{ class=[string]$_.Class; friendlyName=[string]$_.FriendlyName; instanceId=[string]$_.InstanceId; status=[string]$_.Status; problem=[int]$_.Problem; classification=$classification }
        } | Sort-Object instanceId)
        [pscustomobject][ordered]@{ devices=$devices }
    } $artifactDirectory

    $driverStore = Invoke-SBMSCollector 'driverStore' {
        $drivers = @(Get-WindowsDriver -Online -All -ErrorAction Stop | Where-Object { [string]$_.ClassName -ieq 'Display' } | ForEach-Object {
            $identity = @([string]$_.OriginalFileName,[string]$_.ProviderName) -join ' '
            [pscustomobject][ordered]@{
                publishedName=[string]$_.Driver; originalFileName=[string]$_.OriginalFileName; provider=[string]$_.ProviderName
                version=[string]$_.Version; date=[string]$_.Date; className=[string]$_.ClassName
                classification=(Get-SBMSClassification -Identity $identity -Provider ([string]$_.ProviderName) -Policy $policy -DisplayProvider)
            }
        } | Sort-Object publishedName)
        $pnputil = Join-Path $env:SystemRoot 'System32\pnputil.exe'
        $raw = @(& $pnputil '/enum-drivers' '/class' 'Display' '/files' 2>&1)
        $rawExit = $LASTEXITCODE
        Write-SBMSCollectorFile (Join-Path $artifactDirectory 'driver-store-pnputil.txt') ($raw -join [Environment]::NewLine)
        if ($rawExit -ne 0) { throw "pnputil display inventory failed with exit code $rawExit." }
        [pscustomobject][ordered]@{ packages=$drivers; pnputilExitCode=$rawExit }
    } $artifactDirectory

    $displayConfig = Invoke-SBMSCollector 'displayConfig' {
        $source = Join-Path $PSScriptRoot 'SBMS.DisplayConfig.cs'
        if ($null -eq ('SBMSDisplayConfig' -as [type])) { Add-Type -TypeDefinition (Get-Content -LiteralPath $source -Raw -Encoding UTF8) -Language CSharp -ErrorAction Stop }
        $paths = @([SBMSDisplayConfig]::GetActivePaths() | Sort-Object AdapterLuid,SourceId,TargetId)
        [pscustomobject][ordered]@{ activePaths=$paths; healthyPhysicalPathCount=@($paths | Where-Object { $_.Active -and $_.TargetAvailable -and $_.Classification -eq 'physical' }).Count }
    } $artifactDirectory

    $runtime = Invoke-SBMSCollector 'runtime' {
        $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { @([string]$_.Name,[string]$_.ExecutablePath) -join ' ' -match [string]$policy.blockingNamePattern } | ForEach-Object {
            [pscustomobject][ordered]@{ name=[string]$_.Name; executablePath=[string]$_.ExecutablePath; classification='blocking' }
        } | Sort-Object name,executablePath)
        $services = @(Get-CimInstance Win32_Service -ErrorAction Stop | Where-Object { @([string]$_.Name,[string]$_.DisplayName,[string]$_.PathName) -join ' ' -match [string]$policy.blockingNamePattern } | ForEach-Object {
            [pscustomobject][ordered]@{ name=[string]$_.Name; state=[string]$_.State; startMode=[string]$_.StartMode; pathName=[string]$_.PathName; classification='blocking' }
        } | Sort-Object name)
        [pscustomobject][ordered]@{ processes=$processes; services=$services }
    } $artifactDirectory

    $startup = Invoke-SBMSCollector 'startup' {
        $entries = New-Object 'Collections.Generic.List[object]'
        foreach ($task in @(Get-ScheduledTask -ErrorAction Stop)) {
            $triggers = @($task.Triggers | ForEach-Object {
                if ($null -eq $_) { return }
                if ($null -ne $_.PSObject.Properties['CimClass'] -and $null -ne $_.CimClass) { [string]$_.CimClass.CimClassName }
                else { [string]$_.GetType().Name }
            })
            if (-not @($triggers | Where-Object { $_ -match 'BootTrigger|LogonTrigger' }).Count) { continue }
            $actionText = @($task.Actions | Where-Object { $null -ne $_ } | ForEach-Object {
                $execute = if ($null -ne $_.PSObject.Properties['Execute']) { [string]$_.Execute } else { '' }
                $arguments = if ($null -ne $_.PSObject.Properties['Arguments']) { [string]$_.Arguments } else { '' }
                @($execute,$arguments) -join ' '
            }) -join '; '
            $taskIdentity = [string]$task.TaskPath + [string]$task.TaskName
            $identity = @($taskIdentity,$actionText) -join ' '
            $explicitlyAllowed = @($policy.allowedStartupIdentityPatterns | Where-Object { $taskIdentity -match [string]$_ }).Count -gt 0
            $classification = if ($explicitlyAllowed) { 'allowed' } elseif ($identity -match [string]$policy.blockingNamePattern) { 'blocking' } elseif ([string]$task.TaskPath -like '\Microsoft\*') { 'allowed' } elseif ([string]::IsNullOrWhiteSpace($actionText)) { 'unknown' } else { 'allowed' }
            $entries.Add([pscustomobject][ordered]@{ kind='ScheduledTask'; identity=$taskIdentity; actions=$actionText; classification=$classification })
        }
        foreach ($key in @('Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce','Registry::HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','Registry::HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce')) {
            if (-not (Test-Path -LiteralPath $key)) { continue }
            $item = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            foreach ($property in @($item.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' })) {
                $identity = @($property.Name,[string]$property.Value) -join ' '
                $classification = if ($identity -match [string]$policy.blockingNamePattern) { 'blocking' } elseif ([string]::IsNullOrWhiteSpace([string]$property.Value)) { 'unknown' } else { 'allowed' }
                $entries.Add([pscustomobject][ordered]@{ kind='RunKey'; identity=$key+'::'+$property.Name; actions=[string]$property.Value; classification=$classification })
            }
        }
        [pscustomobject][ordered]@{ entries=@($entries.ToArray() | Sort-Object kind,identity) }
    } $artifactDirectory

    [pscustomobject][ordered]@{
        machine=$machine; evidenceSecurity=$evidenceSecurity; repository=$repository; auditOnly=$auditOnly; bcd=$bcd; systemIntegrity=$systemIntegrity
        bitLocker=$bitLocker; pendingReboot=$pendingReboot; pnp=$pnp; driverStore=$driverStore
        displayConfig=$displayConfig; runtime=$runtime; startup=$startup
    }
}

Export-ModuleMember -Function Get-SBMSGateARealEvidence
