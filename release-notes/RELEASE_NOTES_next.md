# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

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

## Performance

## Breaking Changes

- **`thermoraw-check --json` spells two verdict names differently.** `NotFinalised` is now
  `NotFinalized`, and the unknown-reason `UnrecognisedFormatVersion` is now
  `UnrecognizedFormatVersion`. The JSON emits these names as strings, so a script matching on the
  old spelling will stop matching. Nothing else about the output changed, and no verdict changed
  meaning.

  Nothing PanoramaBridge itself stores is affected: verdicts are logged, never written to the
  upload ledger, so no existing record needs migrating.
