# PanoramaBridge v26.2.0 Release Notes

PanoramaBridge now reads Thermo `.raw` files before transferring them, and refuses to upload one
that is provably missing bytes.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## New Features

- **A truncated Thermo `.raw` file is no longer uploaded.**

  Until now a file was considered finished when nothing held it open and its size had stopped
  changing. Both of those are statements about the *absence* of change, and neither can tell a
  finished acquisition from an abandoned one — a copy that died part-way across a network share is
  unlocked, perfectly stable, and short.

  PanoramaBridge now also asks the file. It reads the header, follows the internal pointers, and
  checks that the scan index and data actually fit inside the file. A file that is provably short
  is held and shown in the Uploads tab as **Incomplete file**, with the reason. It is never
  uploaded, and never silently dropped.

  This costs about 1,600 bytes read per file no matter how large the acquisition is, and it never
  writes to a `.raw` file.

- **The Uploads tab has a File check column**, and a **Not checked** filter beside it.

  Most files say nothing here — it applies to Thermo `.raw` only. The entries worth looking at are
  the unchecked ones: each names a format revision or a layout the checker does not yet
  understand. Please send those along; they are exactly what tells us where to improve it.

- **`thermoraw-check`, a standalone command-line tool**, is published with this release for
  Windows and Linux. One file, nothing to install, no Thermo libraries needed. It runs the same
  check outside PanoramaBridge, on a folder or a single file, with `--json` for scripting.

  Download `thermoraw-check-win-x64.exe` or `thermoraw-check-linux-x64` from the assets below.

## What it will not do

Worth being plain about, because a check that sounds stronger than it is causes worse decisions
than no check.

- **It does not prove a file is complete.** It proves the absence of one specific kind of damage.
  The best verdict it will ever report is *No truncation detected*.
- **An unrecognised format revision never blocks a transfer.** Thermo ships new revisions, and a
  checker that refused an unfamiliar one would turn a firmware update into an instrument that has
  quietly stopped uploading. Those files transfer and are recorded as unchecked.
- **An aborted acquisition still transfers.** A run that was stopped early is a real file somebody
  may want kept. It is recorded rather than withheld.
- **Thermo only.** Waters `.raw` folders, Bruker `.d` and everything else are untouched.

## Before release

The check was run over **47 real MacCoss Lab acquisitions totalling 313 GB** — 2020 through 2026,
a Q Exactive HF through to current instruments, the largest 9.9 GB, most of them across a network
share. Every one returned *No truncation detected*: no false positives. All 47 were format
revision 66.

Other revisions are inherited from the reference implementation and have not been confirmed
against real files here. If you run an instrument writing something other than revision 66, the
**Not checked** filter is where it will show up, and it is worth telling us.

## Credit

The Thermo RAW file layout is a port of
[thermo-raw-file-validator](https://github.com/mriffle/thermo-raw-file-validator) by **Michael
Riffle** (Apache-2.0), who established it empirically against real acquisitions and published the
findings. Thermo does not document the format; essentially none of that knowledge is ours. If you
want a positive completeness verdict or embedded-checksum validation, his tool does considerably
more than this one.

Full detail, including seven stated limitations, is in
[`docs/THERMO_RAW_VALIDATION.md`](https://github.com/maccoss/PanoramaBridge/blob/v26.2.0/docs/THERMO_RAW_VALIDATION.md).

## Under the hood

- The upload ledger gained a column for the check result, applied to existing databases by the
  first real schema migration this application has needed. Nothing is dropped or retyped, so
  rolling back to 26.1.2 keeps your upload history readable.

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
