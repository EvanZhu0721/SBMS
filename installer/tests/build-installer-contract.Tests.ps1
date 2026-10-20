$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$buildScript = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\build-installer.ps1')
)

Describe 'Installer build entry point contract' {
    BeforeAll {
        $script:command = Get-Command -Name $buildScript
        $script:source = [IO.File]::ReadAllText(
            $buildScript,
            [Text.Encoding]::UTF8
        )
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            $buildScript,
            [ref]$tokens,
            [ref]$parseErrors
        )
        @($parseErrors).Count | Should Be 0
    }

    It 'requires a certificate in the default signed release path' {
        $signed = @($script:command.ParameterSets |
            Where-Object Name -eq 'Signed')

        $signed.Count | Should Be 1
        ($signed[0].Parameters |
            Where-Object Name -eq 'SigningCertificateThumbprint').IsMandatory |
            Should Be $true
        @($signed[0].Parameters |
            Where-Object Name -eq 'Unsigned').Count | Should Be 0
    }

    It 'keeps unsigned packaging explicit and on the full build path' {
        $unsigned = @($script:command.ParameterSets |
            Where-Object Name -eq 'Unsigned')

        $unsigned.Count | Should Be 1
        ($unsigned[0].Parameters |
            Where-Object Name -eq 'Unsigned').IsMandatory | Should Be $true
    }

    It 'rejects a false unsigned switch instead of weakening release signing' {
        $failure = $null
        try {
            & $buildScript -Unsigned:$false
        }
        catch {
            $failure = $_
        }

        $failure | Should Not BeNullOrEmpty
        $failure.Exception.Message | Should Match (
            'Unsigned packaging must be explicitly enabled'
        )
    }

    It 'locks Rust and always runs the real driver build' {
        $script:source | Should Match (
            '(?m)cargo\.exe\s+build\s+--release\s+--bins\s+--locked'
        )
        $script:source | Should Match (
            "(?m)&\s+\(Join-Path\s+\`$repository\s+'build-driver\.ps1'\)"
        )
    }
}
