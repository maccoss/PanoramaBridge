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

## Performance

## Breaking Changes
