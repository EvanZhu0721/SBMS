# IddSampleDriver Installer for Windows 11 Build 26200+
# Run as Administrator in PowerShell

$driverDir = Split-Path -Parent $PSScriptRoot
$infPath = Join-Path $driverDir "Windows-driver-samples\video\IndirectDisplay\IddSampleDriver\IddSampleDriver.inf"
$dllPath = Join-Path $driverDir "Windows-driver-samples\video\IndirectDisplay\x64\Release\IddSampleDriver.dll"
$cdfPath = Join-Path $driverDir "Windows-driver-samples\video\IndirectDisplay\IddSampleDriver\IddSampleDriver.cdf"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$makecat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makecat.exe"

Write-Host "=== [1/7] Check test signing ==="
$ts = bcdedit /enum | findstr testsigning
if ($ts -notmatch 'Yes') {
    Write-Host "Test signing not enabled. Run: bcdedit /set testsigning on"
    Write-Host "Then reboot and run this script again."
    exit 1
}

Write-Host "=== [2/7] Deploy driver files ==="
Copy-Item $infPath "C:\IddSampleDriver.inf" -Force
Copy-Item $dllPath "$env:SystemRoot\System32\drivers\UMDF\IddSampleDriver.dll" -Force
Write-Host "Files deployed."

Write-Host "=== [3/7] Create code signing certificate ==="
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=IddSampleDriver Test Cert" `
    -KeyUsage DigitalSignature -TextExtension "2.5.29.37={text}1.3.6.1.5.5.7.3.3" `
    -KeyExportPolicy Exportable -CertStoreLocation Cert:\LocalMachine\My `
    -FriendlyName "IddSampleDriver Test Cert" -NotAfter (Get-Date).AddYears(5)
$thumb = $cert.Thumbprint
Write-Host "Certificate created. Thumbprint: $thumb"

Write-Host "=== [4/7] Add cert to trusted stores ==="
$pfx = "$env:TEMP\idd-cert.pfx"
$cer = "$env:TEMP\idd-cert.cer"
$pass = "idd123"
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pass -AsPlainText -Force) -Force
Export-Certificate -Cert $cert -FilePath $cer -Force
certutil -addstore Root $cer | Out-Null
certutil -addstore TrustedPublisher $cer | Out-Null
Write-Host "Cert trusted."

Write-Host "=== [5/7] Create and sign catalog ==="
Copy-Item $dllPath "$env:TEMP\IddSampleDriver.dll" -Force
Copy-Item $infPath "$env:TEMP\IddSampleDriver.inf" -Force

Push-Location $env:TEMP
& $makecat -v $cdfPath
if (Test-Path "$env:TEMP\IddSampleDriver.cat") {
    & $signtool sign -v -fd sha256 -f $pfx -p $pass "$env:TEMP\IddSampleDriver.cat"
    Copy-Item "$env:TEMP\IddSampleDriver.cat" "C:\IddSampleDriver.cat" -Force
}
Pop-Location

Write-Host "=== [6/7] Install driver package ==="
pnputil /add-driver "C:\IddSampleDriver.inf" /install

Write-Host "=== [7/7] Create device node ==="
Write-Host "Creating root device node (requires SYSTEM context)..."
$sysScript = @"
reg add "HKLM\SYSTEM\CurrentControlSet\Enum\Root\IddSampleDriver\0000" /v Class /t REG_SZ /d Display /f
reg add "HKLM\SYSTEM\CurrentControlSet\Enum\Root\IddSampleDriver\0000" /v ClassGUID /t REG_SZ /d "{4D36E968-E325-11CE-BFC1-08002BE10318}" /f
reg add "HKLM\SYSTEM\CurrentControlSet\Enum\Root\IddSampleDriver\0000" /v DeviceDesc /t REG_SZ /d "IddSampleDriver Device" /f
reg add "HKLM\SYSTEM\CurrentControlSet\Enum\Root\IddSampleDriver\0000" /v HardwareID /t REG_MULTI_SZ /d Root\IddSampleDriver /f
reg add "HKLM\SYSTEM\CurrentControlSet\Enum\Root\IddSampleDriver\0000" /v ConfigFlags /t REG_DWORD /d 0 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Enum\Root\IddSampleDriver\0000\LogConf" /f
"@
$sysScript | Out-File "$env:TEMP\create-node.cmd" -Encoding ASCII -Force
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine = "cmd /c $env:TEMP\create-node.cmd"
} | Out-Null

Write-Host ""
Write-Host "=== Install complete ==="
Write-Host "REBOOT required for PnP to enumerate the device."
Write-Host ""
Write-Host "After reboot, verify with:"
Write-Host "  Get-PnpDevice -Class Display | Format-List Status, FriendlyName, InstanceId, Service"
