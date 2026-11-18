# Updates

SBMS checks for a stable update at tray startup and every 24 hours. It downloads
the installer in the background, verifies its signature and version, then lights
the taskbar meter's red dot. It never opens a desktop window for an update.
The next time the user opens SBMS, an update card appears inside its panel.
Finishing a download while that panel is already open does not interrupt it.
Network failures are logged without changing mapping
status. Downloads use Windows proxy settings, or an explicit `HTTPS_PROXY`.
GitHub is the primary source. If its metadata cannot be fetched or is invalid,
SBMS tries the blog mirror once and downloads from that mirror. If GitHub
metadata succeeds but the installer transfer fails, SBMS tries the mirror once
for the same version, revision, and size. Partial downloads restart from zero.
Local file errors and signature or installer-version verification failures do
not trigger a source change. Logs identify the primary or fallback source.

**Later** dismisses the card and returns to the current page, retaining the
verified installer in `%LOCALAPPDATA%\SBMS\updates`. Opening SBMS again offers
the update again. A red dot at the taskbar meter's upper-right corner stays visible while
the update is ready, including after choosing **Later**.
Hovering **Later** reveals **Skip** above it; keyboard users can focus the same
option. **Skip** hides the card and red dot for that version and revision,
including after restarting SBMS. A different version or revision is offered
normally. The downloaded installer is retained in the cache.
**Install update** rechecks the file and opens the normal interactive
installer. Only the installer stops SBMS when installation actually begins.
Cancelling before that point leaves mappings running. Driver updates may require
a reboot, and the installer does not resume mappings automatically.

Only installers signed by the same trusted certificate as the running SBMS
executable are accepted. Unsigned development builds cannot install automatic
updates. A signing-certificate change requires a manual upgrade.

## Publishing

- Keep `UPDATE_REVISION` at `1` for a new package version. Increment it for a
  replacement installer with the same version. Each version/revision identifies
  one installer and must not be reused for different content.
- Run the normal signed `build-installer.ps1` without `OutputSuffix`. It creates
  one `SBMS-Setup-<version>-x64-r<revision>.exe` and `update.json` under
  `target/installer`. Manual downloads and automatic updates use this same installer.
- Upload the revision-specific installer first, then `update.json`, to the stable
  `v<version>` Release. Keep that Release selected as the latest stable release.
  Beta and unsigned builds do not produce update metadata.
- Synchronize the same signed revision-specific installer to
  `https://blog.imzhuyi.fan/downloads/EvanZhu0721/SBMS/releases/v<version>/SBMS-Setup-<version>-x64-r<revision>.exe`,
  then publish the same `update.json` at
  `https://blog.imzhuyi.fan/downloads/EvanZhu0721/SBMS/latest/update.json`.
  Keep the mirror synchronized for each stable release; do not repackage or
  re-sign the mirrored installer. Both sources use the same signature checks.
  The blog's mirror timer handles this about every 15 minutes; starting
  `blog-mirror.service` after publication synchronizes it immediately.

The updater reads
`https://github.com/EvanZhu0721/SBMS/releases/latest/download/update.json` first,
with `https://blog.imzhuyi.fan/downloads/EvanZhu0721/SBMS/latest/update.json` as
its fallback. An installer fallback uses the already selected release metadata,
even if the mirror's latest metadata is older.
Metadata contains `version`, `revision`, and installer `size` in bytes. Its
version/revision must match the installer's signed four-part file version.
No update service or GitHub token is required. Existing users need one manual
upgrade to a version that includes this updater.
