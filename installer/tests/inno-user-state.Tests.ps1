$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installerScript = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\SBMS.iss')
)

function Get-InnoSection {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $match = [regex]::Match(
        $Source,
        "(?ms)^\[$([regex]::Escape($Name))\]\s*(?<body>.*?)(?=^\[|\z)"
    )
    if (-not $match.Success) {
        throw "Inno Setup section [$Name] was not found."
    }
    $match.Groups['body'].Value
}

Describe 'Inno Setup user-state lifecycle contract' {
    BeforeAll {
        $script:source = [IO.File]::ReadAllText(
            $installerScript,
            [Text.Encoding]::UTF8
        )
    }

    It 'does not delete LocalAppData SBMS state during install or upgrade' {
        $installDelete = Get-InnoSection `
            -Source $script:source `
            -Name 'InstallDelete'
        $activeEntries = @(
            $installDelete -split "`r?`n" |
                Where-Object { $_ -notmatch '^\s*;' }
        ) -join "`n"

        $activeEntries | Should Not Match '(?i)\{localappdata\}\\SBMS'
    }

    It 'deletes the complete LocalAppData SBMS state on deliberate uninstall' {
        $uninstallDelete = Get-InnoSection `
            -Source $script:source `
            -Name 'UninstallDelete'
        $stateDeletes = @(
            [regex]::Matches(
                $uninstallDelete,
                '(?im)^\s*Type:\s*filesandordirs;\s*Name:\s*"\{localappdata\}\\SBMS"\s*$'
            )
        )

        $stateDeletes.Count | Should Be 1
    }

    It 'prepares an existing installation before setup overwrites files' {
        $code = Get-InnoSection -Source $script:source -Name 'Code'

        $code | Should Match '(?s)function\s+PrepareToInstall\b.*?DirExists\(ExpandConstant\(''\{app\}''\)\).*?ExtractTemporaryFile\(''sbms-maintenance\.ps1''\).*?''PrepareUpgrade'''
    }

    It 'gates uninstall before changes and runs external cleanup at usUninstall' {
        $code = Get-InnoSection -Source $script:source -Name 'Code'

        $code | Should Match '(?s)function\s+InitializeUninstall\b.*?RunMaintenance\(''PreflightUninstall'''
        $code | Should Match '(?s)procedure\s+CurUninstallStepChanged\b.*?CurUninstallStep\s*=\s*usUninstall.*?RunMaintenance\(''Uninstall'''
    }
}
