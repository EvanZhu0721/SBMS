param(
    [string] $OutputName = "SBMSSetup.exe"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$GeneratedRoot = Join-Path $Root "obj\version\setup"
$VersionSource = Join-Path $GeneratedRoot "SBMS.Version.g.cs"
$GeneratedManifest = Join-Path $GeneratedRoot "SBMSSetup.manifest"
Write-SBMSGeneratedFile -LiteralPath $VersionSource -Content (
    New-SBMSCSharpVersionSource -Metadata $BuildMetadata -AssemblyTitle "SBMS Setup" -FileDescription "SBMS installer"
) | Out-Null
Write-SBMSGeneratedFile -LiteralPath $GeneratedManifest -Content (
    New-SBMSApplicationManifest -Metadata $BuildMetadata -AssemblyName "SBMS.Setup" -ExecutionLevel requireAdministrator
) | Out-Null
$Source = Join-Path $Root "installer\SBMSSetup.cs"
$Manifest = $GeneratedManifest
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

& $Csc /nologo /target:winexe /optimize+ /win32manifest:$Manifest /out:$Out /reference:System.Windows.Forms.dll /reference:System.Drawing.dll $Source $VersionSource
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
$versionInfo = (Get-Item -LiteralPath $Out).VersionInfo
if ([string]$versionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
    throw "Setup version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
