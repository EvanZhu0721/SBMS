param(
    [string] $OutputName = "SBMS.exe"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
$BuildMetadata = Get-SBMSBuildMetadata -RepositoryRoot $Root
Assert-SBMSVersionSourceContract -RepositoryRoot $Root
$GeneratedRoot = Join-Path $Root "obj\version\gui"
$VersionSource = Join-Path $GeneratedRoot "SBMS.Version.g.cs"
$GeneratedManifest = Join-Path $GeneratedRoot "SBMSGui.manifest"
Write-SBMSGeneratedFile -LiteralPath $VersionSource -Content (
    New-SBMSCSharpVersionSource -Metadata $BuildMetadata -AssemblyTitle "SBMS" -FileDescription "SBMS display control"
) | Out-Null
Write-SBMSGeneratedFile -LiteralPath $GeneratedManifest -Content (
    New-SBMSApplicationManifest -Metadata $BuildMetadata -AssemblyName "SBMS.Gui" -ExecutionLevel requireAdministrator
) | Out-Null
$SourceDirectories = @(
    (Join-Path $Root "gui"),
    (Join-Path $Root "gui\Core"),
    (Join-Path $Root "gui\Models"),
    (Join-Path $Root "gui\Services")
)
$Sources = @(
    $SourceDirectories |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -File -Filter "*.cs" } |
        Sort-Object FullName |
        Select-Object -ExpandProperty FullName
) + @($VersionSource)
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
if ($Sources.Count -eq 0) {
    throw "No GUI sources found under: $($SourceDirectories -join ', ')"
}
if ((Join-Path $Root "gui\SBMSGui.cs") -notin $Sources) {
    throw "Missing GUI entry source: $(Join-Path $Root 'gui\SBMSGui.cs')"
}
if (-not (Test-Path $Manifest)) {
    throw "Missing manifest: $Manifest"
}

& $Csc /nologo /target:winexe /optimize+ /win32manifest:$Manifest /out:$Out /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll @Sources
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $Out"
$versionInfo = (Get-Item -LiteralPath $Out).VersionInfo
if ([string]$versionInfo.FileVersion -ne [string]$BuildMetadata.WindowsVersion -or
    [string]$versionInfo.ProductVersion -ne [string]$BuildMetadata.SemVer) {
    throw "GUI version metadata mismatch. FileVersion=$($versionInfo.FileVersion) ProductVersion=$($versionInfo.ProductVersion)"
}
Write-Host "Version: $($BuildMetadata.SemVer) ($($BuildMetadata.WindowsVersion))"
