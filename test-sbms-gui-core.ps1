$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$CscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$Csc = $CscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $Csc) {
    throw "Missing .NET Framework csc.exe"
}

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-GuiCoreTests-" + [guid]::NewGuid().ToString("N"))
$TestExe = Join-Path $TestRoot "GuiCoreTests.exe"
$Sources = @(
    (Join-Path $Root "gui\Core\BridgeLifecycle.cs"),
    (Join-Path $Root "gui\Core\ResolutionMath.cs"),
    (Join-Path $Root "gui\Models\GuiConfig.cs"),
    (Join-Path $Root "gui\Models\DisplayModels.cs"),
    (Join-Path $Root "gui\Services\XmlConfigurationStore.cs"),
    (Join-Path $Root "gui\Services\TopologyDiscoveryService.cs"),
    (Join-Path $Root "gui\Services\TopologyRecoveryService.cs"),
    (Join-Path $Root "gui\Services\TopologyRecoveryWorkflow.cs"),
    (Join-Path $Root "gui\Services\DisplayModeService.cs"),
    (Join-Path $Root "tests\GuiCoreTests.cs")
)
foreach ($source in $Sources) {
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing test source: $source"
    }
}

try {
    New-Item -ItemType Directory -Path $TestRoot | Out-Null
    & $Csc /nologo /target:exe /optimize+ /out:$TestExe /reference:System.Xml.dll @Sources
    if ($LASTEXITCODE -ne 0) {
        throw "GUI core test compilation failed with exit code $LASTEXITCODE."
    }
    & $TestExe
    if ($LASTEXITCODE -ne 0) {
        throw "GUI core tests failed with exit code $LASTEXITCODE."
    }
} finally {
    if (Test-Path -LiteralPath $TestRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
