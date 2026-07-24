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

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-ProcessJobTests-" + [guid]::NewGuid().ToString("N"))
$TestExe = Join-Path $TestRoot "ProcessJobTests.exe"
$StatePath = Join-Path $TestRoot "abrupt-owner.txt"
$Sources = @(
    (Join-Path $Root "gui\Services\ChildProcessJob.cs"),
    (Join-Path $Root "tests\ProcessJobTests.cs")
)

try {
    New-Item -ItemType Directory -Path $TestRoot | Out-Null
    & $Csc /nologo /target:exe /optimize+ /out:$TestExe @Sources
    if ($LASTEXITCODE -ne 0) {
        throw "Process job test compilation failed with exit code $LASTEXITCODE."
    }

    & $TestExe
    if ($LASTEXITCODE -ne 0) {
        throw "Process job self-test failed with exit code $LASTEXITCODE."
    }

    $owner = Start-Process -FilePath $TestExe -ArgumentList @("owner", $StatePath) -PassThru
    if (-not $owner.WaitForExit(5000)) {
        $owner.Kill()
        throw "Abrupt owner did not exit within the bounded timeout."
    }
    if (-not (Test-Path -LiteralPath $StatePath)) {
        throw "Abrupt owner did not publish its child identity."
    }
    $state = (Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8).Trim()
    if ($state.StartsWith("ERROR ", [StringComparison]::Ordinal)) {
        throw $state
    }
    $childId = 0
    if (-not [int]::TryParse($state, [ref]$childId)) {
        throw "Abrupt owner published an invalid child PID: $state"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
        if ($null -eq $child) {
            break
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -ne (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
        throw "Child process $childId survived abrupt closure of the owner job handle."
    }

    Write-Host "Abrupt-owner process job test passed."
} finally {
    if (Test-Path -LiteralPath $TestRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
