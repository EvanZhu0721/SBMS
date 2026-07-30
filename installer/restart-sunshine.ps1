[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$')]
    [string]$DisplayId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles
    )
    $sunshineRoot = Join-Path $programFiles 'Sunshine'
    $configuration = Join-Path $sunshineRoot 'config\sunshine.conf'
    if (-not (Test-Path -LiteralPath $configuration -PathType Leaf)) {
        throw "Sunshine configuration was not found at $configuration"
    }

    $utf8 = [Text.UTF8Encoding]::new($false)
    $original = [IO.File]::ReadAllText($configuration, [Text.Encoding]::UTF8)
    $pattern = '(?m)^[\t ]*output_name[\t ]*=.*$'
    $matches = [regex]::Matches($original, $pattern)
    if ($matches.Count -gt 1) {
        throw 'Sunshine configuration contains more than one output_name entry.'
    }

    $setting = "output_name = $DisplayId"
    if ($matches.Count -eq 1) {
        $updated = [regex]::Replace($original, $pattern, $setting, 1)
    } else {
        $separator = if (
            $original.Length -eq 0 -or
            $original.EndsWith("`n")
        ) { '' } else { [Environment]::NewLine }
        $updated = $original + $separator + $setting + [Environment]::NewLine
    }

    $backup = "$configuration.sbms.bak"
    $temporary = "$configuration.sbms.tmp"
    Copy-Item -LiteralPath $configuration -Destination $backup -Force
    try {
        [IO.File]::WriteAllText($temporary, $updated, $utf8)
        Move-Item -LiteralPath $temporary -Destination $configuration -Force

        $service = Get-Service -Name 'SunshineService' -ErrorAction Stop
        Restart-Service -InputObject $service -Force
        $service.WaitForStatus(
            'Running',
            [TimeSpan]::FromSeconds(15)
        )
    } catch {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
        Copy-Item -LiteralPath $backup -Destination $configuration -Force
        try {
            Restart-Service -Name 'SunshineService' -Force
        } catch {
            # Preserve the original failure below; recovery is best-effort.
        }
        throw
    }

    $logDirectory = Join-Path $env:LOCALAPPDATA 'SBMS\logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $timestamp = [DateTimeOffset]::Now.ToString('o')
    Add-Content -LiteralPath (Join-Path $logDirectory 'sunshine-actions.log') `
        -Value "$timestamp output_name=$DisplayId service=Running" `
        -Encoding UTF8
} catch {
    $message = "SBMS could not restart Sunshine.`n`n$($_.Exception.Message)"
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        $message,
        'SBMS · Sunshine',
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Error
    ) | Out-Null
    exit 1
}
