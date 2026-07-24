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

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-ConfigurationTests-" + [guid]::NewGuid().ToString("N"))
$TestExe = Join-Path $TestRoot "ConfigurationTests.exe"
$Sources = @(
    (Join-Path $Root "gui\Models\GuiConfig.cs"),
    (Join-Path $Root "gui\Services\XmlConfigurationStore.cs"),
    (Join-Path $Root "tests\ConfigurationTests.cs")
)
foreach ($source in $Sources) {
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing configuration test source: $source"
    }
}

try {
    New-Item -ItemType Directory -Path $TestRoot | Out-Null
    & $Csc /nologo /target:exe /optimize+ /out:$TestExe /reference:System.Xml.dll @Sources
    $compileExitCode = $LASTEXITCODE
    if ($compileExitCode -ne 0) {
        throw "Configuration test compilation failed with exit code $compileExitCode."
    }
    & $TestExe
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "Configuration tests failed with exit code $testExitCode."
    }
} finally {
    if (Test-Path -LiteralPath $TestRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
