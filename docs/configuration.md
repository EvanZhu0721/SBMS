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

Shared option dropdowns open on hover and omit the saved option. Highlighting
another option previews it in the field; clicking saves it. Leaving the field
and menu cancels the preview. With the menu open, arrow keys preview, Enter
confirms, and Escape cancels.

For a physical target, automatic refresh uses the highest progressive mode
Windows exposes at its current resolution and orientation. A per-display
refresh override takes priority; if mode enumeration is unavailable, SBMS uses
the current target refresh. EDID still supplies physical dimensions, but its
first preferred timing no longer determines the virtual refresh. The target's
physical refresh remains unchanged. The GUI and ordinary `sbms map` command
refresh this choice from live display data; explicit plans and stream-only
outputs keep their specified refresh.

After the first frame, a refresh mismatch briefly shows a centered, rounded
notice on the target. The higher rate is green and the lower rate red. It has
no buttons, does not activate or intercept clicks, and fades after five seconds.
Equivalent nominal rates such as 59.94 and 60 Hz do not trigger the notice.

## CLI interface

```text
sbms gpus
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

- `gpus` prints hardware GPU names, persistent adapter IDs and current-session
  LUIDs as JSON. Use the `id` field for configuration, not the LUID or list index.
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

## GPU preference

The optional top-level `preferred_gpu` field selects the preferred GPU for every
virtual display in the profile. All groups share one IDD adapter, so this is not
a per-group rendering option. Omit it or use `null` for **Automatic**, which
leaves the virtual display's render-adapter choice to Windows.

```json
"preferred_gpu": "<id from sbms gpus>"
```

The settings page exposes **GPU · All groups** while mapping is stopped. The
saved value is the adapter's device interface path and is resolved to its current
LUID on each start. An unavailable explicit selection remains saved and blocks
startup before creating a virtual display; choose another GPU or Automatic.
Imported profiles may therefore need a new GPU selection on another computer.

The selection is a preference, not a guarantee. Windows can assign another GPU;
**Active** displays the selected group's actual GPU after the driver has created
its device. Requested and actual assignments are logged separately. See
[rendering.md](rendering.md#gpu-routing) for how this interacts with Windows
application GPU preferences.

Version 2.2.0 reads existing version 2 profiles without migration. Older SBMS
versions reject the new `preferred_gpu` field. Before downgrading, select
Automatic and save each profile in 2.2.0 so that the field is omitted; otherwise
restore a profile backup made before choosing an explicit GPU.

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
