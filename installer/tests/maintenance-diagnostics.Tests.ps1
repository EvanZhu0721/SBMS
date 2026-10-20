$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Describe 'Maintenance failure diagnostics' {
    It 'returns a failing process exit code and preserves the original reason on stderr' {
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
        $source = '$Action = ''Install''' + "`r`n" +
            $function.Extent.Text + "`r`n" +
            'Invoke-MaintenanceAction { throw ''Driver trust rejected: 0x800B0109'' }'
        [IO.File]::WriteAllText($probe, $source, [Text.Encoding]::UTF8)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $start.Arguments = '-NoLogo -NoProfile -NonInteractive -File "' + $probe + '"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stderr = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            $process.ExitCode | Should Be 1
            $stderr | Should Match 'SBMS Install failed: Driver trust rejected: 0x800B0109'
            $stderr | Should Match 'diagnostic-probe.ps1'
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
        $source | Should Not Match '-ExecutionPolicy'
    }
}
