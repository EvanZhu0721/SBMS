[CmdletBinding()]
param(
    [ValidateSet('contracts', 'integration', 'package', 'all')]
    [string] $Suite = 'contracts',

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $PowerShellExecutable,

    [switch] $RequireCleanSource
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PowerShellExecutable)) {
    $PowerShellExecutable = (Get-Process -Id $PID).Path
}
$resolvedPowerShell = (Get-Command $PowerShellExecutable -ErrorAction Stop).Source

$contracts = @(
    'test-sbms-configuration.ps1',
    'test-sbms-gui-core.ps1',
    'test-sbms-process-job.ps1',
    'test-sbms-supervisors.ps1',
    'test-sbms-driver-contract.ps1',
    'test-sbms-driver-migration.ps1',
    'test-sbms-driver-certification.ps1',
    'test-sbms-signing.ps1',
    'test-sbms-gate-a.ps1',
    'test-sbms-hardware-lab.ps1',
    'test-sbms-version.ps1',
    'test-sbms-production-release.ps1',
    'test-sbms-release-evidence.ps1',
    'test-sbms-installer-integrity.ps1',
    'test-sbms-installer-transaction.ps1',
    'test-sbms-ci-contract.ps1'
)
$integration = @(
    'test-sbms-start-gate.ps1',
    'test-sbms-recovery-broker.ps1',
    'test-sbms-gui.ps1'
)
$package = @('test-sbms-package.ps1')

$scripts = switch ($Suite) {
    'contracts' { $contracts }
    'integration' { $integration }
    'package' { $package }
    'all' { $contracts + $package + $integration }
}

function Quote-WindowsArgument {
    param([string] $Value)
    if ($Value -notmatch '[\s"]') {
        return $Value
    }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

$results = New-Object System.Collections.Generic.List[object]
$startedAt = [DateTime]::UtcNow

foreach ($scriptName in $scripts) {
    $scriptPath = Join-Path $Root $scriptName
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Missing CI script: $scriptPath"
    }

    $baseName = [IO.Path]::GetFileNameWithoutExtension($scriptName)
    $logPath = Join-Path $OutputDirectory ($baseName + '.log')
    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $scriptPath
    )
    if ($RequireCleanSource -and $scriptName -eq 'test-sbms-package.ps1') {
        $arguments += '-RequireCleanSource'
    }

    $start = [DateTime]::UtcNow
    $processInfo = New-Object Diagnostics.ProcessStartInfo
    $processInfo.FileName = $resolvedPowerShell
    $processInfo.Arguments = (($arguments | ForEach-Object { Quote-WindowsArgument ([string]$_) }) -join ' ')
    $processInfo.WorkingDirectory = $Root
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $processInfo.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    # ProcessStartInfo otherwise passes PowerShell 7's module path verbatim to
    # Windows PowerShell 5.1, which then cannot auto-load inbox commands such as
    # Microsoft.PowerShell.Utility/Get-FileHash.
    [void]$processInfo.EnvironmentVariables.Remove('PSModulePath')

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $processInfo
    $null = $process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()

    $combined = $stdout
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        $combined += if ($combined.EndsWith("`n")) { '' } else { "`r`n" }
        $combined += "[stderr]`r`n" + $stderr
    }
    [IO.File]::WriteAllText($logPath, $combined, (New-Object Text.UTF8Encoding($false)))
    if (-not [string]::IsNullOrWhiteSpace($combined)) {
        Write-Host $combined.TrimEnd()
    }

    $finished = [DateTime]::UtcNow
    $results.Add([ordered]@{
        name = $baseName
        script = $scriptName
        status = if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }
        exitCode = $exitCode
        startedAtUtc = $start.ToString('o')
        durationMs = [Math]::Round(($finished - $start).TotalMilliseconds)
        log = [IO.Path]::GetFileName($logPath)
        logSha256 = (Get-FileHash -LiteralPath $logPath -Algorithm SHA256).Hash
    })
}

$failed = @($results | Where-Object { $_.status -ne 'PASS' }).Count
$resultArray = $results.ToArray()
$summary = [ordered]@{
    schemaVersion = 1
    suite = $Suite
    status = if ($failed -eq 0) { 'PASS' } else { 'FAIL' }
    repositoryCommit = (& git -C $Root rev-parse HEAD).Trim()
    sourceDirty = @(& git -C $Root status --porcelain --untracked-files=all).Count -gt 0
    runner = [ordered]@{
        machine = $env:COMPUTERNAME
        os = [Environment]::OSVersion.VersionString
        powershell = $resolvedPowerShell
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        childPowerShellVersion = (& $resolvedPowerShell -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()').Trim()
    }
    startedAtUtc = $startedAt.ToString('o')
    finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    total = $results.Count
    passed = $results.Count - $failed
    failed = $failed
    results = $resultArray
}

$summaryPath = Join-Path $OutputDirectory 'summary.json'
[IO.File]::WriteAllText(
    $summaryPath,
    (($summary | ConvertTo-Json -Depth 8) + "`n"),
    (New-Object Text.UTF8Encoding($false)))

$markdown = @(
    "## SBMS CI: $Suite",
    '',
    "- Status: **$($summary.status)**",
    "- PowerShell: ``$($summary.runner.childPowerShellVersion)``",
    "- Passed: $($summary.passed)/$($summary.total)",
    '',
    '| Test | Status | Exit | Duration ms |',
    '| --- | --- | ---: | ---: |'
)
foreach ($result in $results) {
    $markdown += "| $($result.name) | $($result.status) | $($result.exitCode) | $($result.durationMs) |"
}
[IO.File]::WriteAllLines(
    (Join-Path $OutputDirectory 'summary.md'),
    $markdown,
    (New-Object Text.UTF8Encoding($false)))

if ($failed -ne 0) {
    exit 1
}
