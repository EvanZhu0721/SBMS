# Capture diagnostics

这是独立的 Windows 采集与渲染诊断包。它不运行 SBMS 主程序，不初始化 Slint，不注册输入钩子，不将屏幕像素读回 CPU 或保存。真实 renderer 对照会在 GPU 内采集并显示虚拟源的测试图案。每个条件使用新的进程，避免前一轮 D3D、窗口或采集对象继续存活。

1. 先退出 SBMS，避免活动映射或原有 GUI 干扰实验。
2. 完整解压后双击 `Start-Diagnostics.cmd`，接受管理员提示。请保留驱动子目录。
3. 等待完成，将 `reports` 中本次生成的 ZIP 发回。

不需要手动切换 GPU 设置。外层脚本采集系统信息，分别备份、临时设置并恢复两个诊断 EXE 的 GPU 偏好；专用诊断驱动与产品驱动使用不同硬件 ID、服务和共享内存。原桌面采集对照会先运行，随后测试虚拟源。

默认共 42 项：原桌面 6 项、当前产品驱动虚拟屏 18 项、专用诊断驱动虚拟屏 18 项，只创建两次虚拟屏。每次会话内，省电/高性能偏好的 DDA 都比较原始路由与 `native-output-routing`，覆盖无窗口和源屏动态图案；WGC 两种偏好也各有无窗口/源屏图案对照。保留原始目标屏窗口和省电偏好返回控制。不支持的条件会记录失败或跳过。

每个完整虚拟屏会话还使用同一活跃源运行 4 项真实 renderer 对照：两种 GPU 偏好 × 原始/native 路由，每项默认 3 秒。包内 `sbms-capture-render-probe.exe` 直接编译并调用产品 `src/gpu_renderer.rs` 的 DDA、采样着色器和 Present 路径。源区域来自本次 `ready.source_rect`，目标是 `ready.target_rect` 所在物理屏右下的最多 320×240 像素区域；不额外创建虚拟屏，不运行完整 GUI。配置省略 `high_performance_sampling`，使用产品自动选择；保留产品默认复制与刷新策略。

`native-output-routing` 是仅作用于当前诊断子进程的对照开关，在首次 DXGI 初始化前将 Windows 混合显卡偏好查询置为 UNSPECIFIED，让输出枚举回到原生所属适配器。SBMS 2.2.0 默认使用这一原生路由；诊断包仍保留原始/native 两组条件，每个条件都启动新进程，同一对照期间不重建虚拟屏。诊断运行不会更改产品或系统全局设置。报告记录 hook 是否启用、实际查询次数和实际输出/设备 LUID；声明了开关但没有命中查询不能视为实验生效。

native 枚举进程失败时保留错误报告、退出码与输出，继续其他独立条件，不拿原始路由结果替代 native 结果；各 native 采集仍独立记录成败。原始枚举失败仍中止，避免在缺少基础身份的情况下建屏。

需要继续区分刷新率或驱动渲染 GPU 时，在同一个包中运行 `powershell.exe -NoProfile -File .\run.ps1 -Extended`。扩展增加 60 Hz 原驱动会话和专用驱动的高性能→省电→高性能分配对照；双 GPU 且默认刷新率非 60 Hz 时共 75 项、六次建屏，其中完整会话也包含 4 项 renderer 对照。不自动触发扩展，不重试失败条件；每次会话都必须确认清理成功才继续。完整宿主时限为 35 秒准备上限 + 14×20 秒采集进程上限 + 4×20 秒 renderer 进程上限，共 395 秒；简化会话为 35 + 5×20 = 135 秒。正常完成后立即清理，无额外等待。

诊断运行会临时增加一个虚拟显示器，并短暂显示测试窗口。屏幕可能闪烁；窗口不请求前台激活，不模拟鼠标或键盘输入。采集项的 `shown` 仅测试 HWND 对采集的影响；renderer 项另行验证实际 Present，但不覆盖完整 GUI 或输入处理。

## CLI

```powershell
.\sbms-capture-diagnostics.exe inventory --report inventory.json
.\sbms-capture-diagnostics.exe inventory --native-output-routing --report inventory-native.json
.\sbms-capture-diagnostics.exe probe --config probe.json --report result.json
.\sbms-capture-diagnostics.exe probe --native-output-routing --config probe.json --report result-native.json
.\sbms-capture-diagnostics.exe session --config session.json --report-dir session-report
.\sbms-capture-diagnostics.exe restore --snapshot session-report\topology-before.json --report restore.json
.\sbms-capture-render-probe.exe --config renderer.json --report renderer-result.json
.\sbms-capture-render-probe.exe --native-output-routing --config renderer.json --report renderer-native-result.json
```

`inventory` 是只读枚举，默认 runner 在两种 GPU 偏好下各保留原始/native 枚举。`probe` 不预先初始化 GUI；`window=none` 不创建窗口，`window=shown` 创建测试窗口。DDA 与 WGC 分开测试；API 调用保留原始 HRESULT、调用耗时和对象所属适配器。WGC 空帧池与 API 失败分开记录。`--native-output-routing` 只支持 `inventory` 和 `probe`，不用于会话宿主或拓扑恢复。

独立 renderer EXE 也支持 `--native-output-routing`。其配置仅需 `source_rect`、`target_rect` 和可选 `duration_ms`（默认 3000）；两区域不得相交，均使用物理像素。手工运行时需要外层执行者限制进程超时；默认 runner 每项最多 20 秒，并把该子进程的 `LOCALAPPDATA` 指向 case 目录，保留生产日志且不写入原有 SBMS 日志。

会话配置示例：

```json
{
  "kind": "diagnostic",
  "width": 1314,
  "height": 2336,
  "refresh": 200,
  "timeout_seconds": 180,
  "render_adapter": { "low": 12345, "high": 0 }
}
```

`kind` 可选 `baseline` 或 `diagnostic`。前者使用已安装的产品驱动，存在活动产品 gate 时拒绝开始；后者使用专用驱动。`render_adapter` 仅对专用驱动有效，省略时由系统选择。也支持 `refresh_numerator` / `refresh_denominator`。超时限制为 10–900 秒，默认 120 秒。

会话持有 gate 和软件设备，先保存原始 CCD 拓扑，再等待驱动发布的适配器 LUID 与 target ID；只允许该身份匹配的虚拟源成为采集源。必要时仅扩展该目标，保留原有显示器模式和位置，变化不符便中止。成功后原子发布 `ready.json`，外层根据它启动采集进程。所有 `source_rect` / `target_rect` 均为物理桌面像素 `[left, top, right, bottom]`。默认测试窗口使用原有次屏，无次屏则使用主屏右下的小区域。

采集配置示例（身份字段应从本次 `ready.json` 复制）：

```json
{
  "source_device": "\\\\.\\DISPLAY3",
  "source_identity": [12345, 0, 256],
  "source_monitor_device_path": "本次 ready.json 的 monitor_device_path",
  "backend": "dda",
  "window": "none",
  "target_rect": [1920, 0, 3840, 1080],
  "duration_ms": 1500
}
```

会话收到报告目录中的 `stop` 文件或到达最长时限后关闭自己的软件设备，等待其 PnP 实例与活动源消失，专用驱动还必须释放自己的共享内存占用。随后恢复并重新读取显示拓扑。`session.json.cleanup_ok=true` 需要设备移除、驱动释放和拓扑恢复验证全部满足；仅调用 Drop 不算成功。异常退出时可用独立 `restore` 命令恢复保存的 CCD 快照。快照仅用于本机、本次运行，不应跨机器使用。

## 证据

- `system.json`：系统、显卡驱动和 `win32u.dll` / `dxgi.dll` / `d3d11.dll` 文件版本；找不到的 DLL 版本记为 null。
- `inventory*.json`：适配器、输出和系统 GPU 偏好枚举；`inventory*-process.json` 保留各枚举进程的退出状态。
- `summary.json` / `report.md`：紧凑对照与原始文件相对路径，保留路由命中、实际身份、帧型、耗时、进程退出和清理结果，不重复嵌入逐调用和驱动快照。
- `summary.json.render_cases` / 报告的 renderer 表：独立列出 `returned_ok`、`presented`、`first_present_ms`。进程退出 0 但 `returned_ok=false` 标记为 `renderer-error`；正常返回却未 Present 标记为 `no-present`，缺失字段表示未知。`result.json.run` 保留源图案绘制计数、窗口物理坐标、worker join 和窗口清理结果。
- renderer case 的 `local-appdata/SBMS/logs/`：该子进程写出的原始生产日志；`result.json.diagnostics` 保留最近记录。原始 stdout/stderr 同时保留，超时也能查看已落盘日志。
- `gpu-preference-before.json` / `render-gpu-preference-before.json`：两个 EXE 各自的 GPU 偏好原值及注册表类型，清理时分别恢复。
- 每个 case 的 `config.json`、`result.json`、进程输出：实际输入、逐 API 结果和进程退出状态。
- DDA 的 `desktop_frames` 表示 `LastPresentTime > 0` 的桌面更新，`pointer_only_frames` 是只有鼠标通知的帧，`first_desktop_frame_elapsed_ms` 是首个桌面更新延迟；总帧数不能替代这些字段。WGC 不提供这组 DDA 元数据，报告显示 `n/a`，不能当作零。
- `*.stdout.raw` / `*.stderr.raw`：子进程原始输出字节；对应 `.txt` 是可读副本，解码方式写入进程结果，保留原始证据避免中文乱码丢失信息。
- `*.stderr.txt` 同时包含逐调用立即刷新的 BEGIN/END 日志；即使接口卡住、进程超时退出，也能保留最后开始的调用。
- `ready.json`：严格身份匹配后的源、模式、测试窗口区域和驱动构建标识。
- `driverstate.jsonl`：每 100 毫秒刷新的专用驱动快照及新增事件，含 assigned / actual GPU、D3D 与 SetDevice 结果、首帧、Unassign 和停止信息；环形缓冲丢失单独计数。
- `session-events.jsonl`：宿主阶段、相对耗时、系统 uptime tick 与 UTC 毫秒，用于跨进程对齐。
- `topology-before.json`、`topology-restore.json`、`session.json`：恢复数据和读回验证。
- `windows-events.json`：本轮相关显示驱动、PnP、用户模式驱动和应用崩溃事件；未启用的日志通道只记录状态。
- `prior-sbms-crashes.json`：本轮开始前 24 小时的 SBMS/诊断程序 Application Error 和 WER；最多检查最近 300 条相关提供程序事件、保留 50 条匹配，明确标记为历史证据。
- `sbms-logs/`：只读复制 `%LOCALAPPDATA%\SBMS\logs\sbms.log`、`.1`、`.2`，每份最多 2 MiB；缺失、超限、读取失败和原文件时间写入 `manifest.json`。不启动 SBMS，也不启用全局崩溃转储。

驱动共享协议见 [driver-protocol.md](driver-protocol.md)。进程退出码（含十六进制）与采集 API 返回的 HRESULT 分列；异常退出或缺失结果需要结合 stderr 和 Application Error/WER，不能把非零退出一概叫作崩溃。看到窗口轮成功或改变 GPU 偏好后成功，只能证明该条件下发生变化；需同时核对实际适配器、驱动分配代次和调用顺序，才能判断作用层。桌面采集成功与真实 renderer 的 Present 成功分别判断，后者仍不覆盖完整 GUI 或输入路径。没有目标机器的报告，构建通过不代表故障已修复。

## 构建

从仓库根目录运行：

```powershell
cargo build --release --example capture-routing-render-probe --locked -j1
cargo build --release --locked --manifest-path tools/capture-diagnostics/Cargo.toml --target-dir target/capture-diagnostics
cargo test --locked --manifest-path tools/capture-diagnostics/Cargo.toml --target-dir target/capture-diagnostics
```

该 crate 有独立锁文件与 workspace 边界，依赖 Windows API 投影、JSON 序列化和仅用于显式实验的 `minhook 0.9.0`。新增 hook 依赖的 MIT、MinHook BSD 和 HDE BSD 原文见 `THIRD-PARTY-NOTICES.txt`，随二进制包和源码包一起提供。编译缓存位于仓库 `target/capture-diagnostics`，不包含在诊断包源码中。

`tools/capture-diagnostics/build.ps1` 会先构建产品 renderer example，再构建采集程序和诊断驱动；两个 EXE、runner 和驱动均纳入签名检查。包内 `source/` 保留原诊断工具及驱动源码，并增加 root `Cargo.toml` / `Cargo.lock` / `build.rs`、完整 `src/`（含着色器）和 `ui/`、`assets/app.manifest`、renderer example、`NOTICE.md` 与 `LICENSES/`。虽然探针不初始化 Slint，根项目 build script 仍编译 UI，因此保留 UI 源及 vendored widgets；无需打包 README 图片、构建缓存、Git 历史或运行报告。根许可证与第三方 notices 同时放在二进制包中。
