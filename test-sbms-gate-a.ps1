Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lab\SBMS.GateA.psm1') -Force
$script:GateModule = Get-Module SBMS.GateA

$script:Passed = 0
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
    $script:Passed++
}
function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:Passed++
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action; throw "$Message No exception was thrown." }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "$Message Wrong exception: $($_.Exception.Message)" }
        $script:Passed++
    }
}
function Envelope($Data) { [pscustomobject][ordered]@{ status = 'Captured'; capturedUtc = '2026-07-14T00:00:00Z'; data = $Data } }
function New-PassEvidence {
    [pscustomobject][ordered]@{
        machine = Envelope ([pscustomobject]@{ computerName='TESTHOST'; lastBootUtc='2026-07-14T00:00:00Z'; lastBootUnixSeconds=1783987200 })
        evidenceSecurity = Envelope ([pscustomobject]@{ protected=$true; structuredReadbackRequired=$true })
        repository = Envelope ([pscustomobject]@{ worktreeClean=$true; payloads=@([pscustomobject]@{role='gate-module';exists=$true;sha256='AA';signature=$null}) })
        auditOnly = Envelope ([pscustomobject]@{ result='PASS'; observationOnly=$true; driverInstallOrRemovalAttempted=$false; checks=@(
            [pscustomobject]@{name='PnpAudit';status='PASS'}, [pscustomobject]@{name='DriverAudit';status='PASS'}, [pscustomobject]@{name='NativeListAudit';status='PASS'}
        ) })
        bcd = Envelope ([pscustomobject]@{ testSigning=$false; bootSequence=@() })
        systemIntegrity = Envelope ([pscustomobject]@{ secureBootEnabled=$false; codeIntegrityKnown=$true; testSigningCompatible=$true })
        bitLocker = Envelope ([pscustomobject]@{ protectionOn=$false })
        pendingReboot = Envelope ([pscustomobject]@{ any=$false })
        pnp = Envelope ([pscustomobject]@{ devices=@([pscustomobject]@{class='Display';problem=0;classification='allowed'}) })
        driverStore = Envelope ([pscustomobject]@{ packages=@([pscustomobject]@{classification='allowed'}) })
        displayConfig = Envelope ([pscustomobject]@{ activePaths=@([pscustomobject]@{active=$true;targetAvailable=$true;classification='physical';width=1920;height=1080;refreshNumerator=60;refreshDenominator=1}) })
        runtime = Envelope ([pscustomobject]@{ processes=@(); services=@() })
        startup = Envelope ([pscustomobject]@{ entries=@([pscustomobject]@{classification='allowed'}) })
    }
}

$gateHashDefinition = & $script:GateModule { (Get-Command Get-SBMSGateAHash -CommandType Function).Definition }
Assert-True ($gateHashDefinition -notmatch 'Get-FileHash') 'Gate A hashing must not depend on PowerShell module auto-loading.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'lab\Confirm-SBMSLabRemoteHealth.ps1'))) 'Deprecated SSH proof entry must not ship.'
$gateModuleSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'lab\SBMS.GateA.psm1') -Raw -Encoding UTF8
Assert-True ($gateModuleSource -notmatch 'Confirm-SBMSGateARemoteHealth|remote-challenge\.json|ssh-health-proof\.json|remoteHealth\.proof') 'Deprecated SSH proof implementation must not remain in Gate A.'

$runId = [guid]::NewGuid()
$root = Join-Path ([IO.Path]::GetTempPath()) ('sbms-gate-a-test-' + [guid]::NewGuid().ToString('N'))
try {
    $evidence = New-PassEvidence
    $capture = { $evidence }.GetNewClosure()
    $first = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $capture
    Assert-Equal 'PASS' $first.status 'Complete local Gate A evidence should pass without an external SSH proof.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\stable-state.json')) 'Stable evidence was not written.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\rollback-plan.json')) 'Rollback plan was not written before mutation.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\evidence-index.json')) 'Evidence index was not written.'
    $manifest = Get-Content -LiteralPath (Join-Path $root 'gate-a\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal 4 $manifest.schemaVersion 'Manifest schema mismatch.'
    Assert-Equal 'gate-a/2' $manifest.contractVersion 'Gate A contract version mismatch.'
    Assert-True ($null -eq $manifest.PSObject.Properties['remoteProofPath']) 'Gate A manifest must not expose an SSH proof contract.'

    $externalPending = New-PassEvidence
    $externalPending.pendingReboot.data | Add-Member -NotePropertyName pendingFileRenameCount -NotePropertyValue 1
    $externalPending.pendingReboot.data | Add-Member -NotePropertyName externalPendingFileRenameCount -NotePropertyValue 1
    $externalPending.pendingReboot.data | Add-Member -NotePropertyName blockingPendingFileRenameCount -NotePropertyValue 0
    $externalPending.pendingReboot.data | Add-Member -NotePropertyName pendingFileRenames -NotePropertyValue @([pscustomobject]@{source='gamingservicesproxy_11.dll.0';destination='';classification='external'})
    $externalResult = Test-SBMSGateAEvidence -Evidence $externalPending -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'PASS' $externalResult.status 'Unrelated package-maintenance rename must not block read-only Gate A.'

    $ownedPending = New-PassEvidence
    $ownedPending.pendingReboot.data.any = $true
    $ownedResult = Test-SBMSGateAEvidence -Evidence $ownedPending -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'FAIL' $ownedResult.status 'SBMS/display-lab-owned pending reboot must fail Gate A.'

    $missingBitLockerState = New-PassEvidence
    $missingBitLockerState.bitLocker.data.PSObject.Properties.Remove('protectionOn')
    $bad = Test-SBMSGateAEvidence -Evidence $missingBitLockerState -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'INCONCLUSIVE' $bad.status 'Missing BitLocker protection state must be inconclusive.'

    $unknownDriver = New-PassEvidence
    $unknownDriver.driverStore.data.packages[0].classification = 'unknown'
    $bad = Test-SBMSGateAEvidence -Evidence $unknownDriver -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'FAIL' $bad.status 'Unknown display package must fail closed.'

    $skip = New-PassEvidence
    $skip.auditOnly.data.checks += [pscustomobject]@{name='NativeListAudit';status='SKIP'}
    $bad = Test-SBMSGateAEvidence -Evidence $skip -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'FAIL' $bad.status 'Critical AuditOnly SKIP must fail closed.'

    $missing = New-PassEvidence
    $missing.PSObject.Properties.Remove('pnp')
    $bad = Test-SBMSGateAEvidence -Evidence $missing -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'INCONCLUSIVE' $bad.status 'Missing required collector must be inconclusive.'

    $blockedPnp = New-PassEvidence
    $blockedPnp.pnp.data.devices[0].classification = 'blocking'
    $bad = Test-SBMSGateAEvidence -Evidence $blockedPnp -RunId $runId.ToString() -StableDigest $first.stableDigest
    Assert-Equal 'FAIL' $bad.status 'Present blocking virtual-display PnP device must fail.'

    $drift = New-PassEvidence
    $drift.runtime.data.processes = @('changed')
    $driftCapture = { $drift }.GetNewClosure()
    $bad = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $driftCapture
    Assert-Equal 'FAIL' $bad.status 'Stable evidence drift must fail.'

    $allText = (Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    Assert-True ($allText -notmatch '(?i)recoverypassword') 'Evidence must not persist a BitLocker recovery secret field.'
    "PASS: $script:Passed Gate A assertions"
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
