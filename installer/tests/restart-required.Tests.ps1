$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Describe 'Driver restart propagation' {
    It 'returns native result <NativeExit> through the maintenance process' -TestCases @(
        @{ NativeExit = 0; ExpectedExit = 0; Deferred = 'False' }
        @{ NativeExit = 3010; ExpectedExit = 3010; Deferred = 'True' }
        @{ NativeExit = 5; ExpectedExit = 1; Deferred = '' }
    ) {
        param($NativeExit, $ExpectedExit, $Deferred)

        $maintenance = [IO.File]::ReadAllText(
            (Join-Path $PSScriptRoot '..\maintenance.ps1'), [Text.Encoding]::UTF8)
        $dispatchIndex = $maintenance.LastIndexOf('switch ($Action) {', [StringComparison]::Ordinal)
        $dispatchIndex | Should BeGreaterThan 0
        $probeRoot = Join-Path $TestDrive "native-$NativeExit"
        New-Item -ItemType Directory -Path (Join-Path $probeRoot 'driver') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $probeRoot 'driver\SBMSIndirectDisplay.inf') -Value '' -Encoding UTF8
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\driver-trust.ps1') -Destination $probeRoot
        # All machine mutations are replaced. The driver helper, ref result,
        # orchestration and dispatcher remain the production implementations.
        # pnputil's result is injected via a real native PowerShell subprocess.
        $stubs = @'
function Assert-InstallIdentity {}
function Get-OwnedTaskSnapshots { @() }
function Remove-SbmsTask {}
function Find-SbmsDriverPackages { @() }
function Find-ObsoleteSbmsDriverPackages { @() }
function Remove-DriverPackages { param($Packages) }
function Install-SbmsTask {
    param([switch]$DeferStart)
    Write-Output "DEFERRED=$DeferStart"
}
function pnputil.exe {
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command 'exit NATIVE_EXIT'
    $global:LASTEXITCODE = $LASTEXITCODE
}
'@
        $stubs = $stubs.Replace('NATIVE_EXIT', [string]$NativeExit)
        $probe = Join-Path $probeRoot 'maintenance-probe.ps1'
        [IO.File]::WriteAllText($probe,
            $maintenance.Substring(0, $dispatchIndex) + $stubs + "`r`n" +
            $maintenance.Substring($dispatchIndex), [Text.Encoding]::UTF8)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $start.Arguments = '-NoLogo -NoProfile -NonInteractive -File "' + $probe + '" -Action Install -InstallRoot "' + $probeRoot + '"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEnd()
            $stderr = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            $process.ExitCode | Should Be $ExpectedExit
            if ($NativeExit -eq 5) {
                $stderr | Should Match 'pnputil add-driver failed with exit code 5'
                $stdout | Should Not Match 'DEFERRED='
            }
            else {
                $stderr | Should BeNullOrEmpty
                $stdout | Should Match "DEFERRED=$Deferred"
            }
        }
        finally {
            $process.Dispose()
        }
    }

    It 'treats maintenance 3010 as success with an Inno restart request' {
        $source = [IO.File]::ReadAllText(
            (Join-Path $PSScriptRoot '..\SBMS.iss'), [Text.Encoding]::UTF8)
        $source | Should Match 'AfterInstall: InstallSbms'
        $source | Should Match 'else if ResultCode = 3010 then\s+DriverRestartRequired := True'
        $source | Should Match 'function NeedRestart\(\): Boolean;\s+begin\s+Result := DriverRestartRequired;'
        $source | Should Not Match 'if CurStep = ssPostInstall then'
    }
}
