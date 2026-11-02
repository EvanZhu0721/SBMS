$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$buildScript = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\build-installer.ps1')
)

Describe 'Installer build entry point contract' {
    BeforeAll {
        $script:command = Get-Command -Name $buildScript
    }

    It 'requires a certificate unless unsigned packaging is explicitly selected' {
        $signed = @($script:command.ParameterSets |
            Where-Object Name -eq 'Signed')

        $signed.Count | Should Be 1
        ($signed[0].Parameters |
            Where-Object Name -eq 'SigningCertificateThumbprint').IsMandatory |
            Should Be $true
        @($signed[0].Parameters |
            Where-Object Name -eq 'Unsigned').Count | Should Be 0

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

}
