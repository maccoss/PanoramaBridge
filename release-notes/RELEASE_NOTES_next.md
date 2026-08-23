# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

- **"Restart now" could stop working for the rest of a session.** Reported from an instrument
  where the update banner appeared, the update downloaded, and pressing **Restart now** did
  nothing at all.

  It was refusing on purpose. PanoramaBridge will not restart while a transfer is in flight, and
  a transfer that was interrupted — by stopping monitoring, or by cancelling a scan — never
  reported that it had stopped. Its last word was "uploading", so from then on the application
  believed a transfer was still running and quietly declined to restart. The tray menu's **Exit**
  was refusing for the same reason.

  An interrupted transfer now says so, and both buttons explain themselves rather than appearing
  broken when they decline.

## Performance

## Breaking Changes

- **"Start monitoring automatically when PanoramaBridge opens" has been removed.** Monitoring is
  something to begin deliberately, after choosing a folder and a destination. If you had it
  switched on, press **Start monitoring** once after opening.
