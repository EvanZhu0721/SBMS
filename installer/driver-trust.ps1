# Kept separate so installation tests never touch machine certificate stores.
function Invoke-SbmsCertificateStore {
    param(
        [Parameter(Mandatory)][ValidateSet('Root', 'TrustedPublisher')][string]$Name,
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][ValidateSet('Contains', 'Add', 'Remove')][string]$Action
    )
    $store = [Security.Cryptography.X509Certificates.X509Store]::new($Name, 'LocalMachine')
    try {
        if ($Action -eq 'Contains') { $store.Open('ReadOnly') }
        else { $store.Open('ReadWrite') }
        switch ($Action) {
            'Contains' { return $store.Certificates.Find('FindByThumbprint', $Certificate.Thumbprint, $false).Count -gt 0 }
            'Add' { $store.Add($Certificate) }
            'Remove' { $store.Remove($Certificate) }
        }
    }
    finally { $store.Close() }
}
function Invoke-SbmsDriverInstall {
    param(
        [Parameter(Mandatory)][string]$DriverInf,
        [switch]$TrustCertificate
    )

    $directory = Split-Path -Parent $DriverInf
    $certificatePath = Join-Path $directory 'SBMS.cer'
    $added = @()
    try {
        if (Test-Path -LiteralPath $certificatePath) {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
            $catalog = Join-Path $directory 'SBMSIndirectDisplay.cat'
            $binary = Join-Path $directory 'SBMSIndirectDisplay.dll'
            foreach ($path in @($catalog, $binary)) {
                $signature = Get-AuthenticodeSignature -LiteralPath $path
                if (-not $signature.SignerCertificate -or
                    $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
                    throw "Bundled certificate does not match the driver signer: $path"
                }
                if ($signature.Status -eq 'HashMismatch' -or $signature.Status -eq 'NotSigned') {
                    throw "Driver signature validation failed: $path ($($signature.Status))"
                }
            }
            foreach ($name in @('Root', 'TrustedPublisher')) {
                if (-not (Invoke-SbmsCertificateStore -Name $name -Certificate $certificate -Action Contains)) {
                    if (-not $TrustCertificate) {
                        throw 'The SBMS signing certificate is not trusted. Run Setup interactively and explicitly approve certificate trust.'
                    }
                    Invoke-SbmsCertificateStore -Name $name -Certificate $certificate -Action Add
                    $added += $name
                }
            }
            foreach ($path in @($catalog, $binary)) {
                $signature = Get-AuthenticodeSignature -LiteralPath $path
                if ($signature.Status -ne 'Valid') {
                    throw "Driver signature validation failed: $path ($($signature.Status))"
                }
            }
        }
        & pnputil.exe /add-driver $DriverInf /install
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) {
            throw "pnputil add-driver failed with exit code $LASTEXITCODE. See Windows\INF\setupapi.dev.log."
        }
        if ($LASTEXITCODE -eq 3010) {
            Write-Output 'Windows requested a restart to finish driver installation.'
        }
    }
    catch {
        $originalError = $_
        # Undo only certificate entries created by this failed driver installation.
        foreach ($name in $added) {
            try {
                Invoke-SbmsCertificateStore -Name $name -Certificate $certificate -Action Remove
            }
            catch {
                Write-Warning "Could not remove newly added SBMS certificate from ${name}: $($_.Exception.Message)"
            }
        }
        throw $originalError
    }
}
