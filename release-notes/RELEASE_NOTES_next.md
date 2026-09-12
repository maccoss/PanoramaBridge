# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- `pbctl watch` can watch several folders at once. Pass `--also <dir>` for each extra one; they
  share the concurrency limit rather than each taking it, so three folders still move three files
  at a time and not nine.
- `pbctl watch --no-upload` walks the folders and reports what that cost without contacting a
  server, so it needs no credential. `--for MINUTES` stops the run and reports on its own instead
  of waiting to be interrupted. Together they are how the cost of monitoring is measured on a
  machine that has no API key set.

## Bug Fixes

## Performance

## Breaking Changes
