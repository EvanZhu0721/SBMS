[CmdletBinding()]
param(
    [string]$SigningCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = $PSScriptRoot
$driver = Join-Path $repository 'driver'
$output = Join-Path $repository 'target\driver'
$kitsRoot = (Get-ItemProperty -LiteralPath `
    'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10

$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $kitsRoot 'Include') -Directory |
    Where-Object {
        [version]::TryParse($_.Name, [ref]([version]$null)) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
        (Test-Path -LiteralPath (Join-Path $kitsRoot "Lib\$($_.Name)"))
    } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty Name

if (-not $sdkVersion) {
    throw 'No complete Windows SDK was found.'
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} `
    'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) {
    throw 'Visual Studio C++ Build Tools were not found.'
}

$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$environmentLines = & $env:ComSpec /d /s /c `
    "call `"$vcvars`" >nul && set"
if ($LASTEXITCODE -ne 0) {
    throw 'vcvars64.bat failed.'
}
foreach ($line in $environmentLines) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) {
        [Environment]::SetEnvironmentVariable(
            $line.Substring(0, $separator),
            $line.Substring($separator + 1),
            'Process')
    }
}

# Use the same UMDF target for headers, stub library and INF. Merely lowering
# the INF does not change the version requested by the linked driver.
# IddCx 1.4 needs WDF_STRUCT_INFO from UMDF 2.25 or later.
# UMDF 2.25 ships with Windows 10 1803; our minimum 19041 already has 2.31.
$umdfVersion = '2.25'
$umdfMinor = 25
$sourceInf = Join-Path $driver 'SBMSIndirectDisplay.inf'
$infVersion = [regex]::Match(
    (Get-Content -LiteralPath $sourceInf -Raw -Encoding UTF8),
    '(?m)^UmdfLibraryVersion\s*=\s*(\d+\.\d+)\s*$').Groups[1].Value
if ($infVersion -ne $umdfVersion) {
    throw "INF UMDF version $infVersion does not match build target $umdfVersion."
}
$wdfInclude = Join-Path $kitsRoot "Include\wdf\umdf\$umdfVersion"
$iddInclude = Join-Path $kitsRoot "Include\$sdkVersion\um\iddcx\1.4"
$wdfLibrary = Join-Path $kitsRoot "Lib\wdf\umdf\x64\$umdfVersion\WdfDriverStubUm.lib"
$iddLibrary = Join-Path $kitsRoot `
    "Lib\$sdkVersion\um\x64\iddcx\1.4\IddCxStub.lib"
$apiValidator = Join-Path $kitsRoot "bin\$sdkVersion\x64\ApiValidator.exe"
$apiContract = Join-Path $kitsRoot `
    "build\$sdkVersion\universalDDIs\x64\UniversalDDIs.xml"
$infVerifier = Join-Path $kitsRoot "Tools\$sdkVersion\x64\InfVerif.exe"
$inf2Cat = Join-Path $kitsRoot "bin\$sdkVersion\x86\Inf2Cat.exe"
$signTool = Join-Path $kitsRoot "bin\$sdkVersion\x64\signtool.exe"

$required = @(
    $vcvars, $wdfInclude, $iddInclude, $wdfLibrary, $iddLibrary,
    $apiValidator, $apiContract, $infVerifier, $inf2Cat
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required build input is missing: $path"
    }
}
if ($SigningCertificateThumbprint -and
    -not (Test-Path -LiteralPath $signTool)) {
    throw "Required signing tool is missing: $signTool"
}

if (Test-Path -LiteralPath $output) {
    $expectedOutput = [IO.Path]::GetFullPath((Join-Path $repository 'target\driver'))
    if ((Resolve-Path -LiteralPath $output).Path -ne $expectedOutput) {
        throw "Unexpected driver output directory: $output"
    }
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output | Out-Null

$object = Join-Path $output 'SBMSIndirectDisplay.obj'
$binary = Join-Path $output 'SBMSIndirectDisplay.dll'
$inf = Join-Path $output 'SBMSIndirectDisplay.inf'

$compileArguments = @(
    '/nologo', '/c', '/std:c++17', '/EHs-c-', '/guard:cf',
    '/W4', '/WX',
    '/wd4005', '/wd4324',
    '/D_WIN64', '/D_AMD64_', '/DAMD64', '/DUMDF_DRIVER',
    '/DUMDF_VERSION_MAJOR=2', "/DUMDF_VERSION_MINOR=$umdfMinor",
    "/DUMDF_MINIMUM_VERSION_REQUIRED=$umdfMinor", '/DUMDF_USING_NTSTATUS',
    '/D_UNICODE', '/DUNICODE',
    '/DIDDCX_VERSION_MAJOR=1', '/DIDDCX_VERSION_MINOR=4',
    '/DIDDCX_MINIMUM_VERSION_REQUIRED=4',
    "/I$wdfInclude", "/I$iddInclude",
    "/Fo$object", (Join-Path $driver 'Driver.cpp')
)
& cl.exe @compileArguments
if ($LASTEXITCODE -ne 0) {
    throw "cl.exe failed with exit code $LASTEXITCODE"
}

$linkArguments = @(
    '/nologo', '/DLL', '/guard:cf', '/SUBSYSTEM:WINDOWS,10.0',
    '/OSVERSION:10.0', '/VERSION:10.0',
    "/OUT:$binary", $object, $wdfLibrary, $iddLibrary,
    'onecoreuap.lib', 'd3d11.lib', 'dxgi.lib', 'ntdll.lib'
)
& link.exe @linkArguments
if ($LASTEXITCODE -ne 0) {
    throw "link.exe failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $driver 'SBMSIndirectDisplay.inf') `
    -Destination $inf

& $apiValidator "-BinaryPath:$binary" `
    "-SupportedApiXmlFiles:$apiContract" '-StrictCompliance:true'
if ($LASTEXITCODE -ne 0) {
    throw "ApiValidator failed with exit code $LASTEXITCODE"
}

foreach ($mode in '/w', '/u', '/h') {
    & $infVerifier $mode $inf
    if ($LASTEXITCODE -ne 0) {
        throw "InfVerif $mode failed with exit code $LASTEXITCODE"
    }
}

if ($SigningCertificateThumbprint) {
    & $signTool sign /sha1 $SigningCertificateThumbprint /fd SHA256 $binary
    if ($LASTEXITCODE -ne 0) {
        throw "Signing the driver DLL failed with exit code $LASTEXITCODE"
    }
}

# The catalog must describe the final signed DLL, not the unsigned input.
& $inf2Cat "/driver:$output" /os:10_X64
if ($LASTEXITCODE -ne 0) {
    throw "Inf2Cat failed with exit code $LASTEXITCODE"
}

if ($SigningCertificateThumbprint) {
    $catalog = Join-Path $output 'SBMSIndirectDisplay.cat'
    & $signTool sign /sha1 $SigningCertificateThumbprint /fd SHA256 $catalog
    if ($LASTEXITCODE -ne 0) {
        throw "Signing the catalog failed with exit code $LASTEXITCODE"
    }

    foreach ($file in @($binary, $catalog)) {
        & $signTool verify /pa $file
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed for $file"
        }
    }
    foreach ($member in @($binary, $inf)) {
        & $signTool verify /pa /c $catalog $member
        if ($LASTEXITCODE -ne 0) {
            throw "Catalog membership verification failed for $member"
        }
    }
}

Write-Host "driver_package=$output"
