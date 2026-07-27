# Mapping core

Status: complete.

## Goal

Copy one active SBMS virtual desktop onto one explicitly selected physical
display. Never guess a target from resolution, ordering, or `DISPLAYn`.

## Interface

```text
MappingRequest { target: monitorDevicePath }
MappingSession::start(request)
MappingSession::stop()
```

## Acceptance

- Missing, duplicated, virtual, or cloned target selection fails before device
  creation.
- Start waits for the virtual source to enter active topology.
- Start returns only after the first GPU frame is successfully presented to the
  target window.
- The mapping worker exposes no GUI, input, migration, or recovery policy.
- Signed driver `0.2.6.0`: the physical target changed from native
  `30,30,30` to the current virtual source frame; source and target both
  sampled `0,0,0`. An earlier colored frame check measured source
  `98,68,155` and target `96,67,155`.
- Signed 1.1.3 replaces the CPU-readback/shared-memory/GDI pixel path with
  Desktop Duplication, D3D11 shader scaling, and a flip-model swapchain. A
  local 4640x2610@240 to 2560x1440 cursor-driven run presented 239-240 fps for
  12 consecutive seconds. This is one-machine stress evidence, not a guarantee
  for all content or hardware.
