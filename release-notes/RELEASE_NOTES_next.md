# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **Bruker `.d` acquisitions transfer as a single archive.** Point PanoramaBridge at a folder your
  instrument writes `.d` directories into, add `.d` to the file types, and each completed
  acquisition is packed into one `.d.zip` and uploaded — which is how they are already stored on
  Panorama.

  This replaces the behaviour warned about in earlier notes, where the files inside a `.d` could be
  transferred individually and a folder still being written could arrive in pieces. A `.d` is now
  one item: it either arrives complete and verifies against Panorama's own checksum, or it does
  not.

  A folder counts as finished only once nothing inside it is still open for writing **and** its
  size, file count and newest timestamp have all stopped changing — Bruker finishes the files in a
  `.d` at different moments, so any one of those alone would release it too early. An empty `.d` is
  never sent.

  The archive is built beside the acquisition under a `~` name, so the monitor never mistakes it
  for data, and it is removed once the upload is verified. It is stored rather than compressed:
  Bruker data is already compressed, and processor time on an instrument computer is worth more
  than the few percent deflate would save. Packing needs free space roughly equal to the
  acquisition, checked before anything is written — if there is not enough, the transfer is
  declined and says so rather than filling the disk.

  **Please tell us how this goes.** The MacCoss Lab runs Thermo instruments only, so this has been
  built and tested against acquisitions shaped like a Bruker `.d` rather than against a real one.
  The logic that decides a folder has finished, and the assumption that one `.d.zip` is what you
  want on Panorama, are both reasoned from how these formats are documented and stored — not from
  watching a real instrument write one.

  Nothing here modifies or deletes an acquisition, and a folder is only ever sent whole, so the
  ways this can be wrong are: sending one too early, refusing to send one at all, or packing
  something Panorama does not want. The log records what each folder measured — file count, total
  bytes, newest timestamp — so a report of any of those can be acted on. **Help → Application
  logs.**

  The same mechanism covers a Waters `.raw` **directory**, which is a folder where Thermo's `.raw`
  is a file; PanoramaBridge tells them apart and handles each correctly. That path is even less
  exercised than the Bruker one.

## Bug Fixes

- **Companion files now travel with the acquisition they belong to.**

  A Sciex acquisition is a set of siblings: `run.wiff` holds the metadata and `run.wiff.scan`
  holds the spectra, and on a ZenoTOF 8600 dataset that is 38 MB against 8.2 GB. Windows reports
  the extension of `run.wiff.scan` as `.scan`, so asking for `.wiff` matched the metadata and left
  the data behind — and the result was recorded as verified, correctly as far as it went, because
  the one file that was sent did arrive intact.

  Asking for `.wiff` now brings `.wiff.scan` and the other `.wiff.*` files with it. Asking for
  `.raw` behaves exactly as before.

  Nobody has yet used PanoramaBridge on Sciex data, so there is nothing on a server to repair;
  this is about being right when somebody does.

  Two things are deliberately left behind: SQLite's `-journal`, `-wal` and `-shm` working files,
  which Sciex leaves beside every run, and the `.md5` checksum files PanoramaBridge writes itself.

- **"Restart now" could stop working for the rest of a session.** Reported from an instrument
  where the update banner appeared, the update downloaded, and pressing **Restart now** did
  nothing at all.

  It was refusing on purpose. PanoramaBridge will not restart while a transfer is in flight, and
  a transfer that was interrupted — by stopping monitoring, or by cancelling a scan — never
  reported that it had stopped. Its last word was "uploading", so from then on the application
  believed a transfer was still running and quietly declined to restart. The tray menu's **Exit**
  was refusing for the same reason.

  An interrupted transfer now says so, and both buttons explain themselves rather than appearing
  broken when they decline.

## Performance

## Breaking Changes

- **"Start monitoring automatically when PanoramaBridge opens" has been removed.** Monitoring is
  something to begin deliberately, after choosing a folder and a destination. If you had it
  switched on, press **Start monitoring** once after opening.
