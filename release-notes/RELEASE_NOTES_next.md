# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

## Performance

- **Starting monitoring against a destination that already holds a lot of data no longer stalls
  on "Checking server".**

  Panorama computes a folder's checksums on demand, reading every byte in it, and PanoramaBridge
  was asking for them alongside the folder listing -- before anything knew whether they would be
  used. They are read only when a file of the same name is already on the server, which for new
  acquisitions is never, so the wait bought nothing. A destination holding 300 GB cost several
  minutes before the first file moved.

  Checksums are now fetched only when something compares content, and still once for the whole
  folder. The status line distinguishes the two: **Checking server** is the quick listing,
  **Hashing the destination** is the slow part, and says why it is slow.

## Breaking Changes
