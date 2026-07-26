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

function Get-CSharpBalancedBlock {
    param(
        [string] $Text,
        [int] $OpeningBrace,
        [string] $Label
    )
    if ($OpeningBrace -lt 0 -or
        $OpeningBrace -ge $Text.Length -or
        $Text[$OpeningBrace] -ne '{') {
        throw "Missing C# block opening brace: $Label"
    }
    $depth = 0
    for ($index = $OpeningBrace; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($character -eq '{') {
            $depth++
        }
        elseif ($character -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $Text.Substring(
                    $OpeningBrace,
                    $index - $OpeningBrace + 1
                )
            }
        }
    }
    throw "Unbalanced C# block: $Label"
}

function Get-CSharpMethodBody {
    param(
        [string] $Text,
        [string] $Signature
    )
    $signatureIndex = $Text.IndexOf(
        $Signature,
        [StringComparison]::Ordinal
    )
    if ($signatureIndex -lt 0) {
        throw "Missing C# method signature: $Signature"
    }
    $openingBrace = $Text.IndexOf('{', $signatureIndex)
    if ($openingBrace -lt 0) {
        throw "Missing C# method opening brace: $Signature"
    }
    return Get-CSharpBalancedBlock `
        -Text $Text `
        -OpeningBrace $openingBrace `
        -Label $Signature
}

function Get-EmptyTryCerFinallyBody {
    param([string] $MethodBody)
    $matches = [regex]::Matches(
        $MethodBody,
        'PrepareConstrainedRegions\(\);\s*try\s*\{\s*\}\s*finally\s*'
    )
    if ($matches.Count -ne 1) {
        throw ("Expected exactly one empty-try CER finally; found " +
            $matches.Count)
    }
    $openingBrace = $MethodBody.IndexOf(
        '{',
        $matches[0].Index + $matches[0].Length
    )
    return Get-CSharpBalancedBlock `
        -Text $MethodBody `
        -OpeningBrace $openingBrace `
        -Label 'empty-try CER finally'
}

function Test-NativeAcquireCerShape {
    param([string] $MethodBody)
    try {
        $finallyBody = Get-EmptyTryCerFinallyBody $MethodBody
        return $finallyBody.Contains('ImpersonateNamedPipeClient') -and
            [regex]::IsMatch($finallyBody, 'armed\s*=\s*true')
    }
    catch {
        return $false
    }
}

function Test-LegacyNativeAcquireShape {
    param([string] $MethodBody)
    return [regex]::IsMatch(
        $MethodBody,
        'try\s*\{(?:(?!\}\s*finally).)*ImpersonateNamedPipeClient(?:(?!\}\s*finally).)*\}\s*finally\s*\{[\s\S]*?armed\s*=\s*true',
        [Text.RegularExpressions.RegexOptions]::Singleline
    )
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
$brokerContracts = Read-Utf8 'installer/ProtectedPayloadBrokerContracts.cs'
$maintenanceHost = Read-Utf8 'maintenance-service/SBMSMaintenanceService.cs'
$maintenanceContracts = Read-Utf8 'maintenance-service/MaintenanceServiceRuntimeContracts.cs'
$maintenanceClientAuthorization = Read-Utf8 'maintenance-service/MaintenanceClientAuthorization.cs'
$maintenanceWindowsClientNative = Read-Utf8 'maintenance-service/MaintenanceWindowsClientNative.cs'
$maintenanceContractTest = Read-Utf8 'test-sbms-maintenance-service-contracts.ps1'
$maintenancePipeWireTests = Read-Utf8 'tests/MaintenancePipeWireContractTests.cs'
$maintenanceRuntimeTests = Read-Utf8 'tests/MaintenanceServiceRuntimeContractTests.cs'
$maintenanceNativeAcquireMethod = Get-CSharpMethodBody `
    -Text $maintenanceWindowsClientNative `
    -Signature 'public void AcquireClient('
$maintenanceFailStopChildMethod = Get-CSharpMethodBody `
    -Text $maintenanceRuntimeTests `
    -Signature 'private static int RunNativeFailStopChild('
$maintenanceReplayStore = Read-Utf8 'maintenance-service/MaintenanceReplayProductionStore.cs'
$maintenanceReplayFactory = Read-Utf8 'maintenance-service/MaintenanceReplayFileTransactionJournalFactory.cs'
$journalStore = Read-Utf8 'installer/FileTransactionJournalStore.cs'
$installerJournal = Read-Utf8 'installer/InstallerJournal.cs'
$protectedEscrowStore = Read-Utf8 'installer/ProtectedEscrowManifestStore.cs'

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
Assert-True ($maintenanceContracts.Contains('FileReadData = 0x00000001') -and $maintenanceContracts.Contains('FileWriteData = 0x00000002') -and $maintenanceContracts.Contains('ClientDesiredAccess =') -and $maintenanceContracts.Contains('FileReadData | FileWriteData') -and $maintenanceContracts.Contains('ClientDesiredAccess.ToString(') -and $maintenanceContracts.Contains('CultureInfo.InvariantCulture') -and -not $maintenanceContracts.Contains('(A;;GRGW;;;BA)')) 'Maintenance pipe Administrators must receive only the exact FILE_READ_DATA | FILE_WRITE_DATA mask, never generic read/write.'
Assert-True ($maintenanceRuntimeTests.Contains('pipeAdministratorsAce.AccessMask') -and $maintenanceRuntimeTests.Contains('fileCreatePipeInstance') -and $maintenanceRuntimeTests.Contains('genericWrite') -and $maintenanceRuntimeTests.Contains('0x00000003')) 'Runtime ACL tests must parse the pipe descriptor and reject create-instance or GenericWrite authority for Administrators.'
Assert-True ($brokerContracts.Contains('class PayloadBrokerCommandCodec') -and $brokerContracts.Contains('new UTF8Encoding(false, true)') -and $brokerContracts.Contains('GetCharCount(stableBytes)') -and $brokerContracts.Contains('command.Validate()') -and $brokerContracts.Contains('RequireExactBytes(') -and $brokerContracts.Contains('MaxRequestPayload')) 'Command wire decoding must enforce strict UTF-8, semantic validation, size bounds, and canonical byte equivalence.'
Assert-True ($brokerContracts.Contains('HeaderLength = 16') -and $brokerContracts.Contains('MaxRequestPayload = 64 * 1024') -and $brokerContracts.Contains('MaxResponsePayload = 1024 * 1024') -and $brokerContracts.Contains('AckPayloadLength = 32') -and $brokerContracts.Contains('frame[0] = (byte)''S''') -and $brokerContracts.Contains('private const ushort Version = 1') -and $brokerContracts.Contains('ReadUInt32(frameBytes, 12) != 0') -and $brokerContracts.Contains('rawPayloadLength > Int32.MaxValue')) 'Maintenance pipe framing must retain its fixed little-endian header, caps, zero reserved field, and overflow rejection.'
Assert-True ($brokerContracts.Contains('FixedTimeEquals(actual, expected)') -and $brokerContracts.Contains('algorithm.ComputeHash(stablePayload)') -and $brokerContracts.Contains('frame.Kind != MaintenancePipeFrameKind.Ack') -and $brokerContracts.Contains('GetPayloadCopy()')) 'Acknowledgements must bind an immutable canonical response payload with an exact-kind constant-time SHA-256 check.'
Assert-True ($maintenanceContractTest.Contains('tests\MaintenancePipeWireContractTests.cs') -and $maintenanceRuntimeTests.Contains('MaintenancePipeWireContractTests.Run') -and $maintenancePipeWireTests.Contains('MalformedFrameFuzzIsBounded') -and $maintenancePipeWireTests.Contains('property order') -and $maintenancePipeWireTests.Contains('duplicate property') -and $maintenancePipeWireTests.Contains('multiple frames')) 'The maintenance runtime oracle must compile and execute strict canonical-command, frame-boundary, immutability, acknowledgement, and malformed-fuzz coverage.'
Assert-True ($maintenancePipeWireTests.Contains('byte[][] validSeeds') -and $maintenancePipeWireTests.Contains('mutationCoverage') -and $maintenancePipeWireTests.Contains('payloadMutationReached') -and $maintenancePipeWireTests.Contains('ExerciseSeedMutation') -and $maintenancePipeWireTests.Contains('iteration < 2000')) 'Frame fuzzing must mutate valid request, response, and acknowledgement seeds, retain per-field coverage, reach payload decoding, and keep the random malformed corpus.'
Assert-True ($maintenancePipeWireTests.Contains('PayloadBrokerResponseCodec.SerializeCanonical(') -and $maintenancePipeWireTests.Contains('PayloadBrokerResponseCodec.DeserializeAndValidate(') -and $maintenancePipeWireTests.Contains('changedResponse') -and $maintenancePipeWireTests.Contains('DecodeAckAndVerify(')) 'Acknowledgement integration must bind a real validated canonical broker response and reject mutated response bytes.'
Assert-True (-not $brokerContracts.Contains('CreateNamedPipe') -and -not $brokerContracts.Contains('ConnectNamedPipe') -and -not $brokerContracts.Contains('ImpersonateNamedPipeClient')) 'The strict wire codec slice must remain transport- and authorization-sequencing neutral.'
Assert-True ($maintenanceBuild.Contains('MaintenanceClientAuthorization.cs') -and $setupBuild.Contains('$MaintenanceClientAuthorizationSource')) 'Product builds must compile the maintenance client authorization contract.'
Assert-True ($maintenanceBuild.Contains('MaintenanceWindowsClientNative.cs') -and -not $setupBuild.Contains('MaintenanceWindowsClientNative')) 'Only the maintenance service build may compile the Windows client native adapter.'
Assert-True ($maintenanceClientAuthorization.Contains('ReadOnlyCollection') -and $maintenanceClientAuthorization.Contains('AuthenticationId') -and $maintenanceClientAuthorization.Contains('HasEnabledGroup')) 'Client token evidence must retain an immutable complete authorization snapshot.'
Assert-True ($maintenanceClientAuthorization.Contains('MaintenanceProductionClientPolicyAuthorizer') -and $maintenanceClientAuthorization.Contains('BuiltinAdministratorsSid') -and $maintenanceClientAuthorization.Contains('UseForDenyOnly') -and $maintenanceClientAuthorization.Contains('HighIntegrityRid')) 'Production client policy must retain exact SID, group-attribute, elevation, and integrity gates.'
Assert-True ($maintenanceClientAuthorization.Contains('CaptureScoped') -and $maintenanceClientAuthorization.Contains('impersonation.CaptureScoped') -and -not $maintenanceClientAuthorization.Contains('void Impersonate()') -and -not $maintenanceClientAuthorization.Contains('void Revert()')) 'Client request sequencing must consume only evidence returned by a scoped impersonate-capture-revert API.'
Assert-True (-not $maintenanceClientAuthorization.Contains('RunImpersonated') -and -not $maintenanceClientAuthorization.Contains('DllImport') -and -not $maintenanceClientAuthorization.Contains('CreateNamedPipe') -and -not $maintenanceClientAuthorization.Contains('RunAsClient')) 'Offline client authorization slice must not introduce ambiguous callbacks, native pipes, or impersonation bindings.'
Assert-True ($maintenanceWindowsClientNative.Contains('OpenThreadToken') -and $maintenanceWindowsClientNative.Contains('TokenQuery,') -and $maintenanceWindowsClientNative.Contains('true,') -and -not $maintenanceWindowsClientNative.Contains('OpenProcessToken')) 'Token snapshots must query only the already-impersonated thread token with openAsSelf=true.'
Assert-True ($maintenanceWindowsClientNative.Contains('MaximumTokenInformationLength') -and $maintenanceWindowsClientNative.Contains('MaximumSnapshotAttempts') -and $maintenanceWindowsClientNative.Contains('SameSnapshot') -and $maintenanceWindowsClientNative.Contains('GroupCount')) 'Native token snapshots must bound buffers and statistics consistency retries.'
Assert-True ($maintenanceWindowsClientNative.Contains('first.ExpirationTime == second.ExpirationTime') -and $maintenanceWindowsClientNative.Contains('first.DynamicCharged == second.DynamicCharged') -and $maintenanceWindowsClientNative.Contains('first.DynamicAvailable == second.DynamicAvailable') -and $maintenanceWindowsClientNative.Contains('first.PrivilegeCount == second.PrivilegeCount')) 'TOKEN_STATISTICS sandwich consistency must compare every statistics field, not only identity fields.'
Assert-True ($maintenanceWindowsClientNative.Contains('nativeRestricted | informationRestricted') -and $maintenanceWindowsClientNative.Contains('HasRestrictions') -and -not $maintenanceWindowsClientNative.Contains('IsTokenRestricted(token) ||')) 'Restricted-token evidence must read and merge both native restriction signals without short-circuiting.'
Assert-True ($maintenanceWindowsClientNative.Contains('buffer.Length != 1') -and $maintenanceWindowsClientNative.Contains('buffer.Length != sizeof(int)') -and $maintenanceWindowsClientNative.Contains('expected=1-or-4')) 'TokenHasRestrictions must accept only the documented DWORD or the empirically observed Windows 11 BOOLEAN width.'
Assert-True ($maintenanceWindowsClientNative.Contains('GetLengthSid') -and $maintenanceWindowsClientNative.Contains('IsValidSid') -and $maintenanceWindowsClientNative.Contains('S-1-16-')) 'Native SID snapshots must validate bounded SID bytes and the mandatory-label authority.'
Assert-True ($maintenanceWindowsClientNative.Contains('Marshal.SizeOf(') -and $maintenanceWindowsClientNative.Contains('typeof(MaintenanceNativeSidAndAttributes)') -and $maintenanceWindowsClientNative.Contains('Token SID points into its fixed native header')) 'TOKEN_USER and TOKEN_MANDATORY_LABEL parsing must retain the complete SID_AND_ATTRIBUTES header and reject header-overlapping SID pointers.'
Assert-True ($maintenanceWindowsClientNative.Contains('PrepareConstrainedRegions') -and $maintenanceWindowsClientNative.Contains('DangerousAddRef') -and $maintenanceWindowsClientNative.Contains('DangerousRelease') -and $maintenanceWindowsClientNative.Contains('armed = true') -and $maintenanceWindowsClientNative.Contains('ImpersonateNamedPipeClient') -and $maintenanceWindowsClientNative.Contains('RevertToSelf') -and $maintenanceWindowsClientNative.Contains('GetCurrentThreadIdValue') -and $maintenanceWindowsClientNative.Contains('TerminateUnsafeImpersonation')) 'Named-pipe impersonation ownership must be handle-scoped, CER-armed, thread-bound, exactly reverted, and terminal on unsafe cleanup.'
Assert-True (Test-NativeAcquireCerShape $maintenanceNativeAcquireMethod) 'MaintenanceNamedPipeClientNative.AcquireClient must place its P/Invoke and armed=true in the finally paired with an empty CER try.'
Assert-True (-not (Test-LegacyNativeAcquireShape $maintenanceNativeAcquireMethod)) 'MaintenanceNamedPipeClientNative.AcquireClient must reject the legacy P/Invoke-in-try/finally-arm shape.'
$legacyNativeAcquireFixture = @'
{
    RuntimeHelpers.PrepareConstrainedRegions();
    try
    {
        succeeded = Native.ImpersonateNamedPipeClient(handle);
    }
    finally
    {
        if (succeeded)
        {
            armed = true;
        }
    }
}
'@
Assert-True (-not (Test-NativeAcquireCerShape $legacyNativeAcquireFixture) -and (Test-LegacyNativeAcquireShape $legacyNativeAcquireFixture)) 'CER shape checks must positively identify and reject the legacy P/Invoke-in-try structure.'
$outsideArmNativeAcquireFixture = @'
{
    RuntimeHelpers.PrepareConstrainedRegions();
    try
    {
    }
    finally
    {
        succeeded = Native.ImpersonateNamedPipeClient(handle);
    }
    if (succeeded)
    {
        armed = true;
    }
}
'@
Assert-True (-not (Test-NativeAcquireCerShape $outsideArmNativeAcquireFixture)) 'CER shape checks must reject armed=true moved outside the exact paired finally block.'
Assert-True ([regex]::IsMatch($maintenanceWindowsClientNative, 'try\s*\{\s*\}\s*finally\s*\{[\s\S]*?DangerousAddRef[\s\S]*?AcquireClient[\s\S]*?RevertToSelf[\s\S]*?DangerousRelease')) 'Every borrowed pipe native operation must remain inside one standard CER-acquired SafeHandle DangerousAddRef lease.'
Assert-True ($maintenanceWindowsClientNative.Contains('failStopAttempted = true') -and $maintenanceWindowsClientNative.Contains('Environment.FailFast(reason, failStopCause)') -and $maintenanceWindowsClientNative.Contains('armed && !failStopAttempted')) 'Unsafe impersonation cleanup must make one terminal attempt and use a non-returning FailFast fallback without claiming safe ownership release.'
Assert-True ($maintenanceRuntimeTests.Contains('child-enter:') -and $maintenanceRuntimeTests.Contains('capture-enter:') -and $maintenanceRuntimeTests.Contains('terminator-enter:') -and $maintenanceRuntimeTests.Contains('returned-after-failstop') -and $maintenanceRuntimeTests.Contains('child.ExitCode == CorEFailFast') -and $maintenanceRuntimeTests.Contains('CorEUnhandledException')) 'Fail-stop subprocess tests must prove marker-backed branch entry, reject post-termination return, and distinguish FailFast from an ordinary unhandled CLR exception.'
Assert-True ($maintenanceFailStopChildMethod.IndexOf('TryDisableFailStopUi', [StringComparison]::Ordinal) -ge 0 -and $maintenanceFailStopChildMethod.IndexOf('TryDisableFailStopUi', [StringComparison]::Ordinal) -lt $maintenanceFailStopChildMethod.IndexOf('child-enter:', [StringComparison]::Ordinal)) 'Every FailFast child must install the process UI guard before its first execution marker or fault-capable test action.'
Assert-True ($maintenanceRuntimeTests.Contains('SemFailCriticalErrors = 0x0001') -and $maintenanceRuntimeTests.Contains('SemNoGpFaultErrorBox = 0x0002') -and $maintenanceRuntimeTests.Contains('WerFaultReportingNoUi = 32') -and $maintenanceRuntimeTests.Contains('FailStopUiNative.SetErrorMode') -and $maintenanceRuntimeTests.Contains('FailStopUiNative.WerSetFlags') -and $maintenanceRuntimeTests.IndexOf('FailStopUiNative.SetErrorMode', [StringComparison]::Ordinal) -lt $maintenanceRuntimeTests.IndexOf('FailStopUiNative.WerSetFlags', [StringComparison]::Ordinal)) 'FailFast children must set SEM_FAILCRITICALERRORS, SEM_NOGPFAULTERRORBOX, and WER_FAULT_REPORTING_NO_UI in process scope and in that order.'
Assert-True ($maintenanceRuntimeTests.Contains('ui-guard-failed:') -and $maintenanceRuntimeTests.Contains('return FailStopUiSetupFailed') -and $maintenanceRuntimeTests.Contains('FailStopUiSetupFailed = 96') -and $maintenanceRuntimeTests.Contains('child.ExitCode != FailStopUiSetupFailed')) 'UI-guard setup failure must be marker-visible and exit through a distinct controlled code without reaching FailFast.'
Assert-True (-not $maintenanceWindowsClientNative.Contains('SetErrorMode') -and -not $maintenanceWindowsClientNative.Contains('WerSetFlags') -and -not $maintenanceClientAuthorization.Contains('SetErrorMode') -and -not $maintenanceClientAuthorization.Contains('WerSetFlags')) 'Error-dialog and WER suppression P/Invokes must remain test-only and must not enter production authorization or native capture code.'
Assert-True ($maintenanceWindowsClientNative.Contains('if (active)') -and $maintenanceWindowsClientNative.Contains('scoped capture is already') -and $maintenanceWindowsClientNative.Contains('if (!armed)') -and $maintenanceWindowsClientNative.Contains('active = false')) 'Scoped named-pipe capture must reject lock-reentrant acquisition and clear the guard only after safe reversion.'
Assert-True (-not $maintenanceWindowsClientNative.Contains('InjectedPostSuccessFailure') -and -not $maintenanceWindowsClientNative.Contains('ImpersonateSelf') -and -not $maintenanceWindowsClientNative.Contains('IMaintenanceNamedPipeImpersonationNative')) 'Production native code must not retain test-only impersonation seams.'
Assert-True (-not $maintenanceWindowsClientNative.Contains('CreateNamedPipe') -and -not $maintenanceWindowsClientNative.Contains('ConnectNamedPipe') -and -not $maintenanceHost.Contains('MaintenanceWindowsTokenSnapshotReader') -and -not $maintenanceHost.Contains('MaintenanceNamedPipeClientImpersonationAdapter')) 'Offline native adapters must not create or host-connect a pipe.'
Assert-True (-not $maintenanceContracts.Contains('new Fake') -and -not $maintenanceHost.Contains('new Fake')) 'Production maintenance sources must not contain test fakes.'
Assert-True ($maintenanceBuild.Contains('InstallerJournal.cs') -and $maintenanceBuild.Contains('MaintenanceReplayProductionStore.cs')) 'Maintenance build must compile the shared atomic publisher and replay adapter.'
$maintenanceReplayReferences = [regex]::Matches(
    $setupBuild,
    '\$MaintenanceReplayProductionStoreSource')
Assert-True ($maintenanceReplayReferences.Count -eq 2) 'Setup must define and compile the maintenance production replay adapter.'
$maintenanceReplayFactoryReferences = [regex]::Matches(
    $setupBuild,
    '\$MaintenanceReplayFileTransactionJournalFactorySource')
Assert-True ($maintenanceReplayFactoryReferences.Count -eq 2) 'Setup must define and compile the shared FileTransactionJournalStore replay factory.'
Assert-True ($maintenanceReplayStore.Contains('@"maintenance-replay\v1"') -and $maintenanceReplayStore.Contains('AtomicDocumentBytePublisher')) 'Maintenance replay must use its fixed layout and the existing atomic publisher.'
Assert-True ($maintenanceReplayFactory.Contains('CreateMaintenanceReplayStore') -and $maintenanceReplayFactory.Contains('journalFileSystem') -and $maintenanceReplayFactory.Contains('AcquireTransactionLeaseForMaintenanceReplay') -and $maintenanceReplayFactory.Contains('transactionLeaseIdentity') -and $maintenanceReplayFactory.Contains('StorageAuthorityInvariantDigest') -and $maintenanceReplayFactory.Contains('installerStateRoot')) 'Maintenance replay factory must derive exact root authority and reuse the journal filesystem and global transaction lease.'
$codecStart = $maintenanceContracts.IndexOf('internal static class MaintenanceReplayRecordCodec', [StringComparison]::Ordinal)
$codecEnd = $maintenanceContracts.IndexOf('internal sealed class MaintenanceWriteBeforeAckExecutor', [StringComparison]::Ordinal)
Assert-True ($codecStart -ge 0 -and $codecEnd -gt $codecStart) 'Maintenance replay codec source boundaries are missing.'
$maintenanceCodec = $maintenanceContracts.Substring($codecStart, $codecEnd - $codecStart)
Assert-True ($maintenanceCodec.Contains('MaintenanceReplayContentFormatException') -and $maintenanceCodec.Contains('catch (SerializationException exception)') -and $maintenanceCodec.Contains('catch (InvalidDataContractException exception)')) 'Maintenance replay codec must whitelist deterministic serialization and validation format failures.'
Assert-True (-not $maintenanceCodec.Contains('catch (Exception')) 'Maintenance replay codec must not relabel resource or runtime failures as deterministic corruption.'
Assert-True ($maintenanceReplayStore.Contains('catch (MaintenanceReplayContentFormatException') -and $maintenanceReplayStore.Contains('catch (AtomicDocumentFormatException')) 'Maintenance replay fallback must catch only content and publisher format failures.'
Assert-True ($maintenanceReplayFactory.Contains('lock (lifetimeGate)') -and $maintenanceReplayStore.Contains('lock (lifetimeGate)') -and $journalStore.Contains('activeTransactionLeaseCount')) 'Replay factory, child acquisition, and parent disposal must share one lifecycle gate.'
Assert-True ($installerJournal.Contains('RejectedBeforeMutation') -and $installerJournal.Contains('Confirmed') -and $installerJournal.Contains('Uncertain')) 'Transaction lease release failures must expose all three typed outcomes.'
Assert-True ($installerJournal.Contains('IInstallerTransactionLeaseFaultSeam') -and $protectedEscrowStore.Contains('ReleaseMutexAndCleanup') -and $protectedEscrowStore.Contains('InstallerTransactionLeaseReleaseOutcome.Uncertain') -and $protectedEscrowStore.Contains('InstallerTransactionLeaseReleaseOutcome.Confirmed')) 'Real coordinator release and cleanup failures must remain injectable and typed.'
Assert-True ($installerJournal.Contains('AfterOwnershipRecorded') -and $installerJournal.Contains('BeforeLeaseIdAllocated') -and $protectedEscrowStore.Contains('RollBackInstalledOwnership') -and $protectedEscrowStore.Contains('Object.ReferenceEquals(ownedMutex, candidate)') -and $protectedEscrowStore.Contains('leaseIds.Peek() == installedLeaseId') -and $protectedEscrowStore.Contains('poisonAfterRollback')) 'Post-ownership acquisition faults and lease-id exhaustion must retain exact rollback and poison seams.'
Assert-True ($journalStore.Contains('private bool poisoned;') -and $journalStore.Contains('MarkTransactionLeasePoisoned') -and $journalStore.Contains('CompleteTransactionLeaseRelease')) 'Lifetime-bound transaction leases must retain uncertain reservations and complete only confirmed releases.'
Assert-True ($journalStore.Contains('enum ReleaseState') -and $journalStore.Contains('ReleaseState.Releasing') -and $journalStore.Contains('ReleaseState.Released') -and $journalStore.Contains('ReleaseState.Poisoned')) 'Lifetime-bound transaction lease release must remain a serialized four-state transition.'
Assert-True ($journalStore.Contains('MarkTransactionLeaseAcquisitionPoisoned') -and $journalStore.Contains('PoisonFromMaintenanceReplayAcquisition') -and $maintenanceReplayStore.Contains('RequireUncertainAcquisitionFailure')) 'Direct and replay acquisition cleanup failures must poison their shared parent with typed uncertainty.'
Assert-True ($maintenanceReplayStore.Contains('IMaintenanceReplayPostAcquireFaultSeam') -and $maintenanceReplayStore.Contains('AfterSharedLeaseAcquired') -and $maintenanceReplayFactory.Contains('CreateMaintenanceReplayStoreForFaultTesting')) 'Replay post-shared-acquire cleanup must remain fault-injectable.'
Assert-True ($journalStore.Contains('CreateProtectedEscrowManifestStore') -and $journalStore.Contains('CreateProtectedPayloadWorkspaceCheckpointStore') -and $journalStore.Contains('CreateDurableProtectedPayloadBuildWorkspaceModel')) 'All journal-backed child factories must remain owned by the shared lifetime.'
Assert-True ($maintenanceReplayStore.Contains('InstallerTransactionLeaseReleaseOutcome.Confirmed') -and $maintenanceReplayStore.Contains('CompleteDispose()')) 'Maintenance replay child lifetime must close only a confirmed or successful shared release.'
Assert-True (-not $maintenanceReplayStore.Contains('new Mutex') -and -not $maintenanceReplayStore.Contains('PathAtomicJournalFileSystem') -and -not $maintenanceReplayStore.Contains('GetTempPath') -and -not $maintenanceReplayStore.Contains('CurrentDirectory')) 'Production replay adapter must not create another lock domain or path fallback.'
Assert-True ($maintenanceHost.Contains('Maintenance production dependencies are incomplete.')) 'Maintenance host must fail closed while production dependencies are incomplete.'
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
