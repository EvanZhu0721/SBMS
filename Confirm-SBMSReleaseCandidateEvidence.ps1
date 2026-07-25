[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CandidateCommit,

    [Parameter(Mandatory = $true)]
    [string] $ContractsRoot,

    [Parameter(Mandatory = $true)]
    [string] $CleanBuildRoot,

    [Parameter(Mandatory = $true)]
    [string] $HardwareRoot,

    [Parameter(Mandatory = $true)]
    [string] $WindowsCiRunId,

    [Parameter(Mandatory = $true)]
    [string] $HardwareRunId,

    [Parameter(Mandatory = $true)]
    [string] $TrustedRunsPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param([string] $LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Evidence file is missing: $LiteralPath"
    }
    return [IO.File]::ReadAllText(
        [IO.Path]::GetFullPath($LiteralPath),
        [Text.Encoding]::UTF8) | ConvertFrom-Json
}

function Get-SingleEvidenceFile {
    param(
        [string] $Root,
        [string] $Filter,
        [string] $Description
    )
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Filter)
    if ($files.Count -ne 1) {
        throw "Expected exactly one $Description under '$Root'; found $($files.Count)."
    }
    return $files[0].FullName
}

function Assert-Commit {
    param(
        [string] $Actual,
        [string] $Expected,
        [string] $Description
    )
    if ([string]::IsNullOrWhiteSpace($Actual) -or
        $Actual.Trim().ToLowerInvariant() -cne $Expected) {
        throw "$Description commit '$Actual' does not match candidate '$Expected'."
    }
}

function Get-RelativeEvidencePath {
    param(
        [string] $Root,
        [string] $Path
    )
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence path '$pathFull' is outside '$rootFull'."
    }
    return $pathFull.Substring($rootFull.Length + 1).Replace('\', '/')
}

$candidate = $CandidateCommit.Trim().ToLowerInvariant()
if ($candidate -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
    throw "CandidateCommit is invalid: '$CandidateCommit'."
}
foreach ($root in @($ContractsRoot, $CleanBuildRoot, $HardwareRoot)) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Evidence root is missing: $root"
    }
}
if ($WindowsCiRunId -notmatch '^[1-9][0-9]*$' -or
    $HardwareRunId -notmatch '^[1-9][0-9]*$') {
    throw 'Workflow run identifiers must be positive decimal integers.'
}
$trustedRuns = Read-JsonFile $TrustedRunsPath
Assert-Commit ([string]$trustedRuns.candidateCommit) $candidate 'Trusted run metadata'
foreach ($entry in @(
    @{ Value = $trustedRuns.windowsCi; RunId = $WindowsCiRunId; Path = '.github/workflows/windows-ci.yml'; Name = 'Windows CI' },
    @{ Value = $trustedRuns.hardware; RunId = $HardwareRunId; Path = '.github/workflows/hardware-evidence.yml'; Name = 'Hardware evidence' }
)) {
    if ([string]$entry.Value.runId -cne $entry.RunId) {
        throw "$($entry.Name) trusted workflow run ID is invalid."
    }
    if ([string]$entry.Value.workflowPath -cne $entry.Path) {
        throw "$($entry.Name) trusted workflow path is invalid."
    }
    if ([string]$entry.Value.conclusion -cne 'success') {
        throw "$($entry.Name) trusted workflow conclusion is not success."
    }
    Assert-Commit ([string]$entry.Value.headSha) $candidate "$($entry.Name) workflow run"
}

$contractFiles = @(Get-ChildItem -LiteralPath $ContractsRoot -Recurse -File -Filter 'summary.json')
if ($contractFiles.Count -ne 2) {
    throw "Expected two contract summaries; found $($contractFiles.Count)."
}
$contractShells = New-Object System.Collections.Generic.List[string]
foreach ($file in $contractFiles) {
    $summary = Read-JsonFile $file.FullName
    if ([string]$summary.suite -cne 'contracts' -or [string]$summary.status -cne 'PASS') {
        throw "Contract summary is not PASS: $($file.FullName)"
    }
    Assert-Commit ([string]$summary.repositoryCommit) $candidate 'Contract summary'
    if ([bool]$summary.sourceDirty) {
        throw "Contract summary reports dirty source: $($file.FullName)"
    }
    $contractShells.Add([string]$summary.runner.childPowerShellVersion)
}
if (-not @($contractShells | Where-Object { $_ -like '5.1.*' }).Count -or
    -not @($contractShells | Where-Object { $_ -notlike '5.1.*' }).Count) {
    throw 'Contract evidence does not contain both Windows PowerShell 5.1 and PowerShell 7.'
}

$packageSummaryPath = Join-Path $CleanBuildRoot 'package\summary.json'
$integrationSummaryPath = Join-Path $CleanBuildRoot 'integration\summary.json'
foreach ($entry in @(
    @{ Path = $packageSummaryPath; Suite = 'package' },
    @{ Path = $integrationSummaryPath; Suite = 'integration' }
)) {
    $summary = Read-JsonFile $entry.Path
    if ([string]$summary.suite -cne $entry.Suite -or [string]$summary.status -cne 'PASS') {
        throw "$($entry.Suite) summary is not PASS."
    }
    Assert-Commit ([string]$summary.repositoryCommit) $candidate "$($entry.Suite) summary"
    if ([bool]$summary.sourceDirty) {
        throw "$($entry.Suite) summary reports dirty source."
    }
}
$candidateManifestPath = Get-SingleEvidenceFile `
    -Root (Join-Path $CleanBuildRoot 'candidate') `
    -Filter 'SBMS.release.json' `
    -Description 'candidate release manifest'
$candidateManifest = Read-JsonFile $candidateManifestPath
Assert-Commit ([string]$candidateManifest.source.commit) $candidate 'Candidate release manifest'
if ([bool]$candidateManifest.source.dirty) {
    throw 'Candidate release manifest reports dirty source.'
}
$candidateManifestHash = (Get-FileHash -LiteralPath $candidateManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

$hardwareSummaryPath = Get-SingleEvidenceFile `
    -Root $HardwareRoot `
    -Filter 'summary.json' `
    -Description 'hardware summary'
$hardwareWorkflowPath = Get-SingleEvidenceFile `
    -Root $HardwareRoot `
    -Filter 'workflow-run.json' `
    -Description 'hardware workflow metadata'
$hardware = Read-JsonFile $hardwareSummaryPath
$hardwareWorkflow = Read-JsonFile $hardwareWorkflowPath
if ([string]$hardware.result -cne 'PASS' -or [string]$hardware.scenario -eq 'AuditOnly') {
    throw 'A qualified candidate requires a PASS non-AuditOnly hardware scenario.'
}
if (-not [bool]$hardware.observationOnly -or [bool]$hardware.driverInstallOrRemovalAttempted) {
    throw 'Hardware evidence violated the observation-only contract.'
}
Assert-Commit ([string]$hardware.repositoryCommit) $candidate 'Hardware summary'
Assert-Commit ([string]$hardware.testedPayload.sourceCommit) $candidate 'Tested payload'
if (-not [bool]$hardware.testedPayload.artifactsVerified) {
    throw 'Hardware evidence did not verify all manifest-bound payload artifacts.'
}
if ([string]$hardware.testedPayload.manifestSha256 -cne $candidateManifestHash) {
    throw 'The hardware-tested release manifest does not match the retained CI candidate.'
}
Assert-Commit ([string]$hardwareWorkflow.repositoryCommit) $candidate 'Hardware workflow'
if ([string]$hardwareWorkflow.workflowRunId -cne $HardwareRunId) {
    throw 'Hardware workflow metadata does not match the selected run ID.'
}

$evidenceRecords = New-Object System.Collections.Generic.List[object]
$evidenceRecords.Add([pscustomobject][ordered]@{
    path = 'trusted-runs.json'
    sha256 = (Get-FileHash -LiteralPath $TrustedRunsPath -Algorithm SHA256).Hash.ToLowerInvariant()
})
foreach ($entry in @(
    @{ Root = $ContractsRoot; Bundle = 'contracts' },
    @{ Root = (Join-Path $CleanBuildRoot 'package'); Bundle = 'package-report' },
    @{ Root = (Join-Path $CleanBuildRoot 'integration'); Bundle = 'integration-report' },
    @{ Root = (Join-Path $CleanBuildRoot 'candidate'); Bundle = 'candidate' },
    @{ Root = $HardwareRoot; Bundle = 'hardware' }
)) {
    foreach ($file in @(Get-ChildItem -LiteralPath $entry.Root -Recurse -File)) {
        $evidenceRecords.Add([pscustomobject][ordered]@{
            path = $entry.Bundle + '/' + (Get-RelativeEvidencePath $entry.Root $file.FullName)
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
}
$index = [pscustomobject][ordered]@{
    schemaVersion = 1
    status = 'QUALIFIED'
    candidateCommit = $candidate
    windowsCiRunId = $WindowsCiRunId
    hardwareRunId = $HardwareRunId
    hardwareScenario = [string]$hardware.scenario
    candidateManifestSha256 = $candidateManifestHash
    evidence = $evidenceRecords.ToArray()
}
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
[IO.File]::WriteAllText(
    $outputFullPath,
    (($index | ConvertTo-Json -Depth 8) + "`n"),
    (New-Object Text.UTF8Encoding($false)))
Write-Host "Qualified release-candidate evidence: $outputFullPath"
