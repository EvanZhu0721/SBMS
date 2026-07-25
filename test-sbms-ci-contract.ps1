Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$assertions = 0

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw $Message
    }
    $script:assertions++
}

function Read-Utf8 {
    param([string] $RelativePath)
    return [IO.File]::ReadAllText(
        (Join-Path $Root $RelativePath),
        [Text.Encoding]::UTF8)
}

$windowsCi = Read-Utf8 '.github/workflows/windows-ci.yml'
$hardwareCi = Read-Utf8 '.github/workflows/hardware-evidence.yml'
$qualifyCi = Read-Utf8 '.github/workflows/qualify-release-candidate.yml'
$runner = Read-Utf8 'invoke-sbms-ci.ps1'
$releaseEvidence = Read-Utf8 'Confirm-SBMSReleaseCandidateEvidence.ps1'
$hardwareHarness = Read-Utf8 'test-sbms-hardware.ps1'
$nativeBuild = Read-Utf8 'build-sbms-native.ps1'
$hostBuild = Read-Utf8 'build-sbms-device-host.ps1'
$setupBuild = Read-Utf8 'build-sbms-setup.ps1'
$maintenanceBuild = Read-Utf8 'build-sbms-maintenance-service.ps1'
$package = Read-Utf8 'package-sbms.ps1'
$productionPackage = Read-Utf8 'package-sbms-production.ps1'
$setupRuntime = Read-Utf8 'installer/SBMSSetup.cs'
$releaseVerifier = Read-Utf8 'installer/ReleaseIntegrityVerifier.cs'
$maintenanceHost = Read-Utf8 'maintenance-service/SBMSMaintenanceService.cs'
$maintenanceContracts = Read-Utf8 'maintenance-service/MaintenanceServiceRuntimeContracts.cs'

Assert-True ($windowsCi.Contains('runs-on: windows-2022')) 'Hosted CI must pin windows-2022.'
Assert-True ($windowsCi.Contains('permissions:') -and $windowsCi.Contains('contents: read')) 'CI permissions must be read-only.'
Assert-True ($windowsCi.Contains('persist-credentials: false')) 'Checkout credentials must not persist.'
Assert-True ($windowsCi.Contains('if: always()') -and $windowsCi.Contains('upload-artifact@v4')) 'Failure reports must be retained.'
Assert-True (($windowsCi -split 'retention-days: 90').Count -ge 3) 'Contract and clean-build reports must be retained for 90 days.'
Assert-True ($windowsCi.Contains('Retain exact candidate payload') -and $windowsCi.Contains("candidate'")) 'Windows CI must retain the exact package consumed by hardware qualification.'
Assert-True ($windowsCi.Contains('-RequireCleanSource')) 'Package gate must reject dirty source.'
Assert-True (-not $windowsCi.Contains('install-sbms-driver.ps1')) 'Hosted CI must not stage drivers.'
Assert-True (-not $windowsCi.Contains('install-sbms-program-files.ps1')) 'Hosted CI must not write Program Files.'
Assert-True (-not $windowsCi.Contains('Invoke-SBMSHardwareLab')) 'Hosted CI must not run the mutating hardware lab.'

Assert-True ($hardwareCi.Contains('workflow_dispatch:')) 'Hardware evidence must be manually dispatched.'
Assert-True (-not $hardwareCi.Contains('pull_request:')) 'Hardware evidence must never run on pull requests.'
Assert-True ($hardwareCi.Contains('self-hosted') -and $hardwareCi.Contains('sbms-hardware')) 'Hardware evidence requires a labeled self-hosted runner.'
Assert-True ($hardwareCi.Contains('test-sbms-hardware.ps1')) 'Hardware workflow must use the observation-only harness.'
Assert-True ($hardwareCi.Contains('retention-days: 90')) 'Hardware evidence must be retained for release qualification.'
Assert-True ($hardwareCi.Contains('candidate_sha') -and $hardwareCi.Contains('release_manifest_path')) 'Hardware evidence must bind a candidate commit and tested release manifest.'
Assert-True ($hardwareCi.Contains('ref: ${{ inputs.candidate_sha }}')) 'Hardware evidence must checkout the candidate commit explicitly.'
Assert-True ($hardwareCi.Contains('workflow-run.json') -and $hardwareCi.Contains('inputs.candidate_sha')) 'Hardware artifact metadata must retain its candidate and workflow identity.'
Assert-True ($hardwareCi.Contains("New-Item -ItemType Directory -Path `$evidence")) 'Hardware workflow must create its evidence directory before writing metadata.'

Assert-True ($qualifyCi.Contains('workflow_dispatch:')) 'Release qualification must be an explicit post-hardware workflow.'
Assert-True ($qualifyCi.Contains('actions: read') -and $qualifyCi.Contains('contents: read')) 'Release qualification permissions must remain read-only.'
Assert-True (($qualifyCi -split 'actions/download-artifact@v4').Count -ge 4) 'Release qualification must download contract, integration/package, and hardware artifacts.'
Assert-True ($qualifyCi.Contains('Confirm-SBMSReleaseCandidateEvidence.ps1')) 'Release qualification must verify all downloaded evidence.'
Assert-True ($qualifyCi.Contains('/actions/runs/') -and $qualifyCi.Contains('GITHUB_API_URL')) 'Release qualification must independently query workflow run provenance.'
Assert-True ($qualifyCi.Contains('trusted-runs.json') -and $qualifyCi.Contains('-TrustedRunsPath')) 'Release qualification must retain and verify trusted workflow metadata.'
Assert-True ($qualifyCi.Contains(".github/workflows/windows-ci.yml") -and $qualifyCi.Contains(".github/workflows/hardware-evidence.yml")) 'Release qualification must pin the expected workflow paths.'
Assert-True ($qualifyCi.Contains('head_sha') -and $qualifyCi.Contains('conclusion')) 'Release qualification must verify candidate SHA and successful workflow conclusion.'
Assert-True ($qualifyCi.Contains('qualified-release-candidate-') -and $qualifyCi.Contains('retention-days: 90')) 'Qualified release bundle must be retained for 90 days.'
Assert-True ($releaseEvidence.Contains('testedPayload.manifestSha256') -and $releaseEvidence.Contains('candidateManifestHash')) 'Release qualification must bind the hardware-tested payload to the retained candidate manifest.'
Assert-True ($releaseEvidence.Contains('trustedRuns.windowsCi') -and $releaseEvidence.Contains('trustedRuns.hardware')) 'Release evidence verification must consume trusted workflow metadata.'
Assert-True ($hardwareHarness.Contains('payloadBindings') -and $hardwareHarness.Contains('Test-TestedPayloadProcess')) 'Every runtime snapshot must retain exact candidate path and hash bindings.'
Assert-True ($hardwareHarness.Contains("Get-TestedPayloadExecutable -TestedPayload `$script:TestedPayload -FileName 'SBMSNative.exe'")) 'Native display observations must execute the manifest-bound candidate binary.'
Assert-True ($hardwareHarness.Contains('PnpDevices') -and $hardwareHarness.Contains('present, problem-free PnP device')) 'Driver evidence must bind the candidate package to a present, healthy PnP device.'

Assert-True ($runner.Contains('schemaVersion = 1')) 'CI summary schema is missing.'
Assert-True ($runner.Contains('logSha256')) 'CI logs must be hash-bound in summary.json.'
Assert-True ($runner.Contains("EnvironmentVariables.Remove('PSModulePath')")) 'Child PowerShell module paths must be rebuilt for the selected engine.'
Assert-True ($runner.Contains('status --porcelain --untracked-files=all')) 'CI source cleanliness must include untracked files.'
Assert-True ($runner.Contains('summary.json') -and $runner.Contains('summary.md')) 'Machine and human summaries are required.'
Assert-True ($runner.Contains('RedirectStandardOutput') -and $runner.Contains('RedirectStandardError')) 'Test stdout/stderr must be retained.'
Assert-True ($runner.Contains("'test-sbms-installer-transaction.ps1'")) 'Hosted contract suite must execute the transactional installer fault matrix.'
Assert-True ($runner.Contains("'test-sbms-escrow-manifest-model.ps1'")) 'Hosted contract suite must execute the escrow manifest v2 model contract.'
Assert-True ($runner.Contains("'test-sbms-protected-escrow-manifest-store.ps1'")) 'Hosted contract suite must execute the protected escrow manifest store contract.'
Assert-True ($runner.Contains("'test-sbms-protected-payload-store-contracts.ps1'")) 'Hosted contract suite must execute the protected payload store model contract.'
Assert-True ($runner.Contains("'test-sbms-protected-payload-recovery-planner.ps1'")) 'Hosted contract suite must execute the protected payload recovery crash matrix.'
Assert-True ($runner.Contains("'test-sbms-protected-payload-transaction-executor.ps1'")) 'Hosted contract suite must execute the protected payload transaction executor crash matrix.'
Assert-True ($runner.Contains("'test-sbms-protected-payload-build-contracts.ps1'")) 'Hosted contract suite must execute the protected payload build workspace contract.'
Assert-True ($runner.Contains("'test-sbms-protected-payload-namespace-owner-contracts.ps1'")) 'Hosted contract suite must execute the payload namespace owner and broker contract.'
Assert-True ($runner.Contains("'test-sbms-maintenance-service-contracts.ps1'")) 'Hosted contract suite must execute the maintenance service offline runtime contract.'
$contractsBlock = [regex]::Match(
    $runner,
    '(?s)\$contracts\s*=\s*@\((?<body>.*?)\)\s*\r?\n')
Assert-True ($contractsBlock.Success -and $contractsBlock.Groups['body'].Value -match "(?m)^\s*'test-sbms-protected-payload-build-state-machine\.ps1',?\s*$") 'Hosted contract suite must execute the protected payload build state-machine crash matrix.'
Assert-True ($contractsBlock.Success -and $contractsBlock.Groups['body'].Value -match "(?m)^\s*'test-sbms-durable-protected-payload-workspace-model\.ps1',?\s*$") 'Hosted contract suite must execute the durable protected payload workspace-model contract.'
Assert-True ($contractsBlock.Success -and $contractsBlock.Groups['body'].Value -match "(?m)^\s*'test-sbms-windows-isolated-temp-protected-payload-native-tree\.ps1',?\s*$") 'Hosted contract suite must execute the Windows isolated-temp protected payload native-tree contract.'
Assert-True ($contractsBlock.Success -and $contractsBlock.Groups['body'].Value -match "(?m)^\s*'test-sbms-protected-payload-workspace-checkpoint-store\.ps1',?\s*$") 'Hosted contract suite must execute the protected payload workspace checkpoint store contract.'
Assert-True ($runner.Contains("'test-sbms-file-transaction-journal-store.ps1'")) 'Hosted contract suite must execute the production journal store contract.'
Assert-True ($runner.Contains("'test-sbms-windows-transaction-platform.ps1'")) 'Hosted contract suite must execute the Windows transaction platform contract.'
Assert-True ($runner.Contains("'test-sbms-windows-mutation-execution.ps1'")) 'Hosted contract suite must execute the Windows mutation execution outcome matrix.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-file-transaction-journal-store.ps1') -PathType Leaf) 'Production journal store contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-escrow-manifest-store.ps1') -PathType Leaf) 'Protected escrow manifest store contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-store-contracts.ps1') -PathType Leaf) 'Protected payload store model contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-recovery-planner.ps1') -PathType Leaf) 'Protected payload recovery planner contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-transaction-executor.ps1') -PathType Leaf) 'Protected payload transaction executor contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-build-contracts.ps1') -PathType Leaf) 'Protected payload build workspace contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-namespace-owner-contracts.ps1') -PathType Leaf) 'Payload namespace owner contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-build-state-machine.ps1') -PathType Leaf) 'Protected payload build state-machine contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-durable-protected-payload-workspace-model.ps1') -PathType Leaf) 'Durable protected payload workspace-model contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-windows-isolated-temp-protected-payload-native-tree.ps1') -PathType Leaf) 'Windows isolated-temp protected payload native-tree contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-protected-payload-workspace-checkpoint-store.ps1') -PathType Leaf) 'Protected payload workspace checkpoint store contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-windows-transaction-platform.ps1') -PathType Leaf) 'Windows transaction platform contract script is missing.'
Assert-True (Test-Path -LiteralPath (Join-Path $Root 'test-sbms-windows-mutation-execution.ps1') -PathType Leaf) 'Windows mutation execution contract script is missing.'
Assert-True ($runner.Contains("'test-sbms-installer-audit.ps1'")) 'Hosted contract suite must execute the read-only installer inventory and ownership tests.'

Assert-True ($nativeBuild.Contains('Resolve-SBMSVsDevCmd')) 'Native build must use shared toolchain discovery.'
Assert-True ($hostBuild.Contains('Resolve-SBMSVsDevCmd')) 'Device-host build must use shared toolchain discovery.'
Assert-True (-not $nativeBuild.Contains('$VsDevCmd = "C:\BuildTools')) 'Native build still hard-codes a personal toolchain.'
Assert-True (-not $hostBuild.Contains('$VsDevCmd = "C:\BuildTools')) 'Device-host build still hard-codes a personal toolchain.'
Assert-True ($setupBuild.Contains('/platform:x64')) 'Setup must compile explicitly as x64 before using SetupAPI inventory.'
Assert-True ($setupBuild.Contains('$WindowsMutationExecutionSource')) 'Setup must compile the Windows mutation execution contract into the product binary.'
Assert-True ($setupBuild.Contains('$ProtectedEscrowManifestStoreSource')) 'Setup must compile the protected escrow manifest store into the product binary.'
Assert-True ($setupBuild.Contains('$ProtectedPayloadStoreContractsSource')) 'Setup must compile the protected payload store contracts into the product binary.'
Assert-True ($setupBuild.Contains('$ProtectedPayloadRecoveryPlannerSource')) 'Setup must compile the protected payload recovery planner into the product binary.'
Assert-True ($setupBuild.Contains('$ProtectedPayloadTransactionExecutorSource')) 'Setup must compile the protected payload transaction executor into the product binary.'
Assert-True ($setupBuild.Contains('$ProtectedPayloadBuildContractsSource')) 'Setup must compile the protected payload build workspace contract into the product binary.'
$buildStateMachineReferences = [regex]::Matches(
    $setupBuild,
    '\$ProtectedPayloadBuildStateMachineSource')
Assert-True ($buildStateMachineReferences.Count -eq 2) 'Setup must define and pass the protected payload build state machine source to the compiler.'
$durableWorkspaceModelReferences = [regex]::Matches(
    $setupBuild,
    '\$DurableProtectedPayloadBuildWorkspaceModelSource')
Assert-True ($durableWorkspaceModelReferences.Count -eq 2) 'Setup must define and pass the durable protected payload workspace model source to the compiler.'
$workspaceCheckpointStoreReferences = [regex]::Matches(
    $setupBuild,
    '\$ProtectedPayloadWorkspaceCheckpointStoreSource')
Assert-True ($workspaceCheckpointStoreReferences.Count -eq 2) 'Setup must define and pass the protected payload workspace checkpoint store source to the compiler.'
$namespaceOwnerContractReferences = [regex]::Matches(
    $setupBuild,
    '\$ProtectedPayloadNamespaceOwnerContractsSource')
Assert-True ($namespaceOwnerContractReferences.Count -eq 2) 'Setup must define and pass the payload namespace owner contract source to the compiler.'
$brokerContractReferences = [regex]::Matches(
    $setupBuild,
    '\$ProtectedPayloadBrokerContractsSource')
Assert-True ($brokerContractReferences.Count -eq 2) 'Setup must define and pass the payload broker contract source to the compiler.'
$maintenanceContractReferences = [regex]::Matches(
    $setupBuild,
    '\$MaintenanceServiceRuntimeContractsSource')
Assert-True ($maintenanceContractReferences.Count -eq 2) 'Setup build must compile the maintenance service pure runtime contracts.'
Assert-True ($maintenanceBuild.Contains('/platform:x64')) 'Maintenance service must compile explicitly as x64.'
Assert-True ($maintenanceBuild.Contains('/reference:System.ServiceProcess.dll')) 'Maintenance service host must use ServiceBase.'
Assert-True ($maintenanceBuild.Contains('SBMSMaintenanceService.exe')) 'Maintenance service build output is missing.'
Assert-True ($package.Contains('"build-sbms-maintenance-service.ps1"') -and $package.Contains('"maintenance-service"')) 'Developer package must build and mirror maintenance service sources.'
$developerReleaseFiles = [regex]::Match(
    $package,
    '(?s)\$releaseFiles\s*=\s*@\((?<body>.*?)\)\s*\r?\n')
Assert-True ($developerReleaseFiles.Success -and -not $developerReleaseFiles.Groups['body'].Value.Contains('"SBMSMaintenanceService.exe"')) 'Developer release payload must exclude the offline maintenance service executable.'
Assert-True (-not $productionPackage.Contains('build-sbms-maintenance-service.ps1') -and -not $productionPackage.Contains('SBMSMaintenanceService.exe')) 'Production package must not build, sign, or copy the offline maintenance service.'
Assert-True (-not $releaseVerifier.Contains("'SBMSMaintenanceService.exe'")) 'Release verifier must not whitelist the offline maintenance service.'
Assert-True (-not $setupRuntime.Contains('RequireFile("SBMSMaintenanceService.exe")') -and -not $setupRuntime.Contains('CopyPayload("SBMSMaintenanceService.exe"')) 'Current root-payload installer must not install the stable maintenance host.'
Assert-True (-not $maintenanceHost.Contains('--console') -and -not $maintenanceHost.Contains('--contract-self-test')) 'Shipped maintenance host must not expose interactive or self-test modes.'
Assert-True ($maintenanceHost.Contains('args.Length != 0') -and $maintenanceHost.Contains('ServiceBase.Run')) 'Shipped maintenance host must accept only zero-argument SCM startup.'
Assert-True ($maintenanceHost.Contains('OnShutdown') -and $maintenanceHost.Contains('StopRuntime')) 'SCM shutdown must use the bounded lifecycle stop path.'
Assert-True ($maintenanceContracts.Contains('IMaintenanceCommandAuthorizer') -and $maintenanceContracts.Contains('MaintenanceAuthorizationEvidence')) 'Dispatcher authorization must consume injected trusted evidence.'
Assert-True (-not $maintenanceContracts.Contains('new Fake') -and -not $maintenanceHost.Contains('new Fake')) 'Production maintenance sources must not contain test fakes.'
Assert-True ($package.Contains('"test-sbms-protected-payload-namespace-owner-contracts.ps1"')) 'Package source mirror must include the payload namespace owner contract wrapper.'
Assert-True (-not $package.Contains('Sort-Object LastWriteTime')) 'Package must not select signing material by timestamp.'
Assert-True (-not $package.Contains('-Filter "SBMSIndirectDisplay.cer"')) 'Development package must not discover an implicit certificate.'

$fakeRoot = Join-Path ([IO.Path]::GetTempPath()) ('SBMS-CI-Toolchain-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fakeRoot -Force | Out-Null
    $fakeVsDevCmd = Join-Path $fakeRoot 'VsDevCmd.bat'
    [IO.File]::WriteAllText($fakeVsDevCmd, '@echo off', (New-Object Text.UTF8Encoding($false)))
    Import-Module (Join-Path $Root 'build/SBMS.Toolchain.psm1') -Force
    Assert-True ((Resolve-SBMSVsDevCmd -ExplicitPath $fakeVsDevCmd) -eq $fakeVsDevCmd) 'Explicit toolchain path does not round-trip.'
    $rejectedMissing = $false
    try {
        Resolve-SBMSVsDevCmd -ExplicitPath (Join-Path $fakeRoot 'missing.bat')
    } catch {
        $rejectedMissing = $true
    }
    Assert-True $rejectedMissing 'Missing explicit toolchain path must fail closed.'
}
finally {
    if (Test-Path -LiteralPath $fakeRoot) {
        Remove-Item -LiteralPath $fakeRoot -Recurse -Force
    }
}

Write-Host "CI contract passed: $assertions assertions"
