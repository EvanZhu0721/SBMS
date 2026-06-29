# SBMS Project Context

This file is the first-read context document for SBMS. Treat the rules in this
file as project constraints, not suggestions.

## Non-Negotiable GitHub Workflow

Every bug fix and every new feature must have a GitHub Issue before code is
changed or merged.

- Create one GitHub Issue per distinct bug fix or feature.
- Keep the issue title specific enough to identify the affected behavior.
- Reference the issue from the commit message or pull request.
- After the issue is solved, add a brief comment in the involved logic code
  explaining the fix or feature decision and referencing the issue number.
- Keep these code comments short and close to the relevant logic. Prefer:
  `// Issue #123: Re-discover the virtual DISPLAY id after topology changes.`
- Do not use issue comments as a replacement for readable code. The issue note
  should capture why the logic exists, not narrate every line.

## Current Engineering Bias

- Preserve the existing GUI interaction model unless a change is explicitly
  requested.
- Start debugging from real local evidence: logs, installed driver binding,
  PnP state, display list, and actual release payload hashes.
- Driver, host, native, and GUI changes must be verified against the real
  Windows display environment before being considered done.
- Keep the GitHub repository lean: source code, build scripts, tests,
  documentation, issue-linked comments, and the minimum build metadata needed
  for reproducible source builds.
- Keep generated release packages, installed payloads, temporary validation
  scripts, logs, fallback DLLs, and local experiments out of the source repo.
- Issue #3: the only allowed vendored build metadata exception is the minimal
  `msbuild-vctargets-v170/Platforms/<platform>` overlay required when local VS
  Build Tools do not expose WDK driver platform toolsets. Do not vendor release
  binaries to compensate for missing toolchains.
