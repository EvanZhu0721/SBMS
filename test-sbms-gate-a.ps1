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
function Confirm-FixtureRemoteHealth([guid]$RunId, [string]$RunDirectory, [string]$Challenge, [scriptblock]$CaptureSession) {
    & $script:GateModule {
        param($InnerRunId,$InnerRunDirectory,$InnerChallenge,$InnerCaptureSession)
        Confirm-SBMSGateARemoteHealthCore -RunId $InnerRunId -RunDirectory $InnerRunDirectory -Challenge $InnerChallenge -CaptureSession $InnerCaptureSession
    } $RunId $RunDirectory $Challenge $CaptureSession
}
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

$productionRemoteHealth = Get-Command Confirm-SBMSGateARemoteHealth -CommandType Function -ErrorAction Stop
Assert-True ($productionRemoteHealth.Definition -match '\$sessionEvidence\s*=\s*Get-SBMSGateARemoteSessionEvidence') 'Production SSH proof must capture private session evidence before creating its closure.'

$runId = [guid]::NewGuid()
$root = Join-Path ([IO.Path]::GetTempPath()) ('sbms-gate-a-test-' + [guid]::NewGuid().ToString('N'))
try {
    $evidence = New-PassEvidence
    $capture = { $evidence }.GetNewClosure()
    $first = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $capture
    Assert-Equal 'INCONCLUSIVE' $first.status 'Missing SSH proof must not pass.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\stable-state.json')) 'Stable evidence was not written.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\rollback-plan.json')) 'Rollback plan was not written before mutation.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\evidence-index.json')) 'Evidence index was not written.'
    Assert-Equal 3 ((Get-Content -LiteralPath (Join-Path $root 'gate-a\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).schemaVersion) 'Manifest schema mismatch.'
    Assert-True ($null -eq (Get-Content -LiteralPath (Join-Path $root 'gate-a\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties['challenge']) 'Plaintext challenge must not be persisted in the manifest.'

    $session = [pscustomobject][ordered]@{
        sshdAncestor=$true; nonLoopbackClient=$true; adminCapable=$true; evidenceReadable=$true; activePhysicalDisplay=$true
        computerName='TESTHOST'; lastBootUtc='2026-07-14T00:00:00Z'; lastBootUnixSeconds=1783987200; clientAddress='192.0.2.20'
    }
    $sessionCapture = { $session }.GetNewClosure()
    $proof = Confirm-FixtureRemoteHealth -RunId $runId -RunDirectory $root -Challenge $first.challenge -CaptureSession $sessionCapture
    Assert-Throws { Confirm-FixtureRemoteHealth -RunId $runId -RunDirectory $root -Challenge $first.challenge -CaptureSession $sessionCapture } 'already consumed' 'SSH proof replay must be rejected.'
    $second = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $capture
    Assert-Equal 'PASS' $second.status 'Complete evidence and bound SSH proof should pass.'

    $badProof = $proof.PSObject.Copy(); $badProof.runId = [guid]::NewGuid().ToString()
    $bad = Test-SBMSGateAEvidence -Evidence $evidence -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $badProof -ChallengeSha256 $first.challengeSha256
    Assert-Equal 'FAIL' $bad.status 'Cross-run SSH proof must fail.'
    $challengeState = Get-Content -LiteralPath (Join-Path $root 'gate-a\remote-challenge.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$challengeState.consumedProofSha256)) 'Consumed challenge must bind the proof file hash.'

    $missingBitLockerState = New-PassEvidence
    $missingBitLockerState.bitLocker.data.PSObject.Properties.Remove('protectionOn')
    $bad = Test-SBMSGateAEvidence -Evidence $missingBitLockerState -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof -ChallengeSha256 $first.challengeSha256
    Assert-Equal 'INCONCLUSIVE' $bad.status 'Missing BitLocker protection state must be inconclusive.'

    $unknownDriver = New-PassEvidence
    $unknownDriver.driverStore.data.packages[0].classification = 'unknown'
    $bad = Test-SBMSGateAEvidence -Evidence $unknownDriver -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof -ChallengeSha256 $first.challengeSha256
    Assert-Equal 'FAIL' $bad.status 'Unknown display package must fail closed.'

    $skip = New-PassEvidence
    $skip.auditOnly.data.checks += [pscustomobject]@{name='NativeListAudit';status='SKIP'}
    $bad = Test-SBMSGateAEvidence -Evidence $skip -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof -ChallengeSha256 $first.challengeSha256
    Assert-Equal 'FAIL' $bad.status 'Critical AuditOnly SKIP must fail closed.'

    $missing = New-PassEvidence
    $missing.PSObject.Properties.Remove('pnp')
    $bad = Test-SBMSGateAEvidence -Evidence $missing -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof -ChallengeSha256 $first.challengeSha256
    Assert-Equal 'INCONCLUSIVE' $bad.status 'Missing required collector must be inconclusive.'

    $blockedPnp = New-PassEvidence
    $blockedPnp.pnp.data.devices[0].classification = 'blocking'
    $bad = Test-SBMSGateAEvidence -Evidence $blockedPnp -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof -ChallengeSha256 $first.challengeSha256
    Assert-Equal 'FAIL' $bad.status 'Present blocking virtual-display PnP device must fail.'

    $drift = New-PassEvidence
    $drift.runtime.data.processes = @('changed')
    $driftCapture = { $drift }.GetNewClosure()
    $bad = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $driftCapture
    Assert-Equal 'FAIL' $bad.status 'Stable evidence drift must fail.'

    Add-Content -LiteralPath (Join-Path $root 'gate-a\ssh-health-proof.json') -Value ' ' -Encoding UTF8
    Assert-Throws { Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $capture } 'proof hash' 'Tampered SSH proof must be rejected.'

    $allText = (Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    Assert-True ($allText -notmatch '(?i)recoverypassword') 'Evidence must not persist a BitLocker recovery secret field.'
    "PASS: $script:Passed Gate A assertions"
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
