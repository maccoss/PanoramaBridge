# PanoramaBridge v26.2.1 Release Notes

A patch release. Starting monitoring against a destination that already holds a lot of data no
longer sits on **Checking server** for minutes before anything moves.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Performance

- **"Checking server" no longer stalls on a large destination.**

  Panorama computes a folder's checksums on demand, reading every byte in the folder to do it.
  PanoramaBridge was asking for them alongside the folder listing — for every folder, before
  anything knew whether they would be used.

  They are used in exactly one place: when a file of the same name is already on the server and
  the two copies have to be compared. For new acquisitions no name matches, so the wait bought
  nothing at all. A destination holding 300 GB cost several minutes before the first file moved.

  Checksums are now fetched only when something actually compares content, and still once for the
  whole folder, so a batch re-offered into a populated destination still costs a single request.

  Confirmed against a real lab destination rather than only a test server: much faster than the
  previous build.

- **The status line now distinguishes the two steps.** **Checking server** is the quick listing.
  **Hashing the destination** is the slow one, and says why it is slow and that it is paid once
  per folder. Previously one message covered both and had to describe the expensive case, so it
  explained a delay most files never incur.

## Changed

- **"Keep running in the notification area when closed" and "Verbose logging" have moved.** They
  were on the Remote Settings tab under Advanced, beside a trusted-root certificate path, for want
  of anywhere better to put them. Neither is a remote setting. They are now under **This
  application** on the Local Monitoring tab, which is the tab about this machine, and each has a
  line explaining what it does.

  Your existing choices are unaffected — only where the checkboxes appear has changed.

## Known Limitations

Unchanged from 26.2.0, and repeated because they still apply.

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
- **The Thermo RAW check cannot prove a file complete.** It proves the absence of truncation. An
  unrecognised format revision never blocks a transfer and is recorded as unchecked; the **Not
  checked** filter in the Uploads tab lists them. Only revision 66 has been confirmed against real
  files from this lab.
