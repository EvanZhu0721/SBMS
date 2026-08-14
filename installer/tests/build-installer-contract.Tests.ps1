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
        $hashFunction = $ast.Find(
            {
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Get-Sha256Hex'
            },
            $true
        )
        $hashFunction | Should Not BeNullOrEmpty
        $script:hashFunctionSource = $hashFunction.Extent.Text
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

    It 'computes SHA256 without depending on PowerShell module discovery' {
        . ([scriptblock]::Create($script:hashFunctionSource))
        $inputFile = Join-Path $TestDrive 'sha256-input.txt'
        [IO.File]::WriteAllText(
            $inputFile,
            'abc',
            [Text.UTF8Encoding]::new($false)
        )

        Get-Sha256Hex -LiteralPath $inputFile | Should Be (
            'BA7816BF8F01CFEA414140DE5DAE2223' +
            'B00361A396177A9CB410FF61F20015AD'
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
