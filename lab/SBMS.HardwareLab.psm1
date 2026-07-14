Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:GuidPattern = '\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}'

function ConvertTo-SBMSCanonicalGuid {
    param([Parameter(Mandatory = $true)][string]$Value)
    $match = [regex]::Match($Value, $script:GuidPattern)
    if (-not $match.Success) { throw "Invalid BCD identifier: $Value" }
    return $match.Value.ToLowerInvariant()
}

function Get-SBMSUtcTimestamp { return [DateTime]::UtcNow.ToString('o') }

function Get-SBMSFileSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash
}

function Write-SBMSUtf8Atomic {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)][string]$Text
    )
    $directory = Split-Path -Parent $LiteralPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = $script:Utf8NoBom.GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
    if (Test-Path -LiteralPath $LiteralPath) {
        $backup = Join-Path $directory ('.' + [IO.Path]::GetFileName($LiteralPath) + '.' + [guid]::NewGuid().ToString('N') + '.bak')
        [IO.File]::Replace($temporary, $LiteralPath, $backup)
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    } else {
        [IO.File]::Move($temporary, $LiteralPath)
    }
}

function Add-SBMSJournalEntry {
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$Event,
        [hashtable]$Data = @{}
    )
    $entry = [ordered]@{ timestampUtc = Get-SBMSUtcTimestamp; event = $Event; data = $Data }
    $line = ($entry | ConvertTo-Json -Depth 12 -Compress) + [Environment]::NewLine
    $path = Join-Path $RunDirectory 'journal.jsonl'
    $bytes = $script:Utf8NoBom.GetBytes($line)
    $stream = New-Object IO.FileStream($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Save-SBMSManifest {
    param([Parameter(Mandatory = $true)]$Manifest, [Parameter(Mandatory = $true)][string]$RunDirectory)
    $Manifest.updatedUtc = Get-SBMSUtcTimestamp
    $path = Join-Path $RunDirectory 'manifest.json'
    Write-SBMSUtf8Atomic -LiteralPath $path -Text ($Manifest | ConvertTo-Json -Depth 20)
}

function Read-SBMSManifest {
    param([Parameter(Mandatory = $true)][string]$RunDirectory)
    $path = Join-Path $RunDirectory 'manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Run manifest not found: $path" }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function ConvertTo-SBMSWindowsCommandLineArgument {
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
    return $builder.ToString()
}

function Invoke-SBMSNativeProcess {
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$ArgumentList = @())
    $quoted = foreach ($argument in $ArgumentList) { ConvertTo-SBMSWindowsCommandLineArgument -Argument $argument }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = ($quoted -join ' ')
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
    return [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr }
}

function Test-SBMSAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-SBMSHardwareLabAdapter {
    [CmdletBinding()]
    param()
    return @{
        IsReal = $true
        TestAdministrator = { Test-SBMSAdministrator }
        InvokeBcd = {
            param([string[]]$ArgumentList)
            Invoke-SBMSNativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\bcdedit.exe') -ArgumentList $ArgumentList
        }
        InstallWatchdog = {
            param($Specification)
            $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $arguments = Get-SBMSWatchdogExpectedArguments -Specification $Specification
            $escape = { param([string]$Value) [Security.SecurityElement]::Escape($Value) }
            $delay = 'PT{0}M' -f [int]$Specification.timeoutMinutes
            $xmlText = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>SBMS one-shot hardware lab recovery watchdog</Description></RegistrationInfo>
  <Triggers><BootTrigger><Enabled>true</Enabled><Delay>$delay</Delay></BootTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT5M</ExecutionTimeLimit><Priority>4</Priority></Settings>
  <Actions Context="Author"><Exec><Command>$(& $escape $powerShell)</Command><Arguments>$(& $escape $arguments)</Arguments></Exec></Actions>
</Task>
"@
            $xmlPath = Join-Path ([IO.Path]::GetTempPath()) ('SBMS-Watchdog-' + [guid]::NewGuid().ToString('N') + '.xml')
            try {
                [IO.File]::WriteAllText($xmlPath, $xmlText, [Text.Encoding]::Unicode)
                [xml]$parsed = [IO.File]::ReadAllText($xmlPath, [Text.Encoding]::Unicode)
                if ([string]$parsed.Task.Triggers.BootTrigger.Delay -cne $delay) { throw 'Generated watchdog XML delay failed self-readback.' }
                Invoke-SBMSNativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\schtasks.exe') -ArgumentList @(
                    '/Create', '/TN', [string]$Specification.taskName, '/XML', $xmlPath, '/F'
                )
            } finally {
                if (Test-Path -LiteralPath $xmlPath) { Remove-Item -LiteralPath $xmlPath -Force }
            }
        }
        GetWatchdog = {
            param([string]$TaskName)
            $result = Invoke-SBMSNativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\schtasks.exe') -ArgumentList @('/Query', '/TN', $TaskName, '/XML')
            if ($result.ExitCode -ne 0) { return [pscustomobject]@{ exists = $false; exitCode = $result.ExitCode } }
            try {
                [xml]$xml = $result.StdOut
                $exec = $xml.Task.Actions.Exec
                $principal = $xml.Task.Principals.Principal
                return [pscustomobject]@{
                    exists = $true
                    command = [string]$exec.Command
                    arguments = [string]$exec.Arguments
                    userId = [string]$principal.UserId
                    hasBootTrigger = ($null -ne $xml.Task.Triggers.BootTrigger)
                    bootDelay = [string]$xml.Task.Triggers.BootTrigger.Delay
                    bootTriggerEnabled = ([string]$xml.Task.Triggers.BootTrigger.Enabled -match '^(?i:true|1)$')
                    enabled = ([string]$xml.Task.Settings.Enabled -match '^(?i:true|1)$')
                    rawXml = $result.StdOut
                }
            } catch { throw "Watchdog task XML could not be parsed: $($_.Exception.Message)" }
        }
        RemoveWatchdog = {
            param([string]$TaskName)
            Invoke-SBMSNativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\schtasks.exe') -ArgumentList @('/Delete', '/TN', $TaskName, '/F')
        }
        DisableWatchdog = {
            param([string]$TaskName)
            Invoke-SBMSNativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\schtasks.exe') -ArgumentList @('/Change', '/TN', $TaskName, '/Disable')
        }
        RequestRestart = {
            Invoke-SBMSNativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\shutdown.exe') -ArgumentList @('/r', '/f', '/t', '5', '/d', 'p:0:0', '/c', 'SBMS hardware lab watchdog rollback')
        }
        SecureRunDirectory = {
            param([string]$RunDirectory)
            $systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
            $adminsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
            $directories = @($RunDirectory) + @(Get-ChildItem -LiteralPath $RunDirectory -Directory -Recurse -Force -ErrorAction Stop | Select-Object -ExpandProperty FullName)
            foreach ($directory in $directories) {
                $security = New-Object Security.AccessControl.DirectorySecurity
                $security.SetOwner($adminsSid)
                $security.SetAccessRuleProtection($true, $false)
                foreach ($sid in @($systemSid, $adminsSid)) {
                    $rule = New-Object Security.AccessControl.FileSystemAccessRule(
                        $sid, [Security.AccessControl.FileSystemRights]::FullControl,
                        ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
                        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
                    $security.AddAccessRule($rule) | Out-Null
                }
                Set-Acl -LiteralPath $directory -AclObject $security
            }
            foreach ($file in @(Get-ChildItem -LiteralPath $RunDirectory -File -Recurse -Force -ErrorAction Stop)) {
                $security = New-Object Security.AccessControl.FileSecurity
                $security.SetOwner($adminsSid)
                $security.SetAccessRuleProtection($true, $false)
                foreach ($sid in @($systemSid, $adminsSid)) {
                    $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)
                    $security.AddAccessRule($rule) | Out-Null
                }
                Set-Acl -LiteralPath $file.FullName -AclObject $security
            }
            [pscustomobject]@{ success = $true }
        }
        TestRunDirectorySecurity = {
            param([string]$RunDirectory)
            $allowed = @('S-1-5-18', 'S-1-5-32-544')
            $paths = @($RunDirectory) + @(Get-ChildItem -LiteralPath $RunDirectory -Recurse -Force -ErrorAction Stop | Select-Object -ExpandProperty FullName)
            $objects = New-Object Collections.Generic.List[object]
            foreach ($path in $paths) {
                $acl = Get-Acl -LiteralPath $path
                $ownerSid = try { (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { [string]$acl.Owner }
                $fullControlSids = New-Object Collections.Generic.HashSet[string]
                $unexpectedRules = New-Object Collections.Generic.List[string]
                foreach ($rule in $acl.Access) {
                    $sid = try { $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { [string]$rule.IdentityReference }
                    if ($allowed -notcontains $sid -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
                        $unexpectedRules.Add("$sid|$($rule.AccessControlType)|$($rule.FileSystemRights)")
                    }
                    if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl) {
                        [void]$fullControlSids.Add($sid)
                    }
                }
                $objects.Add([pscustomobject]@{
                    path = $path
                    ownerAllowed = ($allowed -contains $ownerSid)
                    inheritanceProtected = [bool]$acl.AreAccessRulesProtected
                    systemFullControl = $fullControlSids.Contains('S-1-5-18')
                    administratorsFullControl = $fullControlSids.Contains('S-1-5-32-544')
                    unexpectedRules = @($unexpectedRules)
                })
            }
            $success = @($objects | Where-Object {
                -not $_.ownerAllowed -or -not $_.inheritanceProtected -or -not $_.systemFullControl -or
                -not $_.administratorsFullControl -or @($_.unexpectedRules).Count -gt 0
            }).Count -eq 0
            return [pscustomobject]@{ success = $success; objects = @($objects) }
        }
    }
}

function Invoke-SBMSAdapterBcd {
    param([Parameter(Mandatory = $true)][hashtable]$Adapter, [Parameter(Mandatory = $true)][string[]]$Arguments)
    if (-not $Adapter.ContainsKey('InvokeBcd')) { throw 'Adapter does not implement InvokeBcd.' }
    $result = & $Adapter.InvokeBcd $Arguments
    if ($null -eq $result -or $null -eq $result.ExitCode) { throw 'InvokeBcd returned an invalid result.' }
    return $result
}

function Get-SBMSGidsFromText {
    param([string]$Text)
    return @([regex]::Matches([string]$Text, $script:GuidPattern) | ForEach-Object { $_.Value.ToLowerInvariant() } | Select-Object -Unique)
}

function Get-SBMSBcdTokenGuids {
    param([string]$Text, [Parameter(Mandatory = $true)][string]$Token)
    $lines = @(([string]$Text) -split '\r?\n')
    $values = New-Object Collections.Generic.List[string]
    $collecting = $false
    foreach ($line in $lines) {
        if ($line -match ('^\s*' + [regex]::Escape($Token) + '\s+(.*)$')) {
            $collecting = $true
            foreach ($guid in (Get-SBMSGidsFromText -Text $matches[1])) { $values.Add($guid) }
            continue
        }
        if ($collecting -and $line -match '^\s+\{') {
            foreach ($guid in (Get-SBMSGidsFromText -Text $line)) { $values.Add($guid) }
            continue
        }
        if ($collecting -and $line.Trim().Length -gt 0) { break }
    }
    return @($values | Select-Object -Unique)
}

function Get-SBMSBcdEntrySections {
    param([string]$Text)
    $lines = @(([string]$Text) -split '\r?\n')
    $separators = New-Object Collections.Generic.List[int]
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s*-{3,}\s*$') { $separators.Add($index) }
    }
    if ($separators.Count -eq 0) { return @([string]$Text) }
    $sections = New-Object Collections.Generic.List[string]
    for ($sectionIndex = 0; $sectionIndex -lt $separators.Count; $sectionIndex++) {
        $start = $separators[$sectionIndex] + 1
        $end = if ($sectionIndex + 1 -lt $separators.Count) { $separators[$sectionIndex + 1] - 2 } else { $lines.Count - 1 }
        if ($end -lt $start) { continue }
        $sections.Add(($lines[$start..$end] -join "`r`n"))
    }
    return @($sections)
}

function Get-SBMSBcdPrimaryIdentifierGuids {
    param([string]$Text)
    $identifiers = New-Object Collections.Generic.List[string]
    foreach ($section in @(Get-SBMSBcdEntrySections -Text $Text)) {
        foreach ($line in @(([string]$section) -split '\r?\n')) {
            $guids = @(Get-SBMSGidsFromText -Text $line)
            if ($guids.Count -eq 0) { continue }
            if ($guids.Count -ne 1) { throw 'The first GUID-bearing BCD entry field was ambiguous.' }
            $identifiers.Add($guids[0])
            break
        }
    }
    return @($identifiers)
}

function Get-SBMSBcdState {
    param([Parameter(Mandatory = $true)][hashtable]$Adapter)
    $bootmgr = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', '{bootmgr}', '/v')
    $current = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', '{current}', '/v')
    $default = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', '{default}', '/v')
    foreach ($item in @($bootmgr, $current, $default)) {
        if ($item.ExitCode -ne 0) {
            $detail = (@([string]$item.StdErr, [string]$item.StdOut) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' | '
            if ([string]::IsNullOrWhiteSpace($detail)) { $detail = 'No diagnostic text was returned. BCD reads normally require an elevated administrator session.' }
            throw "BCD read failed (exit $($item.ExitCode)): $detail"
        }
    }
    $currentIds = @(Get-SBMSBcdPrimaryIdentifierGuids -Text $current.StdOut)
    $defaultIds = @(Get-SBMSBcdPrimaryIdentifierGuids -Text $default.StdOut)
    if ($currentIds.Count -ne 1 -or $defaultIds.Count -ne 1) { throw 'Could not uniquely parse the primary identifier for current/default BCD entries.' }
    return [ordered]@{
        currentGuid = $currentIds[0]
        defaultGuid = $defaultIds[0]
        resolvedDefaultGuid = $defaultIds[0]
        displayOrder = @(Get-SBMSBcdTokenGuids -Text $bootmgr.StdOut -Token 'displayorder')
        bootSequence = @(Get-SBMSBcdTokenGuids -Text $bootmgr.StdOut -Token 'bootsequence')
        currentText = $current.StdOut
        bootManagerText = $bootmgr.StdOut
    }
}

function Test-SBMSCurrentTestSigning {
    param($BcdState)
    return [regex]::IsMatch([string]$BcdState.currentText, '(?im)^\s*testsigning\s+(yes|on|true|是|开)\s*$')
}

function Test-SBMSBaselineInvariant {
    param($Baseline, $Current)
    if ($Baseline.currentGuid -ne $Current.currentGuid) { throw 'Current BCD entry changed unexpectedly.' }
    if ($Baseline.defaultGuid -ne $Current.defaultGuid) { throw 'bootmgr default changed unexpectedly.' }
    if ((@($Baseline.displayOrder) -join '|') -ne (@($Current.displayOrder) -join '|')) { throw 'bootmgr displayorder changed unexpectedly.' }
}

function Assert-SBMSCloneDeletionSafe {
    param($Manifest, [hashtable]$Adapter)
    $state = Get-SBMSBcdState -Adapter $Adapter
    Test-SBMSBaselineInvariant -Baseline $Manifest.baseline -Current $state
    $cloneGuid = [string]$Manifest.clone.guid
    if ([string]::IsNullOrWhiteSpace($cloneGuid)) { throw 'Clone deletion requires a non-empty manifest GUID.' }
    if ($state.currentGuid -eq $cloneGuid) { throw 'Refusing to delete the currently active loader.' }
    if ($state.defaultGuid -eq $cloneGuid -or $state.resolvedDefaultGuid -eq $cloneGuid) { throw 'Refusing to delete the default loader.' }
    return $state
}

function Resolve-SBMSCloneCleanupPresence {
    param($Manifest, [hashtable]$Adapter)
    [void](Assert-SBMSCloneDeletionSafe -Manifest $Manifest -Adapter $Adapter)
    $cloneGuid = [string]$Manifest.clone.guid
    $probe = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', $cloneGuid, '/v')
    if ($probe.ExitCode -eq 0) {
        if (-not (Test-SBMSCloneOwnershipReadback -Adapter $Adapter -CloneGuid $cloneGuid -Description $Manifest.clone.description)) {
            throw 'Clone identifier or exact description no longer matches the manifest.'
        }
        return $true
    }
    $candidates = @(Resolve-SBMSOwnedCloneGuid -Manifest $Manifest -Adapter $Adapter)
    if ($candidates.Count -eq 0) { return $false }
    throw "Clone-specific read-back failed while exact-description reconciliation still found $($candidates.Count) candidate(s); cleanup is blocked."
}

function Get-SBMSWatchdogExpectedArguments {
    param($Specification)
    $values = @(
        [string]$Specification.scriptPath, [string]$Specification.runDirectory,
        [string]$Specification.runId, [string]$Specification.taskName,
        [string]$Specification.profile, [string]$Specification.acknowledgement
    )
    if (@($values | Where-Object { $_ -match "['`0`r`n]" }).Count -gt 0) {
        throw 'Watchdog arguments must not contain whitespace, quotes, CR, or LF.'
    }
    $scriptLines = @(
        '$ErrorActionPreference=''Stop'''
        ('$rd=''{0}''' -f [string]$Specification.runDirectory)
        ('$runId=''{0}''' -f [string]$Specification.runId)
        ('$task=''{0}''' -f [string]$Specification.taskName)
        ('$profile=''{0}''' -f [string]$Specification.profile)
        ('$ack=''{0}''' -f [string]$Specification.acknowledgement)
        ('$rich=''{0}''' -f [string]$Specification.scriptPath)
        'function Invoke-Fallback {'
        ' if(-not(Test-Path -LiteralPath $rd -PathType Container)){New-Item -ItemType Directory -Path $rd -Force|Out-Null}'
        ' $utf=New-Object Text.UTF8Encoding($false)'
        ' $terminalPath=Join-Path $rd ''watchdog-restart.requested'''
        ' if(Test-Path -LiteralPath $terminalPath -PathType Leaf){return}'
        ' $intentPath=Join-Path $rd ''watchdog-restart.intent'''
        ' if(-not(Test-Path -LiteralPath $intentPath -PathType Leaf)){try{$s=New-Object IO.FileStream($intentPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$b=$utf.GetBytes(([DateTime]::UtcNow.ToString(''o''))+" inline`n");$s.Write($b,0,$b.Length);$s.Flush($true)}finally{$s.Dispose()}}catch [IO.IOException]{}}'
        ' & (Join-Path $env:SystemRoot ''System32\shutdown.exe'') /r /f /t 5 /d p:0:0 /c ''SBMS watchdog fail-safe recovery'' *> $null'
        ' if($LASTEXITCODE -eq 0 -and -not(Test-Path -LiteralPath $terminalPath)){try{$s=New-Object IO.FileStream($terminalPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$b=$utf.GetBytes(([DateTime]::UtcNow.ToString(''o''))+" requested`n");$s.Write($b,0,$b.Length);$s.Flush($true)}finally{$s.Dispose()}}catch [IO.IOException]{}}'
        ' if(Test-Path -LiteralPath $terminalPath -PathType Leaf){& (Join-Path $env:SystemRoot ''System32\schtasks.exe'') /Change /TN $task /Disable *> $null}'
        '}'
        'try{& $rich -RunDirectory $rd -RunId $runId -TaskName $task -Profile $profile -Execute -Acknowledgement $ack}catch{Invoke-Fallback}'
    )
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(($scriptLines -join "`r`n")))
    return "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded"
}

function Test-SBMSCloneOwnershipReadback {
    param([hashtable]$Adapter, [string]$CloneGuid, [string]$Description)
    $result = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', $CloneGuid, '/v')
    if ($result.ExitCode -ne 0) { return $false }
    $ids = @(Get-SBMSBcdPrimaryIdentifierGuids -Text $result.StdOut)
    $descriptions = @([regex]::Matches([string]$result.StdOut, '(?im)^\s*description\s+(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    $hasId = ($ids.Count -eq 1 -and $ids[0] -eq $CloneGuid)
    $hasDescription = ($descriptions.Count -eq 1 -and $descriptions[0] -ceq $Description)
    return ($hasId -and $hasDescription)
}

function Test-SBMSCloneReadback {
    param([hashtable]$Adapter, [string]$CloneGuid, [string]$Description, [string]$Profile)
    if (-not (Test-SBMSCloneOwnershipReadback -Adapter $Adapter -CloneGuid $CloneGuid -Description $Description)) { return $false }
    $result = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', $CloneGuid, '/v')
    $hasTestSigning = [regex]::IsMatch([string]$result.StdOut, '(?im)^\s*testsigning\s+(yes|on|true|是|开)\s*$')
    $profileMatches = if ($Profile -eq 'TestSigning') { $hasTestSigning } else { -not $hasTestSigning }
    return $profileMatches
}

function Get-SBMSOwnedCloneCandidates {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Adapter,
        [Parameter(Mandatory = $true)][string]$Description,
        [string[]]$ExcludedGuids = @()
    )
    $all = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', 'all', '/v')
    if ($all.ExitCode -ne 0) { throw "BCD ownership read-back failed: $($all.StdErr)" }
    $blocks = @(Get-SBMSBcdEntrySections -Text $all.StdOut)
    $candidates = New-Object Collections.Generic.List[string]
    foreach ($block in $blocks) {
        $descriptionMatches = @([regex]::Matches($block, '(?im)^\s*description\s+(.+?)\s*$'))
        if ($descriptionMatches.Count -ne 1) { continue }
        if ($descriptionMatches[0].Groups[1].Value.Trim() -cne $Description) { continue }
        $identifiers = @(Get-SBMSBcdPrimaryIdentifierGuids -Text $block)
        if ($identifiers.Count -ne 1) { continue }
        if ($ExcludedGuids -notcontains $identifiers[0]) { $candidates.Add($identifiers[0]) }
    }
    return @($candidates | Select-Object -Unique)
}

function Resolve-SBMSOwnedCloneGuid {
    param($Manifest, [hashtable]$Adapter)
    $excluded = @([string]$Manifest.baseline.currentGuid, [string]$Manifest.baseline.defaultGuid)
    return @(Get-SBMSOwnedCloneCandidates -Adapter $Adapter -Description ([string]$Manifest.clone.description) -ExcludedGuids $excluded)
}

function New-SBMSRunManifest {
    param([string]$RunId, [string]$RunDirectory, $Baseline, [int]$WatchdogTimeoutMinutes, [string]$Profile)
    return [pscustomobject][ordered]@{
        schemaVersion = 2
        runId = $RunId
        profile = $Profile
        state = 'Created'
        createdUtc = Get-SBMSUtcTimestamp
        updatedUtc = Get-SBMSUtcTimestamp
        runDirectory = $RunDirectory
        baseline = $Baseline
        clone = [pscustomobject]@{ guid = $null; description = "SBMS LAB $Profile ONE-TIME $RunId" }
        watchdogPlan = [pscustomobject]@{
            status = 'Planned'
            autoRestart = $true
            timeoutMinutes = $WatchdogTimeoutMinutes
            taskName = "SBMS-HardwareLab-Watchdog-$RunId"
            runId = $RunId
            profile = $Profile
            scriptPath = $null
            scriptSha256 = $null
            modulePath = $null
            moduleSha256 = $null
            acknowledgement = "SBMS-HARDWARE-LAB-WATCHDOG/$RunId/$Profile"
            note = 'SYSTEM ONSTART watchdog. It self-disables and requests at most one recovery reboot when the active loader is this clone.'
        }
        lastError = $null
    }
}

function Protect-SBMSRunAssets {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    if (-not $Adapter.ContainsKey('SecureRunDirectory') -or -not $Adapter.ContainsKey('TestRunDirectorySecurity')) {
        throw 'Adapter lacks run-directory security methods.'
    }
    $watchdogDirectory = Join-Path $RunDirectory 'watchdog'
    if (-not (Test-Path -LiteralPath $watchdogDirectory)) { New-Item -ItemType Directory -Path $watchdogDirectory -Force | Out-Null }
    $sourceScript = Join-Path $PSScriptRoot 'Invoke-SBMSLabWatchdog.ps1'
    $sourceModule = Join-Path $PSScriptRoot 'SBMS.HardwareLab.psm1'
    $frozenScript = Join-Path $watchdogDirectory 'Invoke-SBMSLabWatchdog.ps1'
    $frozenModule = Join-Path $watchdogDirectory 'SBMS.HardwareLab.psm1'
    Copy-Item -LiteralPath $sourceScript -Destination $frozenScript -Force
    Copy-Item -LiteralPath $sourceModule -Destination $frozenModule -Force
    $Manifest.watchdogPlan.scriptPath = $frozenScript
    $Manifest.watchdogPlan.scriptSha256 = Get-SBMSFileSha256 -LiteralPath $frozenScript
    $Manifest.watchdogPlan.modulePath = $frozenModule
    $Manifest.watchdogPlan.moduleSha256 = Get-SBMSFileSha256 -LiteralPath $frozenModule
    $Manifest.watchdogPlan | Add-Member -NotePropertyName runDirectory -NotePropertyValue $RunDirectory -Force
    Save-SBMSManifest $Manifest $RunDirectory
    Add-SBMSJournalEntry $RunDirectory 'RunAssetsFrozenAndSecured' @{
        scriptSha256 = $Manifest.watchdogPlan.scriptSha256
        moduleSha256 = $Manifest.watchdogPlan.moduleSha256
    }
    Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter
}

function Test-SBMSRunDirectorySecurityResult {
    param($Result)
    if ($null -eq $Result -or $Result -is [bool]) { return $false }
    if ($null -eq $Result.PSObject.Properties['success'] -or -not [bool]$Result.success) { return $false }
    if ($null -eq $Result.PSObject.Properties['objects']) { return $false }
    $objects = @($Result.objects)
    if ($objects.Count -eq 0) { return $false }
    foreach ($object in $objects) {
        foreach ($property in @('path', 'ownerAllowed', 'inheritanceProtected', 'systemFullControl', 'administratorsFullControl', 'unexpectedRules')) {
            if ($null -eq $object.PSObject.Properties[$property]) { return $false }
        }
        if ([string]::IsNullOrWhiteSpace([string]$object.path)) { return $false }
        if (-not [bool]$object.ownerAllowed -or -not [bool]$object.inheritanceProtected -or
            -not [bool]$object.systemFullControl -or -not [bool]$object.administratorsFullControl -or
            @($object.unexpectedRules).Count -gt 0) { return $false }
    }
    return $true
}

function Set-SBMSRunDirectorySecurity {
    param([string]$RunDirectory, [hashtable]$Adapter)
    if (-not $Adapter.ContainsKey('SecureRunDirectory') -or -not $Adapter.ContainsKey('TestRunDirectorySecurity')) {
        throw 'Adapter lacks run-directory security methods.'
    }
    $secured = & $Adapter.SecureRunDirectory $RunDirectory
    if ($null -eq $secured -or -not $secured.success) { throw 'Securing the run directory failed.' }
    $readback = & $Adapter.TestRunDirectorySecurity $RunDirectory
    if (-not (Test-SBMSRunDirectorySecurityResult -Result $readback)) { throw 'Run-directory ACL structured read-back failed.' }
}

function Test-SBMSWatchdogReadback {
    param([hashtable]$Adapter, $Specification, [bool]$RequireEnabled = $true)
    if (-not $Adapter.ContainsKey('GetWatchdog')) { throw 'Adapter does not implement GetWatchdog.' }
    $actual = & $Adapter.GetWatchdog ([string]$Specification.taskName)
    if ($null -eq $actual -or $actual -is [bool]) { return $false }
    foreach ($property in @('exists', 'command', 'arguments', 'userId', 'hasBootTrigger', 'bootDelay', 'bootTriggerEnabled', 'enabled')) {
        if ($null -eq $actual.PSObject.Properties[$property]) { return $false }
    }
    if (-not [bool]$actual.exists) { return $false }
    $expectedPowerShell = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')
    if (-not ([string]$actual.command).Equals($expectedPowerShell, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $expectedArguments = Get-SBMSWatchdogExpectedArguments -Specification $Specification
    if (-not ([string]$actual.arguments).Equals($expectedArguments, [StringComparison]::Ordinal)) { return $false }
    if (-not ([string]$actual.userId -match '(?i)(^|\\)SYSTEM$|S-1-5-18')) { return $false }
    if (-not $actual.hasBootTrigger) { return $false }
    if (-not [bool]$actual.bootTriggerEnabled) { return $false }
    if ($RequireEnabled -and -not [bool]$actual.enabled) { return $false }
    $expectedDelay = 'PT{0}M' -f [int]$Specification.timeoutMinutes
    if (-not ([string]$actual.bootDelay).Equals($expectedDelay, [StringComparison]::Ordinal)) { return $false }
    if ((Get-SBMSFileSha256 -LiteralPath $Specification.scriptPath) -ne $Specification.scriptSha256) { return $false }
    if ((Get-SBMSFileSha256 -LiteralPath $Specification.modulePath) -ne $Specification.moduleSha256) { return $false }
    return $true
}

function Install-SBMSWatchdog {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    if (-not $Adapter.ContainsKey('InstallWatchdog')) { throw 'Adapter does not implement InstallWatchdog.' }
    if (-not (Test-Path -LiteralPath $Manifest.watchdogPlan.scriptPath -PathType Leaf)) { throw "Frozen watchdog script not found: $($Manifest.watchdogPlan.scriptPath)" }
    if (-not (Test-Path -LiteralPath $Manifest.watchdogPlan.modulePath -PathType Leaf)) { throw "Frozen watchdog module not found: $($Manifest.watchdogPlan.modulePath)" }
    $existing = $false
    if ($Adapter.ContainsKey('GetWatchdog')) {
        $probe = & $Adapter.GetWatchdog ([string]$Manifest.watchdogPlan.taskName)
        $existing = ($null -ne $probe -and $probe.exists)
    }
    if ($existing) {
        if (-not (Test-SBMSWatchdogReadback -Adapter $Adapter -Specification $Manifest.watchdogPlan)) { throw 'An existing watchdog task has the expected name but different content.' }
    } else {
        $result = & $Adapter.InstallWatchdog $Manifest.watchdogPlan
        if ($null -eq $result -or $result.ExitCode -ne 0) { throw "Watchdog task creation failed: $($result.StdErr)" }
    }
    if (-not (Test-SBMSWatchdogReadback -Adapter $Adapter -Specification $Manifest.watchdogPlan)) { throw 'Watchdog task read-back failed.' }
    $Manifest.watchdogPlan.status = 'InstalledAndVerified'
    Save-SBMSManifest $Manifest $RunDirectory
    Add-SBMSJournalEntry $RunDirectory 'WatchdogInstalledAndVerified' @{ taskName = $Manifest.watchdogPlan.taskName; scriptSha256 = $Manifest.watchdogPlan.scriptSha256 }
}

function Remove-SBMSWatchdog {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    if (-not $Adapter.ContainsKey('GetWatchdog') -or -not $Adapter.ContainsKey('RemoveWatchdog')) { throw 'Adapter lacks watchdog cleanup methods.' }
    $probe = & $Adapter.GetWatchdog ([string]$Manifest.watchdogPlan.taskName)
    if ($null -eq $probe -or -not $probe.exists) { return }
    if (-not (Test-SBMSWatchdogReadback -Adapter $Adapter -Specification $Manifest.watchdogPlan -RequireEnabled $false)) { throw 'Watchdog task content drifted; refusing name-only deletion.' }
    Add-SBMSJournalEntry $RunDirectory 'WatchdogDeleteIntent' @{ taskName = $Manifest.watchdogPlan.taskName }
    $result = & $Adapter.RemoveWatchdog ([string]$Manifest.watchdogPlan.taskName)
    if ($null -eq $result -or $result.ExitCode -ne 0) { throw "Watchdog deletion failed: $($result.StdErr)" }
    $readback = & $Adapter.GetWatchdog ([string]$Manifest.watchdogPlan.taskName)
    if ($null -ne $readback -and $readback.exists) { throw 'Watchdog still exists after deletion.' }
    $Manifest.watchdogPlan.status = 'Removed'
    Save-SBMSManifest $Manifest $RunDirectory
}

function Get-SBMSHardwareLabSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][hashtable]$Adapter, [Parameter(Mandatory = $true)][string]$RunDirectory)
    $state = Get-SBMSBcdState -Adapter $Adapter
    $all = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', 'all', '/v')
    if ($all.ExitCode -ne 0) { throw "BCD snapshot failed: $($all.StdErr)" }
    $snapshotDirectory = Join-Path $RunDirectory 'snapshot'
    if (-not (Test-Path -LiteralPath $snapshotDirectory)) { New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null }
    Write-SBMSUtf8Atomic -LiteralPath (Join-Path $snapshotDirectory 'bcd-all.txt') -Text $all.StdOut
    Write-SBMSUtf8Atomic -LiteralPath (Join-Path $snapshotDirectory 'bcd-state.json') -Text ($state | ConvertTo-Json -Depth 12)
    return [pscustomobject]@{
        bcd = $state
        bcdAllSha256 = Get-SBMSFileSha256 -LiteralPath (Join-Path $snapshotDirectory 'bcd-all.txt')
        capturedUtc = Get-SBMSUtcTimestamp
    }
}

function Assert-SBMSMutationAuthorized {
    param([hashtable]$Adapter, [bool]$Execute, [string]$Acknowledgement, [string]$ExpectedAcknowledgement)
    if (-not $Execute) { throw 'Mutation phase requires -Execute.' }
    if ($Acknowledgement -cne $ExpectedAcknowledgement) { throw "Explicit acknowledgement mismatch. Expected: $ExpectedAcknowledgement" }
    if (-not $Adapter.ContainsKey('TestAdministrator') -or -not (& $Adapter.TestAdministrator)) { throw 'An elevated administrator session is required.' }
}

function Invoke-SBMSPrepare {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    if ($Manifest.state -eq 'Prepared') {
        if (-not (Test-SBMSCloneReadback -Adapter $Adapter -CloneGuid $Manifest.clone.guid -Description $Manifest.clone.description -Profile $Manifest.profile)) { throw 'Prepared manifest does not match BCD read-back.' }
        if (-not (Test-SBMSWatchdogReadback -Adapter $Adapter -Specification $Manifest.watchdogPlan)) { throw 'Prepared manifest watchdog read-back failed.' }
        return $Manifest
    }
    if ($Manifest.state -notin @('Created', 'SnapshotComplete')) { throw "Prepare is invalid from state $($Manifest.state); use Rollback to reconcile failed transactions." }
    Protect-SBMSRunAssets -Manifest $Manifest -RunDirectory $RunDirectory -Adapter $Adapter
    $before = Get-SBMSBcdState -Adapter $Adapter
    Test-SBMSBaselineInvariant -Baseline $Manifest.baseline -Current $before
    if (Test-SBMSCurrentTestSigning -BcdState $before) { throw 'The active baseline already has Test Signing enabled; mutation is blocked.' }
    $Manifest.state = 'CloneCreateIntent'; Save-SBMSManifest $Manifest $RunDirectory
    Add-SBMSJournalEntry $RunDirectory 'CloneCreateIntent' @{}
    try {
        $copy = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/copy', $Manifest.baseline.currentGuid, '/d', $Manifest.clone.description)
        $outputCandidates = @(Get-SBMSGidsFromText -Text ($copy.StdOut + "`n" + $copy.StdErr) | Where-Object { $_ -ne $Manifest.baseline.currentGuid -and $_ -ne $Manifest.baseline.defaultGuid })
        Add-SBMSJournalEntry $RunDirectory 'CloneCopyResult' @{
            exitCode = $copy.ExitCode
            outputGuidCandidates = @($outputCandidates)
        }
        $ownedCandidates = @(Resolve-SBMSOwnedCloneGuid -Manifest $Manifest -Adapter $Adapter)
        if ($ownedCandidates.Count -ne 1) {
            throw "BCD clone ownership read-back found $($ownedCandidates.Count) entries for the exact run description; expected exactly one."
        }
        $Manifest.clone.guid = $ownedCandidates[0]
        $Manifest.state = 'CloneCreated'; Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'CloneCreated' @{
            cloneGuid = $Manifest.clone.guid
            copyExitCode = $copy.ExitCode
            outputCandidateMatched = ($outputCandidates -contains $Manifest.clone.guid)
        }
        if ($copy.ExitCode -ne 0) { throw "BCD clone command returned failure after ownership read-back: $($copy.StdErr)" }
        if ($Manifest.profile -eq 'TestSigning') {
            $Manifest.state = 'CloneConfigureIntent'; Save-SBMSManifest $Manifest $RunDirectory
            $set = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/set', $Manifest.clone.guid, 'testsigning', 'on')
            if ($set.ExitCode -ne 0) { throw "Setting Test Signing on clone failed: $($set.StdErr)" }
        }
        if (-not (Test-SBMSCloneReadback -Adapter $Adapter -CloneGuid $Manifest.clone.guid -Description $Manifest.clone.description -Profile $Manifest.profile)) { throw "Clone $($Manifest.profile) read-back failed." }
        $after = Get-SBMSBcdState -Adapter $Adapter
        Test-SBMSBaselineInvariant -Baseline $Manifest.baseline -Current $after
        $Manifest.state = 'WatchdogInstallIntent'; Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'WatchdogInstallIntent' @{ taskName = $Manifest.watchdogPlan.taskName }
        Install-SBMSWatchdog -Manifest $Manifest -RunDirectory $RunDirectory -Adapter $Adapter
        $Manifest.state = 'Prepared'; Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'Prepared' @{ cloneGuid = $Manifest.clone.guid }
        Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter
        return $Manifest
    } catch {
        $failureMessage = $_.Exception.Message
        $cleanupFailures = New-Object Collections.Generic.List[string]
        $ownedCandidates = @()
        try { $ownedCandidates = @(Resolve-SBMSOwnedCloneGuid -Manifest $Manifest -Adapter $Adapter) } catch {
            Add-SBMSJournalEntry $RunDirectory 'CloneOwnershipReconcileFailed' @{ reason = $_.Exception.Message }
            $cleanupFailures.Add("Clone ownership reconciliation failed: $($_.Exception.Message)")
        }
        if ([string]::IsNullOrWhiteSpace([string]$Manifest.clone.guid) -and $ownedCandidates.Count -eq 1) {
            $Manifest.clone.guid = $ownedCandidates[0]
            Save-SBMSManifest $Manifest $RunDirectory
            Add-SBMSJournalEntry $RunDirectory 'CloneOwnershipRecovered' @{ cloneGuid = $Manifest.clone.guid }
        }
        if ($ownedCandidates.Count -gt 1) {
            Add-SBMSJournalEntry $RunDirectory 'CloneOwnershipManualBlock' @{
                reason = 'Multiple entries share the exact run description; no entry was guessed or deleted.'
                candidateGuids = @($ownedCandidates)
                originalFailure = $failureMessage
            }
            $cleanupFailures.Add("Ownership is ambiguous: $($ownedCandidates.Count) exact-description clones exist.")
        }
        Add-SBMSJournalEntry $RunDirectory 'PrepareRollbackIntent' @{ cloneGuid = $Manifest.clone.guid; reason = $failureMessage }
        $cleanupSafe = ($ownedCandidates.Count -le 1)
        $clonePresent = $false
        if ($ownedCandidates.Count -le 1 -and -not [string]::IsNullOrWhiteSpace([string]$Manifest.clone.guid)) {
            try {
                $clonePresent = Resolve-SBMSCloneCleanupPresence -Manifest $Manifest -Adapter $Adapter
            } catch {
                $cleanupFailures.Add("Clone cleanup safety/reconciliation failed: $($_.Exception.Message)")
                $cleanupSafe = $false
            }
        }
        if ($cleanupSafe) {
            try { Remove-SBMSWatchdog -Manifest $Manifest -RunDirectory $RunDirectory -Adapter $Adapter } catch {
                Add-SBMSJournalEntry $RunDirectory 'PrepareWatchdogRollbackFailed' @{ reason = $_.Exception.Message }
                $cleanupFailures.Add("Watchdog cleanup failed: $($_.Exception.Message)")
            }
        } else {
            Add-SBMSJournalEntry $RunDirectory 'PrepareCleanupBlocked' @{ reason = 'Clone ownership or baseline safety could not be proven; task and clone were retained.' }
        }
        if ($cleanupSafe -and $clonePresent) {
            try {
                $clonePresent = Resolve-SBMSCloneCleanupPresence -Manifest $Manifest -Adapter $Adapter
                if ($clonePresent) {
                $delete = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/delete', $Manifest.clone.guid)
                Add-SBMSJournalEntry $RunDirectory 'PrepareRollbackResult' @{ exitCode = $delete.ExitCode; cloneGuid = $Manifest.clone.guid }
                if ($delete.ExitCode -ne 0) { $cleanupFailures.Add("Clone deletion failed: $($delete.StdErr)") }
                $verifyDelete = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', $Manifest.clone.guid, '/v')
                if ($verifyDelete.ExitCode -eq 0) { $cleanupFailures.Add('Clone still exists after prepare rollback deletion.') }
                }
            } catch {
                $cleanupFailures.Add("Clone ownership changed after watchdog cleanup; deletion was refused: $($_.Exception.Message)")
            }
        } elseif ($cleanupSafe) {
            Add-SBMSJournalEntry $RunDirectory 'PrepareRollbackResult' @{ exitCode = $null; cloneGuid = $null; note = 'No uniquely owned clone was present.' }
        }
        if ($cleanupFailures.Count -gt 0) {
            $Manifest.state = 'RecoveryRequired'
            $Manifest.lastError = "$failureMessage Cleanup failures: $($cleanupFailures -join ' | ')"
            Save-SBMSManifest $Manifest $RunDirectory
            Add-SBMSJournalEntry $RunDirectory 'RecoveryRequired' @{ originalFailure = $failureMessage; cleanupFailures = @($cleanupFailures) }
            try { Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter } catch {}
            throw $Manifest.lastError
        }
        throw $failureMessage
    }
}

function Invoke-SBMSArm {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    if ($Manifest.state -eq 'Armed') {
        $state = Get-SBMSBcdState -Adapter $Adapter
        if (-not ($state.bootSequence -contains $Manifest.clone.guid)) { throw 'Manifest says Armed but bootsequence read-back disagrees.' }
        return $Manifest
    }
    if ($Manifest.state -ne 'Prepared') { throw "Arm requires Prepared state, found $($Manifest.state)." }
    if (-not $Adapter.ContainsKey('TestRunDirectorySecurity') -or -not (Test-SBMSRunDirectorySecurityResult -Result (& $Adapter.TestRunDirectorySecurity $RunDirectory))) {
        throw 'Arm requires a locked run directory whose ACL read-back allows only SYSTEM and Administrators.'
    }
    if ($Manifest.watchdogPlan.status -ne 'InstalledAndVerified' -or -not (Test-SBMSWatchdogReadback -Adapter $Adapter -Specification $Manifest.watchdogPlan)) {
        throw 'Arm requires an installed watchdog whose task action, SYSTEM identity, runId and script hash pass read-back.'
    }
    $before = Get-SBMSBcdState -Adapter $Adapter
    Test-SBMSBaselineInvariant -Baseline $Manifest.baseline -Current $before
    if (-not (Test-SBMSCloneReadback -Adapter $Adapter -CloneGuid $Manifest.clone.guid -Description $Manifest.clone.description -Profile $Manifest.profile)) { throw 'Clone read-back failed before Arm.' }
    $Manifest.watchdogPlan.status = 'OperatorConfirmed'
    $Manifest.state = 'BootSequenceArmIntent'; Save-SBMSManifest $Manifest $RunDirectory
    Add-SBMSJournalEntry $RunDirectory 'BootSequenceArmIntent' @{ cloneGuid = $Manifest.clone.guid }
    $arm = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/bootsequence', $Manifest.clone.guid)
    if ($arm.ExitCode -ne 0) { throw "Arming one-time bootsequence failed: $($arm.StdErr)" }
    $after = Get-SBMSBcdState -Adapter $Adapter
    Test-SBMSBaselineInvariant -Baseline $Manifest.baseline -Current $after
    if ((@($after.bootSequence).Count -ne 1) -or -not ($after.bootSequence -contains $Manifest.clone.guid)) { throw 'One-time bootsequence read-back failed.' }
    $Manifest.state = 'Armed'; Save-SBMSManifest $Manifest $RunDirectory
    Add-SBMSJournalEntry $RunDirectory 'Armed' @{ cloneGuid = $Manifest.clone.guid; automaticRestart = $false }
    Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter
    return $Manifest
}

function Invoke-SBMSRollback {
    param($Manifest, [string]$RunDirectory, [hashtable]$Adapter)
    if ($Manifest.state -eq 'Cleaned') { return $Manifest }
    $cloneGuid = [string]$Manifest.clone.guid
    if ([string]::IsNullOrWhiteSpace($cloneGuid)) {
        $ownedCandidates = @(Resolve-SBMSOwnedCloneGuid -Manifest $Manifest -Adapter $Adapter)
        if ($ownedCandidates.Count -eq 1) {
            $cloneGuid = $ownedCandidates[0]
            $Manifest.clone.guid = $cloneGuid
            Save-SBMSManifest $Manifest $RunDirectory
            Add-SBMSJournalEntry $RunDirectory 'CloneOwnershipRecovered' @{ cloneGuid = $cloneGuid; phase = 'Rollback' }
        } else {
            $Manifest.state = 'RecoveryRequired'
            $Manifest.lastError = if ($ownedCandidates.Count -eq 0) {
                'Rollback cannot prove whether a clone exists because the manifest clone GUID is empty and exact-description reconciliation found none.'
            } else {
                "Rollback found $($ownedCandidates.Count) exact-description clones and cannot choose one safely."
            }
            Save-SBMSManifest $Manifest $RunDirectory
            Add-SBMSJournalEntry $RunDirectory 'RecoveryRequired' @{ reason = $Manifest.lastError; candidateGuids = @($ownedCandidates) }
            throw $Manifest.lastError
        }
    }
    $state = Get-SBMSBcdState -Adapter $Adapter
    if ($state.currentGuid -eq $cloneGuid) {
        $Manifest.state = 'RollbackPendingDefault'; Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'RollbackPendingDefault' @{ reason = 'Clone is the current loader; no automatic restart or deletion performed.' }
        throw 'Currently booted from the lab clone. Reboot manually to the unchanged default entry, then run Rollback again.'
    }
    if (Test-SBMSCurrentTestSigning -BcdState $state) {
        throw 'The active non-clone loader still has Test Signing enabled. Refusing watchdog and clone cleanup.'
    }
    try {
        $clonePresent = Resolve-SBMSCloneCleanupPresence -Manifest $Manifest -Adapter $Adapter
    } catch {
        $Manifest.state = 'RecoveryRequired'
        $Manifest.lastError = "Rollback cleanup safety gate failed before bootsequence/task mutation: $($_.Exception.Message)"
        Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'RecoveryRequired' @{ reason = $Manifest.lastError; cloneGuid = $cloneGuid }
        throw $Manifest.lastError
    }
    if ($state.bootSequence -contains $cloneGuid) {
        $Manifest.state = 'BootSequenceRemoveIntent'; Save-SBMSManifest $Manifest $RunDirectory
        $remove = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/bootsequence', $cloneGuid, '/remove')
        if ($remove.ExitCode -ne 0) { throw "Could not remove clone from bootsequence: $($remove.StdErr)" }
        $readback = Get-SBMSBcdState -Adapter $Adapter
        if ($readback.bootSequence -contains $cloneGuid) { throw 'bootsequence removal read-back failed.' }
    }
    Remove-SBMSWatchdog -Manifest $Manifest -RunDirectory $RunDirectory -Adapter $Adapter
    if ($clonePresent) {
        if (-not (Resolve-SBMSCloneCleanupPresence -Manifest $Manifest -Adapter $Adapter)) { throw 'Clone disappeared after task cleanup; deletion state is inconsistent.' }
        $Manifest.state = 'CloneDeleteIntent'; Save-SBMSManifest $Manifest $RunDirectory
        $delete = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/delete', $cloneGuid)
        if ($delete.ExitCode -ne 0) { throw "Exact clone deletion failed: $($delete.StdErr)" }
        $verify = Invoke-SBMSAdapterBcd -Adapter $Adapter -Arguments @('/enum', $cloneGuid, '/v')
        if ($verify.ExitCode -eq 0) { throw 'Clone still exists after deletion.' }
    }
    $after = Get-SBMSBcdState -Adapter $Adapter
    Test-SBMSBaselineInvariant -Baseline $Manifest.baseline -Current $after
    if ($after.bootSequence -contains $cloneGuid) { throw 'Cleanup invariant failed: clone remains in bootsequence.' }
    $Manifest.state = 'Cleaned'; Save-SBMSManifest $Manifest $RunDirectory
    Add-SBMSJournalEntry $RunDirectory 'Cleaned' @{ cloneGuid = $cloneGuid }
    Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter
    return $Manifest
}

function Invoke-SBMSHardwareLabWatchdog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][string]$TaskName,
        [ValidateSet('RecoveryDrill', 'TestSigning')][string]$Profile = 'RecoveryDrill',
        [switch]$Execute,
        [Parameter(Mandatory = $true)][string]$Acknowledgement,
        [hashtable]$Adapter = (New-SBMSHardwareLabAdapter)
    )
    $manifest = Read-SBMSManifest -RunDirectory $RunDirectory
    if ([string]$manifest.runId -cne $RunId -or [string]$manifest.profile -cne $Profile -or [string]$manifest.watchdogPlan.taskName -cne $TaskName) {
        throw 'Watchdog immutable identity does not match the manifest.'
    }
    $mutex = New-Object Threading.Mutex($false, ('Global\SBMSHardwareLab_' + $RunId.Replace('-', '')))
    if (-not $mutex.WaitOne([TimeSpan]::FromSeconds(30))) { throw 'Watchdog timed out waiting for the run transaction lock.' }
    try {
        $manifest = Read-SBMSManifest -RunDirectory $RunDirectory
        $expected = "SBMS-HARDWARE-LAB-WATCHDOG/$RunId/$Profile"
        if (-not $Execute -or $Acknowledgement -cne $expected) {
            return [pscustomobject]@{ action = 'AuditOnly'; restartRequested = $false; reason = 'Execute and exact watchdog acknowledgement are required.' }
        }
        if ($manifest.watchdogPlan.status -notin @('InstalledAndVerified', 'OperatorConfirmed') -or $manifest.state -notin @('Armed', 'BootSequenceArmIntent', 'WatchdogRestartIntentPersisted')) {
            Add-SBMSJournalEntry $RunDirectory 'WatchdogNoAction' @{ reason = 'Run is not armed.' }
            return [pscustomobject]@{ action = 'NoAction'; restartRequested = $false; reason = 'Run is not armed.' }
        }
        $state = Get-SBMSBcdState -Adapter $Adapter
        $isClone = ($state.currentGuid -eq [string]$manifest.clone.guid)
        $testSigning = Test-SBMSCurrentTestSigning -BcdState $state
        if (-not $isClone) {
            Add-SBMSJournalEntry $RunDirectory 'WatchdogDefaultObserved' @{ currentGuid = $state.currentGuid; isClone = $isClone; testSigning = $testSigning }
            return [pscustomobject]@{ action = 'NoAction'; restartRequested = $false; reason = 'Active loader is not the exact lab clone.'; testSigning = $testSigning }
        }
        $terminalMarker = Join-Path $RunDirectory 'watchdog-restart.requested'
        if (Test-Path -LiteralPath $terminalMarker -PathType Leaf) {
            return [pscustomobject]@{ action = 'NoAction'; restartRequested = $false; reason = 'A terminal restart-requested marker already exists.' }
        }
        if (-not $Adapter.ContainsKey('RequestRestart')) { throw 'Adapter does not implement RequestRestart.' }
        if (-not $Adapter.ContainsKey('DisableWatchdog')) { throw 'Adapter does not implement DisableWatchdog.' }
        $scriptHashValid = ((Get-SBMSFileSha256 -LiteralPath $manifest.watchdogPlan.scriptPath) -eq $manifest.watchdogPlan.scriptSha256)
        $moduleHashValid = ((Get-SBMSFileSha256 -LiteralPath $manifest.watchdogPlan.modulePath) -eq $manifest.watchdogPlan.moduleSha256)
        Add-SBMSJournalEntry $RunDirectory 'WatchdogRestartIntent' @{
            cloneGuid = $manifest.clone.guid
            testSigning = $testSigning
            scriptHashValid = $scriptHashValid
            moduleHashValid = $moduleHashValid
        }
        $intentMarker = Join-Path $RunDirectory 'watchdog-restart.intent'
        try {
            $intentStream = New-Object IO.FileStream($intentMarker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $intentBytes = $script:Utf8NoBom.GetBytes((Get-SBMSUtcTimestamp) + "`n")
                $intentStream.Write($intentBytes, 0, $intentBytes.Length)
                $intentStream.Flush($true)
            } finally { $intentStream.Dispose() }
        } catch [IO.IOException] {}
        $Manifest = $manifest
        $Manifest.state = 'WatchdogRestartIntentPersisted'
        Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'WatchdogRestartIntentPersisted' @{ taskName = $TaskName }
        Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter
        $result = & $Adapter.RequestRestart
        if ($null -eq $result -or $result.ExitCode -ne 0) { throw "Watchdog restart request failed: $($result.StdErr)" }
        try {
            $terminalStream = New-Object IO.FileStream($terminalMarker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $terminalBytes = $script:Utf8NoBom.GetBytes((Get-SBMSUtcTimestamp) + "`n")
                $terminalStream.Write($terminalBytes, 0, $terminalBytes.Length)
                $terminalStream.Flush($true)
            } finally { $terminalStream.Dispose() }
        } catch [IO.IOException] {}
        $disable = & $Adapter.DisableWatchdog $TaskName
        $disableSucceeded = ($null -ne $disable -and $disable.ExitCode -eq 0)
        Add-SBMSJournalEntry $RunDirectory 'WatchdogSelfDisableResult' @{ taskName = $TaskName; success = $disableSucceeded; exitCode = if ($null -ne $disable) { $disable.ExitCode } else { $null } }
        $Manifest.state = 'WatchdogRestartRequested'; Save-SBMSManifest $Manifest $RunDirectory
        Add-SBMSJournalEntry $RunDirectory 'WatchdogRestartRequested' @{ exitCode = $result.ExitCode }
        Set-SBMSRunDirectorySecurity -RunDirectory $RunDirectory -Adapter $Adapter
        return [pscustomobject]@{
            action = 'RestartRequested'
            restartRequested = $true
            cloneGuid = $manifest.clone.guid
            testSigning = $testSigning
            scriptHashValid = $scriptHashValid
            moduleHashValid = $moduleHashValid
        }
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

function Invoke-SBMSHardwareLab {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [ValidateSet('Audit', 'Prepare', 'Arm', 'Rollback')][string]$Phase = 'Audit',
        [ValidateSet('RecoveryDrill', 'TestSigning')][string]$Profile = 'RecoveryDrill',
        [string]$RunId = ([guid]::NewGuid().ToString()),
        [string]$RunRoot = 'C:\ProgramData\SBMSLab\Runs',
        [switch]$Execute,
        [string]$Acknowledgement,
        [ValidateRange(3, 30)][int]$WatchdogTimeoutMinutes = 8,
        [hashtable]$Adapter = (New-SBMSHardwareLabAdapter)
    )
    $parsedRunId = ([guid]$RunId).ToString()
    $root = [IO.Path]::GetFullPath($RunRoot)
    $runDirectory = [IO.Path]::GetFullPath((Join-Path $root $parsedRunId))
    if (-not $runDirectory.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Run directory escaped RunRoot.' }
    if ($WhatIfPreference -and $Phase -ne 'Audit') {
        return [pscustomobject]@{
            phase = $Phase
            profile = $Profile
            runId = $parsedRunId
            state = 'WhatIf'
            runDirectory = $runDirectory
            mutationPerformed = $false
            automaticRestart = $false
        }
    }
    if ($Execute -and $Adapter.ContainsKey('IsReal') -and $Adapter.IsReal) {
        $requiredRoot = [IO.Path]::GetFullPath('C:\ProgramData\SBMSLab\Runs')
        if (-not $root.TrimEnd('\').Equals($requiredRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Real mutation is restricted to the protected run root: $requiredRoot"
        }
        if ($Profile -eq 'TestSigning' -and $Phase -in @('Prepare', 'Arm')) {
            throw 'Real TestSigning Prepare/Arm is blocked until Gate A, SSH recovery, and BitLocker recovery-key proofs are implemented and verified.'
        }
    }
    if (-not (Test-Path -LiteralPath $runDirectory)) { New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null }
    $mutex = New-Object Threading.Mutex($false, ('Global\SBMSHardwareLab_' + $parsedRunId.Replace('-', '')))
    if (-not $mutex.WaitOne([TimeSpan]::FromSeconds(30))) { throw 'Timed out waiting for the run transaction lock.' }
    try {
        $manifestPath = Join-Path $runDirectory 'manifest.json'
        if (Test-Path -LiteralPath $manifestPath) {
            $manifest = Read-SBMSManifest -RunDirectory $runDirectory
            if ([string]$manifest.runId -ne $parsedRunId) { throw 'Manifest runId mismatch.' }
            if ([int]$manifest.schemaVersion -ne 2 -or [string]$manifest.profile -cne $Profile) { throw 'Manifest schema/profile mismatch; profile is immutable for a run.' }
        } else {
            $snapshot = Get-SBMSHardwareLabSnapshot -Adapter $Adapter -RunDirectory $runDirectory
            $manifest = New-SBMSRunManifest -RunId $parsedRunId -RunDirectory $runDirectory -Baseline $snapshot.bcd -WatchdogTimeoutMinutes $WatchdogTimeoutMinutes -Profile $Profile
            $manifest.state = 'SnapshotComplete'; Save-SBMSManifest $manifest $runDirectory
            Add-SBMSJournalEntry $runDirectory 'SnapshotComplete' @{ bcdAllSha256 = $snapshot.bcdAllSha256 }
        }
        if ($Phase -eq 'Audit') {
            $current = Get-SBMSBcdState -Adapter $Adapter
            return [pscustomobject]@{ phase = 'Audit'; profile = $Profile; runId = $parsedRunId; state = $manifest.state; runDirectory = $runDirectory; baseline = $manifest.baseline; current = $current; mutationPerformed = $false }
        }
        $expectedAck = "SBMS-HARDWARE-LAB/$parsedRunId/$Profile/$Phase"
        Assert-SBMSMutationAuthorized -Adapter $Adapter -Execute ([bool]$Execute) -Acknowledgement $Acknowledgement -ExpectedAcknowledgement $expectedAck
        if (-not $PSCmdlet.ShouldProcess("BCD lab run $parsedRunId", $Phase)) { return }
        try {
            switch ($Phase) {
                'Prepare' { $manifest = Invoke-SBMSPrepare -Manifest $manifest -RunDirectory $runDirectory -Adapter $Adapter }
                'Arm' { $manifest = Invoke-SBMSArm -Manifest $manifest -RunDirectory $runDirectory -Adapter $Adapter }
                'Rollback' { $manifest = Invoke-SBMSRollback -Manifest $manifest -RunDirectory $runDirectory -Adapter $Adapter }
            }
        } catch {
            $phaseFailure = $_.Exception.Message
            $manifest.lastError = $phaseFailure
            if ($manifest.state -notin @('RollbackPendingDefault', 'BootSequenceArmIntent', 'CloneDeleteIntent', 'RecoveryRequired')) { $manifest.state = 'Failed' }
            Save-SBMSManifest $manifest $runDirectory
            Add-SBMSJournalEntry $runDirectory 'Failed' @{ phase = $Phase; message = $phaseFailure }
            try {
                Set-SBMSRunDirectorySecurity -RunDirectory $runDirectory -Adapter $Adapter
            } catch {
                $securityFailure = $_.Exception.Message
                $manifest.state = 'RecoveryRequired'
                $manifest.lastError = "$phaseFailure Run-directory security recovery failed: $securityFailure"
                Save-SBMSManifest $manifest $runDirectory
                throw $manifest.lastError
            }
            throw $phaseFailure
        }
        return [pscustomobject]@{ phase = $Phase; profile = $Profile; runId = $parsedRunId; state = $manifest.state; runDirectory = $runDirectory; cloneGuid = $manifest.clone.guid; automaticRestart = $false; mutationPerformed = $true }
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

Export-ModuleMember -Function New-SBMSHardwareLabAdapter, Get-SBMSHardwareLabSnapshot, Invoke-SBMSHardwareLab, Invoke-SBMSHardwareLabWatchdog
