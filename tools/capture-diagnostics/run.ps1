[CmdletBinding()]
param(
    [string]$ReportRoot,
    [int]$Width = 1314,
    [int]$Height = 2336,
    [int]$Refresh = 200,
    [switch]$Elevated,
    [switch]$InventoryOnly,
    [switch]$Extended
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = $PSScriptRoot
$exe = Join-Path $package 'sbms-capture-diagnostics.exe'
$renderExe = Join-Path $package 'sbms-capture-render-probe.exe'
$utf8 = New-Object Text.UTF8Encoding($false)
if (-not $ReportRoot) {
    $ReportRoot = Join-Path $package ('reports\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$ReportRoot = [IO.Path]::GetFullPath($ReportRoot)
[IO.Directory]::CreateDirectory($ReportRoot) | Out-Null

function Write-Json($Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 50), $utf8)
}

function Read-Json($Path) {
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Select-Fields($Value, [string[]]$Names) {
    if ($null -eq $Value) { return $null }
    $selected = [ordered]@{}
    foreach ($name in $Names) {
        if ($Value.PSObject.Properties[$name]) { $selected[$name] = $Value.$name }
    }
    return [pscustomobject]$selected
}

function Get-Field($Value, [string]$Name, $Default = $null) {
    if ($null -ne $Value -and $Value.PSObject.Properties[$Name] -and $null -ne $Value.$Name) { return $Value.$Name }
    return $Default
}

function Get-ReportPath([string]$Path) {
    return $Path.Substring($ReportRoot.TrimEnd('\').Length + 1).Replace('\', '/')
}

function Write-ProgressLine([string]$Message) {
    $line = (Get-Date -Format 'HH:mm:ss') + ' ' + $Message
    Write-Host $line
    [IO.File]::AppendAllText((Join-Path $ReportRoot 'run.log'), $line + [Environment]::NewLine, $utf8)
}

function Quote-Argument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $quoted = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $quoted = [regex]::Replace($quoted, '(\\+)$', '$1$1')
    return '"' + $quoted + '"'
}

function Start-Child([string]$Program, [string[]]$Arguments, [string]$Prefix, [string]$LocalAppData) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Program
    $psi.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $psi.WorkingDirectory = $package
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($LocalAppData) { $psi.EnvironmentVariables['LOCALAPPDATA'] = $LocalAppData }
    $process = [Diagnostics.Process]::Start($psi)
    $stdout = [IO.File]::Create($Prefix + '.stdout.raw')
    $stderr = [IO.File]::Create($Prefix + '.stderr.raw')
    return [pscustomobject]@{
        Process = $process
        Out = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        Err = $process.StandardError.BaseStream.CopyToAsync($stderr)
        OutFile = $stdout
        ErrFile = $stderr
        Prefix = $Prefix
    }
}

function Export-ChildText([string]$Prefix, [string]$Stream) {
    $bytes = [IO.File]::ReadAllBytes($Prefix + '.' + $Stream + '.raw')
    $encoding = New-Object Text.UTF8Encoding($false, $true)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) {
        $encoding = [Text.Encoding]::Unicode
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255) {
        $encoding = [Text.Encoding]::BigEndianUnicode
    }
    try { $decoded = $encoding.GetString($bytes) }
    catch {
        $encoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
        $decoded = $encoding.GetString($bytes)
    }
    [IO.File]::WriteAllText($Prefix + '.' + $Stream + '.txt', $decoded, $utf8)
    return $encoding.WebName
}

function Finish-Child($Child, [int]$TimeoutSeconds = 20) {
    $timedOut = -not $Child.Process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        $Child.Process.Kill()
        $Child.Process.WaitForExit()
    }
    try {
        $null = $Child.Out.GetAwaiter().GetResult()
        $null = $Child.Err.GetAwaiter().GetResult()
    }
    finally { $Child.OutFile.Dispose(); $Child.ErrFile.Dispose() }
    $outEncoding = Export-ChildText $Child.Prefix 'stdout'
    $errEncoding = Export-ChildText $Child.Prefix 'stderr'
    $result = [pscustomobject]@{
        exit_code = $Child.Process.ExitCode; timed_out = $timedOut
        exit_code_hex = '0x{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$Child.Process.ExitCode), 0)
        stdout_text_encoding = $outEncoding; stderr_text_encoding = $errEncoding
    }
    $Child.Process.Dispose()
    return $result
}

function Invoke-Child([string]$Program, [string[]]$Arguments, [string]$Prefix, [int]$TimeoutSeconds = 20) {
    Finish-Child (Start-Child $Program $Arguments $Prefix) $TimeoutSeconds
}

function Get-DiagnosticDriverPackages {
    @(Get-WindowsDriver -Online -All | Where-Object {
        [IO.Path]::GetFileName($_.OriginalFileName) -ieq 'SBMSCaptureDiagnostics.inf'
    } | Select-Object Driver, OriginalFileName, ProviderName, Version)
}

function Set-ProbePreference([int]$Preference, [string]$Program = $exe) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
    try { $key.SetValue($Program, ('GpuPreference={0};' -f $Preference), [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}

function Get-PreferredAdapters($Inventory, [string]$Name) {
    if ($Inventory.PSObject.Properties['gpu_preferences'] -and $Inventory.gpu_preferences.PSObject.Properties[$Name]) {
        @($Inventory.gpu_preferences.$Name | Where-Object { -not $_.software })
    }
}

function Invoke-Inventory([string]$Name, [switch]$NativeRouting) {
    $path = Join-Path $ReportRoot ($Name + '.json')
    $arguments = @('inventory', '--report', $path)
    if ($NativeRouting) { $arguments += '--native-output-routing' }
    $completion = Invoke-Child $exe $arguments (Join-Path $ReportRoot $Name)
    Write-Json (Join-Path $ReportRoot ($Name + '-process.json')) $completion
    $inventory = if (Test-Path -LiteralPath $path) { Read-Json $path } else { $null }
    $inventories.Add([pscustomobject]@{
        name = $Name; requested_routing = $(if ($NativeRouting) { 'native-output-routing' } else { 'unmodified' })
        process = $completion; result = (Select-Fields $inventory @('status', 'error', 'errors', 'routing'))
        files = @{ result = "$Name.json"; process = "$Name-process.json"; stderr = "$Name.stderr.txt" }
    })
    if ($completion.timed_out -or $completion.exit_code -ne 0 -or $null -eq $inventory) {
        $message = "Inventory failed: $Name (exit $($completion.exit_code_hex), timed_out $($completion.timed_out)); see $Name-process.json and $Name.stderr.txt."
        if ($NativeRouting) { $warnings.Add($message + ' Independent conditions will continue.'); return $null }
        throw $message
    }
    if ($inventory.status -ne 'ok') { $warnings.Add($Name + ': inventory is incomplete; see its errors and calls.') }
    return $inventory
}

function Export-SbmsLogs {
    $directory = Join-Path $ReportRoot 'sbms-logs'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $source = Join-Path $env:LOCALAPPDATA 'SBMS\logs'
    $rows = foreach ($name in @('sbms.log', 'sbms.log.1', 'sbms.log.2')) {
        $path = Join-Path $source $name
        $row = [ordered]@{ source = $path; report = $null; bytes = $null; last_write_time = $null; status = 'missing'; error = $null }
        try {
            if (Test-Path -LiteralPath $path) {
                $file = Get-Item -LiteralPath $path
                $row.bytes = $file.Length
                $row.last_write_time = $file.LastWriteTime.ToString('o')
                if ($file.Length -le 2MB) {
                    Copy-Item -LiteralPath $path -Destination (Join-Path $directory $name)
                    $row.report = 'sbms-logs/' + $name
                    $row.status = 'copied'
                }
                else { $row.status = 'skipped-over-2MiB' }
            }
        }
        catch { $row.status = 'copy-error'; $row.error = $_.Exception.Message }
        [pscustomobject]$row
    }
    Write-Json (Join-Path $directory 'manifest.json') @($rows)
}

if (-not $InventoryOnly) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        if ($Elevated) { throw 'Administrator access is required to create the diagnostic display.' }
        $launchArguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $package 'run.ps1'), '-Elevated', '-ReportRoot', $ReportRoot,
            '-Width', [string]$Width, '-Height', [string]$Height, '-Refresh', [string]$Refresh)
        if ($Extended) { $launchArguments += '-Extended' }
        if ((Get-ExecutionPolicy -Scope Process) -eq 'RemoteSigned') {
            $launchArguments = @('-ExecutionPolicy', 'RemoteSigned') + $launchArguments
        }
        Write-ProgressLine 'Windows will request administrator access. The report will be written here.'
        $elevatedProcess = Start-Process -FilePath (Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList (($launchArguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
        $lastProgress = ''
        while (-not $elevatedProcess.WaitForExit(500)) {
            try {
                $progress = Get-Content -LiteralPath (Join-Path $ReportRoot 'run.log') -Encoding UTF8 -Tail 1
                if ($progress -and $progress -ne $lastProgress) { Write-Host $progress; $lastProgress = $progress }
            }
            catch { } # The elevated writer may briefly hold this log open.
        }
        if (-not (Test-Path -LiteralPath (Join-Path $ReportRoot 'summary.json'))) {
            Write-ProgressLine ('Elevated execution ended before producing a report (exit ' + $elevatedProcess.ExitCode + '). Check the script execution policy and publisher trust.')
        }
        exit $elevatedProcess.ExitCode
    }
}

$cases = New-Object 'Collections.Generic.List[object]'
$renderCases = New-Object 'Collections.Generic.List[object]'
$sessions = New-Object 'Collections.Generic.List[object]'
$inventories = New-Object 'Collections.Generic.List[object]'
$warnings = New-Object 'Collections.Generic.List[string]'
$addedDrivers = @()
$driverBefore = @()
$driverInstallAttempted = $false
$preferenceBackups = @()
$fatal = $null
$cleanupErrors = New-Object 'Collections.Generic.List[string]'
$expectedPower = $null
$expectedPerformance = $null
$runMutex = $null
$ownsMutex = $false
$runStarted = Get-Date

function Invoke-Case($Ready, [string]$Directory, [string]$Name, [int]$Preference,
    [string]$Backend = 'dda', [string]$Window = 'none', [switch]$SourceWindow, [switch]$NativeRouting) {
    Set-ProbePreference $Preference
    $caseDirectory = Join-Path $Directory $Name
    [IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $config = [ordered]@{
        source_device = $Ready.source_device
        source_identity = $Ready.identity
        source_monitor_device_path = $Ready.monitor_device_path
        backend = $Backend
        window = $Window
        target_rect = $(if ($SourceWindow) { $Ready.source_rect } else { $Ready.target_rect })
        duration_ms = 1500
    }
    $configPath = Join-Path $caseDirectory 'config.json'
    $resultPath = Join-Path $caseDirectory 'result.json'
    Write-Json $configPath $config
    Write-ProgressLine $Name
    $arguments = @('probe', '--config', $configPath, '--report', $resultPath)
    if ($NativeRouting) { $arguments += '--native-output-routing' }
    $completion = Invoke-Child $exe $arguments (Join-Path $caseDirectory 'process') 20
    $result = if (Test-Path -LiteralPath $resultPath) { Read-Json $resultPath } else { $null }
    $expected = if ($Preference -eq 1) { $expectedPower } else { $expectedPerformance }
    $matched = $null
    if ($null -ne $result -and $result.PSObject.Properties['device_luid'] -and $null -ne $expected) {
        $matched = $result.device_luid.low -eq $expected.low -and $result.device_luid.high -eq $expected.high
    }
    $entry = [pscustomobject]@{
        session = [IO.Path]::GetFileName($Directory)
        name = $Name
        preference = $Preference
        requested_routing = $(if ($NativeRouting) { 'native-output-routing' } else { 'unmodified' })
        config = $config
        expected_adapter = $expected
        actual_adapter_matches_preference = $matched
        process = $completion
        result = $result
    }
    Write-Json (Join-Path $caseDirectory 'case.json') $entry
    $compactResult = Select-Fields $result @('status', 'error', 'frames', 'desktop_frames', 'pointer_only_frames',
        'subsequent_frames', 'first_acquire', 'first_frame_elapsed_ms', 'first_desktop_frame_elapsed_ms',
        'timeouts', 'empty_polls', 'acquire_attempts', 'elapsed_ms', 'capture_elapsed_ms', 'device_luid',
        'output_parent_luid', 'device_matches_output_parent', 'device_removed_reason', 'routing')
    if ($null -ne $compactResult) {
        $windowResult = Get-Field $result 'window'
        $compactResult | Add-Member -NotePropertyName window -NotePropertyValue (Select-Fields $windowResult @('rect', 'intersects_source', 'paint_metrics'))
    }
    $relative = Get-ReportPath $caseDirectory
    $cases.Add([pscustomobject]@{
        session = $entry.session; name = $Name; preference = $Preference; requested_routing = $entry.requested_routing
        config = $config; expected_adapter = $expected; actual_adapter_matches_preference = $matched
        process = $completion; result = $compactResult
        files = @{ case = "$relative/case.json"; result = "$relative/result.json"; stderr = "$relative/process.stderr.txt" }
    })
}

function Invoke-RenderCase($Ready, [string]$Directory, [string]$Name, [int]$Preference, [switch]$NativeRouting) {
    Set-ProbePreference $Preference $renderExe
    $caseDirectory = Join-Path $Directory $Name
    [IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $target = $Ready.target_rect
    $config = [ordered]@{
        source_rect = $Ready.source_rect
        target_rect = @([Math]::Max($target[0], $target[2] - 320), [Math]::Max($target[1], $target[3] - 240), $target[2], $target[3])
        duration_ms = 3000
    }
    $configPath = Join-Path $caseDirectory 'config.json'
    $resultPath = Join-Path $caseDirectory 'result.json'
    Write-Json $configPath $config
    Write-ProgressLine $Name
    $arguments = @('--config', $configPath, '--report', $resultPath)
    if ($NativeRouting) { $arguments += '--native-output-routing' }
    $child = Start-Child $renderExe $arguments (Join-Path $caseDirectory 'process') (Join-Path $caseDirectory 'local-appdata')
    $completion = Finish-Child $child 20
    $result = if (Test-Path -LiteralPath $resultPath) { Read-Json $resultPath } else { $null }
    $run = Get-Field $result 'run'
    $gpu = Get-Field $run 'gpu'
    $status = Get-Field $result 'status' 'no-result'
    if ($completion.timed_out) { $status = 'process-timeout' }
    elseif ($completion.exit_code -ne 0) { $status = 'process-error' }
    elseif ($null -ne $gpu -and (Get-Field $gpu 'returned_ok') -eq $false) { $status = 'renderer-error' }
    elseif ($null -ne $gpu -and (Get-Field $gpu 'presented' 0) -eq 0) { $status = 'no-present' }
    $relative = Get-ReportPath $caseDirectory
    $entry = [pscustomobject]@{
        session = [IO.Path]::GetFileName($Directory); name = $Name; preference = $Preference
        requested_routing = $(if ($NativeRouting) { 'native-output-routing' } else { 'unmodified' })
        config = $config; process = $completion
        result = [pscustomobject]@{
            status = $status; harness_status = (Get-Field $result 'status')
            returned_ok = (Get-Field $gpu 'returned_ok'); presented = (Get-Field $gpu 'presented')
            first_present_ms = (Get-Field $gpu 'first_present_ms'); error = (Get-Field $gpu 'error' (Get-Field $result 'error'))
            routing = (Get-Field $result 'routing'); gpu = $gpu
            worker_joined = (Get-Field $run 'worker_joined'); cleanup_errors = (Get-Field $run 'cleanup_errors')
            source_stimulus = (Get-Field $run 'source_stimulus')
        }
        files = @{ case = "$relative/case.json"; result = "$relative/result.json"; stderr = "$relative/process.stderr.txt";
            diagnostics = "$relative/local-appdata/SBMS/logs/" }
    }
    Write-Json (Join-Path $caseDirectory 'case.json') $entry
    $renderCases.Add($entry)
}

function Invoke-Session([string]$Name, [string]$Kind, [int]$Hz, $RenderAdapter, [string]$Plan) {
    $directory = Join-Path $ReportRoot $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $config = [ordered]@{
        kind = $Kind; width = $Width; height = $Height; refresh = $Hz
        timeout_seconds = 35 + $(if ($Plan -eq 'full') { 14 * 20 + 4 * 20 } else { 5 * 20 })
    }
    if ($null -ne $RenderAdapter) { $config.render_adapter = $RenderAdapter }
    $configPath = Join-Path $directory 'config.json'
    Write-Json $configPath $config
    Set-ProbePreference 1
    Write-ProgressLine ("Session {0}: {1}x{2}@{3}" -f $Name, $Width, $Height, $Hz)
    $hostChild = Start-Child $exe @('session', '--config', $configPath, '--report-dir', $directory) (Join-Path $directory 'host')
    $sessionError = $null
    $ready = $null
    try {
        $readyPath = Join-Path $directory 'ready.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(35)
        while (-not (Test-Path -LiteralPath $readyPath) -and -not $hostChild.Process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $readyPath)) { throw 'Session did not publish ready.json.' }
        $ready = Read-Json $readyPath
        if ($ready.status -ne 'ready') { throw ('Session setup failed; see ' + $Name + '/ready.json: ' + (Get-Field $ready 'error' $ready.status)) }
        Invoke-Case $ready $directory '01-power-dda-bare' 1
        Invoke-Case $ready $directory '01b-power-native-dda-bare' 1 -NativeRouting
        if ($Plan -eq 'full') {
            Invoke-Case $ready $directory '02-power-dda-window' 1 'dda' 'shown'
            Invoke-Case $ready $directory '04-power-wgc' 1 'wgc'
            Invoke-Case $ready $directory '04b-power-dda-source-window' 1 'dda' 'shown' -SourceWindow
            Invoke-Case $ready $directory '04bn-power-native-dda-source-window' 1 'dda' 'shown' -SourceWindow -NativeRouting
            Invoke-Case $ready $directory '04c-power-wgc-source-window' 1 'wgc' 'shown' -SourceWindow
        }
        Invoke-Case $ready $directory '05-performance-dda-bare' 2
        Invoke-Case $ready $directory '05b-performance-native-dda-bare' 2 -NativeRouting
        if ($Plan -eq 'full') {
            Invoke-Case $ready $directory '05c-performance-dda-source-window' 2 'dda' 'shown' -SourceWindow
            Invoke-Case $ready $directory '05cn-performance-native-dda-source-window' 2 'dda' 'shown' -SourceWindow -NativeRouting
            Invoke-Case $ready $directory '05d-performance-wgc' 2 'wgc'
            Invoke-Case $ready $directory '05e-performance-wgc-source-window' 2 'wgc' 'shown' -SourceWindow
        }
        Invoke-Case $ready $directory '06-power-dda-return' 1
        if ($Plan -eq 'full') {
            Invoke-RenderCase $ready $directory '07-power-renderer' 1
            Invoke-RenderCase $ready $directory '07b-power-native-renderer' 1 -NativeRouting
            Invoke-RenderCase $ready $directory '08-performance-renderer' 2
            Invoke-RenderCase $ready $directory '08b-performance-native-renderer' 2 -NativeRouting
        }
    }
    catch {
        $sessionError = $_.Exception.Message
        $warnings.Add($Name + ': ' + $sessionError)
        Write-ProgressLine ($Name + ': ' + $sessionError)
    }
    finally {
        [IO.File]::WriteAllText((Join-Path $directory 'stop'), '', $utf8)
        $completion = Finish-Child $hostChild 25
        if ($completion.timed_out) {
            $cleanupErrors.Add($Name + ': host timed out; device removal and driver detachment remain unverified.')
            $warnings.Add($Name + ': session timeout; executing saved topology restoration.')
            $snapshot = Join-Path $directory 'topology-before.json'
            if (Test-Path -LiteralPath $snapshot) {
                $restore = Invoke-Child $exe @('restore', '--snapshot', $snapshot, '--report', (Join-Path $directory 'emergency-restore.json')) (Join-Path $directory 'restore') 20
                if ($restore.timed_out -or $restore.exit_code -ne 0) { $cleanupErrors.Add($Name + ': topology restoration failed.') }
            }
            else { $cleanupErrors.Add($Name + ': missing topology snapshot after host timeout.') }
        }
        $sessionResult = if (Test-Path -LiteralPath (Join-Path $directory 'session.json')) { Read-Json (Join-Path $directory 'session.json') } else { $null }
        $sessions.Add([pscustomobject]@{
            name = $Name; config = $config; error = $sessionError; process = $completion
            ready = (Select-Fields $ready @('status', 'error', 'source_device', 'identity', 'adapter_luid', 'source_rect',
                'target_rect', 'driver_ready', 'has_first_driver_frame', 'elapsed_ms'))
            result = (Select-Fields $sessionResult @('status', 'elapsed_ms', 'cleanup_ok', 'device_removed', 'creation_without_instance', 'driver_detached', 'topology_restored'))
            files = @{ session = "$Name/session.json"; ready = "$Name/ready.json"; driver = "$Name/driverstate.jsonl";
                events = "$Name/session-events.jsonl"; stderr = "$Name/host.stderr.txt" }
        })
        if (-not $completion.timed_out -and ($null -eq $sessionResult -or -not $sessionResult.cleanup_ok)) {
            $cleanupErrors.Add($Name + ': host did not confirm complete display cleanup; further experiments stopped.')
            throw $cleanupErrors[$cleanupErrors.Count - 1]
        }
        if ($completion.timed_out) {
            throw ($Name + ': host exceeded cleanup deadline; further experiments stopped. See emergency restoration result.')
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $exe)) { throw "Missing diagnostic program: $exe" }
    if (-not $InventoryOnly -and -not (Test-Path -LiteralPath $renderExe)) { throw "Missing renderer probe: $renderExe" }
    if (-not $InventoryOnly) {
        $runMutex = New-Object Threading.Mutex($false, 'Local\SBMSCaptureDiagnosticRun')
        try { $ownsMutex = $runMutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { throw 'Another capture diagnostic run is active.' }
    }
    Write-ProgressLine 'Collecting system information.'
    $system = [ordered]@{
        collected_at = [DateTimeOffset]::Now.ToString('o')
        os = @(Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture)
        video = @(Get-CimInstance Win32_VideoController | Select-Object Name, PNPDeviceID, DriverVersion, AdapterCompatibility, VideoModeDescription, Status)
        display_drivers = @(Get-CimInstance Win32_PnPSignedDriver -Filter "DeviceClass='DISPLAY'" | Select-Object DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned)
        graphics_runtime = @(foreach ($name in @('win32u.dll', 'dxgi.dll', 'd3d11.dll')) {
            $runtimeFile = Get-Item -LiteralPath (Join-Path $env:windir ('System32\' + $name)) -ErrorAction SilentlyContinue
            [pscustomobject]@{ name = $name; file_version = $(if ($null -ne $runtimeFile) { $runtimeFile.VersionInfo.FileVersion } else { $null }) }
        })
        executable = $exe
        signature = [string](Get-AuthenticodeSignature -LiteralPath $exe).Status
        render_executable = $renderExe
        render_signature = $(if (Test-Path -LiteralPath $renderExe) { [string](Get-AuthenticodeSignature -LiteralPath $renderExe).Status } else { $null })
    }
    Write-Json (Join-Path $ReportRoot 'system.json') $system
    if ($InventoryOnly) {
        $null = Invoke-Inventory 'inventory-current'
        Write-ProgressLine 'Read-only inventory complete.'
    }
    else {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
        try {
            foreach ($program in @($exe, $renderExe)) {
                $exists = $key.GetValueNames() -contains $program
                $backup = [ordered]@{ path = $program; existed = $exists; value = $null; kind = $null }
                if ($exists) {
                    $backup.value = $key.GetValue($program, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    $backup.kind = [string]$key.GetValueKind($program)
                }
                $preferenceBackups += $backup
            }
        }
        finally { $key.Dispose() }
        Write-Json (Join-Path $ReportRoot 'gpu-preference-before.json') $preferenceBackups[0]
        Write-Json (Join-Path $ReportRoot 'render-gpu-preference-before.json') $preferenceBackups[1]
        Set-ProbePreference 1
        $powerInventory = Invoke-Inventory 'inventory-power'
        $null = Invoke-Inventory 'inventory-power-native' -NativeRouting
        Set-ProbePreference 2
        $performanceInventory = Invoke-Inventory 'inventory-performance'
        $null = Invoke-Inventory 'inventory-performance-native' -NativeRouting
        $powerAdapters = @(Get-PreferredAdapters $powerInventory 'minimum_power')
        $performanceAdapters = @(Get-PreferredAdapters $performanceInventory 'high_performance')
        if ($powerAdapters.Count -gt 0) { $expectedPower = $powerAdapters[0].luid }
        if ($performanceAdapters.Count -gt 0) { $expectedPerformance = $performanceAdapters[0].luid }
        if ($powerInventory.PSObject.Properties['display_paths'] -and $powerInventory.display_paths.Count -gt 0) {
            $desktop = @($powerInventory.display_paths | Where-Object { $_.rect[0] -eq 0 -and $_.rect[1] -eq 0 } | Select-Object -First 1)
            if ($desktop.Count -eq 0) { $desktop = @($powerInventory.display_paths[0]) }
            $screen = $desktop[0]
            $control = [pscustomobject]@{
                source_device = $screen.source_device
                identity = @($screen.adapter_luid.low, $screen.adapter_luid.high, $screen.target_id)
                monitor_device_path = $screen.monitor_device_path
                source_rect = $screen.rect
                target_rect = @([Math]::Max($screen.rect[0], $screen.rect[2] - 320), [Math]::Max($screen.rect[1], $screen.rect[3] - 240), $screen.rect[2], $screen.rect[3])
            }
            $controlDirectory = Join-Path $ReportRoot '00-existing-desktop'
            Invoke-Case $control $controlDirectory '01-power-dda-control' 1 'dda' 'shown'
            Invoke-Case $control $controlDirectory '01b-power-native-dda-control' 1 'dda' 'shown' -NativeRouting
            Invoke-Case $control $controlDirectory '02-performance-dda-control' 2 'dda' 'shown'
            Invoke-Case $control $controlDirectory '02b-performance-native-dda-control' 2 'dda' 'shown' -NativeRouting
            Invoke-Case $control $controlDirectory '03-power-wgc-control' 1 'wgc' 'shown'
            Invoke-Case $control $controlDirectory '04-performance-wgc-control' 2 'wgc' 'shown'
        }
        else { $warnings.Add('Existing desktop identity could not be read; physical-desktop controls were skipped.') }
        Invoke-Session '01-original-driver' 'baseline' $Refresh $null 'full'
        if ($Extended -and $Refresh -ne 60) { Invoke-Session '02-original-driver-60hz' 'baseline' 60 $null 'pair' }

        $driverInf = Join-Path $package 'driver\SBMSCaptureDiagnostics.inf'
        $driverCat = Join-Path $package 'driver\SBMSCaptureDiagnostics.cat'
        if (-not (Test-Path -LiteralPath $driverInf) -or (Get-AuthenticodeSignature -LiteralPath $driverCat).Status -ne 'Valid') {
            $warnings.Add('Diagnostic driver missing or its signature is not trusted. Driver experiments were skipped.')
        }
        else {
            $driverBefore = @(Get-DiagnosticDriverPackages)
            Write-Json (Join-Path $ReportRoot 'diagnostic-drivers-before.json') $driverBefore
            try {
                $driverInstallAttempted = $true
                $install = Invoke-Child (Join-Path $env:windir 'System32\pnputil.exe') @('/add-driver', $driverInf, '/install') (Join-Path $ReportRoot 'driver-install') 60
                $driverAfter = @(Get-DiagnosticDriverPackages)
                $previousNames = @($driverBefore | ForEach-Object { $_.Driver })
                $addedDrivers = @($driverAfter | Where-Object { $previousNames -notcontains $_.Driver })
                Write-Json (Join-Path $ReportRoot 'diagnostic-drivers-added.json') $addedDrivers
                if ($install.timed_out -or $install.exit_code -ne 0) { throw ('Diagnostic driver installation requires attention: ' + ($install | ConvertTo-Json -Compress)) }
                Invoke-Session '03-diagnostic-default' 'diagnostic' $Refresh $null 'full'
                if ($Extended -and $powerAdapters.Count -gt 0 -and $performanceAdapters.Count -gt 0) {
                    $low = $powerAdapters[0].luid
                    $high = $performanceAdapters[0].luid
                    if ($low.low -ne $high.low -or $low.high -ne $high.high) {
                        Invoke-Session '04-render-performance' 'diagnostic' $Refresh $high 'pair'
                        Invoke-Session '05-render-power' 'diagnostic' $Refresh $low 'full'
                        Invoke-Session '06-render-performance-return' 'diagnostic' $Refresh $high 'pair'
                    }
                    else { $warnings.Add('GPU preference lists selected the same adapter; the two-GPU reversal was not performed.') }
                }
                elseif ($Extended) { $warnings.Add('GPU preference enumeration was unavailable; the two-GPU reversal was not performed.') }
            }
            catch { $warnings.Add($_.Exception.Message) }
        }
    }
}
catch {
    $fatal = $_.Exception.ToString()
    Write-ProgressLine $fatal
}
finally {
    foreach ($preferenceBackup in $preferenceBackups) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
            try {
                if ($preferenceBackup.existed) {
                    $kind = [Enum]::Parse([Microsoft.Win32.RegistryValueKind], $preferenceBackup.kind)
                    $key.SetValue($preferenceBackup.path, $preferenceBackup.value, $kind)
                }
                else { $key.DeleteValue($preferenceBackup.path, $false) }
            }
            finally { $key.Dispose() }
        }
        catch { $cleanupErrors.Add('GPU preference restoration for ' + $preferenceBackup.path + ': ' + $_.Exception.Message) }
    }
    if ($driverInstallAttempted) {
        try {
            $previousNames = @($driverBefore | ForEach-Object { $_.Driver })
            $addedDrivers = @(Get-DiagnosticDriverPackages | Where-Object { $previousNames -notcontains $_.Driver })
            Write-Json (Join-Path $ReportRoot 'diagnostic-drivers-added.json') $addedDrivers
        }
        catch { $cleanupErrors.Add('Could not enumerate diagnostic driver packages for cleanup: ' + $_.Exception.Message) }
    }
    foreach ($driverPackage in $addedDrivers) {
        try {
            if ($driverPackage.Driver -notmatch '^oem\d+\.inf$' -or [IO.Path]::GetFileName($driverPackage.OriginalFileName) -ine 'SBMSCaptureDiagnostics.inf') {
                throw 'Unexpected diagnostic driver identity; automatic removal refused.'
            }
            $remove = Invoke-Child (Join-Path $env:windir 'System32\pnputil.exe') @('/delete-driver', $driverPackage.Driver, '/uninstall') (Join-Path $ReportRoot ('driver-remove-' + $driverPackage.Driver)) 60
            if ($remove.timed_out -or $remove.exit_code -ne 0) { throw ('Driver removal incomplete: ' + ($remove | ConvertTo-Json -Compress)) }
        }
        catch { $cleanupErrors.Add($_.Exception.Message) }
    }
    if (-not $InventoryOnly) {
        try { Export-SbmsLogs }
        catch { $warnings.Add('SBMS log export: ' + $_.Exception.Message) }
        $eventReports = foreach ($logName in @('System', 'Application', 'Microsoft-Windows-DriverFrameworks-UserMode/Operational')) {
            try {
                $logInfo = Get-WinEvent -ListLog $logName -ErrorAction Stop
                $eventRows = @(Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = $runStarted.AddMinutes(-2) } -MaxEvents 1000 -ErrorAction SilentlyContinue |
                    Where-Object { $_.ProviderName -match 'Display|Dxg|DriverFrameworks|Kernel-PnP|Application Error|Windows Error Reporting' } |
                    Select-Object -First 200 TimeCreated, ProviderName, Id, LevelDisplayName, Message)
                [pscustomobject]@{ log = $logName; enabled = $logInfo.IsEnabled; since = $runStarted.AddMinutes(-2).ToString('o'); events = $eventRows; error = $null }
            }
            catch { [pscustomobject]@{ log = $logName; enabled = $null; events = @(); error = $_.Exception.Message } }
        }
        try { Write-Json (Join-Path $ReportRoot 'windows-events.json') @($eventReports) }
        catch { $warnings.Add('Windows event export: ' + $_.Exception.Message) }
        try {
            $priorEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application';
                    ProviderName = @('Application Error', 'Windows Error Reporting');
                    StartTime = $runStarted.AddHours(-24); EndTime = $runStarted } -MaxEvents 300 -ErrorAction SilentlyContinue -ErrorVariable priorEventErrors |
                Where-Object { $_.Message -match '(?i)\bsbms(?:-tray|-capture-diagnostics|-capture-render-probe)?\.exe\b' } |
                Select-Object -First 50 TimeCreated, ProviderName, Id, LevelDisplayName, Message)
            Write-Json (Join-Path $ReportRoot 'prior-sbms-crashes.json') ([ordered]@{
                scope = 'Historical events before this diagnostic run; not caused by or proof of this run.'
                from = $runStarted.AddHours(-24).ToString('o'); until = $runStarted.ToString('o')
                examined_event_limit = 300; retained_event_limit = 50; events = $priorEvents
                query_errors = @($priorEventErrors | Where-Object { $_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*' } |
                    Select-Object -First 3 | ForEach-Object { $_.Exception.Message })
            })
        }
        catch { $warnings.Add('Prior SBMS crash event export: ' + $_.Exception.Message) }
    }
    if ($ownsMutex) { $runMutex.ReleaseMutex() }
    if ($null -ne $runMutex) { $runMutex.Dispose() }
    $summary = [ordered]@{
        schema = 3; extended = [bool]$Extended; started_at = $runStarted.ToString('o'); finished_at = [DateTimeOffset]::Now.ToString('o'); fatal_error = $fatal
        warnings = @($warnings.ToArray()); cleanup_errors = @($cleanupErrors.ToArray())
        inventories = @($inventories.ToArray()); sessions = @($sessions.ToArray()); cases = @($cases.ToArray())
        render_cases = @($renderCases.ToArray())
        files = @('system.json', 'inventory*.json', 'windows-events.json', 'prior-sbms-crashes.json', 'sbms-logs/manifest.json')
        interpretation = 'Requested GPU preferences are not proof of actual GPU use. Native routing deliberately changes the process routing query; check its observed mode and hook count. Compare actual device/output/driver LUIDs and assignment events in raw reports. DDA desktop_frames exclude pointer-only notifications; missing fields are unknown. TIMEOUT is inconclusive. Process exit status and returned API HRESULT are separate evidence. Renderer cases execute production DDA/shaders/Present with automatic sampling; exit 0 is not renderer success. Present counts do not establish full SBMS GUI/input behavior.'
    }
    Write-Json (Join-Path $ReportRoot 'summary.json') $summary
    $lines = New-Object 'Collections.Generic.List[string]'
    $lines.Add('# SBMS capture diagnostic report')
    $lines.Add('')
    $lines.Add('summary.json is compact; full calls, driver telemetry and raw process streams remain in each case/session directory. Renderer cases separately exercise production DDA/shaders/Present; GUI/input behavior is outside this probe.')
    $lines.Add('')
    $lines.Add('DDA desktop counts mean LastPresentTime > 0; pointer-only counts do not show a desktop update. n/a means unavailable (including WGC), not zero. Native routing may intentionally differ from the registry GPU preference; check the actual LUIDs and hook query count.')
    $lines.Add('')
    $lines.Add('| Session | Test | GPU preference / matched | Routing / enabled / queries | Result | Frames / desktop / pointer-only | First desktop ms | HRESULT / stage | Process |')
    $lines.Add('|---|---|---|---|---|---|---:|---|---|')
    foreach ($case in $cases) {
        $status = Get-Field $case.result 'status' 'no-result'
        $frames = Get-Field $case.result 'frames' 'n/a'
        $desktop = Get-Field $case.result 'desktop_frames' 'n/a'
        $pointer = Get-Field $case.result 'pointer_only_frames' 'n/a'
        $firstDesktop = Get-Field $case.result 'first_desktop_frame_elapsed_ms' 'n/a'
        $errorResult = Get-Field $case.result 'error'
        $hr = Get-Field $errorResult 'hr' ''
        $stage = Get-Field $errorResult 'stage' ''
        $routing = Get-Field $case.result 'routing'
        $mode = Get-Field $routing 'mode' ($case.requested_routing + ' (unconfirmed)')
        $enabled = Get-Field $routing 'hook_enabled' 'n/a'
        $queries = Get-Field $routing 'hook_queries' 'n/a'
        $processStatus = 'exit {0} ({1})' -f $case.process.exit_code, $case.process.exit_code_hex
        if ($case.process.timed_out) { $processStatus = 'timeout; runner terminated process; ' + $processStatus }
        $lines.Add(('| {0} | [{1}]({2}) | {3} / {4} | {5} / {6} / {7} | {8} | {9} / {10} / {11} | {12} | {13} / {14} | {15} |' -f
            $case.session, $case.name, $case.files.case, $case.preference, $case.actual_adapter_matches_preference,
            $mode, $enabled, $queries, $status, $frames, $desktop, $pointer, $firstDesktop, $hr, $stage, $processStatus))
    }
    $lines.Add('')
    $lines.Add('| Session | Renderer test | GPU preference | Routing / enabled / queries | Result | returned_ok | Presented | First Present ms | Error | Process |')
    $lines.Add('|---|---|---|---|---|---|---:|---:|---|---|')
    foreach ($case in $renderCases) {
        $routing = Get-Field $case.result 'routing'
        $mode = Get-Field $routing 'mode' ($case.requested_routing + ' (unconfirmed)')
        $enabled = Get-Field $routing 'hook_enabled' 'n/a'
        $queries = Get-Field $routing 'hook_queries' 'n/a'
        $processStatus = 'exit {0} ({1})' -f $case.process.exit_code, $case.process.exit_code_hex
        if ($case.process.timed_out) { $processStatus = 'timeout; runner terminated process; ' + $processStatus }
        $errorText = [string](Get-Field $case.result 'error' '')
        $errorText = $errorText.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
        $lines.Add(('| {0} | [{1}]({2}) | {3} | {4} / {5} / {6} | {7} | {8} | {9} | {10} | {11} | {12} |' -f
            $case.session, $case.name, $case.files.case, $case.preference, $mode, $enabled, $queries,
            $case.result.status, (Get-Field $case.result 'returned_ok' 'n/a'), (Get-Field $case.result 'presented' 'n/a'),
            (Get-Field $case.result 'first_present_ms' 'n/a'), $errorText, $processStatus))
    }
    $lines.Add('')
    $lines.Add('Renderer process exit 0 with returned_ok=false is renderer-error. returned_ok=true with no Present is no-present. Missing metrics mean unknown. Per-case production logs are under local-appdata/SBMS/logs; source stimulus and cleanup observations remain in result.json.')
    $lines.Add('')
    $lines.Add('A nonzero process exit is not an API HRESULT or automatic proof of a crash. A missing result with an exception-like exit code needs stderr and Application Error/WER correlation. Prior failures (24 hours before run start, bounded to 50 matching events) are in prior-sbms-crashes.json; available SBMS rolling logs are in sbms-logs with copy status and timestamps. No SBMS process was launched and no global crash dump setting was changed.')
    $lines.Add('')
    $lines.Add('| Session | Setup | Cleanup verified | Device removed | Driver detached | Topology restored | Process exit |')
    $lines.Add('|---|---|---|---|---|---|---|')
    foreach ($session in $sessions) {
        $lines.Add(('| [{0}]({1}) | {2} | {3} | {4} | {5} | {6} | {7} |' -f $session.name, $session.files.session,
            (Get-Field $session.ready 'status' 'no-ready'), (Get-Field $session.result 'cleanup_ok' 'n/a'),
            (Get-Field $session.result 'device_removed' 'n/a'), (Get-Field $session.result 'driver_detached' 'n/a'),
            (Get-Field $session.result 'topology_restored' 'n/a'), $session.process.exit_code_hex))
    }
    $lines.Add('')
    foreach ($item in @($fatal) + @($warnings.ToArray()) + @($cleanupErrors.ToArray())) {
        if ($item) { $lines.Add('- ' + $item) }
    }
    [IO.File]::WriteAllLines((Join-Path $ReportRoot 'report.md'), $lines.ToArray(), $utf8)
    try {
        Compress-Archive -LiteralPath $ReportRoot -DestinationPath ($ReportRoot + '.zip') -Force
        Write-ProgressLine ('Report: ' + $ReportRoot + '.zip')
    }
    catch { Write-ProgressLine ('Could not zip report; send the directory: ' + $ReportRoot) }
}
if ($fatal -or $cleanupErrors.Count -gt 0) { exit 1 }
exit 0
