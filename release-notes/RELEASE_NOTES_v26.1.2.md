# PanoramaBridge v26.1.2 Release Notes

A patch release correcting two defects in 26.1.1, one of which its own release notes described as
already fixed.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **Exiting during a transfer started by monitoring no longer cancels it without asking.** 26.1.1
  added a confirmation when **Exit** would abandon an upload, and it only recognised uploads
  started by hand with **Upload now**. A file being transferred by monitoring was not counted, so
  the ordinary case — an unattended machine part-way through an acquisition — got no prompt and
  the upload was cancelled silently.

  The interrupted file was never at risk of being recorded as transferred, and the next check
  uploads it again. What was lost was the chance to say no.

- **Applying an update mid-transfer had the same gap.** The updater already refused to restart
  while a transfer was running, and it asked the same incomplete question, so an update applied
  while monitoring was uploading restarted straight through it.

- **One copy per user now means per user, across sessions.** The check 26.1.1 introduced was
  scoped to a single Windows session, while the upload ledger it protects is shared by the
  account across all of them. One user signed in to two sessions could therefore still run two
  copies over one ledger. Exclusion is now a lock held on a file beside the ledger, which is the
  same scope as the thing it protects, and is released by Windows if the application ever stops
  unexpectedly.

## Corrections to the 26.1.1 notes

Stated here because those notes are published and were wrong.

- They said Exit "asks before abandoning a transfer". It did not, for transfers started by
  monitoring. Fixed above.
- They said closing the window "leaves PanoramaBridge running and watching". It keeps running,
  but it only keeps watching if monitoring was already on. The notification it raises has always
  said which.
- Their Breaking Changes section opened by saying nothing changes for the installed Windows
  application. Nothing *breaks*, but the close button's behaviour did change, which is the whole
  point of that release.

## Known Limitations

Unchanged from 26.1.0, and repeated because they still apply.

- **Not code-signed.** SmartScreen will warn on first run until a certificate is in place.
  Automatic updates do not prompt again, because the installed application applies them itself.
- **Folder acquisitions are not yet handled as single items.** Bruker `.d` and Waters `.raw`
  directories transfer as the individual files inside them rather than as one atomic unit, so a
  folder that is still being written can transfer partially. Set **Unchanged for (seconds)**
  generously if you acquire into folders, and prefer to watch the folder they are moved into once
  complete.
- **Conflicts are recorded, not resolved.** If a file this application did not upload already
  occupies a destination, the transfer is held and marked *Needs a decision* in the Uploads tab.
  There is no dialog to resolve it yet; remove or rename the remote copy and it transfers on the
  next check.
- **One monitored folder** per installation.
- **The first transfer into a destination holding a lot of data pauses** while Panorama computes
  that folder's checksums, which it does on demand over everything in it — about thirty seconds
  for a folder holding 19 GB. The status column says **Checking server** while this happens. It is
  paid once per folder per session.
