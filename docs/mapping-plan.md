# Mapping plan interface

The Rust core supports one process-level mapping plan containing up to sixteen
groups. A group ID is a stable zero-based IDD connector index (`0..=15`); list
order is not identity.

```rust
MappingPlan::new(groups)?;
MappingSession::start_plan(plan)?;
MappingSession::start_plan_with_reporter(plan, reporter)?;
session.groups();
session.stop()?;
```

`MappingRoute::Mirror { target }` creates a virtual display, migrates windows
from the physical target, and starts the GPU mirror and input endpoint.
`MappingRoute::StreamOnly` only creates and mode-confirms the virtual display.
It does not create a renderer, migrate windows, or capture input. This is the
route intended for Sunshine or another external capturer.

Startup rejects an empty plan, more than sixteen groups, duplicate/out-of-range
group IDs, duplicate physical targets, invalid modes, missing targets, cloned
GDI targets, and attempts to use an SBMS virtual display as a physical target.
The plan is atomic: a failure rolls back all groups and restores physical
topology once. Runtime insertion, deletion, or mutation of individual groups is
not part of this interface; stop the plan and start a replacement plan.

Each `MappingGroupInfo` exposes:

- stable group/connector ID;
- requested rational mode;
- route and physical target, if any;
- current `\\.\DISPLAYn` name;
- DisplayConfig monitor path;
- Sunshine-compatible `{GUID}`, when it can be resolved.

`\\.\DISPLAYn` is temporary and must not be persisted. Use the group ID for
SBMS configuration and the brace GUID for Sunshine's `output_name`.

Each group may also specify `rotation` as `deg0`, `deg90`, `deg180`, or
`deg270`; omitted values remain `deg0`. `mode.width` and `mode.height` are the
final native desktop dimensions published by the IDD. SBMS does not swap a
portrait mode back to landscape: `1800x2880` remains a native `1800x2880`
display so applications can detect the portrait surface directly.

The rotation value remains the user's orientation and sizing input. Once those
inputs have produced the final native dimensions, the virtual display is
published with identity Windows topology rotation. For example, both portrait
directions produce a native portrait surface rather than applying another
width/height swap. Content rotation, if needed by a future streaming backend,
must be an explicit renderer operation rather than an implicit display-mode
rotation.

For each ready group, the tray starts a separate managed Sunshine instance bound
to that group's brace GUID. Instances use independent port families, so several
virtual desktops can be streamed at the same time without rewriting the global
Sunshine configuration or interrupting another group. The tray exposes the
stream-only instances' LAN address and Web panel. Stopping the mapping also
stops every instance that SBMS created. Moonlight pairing and connection still
happen from the client device.

At plan start, the tray projects Windows Desktop Duplication consumers as one
process per managed Sunshine instance plus one process when any mirror renderer
is present. If that total exceeds Windows' four-process DDA limit, the entire
Sunshine batch starts with the WGC capture backend; otherwise Sunshine keeps its
automatic backend selection. This is a runtime launch override and is not
written into the global or per-instance Sunshine configuration.

The projection covers only processes owned by the current SBMS plan; Windows
does not expose a reliable list of external DDA consumers such as OBS or another
Sunshine installation. Sunshine's WGC selection also does not change the SBMS
mirror renderer: its Desktop Duplication dirty/move-rectangle copy path remains
enabled independently.

## JSON interface

The CLI provides a GUI-independent validation and execution surface:

```text
sbms plan validate plan.json
sbms plan run plan.json
sbms plan run plan.json --hold-ms 30000
```

Example with one local mirror and one streaming desktop:

```json
{
  "groups": [
    {
      "id": 0,
      "mode": {
        "width": 3840,
        "height": 2160,
        "refresh_numerator": 240,
        "refresh_denominator": 1
      },
      "route": {
        "kind": "mirror",
        "target": "\\\\?\\DISPLAY#..."
      }
    },
    {
      "id": 1,
      "mode": {
        "width": 2560,
        "height": 1440,
        "refresh_numerator": 120,
        "refresh_denominator": 1
      },
      "route": {
        "kind": "stream_only"
      }
    }
  ]
}
```

The tray UI persists the complete ordered group list in
`%LOCALAPPDATA%\SBMS\config-v2.json` and builds one `MappingPlan` from it.
Mirror and stream-only routes keep independent display, sizing, mode, refresh,
aspect-ratio and rotation settings. The selected tab is persisted as a group
ID. A valid legacy `config-v1.json` is imported once as mirror group
`Output 1`; the old file is retained.

The compatibility commands `sbms map` and `sbms config set-target` operate on
`Output 1` only. They preserve every other configured group.
