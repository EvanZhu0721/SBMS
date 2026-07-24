# SBMS Versioning

SBMS uses Semantic Versioning:

```text
MAJOR.MINOR.PATCH[-PRERELEASE.N]
```

`VERSION` at the repository root is the single source of truth. Build scripts,
binary metadata, installers, driver metadata, manifests, diagnostics, release
notes, tags, and package names derive their active version from this file.

## Deterministic Mappings

The accepted prerelease labels map to a monotonically ordered Windows
four-part version:

| SemVer channel | Windows revision |
| --- | ---: |
| `dev.N` | `0 + N` |
| `alpha.N` | `10000 + N` |
| `beta.N` | `20000 + N` |
| `rc.N` | `30000 + N` |
| stable | `65534` |

The major, minor, patch, and prerelease sequence components are restricted to
`0..9999`. For example, `0.1.0-dev.0` maps to `0.1.0.0`, while
`1.2.3-rc.4` maps to `1.2.3.30004`.

`DriverVer` uses the clean source commit's UTC commit date plus the mapped
Windows version:

```text
MM/dd/yyyy,M.m.p.revision
```

The Windows clock is not a version source. Package output is named
`SBMS-<semver>-windows-x64` and
`SBMS-<semver>-windows-x64.zip`.

Builds fail when an active source label, source manifest, or INF placeholder
drifts from the generated-version contract. Historical date-based release
headings remain unchanged.

## Increment Rules

- `MAJOR`: incompatible product, configuration, driver, or control-protocol
  change after 1.0.
- `MINOR`: backward-compatible feature. Before 1.0, a breaking architectural
  milestone also increments `MINOR`.
- `PATCH`: backward-compatible bug or packaging fix.
- Prerelease sequence: `dev.N` -> `alpha.N` -> `beta.N` -> `rc.N` -> stable.

The formalization baseline starts at `0.1.0-dev.0`. Existing date-based beta
labels remain historical release identifiers and are not rewritten.

## Required Flow

1. Create one GitHub Issue before each feature or bug fix.
2. Create a branch named `<type>/<issue-number>-<short-slug>`.
3. Keep the matching local note under `SBMS-Dev-Vault/Issues/` updated.
4. Reference the Issue in commits, pull requests, and relevant logic comments.
5. For a release Issue, update `VERSION`, `CHANGELOG.md`, and the matching
   Obsidian version note together.
6. Build and verify from a clean checkout.
7. Tag the verified commit as `v<version>`; never move an existing release tag.

Every package contains `VERSION`, `SBMS.release.json`, generated current
release notes, component versions, source commit metadata, and SHA-256 hashes
for its payload files. Run `diagnose-sbms.ps1 -VersionOnly` inside the unpacked
package to print the release provenance without probing live display state.
Formal packaging refuses a dirty Git worktree and always rebuilds every
component before it writes provenance; it never reuses a previous release's
driver payload.
