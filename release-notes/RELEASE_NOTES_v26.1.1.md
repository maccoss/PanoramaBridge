# PanoramaBridge v26.1.1 Release Notes

A patch release. **Keep running in the notification area when closed** has been on the Remote
Settings tab, and on by default, since 26.1.0 — and it did nothing. It now does what it says.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **Fixed the notification-area setting, which had no effect at all.** The checkbox saved and
  reloaded correctly, so it looked like it worked, but no tray icon was ever created and closing
  the window always closed the application.

  Closing now leaves PanoramaBridge running and watching. Click the icon to bring the window
  back, or right-click it for **Open** and **Exit**. Because Windows files a new icon under
  hidden icons, the first time the window is hidden it says so — otherwise the window appears to
  simply vanish, which looks exactly like having exited.

  Turn it off on the Remote Settings tab if you would rather closing meant closing.

- **A hidden window can now report a problem.** While the window is closed, the icon's hover text
  carries the current status, and a connection or monitoring failure raises a notification.
  Previously a rejected API key or an unreachable server changed only a status line inside a
  window nobody could see, which on an instrument computer can go unnoticed for weeks.

- **Exit asks before abandoning a transfer.** Choosing **Exit** while an upload is in progress now
  confirms first, rather than stopping it part-way without comment.

- **Updating no longer leaves a dead icon behind.** Applying an update removed the application but
  not its icon, so the notification area kept drawing a stale one and the restarted copy added a
  second alongside it.

## New Features

- **Only one copy runs per signed-in user.** Starting PanoramaBridge when it is already running
  brings the existing window back instead of starting a second copy.

  This matters more now that the window can be hidden: a copy you cannot see is a copy you will
  start again, and two of them would share one upload ledger, walk the same folder, and race each
  other uploading the same file.

## Breaking Changes

Nothing changes for the installed Windows application. This affects only the retired Python
package.

- **The Python application has been retired completely.** It was superseded in 26.1.0, and its
  source, tests and build scripts have now been removed from the repository.

  The `panoramabridge` package on PyPI is archived, and all four of its releases are yanked, so
  `pip install panoramabridge` no longer resolves. Install
  [the Windows installer](https://github.com/maccoss/PanoramaBridge/releases/latest) instead —
  it is a different application and shares no settings or history with the Python one.

  Nothing is lost. The retired source and its documentation remain permanently fetchable from the
  `v0.1.9rc4` tag: `git show v0.1.9rc4:panoramabridge.py`.

## Known Limitations

Unchanged from 26.1.0, and repeated because they still apply.

- **Not code-signed.** SmartScreen will warn on first run until a certificate is in place.
  Automatic updates do not prompt again, because the installed application applies them itself.
- **Folder acquisitions are not yet handled as single items.** Bruker `.d` and Waters `.raw`
  directories transfer as the individual files inside them rather than as one atomic unit, so a
  folder that is still being written can transfer partially. Set **Unchanged for (seconds)**
  generously if you acquire into folders, and prefer to watch a folder these are moved into once
  complete.
- **Conflicts are recorded, not resolved.** If a file this application did not upload already
  occupies a destination, the transfer is held and marked *Needs a decision* in the Uploads tab.
  There is no dialog to resolve it yet; remove or rename the remote copy and it transfers on the
  next check.
- **One monitored folder** per installation.
- **The first transfer into a destination holding a lot of data pauses** while Panorama computes
  that folder's checksums, which it does on demand over everything in it -- about thirty seconds
  for a folder holding 19 GB. The status column says **Checking server** while this happens. It is
  paid once per folder per session.
