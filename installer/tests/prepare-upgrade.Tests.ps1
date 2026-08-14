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

    It 'copies only persistent SBMS configuration files' {
        $expected = @{
            'config-v1.json' = '{"version":1,"target":"legacy"}'
            'config-v2.json' = '{"version":2,"groups":[{"id":0}]}'
            'config-profiles-v1.json' = '{"version":1,"active_profile":"default","profiles":[]}'
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

    It 'computes SHA256 without depending on PowerShell module discovery' {
        $inputFile = Join-Path $script:userStateRoot 'hash-input.txt'
        [IO.File]::WriteAllText(
            $inputFile,
            'abc',
            [Text.UTF8Encoding]::new($false)
        )

        Get-Sha256Hex -LiteralPath $inputFile | Should Be (
            'BA7816BF8F01CFEA414140DE5DAE2223' +
            'B00361A396177A9CB410FF61F20015AD'
        )
    }

    It 'verifies and marks each snapshot without creating an unused manifest' {
        $source = Join-Path $script:userStateRoot 'config-v2.json'
        [IO.File]::WriteAllText(
            $source,
            '{"version":2}',
            [Text.UTF8Encoding]::new($false)
        )

        Backup-SbmsConfiguration
        $snapshot = @(
            Get-ChildItem -LiteralPath (
                Join-Path $script:userStateRoot 'upgrade-backups'
            ) -Directory
        )[0]
        $copy = Join-Path $snapshot.FullName 'config-v2.json'

        Get-Sha256Hex -LiteralPath $copy |
            Should Be (Get-Sha256Hex -LiteralPath $source)
        [IO.File]::ReadAllText(
            (Join-Path $snapshot.FullName '.sbms-upgrade-snapshot-v1'),
            [Text.Encoding]::UTF8
        ) | Should Be 'SBMS upgrade snapshot v1'
        Test-OwnedUpgradeSnapshot -Snapshot $snapshot -BackupRoot (
            Join-Path $script:userStateRoot 'upgrade-backups'
        ) | Should Be $true
        Test-Path -LiteralPath (
            Join-Path $snapshot.FullName 'manifest.json'
        ) | Should Be $false
    }

    It 'adds a fresh snapshot on repeat upgrade and preserves retained history' {
        foreach ($name in @(
            'config-v1.json',
            'config-v2.json',
            'config-profiles-v1.json',
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

        Backup-SbmsConfiguration
        $snapshots = @(
            Get-ChildItem -LiteralPath $backupRoot -Directory
        )
        $snapshots.Count | Should Be 2
        $secondSnapshots = @(
            $snapshots | Where-Object Name -ne $firstSnapshot.Name
        )
        $secondSnapshots.Count | Should Be 1
        $secondSnapshot = $secondSnapshots[0]

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

        Test-Path -LiteralPath (
            Join-Path $firstSnapshot.FullName 'config-v1.json'
        ) -PathType Leaf | Should Be $true
        Test-Path -LiteralPath (
            Join-Path $firstSnapshot.FullName 'display-overrides-v1.json'
        ) -PathType Leaf | Should Be $true
    }

    It 'deletes only the oldest owned snapshot and preserves unmarked directories' {
        [IO.File]::WriteAllText(
            (Join-Path $script:userStateRoot 'config-v2.json'),
            '{"version":2}',
            [Text.UTF8Encoding]::new($false)
        )

        $backupRoot = Join-Path $script:userStateRoot 'upgrade-backups'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        1..5 | ForEach-Object {
            $ownedSnapshot = New-Item -ItemType Directory -Path (
                Join-Path $backupRoot ("20200101T00000000{0}Z" -f $_)
            ) -Force
            [IO.File]::WriteAllText(
                (Join-Path $ownedSnapshot.FullName '.sbms-upgrade-snapshot-v1'),
                'SBMS upgrade snapshot v1',
                [Text.UTF8Encoding]::new($false)
            )
        }
        $unmarked = Join-Path $backupRoot '20200101T000000000Z'
        New-Item -ItemType Directory -Path $unmarked -Force | Out-Null

        Backup-SbmsConfiguration

        $ownedSnapshots = @(
            Get-ChildItem -LiteralPath $backupRoot -Directory |
                Where-Object {
                Test-OwnedUpgradeSnapshot -Snapshot $_ -BackupRoot $backupRoot
            }
        )
        $ownedSnapshots.Count | Should Be 5
        Test-Path -LiteralPath $unmarked -PathType Container | Should Be $true
        Test-Path -LiteralPath (
            Join-Path $backupRoot '20200101T000000001Z'
        ) | Should Be $false
        Test-Path -LiteralPath (
            Join-Path $backupRoot '20200101T000000002Z'
        ) -PathType Container | Should Be $true
    }
}
