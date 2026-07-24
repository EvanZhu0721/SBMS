Set-StrictMode -Version 2.0

function ConvertTo-SBMSThumbprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Thumbprint
    )

    $normalized = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalized.Length -ne 40) {
        throw "Certificate thumbprint must contain exactly 40 hexadecimal characters: '$Thumbprint'."
    }
    $normalized
}

function Assert-SBMSNoPlaceholder {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value,

        [Parameter(Mandatory = $true)]
        [string] $Field
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -match '(?i)(replace|placeholder|example|todo|your[ _-])') {
        throw "Production signing policy field '$Field' is missing or still contains a placeholder."
    }
}

function Read-SBMSSigningPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    $fullPath = [System.IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Signing policy not found: $fullPath"
    }

    try {
        $policy = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        throw "Signing policy is not valid UTF-8 JSON: $($_.Exception.Message)"
    }

    if ([int]$policy.schemaVersion -ne 1) {
        throw "Unsupported signing policy schemaVersion '$($policy.schemaVersion)'; expected 1."
    }
    if ([string]$policy.profile -cne 'Production') {
        throw "Signing policy profile must be exactly 'Production'."
    }

    Assert-SBMSNoPlaceholder -Value ([string]$policy.publisher.subject) -Field 'publisher.subject'
    $thumbprint = ConvertTo-SBMSThumbprint -Thumbprint ([string]$policy.publisher.thumbprint)

    $storeLocation = [string]$policy.publisher.storeLocation
    if ($storeLocation -notin @('CurrentUser', 'LocalMachine')) {
        throw "publisher.storeLocation must be CurrentUser or LocalMachine."
    }
    if ([string]$policy.publisher.storeName -cne 'My') {
        throw "publisher.storeName must be exactly 'My'."
    }

    $timestampUri = $null
    if (-not [System.Uri]::TryCreate(
            [string]$policy.timestamp.url,
            [System.UriKind]::Absolute,
            [ref]$timestampUri
        ) -or
        $timestampUri.Scheme -cne 'https') {
        throw "timestamp.url must be an absolute HTTPS URI."
    }
    if ([string]$policy.timestamp.protocol -cne 'RFC3161' -or
        [string]$policy.timestamp.digest -cne 'SHA256' -or
        -not [bool]$policy.timestamp.required) {
        throw "Production timestamp policy must require RFC3161 with SHA256."
    }

    if ([string]$policy.driverCertification.method -cne 'WHQL') {
        throw "Production driverCertification.method must be exactly 'WHQL'."
    }
    $driverSubjects = @($policy.driverCertification.allowedCatalogSubjects)
    if ($driverSubjects.Count -eq 0) {
        throw "driverCertification.allowedCatalogSubjects must contain at least one Microsoft catalog signer."
    }
    foreach ($subject in $driverSubjects) {
        Assert-SBMSNoPlaceholder -Value ([string]$subject) -Field 'driverCertification.allowedCatalogSubjects'
    }

    if ([string]$policy.integrity.hashAlgorithm -cne 'SHA256' -or
        [string]$policy.integrity.catalogVersion -cne '2.0') {
        throw "Production integrity policy must use SHA256 and catalogVersion 2.0."
    }
    if ([string]$policy.sbom.format -cne 'SPDX' -or
        [string]$policy.sbom.specVersion -cne '2.2') {
        throw "Production SBOM policy must require SPDX 2.2."
    }

    $policy.publisher.thumbprint = $thumbprint
    $policy
}

function Resolve-SBMSSignTool {
    [CmdletBinding()]
    param(
        [string] $LiteralPath
    )

    if (-not [string]::IsNullOrWhiteSpace($LiteralPath)) {
        $fullPath = [System.IO.Path]::GetFullPath($LiteralPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "SignTool not found: $fullPath"
        }
        return $fullPath
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter 'signtool.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw "SignTool x64 was not found under $kitsRoot."
    }
    $candidate.FullName
}

function Resolve-SBMSSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Policy,

        [datetime] $Now = [datetime]::UtcNow
    )

    $thumbprint = ConvertTo-SBMSThumbprint -Thumbprint ([string]$Policy.publisher.thumbprint)
    $storePath = 'Cert:\{0}\{1}\{2}' -f
        [string]$Policy.publisher.storeLocation,
        [string]$Policy.publisher.storeName,
        $thumbprint
    $certificate = Get-Item -LiteralPath $storePath -ErrorAction SilentlyContinue
    if (-not $certificate) {
        throw "The explicit production signing certificate was not found: $storePath"
    }
    if (-not $certificate.HasPrivateKey) {
        throw "The production signing certificate does not expose a private key: $thumbprint"
    }
    if ($Now -lt $certificate.NotBefore.ToUniversalTime() -or
        $Now -gt $certificate.NotAfter.ToUniversalTime()) {
        throw "The production signing certificate is not valid at $($Now.ToString('o')): $thumbprint"
    }
    if ([string]$certificate.Subject -cne [string]$Policy.publisher.subject) {
        throw "The production signing certificate subject does not match policy. Expected '$($Policy.publisher.subject)', actual '$($certificate.Subject)'."
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $ekuOids = @(
        $certificate.Extensions |
            Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
            ForEach-Object {
                $_.EnhancedKeyUsages | ForEach-Object { $_.Value }
            }
    )
    if ($ekuOids -notcontains $codeSigningOid) {
        throw "The production signing certificate does not include the Code Signing EKU."
    }
    $certificate
}

function Invoke-SBMSSignTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $SignToolPath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList,

        [scriptblock] $ToolInvoker
    )

    if ($ToolInvoker) {
        $result = & $ToolInvoker $SignToolPath $ArgumentList
        $exitCode = [int]$result.ExitCode
        $output = [string]$result.Output
    } else {
        $lines = @(& $SignToolPath @ArgumentList 2>&1)
        $exitCode = [int]$LASTEXITCODE
        $output = ($lines | ForEach-Object { [string]$_ }) -join "`n"
    }

    if ($exitCode -ne 0) {
        throw "SignTool failed or completed with warnings (exit $exitCode).`n$output"
    }
    [pscustomobject][ordered]@{
        ExitCode = $exitCode
        Output = $output
        Arguments = @($ArgumentList)
    }
}

function Invoke-SBMSSignAuthenticode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [psobject] $Policy,

        [string] $SignToolPath,

        [scriptblock] $ToolInvoker
    )

    $fullPath = [System.IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Signing input not found: $fullPath"
    }
    $tool = Resolve-SBMSSignTool -LiteralPath $SignToolPath
    $arguments = New-Object System.Collections.Generic.List[string]
    foreach ($argument in @(
            'sign',
            '/fd', 'SHA256',
            '/sha1', (ConvertTo-SBMSThumbprint ([string]$Policy.publisher.thumbprint)),
            '/s', [string]$Policy.publisher.storeName
        )) {
        $arguments.Add([string]$argument)
    }
    if ([string]$Policy.publisher.storeLocation -ceq 'LocalMachine') {
        $arguments.Add('/sm')
    }
    foreach ($argument in @(
            '/tr', [string]$Policy.timestamp.url,
            '/td', 'SHA256',
            $fullPath
        )) {
        $arguments.Add([string]$argument)
    }
    Invoke-SBMSSignTool -SignToolPath $tool -ArgumentList $arguments.ToArray() -ToolInvoker $ToolInvoker
}

function Assert-SBMSAuthenticodeSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [psobject] $Policy,

        [string] $SignToolPath,

        [scriptblock] $ToolInvoker,

        [psobject] $Signature
    )

    $fullPath = [System.IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Signature verification input not found: $fullPath"
    }
    $tool = Resolve-SBMSSignTool -LiteralPath $SignToolPath
    $null = Invoke-SBMSSignTool `
        -SignToolPath $tool `
        -ArgumentList @('verify', '/pa', '/all', '/tw', $fullPath) `
        -ToolInvoker $ToolInvoker

    if (-not $Signature) {
        $Signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    }
    if ([string]$Signature.Status -cne 'Valid') {
        throw "Authenticode signature is not valid for '$fullPath': $($Signature.Status)"
    }
    if (-not $Signature.SignerCertificate) {
        throw "Authenticode signer certificate is missing for '$fullPath'."
    }
    $expectedThumbprint = ConvertTo-SBMSThumbprint ([string]$Policy.publisher.thumbprint)
    $actualThumbprint = ConvertTo-SBMSThumbprint ([string]$Signature.SignerCertificate.Thumbprint)
    if ($actualThumbprint -cne $expectedThumbprint) {
        throw "Authenticode signer mismatch for '$fullPath'. Expected $expectedThumbprint, actual $actualThumbprint."
    }
    if (-not $Signature.TimeStamperCertificate) {
        throw "Authenticode RFC3161 timestamp is missing for '$fullPath'."
    }
    [pscustomobject][ordered]@{
        path = $fullPath
        status = 'Valid'
        signerSubject = [string]$Signature.SignerCertificate.Subject
        signerThumbprint = $actualThumbprint
        timestampSubject = [string]$Signature.TimeStamperCertificate.Subject
        timestampThumbprint = [string]$Signature.TimeStamperCertificate.Thumbprint
    }
}

function Assert-SBMSWhqlCatalog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [psobject] $Policy,

        [string] $SignToolPath,

        [scriptblock] $ToolInvoker,

        [psobject] $Signature
    )

    $fullPath = [System.IO.Path]::GetFullPath($LiteralPath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "WHQL catalog not found: $fullPath"
    }
    $tool = Resolve-SBMSSignTool -LiteralPath $SignToolPath
    $null = Invoke-SBMSSignTool `
        -SignToolPath $tool `
        -ArgumentList @('verify', '/kp', '/all', '/tw', $fullPath) `
        -ToolInvoker $ToolInvoker

    if (-not $Signature) {
        $Signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    }
    if ([string]$Signature.Status -cne 'Valid' -or -not $Signature.SignerCertificate) {
        throw "WHQL catalog Authenticode signature is not valid: '$fullPath'."
    }
    $actualSubject = [string]$Signature.SignerCertificate.Subject
    $allowedSubjects = @($Policy.driverCertification.allowedCatalogSubjects | ForEach-Object { [string]$_ })
    if ($allowedSubjects -notcontains $actualSubject) {
        throw "WHQL catalog signer is not allowed. Actual '$actualSubject'."
    }
    if (-not $Signature.TimeStamperCertificate) {
        throw "WHQL catalog timestamp is missing: '$fullPath'."
    }
    [pscustomobject][ordered]@{
        path = $fullPath
        status = 'Valid'
        signerSubject = $actualSubject
        signerThumbprint = [string]$Signature.SignerCertificate.Thumbprint
        timestampSubject = [string]$Signature.TimeStamperCertificate.Subject
    }
}

function Assert-SBMSWhqlPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $CatalogPath,

        [Parameter(Mandatory = $true)]
        [string[]] $PayloadPaths,

        [Parameter(Mandatory = $true)]
        [psobject] $Policy,

        [string] $SignToolPath,

        [scriptblock] $ToolInvoker,

        [psobject] $CatalogSignature
    )

    $catalog = [System.IO.Path]::GetFullPath($CatalogPath)
    $catalogResult = Assert-SBMSWhqlCatalog `
        -LiteralPath $catalog `
        -Policy $Policy `
        -SignToolPath $SignToolPath `
        -ToolInvoker $ToolInvoker `
        -Signature $CatalogSignature

    $tool = Resolve-SBMSSignTool -LiteralPath $SignToolPath
    $verifiedPayload = New-Object System.Collections.Generic.List[object]
    foreach ($payloadPath in $PayloadPaths) {
        $payload = [System.IO.Path]::GetFullPath($payloadPath)
        if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) {
            throw "WHQL payload file not found: $payload"
        }
        $null = Invoke-SBMSSignTool `
            -SignToolPath $tool `
            -ArgumentList @('verify', '/kp', '/all', '/tw', '/c', $catalog, $payload) `
            -ToolInvoker $ToolInvoker
        $verifiedPayload.Add([pscustomobject][ordered]@{
            path = $payload
            bytes = (Get-Item -LiteralPath $payload).Length
            sha256 = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }

    [pscustomobject][ordered]@{
        catalog = $catalogResult
        payload = $verifiedPayload.ToArray()
    }
}

Export-ModuleMember -Function @(
    'ConvertTo-SBMSThumbprint',
    'Read-SBMSSigningPolicy',
    'Resolve-SBMSSignTool',
    'Resolve-SBMSSigningCertificate',
    'Invoke-SBMSSignTool',
    'Invoke-SBMSSignAuthenticode',
    'Assert-SBMSAuthenticodeSignature',
    'Assert-SBMSWhqlCatalog',
    'Assert-SBMSWhqlPackage'
)
