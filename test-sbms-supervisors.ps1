$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot
$Csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $Csc) {
    throw "Missing .NET Framework csc.exe"
}

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-SupervisorTests-" + [guid]::NewGuid().ToString("N"))
$TestExe = Join-Path $TestRoot "SupervisorTests.exe"
$Sources = @(
    (Join-Path $Root "gui\Services\ChildProcessJob.cs"),
    (Join-Path $Root "gui\Services\ProcessStopResult.cs"),
    (Join-Path $Root "gui\Services\NativeProcessSupervisor.cs"),
    (Join-Path $Root "gui\Services\DeviceHostSupervisor.cs"),
    (Join-Path $Root "tests\SupervisorTests.cs")
)
try {
    New-Item -ItemType Directory -Path $TestRoot | Out-Null
    & $Csc /nologo /target:exe /optimize+ "/out:$TestExe" @Sources
    if ($LASTEXITCODE -ne 0) {
        throw "Supervisor test compilation failed with exit code $LASTEXITCODE."
    }
    & $TestExe
    if ($LASTEXITCODE -ne 0) {
        throw "Supervisor tests failed with exit code $LASTEXITCODE."
    }
} finally {
    if (Test-Path -LiteralPath $TestRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
