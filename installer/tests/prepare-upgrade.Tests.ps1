$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maintenanceScript = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\maintenance.ps1')
)

Describe 'PrepareUpgrade maintenance contract' {
    BeforeAll {
        $loadRoot = Join-Path $TestDrive 'load-root'
        New-Item -ItemType Directory -Path $loadRoot -Force | Out-Null

        # Load the real function definitions without dispatching an action. This
        # keeps the contract tests isolated from live SBMS processes, tasks, and
        # drivers.
        $maintenanceSource = [IO.File]::ReadAllText(
            $maintenanceScript,
            [Text.Encoding]::UTF8
        )
        $dispatchMarker = 'switch ($Action) {'
        $dispatchIndex = $maintenanceSource.LastIndexOf(
            $dispatchMarker,
            [StringComparison]::Ordinal
        )
        if ($dispatchIndex -lt 0) {
            throw 'maintenance.ps1 action dispatcher was not found.'
        }
        $definitions = $maintenanceSource.Substring(0, $dispatchIndex)
        . ([scriptblock]::Create($definitions)) `
            -Action Stop `
            -InstallRoot $loadRoot
    }

    BeforeEach {
        $script:previousLocalAppData = $env:LOCALAPPDATA
        $script:caseRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:localAppData = Join-Path $script:caseRoot 'LocalAppData'
        $script:installRoot = Join-Path $script:caseRoot 'Program Files\SBMS'
        $script:userStateRoot = Join-Path $script:localAppData 'SBMS'
        $script:snapshotRoot = Join-Path $script:caseRoot 'ProgramData\SBMS\upgrade-backup\current'

        New-Item -ItemType Directory -Path $script:userStateRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $script:installRoot -Force | Out-Null
        $env:LOCALAPPDATA = $script:localAppData
        $script:configRoot = $script:userStateRoot
    }

    AfterEach {
        $env:LOCALAPPDATA = $script:previousLocalAppData
    }

    It 'exposes PrepareUpgrade as a supported action' {
        $source = [IO.File]::ReadAllText(
            $maintenanceScript,
            [Text.Encoding]::UTF8
        )

        $source | Should Match "ValidateSet\([^\)]*'PrepareUpgrade'"
        $source | Should Match "'PrepareUpgrade'\s*\{"
    }

    It 'stops the old tray before taking the snapshot' {
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            $maintenanceScript,
            [ref]$tokens,
            [ref]$parseErrors
        )
        @($parseErrors).Count | Should Be 0

        $prepare = $ast.Find(
            {
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Prepare-SbmsUpgrade'
            },
            $true
        )
        $prepare | Should Not BeNullOrEmpty

        $body = $prepare.Extent.Text
        $stopIndex = $body.IndexOf('Stop-Sbms', [StringComparison]::Ordinal)
        $snapshotIndex = $body.IndexOf(
            'Backup-SbmsConfiguration',
            [StringComparison]::Ordinal
        )
        ($stopIndex -ge 0) | Should Be $true
        ($snapshotIndex -gt $stopIndex) | Should Be $true
    }

    It 'does not create a backup directory when no configuration exists' {
        Backup-SbmsConfiguration

        Test-Path -LiteralPath (
            Join-Path $script:userStateRoot 'upgrade-backups'
        ) | Should Be $false
    }

    It 'copies only v1 v2 and display override configuration' {
        $expected = @{
            'config-v1.json' = '{"version":1,"target":"legacy"}'
            'config-v2.json' = '{"version":2,"groups":[{"id":0}]}'
            'display-overrides-v1.json' = '{"version":1,"displays":{}}'
        }
        foreach ($entry in $expected.GetEnumerator()) {
            [IO.File]::WriteAllText(
                (Join-Path $script:userStateRoot $entry.Key),
                $entry.Value,
                [Text.UTF8Encoding]::new($false)
            )
        }
        [IO.File]::WriteAllText(
            (Join-Path $script:userStateRoot 'unrelated.txt'),
            'must not be copied',
            [Text.UTF8Encoding]::new($false)
        )

        Backup-SbmsConfiguration
        $snapshot = @(
            Get-ChildItem -LiteralPath (
                Join-Path $script:userStateRoot 'upgrade-backups'
            ) -Directory
        )
        $snapshot.Count | Should Be 1
        $snapshot = $snapshot[0]

        foreach ($entry in $expected.GetEnumerator()) {
            $copy = Join-Path $snapshot.FullName $entry.Key
            Test-Path -LiteralPath $copy -PathType Leaf | Should Be $true
            [IO.File]::ReadAllText($copy, [Text.Encoding]::UTF8) |
                Should Be $entry.Value
        }
        Test-Path -LiteralPath (
            Join-Path $snapshot.FullName 'unrelated.txt'
        ) | Should Be $false
    }

    It 'records length and SHA256 for every copied file' {
        $files = @(
            'config-v1.json',
            'config-v2.json',
            'display-overrides-v1.json'
        )
        foreach ($name in $files) {
            [IO.File]::WriteAllText(
                (Join-Path $script:userStateRoot $name),
                "content-$name",
                [Text.UTF8Encoding]::new($false)
            )
        }

        Backup-SbmsConfiguration
        $snapshot = @(
            Get-ChildItem -LiteralPath (
                Join-Path $script:userStateRoot 'upgrade-backups'
            ) -Directory
        )
        $snapshot.Count | Should Be 1
        $snapshot = $snapshot[0]

        $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (
            Join-Path $snapshot.FullName 'manifest.json'
        ) | ConvertFrom-Json
        @($manifest.files).Count | Should Be 3

        foreach ($name in $files) {
            $source = Join-Path $script:userStateRoot $name
            $record = @($manifest.files) |
                Where-Object name -eq $name
            @($record).Count | Should Be 1
            $record.bytes | Should Be (Get-Item -LiteralPath $source).Length
            $record.sha256.ToLowerInvariant() |
                Should Be (
                    (Get-FileHash -LiteralPath $source -Algorithm SHA256).
                        Hash.ToLowerInvariant()
                )
        }
    }

    It 'adds a fresh snapshot on repeat upgrade and preserves the old one' {
        foreach ($name in @(
            'config-v1.json',
            'config-v2.json',
            'display-overrides-v1.json'
        )) {
            [IO.File]::WriteAllText(
                (Join-Path $script:userStateRoot $name),
                "first-$name",
                [Text.UTF8Encoding]::new($false)
            )
        }
        Backup-SbmsConfiguration
        $backupRoot = Join-Path $script:userStateRoot 'upgrade-backups'
        $firstSnapshots = @(
            Get-ChildItem -LiteralPath $backupRoot -Directory
        )
        $firstSnapshots.Count | Should Be 1
        $firstSnapshot = $firstSnapshots[0]

        Remove-Item -LiteralPath (
            Join-Path $script:userStateRoot 'config-v1.json'
        ) -Force
        Remove-Item -LiteralPath (
            Join-Path $script:userStateRoot 'display-overrides-v1.json'
        ) -Force
        [IO.File]::WriteAllText(
            (Join-Path $script:userStateRoot 'config-v2.json'),
            'second-config-v2',
            [Text.UTF8Encoding]::new($false)
        )
        Start-Sleep -Milliseconds 5

        Backup-SbmsConfiguration
        $snapshots = @(
            Get-ChildItem -LiteralPath $backupRoot -Directory |
                Sort-Object CreationTime
        )
        $snapshots.Count | Should Be 2
        $secondSnapshot = $snapshots[-1]

        Test-Path -LiteralPath (
            Join-Path $secondSnapshot.FullName 'config-v1.json'
        ) | Should Be $false
        Test-Path -LiteralPath (
            Join-Path $secondSnapshot.FullName 'display-overrides-v1.json'
        ) | Should Be $false
        [IO.File]::ReadAllText(
            (Join-Path $secondSnapshot.FullName 'config-v2.json'),
            [Text.Encoding]::UTF8
        ) | Should Be 'second-config-v2'

        $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (
            Join-Path $secondSnapshot.FullName 'manifest.json'
        ) | ConvertFrom-Json
        @($manifest.files).Count | Should Be 1
        $manifest.files.name | Should Be 'config-v2.json'
        Test-Path -LiteralPath (
            Join-Path $firstSnapshot.FullName 'config-v1.json'
        ) -PathType Leaf | Should Be $true
        Test-Path -LiteralPath (
            Join-Path $firstSnapshot.FullName 'display-overrides-v1.json'
        ) -PathType Leaf | Should Be $true
    }
}
