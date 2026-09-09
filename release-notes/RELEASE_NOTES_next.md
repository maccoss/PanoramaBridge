# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

## Performance

## Breaking Changes

- **`thermoraw-check --json` spells two verdict names differently.** `NotFinalised` is now
  `NotFinalized`, and the unknown-reason `UnrecognisedFormatVersion` is now
  `UnrecognizedFormatVersion`. The JSON emits these names as strings, so a script matching on the
  old spelling will stop matching. Nothing else about the output changed, and no verdict changed
  meaning.

  Nothing PanoramaBridge itself stores is affected: verdicts are logged, never written to the
  upload ledger, so no existing record needs migrating.
