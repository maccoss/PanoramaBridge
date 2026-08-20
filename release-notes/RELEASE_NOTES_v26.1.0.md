# PanoramaBridge v26.1.0 Release Notes

PanoramaBridge is now a native Windows application, rebuilt from scratch on .NET 8. This is the
first release that transfers files, and it replaces the Python application.

Point it at the folder your instrument writes into, press **Start monitoring**, and each
acquisition is transferred to Panorama once it has finished being written and confirmed against
the server's own checksum.

## Installing

Download and run `MacCossLab.PanoramaBridge-win-Setup.exe`. It installs for the current user only
and needs no administrator rights, so it works on a locked-down instrument PC.

This build is **not code-signed**, so Windows SmartScreen will warn on first run: choose
**More info**, then **Run anyway**. `SHA256SUMS.txt` is published alongside the installer if you
would like to check the download first.

Install it alongside the Python application rather than over it. The two keep entirely separate
settings and history, so you can run the old one until you are satisfied with this one.

## What it does

### Watches the folder and transfers what appears

- **Start monitoring** watches the folder and everything below it. New acquisitions are picked
  up within seconds and transferred without anyone pressing anything. Tick **Start monitoring
  automatically when PanoramaBridge opens** to have it resume after a reboot.
- Two independent things find your files. The folder is walked in full every fifteen minutes,
  and that walk is what guarantees nothing is missed. Windows change notifications are used as
  well so a file usually starts transferring within seconds, but they are treated as a bonus:
  they get dropped under load, and whether they arrive at all over a network share depends on
  the server.
- A folder on a file server works. Choose a mapped drive and the full network path is recorded
  instead, because a drive letter belongs to one Windows sign-in and would not resolve for a
  service or a scheduled task.

### Never uploads a half-written file

The single most important property, and the reason to trust the rest. A file is transferred only
once **nothing else holds it open** and **its size has stopped changing**, and both checks are
required: an instrument often leaves its output readable while still writing, and Windows does
not keep a file's recorded size up to date while a write handle is open.

A file another program is holding is checked patiently rather than constantly, and never given
up on. A transfer starts within seconds of the instrument letting go.

### Says what it actually checked

- Every upload is confirmed against the checksum Panorama computes over the bytes it stored.
  Nothing is reported as verified on any weaker basis, and the Uploads tab distinguishes
  *Verified (server MD5)* from *Uploaded — size only* from *not verified*.
- A small `.md5` file is written beside each uploaded file, holding its checksum, its size, the
  date the instrument wrote it and the date it was uploaded. The first line is exactly what
  `md5sum` writes, so `md5sum -c run_013.raw.md5` checks the file years from now with no special
  tooling and no access to this application.
- **Uploaded files keep the date they were collected.** A file on Panorama shows the date the
  instrument wrote it rather than the date it was transferred.

### Remembers, so you can answer the question later

The **Uploads** tab reads a durable record rather than the transfer list, so it still answers
"did that actually get uploaded?" next week, after a restart, or on a rebuilt machine. It filters,
searches, and exports to CSV — because in a lab the requirement is usually not to see that a file
transferred but to be able to show that it did.

Because of that record, a folder that is already transferred costs almost nothing to re-check:
no network requests, no reading of file contents, and one database question per five hundred
files.

### Stays out of the instrument's way

This runs on the computer attached to a mass spectrometer, so idle cost was measured rather than
assumed. Watching a folder costs **0.026% of one processor core** and about 21 MB. Transfers run
at below-normal priority so an acquisition always wins.

### Updates itself

Installed copies check for updates at startup and every four hours, download in the background,
and apply on the next restart. **An upload in progress is never interrupted** — the application
never restarts itself; it stages the update and says it is ready. Updates transfer as deltas,
typically a few hundred KB rather than the full 66 MB.

A minimum-supported-version floor can be published centrally, so a release found to have a
data-integrity problem can be retired: older builds stop starting new uploads and prompt to
update, while letting any transfer already running finish.

### Diagnostics

- Logs are written to `%LOCALAPPDATA%\PanoramaBridge\logs`, roll daily, cap at 32 MB each and
  keep 14 files. Credentials, API keys and authorization headers are scrubbed from them.
- An unexpected error reports itself and leaves the application running rather than closing the
  window.
- `pbctl`, a command-line harness, ships alongside for scripted transfers and for measuring what
  monitoring costs on a given machine.

## Breaking Changes

- **The Python application is superseded.** It still works and its source is still in the
  repository, but it is no longer developed and the `panoramabridge` PyPI package is no longer
  the way to install. It will be removed in a future release; nothing you have to do now.
- **Application data has moved** from `~/.panoramabridge/` to `%LOCALAPPDATA%\PanoramaBridge\`.
  Settings are **not** imported automatically in this release — the two settings screens differ
  enough that a silent translation would be worse than filling in four fields once. Upload
  history is not imported either: the old format was a Python pickle, which cannot be read safely
  from .NET.
- **Nothing is lost by not importing the history.** Point the new application at the same folder
  and the same destination, and the first pass recognises everything already on the server from
  its checksums and records it as already there.

## Known Limitations

Stated plainly, because finding these out by surprise is worse than reading them here.

- **Not code-signed.** SmartScreen will warn on first run until a certificate is in place.
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
  that folder's checksums, which it does on demand over everything in it — about thirty seconds
  for a folder holding 19 GB. The status column says **Checking server** while this happens. It
  is paid once per folder per session.
