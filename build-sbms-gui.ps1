param(
    [string] $OutputName = "SBMS.exe"
)

$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
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
)
$Manifest = Join-Path $Root "gui\SBMSGui.manifest"
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
