Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:RequiredCollectors = @(
    'repository', 'auditOnly', 'bcd', 'systemIntegrity', 'bitLocker',
    'pendingReboot', 'pnp', 'driverStore', 'displayConfig', 'runtime', 'startup'
)

function Get-SBMSGateAUtc { [DateTime]::UtcNow.ToString('o') }

function Write-SBMSGateAAtomic {
    param([Parameter(Mandatory)][string]$LiteralPath, [Parameter(Mandatory)][string]$Text)
    $directory = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, $Text, $script:Utf8NoBom)
    if (Test-Path -LiteralPath $LiteralPath) {
        Move-Item -LiteralPath $temporary -Destination $LiteralPath -Force
    } else { [IO.File]::Move($temporary, $LiteralPath) }
}

function Get-SBMSGateAHash {
    param([Parameter(Mandatory)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { return $null }
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function ConvertTo-SBMSGateAStableJson {
    param([Parameter(Mandatory)]$InputObject)
    $InputObject | ConvertTo-Json -Depth 30 -Compress
}

function Get-SBMSGateAObjectHash {
    param([Parameter(Mandatory)]$InputObject)
    $bytes = $script:Utf8NoBom.GetBytes((ConvertTo-SBMSGateAStableJson $InputObject))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') } finally { $sha.Dispose() }
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
        $RemoteProof
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

    if ($collectorMap.ContainsKey('repository') -and $collectorMap.repository.status -eq 'Captured') {
        if (-not [bool]$collectorMap.repository.data.worktreeClean) { Add-SBMSGateACheck $checks 'repository.clean' 'FAIL' 'Repository worktree is dirty.' }
        elseif (@($collectorMap.repository.data.payloads | Where-Object { -not $_.exists -or [string]::IsNullOrWhiteSpace([string]$_.sha256) }).Count) {
            Add-SBMSGateACheck $checks 'repository.payloads' 'FAIL' 'A required payload is missing or unhashed.'
        } else { Add-SBMSGateACheck $checks 'repository.clean' 'PASS' 'Repository and payload hashes are complete.' }
    }
    if ($collectorMap.ContainsKey('auditOnly') -and $collectorMap.auditOnly.status -eq 'Captured') {
        $criticalSkip = @($collectorMap.auditOnly.data.checks | Where-Object { $_.status -eq 'SKIP' -and $_.name -in @('PnpAudit','DriverAudit','NativeListAudit') })
        if ([string]$collectorMap.auditOnly.data.result -cne 'PASS' -or $criticalSkip.Count) { Add-SBMSGateACheck $checks 'auditOnly.result' 'FAIL' 'AuditOnly did not produce complete PASS evidence.' }
        else { Add-SBMSGateACheck $checks 'auditOnly.result' 'PASS' 'AuditOnly evidence is complete.' }
    }
    if ($collectorMap.ContainsKey('bcd') -and $collectorMap.bcd.status -eq 'Captured') {
        if ([bool]$collectorMap.bcd.data.testSigning -or @($collectorMap.bcd.data.bootSequence).Count) { Add-SBMSGateACheck $checks 'bcd.safeBaseline' 'FAIL' 'BCD already has testsigning or a one-time boot sequence.' }
        else { Add-SBMSGateACheck $checks 'bcd.safeBaseline' 'PASS' 'BCD is at a safe baseline.' }
    }
    if ($collectorMap.ContainsKey('systemIntegrity') -and $collectorMap.systemIntegrity.status -eq 'Captured') {
        if ($null -eq $collectorMap.systemIntegrity.data.secureBootEnabled -or $null -eq $collectorMap.systemIntegrity.data.codeIntegrityKnown) { Add-SBMSGateACheck $checks 'integrity.known' 'INCONCLUSIVE' 'Secure Boot or Code Integrity state is unknown.' }
        elseif ([bool]$collectorMap.systemIntegrity.data.secureBootEnabled -or -not [bool]$collectorMap.systemIntegrity.data.codeIntegrityKnown) { Add-SBMSGateACheck $checks 'integrity.compatible' 'FAIL' 'System integrity policy is incompatible with the TestSigning profile.' }
        else { Add-SBMSGateACheck $checks 'integrity.compatible' 'PASS' 'Integrity state is known and compatible.' }
    }
    if ($collectorMap.ContainsKey('pendingReboot') -and $collectorMap.pendingReboot.status -eq 'Captured') {
        if ([bool]$collectorMap.pendingReboot.data.any) { Add-SBMSGateACheck $checks 'pendingReboot.none' 'FAIL' 'A pending reboot signal is present.' }
        else { Add-SBMSGateACheck $checks 'pendingReboot.none' 'PASS' 'No pending reboot signal is present.' }
    }
    if ($collectorMap.ContainsKey('driverStore') -and $collectorMap.driverStore.status -eq 'Captured') {
        $bad = @($collectorMap.driverStore.data.packages | Where-Object { $_.classification -in @('blocking','unknown') })
        if ($bad.Count) { Add-SBMSGateACheck $checks 'drivers.classified' 'FAIL' 'Blocking or unknown display driver packages exist.' }
        else { Add-SBMSGateACheck $checks 'drivers.classified' 'PASS' 'Display driver packages are explicitly allowed.' }
    }
    if ($collectorMap.ContainsKey('displayConfig') -and $collectorMap.displayConfig.status -eq 'Captured') {
        $physical = @($collectorMap.displayConfig.data.activePaths | Where-Object { $_.active -and $_.classification -eq 'physical' })
        $unknown = @($collectorMap.displayConfig.data.activePaths | Where-Object { $_.classification -eq 'unknown' })
        if ($unknown.Count -or -not $physical.Count) { Add-SBMSGateACheck $checks 'display.physicalRecovery' 'FAIL' 'No unambiguous active physical display recovery path exists.' }
        else { Add-SBMSGateACheck $checks 'display.physicalRecovery' 'PASS' 'An active physical recovery path exists.' }
    }
    if ($collectorMap.ContainsKey('startup') -and $collectorMap.startup.status -eq 'Captured') {
        if (@($collectorMap.startup.data.entries | Where-Object { $_.classification -in @('blocking','unknown') }).Count) { Add-SBMSGateACheck $checks 'startup.classified' 'FAIL' 'Blocking or unknown privileged startup entries exist.' }
        else { Add-SBMSGateACheck $checks 'startup.classified' 'PASS' 'Privileged startup entries are explicitly allowed.' }
    }
    if ($collectorMap.ContainsKey('bitLocker') -and $collectorMap.bitLocker.status -eq 'Captured' -and [bool]$collectorMap.bitLocker.data.protectionOn) {
        if ($null -eq $RemoteProof -or -not [bool]$RemoteProof.bitLockerRecoveryAccessVerified) { Add-SBMSGateACheck $checks 'bitlocker.remoteRecovery' 'INCONCLUSIVE' 'Remote BitLocker recovery access is not proven.' }
        else { Add-SBMSGateACheck $checks 'bitlocker.remoteRecovery' 'PASS' 'Remote recovery access was confirmed without storing a secret.' }
    }
    if ($null -eq $RemoteProof) { Add-SBMSGateACheck $checks 'remoteHealth.proof' 'INCONCLUSIVE' 'Run-bound SSH health proof is missing.' }
    else {
        $proofValid = ([string]$RemoteProof.runId -ceq $RunId -and [string]$RemoteProof.stableDigest -ceq $StableDigest -and
            [bool]$RemoteProof.sshdAncestor -and [bool]$RemoteProof.nonLoopbackClient -and [bool]$RemoteProof.adminCapable -and
            [bool]$RemoteProof.evidenceReadable -and [bool]$RemoteProof.activePhysicalDisplay)
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
        [Parameter(Mandatory)][scriptblock]$CaptureEvidence,
        [string]$RemoteProofPath
    )
    $root = [IO.Path]::GetFullPath($RunDirectory)
    $gateDirectory = Join-Path $root 'gate-a'
    if (-not (Test-Path -LiteralPath $gateDirectory)) { New-Item -ItemType Directory -Path $gateDirectory -Force | Out-Null }
    $evidence = & $CaptureEvidence
    $stablePath = Join-Path $gateDirectory 'stable-state.json'
    $manifestPath = Join-Path $gateDirectory 'manifest.json'
    $stableJson = ConvertTo-SBMSGateAStableJson $evidence
    $currentDigest = Get-SBMSGateAObjectHash $evidence
    if (-not (Test-Path -LiteralPath $stablePath)) {
        Write-SBMSGateAAtomic $stablePath $stableJson
        $baselineDigest = $currentDigest
    } else {
        $baselineDigest = Get-SBMSGateAHash $stablePath
        $currentBytesHash = Get-SBMSGateAObjectHash $evidence
        if ($baselineDigest -cne $currentBytesHash) {
            $driftResult = [pscustomobject][ordered]@{ status='FAIL'; evaluatedUtc=Get-SBMSGateAUtc; checks=@([pscustomobject]@{id='baseline.drift';status='FAIL';reason='Stable Gate A evidence drifted.'}) }
            Write-SBMSGateAAtomic $manifestPath (($driftResult | Select-Object @{n='schemaVersion';e={3}},*,@{n='runId';e={$RunId.ToString()}},@{n='stableDigest';e={$baselineDigest}}) | ConvertTo-Json -Depth 30)
            return $driftResult
        }
    }
    $proof = $null
    if ($RemoteProofPath -and (Test-Path -LiteralPath $RemoteProofPath -PathType Leaf)) { $proof = Get-Content -LiteralPath $RemoteProofPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    $result = Test-SBMSGateAEvidence -Evidence $evidence -RunId $RunId.ToString() -StableDigest $baselineDigest -RemoteProof $proof
    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 3; contractVersion = 'gate-a/1'; runId = $RunId.ToString(); status = $result.status
        evaluatedUtc = $result.evaluatedUtc; stableStatePath = $stablePath; stableDigest = $baselineDigest
        remoteProofPath = $RemoteProofPath; checks = $result.checks
    }
    Write-SBMSGateAAtomic $manifestPath ($manifest | ConvertTo-Json -Depth 30)
    $manifest
}

Export-ModuleMember -Function Invoke-SBMSGateA, Test-SBMSGateAEvidence
