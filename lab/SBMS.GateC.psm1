Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Utf8NoBom = New-Object Text.UTF8Encoding($false)

function Get-SBMSGateCUtc { [DateTime]::UtcNow.ToString('o') }

function Get-SBMSGateCHash {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Gate C file is missing: $LiteralPath"
    }
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Write-SBMSGateCAtomic {
    param([string]$LiteralPath, [string]$Text)
    $directory = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = $script:Utf8NoBom.GetBytes($Text)
    $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
    if (Test-Path -LiteralPath $LiteralPath) {
        $backup = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.bak')
        [IO.File]::Replace($temporary, $LiteralPath, $backup)
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    } else {
        [IO.File]::Move($temporary, $LiteralPath)
    }
}

function Save-SBMSGateCManifest {
    param($Manifest, [string]$GateDirectory)
    $Manifest.updatedUtc = Get-SBMSGateCUtc
    Write-SBMSGateCAtomic -LiteralPath (Join-Path $GateDirectory 'manifest.json') -Text ($Manifest | ConvertTo-Json -Depth 20)
}

function Read-SBMSGateCManifest {
    param([string]$RunDirectory)
    $path = Join-Path (Join-Path $RunDirectory 'gate-c') 'manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Gate C manifest is missing: $path"
    }
    Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Add-SBMSGateCJournal {
    param([string]$GateDirectory, [string]$Event, [hashtable]$Data = @{})
    $entry = [ordered]@{ timestampUtc = Get-SBMSGateCUtc; event = $Event; data = $Data }
    $path = Join-Path $GateDirectory 'journal.jsonl'
    $bytes = $script:Utf8NoBom.GetBytes((($entry | ConvertTo-Json -Depth 12 -Compress) + [Environment]::NewLine))
    $stream = New-Object IO.FileStream($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
}

function ConvertTo-SBMSGateCArgument {
    param([AllowEmptyString()][string]$Argument)
    if ($Argument -match "[`0`r`n]") { throw 'Unsafe native argument rejected.' }
    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') { return $Argument }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append([char]34)
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $backslashes) + 1)
            [void]$builder.Append([char]34)
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append([char]92, $backslashes)
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) { [void]$builder.Append([char]92, 2 * $backslashes) }
    [void]$builder.Append([char]34)
    $builder.ToString()
}

function Invoke-SBMSGateCNative {
    param([string]$FilePath, [string[]]$ArgumentList)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = (@($ArgumentList | ForEach-Object { ConvertTo-SBMSGateCArgument $_ }) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Failed to start $FilePath" }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr }
}

function Get-SBMSGateCInfVersion {
    param([string]$InfPath)
    $line = Select-String -LiteralPath $InfPath -Pattern '^\s*DriverVer\s*=' | Select-Object -First 1
    if (-not $line -or $line.Line -notmatch '^\s*DriverVer\s*=\s*[^,]+,\s*(.+?)\s*$') {
        throw 'Gate C could not parse DriverVer from the exact INF.'
    }
    $Matches[1]
}

function New-SBMSGateCRealAdapter {
    $displayConfigSource = Join-Path $PSScriptRoot 'SBMS.DisplayConfig.cs'
    $getDeviceBindings = {
        param([string[]]$InstanceIds)
        $allDevices = @(Get-PnpDevice -ErrorAction Stop)
        @(
            foreach ($instanceId in $InstanceIds) {
                $matches = @($allDevices | Where-Object { [string]$_.InstanceId -ieq $instanceId })
                if ($matches.Count -eq 0) {
                    [pscustomobject]@{ exists = $false; instanceId = $instanceId; driverInf = ''; hasProblem = $false; status = 'Absent' }
                    continue
                }
                if ($matches.Count -ne 1) {
                    throw "Gate C exact PnP query returned $($matches.Count) devices for $instanceId."
                }
                $device = $matches[0]
                $properties = @(Get-PnpDeviceProperty -InstanceId ([string]$device.InstanceId) -ErrorAction Stop)
                $infProperties = @($properties | Where-Object { [string]$_.KeyName -ceq 'DEVPKEY_Device_DriverInfPath' })
                if ($infProperties.Count -gt 1) {
                    throw "Gate C PnP property query returned duplicate DriverInfPath values for $instanceId."
                }
                [pscustomobject]@{
                    exists = $true
                    instanceId = [string]$device.InstanceId
                    driverInf = if ($infProperties.Count -eq 1) { [string]$infProperties[0].Data } else { '' }
                    hasProblem = ([string]$device.Problem -cne 'CM_PROB_NONE')
                    status = [string]$device.Status
                }
            }
        )
    }.GetNewClosure()
    $getDeviceBinding = {
        param([string]$InstanceId)
        @(& $getDeviceBindings @($InstanceId))[0]
    }.GetNewClosure()
    @{
        IsReal = $true
        TestAdministrator = {
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object Security.Principal.WindowsPrincipal($identity)
            $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        }
        GetDriverPackages = {
            @(
                Get-WindowsDriver -Online -All -ErrorAction Stop |
                    ForEach-Object {
                        [pscustomobject]@{
                            publishedName = [string]$_.Driver
                            originalName = [IO.Path]::GetFileName([string]$_.OriginalFileName)
                            version = [string]$_.Version
                            provider = [string]$_.ProviderName
                            className = [string]$_.ClassName
                        }
                    }
            )
        }
        AddDriver = {
            param([string]$InfPath)
            Invoke-SBMSGateCNative -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -ArgumentList @('/add-driver', $InfPath)
        }
        ScanDevices = {
            Invoke-SBMSGateCNative -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -ArgumentList @('/scan-devices')
        }
        GetDeviceBindings = $getDeviceBindings
        GetDeviceBinding = $getDeviceBinding
        GetBindingsByPublishedInf = {
            param([string]$PublishedName)
            @(
                Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop |
                    Where-Object { [string]$_.InfName -ieq $PublishedName } |
                    ForEach-Object { [string]$_.DeviceID }
            )
        }
        GetActiveDisplayPaths = {
            if (-not ('SBMSDisplayConfig' -as [type])) {
                Add-Type -LiteralPath $displayConfigSource -ErrorAction Stop
            }
            @(
                [SBMSDisplayConfig]::GetActivePaths() |
                    ForEach-Object {
                        [pscustomobject][ordered]@{
                            adapterLuid = [string]$_.AdapterLuid
                            targetId = [uint32]$_.TargetId
                            monitorDevicePath = [string]$_.MonitorDevicePath
                            active = [bool]$_.Active
                            targetAvailable = [bool]$_.TargetAvailable
                            classification = [string]$_.Classification
                        }
                    }
            )
        }.GetNewClosure()
        RemoveDevice = {
            param([string]$InstanceId)
            Invoke-SBMSGateCNative -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -ArgumentList @('/remove-device', $InstanceId)
        }
        DeleteDriver = {
            param([string]$PublishedName)
            Invoke-SBMSGateCNative -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -ArgumentList @('/delete-driver', $PublishedName, '/uninstall', '/force')
        }
        GetProductProcesses = {
            param([string[]]$ExactPaths)
            $normalized = @($ExactPaths | ForEach-Object { [IO.Path]::GetFullPath($_) })
            @(
                Get-CimInstance Win32_Process -ErrorAction Stop |
                    Where-Object {
                        -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
                        $normalized -contains [IO.Path]::GetFullPath([string]$_.ExecutablePath)
                    } |
                    Select-Object Name, ProcessId, ParentProcessId, ExecutablePath, CommandLine, CreationDate
            )
        }
        StopProcess = {
            param($ExpectedProcess)
            $current = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$ExpectedProcess.ProcessId)" -ErrorAction SilentlyContinue
            if ($null -eq $current) {
                return [pscustomobject]@{ ExitCode = 0; StdOut = 'already absent'; StdErr = '' }
            }
            if (-not [string]::Equals([string]$current.ExecutablePath, [string]$ExpectedProcess.ExecutablePath, [StringComparison]::OrdinalIgnoreCase) -or
                [string]$current.CreationDate -cne [string]$ExpectedProcess.CreationDate) {
                throw "Gate C process identity changed before stop: $($ExpectedProcess.ProcessId)"
            }
            Stop-Process -Id ([int]$ExpectedProcess.ProcessId) -Force -ErrorAction Stop
            [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
        }
    }
}

function Get-SBMSGateCExpectedDeviceIds {
    param([int]$Count)
    for ($i = 1; $i -le $Count; ++$i) {
        $leaf = if ($i -eq 1) { 'IDDSAMPLEDRIVER' } else { "IDDSAMPLEDRIVER$i" }
        "SWD\IDDSAMPLEDRIVER\$leaf"
    }
}

function ConvertTo-SBMSGateCCanonicalValue {
    param($Value)
    if ($null -eq $Value) { return 'null;' }
    if ($Value -is [bool]) { return ('bool:' + $(if ($Value) { '1;' } else { '0;' })) }
    if ($Value -is [string] -or $Value -is [char]) {
        $text = [string]$Value
        return ('string:' + $script:Utf8NoBom.GetByteCount($text).ToString([Globalization.CultureInfo]::InvariantCulture) + ':' + $text + ';')
    }
    if ($Value -is [Collections.IDictionary]) {
        [string[]]$keys = @($Value.Keys | ForEach-Object { [string]$_ })
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        $parts = New-Object Collections.Generic.List[string]
        foreach ($key in $keys) {
            $parts.Add((ConvertTo-SBMSGateCCanonicalValue -Value $key))
            $parts.Add((ConvertTo-SBMSGateCCanonicalValue -Value $Value[$key]))
        }
        return ('dictionary:' + $keys.Count.ToString([Globalization.CultureInfo]::InvariantCulture) + ':{' + ($parts -join '') + '}')
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = @($Value)
        $parts = New-Object Collections.Generic.List[string]
        foreach ($item in $items) {
            $parts.Add((ConvertTo-SBMSGateCCanonicalValue -Value $item))
        }
        return ('array:' + $items.Count.ToString([Globalization.CultureInfo]::InvariantCulture) + ':[' + ($parts -join '') + ']')
    }
    $type = $Value.GetType()
    if ($type.IsEnum) {
        return ('enum:' + $type.FullName + ':' + ([Convert]::ToInt64($Value, [Globalization.CultureInfo]::InvariantCulture)).ToString([Globalization.CultureInfo]::InvariantCulture) + ';')
    }
    $typeCode = [Type]::GetTypeCode($type)
    if ($typeCode -in @(
            [TypeCode]::SByte, [TypeCode]::Byte, [TypeCode]::Int16, [TypeCode]::UInt16,
            [TypeCode]::Int32, [TypeCode]::UInt32, [TypeCode]::Int64, [TypeCode]::UInt64)) {
        return ('number:integer:' + ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $Value)) + ';')
    }
    if ($typeCode -eq [TypeCode]::Decimal) {
        return ('number:System.Decimal:' + ([decimal]$Value).ToString('G29', [Globalization.CultureInfo]::InvariantCulture) + ';')
    }
    if ($typeCode -eq [TypeCode]::Single) {
        return ('number:System.Single:' + ([single]$Value).ToString('R', [Globalization.CultureInfo]::InvariantCulture) + ';')
    }
    if ($typeCode -eq [TypeCode]::Double) {
        return ('number:System.Double:' + ([double]$Value).ToString('R', [Globalization.CultureInfo]::InvariantCulture) + ';')
    }
    $properties = @($Value.PSObject.Properties | Where-Object { $_.MemberType -in @('NoteProperty', 'Property') })
    if ($properties.Count -eq 0) {
        throw "Gate C canonical serialization does not support $($type.FullName)."
    }
    $propertyParts = New-Object Collections.Generic.List[string]
    foreach ($property in $properties) {
        $propertyParts.Add((ConvertTo-SBMSGateCCanonicalValue -Value ([string]$property.Name)))
        $propertyParts.Add((ConvertTo-SBMSGateCCanonicalValue -Value $property.Value))
    }
    'object:' + $properties.Count.ToString([Globalization.CultureInfo]::InvariantCulture) + ':{' + ($propertyParts -join '') + '}'
}

function Get-SBMSGateCPlanDigest {
    param($Plan)
    $canonical = ConvertTo-SBMSGateCCanonicalValue -Value $Plan
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        (($sha.ComputeHash($script:Utf8NoBom.GetBytes($canonical)) | ForEach-Object { $_.ToString('x2') }) -join '').ToUpperInvariant()
    } finally { $sha.Dispose() }
}

function New-SBMSGateCOwnershipRecord {
    param($Package)
    [pscustomobject][ordered]@{
        publishedName = [string]$Package.publishedName
        originalName = [string]$Package.originalName
        version = [string]$Package.version
        provider = [string]$Package.provider
        className = [string]$Package.className
    }
}

function Assert-SBMSGateCExpectedDevicesAbsent {
    param([hashtable]$Adapter, [string[]]$ExpectedDeviceIds, [string]$Stage)
    $bindings = if ($Adapter.ContainsKey('GetDeviceBindings')) {
        @(& $Adapter.GetDeviceBindings $ExpectedDeviceIds)
    } else {
        @($ExpectedDeviceIds | ForEach-Object { & $Adapter.GetDeviceBinding $_ })
    }
    if ($bindings.Count -ne $ExpectedDeviceIds.Count) {
        throw "Gate C $Stage exact device query returned an unexpected result count."
    }
    foreach ($instanceId in $ExpectedDeviceIds) {
        $matches = @($bindings | Where-Object { [string]$_.instanceId -ieq $instanceId })
        if ($matches.Count -ne 1) {
            throw "Gate C $Stage could not uniquely read exact expected device state: $instanceId"
        }
        $binding = $matches[0]
        if ($null -eq $binding) {
            throw "Gate C $Stage could not read exact expected device state: $instanceId"
        }
        if ([bool]$binding.exists) {
            throw "Gate C $Stage refuses stale expected device: $instanceId"
        }
    }
}

function Get-SBMSGateCPhysicalPathIdentity {
    param($Path)
    if ($null -eq $Path.PSObject.Properties['adapterLuid'] -or
        $null -eq $Path.PSObject.Properties['targetId'] -or
        $null -eq $Path.PSObject.Properties['monitorDevicePath'] -or
        [string]::IsNullOrWhiteSpace([string]$Path.monitorDevicePath)) {
        throw 'Gate C physical display path identity is incomplete.'
    }
    [pscustomobject][ordered]@{
        adapterLuid = [string]$Path.adapterLuid
        targetId = [uint32]$Path.targetId
        monitorDevicePath = [string]$Path.monitorDevicePath
    }
}

function Get-SBMSGateCUsablePhysicalPaths {
    param($Paths)
    @(
        $Paths | Where-Object {
            [bool]$_.active -and [bool]$_.targetAvailable -and [string]$_.classification -ceq 'physical'
        }
    )
}

function Test-SBMSGateCPhysicalIdentityPresent {
    param($Identity, $Paths)
    @(
        $Paths | Where-Object {
            [string]$_.adapterLuid -ceq [string]$Identity.adapterLuid -and
            [uint32]$_.targetId -eq [uint32]$Identity.targetId -and
            [string]$_.monitorDevicePath -ieq [string]$Identity.monitorDevicePath
        }
    ).Count -eq 1
}

function Get-SBMSGateCSessionPhysicalBaseline {
    param($Manifest, [hashtable]$Adapter, [string]$GateDirectory)
    $current = @(Get-SBMSGateCUsablePhysicalPaths -Paths @(& $Adapter.GetActiveDisplayPaths))
    $currentIdentities = @($current | ForEach-Object { Get-SBMSGateCPhysicalPathIdentity -Path $_ })
    $missing = @(
        $Manifest.plan.baselinePhysicalMonitorPaths | Where-Object {
            $monitorPath = [string]$_
            @($current | Where-Object { [string]$_.monitorDevicePath -ieq $monitorPath }).Count -ne 1
        }
    )
    $sessionBaseline = @(
        foreach ($monitorPath in @($Manifest.plan.baselinePhysicalMonitorPaths)) {
            $match = @($current | Where-Object { [string]$_.monitorDevicePath -ieq [string]$monitorPath })
            if ($match.Count -eq 1) {
                Get-SBMSGateCPhysicalPathIdentity -Path $match[0]
            }
        }
    )
    Add-SBMSGateCJournal $GateDirectory 'PhysicalPathsBeforeHost' @{
        plannedMonitorCount = @($Manifest.plan.baselinePhysicalMonitorPaths).Count
        activePhysicalCount = $currentIdentities.Count
        activePhysicalDigest = Get-SBMSGateCPlanDigest -Plan $currentIdentities
        sessionBaselineDigest = Get-SBMSGateCPlanDigest -Plan $sessionBaseline
        missing = @($missing)
    }
    if ($missing.Count -gt 0) {
        throw "Gate C current boot cannot uniquely resolve $($missing.Count) planned physical monitor path(s)."
    }
    $sessionBaseline
}

function Assert-SBMSGateCSessionPhysicalPathsPreserved {
    param($SessionBaseline, [hashtable]$Adapter, [string]$GateDirectory, [string]$JournalEvent)
    $current = @(Get-SBMSGateCUsablePhysicalPaths -Paths @(& $Adapter.GetActiveDisplayPaths))
    $currentIdentities = @($current | ForEach-Object { Get-SBMSGateCPhysicalPathIdentity -Path $_ })
    $missing = @(
        $SessionBaseline | Where-Object {
            -not (Test-SBMSGateCPhysicalIdentityPresent -Identity $_ -Paths $current)
        }
    )
    Add-SBMSGateCJournal $GateDirectory $JournalEvent @{
        sessionBaselineCount = @($SessionBaseline).Count
        activePhysicalCount = $currentIdentities.Count
        activePhysicalDigest = Get-SBMSGateCPlanDigest -Plan $currentIdentities
        missing = @($missing)
    }
    if ($missing.Count -gt 0) {
        throw "Gate C physical recovery path disappeared: $($missing.Count) current-boot baseline path(s) missing."
    }
}

function Test-SBMSGateCBindingSetEqual {
    param([string[]]$Expected, [string[]]$Actual)
    if ($Expected.Count -ne $Actual.Count) { return $false }
    $set = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($instanceId in $Expected) {
        if (-not $set.Add([string]$instanceId)) { return $false }
    }
    foreach ($instanceId in $Actual) {
        if (-not $set.Remove([string]$instanceId)) { return $false }
    }
    $set.Count -eq 0
}

function Assert-SBMSGateCManifest {
    param($Manifest, [string]$RunDirectory, [switch]$RequireInstallAuthorization)
    if ([int]$Manifest.schemaVersion -ne 1 -or
        [string]$Manifest.contractVersion -cne 'gate-c/1' -or
        [string]$Manifest.runId -cne [IO.Path]::GetFileName($RunDirectory)) {
        throw 'Gate C manifest identity is invalid.'
    }
    if ((Get-SBMSGateCPlanDigest -Plan $Manifest.plan) -cne [string]$Manifest.planSha256) {
        throw 'Gate C immutable plan digest drifted.'
    }
    $requiredFiles = if ($RequireInstallAuthorization) {
        @($Manifest.plan.files)
    } else {
        @($Manifest.plan.files | Where-Object { [string]$_.name -eq 'SBMS.GateC.psm1' })
    }
    foreach ($file in $requiredFiles) {
        if ((Get-SBMSGateCHash -LiteralPath ([string]$file.path)) -cne [string]$file.sha256) {
            throw "Gate C payload hash drifted: $($file.name)"
        }
    }
    if (-not $RequireInstallAuthorization) { return }
    $gateAPath = Join-Path (Join-Path $RunDirectory 'gate-a') 'manifest.json'
    $gateA = Get-Content -LiteralPath $gateAPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$gateA.schemaVersion -ne 4 -or [string]$gateA.contractVersion -cne 'gate-a/2' -or
        [string]$gateA.runId -cne [string]$Manifest.runId -or [string]$gateA.status -cne 'PASS' -or
        [string]$gateA.stableDigest -cne [string]$Manifest.plan.gateAStableDigest) {
        throw 'Gate C no longer matches its Gate A authorization.'
    }
}

function Initialize-SBMSGateC {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][guid]$RunId,
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$DriverPackagePath,
        [Parameter(Mandatory = $true)][string]$ProductRoot,
        [ValidateRange(1, 3)][int]$VerificationDeviceCount = 1,
        [hashtable]$Adapter = (New-SBMSGateCRealAdapter)
    )
    if (-not (& $Adapter.TestAdministrator)) { throw 'Gate C planning requires elevation.' }
    $resolvedRunDirectory = [IO.Path]::GetFullPath($RunDirectory)
    if ([IO.Path]::GetFileName($resolvedRunDirectory) -cne $RunId.ToString()) { throw 'Gate C Run directory does not match Run ID.' }
    $gateAPath = Join-Path (Join-Path $resolvedRunDirectory 'gate-a') 'manifest.json'
    $gateA = Get-Content -LiteralPath $gateAPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$gateA.schemaVersion -ne 4 -or [string]$gateA.contractVersion -cne 'gate-a/2' -or
        [string]$gateA.runId -cne $RunId.ToString() -or [string]$gateA.status -cne 'PASS') {
        throw 'Gate C planning requires same-Run-ID Gate A PASS.'
    }
    $gateACurrentEvidencePath = Join-Path (Join-Path $resolvedRunDirectory 'gate-a') 'current-evidence.json'
    if (-not (Test-Path -LiteralPath $gateACurrentEvidencePath -PathType Leaf)) {
        throw 'Gate C planning requires Gate A current display evidence.'
    }
    $gateACurrentEvidence = Get-Content -LiteralPath $gateACurrentEvidencePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $gateACurrentEvidence.PSObject.Properties['displayConfig'] -or
        [string]$gateACurrentEvidence.displayConfig.status -cne 'Captured') {
        throw 'Gate C planning requires captured Gate A display configuration.'
    }
    $baselinePhysicalMonitorPaths = @(
        Get-SBMSGateCUsablePhysicalPaths -Paths @($gateACurrentEvidence.displayConfig.data.activePaths) |
            ForEach-Object {
                $identity = Get-SBMSGateCPhysicalPathIdentity -Path $_
                [string]$identity.monitorDevicePath
            }
    )
    if ($baselinePhysicalMonitorPaths.Count -eq 0) {
        throw 'Gate C planning requires at least one active and available physical recovery path.'
    }
    $uniqueMonitorPaths = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($monitorPath in $baselinePhysicalMonitorPaths) {
        if (-not $uniqueMonitorPaths.Add([string]$monitorPath)) {
            throw 'Gate C planning requires unique physical monitor device paths.'
        }
    }
    $expectedDeviceIds = @(Get-SBMSGateCExpectedDeviceIds -Count $VerificationDeviceCount)
    $reservedDeviceIds = @(Get-SBMSGateCExpectedDeviceIds -Count 3)
    Assert-SBMSGateCExpectedDevicesAbsent -Adapter $Adapter -ExpectedDeviceIds $reservedDeviceIds -Stage 'Initialize'

    $gateDirectory = Join-Path $resolvedRunDirectory 'gate-c'
    $payloadDirectory = Join-Path $gateDirectory 'payload'
    if (Test-Path -LiteralPath $gateDirectory) { throw 'Gate C is already initialized for this Run ID.' }
    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
    $sourceFiles = [ordered]@{
        'IddSampleDriver.inf' = (Join-Path $DriverPackagePath 'IddSampleDriver.inf')
        'IddSampleDriver.dll' = (Join-Path $DriverPackagePath 'IddSampleDriver.dll')
        'iddsampledriver.cat' = (Join-Path $DriverPackagePath 'iddsampledriver.cat')
        'SBMS.exe' = (Join-Path $ProductRoot 'SBMS.exe')
        'SBMSNative.exe' = (Join-Path $ProductRoot 'SBMSNative.exe')
        'SBMSDeviceHost.exe' = (Join-Path $ProductRoot 'SBMSDeviceHost.exe')
        'SBMS.GateC.psm1' = (Join-Path $PSScriptRoot 'SBMS.GateC.psm1')
        'SBMS.DisplayConfig.cs' = (Join-Path $PSScriptRoot 'SBMS.DisplayConfig.cs')
    }
    foreach ($name in $sourceFiles.Keys) {
        if (-not (Test-Path -LiteralPath $sourceFiles[$name] -PathType Leaf)) { throw "Gate C payload is missing: $name" }
        Copy-Item -LiteralPath $sourceFiles[$name] -Destination (Join-Path $payloadDirectory $name)
    }
    if ($Adapter.IsReal) {
        foreach ($signedName in @('iddsampledriver.cat', 'IddSampleDriver.dll')) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $payloadDirectory $signedName)
            if ([string]$signature.Status -cne 'Valid') { throw "Gate C signature is not valid: $signedName" }
        }
    }
    $baselinePackages = @(& $Adapter.GetDriverPackages)
    if (@($baselinePackages | Where-Object { [string]$_.originalName -ieq 'IddSampleDriver.inf' }).Count -gt 0) {
        throw 'Gate C refuses a baseline that already contains IddSampleDriver.inf.'
    }
    $files = @(
        foreach ($name in $sourceFiles.Keys) {
            $path = Join-Path $payloadDirectory $name
            [pscustomobject][ordered]@{ name = $name; path = $path; sha256 = Get-SBMSGateCHash -LiteralPath $path }
        }
    )
    $plan = [pscustomobject][ordered]@{
        runId = $RunId.ToString()
        gateAStableDigest = [string]$gateA.stableDigest
        originalInfName = 'IddSampleDriver.inf'
        driverVersion = Get-SBMSGateCInfVersion -InfPath (Join-Path $payloadDirectory 'IddSampleDriver.inf')
        verificationDeviceCount = $VerificationDeviceCount
        expectedDeviceIds = $expectedDeviceIds
        reservedDeviceIds = $reservedDeviceIds
        baselinePhysicalMonitorPaths = $baselinePhysicalMonitorPaths
        stopEventName = "Global\SBMSDeviceHostStop-$($RunId.ToString())"
        baselinePublishedNames = @($baselinePackages | ForEach-Object { [string]$_.publishedName } | Sort-Object -Unique)
        files = $files
    }
    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        contractVersion = 'gate-c/1'
        runId = $RunId.ToString()
        state = 'Planned'
        createdUtc = Get-SBMSGateCUtc
        updatedUtc = Get-SBMSGateCUtc
        plan = $plan
        planSha256 = Get-SBMSGateCPlanDigest -Plan $plan
        ownedPublishedName = $null
        ownership = $null
        ownershipSha256 = $null
        ownedDeviceIds = @()
        rebootRequired = $false
        lastError = $null
    }
    Save-SBMSGateCManifest -Manifest $manifest -GateDirectory $gateDirectory
    Add-SBMSGateCJournal -GateDirectory $gateDirectory -Event 'Planned' -Data @{ planSha256 = $manifest.planSha256 }
    $manifest
}

function Wait-SBMSGateCDeviceBinding {
    param([hashtable]$Adapter, [string]$InstanceId, [string]$PublishedName, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $binding = & $Adapter.GetDeviceBinding $InstanceId
        if ($null -ne $binding -and [bool]$binding.exists -and
            [string]$binding.driverInf -ieq $PublishedName -and
            -not [bool]$binding.hasProblem -and [string]$binding.status -ieq 'OK') {
            return $binding
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Start-SBMSGateCVerificationHost {
    param($Manifest, [hashtable]$Adapter)
    if ($Adapter.ContainsKey('StartVerificationHost')) {
        return & $Adapter.StartVerificationHost $Manifest
    }
    $hostPath = [string](@($Manifest.plan.files | Where-Object name -eq 'SBMSDeviceHost.exe')[0].path)
    $arguments = @(
        '--count', [string]$Manifest.plan.verificationDeviceCount,
        '--run-id', [string]$Manifest.runId,
        '--stop-event', [string]$Manifest.plan.stopEventName
    )
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $hostPath
    $info.Arguments = (@($arguments | ForEach-Object { ConvertTo-SBMSGateCArgument $_ }) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    if (-not $process.Start()) { throw 'Gate C could not start the verification host.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    $lines = New-Object Collections.Generic.List[string]
    $read = $process.StandardOutput.ReadLineAsync()
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        if ($read.Wait(250)) {
            $line = [string]$read.Result
            if ($line.Length -gt 0) { $lines.Add($line) }
            if ($line -match '^device_host=ready ' -and $line -match ('run_id=' + [regex]::Escape([string]$Manifest.runId))) {
                $deviceIds = @(
                    $lines |
                        ForEach-Object {
                            if ([string]$_ -match '^device_host=created\s+index=\d+\s+instance=(.+)$') {
                                [string]$Matches[1]
                            }
                        }
                )
                if ($deviceIds.Count -ne [int]$Manifest.plan.verificationDeviceCount) {
                    throw 'Gate C verification host did not report the exact created device identities.'
                }
                return [pscustomobject]@{
                    process = $process
                    output = @($lines | ForEach-Object { [string]$_ })
                    arguments = $arguments
                    deviceIds = $deviceIds
                }
            }
            $read = $process.StandardOutput.ReadLineAsync()
        }
    }
    try { if (-not $process.HasExited) { $process.Kill() } } catch {}
    throw "Gate C verification host did not become ready. Output: $($lines -join ' | ')"
}

function Stop-SBMSGateCVerificationHost {
    param($HostResult, [hashtable]$Adapter)
    if ($Adapter.ContainsKey('StopVerificationHost')) {
        & $Adapter.StopVerificationHost $HostResult
        return
    }
    if ($null -eq ('SBMSGateCNativeEvents' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SBMSGateCNativeEvents {
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr OpenEvent(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@
    }
    $eventHandle = [SBMSGateCNativeEvents]::OpenEvent(0x0002, $false, [string]$HostResult.arguments[5])
    if ($eventHandle -eq [IntPtr]::Zero) { throw 'Gate C could not open the run-owned host stop event.' }
    try {
        if (-not [SBMSGateCNativeEvents]::SetEvent($eventHandle)) { throw 'Gate C could not signal the run-owned host stop event.' }
    } finally { [void][SBMSGateCNativeEvents]::CloseHandle($eventHandle) }
    if (-not $HostResult.process.WaitForExit(10000)) {
        try { $HostResult.process.Kill() } catch {}
        throw 'Gate C verification host did not stop after its run-owned event was signaled.'
    }
    if ($HostResult.process.ExitCode -ne 0) { throw "Gate C verification host exited with $($HostResult.process.ExitCode)." }
}

function Invoke-SBMSGateCInstall {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    $gateDirectory = Join-Path $RunDirectory 'gate-c'
    if ([string]$Manifest.state -eq 'InstalledAndVerified') { return $Manifest }
    if ([string]$Manifest.state -ne 'Planned') { throw "Gate C Install requires Planned state, found $($Manifest.state)." }
    Assert-SBMSGateCExpectedDevicesAbsent -Adapter $Adapter -ExpectedDeviceIds @($Manifest.plan.reservedDeviceIds) -Stage 'Install'
    $packagesBefore = @(& $Adapter.GetDriverPackages)
    if (@($packagesBefore | Where-Object { [string]$_.originalName -ieq [string]$Manifest.plan.originalInfName }).Count -gt 0) {
        throw 'Gate C found an unowned matching package before installation.'
    }
    $sessionPhysicalBaseline = @(
        Get-SBMSGateCSessionPhysicalBaseline -Manifest $Manifest -Adapter $Adapter -GateDirectory $gateDirectory
    )
    $Manifest.state = 'InstallIntent'
    Save-SBMSGateCManifest $Manifest $gateDirectory
    Add-SBMSGateCJournal $gateDirectory 'InstallIntent' @{}
    $infPath = [string](@($Manifest.plan.files | Where-Object name -eq 'IddSampleDriver.inf')[0].path)
    $add = & $Adapter.AddDriver $infPath
    Add-SBMSGateCJournal $gateDirectory 'AddDriverResult' @{ exitCode = $add.ExitCode; stdout = $add.StdOut; stderr = $add.StdErr }
    if ([int]$add.ExitCode -notin @(0, 259, 3010)) { throw "Gate C pnputil add-driver failed with $($add.ExitCode)." }
    $Manifest.rebootRequired = ([int]$add.ExitCode -eq 3010)
    $packagesAfter = @(& $Adapter.GetDriverPackages)
    $newPackages = @(
        $packagesAfter | Where-Object {
            [string]$_.originalName -ieq [string]$Manifest.plan.originalInfName -and
            [string]$_.version -ieq [string]$Manifest.plan.driverVersion -and
            [string]$_.publishedName -notin @($Manifest.plan.baselinePublishedNames)
        }
    )
    if ($newPackages.Count -ne 1) { throw "Gate C ownership read-back found $($newPackages.Count) matching new packages; expected exactly one." }
    $Manifest.ownedPublishedName = [string]$newPackages[0].publishedName
    $Manifest.ownership = New-SBMSGateCOwnershipRecord -Package $newPackages[0]
    $Manifest.ownershipSha256 = Get-SBMSGateCPlanDigest -Plan $Manifest.ownership
    $Manifest.state = 'PackageOwned'
    Save-SBMSGateCManifest $Manifest $gateDirectory
    Add-SBMSGateCJournal $gateDirectory 'PackageOwned' @{ publishedName = $Manifest.ownedPublishedName }
    $preExistingBindings = @(& $Adapter.GetBindingsByPublishedInf $Manifest.ownedPublishedName)
    if ($preExistingBindings.Count -gt 0) {
        throw "Gate C staged package already has device bindings: $($Manifest.ownedPublishedName)"
    }
    Assert-SBMSGateCSessionPhysicalPathsPreserved -SessionBaseline $sessionPhysicalBaseline `
        -Adapter $Adapter -GateDirectory $gateDirectory -JournalEvent 'PhysicalPathsAfterStage'

    $hostResult = $null
    try {
        $hostResult = Start-SBMSGateCVerificationHost -Manifest $Manifest -Adapter $Adapter
        $Manifest.state = 'HostStarted'
        Save-SBMSGateCManifest $Manifest $gateDirectory
        Add-SBMSGateCJournal $gateDirectory 'HostStarted' @{ pid = $hostResult.process.Id; output = @($hostResult.output) }
        & $Adapter.ScanDevices | Out-Null
        $verificationIds = @(
            if ($Adapter.IsReal) {
                @($hostResult.deviceIds)
            } elseif ($null -ne $hostResult.PSObject.Properties['deviceIds']) {
                @($hostResult.deviceIds)
            } else {
                @($Manifest.plan.expectedDeviceIds)
            }
        )
        if ($verificationIds.Count -ne [int]$Manifest.plan.verificationDeviceCount) {
            throw 'Gate C verification host returned an unexpected device count.'
        }
        $ownedIds = New-Object Collections.Generic.List[string]
        foreach ($instanceId in $verificationIds) {
            $binding = Wait-SBMSGateCDeviceBinding -Adapter $Adapter -InstanceId $instanceId -PublishedName $Manifest.ownedPublishedName
            if ($null -eq $binding) { throw "Gate C device binding failed: $instanceId" }
            $ownedIds.Add([string]$binding.instanceId)
        }
        $Manifest.ownedDeviceIds = @($ownedIds | ForEach-Object { [string]$_ })
        $allPublishedBindings = @(& $Adapter.GetBindingsByPublishedInf $Manifest.ownedPublishedName | ForEach-Object { [string]$_ })
        if (-not (Test-SBMSGateCBindingSetEqual -Expected @($Manifest.ownedDeviceIds) -Actual $allPublishedBindings)) {
            $reservedSet = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach ($reservedId in @($Manifest.plan.reservedDeviceIds)) { [void]$reservedSet.Add([string]$reservedId) }
            $safeCleanupIds = @(
                @($Manifest.ownedDeviceIds) + @($allPublishedBindings) |
                    Where-Object { $reservedSet.Contains([string]$_) } |
                    Select-Object -Unique
            )
            $Manifest.ownedDeviceIds = @($safeCleanupIds)
            Save-SBMSGateCManifest $Manifest $gateDirectory
            throw 'Gate C published-name bindings do not exactly match this verification host.'
        }
        Add-SBMSGateCJournal $gateDirectory 'PackageBindingsVerified' @{
            publishedName = $Manifest.ownedPublishedName
            deviceIds = @($Manifest.ownedDeviceIds)
        }
        Assert-SBMSGateCSessionPhysicalPathsPreserved -SessionBaseline $sessionPhysicalBaseline `
            -Adapter $Adapter -GateDirectory $gateDirectory -JournalEvent 'PhysicalPathsAfterHost'
    } finally {
        if ($null -ne $hostResult) { Stop-SBMSGateCVerificationHost -HostResult $hostResult -Adapter $Adapter }
    }
    $Manifest.state = 'InstalledAndVerified'
    $Manifest.lastError = $null
    Save-SBMSGateCManifest $Manifest $gateDirectory
    Add-SBMSGateCJournal $gateDirectory 'InstalledAndVerified' @{ publishedName = $Manifest.ownedPublishedName; deviceIds = @($Manifest.ownedDeviceIds) }
    $Manifest
}

function Invoke-SBMSGateCRollback {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    $gateDirectory = Join-Path $RunDirectory 'gate-c'
    if ([string]$Manifest.state -eq 'RollbackVerified') { return $Manifest }
    $Manifest.state = 'RollbackIntent'
    Save-SBMSGateCManifest $Manifest $gateDirectory
    Add-SBMSGateCJournal $gateDirectory 'RollbackIntent' @{}
    $rollbackNeedsReboot = $false

    $productPaths = @($Manifest.plan.files | Where-Object { [string]$_.name -in @('SBMS.exe','SBMSNative.exe','SBMSDeviceHost.exe') } | ForEach-Object { [string]$_.path })
    foreach ($process in @(& $Adapter.GetProductProcesses $productPaths)) {
        if ([string]$process.ExecutablePath -in $productPaths) {
            $stop = & $Adapter.StopProcess $process
            if ($null -eq $stop -or [int]$stop.ExitCode -ne 0) { throw "Gate C could not stop run-owned process $($process.ProcessId)." }
        }
    }

    $publishedName = [string]$Manifest.ownedPublishedName
    if ([string]::IsNullOrWhiteSpace($publishedName)) {
        $adoptionCandidates = @(
            & $Adapter.GetDriverPackages | Where-Object {
                [string]$_.originalName -ieq [string]$Manifest.plan.originalInfName -and
                [string]$_.version -ieq [string]$Manifest.plan.driverVersion -and
                [string]$_.publishedName -notin @($Manifest.plan.baselinePublishedNames)
            }
        )
        if ($adoptionCandidates.Count -gt 1) {
            throw 'Gate C rollback found multiple matching post-baseline packages and refuses to guess ownership.'
        }
        if ($adoptionCandidates.Count -eq 1) {
            $publishedName = [string]$adoptionCandidates[0].publishedName
            $Manifest.ownedPublishedName = $publishedName
            $Manifest.ownership = New-SBMSGateCOwnershipRecord -Package $adoptionCandidates[0]
            $Manifest.ownershipSha256 = Get-SBMSGateCPlanDigest -Plan $Manifest.ownership
            Save-SBMSGateCManifest $Manifest $gateDirectory
            Add-SBMSGateCJournal $gateDirectory 'PackageOwnershipRecovered' @{ publishedName = $publishedName }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($publishedName)) {
        if ($null -eq $Manifest.ownership -or
            (Get-SBMSGateCPlanDigest -Plan $Manifest.ownership) -cne [string]$Manifest.ownershipSha256 -or
            [string]$Manifest.ownership.publishedName -cne $publishedName -or
            [string]$Manifest.ownership.originalName -ine [string]$Manifest.plan.originalInfName -or
            [string]$Manifest.ownership.version -ine [string]$Manifest.plan.driverVersion -or
            [string]$Manifest.ownership.publishedName -in @($Manifest.plan.baselinePublishedNames)) {
            throw 'Gate C package ownership record is invalid.'
        }
        $ownedPackages = @(
            & $Adapter.GetDriverPackages | Where-Object {
                [string]$_.publishedName -ieq $publishedName -and
                [string]$_.originalName -ieq [string]$Manifest.ownership.originalName -and
                [string]$_.version -ieq [string]$Manifest.ownership.version -and
                [string]$_.provider -ceq [string]$Manifest.ownership.provider -and
                [string]$_.className -ceq [string]$Manifest.ownership.className
            }
        )
        if ($ownedPackages.Count -gt 1) {
            throw "Gate C package ownership read-back failed for $publishedName."
        }
        if ($ownedPackages.Count -eq 0) {
            $orphanBindings = @(& $Adapter.GetBindingsByPublishedInf $publishedName)
            if ($orphanBindings.Count -gt 0) {
                throw "Gate C sees bindings for an absent owned package: $publishedName."
            }
            $Manifest.state = 'RollbackVerified'
            $Manifest.lastError = $null
            Save-SBMSGateCManifest $Manifest $gateDirectory
            Add-SBMSGateCJournal $gateDirectory 'RollbackVerified' @{ publishedName = $publishedName; recoveredAfterReboot = $true }
            return $Manifest
        }
        $ownedDeviceIds = if (@($Manifest.ownedDeviceIds).Count -gt 0) {
            @($Manifest.ownedDeviceIds)
        } else {
            @($Manifest.plan.expectedDeviceIds)
        }
        $externalBindings = @(& $Adapter.GetBindingsByPublishedInf $publishedName | Where-Object { [string]$_ -notin $ownedDeviceIds })
        if ($externalBindings.Count -gt 0) {
            throw "Gate C refuses package deletion because external devices use $publishedName."
        }
        foreach ($instanceId in $ownedDeviceIds) {
            $binding = & $Adapter.GetDeviceBinding $instanceId
            if ($null -ne $binding -and [bool]$binding.exists) {
                if ([string]$binding.driverInf -ine $publishedName) { throw "Gate C refuses to remove a device not bound to $publishedName." }
                $remove = & $Adapter.RemoveDevice $instanceId
                if ([int]$remove.ExitCode -notin @(0, 259, 3010)) { throw "Gate C remove-device failed with $($remove.ExitCode)." }
                if ([int]$remove.ExitCode -eq 3010) { $rollbackNeedsReboot = $true }
            }
        }
        $remainingBindings = @(& $Adapter.GetBindingsByPublishedInf $publishedName)
        if ($remainingBindings.Count -gt 0) {
            if ($rollbackNeedsReboot) {
                $Manifest.rebootRequired = $true
                $Manifest.state = 'RollbackPendingReboot'
                Save-SBMSGateCManifest $Manifest $gateDirectory
                Add-SBMSGateCJournal $gateDirectory 'RollbackPendingReboot' @{ stage = 'Devices'; publishedName = $publishedName }
                return $Manifest
            }
            throw "Gate C still sees devices bound to $publishedName."
        }
        $delete = & $Adapter.DeleteDriver $publishedName
        if ([int]$delete.ExitCode -notin @(0, 259, 3010)) { throw "Gate C delete-driver failed with $($delete.ExitCode)." }
        if ([int]$delete.ExitCode -eq 3010) { $rollbackNeedsReboot = $true }
        $remainingPackages = @(& $Adapter.GetDriverPackages | Where-Object { [string]$_.publishedName -ieq $publishedName })
        if ($remainingPackages.Count -gt 0) {
            if ($rollbackNeedsReboot) {
                $Manifest.rebootRequired = $true
                $Manifest.state = 'RollbackPendingReboot'
                Save-SBMSGateCManifest $Manifest $gateDirectory
                Add-SBMSGateCJournal $gateDirectory 'RollbackPendingReboot' @{ stage = 'Package'; publishedName = $publishedName }
                return $Manifest
            }
            throw "Gate C package remains after deletion: $publishedName"
        }
    }
    $Manifest.state = 'RollbackVerified'
    $Manifest.lastError = $null
    Save-SBMSGateCManifest $Manifest $gateDirectory
    Add-SBMSGateCJournal $gateDirectory 'RollbackVerified' @{ publishedName = $publishedName }
    $Manifest
}

function Invoke-SBMSGateC {
    [CmdletBinding()]
    param(
        [ValidateSet('Audit','Install','Rollback')][string]$Phase = 'Audit',
        [Parameter(Mandatory = $true)][guid]$RunId,
        [string]$RunRoot = 'C:\ProgramData\SBMSLab\Runs',
        [switch]$Execute,
        [string]$Acknowledgement,
        [hashtable]$Adapter = (New-SBMSGateCRealAdapter)
    )
    $mutex = New-Object Threading.Mutex($false, "Global\SBMSGateC_$($RunId.ToString('N'))")
    $hasMutex = $false
    try {
        try {
            $hasMutex = $mutex.WaitOne([TimeSpan]::FromSeconds(30))
        } catch [Threading.AbandonedMutexException] {
            $hasMutex = $true
        }
        if (-not $hasMutex) { throw 'Gate C could not acquire the run transaction lock.' }

        $root = [IO.Path]::GetFullPath($RunRoot)
        $runDirectory = [IO.Path]::GetFullPath((Join-Path $root $RunId.ToString()))
        if (-not $runDirectory.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Gate C Run directory escaped RunRoot.' }
        $manifest = Read-SBMSGateCManifest -RunDirectory $runDirectory
        Assert-SBMSGateCManifest -Manifest $manifest -RunDirectory $runDirectory -RequireInstallAuthorization:($Phase -ne 'Rollback')
        if ($Phase -eq 'Audit') { return $manifest }
        if (-not $Execute -or -not (& $Adapter.TestAdministrator)) { throw 'Gate C mutation requires -Execute and elevation.' }
        $expected = "SBMS-GATE-C/$($RunId.ToString())/$Phase/$($manifest.planSha256)"
        if ($Acknowledgement -cne $expected) { throw "Gate C acknowledgement mismatch. Expected: $expected" }
        if ($Adapter.IsReal) {
            $plannedModule = @($manifest.plan.files | Where-Object { [string]$_.name -eq 'SBMS.GateC.psm1' })
            $loadedModulePath = [IO.Path]::GetFullPath([string]$MyInvocation.MyCommand.Module.Path)
            if ($plannedModule.Count -ne 1 -or
                -not [string]::Equals($loadedModulePath, [IO.Path]::GetFullPath([string]$plannedModule[0].path), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Gate C real mutation must execute the frozen run-owned module.'
            }
            $hardwareManifest = Get-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            if ([string]$hardwareManifest.runId -cne $RunId.ToString() -or [string]$hardwareManifest.profile -cne 'TestSigning') {
                throw 'Gate C is not bound to a TestSigning HardwareLab run.'
            }
            if ($Phase -eq 'Install') {
                if ([string]$hardwareManifest.state -notin @('Armed','WatchdogRestartIntentPersisted')) {
                    throw 'Gate C Install requires an armed HardwareLab manifest.'
                }
                foreach ($frozen in @(
                        @{ path = [string]$hardwareManifest.watchdogPlan.scriptPath; sha256 = [string]$hardwareManifest.watchdogPlan.scriptSha256 },
                        @{ path = [string]$hardwareManifest.watchdogPlan.modulePath; sha256 = [string]$hardwareManifest.watchdogPlan.moduleSha256 })) {
                    if ((Get-SBMSGateCHash -LiteralPath $frozen.path) -cne $frozen.sha256) {
                        throw 'Gate C Install requires intact frozen watchdog assets.'
                    }
                }
                Import-Module -Name ([string]$hardwareManifest.watchdogPlan.modulePath) -Force
                if (-not (Test-SBMSHardwareLabWatchdogContract -RunDirectory $runDirectory -RunId $RunId)) {
                    throw 'Gate C Install requires an enabled, exact same-run watchdog contract.'
                }
                $bcd = Invoke-SBMSGateCNative -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') -ArgumentList @('/enum', '{current}', '/v')
                if ($bcd.ExitCode -ne 0 -or $bcd.StdOut -notmatch '(?im)^\s*testsigning\s+(yes|on|true|是|开)\s*$' -or
                    $bcd.StdOut -notmatch [regex]::Escape([string]$hardwareManifest.clone.guid)) {
                    throw 'Gate C Install requires the exact TestSigning clone to be current.'
                }
            }
        }
        $gateDirectory = Join-Path $runDirectory 'gate-c'
        try {
            if ($Phase -eq 'Install') {
                $manifest = Invoke-SBMSGateCInstall -Manifest $manifest -RunDirectory $runDirectory -Adapter $Adapter
            } else {
                $manifest = Invoke-SBMSGateCRollback -Manifest $manifest -RunDirectory $runDirectory -Adapter $Adapter
            }
            return $manifest
        } catch {
            $manifest.lastError = $_.Exception.Message
            $manifest.state = 'RollbackRequired'
            Save-SBMSGateCManifest $manifest $gateDirectory
            Add-SBMSGateCJournal $gateDirectory 'RollbackRequired' @{ phase = $Phase; reason = $_.Exception.Message }
            throw
        }
    } finally {
        if ($hasMutex) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

Export-ModuleMember -Function New-SBMSGateCRealAdapter, Read-SBMSGateCManifest, Initialize-SBMSGateC, Invoke-SBMSGateC
