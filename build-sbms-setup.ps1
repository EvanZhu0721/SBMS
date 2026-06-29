param(
    [string] $OutputName = "SBMSSetup.exe"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Source = Join-Path $Root "installer\SBMSSetup.cs"
$Manifest = Join-Path $Root "installer\SBMSSetup.manifest"
if ([System.IO.Path]::IsPathRooted($OutputName)) {
    $Out = $OutputName
} else {
    $Out = Join-Path $Root $OutputName
}

$CscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$Csc = $CscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Csc) {
    throw "Missing .NET Framework csc.exe"
}
if (-not (Test-Path $Source)) {
    throw "Missing source: $Source"
}
if (-not (Test-Path $Manifest)) {
    throw "Missing manifest: $Manifest"
}

& $Csc /nologo /target:winexe /optimize+ /win32manifest:$Manifest /out:$Out /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $Source
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$WdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10"
$signTool = Get-ChildItem (Join-Path $WdkRoot "bin") -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\(x64|x86)\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
$certs = @(Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue)
$signingCert = $certs |
    Where-Object { $_.Subject -like "*WDKTestCert*" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $signingCert) {
    $signingCert = $certs | Sort-Object NotAfter -Descending | Select-Object -First 1
}

if ($signTool -and $signingCert) {
    & $signTool.FullName sign /v /fd SHA256 /sha1 $signingCert.Thumbprint $Out
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
} else {
    Write-Warning "Setup executable was built unsigned because signtool or a code-signing certificate was not found."
}

Write-Host "Built: $Out"
