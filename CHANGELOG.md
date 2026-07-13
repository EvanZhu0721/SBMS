# Changelog

All notable SBMS changes are recorded here. Versions follow Semantic
Versioning; GitHub Issues remain the source of truth for individual features
and bug fixes.

## [Unreleased]

### Added

- Issue #11: local Obsidian development record and semantic-version workflow.

### Fixed

- Issue #9: build the x64 WDK driver from installed Visual C++ targets plus the
  minimal local driver toolsets, without vendoring task assemblies or falling
  back to a stale prebuilt driver.
- Issue #9: make Debug x64 compatible with WDK Control Flow Guard by using
  program-database debug information instead of Edit and Continue.
