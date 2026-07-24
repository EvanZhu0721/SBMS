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
    $stream = New-Object IO.FileStream($LiteralPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    } finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
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

function Add-SBMSGateACheck {
    param([Collections.Generic.List[object]]$Checks, [string]$Id, [string]$Status, [string]$Reason)
    $Checks.Add([pscustomobject][ordered]@{ id = $Id; status = $Status; reason = $Reason })
}

function Test-SBMSGateAEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$StableDigest
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
        if ([bool]$collectorMap.pendingReboot.data.any) { Add-SBMSGateACheck $checks 'pendingReboot.none' 'FAIL' 'A servicing or SBMS/display-lab-owned pending reboot signal is present.' }
        else { Add-SBMSGateACheck $checks 'pendingReboot.none' 'PASS' 'No servicing or SBMS/display-lab-owned pending reboot signal is present; unrelated file maintenance is recorded but does not block read-only Gate A.' }
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
    if ($collectorMap.ContainsKey('bitLocker') -and $collectorMap.bitLocker.status -eq 'Captured' -and $schemaComplete['bitLocker']) {
        Add-SBMSGateACheck $checks 'bitlocker.state' 'PASS' 'BitLocker protection state is recorded; Gate A performs no boot-policy mutation.'
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
    } else {
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
    $result = Test-SBMSGateAEvidence -Evidence $evidence -RunId $RunId.ToString() -StableDigest $baselineDigest
    $evidenceIndexPath = Update-SBMSGateAEvidenceIndex -GateDirectory $gateDirectory
    $persistedManifest = [pscustomobject][ordered]@{
        schemaVersion = 4; contractVersion = 'gate-a/2'; runId = $RunId.ToString(); status = $result.status
        evaluatedUtc = $result.evaluatedUtc; stableStatePath = $stablePath; stableDigest = $baselineDigest
        baselineEvidencePath = $baselineEvidencePath; currentEvidencePath = $currentEvidencePath
        rollbackPlanPath = $rollbackPlanPath; rollbackPlanSha256 = Get-SBMSGateAHash $rollbackPlanPath
        evidenceIndexPath = $evidenceIndexPath; evidenceIndexSha256 = Get-SBMSGateAHash $evidenceIndexPath
        checks = $result.checks
    }
    Write-SBMSGateAAtomic $manifestPath ($persistedManifest | ConvertTo-Json -Depth 30)
    $persistedManifest
}

Export-ModuleMember -Function Invoke-SBMSGateA, Test-SBMSGateAEvidence
