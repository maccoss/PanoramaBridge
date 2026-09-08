# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **A "Never transfer" list on the Local Monitoring tab.** Extensions that are never data even
  when they sit on top of one that is. It starts with `.skyd` and `.tmp`, and you can add
  anything else that appears next to your acquisitions. An extension in the transfer list above
  always wins, so putting one in both boxes still sends it; clearing the box transfers every
  companion file, as before.

## Bug Fixes

- **Skyline chromatogram caches are no longer uploaded as though they were acquisitions.** If
  AutoQC runs on the instrument computer, it leaves `run.raw.skyd` next to `run.raw` for every
  run it imports. PanoramaBridge accepted that as a `.raw` file, so a cache that can run to
  gigabytes — and is rebuilt from scratch every time AutoQC re-imports — was transferred, and
  transferred again, alongside the data. Those files are now skipped, and skipped before they
  cost a hash or a request. Nothing needs to be configured: an existing installation picks the
  new list up when it starts.

  Genuine companion files are unaffected. Asking for `.wiff` still brings `run.wiff.scan`, which
  is where the spectra actually are.

## Performance

## Breaking Changes
