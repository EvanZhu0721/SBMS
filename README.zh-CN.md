# SBMS

[English](README.md)

SBMS 正围绕一条可审查的路径重建：创建一个 Windows 间接显示器，将该虚拟桌面复制到一个由用户明确选择的物理显示器，并在会话停止时释放自身拥有的全部资源。

旧版 GUI、进程监督器、恢复代理、XML 配置、事务式安装框架和契约测试框架均被有意移除。它们最后的代码树保存在 Git 标签 `legacy-csharp-eb9d2a1` 中。

## 代码结构

```text
src/main.rs                 CLI 和进程生命周期
src/controller.rs           不阻塞 UI 的会话控制 worker
src/control.rs              托盘单实例与优雅退出事件
src/display.rs              稳定的显示器标识和活动拓扑
src/input.rs                鼠标捕获、转发与安全释放
src/mapping.rs              映射的启动/停止及所有权
src/window_migration.rs     可逆的顶层窗口迁移
src/frame_transport.rs      受保护的单会话模式入口
src/renderer.rs             Desktop Duplication 与目标窗口
src/gpu_renderer.rs         D3D11 缩放和 flip-model 呈现
src/ui.rs + ui/             托盘适配层和 Slint 快速控制面板
src/virtual_display.rs      SwDeviceCreate 和 HSWDEVICE 所有权
driver/Driver.cpp           IddCx 模式发布和交换链排空
driver/SBMSIndirectDisplay.inf
build-driver.ps1            构建、验证和可选的测试签名
installer/                  极薄的 Inno Setup 清单与维护脚本
build-installer.ps1         构建并签名预览安装包
```

Rust 进程负责产品策略。C++ 驱动只负责 Windows Driver Kit 天然以 C++ 暴露的 WDF/IddCx 边界。

项目刻意维持一组很小的不变量：

1. 目标显示器通过 DisplayConfig 的 `monitorDevicePath` 选择，绝不依赖显示器顺序或分辨率。
2. `sbms map` 请求创建 `SBMS\IndirectDisplay`，并等待它对应的活动显示源出现。
3. 在映射进入运行状态前，将目标物理显示器上符合条件的标准顶层窗口迁移到虚拟显示源。
4. IDD 持续获取并完成 IddCx 交换链缓冲，但不再把像素回读到 CPU。
5. Rust 精确定位虚拟 DXGI 输出并在 GPU 上复制；只有第一帧成功呈现到选定物理目标后，启动才算成功。
6. 停止时先释放输入捕获并关闭镜像，再回迁窗口并关闭唯一拥有的 `HSWDEVICE`；等待虚拟拓扑消失后，再按照仅含物理显示器的终态校准一次窗口位置。
7. 关闭该句柄会使当前设备节点变为不在场；Windows 仍可能保留它的历史设备记录。

## 1.1.3 能力边界

支持：

- Windows 10/11 x64；单进程、单虚拟显示源、单物理目标。
- 每次会话根据已保存的尺寸请求计算一个 BGRA 虚拟模式；未配置尺寸请求时回退到 3840×2160@240。
- 通过活动 DisplayConfig `monitorDevicePath` 明确选择目标。
- 配置保存在 `%LOCALAPPDATA%\SBMS\config-v1.json`，仅包含稳定目标 ID 和可选尺寸请求。同目录临时文件写完并同步后，通过 Windows 原子替换提交；损坏或未知版本的配置会保留并告警，不会被静默覆盖。
- 启动时，将选定物理目标上符合条件且可见的标准顶层窗口迁移到虚拟显示源；随后每 250 ms 扫描一次，继续处理新打开的窗口，或被拖回目标屏的已登记窗口。
- 停止时先解除输入和镜像，再回迁窗口；虚拟拓扑移除后，再做一次终态位置校准。
- 任务栏托盘快速控制面板：选择/刷新物理目标、Start/Stop、状态与错误、能力说明和退出。独立 controller worker 持有 `MappingSession`，阻塞式生命周期操作不占用 UI 线程。
- 点击物理镜像后捕获鼠标并转发到虚拟桌面：支持相对移动、左/右/中/X1/X2 键和垂直/水平滚轮。SBMS 不绘制合成的固定指针，以 Desktop Duplication 图像和指针元数据为准。按 F8 释放捕获。
- 键盘遵循注入点击后 Windows 的正常前台焦点；SBMS 不复制也不记录按键。Print Screen 和 Win+Shift+S 会先释放鼠标捕获，再交给 Windows 处理。
- Raw Input 与低级鼠标/键盘钩子使用独立消息泵，不受 GPU 呈现 worker 阻塞；同一批相对移动包会合并到最新的虚拟桌面绝对位置后再注入。
- 镜像端不合成固定指针标记。仅当 Windows 报告指针没有包含在复制图像中时，才根据 Desktop Duplication 指针元数据绘制当前形状、热点、可见性和位置，避免双光标。
- 纯 Rust 尺寸接口显式暴露原生像素尺寸、物理宽高或对角线、旋转、物理/整数倍策略、对齐和首选刷新率。计算本身保持纯函数；`MappingRequest` 会把结果转成实际请求 IDD 的模式。
- 单文件 x64 Inno Setup 安装包，将两个 Rust 可执行文件和已签名 IDD 包安装到 `Program Files\SBMS`。
- 为当前安装用户注册一个 `Highest` 运行级别的登录任务。这不是装饰性提权：受保护的跨会话模式入口使用 `Global\` 内核对象，中完整性托盘无法创建它。
- 升级与卸载会先通知托盘优雅退出，并最多等待 30 秒让 `MappingSession` 清理。卸载随后删除本产品拥有的登录任务，以及精确匹配 SBMS provider/硬件 ID 的全部 OEM INF。若外部清理失败，会尝试补偿并保留安装器登记的程序文件，不会假装已经卸干净。
- 已在本机实测普通、最大化窗口跨不同 DPI 显示器往返，也验证了会话开始后新打开窗口的持续迁移与回迁。
- 首帧确认、有界停止、连续五次启停，以及并发会话拒绝。
- `--version`、`list`、`create`、`map` 和 `config`；`map` 可显式传入目标，也可使用已保存目标。其中 `create` 只测试原始设备生命周期。

v4 会话入口会在 `SwDeviceCreate` 前携带校验后的宽、高、步长和有理刷新率。IDD 为本次会话只发布一个 Monitor/Target Mode，并只负责排空 IddCx 交换链。Rust 将虚拟源矩形精确匹配到一个 DXGI 输出，像素热路径为 `Desktop Duplication -> D3D11 纹理 -> 像素着色器 -> flip-model swapchain`；不再将整帧回读 CPU，也不再经过共享像素映射或 GDI。

1:1 映射直接绕过滤镜。每个轴都处于 1× 到 2× 缩小时，着色器使用四次双线性纹理读取完成精确源像素面积积分，再用两次水平采样对中性高频区域做保亮度的色度低通，以减轻缩放后的 ClearType 子像素彩边。其他比例回退到双线性采样。这是一种轻量显示后处理，不识别字体，也不等同于 gamma-linear 重采样、HDR 或色彩管理。报告 240 Hz 不代表持续 240 fps。

在已验证机器上，4640×2610@240 到 2560×1440 的 cursor-driven 合成压力连续运行 12 秒，呈现保持 239–240 fps。WUDFHost CPU 为 0%，没有高于 0.01% 的 GPU engine；`sbms` CPU 为 0%，3D engine 平均 3.36%、峰值 3.44%。这是单机合成压力证据，不保证所有内容或硬件都能持续 240 fps。

Windows 可能按固定虚拟显示器身份恢复旧的桌面 Source Mode。启动链路会枚举合法 GDI 模式、临时应用请求分辨率，并等待 DisplayConfig 收敛后才迁移窗口、开启输入和确认首帧。程序会在创建虚拟源前快照物理拓扑；启动失败清理和正常停止都会在移除虚拟源后恢复它，避免 Windows 的自动重排成为用户的最终布局。

固定会话入口使用受保护的 ACL，仅允许启动程序的用户、SYSTEM、LocalService 和 Administrators 访问。当前不存在共享像素映射或帧事件。这是 Windows 身份级验权，而非进程身份认证：以上任一受信身份运行的其他进程仍处于权限边界之内。

不支持：

- 后台服务。
- 自动选择目标、多路映射、会话运行中热改模式、Windows 输出旋转、HDR 或色彩管理。
- 触控、笔、绝对指针转发、键盘重映射或通用拓扑恢复。
- 公开受信任的生产发行包。当前预览安装器、Rust 可执行文件、驱动 DLL 和 catalog 使用本地测试证书签名，且没有公开时间戳；普通机器不会默认信任。
- Windows 原生复制模式语义、HDR 或色彩管理、低延迟游戏串流保证，以及持续 240 fps。

窗口迁移不是一个通用窗口管理器。它只处理当前交互桌面中符合条件、仍然存活且可见的标准顶层窗口。最小化窗口会刻意留在原处：它的 `WINDOWPLACEMENT` 使用 workspace 坐标，DPI 与任务栏偏移语义不适合被通用地改写。受 UIPI 或更高完整性级别阻挡的窗口、挂起窗口、持续自定位的应用，以及会话期间被关闭或重建的窗口，都无法承诺无损恢复，不属于保证范围。

选定的物理显示器会被一个置顶且不激活的窗口覆盖。点击镜像会捕获鼠标；按 F8 后，指针回到物理屏上的对应位置。SBMS 会恢复此前的 `ClipCursor` 边界，并且只释放自己已成功注入的鼠标按键。Windows 的 UIPI 禁止 `SendInput` 跨完整性级别控制更高权限的应用；遇到这种情况，SBMS 会释放捕获并报告错误，而不是假装转发成功。

启动期间会重新验证显示拓扑。运行期间发生的拓扑变化不会被恢复，行为也不受保证；此时应停止并重新启动会话。

## 配置与尺寸接口

```powershell
sbms config path
sbms config show
sbms config set-target <monitor-device-path>
sbms config clear-target
sbms config reset
sbms map
```

`set-target` 只接受当前唯一活动的物理显示器。配置损坏时原文件保持不动，必须显式执行 `reset` 才会清除。公开的 `geometry` 模块提供 `DisplayGeometry`、`SizingRequest`、`SizingStrategy`、`Rotation` 和 `CoordinateTransform`；鼠标转发复用同一坐标变换。CLI 与托盘共享已保存的尺寸请求，前端不得保存推导模式或复制计算/应用逻辑。

## 构建

构建 Rust：

```powershell
cargo build --release
```

在装有 Visual Studio C++ Build Tools 和匹配 WDK 的 PowerShell 中构建驱动（构建时使用 UMDF 2.25 和 IddCx 1.4 头文件/库；INF 声明的运行时扩展为 `IddCx0102`）：

```powershell
.\build-driver.ps1
```

本地测试部署时，传入一个受信任的测试签名证书的 SHA-1 指纹。该命令会签名 DLL、创建并签名目录文件，然后验证两者的签名：

```powershell
.\build-driver.ps1 -SigningCertificateThumbprint <thumbprint>
```

使用 Inno Setup 6 构建完整预览安装包：

```powershell
.\build-installer.ps1 `
  -SigningCertificateThumbprint <thumbprint>
```

输出为 `target\installer\SBMS-Setup-1.1.3-x64.exe`。构建脚本还会用同一测试证书签名两个 Rust 可执行文件和最终安装器，验证全部签名，并打印安装包 SHA-256。

## 安装与使用

运行 `SBMS-Setup-1.1.3-x64.exe` 并批准 UAC。安装器会安装/升级驱动、更新安装器拥有的文件、仅删除明确列出的旧版遗留文件、为当前交互式安装账户注册提权登录任务并启动托盘；它不会清空安装目录中的任意文件。固定 Inno `AppId` 让后续版本执行原位升级。

可从 Windows“已安装的应用”卸载，或运行：

```powershell
& 'C:\Program Files\SBMS\unins000.exe'
```

如果活动映射无法正常停止，或驱动包无法删除，卸载器会拒绝继续删除文件；它不会使用 `pnputil /force`。

开发者仍可在管理员终端中手动安装已签名驱动包：

```powershell
pnputil /add-driver .\target\driver\SBMSIndirectDisplay.inf /install
```

继续在同一个管理员终端中运行：

```powershell
.\target\release\sbms.exe --version
.\target\release\sbms.exe list
.\target\release\sbms.exe map --target '<monitor-device-path>'
.\target\release\sbms.exe ui
.\target\release\sbms-tray.exe
```

从 `physical` 行复制完整的 `id=`。它是 `monitorDevicePath`，不是 `\\.\DISPLAYn`；`map` 启动时，该目标必须仍处于活动状态。符合条件的目标窗口会被自动迁移。只有在 Windows 暴露虚拟显示源，并且 Rust 绘制出第一帧有效画面后，命令才会打印 `running=`。按 Enter 进入正常停止路径并回迁窗口；强制结束进程会绕过用户态恢复。需要限定运行时长的无人值守用法：

```powershell
.\target\release\sbms.exe map `
  --target '<monitor-device-path>' `
  --hold-ms 5000

.\target\release\sbms.exe create --hold-ms 5000
```

手动构建、安装和映射应在管理员终端中完成。安装包中的托盘通过已注册的登录任务获得所需权限。
