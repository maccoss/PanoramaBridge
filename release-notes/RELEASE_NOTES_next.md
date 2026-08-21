# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **Closing the window now keeps PanoramaBridge running in the notification area.** The setting
  for this has been in Remote Settings since 26.1.0 and did nothing: the checkbox saved and
  reloaded correctly, but no tray icon was ever created, so closing the window closed the
  application regardless. It is on by default.

  Click the icon to bring the window back, or right-click it for Open and Exit. The first time
  the window is hidden it says so, because Windows files a new icon under hidden icons and a
  window that simply vanishes looks like one that exited.

  If there is no notification area to put an icon in -- a Server Core installation, or policy
  blocking it -- closing the window closes the application as before, rather than hiding it with
  no way back.

  While the window is hidden the icon carries the current status as its hover text, and a
  connection or monitoring failure raises a notification rather than changing only a status line
  nobody can see.

- **Only one copy runs per signed-in user.** Starting PanoramaBridge again brings the running
  copy's window back instead of starting a second one. This matters more now that the window can
  be hidden: two copies would share one upload ledger, walk the same folder, and race each other
  uploading the same file.

## Bug Fixes

## Performance

## Breaking Changes

- **The Python application has been removed from the repository.** It was superseded by the .NET
  application in 26.1.0 and is no longer developed, built, or published. This does not affect the
  installed application in any way -- nothing that ships to an instrument changes.

  The `panoramabridge` package on PyPI is retired. Install
  [the Windows installer](https://github.com/maccoss/PanoramaBridge/releases/latest) instead.

  The retired source, its tests and its documentation remain permanently fetchable from the
  `v0.1.9rc4` tag: `git show v0.1.9rc4:panoramabridge.py`.
