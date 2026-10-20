$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Describe 'Explicit driver certificate trust' {
    BeforeAll {
        . (Join-Path $PSScriptRoot '..\driver-trust.ps1')
        $script:driverInf = Join-Path $TestDrive 'SBMSIndirectDisplay.inf'
        # Fixed public DER fixture; no private key or certificate store access.
        # Avoid CertificateRequest, which is unavailable in Windows PowerShell 5.1.
        $publicDer = [Convert]::FromBase64String(
            'MIICtTCCAZ2gAwIBAgIJAM9XXuxy7z9WMA0GCSqGSIb3DQEBCwUAMBoxGDAWBgNVBAMTD1NCTVMgdHJ1c3QgdGVzdDAeFw0yNjEwMTkxNTU4MDNaFw0zNjEwMjAxNTU4MDNaMBoxGDAWBgNVBAMTD1NCTVMgdHJ1c3QgdGVzdDCCASIwDQYJKoZIhvcNAQEBBQADggEPADCCAQoCggEBAKHUpmoC5nfvo2xDn8kdWmidKwH0cucqTuX8O/0CrAc+hRCn9MEOtAchKXWS4TPK5QYG3/5GBWrzAkXSbn9OlD/tGAqYv7W20YWqyjObqgDupmcmVUL8y4zHk30J9I3VlZKe39x6UBs3nWu+mNR2zQvsVOhr/z7wiLYoNZd0jpuB6ZbVZSVsCCkWnwhqvloe4S4xS7OhsxEWCvMcmB0ZkVlqZ3QwZrCIBzg5NbAQzS4l6XoMO2HI5M2uZP2OmxcY2EEQgsJBUyoAdpbFbwYAGVSrUCeJqqp5BicJuDZ5pGD8Jktr9WXIpz5QjVkJet777Jh8Tfd82yOmC8yJJBMZIuUCAwEAATANBgkqhkiG9w0BAQsFAAOCAQEAS75nd1WQNCgTDCyQE2AfIWfKQ4KP7iVS7W2zakNUJQ4B5f0cjIHa29qlTGkToJS/kvdGAOVh31dalxBBOgh1/ktBo+mqFusEiggcn4GZ58RRHlFatiOSjY4pcIm22DmMD88XMnH/GAtxslaLiyS7Z3c/dZy2ctSIqXD/hP2Hz3m+Ay0EkrTw7jGar6I9Xv2lATaB96+QyFDVhRrAGBTJLF+xI+q2+TXwkDxy22sJGGPjtVW4ii2CEEObsZxsfwyn8cM2BfXLwrB8ruEGoogFidt/6prRTBYxRu2c3J67qznYXN1P6RiQVX34BCKe1+ChpGPKTAu05xKeF6tCgifTvg==')
        $script:testCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicDer)
        [IO.File]::WriteAllBytes((Join-Path $TestDrive 'SBMS.cer'),
            $script:testCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }

    BeforeEach {
        $script:trusted = @{ Root = $false; TrustedPublisher = $false }
        $script:events = [Collections.Generic.List[string]]::new()
        Mock Invoke-SbmsCertificateStore {
            param($Name, $Certificate, $Action)
            if ($Action -eq 'Contains') { return $script:trusted[$Name] }
            $script:events.Add("$Action/$Name")
            $script:trusted[$Name] = $Action -eq 'Add'
        }
        Mock Get-AuthenticodeSignature {
            [pscustomobject]@{ SignerCertificate = $script:testCertificate; Status = 'Valid' }
        }
        Mock pnputil.exe { $global:LASTEXITCODE = 0 }
    }

    AfterAll {
        $script:testCertificate.Dispose()
    }

    It 'rejects a signer mismatch before touching either trust store' {
        Mock Get-AuthenticodeSignature {
            [pscustomobject]@{ SignerCertificate = [pscustomobject]@{ Thumbprint = 'OTHER' }; Status = 'Valid' }
        }
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate } |
            Should Throw 'does not match'
        Assert-MockCalled Invoke-SbmsCertificateStore -Times 0 -Exactly -Scope It
        Assert-MockCalled pnputil.exe -Times 0 -Exactly -Scope It
    }

    It 'does not add trust without explicit approval' {
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf } | Should Throw 'explicitly approve'
        $script:events.Count | Should Be 0
        Assert-MockCalled pnputil.exe -Times 0 -Exactly -Scope It
    }

    It 'rejects a corrupted signed driver before adding trust' {
        Mock Get-AuthenticodeSignature {
            [pscustomobject]@{ SignerCertificate = $script:testCertificate; Status = 'HashMismatch' }
        }
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate } |
            Should Throw 'signature validation failed'
        Assert-MockCalled Invoke-SbmsCertificateStore -Times 0 -Exactly -Scope It
        Assert-MockCalled pnputil.exe -Times 0 -Exactly -Scope It
    }

    It 'installs with existing trust without requesting approval or modifying it' {
        $script:trusted.Root = $true
        $script:trusted.TrustedPublisher = $true
        Invoke-SbmsDriverInstall -DriverInf $script:driverInf
        $script:events.Count | Should Be 0
        Assert-MockCalled pnputil.exe -Times 1 -Exactly -Scope It
    }

    It 'rolls back the first addition if the second store rejects the import' {
        Mock Invoke-SbmsCertificateStore {
            param($Name, $Certificate, $Action)
            if ($Action -eq 'Contains') { return $false }
            $script:events.Add("$Action/$Name")
            if ($Action -eq 'Add' -and $Name -eq 'TrustedPublisher') { throw 'import denied' }
        }
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate } |
            Should Throw 'import denied'
        ($script:events -join ',') | Should Be 'Add/Root,Add/TrustedPublisher,Remove/Root'
        Assert-MockCalled pnputil.exe -Times 0 -Exactly -Scope It
    }

    It 'preserves pre-existing Root trust when installation fails' {
        $script:trusted.Root = $true
        Mock pnputil.exe { $global:LASTEXITCODE = 5 }
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate } |
            Should Throw 'exit code 5'
        ($script:events -join ',') | Should Be 'Add/TrustedPublisher,Remove/TrustedPublisher'
        $script:trusted.Root | Should Be $true
    }

    It 'rolls back both new entries on invalid signature after trust and never installs' {
        Mock Get-AuthenticodeSignature {
            [pscustomobject]@{ SignerCertificate = $script:testCertificate; Status = 'NotTrusted' }
        }
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate } |
            Should Throw 'signature validation failed'
        ($script:events -join ',') | Should Be 'Add/Root,Add/TrustedPublisher,Remove/Root,Remove/TrustedPublisher'
        Assert-MockCalled pnputil.exe -Times 0 -Exactly -Scope It
    }

    It 'continues rollback and preserves the original failure if one removal fails' {
        Mock pnputil.exe { $global:LASTEXITCODE = 5 }
        Mock Invoke-SbmsCertificateStore {
            param($Name, $Certificate, $Action)
            if ($Action -eq 'Contains') { return $false }
            $script:events.Add("$Action/$Name")
            if ($Action -eq 'Remove' -and $Name -eq 'Root') { throw 'rollback denied' }
        }
        { Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate -WarningAction SilentlyContinue } |
            Should Throw 'exit code 5'
        ($script:events -join ',') | Should Be 'Add/Root,Add/TrustedPublisher,Remove/Root,Remove/TrustedPublisher'
    }

    It 'accepts restart-required success and retains approved trust' {
        Mock pnputil.exe { $global:LASTEXITCODE = 3010 }
        Invoke-SbmsDriverInstall -DriverInf $script:driverInf -TrustCertificate | Should Match 'restart'
        ($script:events -join ',') | Should Be 'Add/Root,Add/TrustedPublisher'
    }
}
