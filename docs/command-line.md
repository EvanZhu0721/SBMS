# Command line

[简体中文](command-line.zh-CN.md)

The tray is the normal interface. The same lifecycle is also available from an
administrator terminal:

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

`sbms list` prints the stable ID used by `--target`. Press Enter to stop a
foreground session cleanly. A mapping plan can contain up to sixteen mirror and
stream-only groups. Up to three persistent configuration profiles can coexist;
see [Configuration profiles](configuration.md) for import, export,
activation and hot-reload interfaces.
