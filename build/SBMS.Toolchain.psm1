Set-StrictMode -Version 2.0

function Resolve-SBMSVsDevCmd {
    [CmdletBinding()]
    param(
        [string] $ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = [IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Explicit VsDevCmd path does not exist: $resolved"
        }
        return $resolved
    }

    if (-not [string]::IsNullOrWhiteSpace($env:VSINSTALLDIR)) {
        $candidate = Join-Path $env:VSINSTALLDIR 'Common7\Tools\VsDevCmd.bat'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $installPath = & $vswhere `
            -latest `
            -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installPath)) {
            $candidate = Join-Path ([string]$installPath) 'Common7\Tools\VsDevCmd.bat'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Professional\Common7\Tools\VsDevCmd.bat'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat'),
        'C:\BuildTools\Common7\Tools\VsDevCmd.bat'
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'Visual Studio C++ toolchain was not found. Install the VS 2022 x64 C++ workload or pass -VsDevCmdPath.'
}

Export-ModuleMember -Function Resolve-SBMSVsDevCmd
