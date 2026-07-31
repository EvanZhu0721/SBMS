$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot
$script:ManagerScript = Join-Path `
    $script:RepositoryRoot `
    'installer\manage-sunshine-instance.ps1'
$script:HostExecutable = (Get-Process -Id $PID).Path
$script:OriginalLocalAppData = $env:LOCALAPPDATA
$script:OriginalSunshineExe = $env:SBMS_SUNSHINE_EXE
$script:TestRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "sbms-sunshine-pester-$([Guid]::NewGuid().ToString('N'))"
$script:FakeRoot = Join-Path $script:TestRoot 'Fake Sunshine'
$script:FakeExecutable = Join-Path $script:FakeRoot 'sunshine.exe'
$script:FakeGlobalConfiguration = Join-Path `
    $script:FakeRoot `
    'config\sunshine.conf'

function Invoke-Manager {
    param(
        [Parameter(Mandatory)]
        [string[]]$ManagerArguments
    )

    $nativeArguments = @(
        '-NoLogo'
        '-NoProfile'
        '-NonInteractive'
        '-File'
        $script:ManagerScript
    ) + $ManagerArguments
    $output = & $script:HostExecutable @nativeArguments
    $exitCode = $LASTEXITCODE
    $outputLines = @($output)
    return [pscustomobject]@{
        ExitCode = $exitCode
        Text     = ($outputLines -join [Environment]::NewLine)
        Json     = if ($outputLines.Count -gt 0) {
            ($outputLines -join [Environment]::NewLine) | ConvertFrom-Json
        } else {
            $null
        }
    }
}

function Get-FreeSunshineBasePort {
    foreach ($basePort in 56000..62000) {
        if ($basePort -gt 65514) {
            break
        }

        $listeners = @()
        $udpClients = @()
        try {
            foreach ($offset in @(-5, 0, 1, 21)) {
                $listener = [Net.Sockets.TcpListener]::new(
                    [Net.IPAddress]::Any,
                    ($basePort + $offset)
                )
                $listener.Server.ExclusiveAddressUse = $true
                $listener.Start()
                $listeners += $listener
            }
            foreach ($offset in @(9, 10, 11)) {
                $client = [Net.Sockets.UdpClient]::new()
                $client.Client.ExclusiveAddressUse = $true
                $client.Client.Bind(
                    [Net.IPEndPoint]::new(
                        [Net.IPAddress]::Any,
                        ($basePort + $offset)
                    )
                )
                $udpClients += $client
            }
            return $basePort
        } catch {
            # Continue scanning after releasing any partially acquired family.
        } finally {
            foreach ($listener in $listeners) {
                $listener.Stop()
            }
            foreach ($client in $udpClients) {
                $client.Dispose()
            }
        }
    }

    throw 'No free Sunshine test port family was found.'
}

New-Item -ItemType Directory `
    -Path (Join-Path $script:FakeRoot 'config') `
    -Force |
    Out-Null

$fakeSourcePath = Join-Path $script:TestRoot 'fake-sunshine.rs'
$fakeSource = @'
use std::env;
use std::fs;
use std::net::TcpListener;
use std::path::Path;
use std::thread;
use std::time::Duration;

fn main() {
    let arguments: Vec<String> = env::args().skip(1).collect();
    let configuration = arguments.first().cloned().unwrap_or_else(|| {
        std::process::exit(2);
    });
    let argument_log = Path::new(&configuration)
        .parent()
        .unwrap_or_else(|| {
            std::process::exit(2);
        })
        .join("argv.txt");
    fs::write(argument_log, arguments.join("\n")).unwrap_or_else(|_| {
        std::process::exit(2);
    });
    let contents = fs::read_to_string(configuration).unwrap_or_else(|_| {
        std::process::exit(2);
    });
    let port = contents
        .lines()
        .find_map(|line| {
            let (name, value) = line.split_once('=')?;
            if name.trim().eq_ignore_ascii_case("port") {
                value.trim().parse::<u16>().ok()
            } else {
                None
            }
        })
        .unwrap_or_else(|| {
            std::process::exit(3);
        });
    let _listener = TcpListener::bind(("127.0.0.1", port + 1)).unwrap_or_else(|_| {
        std::process::exit(4);
    });
    loop {
        thread::sleep(Duration::from_secs(60));
    }
}
'@
[IO.File]::WriteAllText(
    $fakeSourcePath,
    $fakeSource,
    [Text.UTF8Encoding]::new($false)
)
$rustCompiler = (Get-Command rustc -ErrorAction Stop).Source
$compilerArguments = @(
    '--edition=2021'
    $fakeSourcePath
    '-o'
    $script:FakeExecutable
)
& $rustCompiler @compilerArguments
$compilerExitCode = $LASTEXITCODE
if ($compilerExitCode -ne 0) {
    throw "rustc failed to build the fake Sunshine process (exit $compilerExitCode)."
}

[IO.File]::WriteAllText(
    (Join-Path $script:FakeRoot 'config\apps.json'),
    '{"env":{},"apps":[]}',
    [Text.UTF8Encoding]::new($false)
)
[IO.File]::WriteAllText(
    $script:FakeGlobalConfiguration,
    'global-config-sentinel',
    [Text.UTF8Encoding]::new($false)
)

$env:LOCALAPPDATA = Join-Path $script:TestRoot 'Local AppData'
$env:SBMS_SUNSHINE_EXE = $script:FakeExecutable

Describe 'manage-sunshine-instance.ps1' {
    It 'starts an isolated instance, reports JSON, and stops only that PID' {
        $groupId = "pester-$([Guid]::NewGuid().ToString('N'))"
        $displayId = '{23f8cce0-fefe-4a5b-9e14-0123456789ab}'
        $port = Get-FreeSunshineBasePort

        $start = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Start'
            '-GroupId'
            $groupId
            '-DisplayId'
            $displayId
            '-Port'
            [string]$port
        )

        $start.ExitCode | Should Be 0
        $start.Json.ok | Should Be $true
        $start.Json.status | Should Be 'started'
        $start.Json.port | Should Be $port
        (Get-Process -Id ([int]$start.Json.pid) -ErrorAction Stop).HasExited |
            Should Be $false

        $instance = Join-Path `
            $env:LOCALAPPDATA `
            "SBMS\sunshine\group-$groupId"
        $configuration = [IO.File]::ReadAllText(
            (Join-Path $instance 'sunshine.conf'),
            [Text.Encoding]::UTF8
        )
        $configuration | Should Match "port = $port"
        $configuration | Should Match 'upnp = disabled'
        $configuration | Should Match 'dd_configuration_option = disabled'
        $configuration | Should Match ([regex]::Escape(
            (Join-Path $instance 'sunshine_state.json').Replace('\', '/')
        ))
        [IO.File]::ReadAllText(
            $script:FakeGlobalConfiguration,
            [Text.Encoding]::UTF8
        ) | Should Be 'global-config-sentinel'

        $stop = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Stop'
            '-GroupId'
            $groupId
        )
        if ($stop.ExitCode -ne 0) {
            throw "Stop failed: $($stop.Text)"
        }
        $stop.ExitCode | Should Be 0
        $stop.Json.ok | Should Be $true
        $stop.Json.status | Should Be 'stopped'
        $remaining = Get-CimInstance `
            -ClassName Win32_Process `
            -Filter "ProcessId = $([int]$start.Json.pid)" `
            -ErrorAction Stop
        $remaining | Should BeNullOrEmpty
    }

    It 'is idempotent for an already-running matching Start request' {
        $groupId = "pester-$([Guid]::NewGuid().ToString('N'))"
        $displayId = '{23f8cce0-fefe-4a5b-9e14-abcdef012345}'
        $port = Get-FreeSunshineBasePort
        $arguments = @(
            '-Action'
            'Start'
            '-GroupId'
            $groupId
            '-DisplayId'
            $displayId
            '-Port'
            [string]$port
        )

        $first = Invoke-Manager -ManagerArguments $arguments
        $second = Invoke-Manager -ManagerArguments $arguments

        $first.ExitCode | Should Be 0
        $second.ExitCode | Should Be 0
        $second.Json.status | Should Be 'already_running'
        $second.Json.pid | Should Be $first.Json.pid

        $null = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Stop'
            '-GroupId'
            $groupId
        )
    }

    It 'passes capture only as an exact runtime argument' {
        $cases = @(
            [pscustomobject]@{ Capture = 'auto'; Expected = $null }
            [pscustomobject]@{ Capture = 'ddx'; Expected = 'capture=ddx' }
            [pscustomobject]@{ Capture = 'wgc'; Expected = 'capture=wgc' }
        )

        foreach ($case in $cases) {
            $groupId = "pester-$([Guid]::NewGuid().ToString('N'))"
            $displayId = '{23f8cce0-fefe-4a5b-9e14-aabbccddeeff}'
            $port = Get-FreeSunshineBasePort
            $start = Invoke-Manager -ManagerArguments @(
                '-Action'
                'Start'
                '-GroupId'
                $groupId
                '-DisplayId'
                $displayId
                '-Port'
                [string]$port
                '-Capture'
                $case.Capture
            )

            $start.ExitCode | Should Be 0
            $instance = Join-Path `
                $env:LOCALAPPDATA `
                "SBMS\sunshine\group-$groupId"
            $arguments = @(
                Get-Content `
                    -LiteralPath (Join-Path $instance 'argv.txt') `
                    -Encoding UTF8
            )
            $arguments[0] | Should Be (Join-Path $instance 'sunshine.conf')
            if ($null -eq $case.Expected) {
                $arguments.Count | Should Be 1
            } else {
                $arguments.Count | Should Be 2
                $arguments[1] | Should Be $case.Expected
            }

            $configuration = Get-Content `
                -LiteralPath (Join-Path $instance 'sunshine.conf') `
                -Encoding UTF8 `
                -Raw
            $configuration | Should Not Match '(?m)^\s*capture\s*='
            $manifest = Get-Content `
                -LiteralPath (Join-Path $instance 'instance.json') `
                -Encoding UTF8 `
                -Raw |
                ConvertFrom-Json
            ($manifest.PSObject.Properties.Name -contains 'capture') |
                Should Be $false
            [IO.File]::ReadAllText(
                $script:FakeGlobalConfiguration,
                [Text.Encoding]::UTF8
            ) | Should Be 'global-config-sentinel'

            $stop = Invoke-Manager -ManagerArguments @(
                '-Action'
                'Stop'
                '-GroupId'
                $groupId
            )
            $stop.ExitCode | Should Be 0
        }
    }

    It 'uses capture mode when matching or restarting a managed instance' {
        $groupId = "pester-$([Guid]::NewGuid().ToString('N'))"
        $displayId = '{23f8cce0-fefe-4a5b-9e14-ffeeddccbbaa}'
        $port = Get-FreeSunshineBasePort
        $wgcArguments = @(
            '-Action'
            'Start'
            '-GroupId'
            $groupId
            '-DisplayId'
            $displayId
            '-Port'
            [string]$port
            '-Capture'
            'wgc'
        )
        $first = Invoke-Manager -ManagerArguments $wgcArguments
        $matching = Invoke-Manager -ManagerArguments $wgcArguments

        $first.ExitCode | Should Be 0
        $matching.ExitCode | Should Be 0
        $matching.Json.status | Should Be 'already_running'
        $matching.Json.pid | Should Be $first.Json.pid

        $mismatch = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Start'
            '-GroupId'
            $groupId
            '-DisplayId'
            $displayId
            '-Port'
            [string]$port
            '-Capture'
            'auto'
        )
        $mismatch.ExitCode | Should Not Be 0
        $mismatch.Json.message | Should Match 'use Restart'
        (Get-Process -Id ([int]$first.Json.pid) -ErrorAction Stop).HasExited |
            Should Be $false

        $restart = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Restart'
            '-GroupId'
            $groupId
            '-DisplayId'
            $displayId
            '-Port'
            [string]$port
            '-Capture'
            'auto'
        )
        $restart.ExitCode | Should Be 0
        $restart.Json.pid | Should Not Be $first.Json.pid
        $restart.Json.port | Should Be $first.Json.port
        Get-CimInstance `
            -ClassName Win32_Process `
            -Filter "ProcessId = $([int]$first.Json.pid)" `
            -ErrorAction Stop |
            Should BeNullOrEmpty
        (Get-Process -Id ([int]$restart.Json.pid) -ErrorAction Stop).HasExited |
            Should Be $false
        $instance = Join-Path `
            $env:LOCALAPPDATA `
            "SBMS\sunshine\group-$groupId"
        @(Get-Content -LiteralPath (Join-Path $instance 'argv.txt') -Encoding UTF8).Count |
            Should Be 1

        $null = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Stop'
            '-GroupId'
            $groupId
        )
    }

    It 'scans complete port families by 27 and StopAll needs no GroupId' {
        $firstGroup = "pester-$([Guid]::NewGuid().ToString('N'))"
        $secondGroup = "pester-$([Guid]::NewGuid().ToString('N'))"
        $displayId = '{23f8cce0-fefe-4a5b-9e14-fedcba987654}'
        $preferredPort = Get-FreeSunshineBasePort

        $first = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Start'
            '-GroupId'
            $firstGroup
            '-DisplayId'
            $displayId
            '-Port'
            [string]$preferredPort
        )
        $second = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Start'
            '-GroupId'
            $secondGroup
            '-DisplayId'
            $displayId
            '-Port'
            [string]$preferredPort
        )

        $first.ExitCode | Should Be 0
        $second.ExitCode | Should Be 0
        $first.Json.port | Should Be $preferredPort
        $second.Json.port | Should Not Be $preferredPort
        (([int]$second.Json.port - $preferredPort) % 27) | Should Be 0

        $stopAll = Invoke-Manager -ManagerArguments @(
            '-Action'
            'StopAll'
        )
        $stopAll.ExitCode | Should Be 0
        $stopAll.Json.ok | Should Be $true
        $stopAll.Json.status | Should Be 'stopped_all'
        $stopAll.Json.stoppedCount | Should Be 2

        $firstRemaining = Get-CimInstance `
            -ClassName Win32_Process `
            -Filter "ProcessId = $([int]$first.Json.pid)" `
            -ErrorAction Stop
        $secondRemaining = Get-CimInstance `
            -ClassName Win32_Process `
            -Filter "ProcessId = $([int]$second.Json.pid)" `
            -ErrorAction Stop
        $firstRemaining | Should BeNullOrEmpty
        $secondRemaining | Should BeNullOrEmpty
    }

    It 'refuses to stop a PID whose executable or command line is not owned' {
        $groupId = "pester-$([Guid]::NewGuid().ToString('N'))"
        $instance = Join-Path `
            $env:LOCALAPPDATA `
            "SBMS\sunshine\group-$groupId"
        New-Item -ItemType Directory -Path $instance -Force | Out-Null
        $manifest = [ordered]@{
            schema_version = 1
            managed_by     = 'SBMS'
            group_id       = $groupId
            display_id     = '{23f8cce0-fefe-4a5b-9e14-abcdef012345}'
            port           = 54321
            pid            = $PID
            sunshine_exe   = $script:FakeExecutable
            config_path    = Join-Path $instance 'sunshine.conf'
            log_path       = Join-Path $instance 'sunshine.log'
            started_at     = [DateTimeOffset]::Now.ToString('o')
        }
        [IO.File]::WriteAllText(
            (Join-Path $instance 'instance.json'),
            ($manifest | ConvertTo-Json -Compress),
            [Text.UTF8Encoding]::new($false)
        )

        $stop = Invoke-Manager -ManagerArguments @(
            '-Action'
            'Stop'
            '-GroupId'
            $groupId
        )

        $stop.ExitCode | Should Not Be 0
        $stop.Json.ok | Should Be $false
        $stop.Json.message | Should Match 'not the Sunshine executable'
        (Get-Process -Id $PID -ErrorAction Stop).HasExited | Should Be $false
    }
}

try {
    Get-ChildItem `
        -LiteralPath (Join-Path $env:LOCALAPPDATA 'SBMS\sunshine') `
        -Filter 'instance.json' `
        -Recurse `
        -File `
        -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $manifest = Get-Content `
                    -LiteralPath $_.FullName `
                    -Encoding UTF8 `
                    -Raw |
                    ConvertFrom-Json
                if ([string]$manifest.managed_by -eq 'SBMS') {
                    $null = Invoke-Manager -ManagerArguments @(
                        '-Action'
                        'Stop'
                        '-GroupId'
                        ([string]$manifest.group_id)
                    )
                }
            } catch {
                # Test cleanup is best-effort; the temporary root is unique.
            }
        }
} finally {
    $env:LOCALAPPDATA = $script:OriginalLocalAppData
    $env:SBMS_SUNSHINE_EXE = $script:OriginalSunshineExe
    if (Test-Path -LiteralPath $script:TestRoot) {
        Remove-Item -LiteralPath $script:TestRoot -Recurse -Force
    }
}
