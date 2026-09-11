# PanoramaBridge v26.8.1 Release Notes

Numbers you read while waiting. A file being watched before upload reported its size in raw bytes;
it now reads in KB, MB or GB, and two places where the window quietly disagreed with itself about
the same transfer are fixed. Nothing about how files are found, sent or verified has changed.

**One thing to check if you script against `thermoraw-check`:** two verdict names in its `--json`
output are spelled differently in this release. See Breaking Changes at the end. The application
itself has no breaking change, which is why this is a patch.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **A file being watched now reports its size in KB, MB or GB rather than raw bytes.** While
  PanoramaBridge waits for a file to stop changing, it says how big it is and how much it grew.
  That used to read *"Still being written (9,437,184 to 12,582,912 bytes since the last check)"*,
  which is two numbers nobody can read at a glance and, on a real acquisition, two nine-digit ones.

  It now reads *"Still being written (12.0 MB, up 3.0 MB since the last check)"*. The change is
  stated outright rather than left for you to subtract — on a large file both ends would round to
  the same figure, so a growing acquisition would otherwise appear stuck at the same size. A file
  that shrinks says "down" instead.

  Sizes are the same units Windows Explorer uses, so a file listing and this window agree.

- **The message about a conflicting file on the server reads the same way.** It compared two raw
  byte counts; it now uses the same units as everything else.

- **Every byte count and every time-remaining in the window now comes from one place.** The
  transfer lines, the Uploads table and the command-line tool each had their own copy of the
  rounding rule, which is how the same file ends up described two ways on one screen. Two of them
  had already diverged, so this fixes real disagreements rather than only preventing future ones:

  - A size just short of the next unit showed a figure that should not exist — a file one byte
    under a gigabyte read **"1024.0 MB"** instead of **"1.0 GB"**.
  - The overall progress line said **"3m left"** where the row for the same transfer said
    **"3m 20s left"**. Both now say "3m 20s left".
  - A transfer slower than a kilobyte a second reads "512 B/s" rather than "512.0 B/s".

  Sizes are also written the same way on every machine now, rather than following the computer's
  regional settings — so a figure pasted into a support request matches what the sender saw.

## Breaking Changes

- **`thermoraw-check --json` spells two verdict names differently.** `NotFinalised` is now
  `NotFinalized`, and the unknown-reason `UnrecognisedFormatVersion` is now
  `UnrecognizedFormatVersion`. The JSON emits these names as strings, so a script matching on the
  old spelling will stop matching. Nothing else about the output changed, and no verdict changed
  meaning.

  Nothing PanoramaBridge itself stores is affected: verdicts are logged, never written to the
  upload ledger, so no existing record needs migrating.
