param(
    [switch] $TryHost
)

$ErrorActionPreference = "Continue"

$Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
$IsAdmin = $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Get-DriverPackages {
    $packages = New-Object System.Collections.Generic.List[object]
    $current = [ordered]@{}

    function Flush-DriverPackage {
        if ($current.Count -eq 0) {
            return
        }
        if ($current.Contains("PublishedName") -or $current.Contains("OriginalName")) {
            $snapshot = [ordered]@{}
            foreach ($key in $current.Keys) {
                $snapshot[$key] = $current[$key]
            }
            $packages.Add([pscustomobject]$snapshot)
        }
        $current.Clear()
    }

    foreach ($line in (pnputil /enum-drivers)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            Flush-DriverPackage
            continue
        }

        if ($line -match '^\s*(.+?)\s*[:\uFF1A]\s*(.*?)\s*$') {
            $field = $Matches[1]
            $value = $Matches[2].Trim()
            if ($field -match '(?i)^Published Name$' -or $value -match '(?i)^oem\d+\.inf$') {
                $current["PublishedName"] = $value
            } elseif ($field -match '(?i)^Original Name$' -or ($value -match '(?i)\.inf$' -and $value -notmatch '(?i)^oem\d+\.inf$' -and -not $current.Contains("OriginalName"))) {
                $current["OriginalName"] = $value
            } elseif ($field -match '(?i)^Provider Name$' -or ($value -match '^<.+>$' -and -not $current.Contains("ProviderName"))) {
                $current["ProviderName"] = $value
            } elseif ($field -match '(?i)^Class Name$' -or ($value -match '^(Display|Monitor|MEDIA|System|SoftwareComponent|Extension|HIDClass|Net)$' -and -not $current.Contains("ClassName"))) {
                $current["ClassName"] = $value
            } elseif ($field -match '(?i)^Driver Version$' -or ($value -match '^\d{1,2}/\d{1,2}/\d{4}\s+\S+$' -and -not $current.Contains("DriverVersion"))) {
                $current["DriverVersion"] = $value
            } elseif ($field -match '(?i)^Signer Name$' -or ($value -match '^(WDKTestCert|Microsoft Windows|SignPath)' -and -not $current.Contains("SignerName"))) {
                $current["SignerName"] = $value
            }
        }
    }

    Flush-DriverPackage
    $packages
}

function Write-DisplayList {
    $Native = Join-Path $PSScriptRoot "SBMSNative.exe"
    if (Test-Path $Native) {
        & $Native --list
    } else {
        Write-Host "missing SBMSNative.exe"
    }
}

function Write-SBMSDevices {
    if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
        Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $_.FriendlyName -like '*IddSample*' -or
            $_.FriendlyName -like '*DisplayBridge*' -or
            $_.FriendlyName -like '*SBMS*' -or
            $_.InstanceId -like '*IddSampleDriver*'
        } | Select-Object Status, Class, FriendlyName, InstanceId, Problem, ConfigManagerErrorCode |
            Format-List
    }

    Get-CimInstance Win32_PnPEntity | Where-Object {
        $_.Name -like '*IddSample*' -or
        $_.Name -like '*DisplayBridge*' -or
        $_.Name -like '*SBMS*' -or
        $_.DeviceID -like '*IddSampleDriver*'
    } | Select-Object Name, Status, PNPDeviceID, ConfigManagerErrorCode |
        Format-List
}

function Write-IddSampleDeviceProperties {
    $instanceId = "SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER"
    if (-not (Get-Command Get-PnpDeviceProperty -ErrorAction SilentlyContinue)) {
        return
    }

    $keys = @(
        "DEVPKEY_Device_HasProblem",
        "DEVPKEY_Device_ProblemCode",
        "DEVPKEY_Device_ProblemStatus",
        "DEVPKEY_Device_DriverInfPath",
        "DEVPKEY_Device_DriverVersion",
        "DEVPKEY_Device_ConfigurationId",
        "DEVPKEY_Device_Service"
    )

    foreach ($key in $keys) {
        try {
            $property = Get-PnpDeviceProperty -InstanceId $instanceId -KeyName $key -ErrorAction Stop
            Write-Host "$($property.KeyName)=$($property.Data)"
        } catch {
        }
    }
}

function Test-VirtualDisplayVisible {
    $Native = Join-Path $PSScriptRoot "SBMSNative.exe"
    if (-not (Test-Path $Native)) {
        return $false
    }
    $text = (& $Native --list 2>&1 | Out-String)
    return ($text -match '(?i)IddSample|DisplayBridge|SBMS')
}

function Get-InfDriverVersion {
    param([string] $InfPath)

    if (-not (Test-Path -LiteralPath $InfPath)) {
        return ""
    }

    $line = Select-String -LiteralPath $InfPath -Pattern '^\s*DriverVer\s*=' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($line -and $line.Line -match '=\s*(.+)$') {
        return $Matches[1].Trim()
    }
    return ""
}

function Write-IddSampleDriverStorePackages {
    $root = Join-Path $env:WINDIR "System32\DriverStore\FileRepository"
    $dirs = Get-ChildItem -LiteralPath $root -Directory -Filter "iddsampledriver.inf_*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending

    foreach ($dir in $dirs) {
        $inf = Join-Path $dir.FullName "IddSampleDriver.inf"
        $dll = Join-Path $dir.FullName "IddSampleDriver.dll"
        $cat = Get-ChildItem -LiteralPath $dir.FullName -Filter "*.cat" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $dllSig = if (Test-Path -LiteralPath $dll) { (Get-AuthenticodeSignature -LiteralPath $dll).Status } else { "Missing" }
        $catSig = if ($cat) { (Get-AuthenticodeSignature -LiteralPath $cat.FullName).Status } else { "Missing" }
        $dllHash = if (Test-Path -LiteralPath $dll) { (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash } else { "" }
        $catHash = if ($cat) { (Get-FileHash -LiteralPath $cat.FullName -Algorithm SHA256).Hash } else { "" }
        $dllLength = if (Test-Path -LiteralPath $dll) { (Get-Item -LiteralPath $dll).Length } else { 0 }

        [pscustomobject]@{
            Directory = $dir.Name
            DriverVer = Get-InfDriverVersion -InfPath $inf
            DllLength = $dllLength
            DllSignature = $dllSig
            CatSignature = $catSig
            DllSHA256 = $dllHash
            CatSHA256 = $catHash
        }
    }
}

function Write-RecentKernelPnpEvents {
    $events = Get-WinEvent -FilterHashtable @{
        ProviderName = "Microsoft-Windows-Kernel-PnP"
        StartTime = (Get-Date).AddHours(-6)
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.Message -match '(?i)IddSampleDriver'
    } | Select-Object -First 12

    foreach ($event in $events) {
        Write-Host ("{0:yyyy-MM-dd HH:mm:ss} EventId={1} Level={2}" -f $event.TimeCreated, $event.Id, $event.LevelDisplayName)
        $summaryLines = New-Object System.Collections.Generic.List[string]
        $messageText = $event.Message.Replace("`r`n", "`n").Replace("`r", "`n")
        foreach ($line in $messageText.Split([char]10)) {
            if ($line -match "(?i)IddSample|oem\d+\.inf|WUDFRd|IndirectKmd|0x[0-9A-F]+|Driver Name|Driver Version|Problem|Problem Status|Service|Configured|Started") {
                $summaryLines.Add($line.Trim())
            }
        }
        $summary = $summaryLines -join "; "
        if ($summary) {
            Write-Host $summary
        }
        Write-Host ""
    }
}

function Write-DriverDiagnosticLogState {
    $logs = @(
        "Microsoft-Windows-DriverFrameworks-UserMode/Operational",
        "Microsoft-Windows-IndirectDisplays-ClassExtension-Events/Diagnostic"
    )

    foreach ($logName in $logs) {
        $log = Get-WinEvent -ListLog $logName -ErrorAction SilentlyContinue
        if ($log) {
            [pscustomobject]@{
                LogName = $log.LogName
                Enabled = $log.IsEnabled
                RecordCount = $log.RecordCount
            }
        } else {
            [pscustomobject]@{
                LogName = $logName
                Enabled = "Missing"
                RecordCount = ""
            }
        }
    }

    Write-Host ""
    Write-Host "To enable deeper driver startup logs, run as Administrator:"
    Write-Host "wevtutil sl `"Microsoft-Windows-DriverFrameworks-UserMode/Operational`" /e:true"
    Write-Host "wevtutil sl `"Microsoft-Windows-IndirectDisplays-ClassExtension-Events/Diagnostic`" /e:true"
}

Write-Host "== SBMS diagnostic =="
Write-Host "admin=$IsAdmin"
Write-Host "cwd=$PSScriptRoot"

Write-Host ""
Write-Host "== processes =="
Get-Process | Where-Object {
    $_.ProcessName -like 'SBMS*' -or
    $_.ProcessName -like 'DisplayBridge*' -or
    $_.ProcessName -like '*native-output*' -or
    $_.ProcessName -like '*IddSample*'
} | Select-Object Id, ProcessName, Path, StartTime | Format-Table -AutoSize

Write-Host ""
Write-Host "== exe files =="
Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.exe |
    Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize

Write-Host ""
Write-Host "== display list =="
Write-DisplayList

Write-Host ""
Write-Host "== installed IddSampleDriver packages =="
$foundDriver = $false
foreach ($package in (Get-DriverPackages)) {
    if ($package.OriginalName -ieq "iddsampledriver.inf") {
        $foundDriver = $true
        Write-Host "PublishedName=$($package.PublishedName)"
        Write-Host "OriginalName=$($package.OriginalName)"
        Write-Host "ProviderName=$($package.ProviderName)"
        Write-Host "ClassName=$($package.ClassName)"
        Write-Host "DriverVersion=$($package.DriverVersion)"
        Write-Host "SignerName=$($package.SignerName)"
        Write-Host ""
    }
}
if (-not $foundDriver) {
    Write-Host "No installed iddsampledriver.inf package found."
}

Write-Host ""
Write-Host "== DriverStore IddSampleDriver payloads =="
Write-IddSampleDriverStorePackages | Format-Table -AutoSize

Write-Host ""
Write-Host "== active IddSample/SBMS PnP devices =="
Write-SBMSDevices

Write-Host ""
Write-Host "== IddSample PnP properties =="
Write-IddSampleDeviceProperties

Write-Host ""
Write-Host "== recent Kernel-PnP IddSample events =="
Write-RecentKernelPnpEvents

Write-Host ""
Write-Host "== driver diagnostic log state =="
Write-DriverDiagnosticLogState | Format-Table -AutoSize

if ($TryHost) {
    Write-Host ""
    Write-Host "== device host probe =="
    $HostExe = Join-Path $PSScriptRoot "SBMSDeviceHost.exe"
    if (-not (Test-Path $HostExe)) {
        Write-Host "missing SBMSDeviceHost.exe"
        exit 2
    }
    $out = Join-Path $env:TEMP "SBMSDeviceHost.diag.out.log"
    $err = Join-Path $env:TEMP "SBMSDeviceHost.diag.err.log"
    Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $HostExe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru

    $visible = $false
    $ready = $false
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        if ($p.HasExited) {
            break
        }

        $hostText = Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue
        if ($hostText -match 'device_host=ready') {
            $ready = $true
        }

        if ($ready -and (Test-VirtualDisplayVisible)) {
            $visible = $true
            break
        }
    }

    Write-Host "host_ready=$ready"
    Write-Host "virtual_display_visible=$visible"
    Write-Host ""
    Write-Host "== display list while host alive =="
    if (-not $p.HasExited) {
        Write-DisplayList
    } else {
        Write-Host "host exited before live display enumeration"
    }

    Write-Host ""
    Write-Host "== PnP devices while host alive =="
    Write-SBMSDevices

    Write-Host ""
    Write-Host "== IddSample PnP properties while host alive =="
    Write-IddSampleDeviceProperties

    if (-not $p.HasExited) {
        Write-Host "host still running; signaling stop"
        try {
            $event = [System.Threading.EventWaitHandle]::OpenExisting("Local\SBMSDeviceHostStop")
            try { [void] $event.Set() } finally { $event.Dispose() }
        } catch {
            Write-Host "stop event not found"
        }
        if (-not $p.WaitForExit(5000)) {
            Stop-Process -Id $p.Id -Force
        }
    }
    try {
        $p.WaitForExit()
        $p.Refresh()
    } catch {
    }
    if ($p.HasExited) {
        if ($null -eq $p.ExitCode) {
            Write-Host "host_exit=<unavailable>"
        } else {
            Write-Host "host_exit=$($p.ExitCode)"
        }
    } else {
        Write-Host "host_exit=<still running>"
    }
    Write-Host ""
    Write-Host "== host stdout/stderr =="
    Get-Content -LiteralPath $out, $err -ErrorAction SilentlyContinue
}
