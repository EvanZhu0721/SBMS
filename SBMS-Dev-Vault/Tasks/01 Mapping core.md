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
- Start returns only after a frame is copied into the target window.
- The mapping worker exposes no GUI, input, migration, or recovery policy.
- Signed driver `0.2.5.0`: the physical target changed from native
  `32,32,32` to the current virtual source frame; source and target both
  sampled `0,0,0`. An earlier colored frame check measured source
  `98,68,155` and target `96,67,155`.
