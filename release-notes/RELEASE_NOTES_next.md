# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

- A settings file that was briefly in use by something else -- antivirus opening it, or a backup
  agent walking your profile -- was treated as though its contents were bad: moved aside and
  replaced with defaults, so the monitored folder, the destination and the sign-in all appeared to
  vanish. A file that cannot be read right now is now retried briefly and then left exactly where
  it is. A file whose contents really are bad is still kept for inspection, as before.
- A settings save that could not replace the file left a `settings.json.tmp` beside it, one for
  every save that lost that race.
- On the Configurations tab, turning a configuration on or off when the settings file could not be
  written left the tick showing a change that had not been saved. The tick now goes back and the
  reason is shown.

## Performance

- Several configurations sending to one Panorama server as the same account now share one
  connection instead of opening one each. Pressing Upload now with eight configurations was
  building and discarding eight connection pools, which meant repeating the TLS handshake for
  every file.

## Breaking Changes

- `pbctl watch --also` now requires `--no-upload`. Every watched folder mirrored into the same
  remote directory, so two files with the same name in different folders overwrote each other on
  the server. The switch exists to measure what several watchers cost, which is what
  `--no-upload` does; to transfer several folders, run `pbctl` once per folder.
