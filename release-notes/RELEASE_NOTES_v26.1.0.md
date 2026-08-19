# PanoramaBridge v26.1.0 Release Notes

First release of PanoramaBridge as a native Windows application, rebuilt on .NET 8. This release
establishes the new installer and automatic update path; the monitoring and transfer features
follow in subsequent releases.

## Installing

Download and run `MacCossLab.PanoramaBridge-win-Setup.exe`. It installs for the current user
only and does not require administrator rights, so it works on locked-down instrument PCs.

This build is not yet code-signed, so Windows SmartScreen will warn on first run. Choose
**More info**, then **Run anyway**. Verify the download against `SHA256SUMS.txt` if you would
like to confirm it before running.

There is no upgrade path from the previous Python application — install this alongside it. The
new application reads its own settings from `%LOCALAPPDATA%\PanoramaBridge` and imports your
existing `~/.panoramabridge/config.json` when it first runs.

## New Features

### Automatic updates

- Installed copies check for updates at startup and every four hours, download in the
  background, and apply the update on the next restart.
- **An upload in progress is never interrupted.** The application will not restart itself; it
  stages the update and tells you it is ready.
- Updates download as deltas rather than full packages. A typical update between adjacent
  releases transfers a few hundred KB instead of the full 66 MB.
- A minimum-supported-version floor can be published centrally. A build older than that floor
  stops starting new uploads and prompts to update, while letting any transfer already running
  finish. This makes it possible to retire a release that has a data-integrity problem without
  waiting for everyone to notice.

### Diagnostics

- Logs are written to `%LOCALAPPDATA%\PanoramaBridge\logs`, roll daily, cap at 32 MB each, and
  keep 14 files. The previous version wrote a single unrotated log to whichever directory it
  happened to be launched from.
- Credentials, API keys and authorization headers are scrubbed from log output.
- An unexpected error now reports itself and leaves the application running instead of closing
  the window.

## Breaking Changes

- **The Python application is retired.** The `panoramabridge` PyPI package is no longer the
  recommended way to install, and the Python build is no longer produced. Existing installations
  keep working but will not receive further updates.
- Application data has moved from `~/.panoramabridge/` to `%LOCALAPPDATA%\PanoramaBridge\`.
  Settings are imported automatically on first run. Upload history is not imported: the previous
  format was a Python pickle, which cannot be read safely from .NET. Rebuild it from the server
  with **Reconcile with server**, which is more trustworthy than the old history anyway.
