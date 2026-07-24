Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failed = 0
$root = $PSScriptRoot
Import-Module (Join-Path $root 'build\SBMS.DriverCertification.psm1') -Force

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param([scriptblock] $Body, [string] $Pattern)
    try {
        & $Body
    } catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected error matching '$Pattern'; actual '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected error matching '$Pattern', but no error was thrown."
}

function Invoke-Test {
    param([string] $Name, [scriptblock] $Body)
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

function New-TestDriver {
    param([string] $Directory)
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $inf = @'
[Version]
DriverVer=07/24/2026,0.3.0.0
CatalogFile=sbmsindirectdisplay.cat

[Manufacturer]
%ManufacturerName%=SBMS,NTamd64

[SBMS.NTamd64]
%DeviceName%=SBMS_Install, SBMS\IndirectDisplay
%DeviceName%=SBMS_Install, Root\SBMSIndirectDisplay

[SBMS_Install.Wdf]
UmdfService=SBMSIndirectDisplay,SBMSIndirectDisplay_Install
UmdfServiceOrder=SBMSIndirectDisplay

[SBMS_Install.HW]
HKR, "WUDF", "DeviceGroupId", %REG_SZ%, "SBMSIndirectDisplayGroup"

[SBMSIndirectDisplay_Install]
UmdfLibraryVersion=2.0.0
ServiceBinary=%12%\UMDF\SBMSIndirectDisplay.dll

[Strings]
ManufacturerName="SBMS"
DeviceName="SBMS Virtual Display Adapter"
'@
    [System.IO.File]::WriteAllText(
        (Join-Path $Directory 'SBMSIndirectDisplay.inf'),
        $inf,
        (New-Object System.Text.UTF8Encoding($false))
    )
    Copy-Item -LiteralPath $script:FixtureBinary `
        -Destination (Join-Path $Directory 'SBMSIndirectDisplay.dll')
}

function New-TestPolicy {
    [pscustomobject]@{
        publisher = [pscustomobject]@{
            thumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
        }
        driverCertification = [pscustomobject]@{
            allowedCatalogSubjects = @('CN=Microsoft Windows Hardware Compatibility Publisher')
        }
    }
}

function New-TestCatalogSignature {
    [pscustomobject]@{
        Status = 'Valid'
        SignerCertificate = [pscustomobject]@{
            Subject = 'CN=Microsoft Windows Hardware Compatibility Publisher'
            Thumbprint = '89ABCDEF0123456789ABCDEF0123456789ABCDEF'
        }
        TimeStamperCertificate = [pscustomobject]@{
            Subject = 'CN=Trusted Timestamp'
            Thumbprint = 'FEDCBA9876543210FEDCBA9876543210FEDCBA98'
        }
    }
}

$script:FixtureBinary = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$script:FixtureVersion = (Get-Item -LiteralPath $script:FixtureBinary).VersionInfo
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'SBMS-driver-certification-' + [guid]::NewGuid().ToString('N')
)
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

try {
    $driver = Join-Path $testRoot 'driver'
    New-TestDriver -Directory $driver
    $toolchain = [pscustomobject]@{ wdk = 'test-wdk'; msbuild = 'test-msbuild' }
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $fakeSignTool = Join-Path $testRoot 'signtool.exe'
    [System.IO.File]::WriteAllBytes($fakeSignTool, [byte[]](0))
    $policy = New-TestPolicy
    $dllSignature = [pscustomobject]@{
        Status = 'Valid'
        SignerCertificate = [pscustomobject]@{
            Subject = 'CN=SBMS Test Publisher'
            Thumbprint = [string]$policy.publisher.thumbprint
        }
        TimeStamperCertificate = [pscustomobject]@{
            Subject = 'CN=Trusted Timestamp'
            Thumbprint = 'FEDCBA9876543210FEDCBA9876543210FEDCBA98'
        }
    }
    $toolInvoker = {
        param($Tool, $Arguments)
        [pscustomobject]@{ ExitCode = 0; Output = 'verified' }
    }
    $candidateCommon = @{
        SourceCommit = $commit
        BuildCommand = '.\build-sbms-driver.ps1 -Production'
        Toolchain = $toolchain
        ExpectedWindowsVersion = [string]$script:FixtureVersion.FileVersion
        ExpectedProductVersion = [string]$script:FixtureVersion.ProductVersion
        ExpectedDriverVer = '07/24/2026,0.3.0.0'
        SigningPolicy = $policy
        SignToolPath = $fakeSignTool
        ToolInvoker = $toolInvoker
        DllSignature = $dllSignature
    }

    Invoke-Test 'Dirty source cannot become a WHQL candidate' {
        Assert-Throws {
            New-SBMSDriverCandidate `
                -DriverDirectory $driver `
                -OutputDirectory (Join-Path $testRoot 'dirty') `
                -SourceDirty $true `
                @candidateCommon
        } 'clean source tree'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'dirty'))) 'Rejected candidate mutated output.'
    }

    Invoke-Test 'Microsoft sample INF identity cannot become a WHQL candidate' {
        $sampleDriver = Join-Path $testRoot 'sample-driver'
        New-TestDriver -Directory $sampleDriver
        Add-Content -LiteralPath (Join-Path $sampleDriver 'SBMSIndirectDisplay.inf') `
            -Value 'Provider=<Your manufacturer name>; Root\IddSampleDriver ; TODO: edit hw-id' `
            -Encoding UTF8
        $output = Join-Path $testRoot 'sample-output'
        Assert-Throws {
            New-SBMSDriverCandidate `
                -DriverDirectory $sampleDriver `
                -OutputDirectory $output `
                -SourceDirty $false `
                @candidateCommon
        } 'legacy sample identity'
        Assert-True (-not (Test-Path -LiteralPath $output)) 'Rejected sample identity mutated output.'
    }

    Invoke-Test 'Stale DLL version cannot be relabeled as the current commit' {
        $wrongVersion = @{} + $candidateCommon
        $wrongVersion.ExpectedProductVersion = '99.99.99-stale'
        $output = Join-Path $testRoot 'stale-output'
        Assert-Throws {
            New-SBMSDriverCandidate `
                -DriverDirectory $driver `
                -OutputDirectory $output `
                -SourceDirty $false `
                @wrongVersion
        } 'DLL version mismatch'
        Assert-True (-not (Test-Path -LiteralPath $output)) 'Rejected stale DLL mutated output.'
    }

    Invoke-Test 'Unsigned or untimestamped DLL cannot become a WHQL candidate' {
        $badSignature = @{} + $candidateCommon
        $badSignature.DllSignature = [pscustomobject]@{
            Status = 'NotSigned'
            SignerCertificate = $null
            TimeStamperCertificate = $null
        }
        $output = Join-Path $testRoot 'unsigned-output'
        Assert-Throws {
            New-SBMSDriverCandidate `
                -DriverDirectory $driver `
                -OutputDirectory $output `
                -SourceDirty $false `
                @badSignature
        } 'signature is not valid'
        Assert-True (-not (Test-Path -LiteralPath $output)) 'Rejected unsigned DLL mutated output.'
    }

    $candidateDir = Join-Path $testRoot 'candidate'
    $candidate = New-SBMSDriverCandidate `
        -DriverDirectory $driver `
        -OutputDirectory $candidateDir `
        -SourceDirty $false `
        @candidateCommon

    Invoke-Test 'Candidate freezes INF and DLL with clean commit provenance' {
        Assert-True ($candidate.manifest.kind -ceq 'SBMS-WHQL-driver-candidate') 'Candidate kind mismatch.'
        Assert-True ($candidate.manifest.source.commit -ceq $commit) 'Candidate commit mismatch.'
        Assert-True (-not $candidate.manifest.source.dirty) 'Candidate unexpectedly records dirty source.'
        Assert-True ([int]$candidate.manifest.schemaVersion -eq 2) 'Candidate schema mismatch.'
        Assert-True ([int]$candidate.manifest.driver.identitySchema -eq 1) 'Candidate identity schema mismatch.'
        Assert-True ([string]$candidate.manifest.driver.identityFingerprint -match '^[0-9a-f]{64}$') 'Candidate identity fingerprint is invalid.'
        Assert-True (Test-Path -LiteralPath (Join-Path $candidateDir 'driver-identity.json') -PathType Leaf) 'Candidate identity contract is missing.'
        Assert-True (@($candidate.manifest.artifacts).Count -eq 2) 'Candidate artifact allow-list mismatch.'
        Assert-True ($candidate.manifestSha256 -match '^[0-9a-f]{64}$') 'Candidate manifest hash is invalid.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $candidateDir 'driver\sbmsindirectdisplay.cat'))) 'Candidate copied a pre-WHQL catalog.'
    }

    $returned = Join-Path $testRoot 'returned'
    New-TestDriver -Directory $returned
    [System.IO.File]::WriteAllBytes((Join-Path $returned 'sbmsindirectdisplay.cat'), [byte[]](9, 9, 9))
    $signature = New-TestCatalogSignature

    Invoke-Test 'Import rejects an unpinned candidate manifest before output mutation' {
        $output = Join-Path $testRoot 'unpinned-output'
        Assert-Throws {
            Import-SBMSWhqlDriver `
                -CandidateDirectory $candidateDir `
                -ExpectedCandidateManifestSha256 ('f' * 64) `
                -ReturnedDirectory $returned `
                -OutputDirectory $output `
                -SigningPolicy $policy `
                -SignToolPath $fakeSignTool `
                -ToolInvoker $toolInvoker `
                -CatalogSignature $signature
        } 'manifest hash mismatch'
        Assert-True (-not (Test-Path -LiteralPath $output)) 'Rejected import mutated output.'
    }

    Invoke-Test 'Import rejects returned payload drift before signature verification' {
        $drifted = Join-Path $testRoot 'drifted'
        Copy-Item -LiteralPath $returned -Destination $drifted -Recurse
        [System.IO.File]::WriteAllBytes((Join-Path $drifted 'SBMSIndirectDisplay.dll'), [byte[]](4, 2))
        $output = Join-Path $testRoot 'drifted-output'
        Assert-Throws {
            Import-SBMSWhqlDriver `
                -CandidateDirectory $candidateDir `
                -ExpectedCandidateManifestSha256 $candidate.manifestSha256 `
                -ReturnedDirectory $drifted `
                -OutputDirectory $output `
                -SigningPolicy $policy `
                -SignToolPath $fakeSignTool `
                -ToolInvoker $toolInvoker `
                -CatalogSignature $signature
        } 'changed after candidate freeze'
        Assert-True (-not (Test-Path -LiteralPath $output)) 'Drift rejection mutated output.'
    }

    Invoke-Test 'Valid WHQL import verifies catalog membership and preserves returned bytes' {
        $script:ToolCalls = New-Object System.Collections.Generic.List[object]
        $capturingInvoker = {
            param($Tool, $Arguments)
            $script:ToolCalls.Add(@($Arguments))
            [pscustomobject]@{ ExitCode = 0; Output = 'verified' }
        }
        $output = Join-Path $testRoot 'imported'
        $result = Import-SBMSWhqlDriver `
            -CandidateDirectory $candidateDir `
            -ExpectedCandidateManifestSha256 $candidate.manifestSha256 `
            -ReturnedDirectory $returned `
            -OutputDirectory $output `
            -SigningPolicy $policy `
            -SignToolPath $fakeSignTool `
            -ToolInvoker $capturingInvoker `
            -CatalogSignature $signature

        Assert-True ($script:ToolCalls.Count -eq 3) 'Expected one catalog and two catalog-membership verification calls.'
        Assert-True (@($script:ToolCalls | Where-Object { $_ -contains '/c' }).Count -eq 2) 'INF and DLL were not both verified against the catalog.'
        foreach ($name in @('SBMSIndirectDisplay.inf', 'SBMSIndirectDisplay.dll', 'sbmsindirectdisplay.cat')) {
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $returned $name) -Algorithm SHA256).Hash
            $outputHash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash
            Assert-True ($sourceHash -ceq $outputHash) "Imported bytes changed for '$name'."
        }
        Assert-True ($result.manifest.certification.method -ceq 'WHQL') 'Import provenance does not record WHQL.'
        Assert-True ([int]$result.manifest.schemaVersion -eq 2) 'Import schema mismatch.'
        Assert-True ([string]$result.manifest.identityFingerprint -ceq [string]$candidate.manifest.driver.identityFingerprint) 'Import identity fingerprint drifted.'
        Assert-True ($result.manifest.candidateManifestSha256 -ceq $candidate.manifestSha256) 'Candidate pin is absent from import provenance.'
        Assert-True ($result.manifest.driverVer -ceq '07/24/2026,0.3.0.0') 'DriverVer was lost during WHQL import.'
    }
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host "Driver certification contract: $script:Passed passed, $script:Failed failed"
if ($script:Failed -ne 0) {
    exit 1
}
