[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'Restart', 'Stop', 'StopAll')]
    [string]$Action,

    [string]$GroupId = '',

    [AllowNull()]
    [AllowEmptyString()]
    [string]$DisplayId,

    [ValidateRange(0, 65514)]
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$script:Utf8NoBom = [Text.UTF8Encoding]::new($false)
$script:ManagerName = 'SBMS'
$script:ManifestSchema = 1

function Write-JsonResult {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Value
    )

    $Value | ConvertTo-Json -Compress -Depth 5
}

function Assert-GroupId {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Id
    )

    if (
        [string]::IsNullOrWhiteSpace($Id) -or
        $Id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'
    ) {
        throw 'GroupId must contain 1-64 ASCII letters, digits, dots, underscores, or hyphens.'
    }
}

function Get-FullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return [IO.Path]::GetFullPath($Path)
}

function Test-SamePath {
    param(
        [Parameter(Mandatory)]
        [string]$Left,

        [Parameter(Mandatory)]
        [string]$Right
    )

    $leftPath = Get-FullPath -Path $Left
    $rightPath = Get-FullPath -Path $Right
    return [StringComparer]::OrdinalIgnoreCase.Equals($leftPath, $rightPath)
}

function Get-InstanceLayout {
    param(
        [Parameter(Mandatory)]
        [string]$Id
    )

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA is unavailable; the Sunshine instance root cannot be resolved.'
    }

    $root = Get-FullPath -Path (Join-Path $env:LOCALAPPDATA 'SBMS\sunshine')
    $instance = Get-FullPath -Path (Join-Path $root "group-$Id")
    $rootPrefix = $root.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar

    if (-not $instance.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The resolved instance path is outside the SBMS Sunshine root: $instance"
    }

    $credentials = Join-Path $instance 'credentials'
    return [pscustomobject]@{
        Root            = $root
        Instance        = $instance
        Credentials     = $credentials
        Config          = Join-Path $instance 'sunshine.conf'
        State           = Join-Path $instance 'sunshine_state.json'
        WebCredentials  = Join-Path $instance 'web_credentials.json'
        PrivateKey      = Join-Path $credentials 'cakey.pem'
        Certificate     = Join-Path $credentials 'cacert.pem'
        Apps            = Join-Path $instance 'apps.json'
        Log             = Join-Path $instance 'sunshine.log'
        Manifest        = Join-Path $instance 'instance.json'
    }
}

function Resolve-SunshineExecutable {
    if (-not [string]::IsNullOrWhiteSpace($env:SBMS_SUNSHINE_EXE)) {
        $override = Get-FullPath -Path $env:SBMS_SUNSHINE_EXE
        if (-not (Test-Path -LiteralPath $override -PathType Leaf)) {
            throw "SBMS_SUNSHINE_EXE does not point to a file: $override"
        }
        return $override
    }

    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles
    )
    $candidates = @(
        (Join-Path $programFiles 'Sunshine\sunshine.exe')
    )

    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} 'Sunshine\sunshine.exe'
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return Get-FullPath -Path $candidate
        }
    }

    throw 'Sunshine was not found under Program Files.'
}

function ConvertTo-SunshinePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FullPath -Path $Path).Replace('\', '/')
}

function Write-ManagedTextFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Contents
    )

    [IO.File]::WriteAllText($Path, $Contents, $script:Utf8NoBom)
}

function Ensure-AppsConfiguration {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout,

        [Parameter(Mandatory)]
        [string]$SunshineExecutable
    )

    if (Test-Path -LiteralPath $Layout.Apps -PathType Leaf) {
        return
    }

    $sunshineRoot = Split-Path -Parent $SunshineExecutable
    $source = Join-Path $sunshineRoot 'config\apps.json'
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        Copy-Item -LiteralPath $source -Destination $Layout.Apps
        return
    }

    Write-ManagedTextFile -Path $Layout.Apps -Contents '{"env":{},"apps":[]}'
}

function Write-InstanceConfiguration {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout,

        [Parameter(Mandatory)]
        [string]$NormalizedDisplayId,

        [Parameter(Mandatory)]
        [int]$BasePort,

        [Parameter(Mandatory)]
        [string]$Id
    )

    $outputLabel = $Id
    $numericId = 0
    if ([int]::TryParse($Id, [ref]$numericId)) {
        $outputLabel = ($numericId + 1).ToString()
    }
    $lines = @(
        '# Managed by SBMS. Runtime state is isolated per mapping group.'
        "port = $BasePort"
        "sunshine_name = SBMS Output $outputLabel"
        "output_name = $NormalizedDisplayId"
        'upnp = disabled'
        'dd_configuration_option = disabled'
        'system_tray = disabled'
        "file_state = $(ConvertTo-SunshinePath -Path $Layout.State)"
        "credentials_file = $(ConvertTo-SunshinePath -Path $Layout.WebCredentials)"
        "pkey = $(ConvertTo-SunshinePath -Path $Layout.PrivateKey)"
        "cert = $(ConvertTo-SunshinePath -Path $Layout.Certificate)"
        "file_apps = $(ConvertTo-SunshinePath -Path $Layout.Apps)"
        "log_path = $(ConvertTo-SunshinePath -Path $Layout.Log)"
    )
    $contents = ($lines -join [Environment]::NewLine) +
        [Environment]::NewLine
    Write-ManagedTextFile -Path $Layout.Config -Contents $contents
}

function Write-InstanceManifest {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout,

        [Parameter(Mandatory)]
        [string]$Id,

        [Parameter(Mandatory)]
        [string]$NormalizedDisplayId,

        [Parameter(Mandatory)]
        [int]$BasePort,

        [Parameter(Mandatory)]
        [int]$ProcessId,

        [Parameter(Mandatory)]
        [string]$SunshineExecutable
    )

    $manifest = [ordered]@{
        schema_version = $script:ManifestSchema
        managed_by     = $script:ManagerName
        group_id       = $Id
        display_id     = $NormalizedDisplayId
        port           = $BasePort
        pid            = $ProcessId
        sunshine_exe   = Get-FullPath -Path $SunshineExecutable
        config_path    = Get-FullPath -Path $Layout.Config
        log_path       = Get-FullPath -Path $Layout.Log
        started_at     = [DateTimeOffset]::Now.ToString('o')
    }
    $json = $manifest | ConvertTo-Json -Compress -Depth 4
    Write-ManagedTextFile -Path $Layout.Manifest -Contents $json
}

function Read-InstanceManifest {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout,

        [Parameter(Mandatory)]
        [string]$Id
    )

    if (-not (Test-Path -LiteralPath $Layout.Manifest -PathType Leaf)) {
        return $null
    }

    try {
        $json = [IO.File]::ReadAllText(
            $Layout.Manifest,
            [Text.Encoding]::UTF8
        )
        $manifest = $json | ConvertFrom-Json
    } catch {
        throw "The Sunshine instance manifest is invalid: $($_.Exception.Message)"
    }

    if (
        $null -eq $manifest.schema_version -or
        [int]$manifest.schema_version -ne $script:ManifestSchema -or
        [string]$manifest.managed_by -ne $script:ManagerName -or
        [string]$manifest.group_id -cne $Id
    ) {
        throw 'The Sunshine instance manifest is not owned by this SBMS group.'
    }

    if (
        [string]::IsNullOrWhiteSpace([string]$manifest.config_path) -or
        -not (Test-SamePath -Left ([string]$manifest.config_path) -Right $Layout.Config)
    ) {
        throw 'The Sunshine instance manifest points outside this group configuration.'
    }

    if (
        $null -eq $manifest.pid -or
        [int64]$manifest.pid -lt 1 -or
        [int64]$manifest.pid -gt [int]::MaxValue -or
        [string]::IsNullOrWhiteSpace([string]$manifest.sunshine_exe)
    ) {
        throw 'The Sunshine instance manifest does not contain a valid managed process.'
    }

    return $manifest
}

function Get-ManagedProcessRecord {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Manifest,

        [Parameter(Mandatory)]
        [string]$ExpectedConfig
    )

    $processId = [int]$Manifest.pid
    $record = Get-CimInstance -ClassName Win32_Process `
        -Filter "ProcessId = $processId" `
        -ErrorAction Stop |
        Select-Object -First 1
    if ($null -eq $record) {
        return $null
    }

    $actualExecutable = [string]$record.ExecutablePath
    if ([string]::IsNullOrWhiteSpace($actualExecutable)) {
        try {
            $actualExecutable = (
                Get-Process -Id $processId -ErrorAction Stop
            ).MainModule.FileName
        } catch {
            throw "PID $processId exists, but its executable path cannot be verified."
        }
    }

    if (
        -not (Test-SamePath `
            -Left $actualExecutable `
            -Right ([string]$Manifest.sunshine_exe))
    ) {
        throw "PID $processId is not the Sunshine executable recorded for this group."
    }

    $commandLine = [string]$record.CommandLine
    if ([string]::IsNullOrWhiteSpace($commandLine)) {
        throw "PID $processId exists, but its command line cannot be verified."
    }

    $escapedConfig = [regex]::Escape((Get-FullPath -Path $ExpectedConfig))
    $configArgument = "(?i)(?:^|\s)`"?$escapedConfig`"?(?=\s|$)"
    if ($commandLine -notmatch $configArgument) {
        throw "PID $processId is not using this group's Sunshine configuration."
    }

    return $record
}

function Remove-StaleManifest {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout
    )

    if (Test-Path -LiteralPath $Layout.Manifest -PathType Leaf) {
        Remove-Item -LiteralPath $Layout.Manifest -Force
    }
}

function Stop-ManagedInstance {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout,

        [Parameter(Mandatory)]
        [string]$Id
    )

    $manifest = Read-InstanceManifest -Layout $Layout -Id $Id
    if ($null -eq $manifest) {
        return [ordered]@{
            status     = 'not_running'
            pid        = $null
            port       = $null
            display_id = $null
            log_path   = Get-FullPath -Path $Layout.Log
        }
    }

    $record = Get-ManagedProcessRecord `
        -Manifest $manifest `
        -ExpectedConfig $Layout.Config
    if ($null -eq $record) {
        Remove-StaleManifest -Layout $Layout
        return [ordered]@{
            status     = 'not_running'
            pid        = $null
            port       = [int]$manifest.port
            display_id = [string]$manifest.display_id
            log_path   = [string]$manifest.log_path
        }
    }

    $processId = [int]$manifest.pid
    $process = Get-Process -Id $processId -ErrorAction Stop
    $closed = $false
    try {
        $closed = $process.CloseMainWindow()
    } catch {
        $closed = $false
    }

    if ($closed) {
        $null = $process.WaitForExit(2000)
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $processId -Force -ErrorAction Stop
        $null = $process.WaitForExit(10000)
    }
    if (-not $process.HasExited) {
        throw "Managed Sunshine PID $processId did not stop within 10 seconds."
    }

    Remove-StaleManifest -Layout $Layout
    return [ordered]@{
        status     = 'stopped'
        pid        = $processId
        port       = [int]$manifest.port
        display_id = [string]$manifest.display_id
        log_path   = [string]$manifest.log_path
    }
}

function Stop-AllManagedInstances {
    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA is unavailable; the Sunshine instance root cannot be resolved.'
    }

    $root = Get-FullPath -Path (Join-Path $env:LOCALAPPDATA 'SBMS\sunshine')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return [ordered]@{
            status       = 'not_running'
            pid          = $null
            port         = $null
            display_id   = $null
            log_path     = $root
            stoppedCount = 0
        }
    }

    $stoppedCount = 0
    $failures = [Collections.Generic.List[string]]::new()
    $directories = @(
        Get-ChildItem `
            -LiteralPath $root `
            -Directory `
            -Filter 'group-*' `
            -ErrorAction Stop
    )
    foreach ($directory in $directories) {
        $id = $directory.Name.Substring('group-'.Length)
        if ($id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
            continue
        }

        $layout = Get-InstanceLayout -Id $id
        if (-not (Test-Path -LiteralPath $layout.Manifest -PathType Leaf)) {
            continue
        }

        try {
            $operation = Stop-ManagedInstance -Layout $layout -Id $id
            if ($operation.status -eq 'stopped') {
                $stoppedCount++
            }
        } catch {
            $failures.Add("$id`: $($_.Exception.Message)")
        }
    }

    if ($failures.Count -gt 0) {
        $details = $failures -join '; '
        throw "Stopped $stoppedCount managed Sunshine instance(s), but some could not be verified or stopped: $details"
    }

    return [ordered]@{
        status       = if ($stoppedCount -gt 0) {
            'stopped_all'
        } else {
            'not_running'
        }
        pid          = $null
        port         = $null
        display_id   = $null
        log_path     = $root
        stoppedCount = $stoppedCount
    }
}

function Test-TcpPortAvailable {
    param(
        [Parameter(Mandatory)]
        [int]$CandidatePort
    )

    $listener = $null
    try {
        $listener = [Net.Sockets.TcpListener]::new(
            [Net.IPAddress]::Any,
            $CandidatePort
        )
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    } catch {
        return $false
    } finally {
        if ($null -ne $listener) {
            $listener.Stop()
        }
    }
}

function Test-UdpPortAvailable {
    param(
        [Parameter(Mandatory)]
        [int]$CandidatePort
    )

    $client = $null
    try {
        $client = [Net.Sockets.UdpClient]::new()
        $client.Client.ExclusiveAddressUse = $true
        $endpoint = [Net.IPEndPoint]::new(
            [Net.IPAddress]::Any,
            $CandidatePort
        )
        $client.Client.Bind($endpoint)
        return $true
    } catch {
        return $false
    } finally {
        if ($null -ne $client) {
            $client.Dispose()
        }
    }
}

function Test-PortFamilyAvailable {
    param(
        [Parameter(Mandatory)]
        [int]$BasePort
    )

    $tcpOffsets = @(-5, 0, 1, 21)
    foreach ($offset in $tcpOffsets) {
        $candidate = $BasePort + $offset
        if (-not (Test-TcpPortAvailable -CandidatePort $candidate)) {
            return $false
        }
    }

    $udpOffsets = @(9, 10, 11)
    foreach ($offset in $udpOffsets) {
        $candidate = $BasePort + $offset
        if (-not (Test-UdpPortAvailable -CandidatePort $candidate)) {
            return $false
        }
    }

    return $true
}

function Find-AvailableSunshinePort {
    param(
        [Parameter(Mandatory)]
        [int]$PreferredPort
    )

    $candidate = [int64]$PreferredPort
    while ($candidate -le 65514) {
        if (Test-PortFamilyAvailable -BasePort ([int]$candidate)) {
            return [int]$candidate
        }
        $candidate += 27
    }

    throw "No complete Sunshine port family is available from base port $PreferredPort in +27 increments."
}

function Start-HiddenSunshine {
    param(
        [Parameter(Mandatory)]
        [string]$SunshineExecutable,

        [Parameter(Mandatory)]
        [string]$Configuration,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $SunshineExecutable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.ErrorDialog = $false

    # Windows PowerShell 5.1 has no ProcessStartInfo.ArgumentList. The managed
    # path cannot contain a quote, so one explicitly quoted argument is exact.
    if ($Configuration.Contains('"')) {
        throw 'The Sunshine configuration path contains an unsupported quote.'
    }
    $startInfo.Arguments = '"' + $Configuration + '"'

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Windows did not return a Sunshine process handle.'
    }
    return $process
}

function Test-TcpEndpoint {
    param(
        [Parameter(Mandatory)]
        [int]$EndpointPort,

        [int]$TimeoutMilliseconds = 250
    )

    $client = [Net.Sockets.TcpClient]::new()
    $asyncResult = $null
    try {
        $asyncResult = $client.BeginConnect(
            [Net.IPAddress]::Loopback,
            $EndpointPort,
            $null,
            $null
        )
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            return $false
        }
        $client.EndConnect($asyncResult)
        return $client.Connected
    } catch {
        return $false
    } finally {
        if ($null -ne $asyncResult) {
            $asyncResult.AsyncWaitHandle.Close()
        }
        $client.Dispose()
    }
}

function Wait-SunshineReady {
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [int]$BasePort,

        [Parameter(Mandatory)]
        [string]$LogPath
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Sunshine exited before its port opened (exit code $($Process.ExitCode)); see $LogPath"
        }

        if (
            (Test-TcpEndpoint -EndpointPort ($BasePort + 1)) -or
            (Test-TcpEndpoint -EndpointPort $BasePort)
        ) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Sunshine did not open TCP port $BasePort or $($BasePort + 1) within 20 seconds; see $LogPath"
}

function Normalize-DisplayId {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'DisplayId is required for Start and Restart.'
    }

    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParseExact($Value, 'B', [ref]$parsed)) {
        throw 'DisplayId must be a brace-enclosed GUID.'
    }
    return $parsed.ToString('B')
}

function Start-ManagedInstance {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Layout,

        [Parameter(Mandatory)]
        [string]$Id,

        [Parameter(Mandatory)]
        [string]$NormalizedDisplayId,

        [Parameter(Mandatory)]
        [int]$BasePort,

        [Parameter(Mandatory)]
        [string]$SunshineExecutable,

        [switch]$AllowAlreadyRunning
    )

    $existing = Read-InstanceManifest -Layout $Layout -Id $Id
    if ($null -ne $existing) {
        $record = Get-ManagedProcessRecord `
            -Manifest $existing `
            -ExpectedConfig $Layout.Config
        if ($null -ne $record) {
            $sameDisplay = [string]$existing.display_id -ieq $NormalizedDisplayId
            $existingPort = [int]$existing.port
            $isRequestedSequence = (
                $existingPort -ge $BasePort -and
                (($existingPort - $BasePort) % 27) -eq 0
            )
            if ($AllowAlreadyRunning -and $sameDisplay -and $isRequestedSequence) {
                return [ordered]@{
                    status     = 'already_running'
                    pid        = [int]$existing.pid
                    port       = [int]$existing.port
                    display_id = [string]$existing.display_id
                    config_path = [string]$existing.config_path
                    log_path   = [string]$existing.log_path
                }
            }
            throw 'A managed Sunshine instance is already running for this group; use Restart.'
        }
        Remove-StaleManifest -Layout $Layout
    }

    $actualPort = Find-AvailableSunshinePort -PreferredPort $BasePort
    New-Item -ItemType Directory -Path $Layout.Credentials -Force | Out-Null
    Ensure-AppsConfiguration `
        -Layout $Layout `
        -SunshineExecutable $SunshineExecutable
    Write-InstanceConfiguration `
        -Layout $Layout `
        -NormalizedDisplayId $NormalizedDisplayId `
        -BasePort $actualPort `
        -Id $Id

    $process = $null
    try {
        $process = Start-HiddenSunshine `
            -SunshineExecutable $SunshineExecutable `
            -Configuration $Layout.Config `
            -WorkingDirectory (Split-Path -Parent $SunshineExecutable)
        Write-InstanceManifest `
            -Layout $Layout `
            -Id $Id `
            -NormalizedDisplayId $NormalizedDisplayId `
            -BasePort $actualPort `
            -ProcessId $process.Id `
            -SunshineExecutable $SunshineExecutable
        Wait-SunshineReady `
            -Process $process `
            -BasePort $actualPort `
            -LogPath $Layout.Log
    } catch {
        if ($null -ne $process) {
            try {
                $process.Refresh()
                if (-not $process.HasExited) {
                    $process.Kill()
                    $null = $process.WaitForExit(5000)
                }
            } catch {
                # The original startup error is more useful than cleanup noise.
            }
        }
        Remove-StaleManifest -Layout $Layout
        throw
    }

    return [ordered]@{
        status      = 'started'
        pid         = $process.Id
        port        = $actualPort
        display_id  = $NormalizedDisplayId
        config_path = Get-FullPath -Path $Layout.Config
        log_path    = Get-FullPath -Path $Layout.Log
    }
}

$layout = $null
$mutex = $null
$lockTaken = $false
$exitCode = 0
$result = $null
$normalizedAction = $Action.ToLowerInvariant()
try {
    $mutex = [Threading.Mutex]::new(
        $false,
        'Local\SBMSSunshineInstanceManager-v1'
    )
    try {
        $lockTaken = $mutex.WaitOne([TimeSpan]::FromSeconds(30))
    } catch [Threading.AbandonedMutexException] {
        $lockTaken = $true
    }
    if (-not $lockTaken) {
        throw 'Timed out waiting for the SBMS Sunshine instance manager lock.'
    }

    if ($Action -eq 'StopAll') {
        $operation = Stop-AllManagedInstances
    } else {
        Assert-GroupId -Id $GroupId
        $layout = Get-InstanceLayout -Id $GroupId

        if ($Action -eq 'Stop') {
            $operation = Stop-ManagedInstance -Layout $layout -Id $GroupId
        } else {
            if ($Port -lt 1029) {
                throw 'Port must be in Sunshine''s supported base-port range 1029-65514.'
            }
            $normalizedDisplayId = Normalize-DisplayId -Value $DisplayId
            $sunshineExecutable = Resolve-SunshineExecutable

            if ($Action -eq 'Restart') {
                $null = Stop-ManagedInstance -Layout $layout -Id $GroupId
            }

            $startParameters = @{
                Layout              = $layout
                Id                  = $GroupId
                NormalizedDisplayId = $normalizedDisplayId
                BasePort            = $Port
                SunshineExecutable  = $sunshineExecutable
                AllowAlreadyRunning = ($Action -eq 'Start')
            }
            $operation = Start-ManagedInstance @startParameters
        }
    }

    $message = switch ($operation.status) {
        'started' {
            "Sunshine instance started on base port $($operation.port)."
        }
        'already_running' {
            "Sunshine instance is already running on base port $($operation.port)."
        }
        'stopped' {
            'Sunshine instance stopped.'
        }
        'stopped_all' {
            "Stopped $($operation.stoppedCount) managed Sunshine instance(s)."
        }
        default {
            'No managed Sunshine instance was running.'
        }
    }

    $configPath = if (
        $null -ne $layout -and
        $operation.Contains('config_path')
    ) {
        $operation.config_path
    } elseif ($null -ne $layout) {
        Get-FullPath -Path $layout.Config
    } else {
        $null
    }
    $result = [ordered]@{
        ok           = $true
        action       = $normalizedAction
        status       = $operation.status
        groupId      = if ($Action -eq 'StopAll') { $null } else { $GroupId }
        displayId    = $operation.display_id
        port         = $operation.port
        pid          = $operation.pid
        message      = $message
        configPath   = $configPath
        logPath      = $operation.log_path
        stoppedCount = if ($operation.Contains('stoppedCount')) {
            $operation.stoppedCount
        } else {
            $null
        }
    }
} catch {
    $exitCode = 1
    $logPath = if ($null -ne $layout) {
        Get-FullPath -Path $layout.Log
    } else {
        $null
    }
    $result = [ordered]@{
        ok         = $false
        action     = $normalizedAction
        groupId    = if ([string]::IsNullOrWhiteSpace($GroupId)) {
            $null
        } else {
            $GroupId
        }
        displayId  = $null
        port       = $null
        pid        = $null
        message    = $_.Exception.Message
        configPath = if ($null -ne $layout) {
            Get-FullPath -Path $layout.Config
        } else {
            $null
        }
        logPath    = $logPath
    }
} finally {
    if ($lockTaken -and $null -ne $mutex) {
        $mutex.ReleaseMutex()
    }
    if ($null -ne $mutex) {
        $mutex.Dispose()
    }
}

Write-JsonResult -Value $result
exit $exitCode
