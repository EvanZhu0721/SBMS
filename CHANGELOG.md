# Changelog

All notable SBMS changes are recorded here. Versions follow Semantic
Versioning; GitHub Issues remain the source of truth for individual features
and bug fixes.

## [Unreleased]

### Added

- Issue #11: local Obsidian development record and semantic-version workflow.
- Issue #15: `VERSION`-derived GUI, installer, native, device-host, driver,
  manifest, diagnostic, release-note, and package metadata.
- Issue #13: durable window-migration journals and an out-of-job recovery
  broker restore pending window moves, placement state, and visible work-area
  bounds after abrupt GUI termination.
- Issue #13: launch-gated native and device-host children are contained in a
  kill-on-close Windows Job owned by the GUI.

### Changed

- Issue #13: Start and Stop are generation-safe and idempotent, child shutdown
  returns structured graceful/timeout/kill results, and recovery is bounded to
  three attempts with 250/500/1000 ms backoff inside a 30-second failure window.

### Fixed

- Issue #9: build the x64 WDK driver from installed Visual C++ targets plus the
  minimal local driver toolsets, without vendoring task assemblies or falling
  back to a stale prebuilt driver.
- Issue #9: make Debug x64 compatible with WDK Control Flow Guard by using
  program-database debug information instead of Edit and Continue.
- Issue #13: terminal recovery preserves the first and last causal failures and
  performs full native, window-journal, and device-host cleanup instead of
  leaving a retry hot loop or orphan process.
