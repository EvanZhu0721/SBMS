# 命令行

[English](command-line.md)

日常使用建议通过托盘完成。管理员终端中也可以使用相同的生命周期：

```powershell
sbms list
sbms map --target '<monitor-device-path>'
sbms plan validate examples\two-streams.json
sbms plan run examples\two-streams.json
sbms config list
sbms config show
sbms config import gaming .\gaming.json --activate
sbms shutdown
```

`sbms list` 会列出 `--target` 所需的稳定显示器 ID。前台运行 `map` 时按
Enter，可以正常停止并完成恢复。映射计划最多可混合十六组本地镜像和纯串流
虚拟桌面。最多可以同时保存三套配置；导入、导出、激活和热加载接口见
[配置档案接口](configuration.md)。
