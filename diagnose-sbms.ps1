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
        } | Select-Object Status, Class, FriendlyName, InstanceId |
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

function Test-VirtualDisplayVisible {
    $Native = Join-Path $PSScriptRoot "SBMSNative.exe"
    if (-not (Test-Path $Native)) {
        return $false
    }
    $text = (& $Native --list 2>&1 | Out-String)
    return ($text -match '(?i)IddSample|DisplayBridge|SBMS')
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
Write-Host "== active IddSample/SBMS PnP devices =="
Write-SBMSDevices

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
