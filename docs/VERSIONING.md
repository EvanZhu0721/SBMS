# SBMS Versioning

SBMS uses Semantic Versioning:

```text
MAJOR.MINOR.PATCH[-PRERELEASE.N]
```

`VERSION` at the repository root is the single source of truth. Build scripts,
binary metadata, installers, tags, release notes, and packages must eventually
derive their version from this file instead of maintaining independent labels.

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
