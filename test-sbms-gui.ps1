param(
    [int] $ProbeTimeoutSeconds = 15
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
if ($Sources.Count -eq 0) {
    throw "No GUI sources found under: $($SourceDirectories -join ', ')"
}

$CscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$Csc = $CscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $Csc) {
    throw "Missing .NET Framework csc.exe"
}

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ("SBMS-GuiTests-" + [guid]::NewGuid().ToString("N"))
$ProbeExe = Join-Path $TestRoot "SBMS-ProbeHost.exe"

function Invoke-Probe {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Argument
    )

    $png = Join-Path $TestRoot "$Name.png"
    $process = Start-Process -FilePath $ProbeExe -ArgumentList @($Argument, $png) -PassThru
    try {
        if (-not $process.WaitForExit($ProbeTimeoutSeconds * 1000)) {
            $process.Kill()
            throw "Probe '$Name' exceeded ${ProbeTimeoutSeconds}s."
        }
        if ($process.ExitCode -ne 0) {
            throw "Probe '$Name' exited with code $($process.ExitCode)."
        }
    } finally {
        $process.Dispose()
    }

    if (-not (Test-Path -LiteralPath $png)) {
        throw "Probe '$Name' did not produce a PNG."
    }
    $item = Get-Item -LiteralPath $png
    if ($item.Length -le 0) {
        throw "Probe '$Name' produced an empty PNG."
    }

    $image = [Drawing.Image]::FromFile($png)
    try {
        if ($image.Width -le 0 -or $image.Height -le 0) {
            throw "Probe '$Name' produced an invalid image size."
        }
        [pscustomobject]@{
            Name = $Name
            ExitCode = 0
            Bytes = $item.Length
            Width = $image.Width
            Height = $image.Height
        }
    } finally {
        $image.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $TestRoot | Out-Null

    # Issue #12: probes use a temporary asInvoker host so UI regression checks
    # can run without weakening the administrator manifest of the real product.
    & $Csc /nologo /target:winexe /optimize+ /out:$ProbeExe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll @Sources
    if ($LASTEXITCODE -ne 0) {
        throw "Probe-host compilation failed with exit code $LASTEXITCODE."
    }

    $results = @(
        Invoke-Probe -Name "config-probe" -Argument "--config-probe"
        Invoke-Probe -Name "risk-probe" -Argument "--risk-probe"
        Invoke-Probe -Name "stream-config-probe" -Argument "--stream-config-probe"
        Invoke-Probe -Name "lock-probe" -Argument "--lock-probe"
    )

    $configText = [IO.File]::ReadAllText((Join-Path $TestRoot "config-probe.png.txt"), [Text.Encoding]::UTF8)
    if (-not $configText.Contains("映射组一 | +")) {
        throw "Config probe semantic output changed: $configText"
    }
    $lockText = [IO.File]::ReadAllText((Join-Path $TestRoot "lock-probe.png.txt"), [Text.Encoding]::UTF8)
    if (-not $lockText.Contains("visible=True")) {
        throw "Lock probe semantic output changed: $lockText"
    }

    $results | Format-Table -AutoSize
    Write-Host "GUI probes passed."
} finally {
    if (Test-Path -LiteralPath $TestRoot) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
