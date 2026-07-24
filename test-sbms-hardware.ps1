<#
.SYNOPSIS
Collects SBMS hardware acceptance evidence without installing or removing drivers.

.PARAMETER Scenario
AuditOnly writes evidence and runs the read-only SBMSNative --list audit when available.
There is intentionally no All scenario. Run SingleOutput, MultiGroup, StreamOnly, and
TopologyRecovery as four independent invocations so each result has complete evidence.
#>
[CmdletBinding()]
param(
    [string] $Scenario = 'AuditOnly',

    [switch] $AcknowledgeSystemChanges,
    [string] $EvidenceDirectory,

    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 60,

    [ValidateRange(0, 600)]
    [int] $StableSeconds = 5,

    [ValidateRange(0, 32)]
    [int] $ExpectedVirtualCount = 1,

    [ValidateRange(0, 32)]
    [int] $ExpectedNativeCount = 1
)

$ErrorActionPreference = 'Stop'

$validScenarios = @('AuditOnly', 'SingleOutput', 'MultiGroup', 'StreamOnly', 'TopologyRecovery')
if ($Scenario -eq 'All') {
    Write-Error "Scenario 'All' is intentionally unsupported. Run four independent calls: SingleOutput, MultiGroup, StreamOnly, and TopologyRecovery."
    exit 1
}
if ($Scenario -notin $validScenarios) {
    Write-Error ("Unknown scenario '{0}'. Valid scenarios: {1}" -f $Scenario, ($validScenarios -join ', '))
    exit 1
}

if ($Scenario -ne 'AuditOnly' -and -not $AcknowledgeSystemChanges) {
    Write-Error "Scenario '$Scenario' requires -AcknowledgeSystemChanges. This harness remains observation-only and never installs or removes drivers."
    exit 1
}
if ($Scenario -ne 'AuditOnly' -and $StableSeconds -lt 1) {
    Write-Error 'Non-AuditOnly scenarios require -StableSeconds of at least 1.'
    exit 1
}

if (-not $PSBoundParameters.ContainsKey('ExpectedVirtualCount') -and $Scenario -eq 'MultiGroup') {
    $ExpectedVirtualCount = 2
}
if (-not $PSBoundParameters.ContainsKey('ExpectedNativeCount') -and $Scenario -eq 'MultiGroup') {
    $ExpectedNativeCount = 2
}
if (-not $PSBoundParameters.ContainsKey('ExpectedNativeCount') -and $Scenario -eq 'StreamOnly') {
    $ExpectedNativeCount = 0
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $EvidenceDirectory = Join-Path $env:TEMP (Join-Path 'SBMS-Hardware-Evidence' $stamp)
}
$EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

$script:Checks = New-Object System.Collections.Generic.List[object]
$script:StartedProcesses = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$script:NativeExe = $null
$script:LatestNativeList = ''
$script:ObservationSamples = New-Object System.Collections.Generic.List[object]
$script:CopiedSessionLog = $null
$script:CopiedSessionLogSource = $null
$script:RecoveryLogMarker = $null
$script:CopiedSessionLogIsCurrent = $false

function Add-Check {
    param(
        [string] $Name,
        [ValidateSet('PASS', 'FAIL', 'SKIP')]
        [string] $Status,
        [string] $Detail
    )
    $script:Checks.Add([pscustomobject][ordered]@{
        name = $Name
        status = $Status
        detail = $Detail
    })
    Write-Host ('[{0}] {1}: {2}' -f $Status, $Name, $Detail)
}

function Write-EvidenceText {
    param([string] $Name, [object] $Content)
    $path = Join-Path $EvidenceDirectory $Name
    $text = if ($null -eq $Content) { '' } elseif ($Content -is [string]) { $Content } else { $Content | Out-String -Width 240 }
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($true))
    return $path
}

function Get-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ProcessEvidence {
    $names = @('SBMS.exe', 'SBMSGui.exe', 'SBMSDeviceHost.exe', 'SBMSNative.exe', 'DisplayBridge.exe', 'IddSampleApp.exe')
    $rows = @(Get-CimInstance Win32_Process | Where-Object { $names -contains $_.Name } | ForEach-Object {
        [pscustomobject][ordered]@{
            Name = $_.Name
            ProcessId = [int]$_.ProcessId
            ParentProcessId = [int]$_.ParentProcessId
            SessionId = [int]$_.SessionId
            ExecutablePath = $_.ExecutablePath
            CommandLine = $_.CommandLine
            CreationDate = $_.CreationDate
        }
    })
    return $rows
}

function Test-PathWithinRoot {
    param([string] $Path, [string] $Root)
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }
    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
        $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
        return $fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith($fullRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Test-TrustedSbmsExecutablePath {
    param([string] $Path)
    if (Test-PathWithinRoot $Path $PSScriptRoot) {
        return $true
    }
    if (${env:ProgramFiles} -and (Test-PathWithinRoot $Path (Join-Path ${env:ProgramFiles} 'SBMS'))) {
        return $true
    }
    if (${env:ProgramFiles(x86)} -and (Test-PathWithinRoot $Path (Join-Path ${env:ProgramFiles(x86)} 'SBMS'))) {
        return $true
    }
    return $false
}

function Find-NativeExecutable {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add((Join-Path $PSScriptRoot 'SBMSNative.exe'))
    if (${env:ProgramFiles}) {
        $candidates.Add((Join-Path ${env:ProgramFiles} 'SBMS\SBMSNative.exe'))
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'SBMS\SBMSNative.exe'))
    }
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='SBMSNative.exe'" -ErrorAction SilentlyContinue)) {
        if (-not [string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            $candidates.Add($process.ExecutablePath)
        }
    }
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

function Invoke-NativeList {
    if ([string]::IsNullOrWhiteSpace($script:NativeExe)) {
        return [pscustomobject]@{ Available = $false; ExitCode = $null; Output = ''; Error = 'SBMSNative.exe not found' }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:NativeExe
    $psi.Arguments = '--list'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()
    $script:StartedProcesses.Add($process)
    $finished = $process.WaitForExit(10000)
    if (-not $finished) {
        try { $process.Kill() } catch {}
        try { $process.WaitForExit() } catch {}
        return [pscustomobject]@{ Available = $true; ExitCode = $null; Output = ''; Error = 'SBMSNative --list timed out after 10 seconds' }
    }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $combined = ($stdout + $(if ([string]::IsNullOrWhiteSpace($stderr)) { '' } else { [Environment]::NewLine + $stderr })).Trim()
    return [pscustomobject]@{ Available = $true; ExitCode = $process.ExitCode; Output = $combined; Error = $stderr.Trim() }
}

function Get-VirtualDisplayCount {
    param([string] $ListOutput)
    if ([string]::IsNullOrWhiteSpace($ListOutput)) {
        return 0
    }
    $deviceNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in ($ListOutput -split "`r?`n")) {
        if ($line -match '^(\\\\\.\\DISPLAY\d+).*\sname=(.+)$') {
            $deviceName = $Matches[1].Trim()
            $name = $Matches[2].Trim()
            if ($name -match '^(?i:SBMS Virtual Display|SBMS Display|SBMS Indirect Display)$') {
                [void]$deviceNames.Add($deviceName)
            }
        }
    }
    return $deviceNames.Count
}

function Get-RuntimeSnapshot {
    param([switch] $ProcessOnly)

    $snapshotStartedUtc = (Get-Date).ToUniversalTime()
    $currentSessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $processRows = @(Get-ProcessEvidence)
    $processSampleUtc = (Get-Date).ToUniversalTime()
    $trustedSessionRows = @($processRows | Where-Object {
        $_.SessionId -eq $currentSessionId -and (Test-TrustedSbmsExecutablePath $_.ExecutablePath)
    })
    $gui = @($trustedSessionRows | Where-Object { $_.Name -in @('SBMS.exe', 'SBMSGui.exe') })
    $guiPids = @($gui | Select-Object -ExpandProperty ProcessId)
    $host = @($trustedSessionRows | Where-Object {
        $_.Name -eq 'SBMSDeviceHost.exe' -and $_.ParentProcessId -in $guiPids
    })
    $native = @($trustedSessionRows | Where-Object {
        $_.Name -eq 'SBMSNative.exe' -and $_.ParentProcessId -in $guiPids
    })

    $listStartedUtc = $null
    $listCompletedUtc = $null
    if ($ProcessOnly) {
        $list = [pscustomobject]@{ Available = $false; ExitCode = $null; Output = ''; Error = 'Process-only sample; SBMSNative --list was not executed' }
    } else {
        $listStartedUtc = (Get-Date).ToUniversalTime()
        $list = Invoke-NativeList
        $listCompletedUtc = (Get-Date).ToUniversalTime()
        $script:LatestNativeList = $list.Output
    }
    return [pscustomobject][ordered]@{
        timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        snapshotStartedUtc = $snapshotStartedUtc.ToString('o')
        processSampleUtc = $processSampleUtc.ToString('o')
        nativeListStartedUtc = if ($null -eq $listStartedUtc) { $null } else { $listStartedUtc.ToString('o') }
        nativeListCompletedUtc = if ($null -eq $listCompletedUtc) { $null } else { $listCompletedUtc.ToString('o') }
        processOnly = [bool]$ProcessOnly
        sessionId = $currentSessionId
        guiPids = @($guiPids | Sort-Object)
        hostPids = @($host | Select-Object -ExpandProperty ProcessId | Sort-Object)
        nativePids = @($native | Select-Object -ExpandProperty ProcessId | Sort-Object)
        guiProcesses = @($gui | Select-Object Name, ProcessId, ParentProcessId, SessionId, ExecutablePath, CommandLine)
        hostProcesses = @($host | Select-Object Name, ProcessId, ParentProcessId, SessionId, ExecutablePath, CommandLine)
        nativeProcesses = @($native | Select-Object Name, ProcessId, ParentProcessId, SessionId, ExecutablePath, CommandLine)
        virtualCount = if (-not $ProcessOnly -and $list.Available -and $list.ExitCode -eq 0) { Get-VirtualDisplayCount $list.Output } else { -1 }
        nativeListAvailable = $list.Available
        nativeListExitCode = $list.ExitCode
        nativeListError = $list.Error
        nativeListRaw = $list.Output
    }
}

function Get-PidSignature {
    param([object[]] $Pids)
    return (@($Pids | Sort-Object) -join ',')
}

function Test-ExpectedRuntime {
    param([object] $Snapshot)
    return $Snapshot.guiPids.Count -eq 1 -and
        $Snapshot.hostPids.Count -eq 1 -and
        $Snapshot.nativePids.Count -eq $ExpectedNativeCount -and
        $Snapshot.virtualCount -eq $ExpectedVirtualCount
}

function Observe-StableRuntime {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $stableSince = $null
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Get-RuntimeSnapshot
        $script:ObservationSamples.Add($last)
        if (Test-ExpectedRuntime $last) {
            if ($null -eq $stableSince) { $stableSince = Get-Date }
            if (((Get-Date) - $stableSince).TotalSeconds -ge $StableSeconds) { return $last }
        } else {
            $stableSince = $null
        }
        Start-Sleep -Milliseconds 500
    }
    return $last
}

function Read-SharedLogLines {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }

    $stream = $null
    $reader = $null
    try {
        $stream = New-Object System.IO.FileStream(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true)
        $text = $reader.ReadToEnd()
        return @($text -split "`r?`n" | Where-Object { $_.Length -gt 0 })
    } finally {
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $stream) { $stream.Dispose() }
    }
}

function Get-CurrentSessionLogMarker {
    $logDirectory = Join-Path $env:LOCALAPPDATA 'SBMS\logs'
    $session = Get-ChildItem -LiteralPath $logDirectory -Filter 'SBMS-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $session) {
        return [pscustomobject]@{ SourcePath = $null; LineCount = 0 }
    }
    $lines = @(Read-SharedLogLines $session.FullName)
    return [pscustomobject]@{ SourcePath = $session.FullName; LineCount = $lines.Count }
}

function Observe-TopologyRecovery {
    $initialDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $initialStableSince = $null
    $initial = $null
    $initialStable = $false
    while ((Get-Date) -lt $initialDeadline) {
        $initial = Get-RuntimeSnapshot
        $script:ObservationSamples.Add($initial)
        $initialMatches = $initial.guiPids.Count -eq 1 -and
            $initial.hostPids.Count -eq 1 -and
            $initial.nativePids.Count -eq 1 -and
            $initial.virtualCount -eq 1
        if ($initialMatches) {
            if ($null -eq $initialStableSince) { $initialStableSince = Get-Date }
            if (((Get-Date) - $initialStableSince).TotalSeconds -ge $StableSeconds) {
                $initialStable = $true
                break
            }
        } else {
            $initialStableSince = $null
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $initialStable) {
        return [pscustomobject]@{
            Initial = $initial
            Final = $initial
            InitialStable = $false
            HostPidUnchanged = $false
            NativeZeroObserved = $false
            NewNativePidObserved = $false
            Stable = $false
            LogMarker = $null
        }
    }

    $script:RecoveryLogMarker = Get-CurrentSessionLogMarker
    Write-Host '[INFO] Initial GUI=1, host=1, native=1, virtual=1 baseline is stable. Trigger topology recovery now.'
    if (-not [string]::IsNullOrWhiteSpace($script:RecoveryLogMarker.SourcePath)) {
        Write-Host ("[INFO] Recovery log marker: {0} after {1} line(s)" -f $script:RecoveryLogMarker.SourcePath, $script:RecoveryLogMarker.LineCount)
    }
    $initialHost = Get-PidSignature $initial.hostPids
    $initialNative = Get-PidSignature $initial.nativePids
    $hostUnchanged = $initial.hostPids.Count -eq 1
    $nativeZeroObserved = $false
    $newNativePidObserved = $false
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $initial

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $last = Get-RuntimeSnapshot -ProcessOnly
        $script:ObservationSamples.Add($last)
        if ((Get-PidSignature $last.hostPids) -ne $initialHost) {
            $hostUnchanged = $false
        }
        $nativeSignature = Get-PidSignature $last.nativePids
        if (-not $nativeZeroObserved -and $last.nativePids.Count -eq 0) {
            $nativeZeroObserved = $true
        }
        if ($last.nativePids.Count -gt 0 -and $nativeSignature -ne $initialNative) {
            $newNativePidObserved = $true
            break
        }
    }

    $stableSince = $null
    if ($newNativePidObserved) {
        $finalDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $finalDeadline) {
            $last = Get-RuntimeSnapshot
            $script:ObservationSamples.Add($last)
            if ((Get-PidSignature $last.hostPids) -ne $initialHost) {
                $hostUnchanged = $false
            }
            $finalMatches = $last.guiPids.Count -eq 1 -and
                $last.hostPids.Count -eq 1 -and
                $last.nativePids.Count -eq 1 -and
                $last.virtualCount -eq 1
            if ($hostUnchanged -and $finalMatches) {
                if ($null -eq $stableSince) { $stableSince = Get-Date }
                if (((Get-Date) - $stableSince).TotalSeconds -ge $StableSeconds) { break }
            } else {
                $stableSince = $null
            }
            Start-Sleep -Milliseconds 500
        }
    }
    return [pscustomobject]@{
        Initial = $initial
        Final = $last
        InitialStable = $initialStable
        HostPidUnchanged = $hostUnchanged
        NativeZeroObserved = $nativeZeroObserved
        NewNativePidObserved = $newNativePidObserved
        Stable = ($null -ne $stableSince -and ((Get-Date) - $stableSince).TotalSeconds -ge $StableSeconds)
        LogMarker = $script:RecoveryLogMarker
    }
}

function Copy-GuiLogs {
    $logDirectory = Join-Path $env:LOCALAPPDATA 'SBMS\logs'
    if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
        Add-Check 'GuiLogs' 'SKIP' "Log directory not found: $logDirectory"
        return @()
    }
    $copied = New-Object System.Collections.Generic.List[string]
    $manifest = New-Object System.Collections.Generic.List[object]
    $currentSessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $runningGui = @(Get-ProcessEvidence | Where-Object {
        $_.Name -in @('SBMS.exe', 'SBMSGui.exe') -and
        $_.SessionId -eq $currentSessionId -and
        (Test-TrustedSbmsExecutablePath $_.ExecutablePath)
    } | Sort-Object CreationDate -Descending | Select-Object -First 1)
    $latest = Join-Path $logDirectory 'latest.log'
    if (Test-Path -LiteralPath $latest -PathType Leaf) {
        $destination = Join-Path $EvidenceDirectory 'gui-latest.log'
        Copy-Item -LiteralPath $latest -Destination $destination -Force
        $copied.Add($destination)
        $latestItem = Get-Item -LiteralPath $latest
        $manifest.Add([pscustomobject][ordered]@{
            kind = 'latest'
            sourcePath = $latestItem.FullName
            creationTimeUtc = $latestItem.CreationTimeUtc.ToString('o')
            lastWriteTimeUtc = $latestItem.LastWriteTimeUtc.ToString('o')
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        })
    }
    $session = $null
    if ($null -ne $script:RecoveryLogMarker -and
        -not [string]::IsNullOrWhiteSpace($script:RecoveryLogMarker.SourcePath) -and
        (Test-Path -LiteralPath $script:RecoveryLogMarker.SourcePath -PathType Leaf)) {
        $session = Get-Item -LiteralPath $script:RecoveryLogMarker.SourcePath
    } else {
        $session = Get-ChildItem -LiteralPath $logDirectory -Filter 'SBMS-*.log' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    }
    if ($null -ne $session) {
        $destination = Join-Path $EvidenceDirectory 'gui-session-latest.log'
        Copy-Item -LiteralPath $session.FullName -Destination $destination -Force
        $copied.Add($destination)
        $script:CopiedSessionLog = $destination
        $script:CopiedSessionLogSource = $session.FullName
        $guiStartUtc = $null
        if ($runningGui.Count -gt 0 -and $null -ne $runningGui[0].CreationDate) {
            $guiStartUtc = ([datetime]$runningGui[0].CreationDate).ToUniversalTime()
            $script:CopiedSessionLogIsCurrent = $session.CreationTimeUtc -le $guiStartUtc.AddMinutes(2) -and
                $session.LastWriteTimeUtc -ge $guiStartUtc.AddSeconds(-5)
        }
        $manifest.Add([pscustomobject][ordered]@{
            kind = 'session'
            sourcePath = $session.FullName
            creationTimeUtc = $session.CreationTimeUtc.ToString('o')
            lastWriteTimeUtc = $session.LastWriteTimeUtc.ToString('o')
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            runningGuiPid = if ($runningGui.Count -gt 0) { $runningGui[0].ProcessId } else { $null }
            runningGuiStartUtc = if ($null -ne $guiStartUtc) { $guiStartUtc.ToString('o') } else { $null }
            currentSessionMatch = $script:CopiedSessionLogIsCurrent
            recoveryMarkerLineCount = if ($null -ne $script:RecoveryLogMarker) { $script:RecoveryLogMarker.LineCount } else { $null }
        })
    }
    Write-EvidenceText 'gui-log-manifest.json' ($manifest | ConvertTo-Json -Depth 5) | Out-Null
    if ($copied.Count -gt 0) {
        Add-Check 'GuiLogs' 'PASS' ("Copied {0} GUI log file(s)" -f $copied.Count)
    } else {
        Add-Check 'GuiLogs' 'SKIP' 'No latest or session GUI logs were found'
    }
    return @($copied)
}

function Test-GuiLifecycleLog {
    param([switch] $RequireRecovery)

    if ([string]::IsNullOrWhiteSpace($script:CopiedSessionLog) -or -not (Test-Path -LiteralPath $script:CopiedSessionLog -PathType Leaf)) {
        Add-Check 'LifecycleLog' 'SKIP' 'Current GUI session log was not available for transition verification'
        return
    }
    if (-not $script:CopiedSessionLogIsCurrent) {
        Add-Check 'LifecycleLog' 'SKIP' 'Newest GUI session log could not be correlated with the running GUI process start time'
        return
    }

    $lines = @(Read-SharedLogLines $script:CopiedSessionLog)
    if (-not ($lines | Where-Object { $_.IndexOf('Starting -> Running', [System.StringComparison]::Ordinal) -ge 0 } | Select-Object -First 1)) {
        Add-Check 'LifecycleLog' 'FAIL' 'Current GUI session log is missing Starting -> Running'
        return
    }

    if (-not $RequireRecovery) {
        Add-Check 'LifecycleLog' 'PASS' 'Current GUI session log contains Starting -> Running'
        return
    }

    if ($null -eq $script:RecoveryLogMarker -or
        [string]::IsNullOrWhiteSpace($script:RecoveryLogMarker.SourcePath) -or
        -not ([System.IO.Path]::GetFullPath($script:RecoveryLogMarker.SourcePath).Equals(
            [System.IO.Path]::GetFullPath($script:CopiedSessionLogSource),
            [System.StringComparison]::OrdinalIgnoreCase))) {
        Add-Check 'LifecycleLog' 'SKIP' 'Recovery log marker could not be matched to the copied current-session log'
        return
    }

    $markerLineCount = [int]$script:RecoveryLogMarker.LineCount
    $appended = if ($lines.Count -gt $markerLineCount) { @($lines[$markerLineCount..($lines.Count - 1)]) } else { @() }
    $recoveryGeneration = $null
    $recoveringIndex = -1
    for ($i = 0; $i -lt $appended.Count; ++$i) {
        if ($appended[$i] -match 'Running -> Recovering generation=(\d+)') {
            $recoveryGeneration = $Matches[1]
            $recoveringIndex = $i
            break
        }
    }
    $runningIndex = -1
    if ($recoveringIndex -ge 0) {
        for ($i = $recoveringIndex + 1; $i -lt $appended.Count; ++$i) {
            if ($appended[$i] -match ('Recovering -> Running generation=' + [regex]::Escape($recoveryGeneration) + '(?:\D|$)')) {
                $runningIndex = $i
                break
            }
        }
    }
    if ($recoveringIndex -lt 0 -or $runningIndex -lt 0) {
        Add-Check 'LifecycleLog' 'FAIL' 'Appended current-session log does not contain ordered Running -> Recovering and Recovering -> Running transitions with the same generation'
    } else {
        Add-Check 'LifecycleLog' 'PASS' ("Appended current-session log contains ordered recovery transitions for generation {0}" -f $recoveryGeneration)
    }
}

$startedUtc = (Get-Date).ToUniversalTime()
$environmentInfo = $null
$osInfo = $null
$nativeAudit = $null
$summaryPath = Join-Path $EvidenceDirectory 'summary.json'
$exitCode = 0

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $environmentInfo = [pscustomobject][ordered]@{
        computerName = $env:COMPUTERNAME
        userName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        administrator = Get-IsAdministrator
        userInteractive = [Environment]::UserInteractive
        sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        scenario = $Scenario
        observationOnly = $true
        acknowledgedSystemChanges = [bool]$AcknowledgeSystemChanges
    }
    $osInfo = [pscustomobject][ordered]@{
        caption = $os.Caption
        version = $os.Version
        buildNumber = $os.BuildNumber
        architecture = $os.OSArchitecture
        lastBootUpTime = $os.LastBootUpTime
    }
    Write-EvidenceText 'environment.txt' (($environmentInfo | Format-List | Out-String) + ($osInfo | Format-List | Out-String)) | Out-Null
    Add-Check 'EnvironmentAudit' 'PASS' 'Captured OS, administrator, interactive-session and PowerShell state'

    $processEvidence = @(Get-ProcessEvidence)
    Write-EvidenceText 'processes.txt' ($processEvidence | Format-Table -AutoSize) | Out-Null
    Add-Check 'ProcessAudit' 'PASS' ("Captured {0} related running process(es)" -f $processEvidence.Count)

    try {
        $pnp = @(Get-PnpDevice -ErrorAction Stop | Where-Object {
            $_.Class -eq 'Display' -or $_.FriendlyName -match '(?i)SBMS|IddSample|DisplayBridge|Indirect Display'
        } | Select-Object Status, Class, FriendlyName, InstanceId, Problem)
        Write-EvidenceText 'pnp-devices.txt' ($pnp | Format-Table -AutoSize) | Out-Null
        Add-Check 'PnpAudit' 'PASS' ("Captured {0} display/SBMS-related PnP device(s)" -f $pnp.Count)
    } catch {
        Write-EvidenceText 'pnp-devices.txt' $_.Exception.ToString() | Out-Null
        Add-Check 'PnpAudit' 'SKIP' $_.Exception.Message
    }

    try {
        $drivers = @(Get-CimInstance Win32_PnPSignedDriver | Where-Object {
            $_.DeviceClass -eq 'DISPLAY' -or $_.DeviceName -match '(?i)SBMS|IddSample|DisplayBridge|Indirect Display'
        } | Select-Object DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned, DeviceID)
        Write-EvidenceText 'signed-drivers.txt' ($drivers | Format-Table -AutoSize) | Out-Null
        Add-Check 'DriverAudit' 'PASS' ("Captured {0} signed display/SBMS-related driver record(s)" -f $drivers.Count)
    } catch {
        Write-EvidenceText 'signed-drivers.txt' $_.Exception.ToString() | Out-Null
        Add-Check 'DriverAudit' 'SKIP' $_.Exception.Message
    }

    $script:NativeExe = Find-NativeExecutable
    $nativeAudit = Invoke-NativeList
    Write-EvidenceText 'native-list.txt' ("Executable: $($script:NativeExe)`r`nExitCode: $($nativeAudit.ExitCode)`r`nError: $($nativeAudit.Error)`r`n`r`n$($nativeAudit.Output)") | Out-Null
    if (-not $nativeAudit.Available) {
        Add-Check 'NativeListAudit' 'SKIP' $nativeAudit.Error
    } elseif ($nativeAudit.ExitCode -eq 0) {
        Add-Check 'NativeListAudit' 'PASS' 'SBMSNative --list completed successfully'
    } else {
        Add-Check 'NativeListAudit' 'SKIP' ("SBMSNative --list exited with code {0}: {1}" -f $nativeAudit.ExitCode, $nativeAudit.Error)
    }

    if ($Scenario -eq 'AuditOnly') {
        Add-Check 'AuditOnly' 'PASS' 'Wrote local evidence and executed the read-only SBMSNative --list audit when available; no SBMS product, driver, PnP, or display-topology state was changed'
    } elseif ($Scenario -eq 'TopologyRecovery') {
        $recovery = Observe-TopologyRecovery
        if ($recovery.InitialStable) {
            Add-Check 'TopologyRecoveryInitialStable' 'PASS' ("Initial runtime held GUI=1, host=1, native=1, virtual=1 for $StableSeconds second(s)")
        } else {
            Add-Check 'TopologyRecoveryInitialStable' 'FAIL' 'Initial GUI=1, host=1, native=1, virtual=1 baseline did not become stable'
        }
        if ($recovery.HostPidUnchanged) {
            Add-Check 'TopologyRecoveryHostPid' 'PASS' ("Host PID remained {0}" -f (Get-PidSignature $recovery.Initial.hostPids))
        } else {
            Add-Check 'TopologyRecoveryHostPid' 'FAIL' 'Host PID was absent or changed during observation'
        }
        if ($recovery.NativeZeroObserved) {
            Add-Check 'TopologyRecoveryNativeStopped' 'PASS' 'Observed native process count reach zero after the stable baseline'
        } else {
            Add-Check 'TopologyRecoveryNativeStopped' 'SKIP' 'The 100ms sampler did not capture native=0; ordered same-generation lifecycle logs and a new same-parent native PID remain mandatory'
        }
        if ($recovery.NewNativePidObserved) {
            Add-Check 'TopologyRecoveryNativePid' 'PASS' 'Observed a new non-empty native PID owned by the same trusted GUI session'
        } else {
            Add-Check 'TopologyRecoveryNativePid' 'FAIL' 'No new same-parent native PID was observed after the stable baseline'
        }
        if ($recovery.Stable) {
            Add-Check 'RuntimeStable' 'PASS' ("Final runtime held GUI=1, host=1, native=1, virtual=1 for $StableSeconds second(s)")
        } else {
            Add-Check 'RuntimeStable' 'FAIL' 'Final GUI=1, host=1, native=1, virtual=1 state did not become stable after the required recovery observations'
        }
    } else {
        $finalSnapshot = Observe-StableRuntime
        if ($null -ne $finalSnapshot -and (Test-ExpectedRuntime $finalSnapshot)) {
            Add-Check 'RuntimeStable' 'PASS' ("Observed already-running GUI=1, host=1, native=$ExpectedNativeCount, virtual=$ExpectedVirtualCount for $StableSeconds second(s)")
        } else {
            $detail = if ($null -eq $finalSnapshot) { 'No runtime sample was captured' } else {
                "Observed GUI=$($finalSnapshot.guiPids.Count), host=$($finalSnapshot.hostPids.Count), native=$($finalSnapshot.nativePids.Count), virtual=$($finalSnapshot.virtualCount); expected GUI=1, host=1, native=$ExpectedNativeCount, virtual=$ExpectedVirtualCount"
            }
            Add-Check 'RuntimeStable' 'FAIL' $detail
        }
    }

    [void](Copy-GuiLogs)
    if ($Scenario -ne 'AuditOnly') {
        Test-GuiLifecycleLog -RequireRecovery:($Scenario -eq 'TopologyRecovery')
    }
} catch {
    Add-Check 'HarnessExecution' 'FAIL' $_.Exception.ToString()
} finally {
    foreach ($ownedProcess in @($script:StartedProcesses)) {
        if ($null -ne $ownedProcess) {
            try {
                if (-not $ownedProcess.HasExited) {
                    $ownedProcess.Kill()
                    $ownedProcess.WaitForExit()
                }
            } catch {}
            try { $ownedProcess.Dispose() } catch {}
        }
    }

    try {
        Write-EvidenceText 'observation-samples.txt' ($script:ObservationSamples | ConvertTo-Json -Depth 8) | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($script:LatestNativeList)) {
            Write-EvidenceText 'native-list-latest.txt' $script:LatestNativeList | Out-Null
        }
        $failed = @($script:Checks | Where-Object { $_.status -eq 'FAIL' })
        $criticalEvidenceSkipped = @($script:Checks | Where-Object {
            $_.status -eq 'SKIP' -and
            (
                ($Scenario -eq 'AuditOnly' -and $_.name -in @('PnpAudit', 'DriverAudit', 'NativeListAudit')) -or
                ($Scenario -ne 'AuditOnly' -and $_.name -in @('NativeListAudit', 'GuiLogs', 'LifecycleLog'))
            )
        })
        if ($failed.Count -gt 0) {
            $exitCode = 1
        } elseif ($criticalEvidenceSkipped.Count -gt 0) {
            $exitCode = 2
        }
        $result = if ($exitCode -eq 2) { 'INCONCLUSIVE' } elseif ($exitCode -eq 1) { 'FAIL' } else { 'PASS' }
        $summary = [pscustomobject][ordered]@{
            schemaVersion = 1
            scenario = $Scenario
            result = $result
            startedUtc = $startedUtc.ToString('o')
            finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
            observationOnly = $true
            driverInstallOrRemovalAttempted = $false
            expectedVirtualCount = $ExpectedVirtualCount
            expectedNativeCount = $ExpectedNativeCount
            timeoutSeconds = $TimeoutSeconds
            stableSeconds = $StableSeconds
            evidenceDirectory = $EvidenceDirectory
            nativeExecutable = $script:NativeExe
            environment = $environmentInfo
            os = $osInfo
            checks = @($script:Checks | ForEach-Object { $_ })
            samples = @($script:ObservationSamples | ForEach-Object { $_ })
            recoveryLogMarker = $script:RecoveryLogMarker
        }
        $json = $summary | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($summaryPath, $json, [System.Text.UTF8Encoding]::new($true))
    } catch {
        Write-Error ("Failed to write hardware-test summary: {0}" -f $_.Exception.Message)
        $exitCode = 1
    }
}

Write-Host "Evidence: $EvidenceDirectory"
Write-Host "Summary:  $summaryPath"
if ($exitCode -eq 0) {
    Write-Host '[PASS] SBMS hardware harness completed.'
} elseif ($exitCode -eq 2) {
    Write-Host '[INCONCLUSIVE] SBMS hardware harness is missing critical NativeList or current-session log evidence.'
} else {
    Write-Host '[FAIL] SBMS hardware harness detected one or more failed checks.'
}
exit $exitCode
