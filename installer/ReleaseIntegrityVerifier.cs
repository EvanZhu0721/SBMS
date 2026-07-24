using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SBMSSetup
{
    internal static class ReleaseIntegrityVerifier
    {
        private const string VerificationScript = @"
$ErrorActionPreference = 'Stop'
$self = [IO.Path]::GetFullPath($env:SBMS_VERIFY_SELF)
$payload = [IO.Path]::GetFullPath($env:SBMS_VERIFY_PAYLOAD)
$catalog = [IO.Path]::GetFullPath($env:SBMS_VERIFY_CATALOG)
$expected = ($env:SBMS_VERIFY_THUMBPRINT -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($expected.Length -ne 40) { throw 'Embedded publisher thumbprint is invalid.' }
$selfSignature = Get-AuthenticodeSignature -LiteralPath $self
if ([string]$selfSignature.Status -cne 'Valid' -or -not $selfSignature.SignerCertificate) {
    throw ('Installer signature is invalid: ' + [string]$selfSignature.Status)
}
$selfThumbprint = ([string]$selfSignature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($selfThumbprint -cne $expected) { throw 'Installer publisher does not match its embedded identity.' }
if (-not $selfSignature.TimeStamperCertificate) { throw 'Installer RFC3161 timestamp is missing.' }
if (-not (Test-Path -LiteralPath $payload -PathType Container)) { throw 'Release payload directory is missing.' }
if (-not (Test-Path -LiteralPath $catalog -PathType Leaf)) { throw 'Release catalog is missing.' }
$signature = Get-AuthenticodeSignature -LiteralPath $catalog
if ([string]$signature.Status -cne 'Valid' -or -not $signature.SignerCertificate) {
    throw ('Release catalog signature is invalid: ' + [string]$signature.Status)
}
$actual = ([string]$signature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($actual -cne $expected) { throw 'Release catalog publisher does not match the installer identity.' }
if (-not $signature.TimeStamperCertificate) { throw 'Release catalog RFC3161 timestamp is missing.' }
$catalogResult = Test-FileCatalog -Path $payload -CatalogFilePath $catalog -Detailed
if ([string]$catalogResult.Status -cne 'Valid') {
    throw ('Release payload catalog mismatch: ' + [string]$catalogResult.Status)
}
$manifestPath = Join-Path $payload 'SBMS.release.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Release manifest is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 3) { throw 'Production release manifest schema must be 3.' }
if ([string]$manifest.profile -cne 'Production') { throw 'Release manifest is not a Production profile.' }
if ([string]$manifest.source.commit -notmatch '^[0-9a-f]{40,64}$' -or [bool]$manifest.source.dirty) {
    throw 'Release source provenance is invalid.'
}
if ([string]$manifest.driverCertification.sourceCommit -cne [string]$manifest.source.commit -or
    [string]$manifest.driverCertification.candidateManifestSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'WHQL driver provenance is not pinned to the release source.'
}
if ([string]$manifest.signing.publisherThumbprint -cne $expected) {
    throw 'Release manifest publisher does not match the installer identity.'
}
$selfItem = Get-Item -LiteralPath $self
$selfHash = (Get-FileHash -LiteralPath $self -Algorithm SHA256).Hash.ToLowerInvariant()
if ($selfHash -cne [string]$manifest.installer.sha256 -or
    [long]$selfItem.Length -ne [long]$manifest.installer.bytes -or
    [string]$manifest.installer.productVersion -cne [string]$manifest.product.version) {
    throw 'Running installer does not match the signed release manifest.'
}
$payloadPrefix = [IO.Path]::GetFullPath($payload).TrimEnd('\') + '\'
$artifactMap = @{}
foreach ($artifact in @($manifest.artifacts)) {
    $relative = ([string]$artifact.path).Replace('/', '\')
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative) -or
        $relative.Contains(':') -or
        @($relative.Split('\') | Where-Object { $_ -eq '..' }).Count -gt 0) {
        throw ('Unsafe release artifact path: ' + $relative)
    }
    $full = [IO.Path]::GetFullPath((Join-Path $payload $relative))
    if (-not $full.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Release artifact escapes payload: ' + $relative)
    }
    $key = $relative.ToLowerInvariant()
    if ($artifactMap.ContainsKey($key)) { throw ('Duplicate release artifact path: ' + $relative) }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw ('Release artifact is missing: ' + $relative) }
    $item = Get-Item -LiteralPath $full
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw ('Release artifact cannot be a reparse point: ' + $relative)
    }
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne [string]$artifact.sha256 -or [long]$item.Length -ne [long]$artifact.bytes) {
        throw ('Release artifact metadata mismatch: ' + $relative)
    }
    $artifactMap[$key] = $full
}
foreach ($file in @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force)) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw ('Release payload contains a reparse point: ' + $file.FullName)
    }
    $relative = $file.FullName.Substring($payloadPrefix.Length).ToLowerInvariant()
    if ($relative -ne 'sbms.release.json' -and -not $artifactMap.ContainsKey($relative)) {
        throw ('Release payload contains an unlisted file: ' + $relative)
    }
}
$version = (Get-Content -LiteralPath (Join-Path $payload 'VERSION') -Raw -Encoding UTF8).Trim()
if ($version -cne [string]$manifest.product.version) { throw 'VERSION does not match the release manifest.' }
foreach ($name in @('SBMS.exe', 'SBMSNative.exe', 'SBMSDeviceHost.exe')) {
    $path = Join-Path $payload $name
    $componentSignature = Get-AuthenticodeSignature -LiteralPath $path
    if ([string]$componentSignature.Status -cne 'Valid' -or -not $componentSignature.SignerCertificate) {
        throw ('Component signature is invalid: ' + $name)
    }
    $componentThumbprint = ([string]$componentSignature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($componentThumbprint -cne $expected -or -not $componentSignature.TimeStamperCertificate) {
        throw ('Component publisher or timestamp is invalid: ' + $name)
    }
    if ([string][Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -cne [string]$manifest.product.version) {
        throw ('Component ProductVersion does not match the release: ' + $name)
    }
}
if ([string]$manifest.driverCertification.method -cne 'WHQL') { throw 'Driver certification method is not WHQL.' }
$driverRoot = Join-Path $payload 'driver\IddSampleDriver'
$driverInf = @(Get-ChildItem -LiteralPath $driverRoot -Filter '*.inf' -File)
$driverDll = @(Get-ChildItem -LiteralPath $driverRoot -Filter '*.dll' -File)
$driverCat = @(Get-ChildItem -LiteralPath $driverRoot -Filter '*.cat' -File)
if ($driverInf.Count -ne 1 -or $driverDll.Count -ne 1 -or $driverCat.Count -ne 1) {
    throw 'Production driver payload must contain exactly one INF, DLL and CAT.'
}
$driverSignature = Get-AuthenticodeSignature -LiteralPath $driverCat[0].FullName
$allowedWhqlSubjects = @($env:SBMS_VERIFY_WHQL_SUBJECTS -split '\r?\n')
if ([string]$driverSignature.Status -cne 'Valid' -or
    -not $driverSignature.SignerCertificate -or
    $allowedWhqlSubjects -notcontains [string]$driverSignature.SignerCertificate.Subject -or
    -not $driverSignature.TimeStamperCertificate) {
    throw 'Driver catalog does not satisfy the embedded Microsoft WHQL policy.'
}
$driverDllSignature = Get-AuthenticodeSignature -LiteralPath $driverDll[0].FullName
$driverDllThumbprint = (
    [string]$driverDllSignature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', ''
).ToUpperInvariant()
if ([string]$driverDllSignature.Status -cne 'Valid' -or
    $driverDllThumbprint -cne $expected -or
    -not $driverDllSignature.TimeStamperCertificate) {
    throw 'Driver DLL publisher signature or timestamp is invalid.'
}
$driverVerLine = Select-String -LiteralPath $driverInf[0].FullName -Pattern '^\s*DriverVer\s*=\s*(.+?)\s*$' |
    Select-Object -First 1
if (-not $driverVerLine -or
    [string]$driverVerLine.Matches[0].Groups[1].Value.Trim() -cne [string]$manifest.product.driverVer) {
    throw 'DriverVer does not match the release manifest.'
}
";

        internal static void VerifyOrThrow(
            string sourceRoot,
            string installerPath,
            string expectedPublisherThumbprint,
            string allowedWhqlSubjects)
        {
            if (String.IsNullOrWhiteSpace(sourceRoot))
            {
                throw new ArgumentException("Release source root is missing.", "sourceRoot");
            }
            VerifyPayloadOrThrow(
                Path.Combine(Path.GetFullPath(sourceRoot), "payload"),
                Path.Combine(Path.GetFullPath(sourceRoot), "SBMS.release.cat"),
                installerPath,
                expectedPublisherThumbprint,
                allowedWhqlSubjects);
        }

        internal static void VerifyPayloadOrThrow(
            string payloadDirectory,
            string catalogPath,
            string installerPath,
            string expectedPublisherThumbprint,
            string allowedWhqlSubjects)
        {
            if (String.IsNullOrWhiteSpace(payloadDirectory))
            {
                throw new ArgumentException("Release payload path is missing.", "payloadDirectory");
            }
            if (String.IsNullOrWhiteSpace(catalogPath))
            {
                throw new ArgumentException("Release catalog path is missing.", "catalogPath");
            }
            if (String.IsNullOrWhiteSpace(expectedPublisherThumbprint))
            {
                throw new InvalidOperationException("Production publisher identity was not embedded at build time.");
            }
            if (String.IsNullOrWhiteSpace(installerPath))
            {
                throw new ArgumentException("Installer path is missing.", "installerPath");
            }
            if (String.IsNullOrWhiteSpace(allowedWhqlSubjects))
            {
                throw new InvalidOperationException("Production WHQL publisher policy was not embedded at build time.");
            }

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(VerificationScript));
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["SBMS_VERIFY_SELF"] = Path.GetFullPath(installerPath);
            startInfo.EnvironmentVariables["SBMS_VERIFY_PAYLOAD"] = Path.GetFullPath(payloadDirectory);
            startInfo.EnvironmentVariables["SBMS_VERIFY_CATALOG"] = Path.GetFullPath(catalogPath);
            startInfo.EnvironmentVariables["SBMS_VERIFY_THUMBPRINT"] = expectedPublisherThumbprint;
            startInfo.EnvironmentVariables["SBMS_VERIFY_WHQL_SUBJECTS"] = allowedWhqlSubjects;

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Unable to start the release verifier.");
                }
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(120000))
                {
                    process.Kill();
                    throw new TimeoutException("Release verification timed out.");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        "Release verification failed before installation. " +
                        (stderr + Environment.NewLine + stdout).Trim());
                }
            }

            string driverRoot = Path.Combine(
                Path.GetFullPath(payloadDirectory),
                "driver",
                "IddSampleDriver");
            string[] catalogs = Directory.GetFiles(driverRoot, "*.cat");
            string[] infs = Directory.GetFiles(driverRoot, "*.inf");
            string[] dlls = Directory.GetFiles(driverRoot, "*.dll");
            if (catalogs.Length != 1 || infs.Length != 1 || dlls.Length != 1)
            {
                throw new InvalidDataException(
                    "Production driver payload must contain exactly one INF, DLL and CAT.");
            }
            DriverCatalogVerifier.VerifyPackageOrThrow(
                catalogs[0],
                infs[0],
                dlls[0]);
        }
    }
}
