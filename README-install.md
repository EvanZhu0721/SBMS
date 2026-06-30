# SBMS Virtual Display 驱动安装指南

## 环境

- OS: Windows 11 Insider Preview (Build 26200)
- 驱动: Indirect Display Driver Sample (IddSampleDriver) from Windows-driver-samples

## 安装步骤

### 前置条件

1. 以管理员身份打开 PowerShell
2. 开启测试签名模式（如需）：
   ```
   bcdedit /set testsigning on
   bcdedit /set nointegritychecks on
   ```
   然后重启系统

### 安装方法

**方法 A：使用一键安装脚本**

```powershell
powershell -ExecutionPolicy Bypass -File "%UserProfile%\.openclaw\workspace\RUN_ME_ADMIN.ps1"
```

脚本会自动：
1. 确认测试签名模式已开启
2. 复制驱动文件到系统目录
3. 创建代码签名证书并安装到受信任根存储
4. 给 INF 添加 CatalogFile 引用
5. 使用 makecat 创建目录文件 (.cat)
6. 使用 signtool 签名
7. 使用 pnputil 添加到驱动存储

**方法 B：手动安装**

```cmd
copy IddSampleDriver.inf C:\SBMS.inf
copy x64\Release\IddSampleDriver.dll C:\Windows\System32\drivers\UMDF\
pnputil /add-driver C:\SBMS.inf /install
```

然后创建根设备节点并重启。

## 排错记录

### 问题 1：pnputil 拒绝第三方无签名 INF
- **症状**: `第三方 INF 不包含数字签名信息`
- **原因**: Windows 11 Build 26200 强制 INF 数字签名，禁用 testsigning/nointegritychecks 也不足以跳过
- **解决**: 需要创建签名证书 + 使用 makecat 生成目录文件 + 用 signtool 签名 .cat 文件

### 问题 2：signtool 找不到证书 / 证书私钥不可导出
- **症状**: `No certificates were found that met all the given criteria`
- **原因**: `New-SelfSignedCertificate` 默认创建不可导出私钥的证书
- **解决**: 添加 `-KeyExportPolicy Exportable` 参数，然后导出为 .pfx 供 signtool 使用

### 问题 3：INF 文件不能直接用 signtool 签名
- **症状**: `This file format cannot be signed because it is not recognized`
- **原因**: INF 不是 PE 格式文件，Authenticode 签名只能用于可执行文件
- **解决**: 需要通过 `.cat` (catalog) 文件来间接签名。使用 makecat 创建目录文件，签名 .cat，并在 INF 的 `[Version]` 段添加 `CatalogFile=SBMS.cat`

### 问题 4：INF 缺少 CatalogFile 引用
- **症状**: pnputil 添加驱动到存储后仍报无签名
- **原因**: INF 需要显式引用签名过的目录文件
- **解决**: 在 `[Version]` 段添加 `CatalogFile=SBMS.cat`

### 问题 5：驱动包安装后设备不出现
- **症状**: pnputil 显示驱动已添加 (oem139.inf)，但设备列表无显示
- **原因**: PnP 设备节点需要通过注册表创建 (`Enum\Root\IddSampleDriver\0000`)，且需要 SYSTEM 权限写入
- **解决**: 
  1. 在 SYSTEM 上下文下创建注册表条目（通过 `Invoke-CimMethod Win32_Process.Create`）
  2. 重启系统让 PnP 枚举根设备

### 问题 6：Enum 注册表键写入权限拒绝
- **症状**: `reg add ... 拒绝访问`
- **原因**: `HKLM\SYSTEM\CurrentControlSet\Enum` 键受 TrustedInstaller 保护，即使管理员也无法直接写入
- **解决**: 使用 WMI/CIM 调用 `Win32_Process.Create` 以 SYSTEM 身份启动 cmd 来执行 reg 命令

### 问题 7：umdf: 驱动依赖 IndirectKmd
- **症状**: 虚拟显示器设备显示但无功能
- **原因**: IddSampleDriver 需要 IndirectKmd（间接显示内核驱动）作为上层过滤器
- **解决**: 在设备参数中添加 `UpperFilters = IndirectKmd`

## 相关文件

| 文件 | 用途 |
|---|---|
| `RUN_ME_ADMIN.ps1` | 一键安装脚本（管理员 PowerShell 执行） |
| `check-status.ps1` | 检测设备状态 |
| `pnp-force.ps1` | 使用 cfgmgr32 API 强制 PnP 重新枚举 |
| `final-system.ps1` | 以 SYSTEM 身份创建设备节点并触发 PnP |
| `system-run.cmd` | SYSTEM 上下文运行的注册表脚本 |
| `fix-quotes.ps1` | 修复脚本中 Unicode 引号编码问题 |
