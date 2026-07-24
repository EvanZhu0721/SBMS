param(
    [switch] $TryHost,
    [switch] $VersionOnly
)

$ErrorActionPreference = "Continue"

$Identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$Principal = [Security.Principal.WindowsPrincipal]::new($Identity)
$IsAdmin = $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Get-SBMSDriverIdentity {
    $paths = @(
        (Join-Path $PSScriptRoot 'driver-identity.json')
    )
    $packaged = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'driver') -Filter 'driver-identity.json' -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($packaged) {
        $paths += $packaged.FullName
    }
    $path = $paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $path) {
        throw 'SBMS driver identity contract is missing.'
    }
    Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

$DriverIdentity = Get-SBMSDriverIdentity
$CurrentInstancePrefix = "SWD\$($DriverIdentity.pnp.enumerator)\$($DriverIdentity.pnp.instancePrefix)"

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

function Get-SBMSDevices {
    param(
        [ValidateSet('Current', 'Legacy')]
        [string] $IdentityKind
    )

    $matchesIdentity = {
        param([string] $InstanceId)
        if ($IdentityKind -eq 'Current') {
            return -not [string]::IsNullOrWhiteSpace($InstanceId) -and
                $InstanceId.StartsWith($CurrentInstancePrefix, [StringComparison]::OrdinalIgnoreCase)
        }
        foreach ($prefix in @($DriverIdentity.legacy.instanceIdPrefixes)) {
            if (-not [string]::IsNullOrWhiteSpace($InstanceId) -and
                $InstanceId.StartsWith([string]$prefix, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        return $false
    }

    if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
        return @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            & $matchesIdentity ([string]$_.InstanceId)
        })
    }

    @(Get-CimInstance Win32_PnPEntity | Where-Object {
        & $matchesIdentity ([string]$_.PNPDeviceID)
    })
}

function Write-SBMSDevices {
    param(
        [ValidateSet('Current', 'Legacy')]
        [string] $IdentityKind
    )

    Get-SBMSDevices -IdentityKind $IdentityKind |
        Select-Object Status, Class, FriendlyName, InstanceId, PNPDeviceID, Problem, ConfigManagerErrorCode |
        Format-List
}

function Write-SBMSDeviceProperties {
    param(
        [ValidateSet('Current', 'Legacy')]
        [string] $IdentityKind
    )

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

    foreach ($device in @(Get-SBMSDevices -IdentityKind $IdentityKind)) {
        $instanceId = if ($device.InstanceId) { [string]$device.InstanceId } else { [string]$device.PNPDeviceID }
        Write-Host "InstanceId=$instanceId"
        foreach ($key in $keys) {
            try {
                $property = Get-PnpDeviceProperty -InstanceId $instanceId -KeyName $key -ErrorAction Stop
                Write-Host "$($property.KeyName)=$($property.Data)"
            } catch {
            }
        }
        Write-Host ''
    }
}

function Test-VirtualDisplayVisible {
    $Native = Join-Path $PSScriptRoot "SBMSNative.exe"
    if (-not (Test-Path $Native)) {
        return $false
    }
    $text = (& $Native --list 2>&1 | Out-String)
    return ($text -match '(?i)SBMS Virtual Display|SBMS Display|SBMS Indirect Display')
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

function Get-SBMSReleaseMetadata {
    $manifestPath = Join-Path $PSScriptRoot "SBMS.release.json"
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
        if ([int]$manifest.schemaVersion -notin @(2, 3, 4)) {
            throw "Unsupported SBMS release manifest schema '$($manifest.schemaVersion)'."
        }
        $productVersion = [string]$manifest.product.version
        $commit = [string]$manifest.source.commit
        $isProduction = (
            [int]$manifest.schemaVersion -in @(3, 4) -and
            [string]$manifest.profile -ceq 'Production'
        )
        if ($isProduction) {
            $releaseRoot = Split-Path -Parent $PSScriptRoot
            $installerVersion = [string]$manifest.installer.productVersion
            $driverVersion = [string]$manifest.product.driverVer
            $packageVersion = $productVersion
            $packageName = Split-Path -Leaf $releaseRoot
            $architecture = [string]$manifest.product.architecture
            if ([int]$manifest.schemaVersion -eq 4) {
                foreach ($field in @('privateProductId', 'sharedProductId', 'submissionId')) {
                    if ([string]$manifest.driverCertification.partnerCenter.$field -notmatch '^[1-9][0-9]*$') {
                        throw "SBMS production manifest contains invalid Partner Center provenance: $field"
                    }
                }
                if ([string]$manifest.driverCertification.partnerCenter.hlkPackageSha256 -notmatch '^[0-9a-f]{64}$') {
                    throw 'SBMS production manifest contains an invalid HLK submission package hash.'
                }
            }
        } else {
            $installerVersion = [string]$manifest.components.installer.productVersion
            $driverVersion = [string]$manifest.components.driver.driverVer
            $packageVersion = [string]$manifest.package.version
            $packageName = [string]$manifest.package.fileName
            $architecture = [string]$manifest.package.architecture
        }
        foreach ($required in @(
            $productVersion,
            $installerVersion,
            $driverVersion,
            $commit,
            $packageVersion,
            $packageName,
            $architecture
        )) {
            if ([string]::IsNullOrWhiteSpace([string]$required)) {
                throw "SBMS release manifest is missing required version provenance: $manifestPath"
            }
        }
        if ($commit -notmatch '^[0-9a-f]{40,64}$') {
            throw "SBMS release manifest contains an invalid source commit '$commit'."
        }
        if ($packageVersion -cne $productVersion) {
            throw "SBMS release manifest package/product version mismatch: '$packageVersion' versus '$productVersion'."
        }

        $actualProductVersion = Get-SBMSExecutableVersion -Path (Join-Path $PSScriptRoot "SBMS.exe")
        $installerPath = if ($isProduction) {
            Join-Path $releaseRoot 'SBMSSetup.exe'
        } else {
            Join-Path $PSScriptRoot 'SBMSSetup.exe'
        }
        $actualInstallerVersion = Get-SBMSExecutableVersion -Path $installerPath
        $packagedInf = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "driver") -Filter ([string]$DriverIdentity.package.infName) -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        $actualDriverVersion = if ($packagedInf) {
            Get-InfDriverVersion -InfPath $packagedInf.FullName
        } else {
            ""
        }
        if ($actualProductVersion -cne $productVersion) {
            throw "SBMS.exe ProductVersion '$actualProductVersion' does not match release manifest '$productVersion'."
        }
        if ($actualInstallerVersion -cne $installerVersion) {
            throw "SBMSSetup.exe ProductVersion '$actualInstallerVersion' does not match release manifest '$installerVersion'."
        }
        if ($actualDriverVersion -cne $driverVersion) {
            throw "DriverVer '$actualDriverVersion' does not match release manifest '$driverVersion'."
        }

        return [pscustomobject]@{
            productVersion = $productVersion
            installerVersion = $installerVersion
            driverVersion = $driverVersion
            commit = $commit
            packageVersion = $packageVersion
            packageName = $packageName
            architecture = $architecture
            sourceDirty = [bool]$manifest.source.dirty
        }
    }

    $versionPath = Join-Path $PSScriptRoot "VERSION"
    $productVersion = if (Test-Path -LiteralPath $versionPath -PathType Leaf) {
        (Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8).Trim()
    } else {
        ""
    }
    $commit = ""
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $commit = (& git -C $PSScriptRoot rev-parse HEAD 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0) { $commit = "" }
    }
    [pscustomobject]@{
        productVersion = $productVersion
        installerVersion = ""
        driverVersion = ""
        commit = [string]$commit
        packageVersion = $productVersion
        packageName = ""
        architecture = "x64"
        sourceDirty = $null
    }
}

function Get-SBMSExecutableVersion {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return "" }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if (-not [string]::IsNullOrWhiteSpace([string]$info.ProductVersion)) { return [string]$info.ProductVersion }
    return [string]$info.FileVersion
}

function Write-SBMSDriverStorePackages {
    param(
        [Parameter(Mandatory = $true)]
        [string] $InfName,

        [Parameter(Mandatory = $true)]
        [string] $DllName
    )

    $root = Join-Path $env:WINDIR "System32\DriverStore\FileRepository"
    $dirs = Get-ChildItem -LiteralPath $root -Directory -Filter "$($InfName.ToLowerInvariant())_*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending

    foreach ($dir in $dirs) {
        $inf = Join-Path $dir.FullName $InfName
        $dll = Join-Path $dir.FullName $DllName
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
    param(
        [ValidateSet('Current', 'Legacy')]
        [string] $IdentityKind
    )

    $pattern = if ($IdentityKind -eq 'Current') {
        '(?i)SBMSIndirectDisplay|SWD\\SBMS\\VIRTUALDISPLAY-'
    } else {
        '(?i)IddSampleDriver|SWD\\IDDSAMPLEDRIVER\\'
    }
    $events = Get-WinEvent -FilterHashtable @{
        ProviderName = "Microsoft-Windows-Kernel-PnP"
        StartTime = (Get-Date).AddHours(-6)
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.Message -match $pattern
    } | Select-Object -First 12

    foreach ($event in $events) {
        Write-Host ("{0:yyyy-MM-dd HH:mm:ss} EventId={1} Level={2}" -f $event.TimeCreated, $event.Id, $event.LevelDisplayName)
        $summaryLines = New-Object System.Collections.Generic.List[string]
        $messageText = $event.Message.Replace("`r`n", "`n").Replace("`r", "`n")
        foreach ($line in $messageText.Split([char]10)) {
            if ($line -match "(?i)SBMSIndirectDisplay|SWD\\SBMS\\VIRTUALDISPLAY-|IddSample|oem\d+\.inf|WUDFRd|IndirectKmd|0x[0-9A-F]+|Driver Name|Driver Version|Problem|Problem Status|Service|Configured|Started") {
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

$releaseMetadata = Get-SBMSReleaseMetadata
$packagedInf = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "driver") -Filter ([string]$DriverIdentity.package.infName) -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
$installerVersion = Get-SBMSExecutableVersion -Path (Join-Path $PSScriptRoot "SBMSSetup.exe")
$driverVersion = if (-not [string]::IsNullOrWhiteSpace([string]$releaseMetadata.driverVersion)) {
    [string]$releaseMetadata.driverVersion
} elseif ($packagedInf) {
    Get-InfDriverVersion -InfPath $packagedInf.FullName
} else {
    ""
}
if ([string]::IsNullOrWhiteSpace($installerVersion)) { $installerVersion = [string]$releaseMetadata.installerVersion }

Write-Host ""
Write-Host "== version provenance =="
Write-Host "ProductVersion=$($releaseMetadata.productVersion)"
Write-Host "InstallerVersion=$installerVersion"
Write-Host "DriverVersion=$driverVersion"
Write-Host "Commit=$($releaseMetadata.commit)"
Write-Host "PackageVersion=$($releaseMetadata.packageVersion)"
Write-Host "PackageName=$($releaseMetadata.packageName)"
Write-Host "Architecture=$($releaseMetadata.architecture)"
Write-Host "SourceDirty=$($releaseMetadata.sourceDirty)"

if ($VersionOnly) {
    return
}

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
    Select-Object Name, Length, LastWriteTime,
        @{ Name = "FileVersion"; Expression = { $_.VersionInfo.FileVersion } },
        @{ Name = "ProductVersion"; Expression = { $_.VersionInfo.ProductVersion } } |
    Format-Table -AutoSize

Write-Host ""
Write-Host "== display list =="
Write-DisplayList

Write-Host ""
Write-Host "== current SBMS driver packages =="
$driverPackages = @(Get-DriverPackages)
$foundCurrentDriver = $false
foreach ($package in $driverPackages) {
    if ($package.OriginalName -ieq [string]$DriverIdentity.package.infName) {
        $foundCurrentDriver = $true
        Write-Host "PublishedName=$($package.PublishedName)"
        Write-Host "OriginalName=$($package.OriginalName)"
        Write-Host "ProviderName=$($package.ProviderName)"
        Write-Host "ClassName=$($package.ClassName)"
        Write-Host "DriverVersion=$($package.DriverVersion)"
        Write-Host "SignerName=$($package.SignerName)"
        Write-Host ""
    }
}
if (-not $foundCurrentDriver) {
    Write-Host "No installed $($DriverIdentity.package.infName) package found."
}

Write-Host ""
Write-Host "== current SBMS DriverStore payloads =="
Write-SBMSDriverStorePackages -InfName ([string]$DriverIdentity.package.infName) -DllName ([string]$DriverIdentity.package.dllName) |
    Format-Table -AutoSize

Write-Host ""
Write-Host "== current SBMS PnP devices =="
Write-SBMSDevices -IdentityKind Current

Write-Host ""
Write-Host "== current SBMS PnP properties =="
Write-SBMSDeviceProperties -IdentityKind Current

Write-Host ""
Write-Host "== recent current SBMS Kernel-PnP events =="
Write-RecentKernelPnpEvents -IdentityKind Current

Write-Host ""
Write-Host "== legacy IddSample residue (report only) =="
$foundLegacyDriver = $false
foreach ($package in $driverPackages) {
    if (@($DriverIdentity.legacy.packageOriginalNames) -icontains [string]$package.OriginalName) {
        $foundLegacyDriver = $true
        $package | Format-List
    }
}
if (-not $foundLegacyDriver) {
    Write-Host 'No installed legacy package found.'
}
Write-SBMSDriverStorePackages -InfName 'IddSampleDriver.inf' -DllName 'IddSampleDriver.dll' |
    Format-Table -AutoSize
Write-SBMSDevices -IdentityKind Legacy
Write-SBMSDeviceProperties -IdentityKind Legacy
Write-RecentKernelPnpEvents -IdentityKind Legacy
Write-Host 'Legacy monitor hardware IDs are evidence only and are never cleanup targets without a proven legacy SBMS parent.'

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
    Write-SBMSDevices -IdentityKind Current

    Write-Host ""
    Write-Host "== current SBMS PnP properties while host alive =="
    Write-SBMSDeviceProperties -IdentityKind Current

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
