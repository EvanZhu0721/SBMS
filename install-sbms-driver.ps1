param(
    [switch] $Force,
    [switch] $KeepOld
)

$ErrorActionPreference = "Stop"

$Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
$IsAdmin = $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $IsAdmin) {
    throw "Run this script from an elevated PowerShell."
}

if (-not $Force) {
    Write-Host "This installs a test display driver package."
    Write-Host "Re-run with -Force after you have enabled test signing or prepared a valid signature."
    exit 10
}

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
            } elseif ($field -match '(?i)^Provider Name$') {
                $current["ProviderName"] = $value
            } elseif ($field -match '(?i)^Class Name$') {
                $current["ClassName"] = $value
            } elseif ($field -match '(?i)^Driver Version$') {
                $current["DriverVersion"] = $value
            }
        }
    }

    Flush-DriverPackage
    $packages
}

function Get-IddSamplePublishedNames {
    Get-DriverPackages |
        Where-Object { $_.OriginalName -ieq "iddsampledriver.inf" } |
        Select-Object -ExpandProperty PublishedName -Unique
}

if (-not $KeepOld) {
    Write-Host "Stopping any SBMS software device host..."
    try {
        $event = [System.Threading.EventWaitHandle]::OpenExisting("Local\SBMSDeviceHostStop")
        try {
            [void] $event.Set()
        } finally {
            $event.Dispose()
        }
    } catch {
    }

    Start-Sleep -Milliseconds 500
    Get-Process -Name SBMS, SBMSDeviceHost, SBMSNative, DisplayBridgeGui, DisplayBridgeDeviceHost, native-output-demo-input -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $oldPackages = @(Get-IddSamplePublishedNames)
    foreach ($publishedName in $oldPackages) {
        Write-Host "Removing old IddSampleDriver package: $publishedName"
        pnputil /delete-driver $publishedName /uninstall /force
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to remove old driver package: $publishedName"
        }
    }
}

$DriverSearchRoots = @(
    (Join-Path $PSScriptRoot "driver"),
    (Join-Path $PSScriptRoot "Windows-driver-samples\video\IndirectDisplay"),
    $PSScriptRoot
) | Where-Object { Test-Path $_ }

$Inf = $DriverSearchRoots |
    ForEach-Object { Get-ChildItem $_ -Recurse -Filter "IddSampleDriver.inf" -ErrorAction SilentlyContinue } |
    Where-Object {
        $Dir = $_.DirectoryName
        (Test-Path (Join-Path $Dir "IddSampleDriver.dll")) -and
        (Test-Path (Join-Path $Dir "IddSampleDriver.cat"))
    } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $Inf) {
    throw "Built driver package not found. Run .\build-sbms-driver.ps1 first."
}

Write-Host "Installing: $($Inf.FullName)"
pnputil /add-driver $Inf.FullName /install

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

pnputil /scan-devices | Out-Host
Write-Host "Driver package installed."
