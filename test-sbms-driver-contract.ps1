[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Assert-Contract {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "SBMS driver contract failed: $Message"
    }
}

$root = $PSScriptRoot
$driverPath = Join-Path $root 'Windows-driver-samples\video\IndirectDisplay\IddSampleDriver\Driver.cpp'
$headerPath = Join-Path $root 'Windows-driver-samples\video\IndirectDisplay\IddSampleDriver\Driver.h'
$driver = Get-Content -LiteralPath $driverPath -Raw -Encoding UTF8
$header = Get-Content -LiteralPath $headerPath -Raw -Encoding UTF8

foreach ($sampleIdentity in @(
        'S2719DGF',
        'Y27fA',
        'DELD0E6',
        'CoCreateGuid',
        'IddSample Device',
        'IddSample Model',
        'pEndPointManufacturerName = L"Microsoft"')) {
    Assert-Contract ($driver -notmatch [regex]::Escape($sampleIdentity)) "sample or random identity remains: $sampleIdentity"
}

$edidMatch = [regex]::Match(
    $driver,
    '(?s)// SBMS-owned EDID\..*?\{\s*\{(?<bytes>.*?)\}\s*,\s*s_SampleDefaultModes')
Assert-Contract $edidMatch.Success 'SBMS EDID block was not found.'
$edid = @(
    [regex]::Matches($edidMatch.Groups['bytes'].Value, '0x(?<hex>[0-9A-Fa-f]{2})') |
        ForEach-Object { [Convert]::ToByte($_.Groups['hex'].Value, 16) }
)
Assert-Contract ($edid.Count -eq 128) "EDID length is $($edid.Count), expected 128."
Assert-Contract ((($edid | Measure-Object -Sum).Sum % 256) -eq 0) 'EDID checksum is invalid.'
Assert-Contract (
    $edid[8] -eq 0x4C -and $edid[9] -eq 0x4D -and
    $edid[10] -eq 0x01 -and $edid[11] -eq 0x00
) 'EDID does not use the SBMS manufacturer/product identity.'

$dtdWidth = [int]$edid[56] + (([int]$edid[58] -band 0xF0) -shl 4)
$dtdHeight = [int]$edid[59] + (([int]$edid[61] -band 0xF0) -shl 4)
$dtdHorizontalBlank = [int]$edid[57] + (([int]$edid[58] -band 0x0F) -shl 8)
$dtdVerticalBlank = [int]$edid[60] + (([int]$edid[61] -band 0x0F) -shl 8)
$dtdPixelClock = (([int]$edid[55] -shl 8) + [int]$edid[54]) * 10000
$dtdRefresh = [Math]::Round(
    $dtdPixelClock / (($dtdWidth + $dtdHorizontalBlank) * ($dtdHeight + $dtdVerticalBlank)),
    2)
Assert-Contract (
    $dtdWidth -eq 1920 -and $dtdHeight -eq 1080 -and
    [Math]::Abs($dtdRefresh - 60.0) -lt 0.01
) "EDID preferred timing is ${dtdWidth}x${dtdHeight}@$dtdRefresh, expected 1920x1080@60."

$monitorName = [Text.Encoding]::ASCII.GetString([byte[]]$edid[95..107]).Trim("`0", "`n", ' ')
Assert-Contract ($monitorName -eq 'SBMS Display') "EDID monitor name is '$monitorName'."

$preferredMatch = [regex]::Match(
    $driver,
    'static constexpr DWORD SBMS_PREFERRED_MODE_INDEX\s*=\s*(?<index>\d+)\s*;')
Assert-Contract $preferredMatch.Success 'preferred mode constant was not found.'
$preferredIndex = [int]$preferredMatch.Groups['index'].Value

$modesMatch = [regex]::Match(
    $driver,
    '(?s)#define SBMS_SUPPORTED_MODES\s*\\(?<modes>.*?)// Default modes reported')
Assert-Contract $modesMatch.Success 'supported mode table was not found.'
$modes = @(
    [regex]::Matches(
        $modesMatch.Groups['modes'].Value,
        '\{\s*(?<width>\d+)\s*,\s*(?<height>\d+)\s*,\s*(?<refresh>\d+)\s*\}') |
        ForEach-Object {
            [pscustomobject]@{
                Width = [int]$_.Groups['width'].Value
                Height = [int]$_.Groups['height'].Value
                Refresh = [int]$_.Groups['refresh'].Value
            }
        }
)
Assert-Contract ($preferredIndex -ge 0 -and $preferredIndex -lt $modes.Count) 'preferred mode index is out of range.'
$preferred = $modes[$preferredIndex]
Assert-Contract (
    $preferred.Width -eq 1920 -and
    $preferred.Height -eq 1080 -and
    $preferred.Refresh -eq 60
) "preferred mode is $($preferred.Width)x$($preferred.Height)@$($preferred.Refresh), expected 1920x1080@60."

foreach ($requiredIdentityCode in @(
        'DEVPKEY_Device_InstanceId',
        'HashSbmsMonitorIdentity',
        'm_MonitorContainerId',
        'm_MonitorEdid[12 + Index]',
        '((FirstHash >> 48) & 0x0fff) | 0x8000',
        '(m_MonitorContainerId.Data4[0] & 0x3f) | 0x80',
        'm_MonitorEdid[127] = static_cast<BYTE>(0 - Checksum)',
        'pEndPointFriendlyName = L"SBMS Virtual Display"',
        'pEndPointManufacturerName = L"SBMS"',
        'pEndPointModelName = L"SBMS Indirect Display"')) {
    Assert-Contract (
        ($driver + [Environment]::NewLine + $header) -match [regex]::Escape($requiredIdentityCode)
    ) "stable per-device identity contract is missing: $requiredIdentityCode"
}

$wrongTypeCleanup = [regex]::Match(
    $driver,
    '(?s)if\s*\(\s*PropertyType\s*!=\s*DEVPROP_TYPE_STRING\s*\)\s*\{\s*WdfObjectDelete\(InstanceIdMemory\);\s*return false;\s*\}')
Assert-Contract $wrongTypeCleanup.Success 'non-string instance-ID property leaks its WDF memory.'

function Get-TestIdentityHash {
    param([string]$InstanceId, [System.Numerics.BigInteger]$Seed, [uint32]$ConnectorIndex)
    $modulus = [System.Numerics.BigInteger]::Pow(2, 64)
    $prime = [System.Numerics.BigInteger]1099511628211
    $hash = $Seed
    $identityBytes = [Text.Encoding]::Unicode.GetBytes($InstanceId.ToUpperInvariant())
    $connectorBytes = [BitConverter]::GetBytes($ConnectorIndex)
    foreach ($value in @($identityBytes) + @($connectorBytes)) {
        $hash = (($hash -bxor [System.Numerics.BigInteger]$value) * $prime) % $modulus
    }
    $hash
}

function Get-TestMonitorIdentity {
    param([string]$InstanceId, [uint32]$ConnectorIndex)
    $first = Get-TestIdentityHash $InstanceId ([System.Numerics.BigInteger]14695981039346656037) $ConnectorIndex
    $second = Get-TestIdentityHash $InstanceId ([System.Numerics.BigInteger]1099511628211) $ConnectorIndex
    $serialMask = [System.Numerics.BigInteger]::Pow(2, 32) - 1
    $serialValue = ($first -bxor ($first -shr 32) -bxor $second) -band $serialMask
    if ($serialValue -eq 0) { $serialValue = 1 }
    $data3 = [uint16]((($first -shr 48) -band 0x0FFF) -bor 0x8000)
    $data4First = [byte]((($second -band 0xFF) -band 0x3F) -bor 0x80)
    [pscustomobject]@{
        Key = "$($first.ToString('X16')):$($second.ToString('X16'))"
        Serial = [uint32]$serialValue
        Version = ($data3 -shr 12)
        Variant = ($data4First -shr 6)
    }
}

$firstIdentity = Get-TestMonitorIdentity 'SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER' 0
$secondIdentity = Get-TestMonitorIdentity 'SWD\IDDSAMPLEDRIVER\IDDSAMPLEDRIVER2' 0
Assert-Contract ($firstIdentity.Key -ne $secondIdentity.Key) 'two SWD instances derive the same container identity.'
Assert-Contract ($firstIdentity.Serial -ne $secondIdentity.Serial) 'two SWD instances derive the same EDID serial.'
Assert-Contract (
    $firstIdentity.Version -eq 8 -and $secondIdentity.Version -eq 8
) 'container identity is not UUID version 8.'
Assert-Contract (
    $firstIdentity.Variant -eq 2 -and $secondIdentity.Variant -eq 2
) 'container identity does not use the RFC variant.'

foreach ($identity in @($firstIdentity, $secondIdentity)) {
    $dynamicEdid = [byte[]]$edid.Clone()
    $serialBytes = [BitConverter]::GetBytes([uint32]$identity.Serial)
    [Array]::Copy($serialBytes, 0, $dynamicEdid, 12, $serialBytes.Count)
    $serialText = 'SBMS' + $identity.Serial.ToString('X8')
    $serialTextBytes = [Text.Encoding]::ASCII.GetBytes($serialText)
    [Array]::Copy($serialTextBytes, 0, $dynamicEdid, 77, $serialTextBytes.Count)
    $dynamicEdid[89] = 0x0A
    $dynamicEdid[127] = 0
    $dynamicEdid[127] = [byte]((256 - (($dynamicEdid | Measure-Object -Sum).Sum % 256)) % 256)
    $readbackSerial = [Text.Encoding]::ASCII.GetString($dynamicEdid[77..88])
    Assert-Contract ($readbackSerial -eq $serialText) 'dynamic EDID ASCII serial descriptor is invalid.'
    Assert-Contract ((($dynamicEdid | Measure-Object -Sum).Sum % 256) -eq 0) 'dynamic EDID checksum is invalid.'
}

[pscustomobject]@{
    status = 'PASS'
    edidBytes = $edid.Count
    edidChecksum = 0
    monitorName = $monitorName
    preferredMode = "$($preferred.Width)x$($preferred.Height)@$($preferred.Refresh)"
    stableIdentity = 'device-instance-id + connector-index'
    distinctIdentityFixture = ($firstIdentity.Key -ne $secondIdentity.Key)
    containerUuidVersion = $firstIdentity.Version
    containerUuidVariant = $firstIdentity.Variant
}
