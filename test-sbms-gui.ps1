param(
    [int] $ProbeTimeoutSeconds = 15
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

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

$TestRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "SBMS GUI Tests With Spaces\" + [guid]::NewGuid().ToString("N"))
$ProbeExe = Join-Path $TestRoot "SBMS-ProbeHost.exe"
$previousProbeMutex = $env:SBMS_GUI_PROBE_MUTEX
$probeMutexId = [guid]::NewGuid()
$env:SBMS_GUI_PROBE_MUTEX = $probeMutexId.ToString()
$testSucceeded = $false

function Invoke-Probe {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Argument
    )

    $png = Join-Path $TestRoot "$Name.png"
    $quotedPng = '"' + $png.Replace('"', '\"') + '"'
    $process = Start-Process -FilePath $ProbeExe -ArgumentList "$Argument $quotedPng" -PassThru
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
    Import-Module (Join-Path $Root "build\SBMS.Version.psm1") -Force
    $versionSource = Join-Path $TestRoot "SBMS.Version.g.cs"
    Write-SBMSGeneratedFile -LiteralPath $versionSource -Content (
        New-SBMSCSharpVersionSource `
            -Metadata (Get-SBMSBuildMetadata -RepositoryRoot $Root) `
            -AssemblyTitle "SBMS GUI probe" `
            -FileDescription "SBMS GUI regression probe"
    ) | Out-Null
    $Sources += $versionSource
    $mainSource = [IO.File]::ReadAllText(
        (Join-Path $Root "gui\SBMSGui.cs"),
        [Text.Encoding]::UTF8)
    if ($mainSource -notmatch 'Local\\SBMS\.Gui\.Singleton') {
        throw "GUI session singleton guard is missing."
    }
    $singletonIndex = $mainSource.IndexOf('Local\SBMS.Gui.Singleton', [StringComparison]::Ordinal)
    $probeIndex = $mainSource.IndexOf('--config-probe', [StringComparison]::Ordinal)
    if ($singletonIndex -lt 0 -or $probeIndex -lt 0 -or $singletonIndex -gt $probeIndex) {
        throw "GUI probe paths bypass the session singleton guard."
    }

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
        Invoke-Probe -Name "config-binding-probe" -Argument "--config-binding-probe"
    )

    $createdNew = $false
    $instanceMutex = [Threading.Mutex]::new(
        $true,
        ("Local\SBMS.Gui.Probe.Singleton." + $probeMutexId.ToString("N")),
        [ref]$createdNew)
    try {
        if (-not $createdNew) {
            throw "GUI singleton test could not acquire a clean mutex."
        }
        $blockedProbePath = Join-Path $TestRoot "blocked-probe.png"
        $blockedProbe = Start-Process `
            -FilePath $ProbeExe `
            -ArgumentList ('--lock-probe "' + $blockedProbePath.Replace('"', '\"') + '"') `
            -PassThru
        try {
            if (-not $blockedProbe.WaitForExit(5000)) {
                $blockedProbe.Kill()
                throw "Second GUI probe did not fail fast behind the singleton."
            }
            if ($blockedProbe.ExitCode -ne 3) {
                throw "Second GUI probe bypassed the singleton; exit=$($blockedProbe.ExitCode)."
            }
            if (Test-Path -LiteralPath $blockedProbePath) {
                throw "Blocked GUI probe performed runtime work."
            }
        } finally {
            $blockedProbe.Dispose()
        }
    } finally {
        $instanceMutex.ReleaseMutex()
        $instanceMutex.Dispose()
    }

    $configText = [IO.File]::ReadAllText((Join-Path $TestRoot "config-probe.png.txt"), [Text.Encoding]::UTF8)
    # Keep this source expression ASCII-only so Windows PowerShell 5.1 does not
    # reinterpret a BOM-less UTF-8 test script through the active ANSI codepage.
    $expectedConfigText = (-join [char[]]@(0x6620, 0x5c04, 0x7ec4, 0x4e00)) + " | +"
    if (-not $configText.Contains($expectedConfigText)) {
        throw "Config probe semantic output changed: $configText"
    }
    $lockText = [IO.File]::ReadAllText((Join-Path $TestRoot "lock-probe.png.txt"), [Text.Encoding]::UTF8)
    if (-not $lockText.Contains("visible=True")) {
        throw "Lock probe semantic output changed: $lockText"
    }

    foreach ($invalidArguments in @(
        "--unknown-probe",
        "--config-probe"
    )) {
        $invalidProbe = Start-Process `
            -FilePath $ProbeExe `
            -ArgumentList $invalidArguments `
            -PassThru
        try {
            if (-not $invalidProbe.WaitForExit(5000)) {
                $invalidProbe.Kill()
                throw "Malformed probe invocation opened the full GUI: $invalidArguments"
            }
            if ($invalidProbe.ExitCode -ne 4) {
                throw "Malformed probe invocation did not fail closed: $invalidArguments exit=$($invalidProbe.ExitCode)"
            }
        } finally {
            $invalidProbe.Dispose()
        }
    }
    $bindingText = [IO.File]::ReadAllText((Join-Path $TestRoot "config-binding-probe.png.txt"), [Text.Encoding]::UTF8)
    if (-not $bindingText.Contains("labelPreserved=True") -or
        -not $bindingText.Contains("tagNull=True") -or
        -not $bindingText.Contains("devicePreserved=True") -or
        -not $bindingText.Contains("persistentIdPreserved=True") -or
        -not $bindingText.Contains("startBlocked=True") -or
        -not $bindingText.Contains("lifecycleIdle=True") -or
        -not $bindingText.Contains("feedbackVisible=True") -or
        -not $bindingText.Contains("backupUnchanged=True") -or
        -not $bindingText.Contains("primaryUnchanged=True")) {
        throw "Configuration binding probe silently retargeted a stale display: $bindingText"
    }

    $results | Format-Table -AutoSize
    Write-Host "GUI probes passed."
    $testSucceeded = $true
} finally {
    $env:SBMS_GUI_PROBE_MUTEX = $previousProbeMutex
    if ($testSucceeded -and (Test-Path -LiteralPath $TestRoot)) {
        Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
    } elseif (-not $testSucceeded) {
        Write-Warning "GUI probe artifacts retained for diagnosis: $TestRoot"
    }
}
