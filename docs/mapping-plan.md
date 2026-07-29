# Mapping plan interface

The Rust core supports one process-level mapping plan containing up to eight
groups. A group ID is a stable zero-based IDD connector index (`0..=7`); list
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

Startup rejects an empty plan, more than eight groups, duplicate/out-of-range
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

The existing `sbms map` and tray controller remain a connector-0 single-mirror
adapter. The current GUI and version-1 persisted configuration are deliberately
unchanged; a later GUI can build and persist `MappingPlan` directly.
