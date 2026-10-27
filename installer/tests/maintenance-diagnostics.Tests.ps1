$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Describe 'Maintenance failure diagnostics' {
    It 'keeps the maintenance result with a blocked log = <BlockedLog>' -TestCases @(
        @{ BlockedLog = $false; ExpectedExit = 1 }
        @{ BlockedLog = $true; ExpectedExit = 0 }
    ) {
        param($BlockedLog, $ExpectedExit)
        $path = Join-Path $PSScriptRoot '..\maintenance.ps1'
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            $path, [ref]$tokens, [ref]$errors)
        $errors.Count | Should Be 0
        $function = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Invoke-MaintenanceAction'
        }, $true)
        $function | Should Not BeNullOrEmpty
        $probe = Join-Path $TestDrive 'diagnostic-probe.ps1'
        # Execute only the exception wrapper, never the real maintenance action.
        $maintenance = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        $source = $maintenance.Substring(0, $maintenance.LastIndexOf('switch ($Action) {')) +
            "`r`n" + 'Invoke-MaintenanceAction { & pnputil.exe /?; OPERATION }'
        $operation = if ($BlockedLog) { '[Console]::Out.WriteLine(''Operation completed'')' }
            else { 'throw ''用户身份无效: 0x800B0109''' }
        $source = $source.Replace('OPERATION', $operation)
        [IO.File]::WriteAllText($probe, $source, [Text.Encoding]::UTF8)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $detail = Join-Path $TestDrive "details-$BlockedLog.log"
        if ($BlockedLog) { New-Item -ItemType Directory -Path $detail | Out-Null }
        $start.Arguments = '-NoLogo -NoProfile -NonInteractive -File "' + $probe +
            '" -Action Install -InstallRoot "' + $TestDrive + '" -DiagnosticLogPath "' + $detail + '"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardError = $true
        $start.RedirectStandardOutput = $true
        $start.StandardErrorEncoding = [Text.Encoding]::UTF8
        $start.StandardOutputEncoding = [Text.Encoding]::UTF8
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            $stdout = $stdoutTask.Result
            $process.ExitCode | Should Be $ExpectedExit
            $stdout | Should Match '/add-driver'
            $stdout | Should Not Match ([string][char]0xfffd)
            if ($BlockedLog) {
                $stderr | Should Match 'Detailed log write failed:'
                $stdout | Should Match 'Operation completed'
                return
            }
            $stderr | Should Match 'SBMS Install failed: 用户身份无效: 0x800B0109'
            $stdout | Should Match 'diagnostic-probe.ps1'
            $logged = [IO.File]::ReadAllText($detail, [Text.Encoding]::UTF8)
            $logged | Should Match '用户身份无效: 0x800B0109'
            $logged | Should Match 'HResult=0x'
            $logged | Should Match 'ErrorId='
            $logged | Should Match 'SID=S-1-'
            $logged | Should Match 'PS=5.1'
        }
        finally {
            $process.Dispose()
        }
    }

    It 'captures the PowerShell process output and points the dialog to a persistent log' {
        $source = [IO.File]::ReadAllText(
            (Join-Path $PSScriptRoot '..\SBMS.iss'), [Text.Encoding]::UTF8)
        $source | Should Match 'ExecAndLogOutput\('
        $source | Should Match '@LogMaintenanceOutput'
        $source | Should Match '\{commonappdata\}\\SBMS\\logs\\setup-'
        $source | Should Match '''Details: '' \+ MaintenanceLogPath'
        $source | Should Match '-NonInteractive -ExecutionPolicy Bypass -File'
        $source | Should Not Match 'Set-ExecutionPolicy'
    }
}
