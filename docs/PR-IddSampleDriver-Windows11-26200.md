# [PR] IddSampleDriver installation fixes for Windows 11 Build 26200

## Problem

`IddSampleDriver` fails to install on Windows 11 Insider Preview Build 26200 (and likely newer builds) with the following error:

```
Microsoft PnP 工具
正在添加驱动程序包:  IddSampleDriver.inf
无法添加驱动程序包: 第三方 INF 不包含数字签名信息。
```

This occurs even with `testsigning on` and `nointegritychecks on`, because pnputil on newer Windows builds requires a signed `.cat` (catalog) file.

Additionally, the INF lacks a `CatalogFile` directive, so pnputil cannot associate the INF with any catalog.

## Changes

### 1. INF: Add `CatalogFile` reference

`IddSampleDriver.inf` needs a `CatalogFile=IddSampleDriver.cat` entry in its `[Version]` section to link INF signing to a catalog file.

```diff
 [Version]
 PnpLockDown=1
 Signature="$Windows NT$"
 ClassGUID = {4D36E968-E325-11CE-BFC1-08002BE10318}
 Class = Display
 ClassVer = 2.0
 Provider=%ManufacturerName%
 DriverVer=06/30/2026,1.0.0.0
+CatalogFile=IddSampleDriver.cat
```

### 2. Add CDF template for makecat

Provide a `.cdf` (Catalog Definition File) to simplify `makecat` usage:

**IddSampleDriver.cdf:**
```
[CatalogHeader]
Name=IddSampleDriver.cat
ResultDir=.

[CatalogFiles]
<HASH>IddSampleDriver.inf
<HASH>IddSampleDriver.dll=IddSampleDriver.dll
```

Usage:
```cmd
cd <driver_dir>
makecat -v IddSampleDriver.cdf
signtool sign /fd sha256 /f yourcert.pfx /p password IddSampleDriver.cat
pnputil /add-driver IddSampleDriver.inf /install
```

### 3. Add installation script (`tools/install-driver.ps1`)

A one-click PowerShell script that handles:
1. Test signing mode check
2. File deployment
3. Certificate creation (with exportable private key)
4. Trust store registration
5. Catalog generation via makecat
6. Catalog signing via signtool
7. pnputil driver store installation

### 4. Documentation: `docs/install-windows11.md`

Installation guide covering:
- Test signing setup
- Certificate management
- Catalog signing
- Root device node creation (SYSTEM-level registry write)
- Troubleshooting checklist

---

## Installation Flow (new)

```
1. Enable testsigning → bcdedit /set testsigning on + reboot
2. Run tools/install-driver.ps1 (Admin PowerShell)
   ├── Copy files to system
   ├── Create & trust self-signed cert
   ├── Add CatalogFile to INF
   ├── makecat → IddSampleDriver.cat
   ├── signtool → sign .cat
   ├── pnputil → add to driver store
   └── Create Enum\Root device node (via SYSTEM context)
3. Reboot → PnP picks up device
```

## Key Issues Encountered

| Issue | Root Cause | Fix |
|---|---|---|
| INF not signed | pnputil + Windows 26200 requires signed catalog | Add `CatalogFile` + sign `.cat` |
| signtool can't sign INF | INF is not a PE file | Sign `.cat` instead |
| Cert private key not exportable | `New-SelfSignedCertificate` default | Add `-KeyExportPolicy Exportable` |
| Can't write to `Enum\Root` | TrustedInstaller protection | Use `Invoke-CimMethod` as SYSTEM |
| PnP not detecting device | Runtime reenumeration insufficient | Reboot to enumerate root devices |
| Unicode quote chars in scripts | Copy-paste encoding issues | Normalize to ASCII quotes |

## Files

| File | Purpose |
|---|---|
| `IddSampleDriver.inf` | Modified: added `CatalogFile=IddSampleDriver.cat` |
| `IddSampleDriver.cdf` | **New**: makecat catalog definition |
| `tools/install-driver.ps1` | **New**: one-click installer |
| `docs/install-windows11.md` | **New**: installation guide |

## Testing

- Tested manually on Windows 11 Build 26200 (arm64)
- Driver package added to store as `oem139.inf`
- Root device node created at `Enum\Root\IddSampleDriver\0000`
- Device appears after reboot as "SBMS Virtual Display" under Display adapters

## Notes

- The current `Root\IddSampleDriver` hardware ID is used for Visual Studio remote debugging; the `IddSampleDriver` ID (without root) requires the `IddSampleApp.exe` to create the software device
- For production deployment, consider using a proper EV certificate or the Windows Hardware Dev Center dashboard for WHQL signing
