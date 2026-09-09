# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **A "Never transfer as a companion" list on the Local Monitoring tab.** Extensions that are
  never data even when they sit on top of one that is. It starts with `.skyd`, and you can add
  anything else that appears next to your acquisitions. These are only ignored when they sit on
  top of something else, so an extension in the transfer list above is still transferred in its
  own right; clearing the box transfers every companion file, as before.

  If you transfer `.wiff` files, the settings screen now stops you excluding `.scan` — a Sciex
  acquisition keeps its spectra in the `.wiff.scan` beside the `.wiff`, so excluding it would
  upload the metadata on its own and report it verified.

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

- **A file still being written as `run.raw.tmp` is never transferred.** It was accepted before,
  by the same rule that accepted the Skyline caches. This one is not a setting: it sits with the
  rules that cannot be switched off, alongside the checksum sidecars PanoramaBridge writes itself.
  A finished file whose name merely reads like a temporary one — `QC.tmp.mzML` — is unaffected.

- **A settings file that has been hand-edited into an unusual shape no longer stops the settings
  screen opening.** An empty value where a list was expected now reads as an empty list instead
  of failing, which the "unreadable settings file" fallback did not cover because the file was
  perfectly readable.

## Performance

## Breaking Changes
