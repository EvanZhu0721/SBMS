Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Verifier = Join-Path $Root 'Confirm-SBMSReleaseCandidateEvidence.ps1'
$Passed = 0
$Failed = 0

function Write-Json {
    param([string] $Path, $Value)
    $directory = [IO.Path]::GetDirectoryName($Path)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 10) + "`n"),
        (New-Object Text.UTF8Encoding($false)))
}

function Invoke-Test {
    param([string] $Name, [scriptblock] $Body)
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

function Assert-Throws {
    param([scriptblock] $Body, [string] $Pattern)
    try {
        & $Body
    } catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected '$Pattern'; actual '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected an exception matching '$Pattern'."
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('SBMS-release-evidence-' + [guid]::NewGuid().ToString('N'))
$commit = '0123456789abcdef0123456789abcdef01234567'
$contracts = Join-Path $fixture 'contracts'
$clean = Join-Path $fixture 'clean'
$hardware = Join-Path $fixture 'hardware'
$output = Join-Path $fixture 'bundle\evidence-index.json'
$trustedRuns = Join-Path $fixture 'trusted-runs.json'

try {
    foreach ($shell in @(
        @{ Name = 'pwsh'; Version = '7.6.4' },
        @{ Name = 'winps'; Version = '5.1.26100.1' }
    )) {
        Write-Json (Join-Path $contracts "$($shell.Name)\summary.json") ([ordered]@{
            schemaVersion = 1
            suite = 'contracts'
            status = 'PASS'
            repositoryCommit = $commit
            sourceDirty = $false
            runner = [ordered]@{ childPowerShellVersion = $shell.Version }
        })
    }
    foreach ($suite in @('package', 'integration')) {
        Write-Json (Join-Path $clean "$suite\summary.json") ([ordered]@{
            schemaVersion = 1
            suite = $suite
            status = 'PASS'
            repositoryCommit = $commit
            sourceDirty = $false
        })
    }

    $candidateRoot = Join-Path $clean 'candidate\SBMS-0.1.0-dev.0-win-x64'
    New-Item -ItemType Directory -Path $candidateRoot -Force | Out-Null
    $binary = Join-Path $candidateRoot 'SBMS.exe'
    [IO.File]::WriteAllBytes($binary, [byte[]](1, 2, 3, 4))
    $binaryHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestPath = Join-Path $candidateRoot 'SBMS.release.json'
    Write-Json $manifestPath ([ordered]@{
        source = [ordered]@{ commit = $commit; dirty = $false }
        artifacts = @([ordered]@{ path = 'SBMS.exe'; bytes = 4; sha256 = $binaryHash })
    })
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $hardwareSummaryPath = Join-Path $hardware 'artifact\summary.json'
    $hardwareSummary = [ordered]@{
        schemaVersion = 1
        scenario = 'SingleOutput'
        result = 'PASS'
        repositoryCommit = $commit
        observationOnly = $true
        driverInstallOrRemovalAttempted = $false
        testedPayload = [ordered]@{
            sourceCommit = $commit
            manifestSha256 = $manifestHash
            artifactsVerified = $true
        }
    }
    Write-Json $hardwareSummaryPath $hardwareSummary
    Write-Json (Join-Path $hardware 'artifact\workflow-run.json') ([ordered]@{
        schemaVersion = 1
        workflowRunId = '456'
        repositoryCommit = $commit
        scenario = 'SingleOutput'
    })
    $trustedRunsModel = [ordered]@{
        schemaVersion = 1
        candidateCommit = $commit
        windowsCi = [ordered]@{
            runId = '123'
            workflowPath = '.github/workflows/windows-ci.yml'
            headSha = $commit
            conclusion = 'success'
        }
        hardware = [ordered]@{
            runId = '456'
            workflowPath = '.github/workflows/hardware-evidence.yml'
            headSha = $commit
            conclusion = 'success'
        }
    }
    Write-Json $trustedRuns $trustedRunsModel

    Invoke-Test 'matching unit integration hardware and payload evidence qualifies' {
        & $Verifier `
            -CandidateCommit $commit `
            -ContractsRoot $contracts `
            -CleanBuildRoot $clean `
            -HardwareRoot $hardware `
            -WindowsCiRunId '123' `
            -HardwareRunId '456' `
            -TrustedRunsPath $trustedRuns `
            -OutputPath $output
        $index = [IO.File]::ReadAllText($output, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([string]$index.status -cne 'QUALIFIED' -or [string]$index.candidateCommit -cne $commit) {
            throw 'Qualified evidence index is invalid.'
        }
    }

    Invoke-Test 'hardware commit drift is rejected' {
        $hardwareSummary.repositoryCommit = '1123456789abcdef0123456789abcdef01234567'
        Write-Json $hardwareSummaryPath $hardwareSummary
        Assert-Throws {
            & $Verifier -CandidateCommit $commit -ContractsRoot $contracts -CleanBuildRoot $clean `
                -HardwareRoot $hardware -WindowsCiRunId '123' -HardwareRunId '456' `
                -TrustedRunsPath $trustedRuns -OutputPath $output
        } 'Hardware summary commit'
        $hardwareSummary.repositoryCommit = $commit
        Write-Json $hardwareSummaryPath $hardwareSummary
    }

    Invoke-Test 'hardware payload manifest drift is rejected' {
        $hardwareSummary.testedPayload.manifestSha256 = ('0' * 64)
        Write-Json $hardwareSummaryPath $hardwareSummary
        Assert-Throws {
            & $Verifier -CandidateCommit $commit -ContractsRoot $contracts -CleanBuildRoot $clean `
                -HardwareRoot $hardware -WindowsCiRunId '123' -HardwareRunId '456' `
                -TrustedRunsPath $trustedRuns -OutputPath $output
        } 'does not match the retained CI candidate'
        $hardwareSummary.testedPayload.manifestSha256 = $manifestHash
        Write-Json $hardwareSummaryPath $hardwareSummary
    }

    Invoke-Test 'AuditOnly evidence cannot qualify a release candidate' {
        $hardwareSummary.scenario = 'AuditOnly'
        Write-Json $hardwareSummaryPath $hardwareSummary
        Assert-Throws {
            & $Verifier -CandidateCommit $commit -ContractsRoot $contracts -CleanBuildRoot $clean `
                -HardwareRoot $hardware -WindowsCiRunId '123' -HardwareRunId '456' `
                -TrustedRunsPath $trustedRuns -OutputPath $output
        } 'non-AuditOnly'
    }

    Invoke-Test 'trusted workflow provenance drift is rejected' {
        $trustedRunsModel.hardware.workflowPath = '.github/workflows/windows-ci.yml'
        Write-Json $trustedRuns $trustedRunsModel
        Assert-Throws {
            & $Verifier -CandidateCommit $commit -ContractsRoot $contracts -CleanBuildRoot $clean `
                -HardwareRoot $hardware -WindowsCiRunId '123' -HardwareRunId '456' `
                -TrustedRunsPath $trustedRuns -OutputPath $output
        } 'workflow path'
        $trustedRunsModel.hardware.workflowPath = '.github/workflows/hardware-evidence.yml'
        Write-Json $trustedRuns $trustedRunsModel
    }
}
finally {
    if (Test-Path -LiteralPath $fixture) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}

Write-Host "Release evidence contract: $Passed passed, $Failed failed"
if ($Failed -ne 0) {
    exit 1
}
