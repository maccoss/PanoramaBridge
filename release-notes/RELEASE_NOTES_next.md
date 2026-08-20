# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

## Performance

## Breaking Changes

- **The Python application has been removed from the repository.** It was superseded by the .NET
  application in 26.1.0 and is no longer developed, built, or published. This does not affect the
  installed application in any way -- nothing that ships to an instrument changes.

  The `panoramabridge` package on PyPI is retired. Install
  [the Windows installer](https://github.com/maccoss/PanoramaBridge/releases/latest) instead.

  The retired source, its tests and its documentation remain permanently fetchable from the
  `v0.1.9rc4` tag: `git show v0.1.9rc4:panoramabridge.py`.
