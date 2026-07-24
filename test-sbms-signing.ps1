$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$ModulePath = Join-Path $Root 'build\SBMS.Signing.psm1'
Import-Module $ModulePath -Force

$script:Passed = 0
$script:Failed = 0

function Invoke-Test {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Action
    )

    try {
        & $Action
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name :: $($_.Exception.Message)"
    }
}

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock] $Action, [string] $Pattern)
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if (-not $caught) { throw "Expected an exception matching '$Pattern'." }
    if ($caught.Exception.Message -notlike "*$Pattern*") {
        throw "Exception '$($caught.Exception.Message)' did not match '$Pattern'."
    }
}

function New-ValidPolicyObject {
    [pscustomobject]@{
        schemaVersion = 1
        profile = 'Production'
        publisher = [pscustomobject]@{
            subject = 'CN=SBMS Release Test'
            thumbprint = '1111111111111111111111111111111111111111'
            storeLocation = 'CurrentUser'
            storeName = 'My'
        }
        timestamp = [pscustomobject]@{
            required = $true
            protocol = 'RFC3161'
            digest = 'SHA256'
            url = 'https://timestamp.example.invalid'
        }
        driverCertification = [pscustomobject]@{
            method = 'WHQL'
            allowedCatalogSubjects = @(
                'CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
            )
        }
        integrity = [pscustomobject]@{
            hashAlgorithm = 'SHA256'
            catalogVersion = '2.0'
        }
        sbom = [pscustomobject]@{
            format = 'SPDX'
            specVersion = '2.2'
        }
    }
}

function Write-PolicyFixture {
    param([psobject] $Policy)
    $path = Join-Path $env:TEMP ("sbms-signing-policy-{0}.json" -f [guid]::NewGuid())
    [IO.File]::WriteAllText(
        $path,
        (($Policy | ConvertTo-Json -Depth 10) -replace "`r`n", "`n"),
        (New-Object Text.UTF8Encoding($false))
    )
    $path
}

$fixtureFiles = New-Object System.Collections.Generic.List[string]
try {
    Invoke-Test 'valid production policy loads and normalizes thumbprint' {
        $path = Write-PolicyFixture (New-ValidPolicyObject)
        $fixtureFiles.Add($path)
        $policy = Read-SBMSSigningPolicy -LiteralPath $path
        Assert-True ($policy.publisher.thumbprint -ceq '1111111111111111111111111111111111111111') 'thumbprint changed unexpectedly'
    }

    Invoke-Test 'repository policy template is deliberately rejected' {
        Assert-Throws {
            Read-SBMSSigningPolicy -LiteralPath (Join-Path $Root 'build\signing-policy.template.json')
        } 'placeholder'
    }

    Invoke-Test 'automatic or malformed certificate identity is rejected' {
        $policy = New-ValidPolicyObject
        $policy.publisher.thumbprint = '1234'
        $path = Write-PolicyFixture $policy
        $fixtureFiles.Add($path)
        Assert-Throws { Read-SBMSSigningPolicy -LiteralPath $path } '40 hexadecimal'
    }

    Invoke-Test 'non-HTTPS timestamp service is rejected' {
        $policy = New-ValidPolicyObject
        $policy.timestamp.url = 'http://timestamp.example.invalid'
        $path = Write-PolicyFixture $policy
        $fixtureFiles.Add($path)
        Assert-Throws { Read-SBMSSigningPolicy -LiteralPath $path } 'absolute HTTPS'
    }

    Invoke-Test 'attestation cannot satisfy production driver policy' {
        $policy = New-ValidPolicyObject
        $policy.driverCertification.method = 'Attestation'
        $path = Write-PolicyFixture $policy
        $fixtureFiles.Add($path)
        Assert-Throws { Read-SBMSSigningPolicy -LiteralPath $path } 'exactly'
    }

    Invoke-Test 'SignTool warning exit code is fail-closed' {
        Assert-Throws {
            Invoke-SBMSSignTool `
                -SignToolPath 'signtool.exe' `
                -ArgumentList @('verify', 'fixture.exe') `
                -ToolInvoker {
                    param($Tool, $Arguments)
                    [pscustomobject]@{ ExitCode = 2; Output = 'warning: not timestamped' }
                }
        } 'exit 2'
    }

    Invoke-Test 'production sign arguments pin certificate SHA256 and RFC3161' {
        $inputPath = Join-Path $env:TEMP ("sbms-sign-input-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($inputPath, [byte[]](1, 2, 3))
        $fixtureFiles.Add($inputPath)
        $toolPath = Join-Path $env:TEMP ("signtool-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($toolPath, [byte[]](0))
        $fixtureFiles.Add($toolPath)
        $script:CapturedArguments = $null
        $null = Invoke-SBMSSignAuthenticode `
            -LiteralPath $inputPath `
            -Policy (New-ValidPolicyObject) `
            -SignToolPath $toolPath `
            -ToolInvoker {
                param($Tool, $Arguments)
                $script:CapturedArguments = @($Arguments)
                [pscustomobject]@{ ExitCode = 0; Output = 'ok' }
            }
        $joined = $script:CapturedArguments -join '|'
        Assert-True ($joined -like 'sign|/fd|SHA256|/sha1|1111111111111111111111111111111111111111*') 'explicit certificate or SHA256 digest missing'
        Assert-True ($joined -like '*/tr|https://timestamp.example.invalid|/td|SHA256*') 'RFC3161 SHA256 timestamp arguments missing'
        Assert-True ($joined -notlike '*/a*') 'automatic certificate selection was used'
    }

    Invoke-Test 'valid signature from wrong publisher is rejected' {
        $inputPath = Join-Path $env:TEMP ("sbms-verify-input-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($inputPath, [byte[]](1))
        $fixtureFiles.Add($inputPath)
        $toolPath = Join-Path $env:TEMP ("signtool-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($toolPath, [byte[]](0))
        $fixtureFiles.Add($toolPath)
        $signature = [pscustomobject]@{
            Status = 'Valid'
            SignerCertificate = [pscustomobject]@{
                Thumbprint = '2222222222222222222222222222222222222222'
                Subject = 'CN=Wrong Publisher'
            }
            TimeStamperCertificate = [pscustomobject]@{
                Thumbprint = '3333333333333333333333333333333333333333'
                Subject = 'CN=TSA'
            }
        }
        Assert-Throws {
            Assert-SBMSAuthenticodeSignature `
                -LiteralPath $inputPath `
                -Policy (New-ValidPolicyObject) `
                -SignToolPath $toolPath `
                -Signature $signature `
                -ToolInvoker {
                    param($Tool, $Arguments)
                    [pscustomobject]@{ ExitCode = 0; Output = 'ok' }
                }
        } 'signer mismatch'
    }

    Invoke-Test 'missing timestamp is rejected after trust verification' {
        $inputPath = Join-Path $env:TEMP ("sbms-verify-input-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($inputPath, [byte[]](1))
        $fixtureFiles.Add($inputPath)
        $toolPath = Join-Path $env:TEMP ("signtool-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($toolPath, [byte[]](0))
        $fixtureFiles.Add($toolPath)
        $signature = [pscustomobject]@{
            Status = 'Valid'
            SignerCertificate = [pscustomobject]@{
                Thumbprint = '1111111111111111111111111111111111111111'
                Subject = 'CN=SBMS Release Test'
            }
            TimeStamperCertificate = $null
        }
        Assert-Throws {
            Assert-SBMSAuthenticodeSignature `
                -LiteralPath $inputPath `
                -Policy (New-ValidPolicyObject) `
                -SignToolPath $toolPath `
                -Signature $signature `
                -ToolInvoker {
                    param($Tool, $Arguments)
                    [pscustomobject]@{ ExitCode = 0; Output = 'ok' }
                }
        } 'timestamp is missing'
    }

    Invoke-Test 'WHQL catalog rejects a non-Microsoft signer' {
        $inputPath = Join-Path $env:TEMP ("sbms-driver-catalog-{0}.cat" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($inputPath, [byte[]](1))
        $fixtureFiles.Add($inputPath)
        $toolPath = Join-Path $env:TEMP ("signtool-{0}.exe" -f [guid]::NewGuid())
        [IO.File]::WriteAllBytes($toolPath, [byte[]](0))
        $fixtureFiles.Add($toolPath)
        $signature = [pscustomobject]@{
            Status = 'Valid'
            SignerCertificate = [pscustomobject]@{
                Thumbprint = '2222222222222222222222222222222222222222'
                Subject = 'CN=WDKTestCert'
            }
            TimeStamperCertificate = [pscustomobject]@{
                Subject = 'CN=TSA'
            }
        }
        Assert-Throws {
            Assert-SBMSWhqlCatalog `
                -LiteralPath $inputPath `
                -Policy (New-ValidPolicyObject) `
                -SignToolPath $toolPath `
                -Signature $signature `
                -ToolInvoker {
                    param($Tool, $Arguments)
                    [pscustomobject]@{ ExitCode = 0; Output = 'ok' }
                }
        } 'not allowed'
    }
} finally {
    foreach ($path in $fixtureFiles) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Signing contract: $($script:Passed) passed, $($script:Failed) failed"
if ($script:Failed -ne 0) { exit 1 }
