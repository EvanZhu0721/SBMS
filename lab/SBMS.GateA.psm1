Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:RequiredCollectors = @(
    'machine', 'evidenceSecurity', 'repository', 'auditOnly', 'bcd', 'systemIntegrity', 'bitLocker',
    'pendingReboot', 'pnp', 'driverStore', 'displayConfig', 'runtime', 'startup'
)
$script:VolatileEvidenceProperties = @(
    'capturedUtc', 'evaluatedUtc', 'startedUtc', 'finishedUtc', 'observedUtc',
    'verifiedUtc', 'issuedUtc', 'expiresUtc', 'expiresUnixSeconds', 'consumedUtc',
    'pid', 'processId', 'parentProcessId', 'sessionId'
)

function Get-SBMSGateAUtc { [DateTime]::UtcNow.ToString('o') }

function Write-SBMSGateAAtomic {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][string]$Text)
    $directory = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = $script:Utf8NoBom.GetBytes($Text)
    $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $LiteralPath) {
        $backup = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.bak')
        [IO.File]::Replace($temporary, $LiteralPath, $backup)
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    } else { [IO.File]::Move($temporary, $LiteralPath) }
}

function Get-SBMSGateAHash {
    param([Parameter(Mandatory)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { return $null }
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function ConvertTo-SBMSGateAStableProjection {
    param($InputObject)
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [string] -or $InputObject -is [ValueType]) { return $InputObject }
    if ($InputObject -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($InputObject.Keys | Sort-Object)) {
            if ([string]$key -in $script:VolatileEvidenceProperties) { continue }
            $ordered[[string]$key] = ConvertTo-SBMSGateAStableProjection $InputObject[$key]
        }
        return [pscustomobject]$ordered
    }
    if ($InputObject -is [Collections.IEnumerable]) {
        return @($InputObject | ForEach-Object { ConvertTo-SBMSGateAStableProjection $_ })
    }
    $projected = [ordered]@{}
    foreach ($property in @($InputObject.PSObject.Properties | Sort-Object Name)) {
        if ($property.Name -in $script:VolatileEvidenceProperties) { continue }
        $projected[$property.Name] = ConvertTo-SBMSGateAStableProjection $property.Value
    }
    [pscustomobject]$projected
}

function ConvertTo-SBMSGateAStableJson {
    param([Parameter(Mandatory)]$InputObject)
    (ConvertTo-SBMSGateAStableProjection $InputObject) | ConvertTo-Json -Depth 30 -Compress
}

function Get-SBMSGateAObjectHash {
    param([Parameter(Mandatory)]$InputObject)
    $bytes = $script:Utf8NoBom.GetBytes((ConvertTo-SBMSGateAStableJson $InputObject))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') } finally { $sha.Dispose() }
}

function Get-SBMSGateAStringHash {
    param([Parameter(Mandatory)][string]$Value)
    $bytes = $script:Utf8NoBom.GetBytes($Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') } finally { $sha.Dispose() }
}

function New-SBMSGateAChallenge {
    param([Parameter(Mandatory)][string]$GateDirectory, [Parameter(Mandatory)][string]$RunId, [Parameter(Mandatory)][string]$StableDigest)
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $nonce = [Convert]::ToBase64String($bytes)
    $state = [pscustomobject][ordered]@{
        schemaVersion = 1; runId = $RunId; stableDigest = $StableDigest
        challengeSha256 = Get-SBMSGateAStringHash $nonce
        issuedUtc = Get-SBMSGateAUtc; expiresUtc = [DateTime]::UtcNow.AddMinutes(30).ToString('o')
        expiresUnixSeconds = [DateTimeOffset]::UtcNow.AddMinutes(30).ToUnixTimeSeconds(); consumedUtc = $null
    }
    Write-SBMSGateAAtomic (Join-Path $GateDirectory 'remote-challenge.json') ($state | ConvertTo-Json -Depth 10)
    $nonce
}

function New-SBMSGateARollbackPlan {
    param(
        [Parameter(Mandatory)][string]$GateDirectory,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$StableDigest,
        [Parameter(Mandatory)]$Evidence
    )
    $plan = [pscustomobject][ordered]@{
        schemaVersion = 1; runId = $RunId; baselineEvidenceRootSha256 = $StableDigest; createdUtc = Get-SBMSGateAUtc
        expectedOwnedTaskName = "SBMS-HardwareLab-Watchdog-$RunId"
        expectedCloneDescription = "SBMS LAB TestSigning ONE-TIME $RunId"
        baselineBcd = $Evidence.bcd.data
        expectedPayloads = @($Evidence.repository.data.payloads)
        exactInverseActions = @(
            'restore-owned-bootsequence', 'restore-owned-bcd-clone', 'remove-owned-driver-package',
            'restore-owned-startup-state', 'remove-owned-watchdog-task', 'verify-baseline-digest'
        )
        ownershipPredicates = @('same-run-id', 'same-machine-identity', 'same-payload-hash', 'exact-owned-resource-id')
    }
    $path = Join-Path $GateDirectory 'rollback-plan.json'
    Write-SBMSGateAAtomic $path ($plan | ConvertTo-Json -Depth 30)
    $path
}

function Update-SBMSGateAEvidenceIndex {
    param([Parameter(Mandatory)][string]$GateDirectory)
    $indexPath = Join-Path $GateDirectory 'evidence-index.json'
    $artifacts = @(
        Get-ChildItem -LiteralPath $GateDirectory -Recurse -File -Force | Where-Object {
            $_.FullName -ne $indexPath -and $_.Name -ne 'manifest.json' -and $_.Name -notlike '*.tmp'
        } | Sort-Object FullName | ForEach-Object {
            if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Evidence artifact is a reparse point: $($_.FullName)" }
            [pscustomobject][ordered]@{
                relativePath = $_.FullName.Substring($GateDirectory.TrimEnd('\').Length + 1).Replace('\','/')
                length = $_.Length; sha256 = Get-SBMSGateAHash $_.FullName
            }
        }
    )
    $index = [pscustomobject][ordered]@{ schemaVersion = 1; artifacts = $artifacts }
    Write-SBMSGateAAtomic $indexPath ($index | ConvertTo-Json -Depth 20)
    $indexPath
}

function Confirm-SBMSGateARemoteHealthCore {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][guid]$RunId,
        [Parameter(Mandatory)][string]$RunDirectory,
        [Parameter(Mandatory)][string]$Challenge,
        [Parameter(Mandatory)][scriptblock]$CaptureSession,
        [switch]$BitLockerRecoveryAccessVerified
    )
    $mutex = New-Object Threading.Mutex($false, ('Global\SBMS-GateA-Proof-' + $RunId.ToString('N')))
    $lockTaken = $false
    try {
        try { $lockTaken = $mutex.WaitOne([TimeSpan]::FromSeconds(30)) } catch [Threading.AbandonedMutexException] { $lockTaken = $true }
        if (-not $lockTaken) { throw 'Timed out waiting for the Gate A proof lock.' }
    $gateDirectory = Join-Path ([IO.Path]::GetFullPath($RunDirectory)) 'gate-a'
    $challengePath = Join-Path $gateDirectory 'remote-challenge.json'
    $proofPath = Join-Path $gateDirectory 'ssh-health-proof.json'
    if (-not (Test-Path -LiteralPath $challengePath -PathType Leaf)) { throw 'Gate A challenge does not exist.' }
    $state = Get-Content -LiteralPath $challengePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$state.runId -cne $RunId.ToString()) { throw 'Challenge Run ID mismatch.' }
    if ($null -ne $state.consumedUtc) { throw 'Gate A challenge was already consumed.' }
    if ([long]$state.expiresUnixSeconds -le [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) { throw 'Gate A challenge expired.' }
    if ((Get-SBMSGateAStringHash $Challenge) -cne [string]$state.challengeSha256) { throw 'Gate A challenge mismatch.' }
    if (Test-Path -LiteralPath $proofPath) { throw 'SSH health proof already exists for this run.' }
    $session = & $CaptureSession
    foreach ($property in @('sshdAncestor','nonLoopbackClient','adminCapable','evidenceReadable','activePhysicalDisplay','computerName','lastBootUtc','lastBootUnixSeconds','clientAddress')) {
        if ($null -eq $session.PSObject.Properties[$property]) { throw "SSH session evidence lacks '$property'." }
    }
    if (-not [bool]$session.sshdAncestor -or -not [bool]$session.nonLoopbackClient -or -not [bool]$session.adminCapable -or
        -not [bool]$session.evidenceReadable -or -not [bool]$session.activePhysicalDisplay) { throw 'SSH recovery session did not satisfy every health condition.' }
    $proof = [pscustomobject][ordered]@{
        schemaVersion = 1; runId = $RunId.ToString(); stableDigest = [string]$state.stableDigest
        challengeSha256 = [string]$state.challengeSha256; verifiedUtc = Get-SBMSGateAUtc
        computerName = [string]$session.computerName; lastBootUtc = [string]$session.lastBootUtc
        lastBootUnixSeconds = [long]$session.lastBootUnixSeconds; clientAddress = [string]$session.clientAddress
        sshdAncestor = $true; nonLoopbackClient = $true; adminCapable = $true; evidenceReadable = $true
        activePhysicalDisplay = $true; bitLockerRecoveryAccessVerified = [bool]$BitLockerRecoveryAccessVerified
    }
    $state | Add-Member -NotePropertyName consumeStartedUtc -NotePropertyValue (Get-SBMSGateAUtc) -Force
    Write-SBMSGateAAtomic $challengePath ($state | ConvertTo-Json -Depth 10)
    Write-SBMSGateAAtomic $proofPath ($proof | ConvertTo-Json -Depth 12)
    $state.consumedUtc = Get-SBMSGateAUtc
    $state | Add-Member -NotePropertyName consumedProofSha256 -NotePropertyValue (Get-SBMSGateAHash $proofPath) -Force
    Write-SBMSGateAAtomic $challengePath ($state | ConvertTo-Json -Depth 10)
    $proof
    } finally {
        if ($lockTaken) { try { $mutex.ReleaseMutex() } catch {} }
        $mutex.Dispose()
    }
}

function Test-SBMSGateASshdAncestor {
    $id = $PID
    for ($depth = 0; $depth -lt 32 -and $id -gt 0; $depth++) {
        $process = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $id) -ErrorAction Stop
        if ([string]$process.Name -ieq 'sshd.exe') { return $true }
        $id = [int]$process.ParentProcessId
    }
    $false
}

function Get-SBMSGateARemoteSessionEvidence {
    param([Parameter(Mandatory)][string]$RunDirectory)
    $parts = @(([string]$env:SSH_CONNECTION).Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -ne 4) { throw 'SSH_CONNECTION is absent or malformed.' }
    $client = [Net.IPAddress]::Parse($parts[0])
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $manifestPath = Join-Path $RunDirectory 'gate-a\manifest.json'
    $null = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 -ErrorAction Stop
    $displaySource = Join-Path $PSScriptRoot 'SBMS.DisplayConfig.cs'
    if ($null -eq ('SBMSDisplayConfig' -as [type])) { Add-Type -TypeDefinition (Get-Content -LiteralPath $displaySource -Raw -Encoding UTF8) -Language CSharp -ErrorAction Stop }
    $paths = @([SBMSDisplayConfig]::GetActivePaths())
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $lastBoot = ([DateTime]$os.LastBootUpTime).ToUniversalTime()
    [pscustomobject][ordered]@{
        sshdAncestor = Test-SBMSGateASshdAncestor
        nonLoopbackClient = -not [Net.IPAddress]::IsLoopback($client)
        adminCapable = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        evidenceReadable = $true
        activePhysicalDisplay = @($paths | Where-Object { $_.Active -and $_.TargetAvailable -and $_.Classification -eq 'physical' }).Count -gt 0
        computerName = $env:COMPUTERNAME
        lastBootUtc = $lastBoot.ToString('o')
        lastBootUnixSeconds = ([DateTimeOffset]$lastBoot).ToUnixTimeSeconds()
        clientAddress = $client.ToString()
    }
}

function Confirm-SBMSGateARemoteHealth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][guid]$RunId,
        [Parameter(Mandatory)][string]$RunDirectory,
        [Parameter(Mandatory)][string]$Challenge,
        [switch]$BitLockerRecoveryAccessVerified
    )
    $expected = [IO.Path]::GetFullPath((Join-Path 'C:\ProgramData\SBMSLab\Runs' $RunId.ToString()))
    $actual = [IO.Path]::GetFullPath($RunDirectory)
    if ($actual -cne $expected) { throw 'Production SSH proof is restricted to the fixed ProgramData Run-ID directory.' }
    $item = Get-Item -LiteralPath $actual -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Run directory must not be a reparse point.' }
    $sessionEvidence = Get-SBMSGateARemoteSessionEvidence -RunDirectory $actual
    $capture = { $sessionEvidence }.GetNewClosure()
    Confirm-SBMSGateARemoteHealthCore -RunId $RunId -RunDirectory $actual -Challenge $Challenge -CaptureSession $capture -BitLockerRecoveryAccessVerified:$BitLockerRecoveryAccessVerified
}

function Add-SBMSGateACheck {
    param([Collections.Generic.List[object]]$Checks, [string]$Id, [string]$Status, [string]$Reason)
    $Checks.Add([pscustomobject][ordered]@{ id = $Id; status = $Status; reason = $Reason })
}

function Test-SBMSGateAEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$StableDigest,
        $RemoteProof,
        [string]$ChallengeSha256
    )
    $checks = New-Object 'Collections.Generic.List[object]'
    $collectorMap = @{}
    foreach ($name in $script:RequiredCollectors) {
        $collector = $Evidence.PSObject.Properties[$name]
        if ($null -eq $collector) {
            Add-SBMSGateACheck $checks "collector.$name" 'INCONCLUSIVE' 'Required collector is absent.'
            continue
        }
        $collectorMap[$name] = $collector.Value
        $status = [string]$collector.Value.status
        if ($status -cne 'Captured') {
            Add-SBMSGateACheck $checks "collector.$name" 'INCONCLUSIVE' "Collector status is '$status'."
        } else { Add-SBMSGateACheck $checks "collector.$name" 'PASS' 'Captured.' }
    }

    $requiredDataFields = [ordered]@{
        machine=@('computerName','lastBootUtc','lastBootUnixSeconds'); evidenceSecurity=@('protected','structuredReadbackRequired')
        repository=@('worktreeClean','payloads'); auditOnly=@('result','observationOnly','driverInstallOrRemovalAttempted','checks')
        bcd=@('testSigning','bootSequence'); systemIntegrity=@('secureBootEnabled','codeIntegrityKnown','testSigningCompatible')
        bitLocker=@('protectionOn'); pendingReboot=@('any'); pnp=@('devices'); driverStore=@('packages')
        displayConfig=@('activePaths'); runtime=@('processes','services'); startup=@('entries')
    }
    $schemaComplete = @{}
    foreach ($collectorName in $requiredDataFields.Keys) {
        if (-not $collectorMap.ContainsKey($collectorName) -or $collectorMap[$collectorName].status -cne 'Captured') { continue }
        $schemaComplete[$collectorName] = $true
        if ($null -eq $collectorMap[$collectorName].data) {
            $schemaComplete[$collectorName] = $false
            Add-SBMSGateACheck $checks "collector.$collectorName.schema" 'INCONCLUSIVE' 'Collector data is absent.'
            continue
        }
        foreach ($field in $requiredDataFields[$collectorName]) {
            if ($null -eq $collectorMap[$collectorName].data.PSObject.Properties[$field]) {
                $schemaComplete[$collectorName] = $false
                Add-SBMSGateACheck $checks "collector.$collectorName.schema" 'INCONCLUSIVE' "Required field '$field' is absent."
            }
        }
    }

    if ($collectorMap.ContainsKey('machine') -and $collectorMap.machine.status -eq 'Captured' -and $schemaComplete['machine']) {
        foreach ($property in @('computerName','lastBootUtc','lastBootUnixSeconds')) {
            if ($null -eq $collectorMap.machine.data.PSObject.Properties[$property] -or [string]::IsNullOrWhiteSpace([string]$collectorMap.machine.data.$property)) {
                Add-SBMSGateACheck $checks 'machine.identity' 'INCONCLUSIVE' "Machine identity lacks '$property'."
            }
        }
    }
    if ($collectorMap.ContainsKey('evidenceSecurity') -and $collectorMap.evidenceSecurity.status -eq 'Captured' -and $schemaComplete['evidenceSecurity']) {
        if (-not [bool]$collectorMap.evidenceSecurity.data.protected -or -not [bool]$collectorMap.evidenceSecurity.data.structuredReadbackRequired) {
            Add-SBMSGateACheck $checks 'evidenceSecurity.protected' 'FAIL' 'Gate A evidence is not protected by verified SYSTEM/Administrators ACLs.'
        } else { Add-SBMSGateACheck $checks 'evidenceSecurity.protected' 'PASS' 'Protected evidence ACL is required and will be read back.' }
    }

    if ($collectorMap.ContainsKey('repository') -and $collectorMap.repository.status -eq 'Captured' -and $schemaComplete['repository']) {
        if (-not [bool]$collectorMap.repository.data.worktreeClean) { Add-SBMSGateACheck $checks 'repository.clean' 'FAIL' 'Repository worktree is dirty.' }
        elseif (-not @($collectorMap.repository.data.payloads).Count -or @($collectorMap.repository.data.payloads | Where-Object { -not $_.exists -or [string]::IsNullOrWhiteSpace([string]$_.sha256) }).Count) {
            Add-SBMSGateACheck $checks 'repository.payloads' 'FAIL' 'A required payload is missing or unhashed.'
        } elseif (@($collectorMap.repository.data.payloads | Where-Object { $_.role -in @('driver-cat','driver-dll') -and $_.signature -cne 'Valid' }).Count) {
            Add-SBMSGateACheck $checks 'repository.signatures' 'FAIL' 'A required driver CAT or DLL signature is not valid.'
        } else { Add-SBMSGateACheck $checks 'repository.clean' 'PASS' 'Repository and payload hashes are complete.' }
    }
    if ($collectorMap.ContainsKey('auditOnly') -and $collectorMap.auditOnly.status -eq 'Captured' -and $schemaComplete['auditOnly']) {
        $criticalSkip = @($collectorMap.auditOnly.data.checks | Where-Object { $_.status -eq 'SKIP' -and $_.name -in @('PnpAudit','DriverAudit','NativeListAudit') })
        $criticalPass = @($collectorMap.auditOnly.data.checks | Where-Object { $_.status -eq 'PASS' -and $_.name -in @('PnpAudit','DriverAudit','NativeListAudit') } | Select-Object -ExpandProperty name -Unique)
        if ([string]$collectorMap.auditOnly.data.result -cne 'PASS' -or $criticalSkip.Count -or $criticalPass.Count -ne 3 -or
            -not [bool]$collectorMap.auditOnly.data.observationOnly -or [bool]$collectorMap.auditOnly.data.driverInstallOrRemovalAttempted) {
            Add-SBMSGateACheck $checks 'auditOnly.result' 'FAIL' 'AuditOnly did not produce complete observation-only PASS evidence.'
        }
        else { Add-SBMSGateACheck $checks 'auditOnly.result' 'PASS' 'AuditOnly evidence is complete.' }
    }
    if ($collectorMap.ContainsKey('bcd') -and $collectorMap.bcd.status -eq 'Captured' -and $schemaComplete['bcd']) {
        if ([bool]$collectorMap.bcd.data.testSigning -or @($collectorMap.bcd.data.bootSequence).Count) { Add-SBMSGateACheck $checks 'bcd.safeBaseline' 'FAIL' 'BCD already has testsigning or a one-time boot sequence.' }
        else { Add-SBMSGateACheck $checks 'bcd.safeBaseline' 'PASS' 'BCD is at a safe baseline.' }
    }
    if ($collectorMap.ContainsKey('systemIntegrity') -and $collectorMap.systemIntegrity.status -eq 'Captured' -and $schemaComplete['systemIntegrity']) {
        if ($null -eq $collectorMap.systemIntegrity.data.secureBootEnabled -or $null -eq $collectorMap.systemIntegrity.data.codeIntegrityKnown) { Add-SBMSGateACheck $checks 'integrity.known' 'INCONCLUSIVE' 'Secure Boot or Code Integrity state is unknown.' }
        elseif ([bool]$collectorMap.systemIntegrity.data.secureBootEnabled -or -not [bool]$collectorMap.systemIntegrity.data.codeIntegrityKnown -or
            ($null -ne $collectorMap.systemIntegrity.data.PSObject.Properties['testSigningCompatible'] -and -not [bool]$collectorMap.systemIntegrity.data.testSigningCompatible)) {
            Add-SBMSGateACheck $checks 'integrity.compatible' 'FAIL' 'System integrity policy is incompatible with the TestSigning profile.'
        }
        else { Add-SBMSGateACheck $checks 'integrity.compatible' 'PASS' 'Integrity state is known and compatible.' }
    }
    if ($collectorMap.ContainsKey('pendingReboot') -and $collectorMap.pendingReboot.status -eq 'Captured' -and $schemaComplete['pendingReboot']) {
        if ([bool]$collectorMap.pendingReboot.data.any) { Add-SBMSGateACheck $checks 'pendingReboot.none' 'FAIL' 'A pending reboot signal is present.' }
        else { Add-SBMSGateACheck $checks 'pendingReboot.none' 'PASS' 'No pending reboot signal is present.' }
    }
    if ($collectorMap.ContainsKey('driverStore') -and $collectorMap.driverStore.status -eq 'Captured' -and $schemaComplete['driverStore']) {
        $bad = @($collectorMap.driverStore.data.packages | Where-Object { $_.classification -in @('blocking','unknown') })
        if (-not @($collectorMap.driverStore.data.packages).Count) { Add-SBMSGateACheck $checks 'drivers.classified' 'INCONCLUSIVE' 'No display driver packages were captured.' }
        elseif ($bad.Count) { Add-SBMSGateACheck $checks 'drivers.classified' 'FAIL' 'Blocking or unknown display driver packages exist.' }
        else { Add-SBMSGateACheck $checks 'drivers.classified' 'PASS' 'Display driver packages are explicitly allowed.' }
    }
    if ($collectorMap.ContainsKey('pnp') -and $collectorMap.pnp.status -eq 'Captured' -and $schemaComplete['pnp']) {
        $bad = @($collectorMap.pnp.data.devices | Where-Object { $_.classification -in @('blocking','unknown') -or ([int]$_.problem -ne 0 -and $_.class -in @('Display','Monitor')) })
        if (-not @($collectorMap.pnp.data.devices).Count) { Add-SBMSGateACheck $checks 'pnp.classified' 'INCONCLUSIVE' 'No present display or monitor PnP devices were captured.' }
        elseif ($bad.Count) { Add-SBMSGateACheck $checks 'pnp.classified' 'FAIL' 'Blocking, unknown, or unhealthy display PnP devices exist.' }
        else { Add-SBMSGateACheck $checks 'pnp.classified' 'PASS' 'Present display PnP devices are explicitly allowed and healthy.' }
    }
    if ($collectorMap.ContainsKey('displayConfig') -and $collectorMap.displayConfig.status -eq 'Captured' -and $schemaComplete['displayConfig']) {
        $physical = @($collectorMap.displayConfig.data.activePaths | Where-Object {
            $_.active -and $_.targetAvailable -and $_.classification -eq 'physical' -and
            [int]$_.width -gt 0 -and [int]$_.height -gt 0 -and [int]$_.refreshNumerator -gt 0 -and [int]$_.refreshDenominator -gt 0
        })
        $unknown = @($collectorMap.displayConfig.data.activePaths | Where-Object { $_.classification -eq 'unknown' })
        if ($unknown.Count -or -not $physical.Count) { Add-SBMSGateACheck $checks 'display.physicalRecovery' 'FAIL' 'No unambiguous active physical display recovery path exists.' }
        else { Add-SBMSGateACheck $checks 'display.physicalRecovery' 'PASS' 'An active physical recovery path exists.' }
    }
    if ($collectorMap.ContainsKey('startup') -and $collectorMap.startup.status -eq 'Captured' -and $schemaComplete['startup']) {
        if (@($collectorMap.startup.data.entries | Where-Object { $_.classification -in @('blocking','unknown') }).Count) { Add-SBMSGateACheck $checks 'startup.classified' 'FAIL' 'Blocking or unknown privileged startup entries exist.' }
        else { Add-SBMSGateACheck $checks 'startup.classified' 'PASS' 'Privileged startup entries are explicitly allowed.' }
    }
    if ($collectorMap.ContainsKey('runtime') -and $collectorMap.runtime.status -eq 'Captured' -and $schemaComplete['runtime']) {
        $bad = @(@($collectorMap.runtime.data.processes) + @($collectorMap.runtime.data.services) | Where-Object { $_.classification -in @('blocking','unknown') })
        if ($bad.Count) { Add-SBMSGateACheck $checks 'runtime.classified' 'FAIL' 'Blocking or unknown display-lab processes or services are active.' }
        else { Add-SBMSGateACheck $checks 'runtime.classified' 'PASS' 'No blocking display-lab process or service is active.' }
    }
    if ($collectorMap.ContainsKey('bitLocker') -and $collectorMap.bitLocker.status -eq 'Captured' -and $schemaComplete['bitLocker'] -and [bool]$collectorMap.bitLocker.data.protectionOn) {
        if ($null -eq $RemoteProof -or -not [bool]$RemoteProof.bitLockerRecoveryAccessVerified) { Add-SBMSGateACheck $checks 'bitlocker.remoteRecovery' 'INCONCLUSIVE' 'Remote BitLocker recovery access is not proven.' }
        else { Add-SBMSGateACheck $checks 'bitlocker.remoteRecovery' 'PASS' 'Remote recovery access was confirmed without storing a secret.' }
    }
    if ($null -eq $RemoteProof) { Add-SBMSGateACheck $checks 'remoteHealth.proof' 'INCONCLUSIVE' 'Run-bound SSH health proof is missing.' }
    else {
        $proofValid = ([string]$RemoteProof.runId -ceq $RunId -and [string]$RemoteProof.stableDigest -ceq $StableDigest -and
            [string]$RemoteProof.challengeSha256 -ceq $ChallengeSha256 -and [int]$RemoteProof.schemaVersion -eq 1 -and
            [bool]$RemoteProof.sshdAncestor -and [bool]$RemoteProof.nonLoopbackClient -and [bool]$RemoteProof.adminCapable -and
            [bool]$RemoteProof.evidenceReadable -and [bool]$RemoteProof.activePhysicalDisplay)
        if ($proofValid -and $collectorMap.ContainsKey('machine') -and $collectorMap.machine.status -eq 'Captured') {
            $proofValid = ([string]$RemoteProof.computerName -ceq [string]$collectorMap.machine.data.computerName -and
                [long]$RemoteProof.lastBootUnixSeconds -eq [long]$collectorMap.machine.data.lastBootUnixSeconds)
        }
        if ($proofValid) { Add-SBMSGateACheck $checks 'remoteHealth.proof' 'PASS' 'Run-bound SSH recovery proof is valid.' }
        else { Add-SBMSGateACheck $checks 'remoteHealth.proof' 'FAIL' 'SSH recovery proof is invalid or bound to different evidence.' }
    }
    $result = if (@($checks | Where-Object status -eq 'FAIL').Count) { 'FAIL' }
        elseif (@($checks | Where-Object status -eq 'INCONCLUSIVE').Count) { 'INCONCLUSIVE' } else { 'PASS' }
    [pscustomobject][ordered]@{ status = $result; evaluatedUtc = Get-SBMSGateAUtc; checks = $checks.ToArray() }
}

function Invoke-SBMSGateA {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][guid]$RunId,
        [Parameter(Mandatory)][string]$RunDirectory,
        [Parameter(Mandatory)][scriptblock]$CaptureEvidence
    )
    $root = [IO.Path]::GetFullPath($RunDirectory)
    $gateDirectory = Join-Path $root 'gate-a'
    if (-not (Test-Path -LiteralPath $gateDirectory)) { New-Item -ItemType Directory -Path $gateDirectory -Force | Out-Null }
    $evidence = & $CaptureEvidence
    $stablePath = Join-Path $gateDirectory 'stable-state.json'
    $baselineEvidencePath = Join-Path $gateDirectory 'baseline-evidence.json'
    $currentEvidencePath = Join-Path $gateDirectory 'current-evidence.json'
    $manifestPath = Join-Path $gateDirectory 'manifest.json'
    $stableJson = ConvertTo-SBMSGateAStableJson $evidence
    $currentDigest = Get-SBMSGateAObjectHash $evidence
    if (-not (Test-Path -LiteralPath $stablePath)) {
        Write-SBMSGateAAtomic $baselineEvidencePath ($evidence | ConvertTo-Json -Depth 30)
        Write-SBMSGateAAtomic $stablePath $stableJson
        $baselineDigest = $currentDigest
        $rollbackPlanPath = New-SBMSGateARollbackPlan -GateDirectory $gateDirectory -RunId $RunId.ToString() -StableDigest $baselineDigest -Evidence $evidence
        $challenge = New-SBMSGateAChallenge -GateDirectory $gateDirectory -RunId $RunId.ToString() -StableDigest $baselineDigest
    } else {
        $challenge = $null
        $rollbackPlanPath = Join-Path $gateDirectory 'rollback-plan.json'
        if (-not (Test-Path -LiteralPath $rollbackPlanPath -PathType Leaf)) { throw 'Gate A rollback plan is missing.' }
        Write-SBMSGateAAtomic $currentEvidencePath ($evidence | ConvertTo-Json -Depth 30)
        $baselineDigest = Get-SBMSGateAHash $stablePath
        $currentBytesHash = Get-SBMSGateAObjectHash $evidence
        if ($baselineDigest -cne $currentBytesHash) {
            $driftResult = [pscustomobject][ordered]@{ status='FAIL'; evaluatedUtc=Get-SBMSGateAUtc; checks=@([pscustomobject]@{id='baseline.drift';status='FAIL';reason='Stable Gate A evidence drifted.'}) }
            Write-SBMSGateAAtomic $manifestPath (($driftResult | Select-Object @{n='schemaVersion';e={3}},*,@{n='runId';e={$RunId.ToString()}},@{n='stableDigest';e={$baselineDigest}}) | ConvertTo-Json -Depth 30)
            return $driftResult
        }
    }
    $proof = $null
    $remoteProofPath = Join-Path $gateDirectory 'ssh-health-proof.json'
    if (Test-Path -LiteralPath $remoteProofPath -PathType Leaf) { $proof = Get-Content -LiteralPath $remoteProofPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    $challengeStatePath = Join-Path $gateDirectory 'remote-challenge.json'
    $challengeState = Get-Content -LiteralPath $challengeStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $proof) {
        if ($null -eq $challengeState.consumedUtc -or $null -eq $challengeState.PSObject.Properties['consumedProofSha256']) { throw 'SSH proof exists but its challenge consumption is incomplete.' }
        if ((Get-SBMSGateAHash $remoteProofPath) -cne [string]$challengeState.consumedProofSha256) { throw 'SSH proof hash does not match the consumed challenge.' }
        $verified = [DateTimeOffset]::Parse([string]$proof.verifiedUtc)
        $issued = [DateTimeOffset]::Parse([string]$challengeState.issuedUtc)
        $expires = [DateTimeOffset]::Parse([string]$challengeState.expiresUtc)
        $consumed = [DateTimeOffset]::Parse([string]$challengeState.consumedUtc)
        if ($verified -lt $issued -or $verified -gt $expires -or $consumed -lt $verified) { throw 'SSH proof time ordering is invalid.' }
    }
    $result = Test-SBMSGateAEvidence -Evidence $evidence -RunId $RunId.ToString() -StableDigest $baselineDigest -RemoteProof $proof -ChallengeSha256 ([string]$challengeState.challengeSha256)
    $evidenceIndexPath = Update-SBMSGateAEvidenceIndex -GateDirectory $gateDirectory
    $persistedManifest = [pscustomobject][ordered]@{
        schemaVersion = 3; contractVersion = 'gate-a/1'; runId = $RunId.ToString(); status = $result.status
        evaluatedUtc = $result.evaluatedUtc; stableStatePath = $stablePath; stableDigest = $baselineDigest
        baselineEvidencePath = $baselineEvidencePath; currentEvidencePath = $currentEvidencePath
        rollbackPlanPath = $rollbackPlanPath; rollbackPlanSha256 = Get-SBMSGateAHash $rollbackPlanPath
        evidenceIndexPath = $evidenceIndexPath; evidenceIndexSha256 = Get-SBMSGateAHash $evidenceIndexPath
        remoteProofPath = $remoteProofPath; remoteProofSha256 = Get-SBMSGateAHash $remoteProofPath
        challengeSha256 = [string]$challengeState.challengeSha256; checks = $result.checks
    }
    Write-SBMSGateAAtomic $manifestPath ($persistedManifest | ConvertTo-Json -Depth 30)
    if ($null -ne $challenge) { $persistedManifest | Add-Member -NotePropertyName challenge -NotePropertyValue $challenge }
    $persistedManifest
}

Export-ModuleMember -Function Invoke-SBMSGateA, Test-SBMSGateAEvidence, Confirm-SBMSGateARemoteHealth
