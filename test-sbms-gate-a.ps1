Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lab\SBMS.GateA.psm1') -Force

$script:Passed = 0
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
    $script:Passed++
}
function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
    $script:Passed++
}
function Envelope($Data) { [pscustomobject][ordered]@{ status = 'Captured'; capturedUtc = '2026-07-14T00:00:00Z'; data = $Data } }
function New-PassEvidence {
    [pscustomobject][ordered]@{
        repository = Envelope ([pscustomobject]@{ worktreeClean=$true; payloads=@([pscustomobject]@{exists=$true;sha256='AA'}) })
        auditOnly = Envelope ([pscustomobject]@{ result='PASS'; checks=@([pscustomobject]@{name='PnpAudit';status='PASS'}) })
        bcd = Envelope ([pscustomobject]@{ testSigning=$false; bootSequence=@() })
        systemIntegrity = Envelope ([pscustomobject]@{ secureBootEnabled=$false; codeIntegrityKnown=$true })
        bitLocker = Envelope ([pscustomobject]@{ protectionOn=$false })
        pendingReboot = Envelope ([pscustomobject]@{ any=$false })
        pnp = Envelope ([pscustomobject]@{ devices=@() })
        driverStore = Envelope ([pscustomobject]@{ packages=@([pscustomobject]@{classification='allowed'}) })
        displayConfig = Envelope ([pscustomobject]@{ activePaths=@([pscustomobject]@{active=$true;classification='physical'}) })
        runtime = Envelope ([pscustomobject]@{ processes=@(); services=@() })
        startup = Envelope ([pscustomobject]@{ entries=@([pscustomobject]@{classification='allowed'}) })
    }
}

$runId = [guid]::NewGuid()
$root = Join-Path ([IO.Path]::GetTempPath()) ('sbms-gate-a-test-' + [guid]::NewGuid().ToString('N'))
try {
    $evidence = New-PassEvidence
    $capture = { $evidence }.GetNewClosure()
    $first = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $capture
    Assert-Equal 'INCONCLUSIVE' $first.status 'Missing SSH proof must not pass.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'gate-a\stable-state.json')) 'Stable evidence was not written.'
    Assert-Equal 3 ((Get-Content -LiteralPath (Join-Path $root 'gate-a\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json).schemaVersion) 'Manifest schema mismatch.'

    $proofPath = Join-Path $root 'proof.json'
    $proof = [pscustomobject][ordered]@{
        runId=$runId.ToString(); stableDigest=$first.stableDigest; sshdAncestor=$true; nonLoopbackClient=$true
        adminCapable=$true; evidenceReadable=$true; activePhysicalDisplay=$true; bitLockerRecoveryAccessVerified=$false
    }
    [IO.File]::WriteAllText($proofPath, ($proof | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    $second = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $capture -RemoteProofPath $proofPath
    Assert-Equal 'PASS' $second.status 'Complete evidence and bound SSH proof should pass.'

    $badProof = $proof.PSObject.Copy(); $badProof.runId = [guid]::NewGuid().ToString()
    $bad = Test-SBMSGateAEvidence -Evidence $evidence -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $badProof
    Assert-Equal 'FAIL' $bad.status 'Cross-run SSH proof must fail.'

    $unknownDriver = New-PassEvidence
    $unknownDriver.driverStore.data.packages[0].classification = 'unknown'
    $bad = Test-SBMSGateAEvidence -Evidence $unknownDriver -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof
    Assert-Equal 'FAIL' $bad.status 'Unknown display package must fail closed.'

    $skip = New-PassEvidence
    $skip.auditOnly.data.checks += [pscustomobject]@{name='NativeListAudit';status='SKIP'}
    $bad = Test-SBMSGateAEvidence -Evidence $skip -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof
    Assert-Equal 'FAIL' $bad.status 'Critical AuditOnly SKIP must fail closed.'

    $missing = New-PassEvidence
    $missing.PSObject.Properties.Remove('pnp')
    $bad = Test-SBMSGateAEvidence -Evidence $missing -RunId $runId.ToString() -StableDigest $first.stableDigest -RemoteProof $proof
    Assert-Equal 'INCONCLUSIVE' $bad.status 'Missing required collector must be inconclusive.'

    $drift = New-PassEvidence
    $drift.runtime.data.processes = @('changed')
    $driftCapture = { $drift }.GetNewClosure()
    $bad = Invoke-SBMSGateA -RunId $runId -RunDirectory $root -CaptureEvidence $driftCapture -RemoteProofPath $proofPath
    Assert-Equal 'FAIL' $bad.status 'Stable evidence drift must fail.'

    $allText = (Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    Assert-True ($allText -notmatch '(?i)recoverypassword') 'Evidence must not persist a BitLocker recovery secret field.'
    "PASS: $script:Passed Gate A assertions"
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
