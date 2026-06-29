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

function Assert-DriverPayload {
    param(
        [System.IO.FileInfo] $Inf
    )

    $Dir = $Inf.DirectoryName
    $Dll = Join-Path $Dir "IddSampleDriver.dll"
    $Cat = Get-ChildItem -LiteralPath $Dir -Filter "*.cat" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not (Test-Path -LiteralPath $Dll)) {
        throw "Driver DLL not found next to INF: $Dll"
    }
    if (-not $Cat) {
        throw "Driver catalog not found next to INF: $Dir"
    }

    $catSignature = Get-AuthenticodeSignature -LiteralPath $Cat.FullName
    if ($catSignature.Status -ne "Valid") {
        throw "Refusing to install invalid driver catalog: $($Cat.FullName) signature=$($catSignature.Status) $($catSignature.StatusMessage)"
    }

    $dllSignature = Get-AuthenticodeSignature -LiteralPath $Dll
    if ($dllSignature.Status -ne "Valid") {
        Write-Warning "Driver DLL embedded signature is $($dllSignature.Status); continuing because the driver package catalog is valid."
    }

    $DllHash = (Get-FileHash -LiteralPath $Dll -Algorithm SHA256).Hash
    $CatHash = (Get-FileHash -LiteralPath $Cat.FullName -Algorithm SHA256).Hash
    Write-Host "Driver payload DLL: $Dll"
    Write-Host "Driver payload DLL SHA256=$DllHash"
    Write-Host "Driver payload DLL signature=$($dllSignature.Status)"
    Write-Host "Driver payload CAT: $($Cat.FullName)"
    Write-Host "Driver payload CAT SHA256=$CatHash"
    Write-Host "Driver payload CAT signature=$($catSignature.Status)"
}

function Get-InfDriverVersionValue {
    param(
        [string] $InfPath
    )

    $line = Select-String -LiteralPath $InfPath -Pattern '^\s*DriverVer\s*=' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($line -and $line.Line -match '^\s*DriverVer\s*=\s*[^,]+,\s*(.+)\s*$') {
        return $Matches[1].Trim()
    }
    return ""
}

function Convert-DriverPackageVersionValue {
    param(
        [string] $DriverVersion
    )

    if ($DriverVersion -match '^\s*\S+\s+(.+?)\s*$') {
        return $Matches[1].Trim()
    }
    return $DriverVersion
}

function Get-DriverPackageVersionByPublishedName {
    param(
        [string] $PublishedName
    )

    if ([string]::IsNullOrWhiteSpace($PublishedName)) {
        return ""
    }

    $package = Get-DriverPackages |
        Where-Object { $_.PublishedName -ieq $PublishedName -and $_.OriginalName -ieq "iddsampledriver.inf" } |
        Select-Object -First 1

    if (-not $package) {
        return ""
    }

    return Convert-DriverPackageVersionValue -DriverVersion $package.DriverVersion
}

function Get-InstalledInfVersionByPublishedName {
    param(
        [string] $PublishedName
    )

    if ([string]::IsNullOrWhiteSpace($PublishedName)) {
        return ""
    }

    $infPath = Join-Path (Join-Path $env:WINDIR "INF") $PublishedName
    if (-not (Test-Path -LiteralPath $infPath)) {
        return ""
    }

    return Get-InfDriverVersionValue -InfPath $infPath
}

function Get-IddSampleDeviceProperty {
    param(
        [string] $KeyName
    )

    if (-not (Get-Command Get-PnpDeviceProperty -ErrorAction SilentlyContinue)) {
        return $null
    }

    try {
        $property = Get-PnpDeviceProperty -InstanceId "SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER" -KeyName $KeyName -ErrorAction Stop
        return $property.Data
    } catch {
        return $null
    }
}

function Assert-ActiveDriverBinding {
    param(
        [System.IO.FileInfo] $Inf
    )

    $expectedVersion = Get-InfDriverVersionValue -InfPath $Inf.FullName
    $activeInf = Get-IddSampleDeviceProperty -KeyName "DEVPKEY_Device_DriverInfPath"
    $activeVersion = Get-IddSampleDeviceProperty -KeyName "DEVPKEY_Device_DriverVersion"
    $activeConfig = Get-IddSampleDeviceProperty -KeyName "DEVPKEY_Device_ConfigurationId"
    $hasProblem = Get-IddSampleDeviceProperty -KeyName "DEVPKEY_Device_HasProblem"
    $activePackageVersion = Get-DriverPackageVersionByPublishedName -PublishedName $activeInf
    $activeInstalledInfVersion = Get-InstalledInfVersionByPublishedName -PublishedName $activeInf

    Write-Host "Active binding DriverInf=$activeInf"
    Write-Host "Active binding DriverVersion=$activeVersion"
    Write-Host "Active binding PackageDriverVersion=$activePackageVersion"
    Write-Host "Active binding InstalledInfVersion=$activeInstalledInfVersion"
    Write-Host "Active binding Configuration=$activeConfig"
    Write-Host "Active binding HasProblem=$hasProblem"

    if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
        Write-Warning "Could not parse DriverVer from payload INF: $($Inf.FullName)"
        return
    }

    Write-Host "Expected payload DriverVersion=$expectedVersion"
    if (-not [string]::IsNullOrWhiteSpace($activePackageVersion) -and
        [System.String]::Equals($activePackageVersion, $expectedVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (-not [string]::IsNullOrWhiteSpace($activeVersion) -and
            -not [System.String]::Equals($activeVersion, $expectedVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Active PnP DriverVersion is stale ($activeVersion), but $activeInf resolves to installed package version $activePackageVersion."
        }
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($activeInstalledInfVersion) -and
        [System.String]::Equals($activeInstalledInfVersion, $expectedVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (-not [string]::IsNullOrWhiteSpace($activeVersion) -and
            -not [System.String]::Equals($activeVersion, $expectedVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Active PnP DriverVersion is stale ($activeVersion), but $activeInf on disk has installed INF version $activeInstalledInfVersion."
        }
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($activeVersion) -and
        -not [System.String]::Equals($activeVersion, $expectedVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed payload version is $expectedVersion, but active device remains bound to $activeInf version $activeVersion. Remove stale iddsampledriver.inf packages and rerun this installer."
    }
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

    pnputil /remove-device "SWD\IddSampleDriver\IddSampleDriver" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove existing IddSampleDriver device instance; continuing with driver package refresh."
    }

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

Assert-DriverPayload -Inf $Inf

Write-Host "Installing: $($Inf.FullName)"
pnputil /add-driver $Inf.FullName /install

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

pnputil /scan-devices | Out-Host
Assert-ActiveDriverBinding -Inf $Inf
Write-Host "Driver package installed."
