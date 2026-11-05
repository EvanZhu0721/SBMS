# Configuration profiles

SBMS stores up to three persistent configuration profiles in
`%LOCALAPPDATA%\SBMS\config-profiles-v1.json`. The complete profile collection,
the active profile identifier, every profile revision and every nested
`AppConfig` are committed as one pretty-printed JSON document. Writes use a
cross-process mutex, a same-directory temporary file, `sync_all`, and a
write-through atomic replacement.

The first profile operation imports the existing `config-v2.json` as the
`default` profile. The legacy file is left unchanged. If the old file is
malformed, unsupported or semantically invalid, initialization fails without
creating or replacing the profile store. A missing old file produces the
normal default configuration.

Profile identifiers contain 1–32 ASCII letters, digits, underscores or
hyphens. They are compared case-insensitively. A collection contains one to
three profiles and exactly one active profile. Display IDs inside a profile are
Windows device identities and may need to be changed after importing onto a
different computer.

## CLI interface

```text
sbms config path
sbms config list
sbms config show [<profile>]
sbms config save <profile>
sbms config import <profile> <file.json> [--replace] [--activate]
sbms config export <profile> <file.json> [--force]
sbms config activate <profile>
sbms config delete <profile>
sbms config reload
sbms config set-target <monitor-device-path>
sbms config clear-target
sbms config reset
```

- `path` prints the profile-store path.
- `list` prints a JSON array containing each profile ID, revision and active
  state.
- `show` prints the active profile, or the named profile, as a standalone
  `AppConfig` JSON document.
- `save` copies the active configuration into the named profile. It creates a
  new profile or replaces the named profile.
- `import` reads a standalone `AppConfig` JSON file, strictly validates its
  schema, version and semantics, and commits it only after validation succeeds.
  Existing profiles require `--replace`; `--activate` also makes the imported
  profile active.
- `export` writes a standalone `AppConfig` JSON file. Existing destinations are
  rejected atomically unless `--force` is present. The profile-store path can
  never be used as an export destination, even with `--force`.
- `activate` atomically changes the active profile and requests a live reload.
- `delete` rejects the active profile and unknown profiles.
- `reload` requests that the running tray reload the already-active profile.
- `set-target`, `clear-target` and `reset` update only the active profile and
  request a live reload.

A successful configuration commit is not rolled back when the tray is not
running. CLI output reports `reload=not-running`; the configuration is loaded
on the next tray or mapping start.

## JSON exchange format

Import and export use the existing standalone `AppConfig` version 2 format,
not the internal multi-profile collection:

```json
{
  "version": 2,
  "groups": [
    {
      "id": 0,
      "route": {
        "kind": "mirror",
        "target_id": "<monitor-device-path>"
      }
    }
  ],
  "selected_group_id": 0
}
```

Unknown fields, unsupported versions, empty or oversized group collections,
duplicate group IDs or mirror targets, invalid geometry and invalid selected
group IDs are rejected before any persistent state changes.

## Rendering options

Each group can include a `rendering` object:

```json
"rendering": {
  "high_performance_sampling": false,
  "full_frame_copy": false,
  "static_refresh": false
}
```

- `high_performance_sampling`: `true` selects the legacy area/bilinear sampler;
  `false` selects the quality sampler. Omitting the field (or using `null`)
  selects the automatic hardware default, not an explicit `false`.
- `full_frame_copy`: `true` copies the complete captured frame instead of using
  dirty/move rectangles. The default is `false`.
- `static_refresh`: `true` keeps presenting the cached image at the target
  refresh rate when no new desktop pixels arrive. The default is `false`.

These options affect local mirror rendering, not an external capturer used for
stream-only output. Omitting the entire object uses all defaults.

Automatic sampling selection enumerates DXGI hardware adapters and queries
`D3D11_FEATURE_DATA_D3D11_OPTIONS2.UnifiedMemoryArchitecture`. If at least one
non-software adapter explicitly reports non-unified memory, quality sampling is
the default; otherwise, including failed or inconclusive detection, the legacy
high-performance sampler is the default. The result is cached once per process.
This checks available adapters, not the GPU actually rendering the group, and
does not recheck hot-plug changes. Hybrid systems defaulting to an integrated GPU,
external-GPU changes and driver misreports may therefore need an explicit
setting; restart to repeat detection.

## Hot reload contract

The CLI signals the running tray only after the profile transaction has been
committed. When no mapping is running, the tray reloads the profile and replaces
its editable in-memory drafts in one UI-thread operation. Existing telemetry is
preserved for unchanged group IDs.

When a mapping is running or changing state, reload is deferred until the
controller reports both `running=false` and `busy=false`. Reload never restarts,
stops or mutates the active mapping. It changes the configuration used by the
next mapping start.

Each profile has a monotonically increasing revision. UI saves use a
profile-ID-and-revision compare-and-swap. A stale UI draft therefore cannot
overwrite a profile imported or activated by another process; the user must
reload before saving again.

## Rust interface

The public API is exported from `sbms::config`:

- `ConfigProfileStore::default_store()` locates the collection.
- `list`, `load_active` and `load_profile` return validated persistent state.
- `save_profile`, `save_active_as`, `activate`, `delete` and `update_active`
  perform locked atomic transactions.
- `save_active_if_revision` is the compare-and-swap save operation for a pinned
  UI session.
- `ConfigStore::load_strict` validates standalone import files; `save` writes
  standalone exports atomically.
- `sbms::control::signal_config_reload` and `listen_for_config_reload` expose
  the cross-process live-reload notification.

The profile collection is the runtime source of truth after initialization.
`config-v2.json` remains only as the preserved migration source.
