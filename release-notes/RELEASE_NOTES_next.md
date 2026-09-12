# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **Several configurations at once.** A configuration is a folder to watch and a Panorama folder
  to send it to, and PanoramaBridge now runs as many of them as you like, each switched on and
  off on its own. Two instruments writing to two folders, or one folder going to two projects,
  no longer means two copies of the application or a settings change between runs. Asked for by
  the lab after using AutoQC Loader, and the new Configurations tab shows what that one does:
  name, who it signs in as, when it was added, and whether it will run.
  - Configurations may use different Panorama servers, and may sign in to one server as different
    people.
  - Two of them may watch the same folder and send it to different destinations.
  - The Local Monitoring and Remote Settings tabs now edit whichever configuration is selected on
    the Configurations tab, and say which one in their headers.
  - Settings that describe this computer rather than one pairing moved to a new Application tab:
    how many files transfer at once, the tray and verbose-logging options, and the additional
    trusted root certificate. They were on the other two tabs, where they would now read as
    belonging to the configuration being edited.
  - **Files at once is shared across every configuration**, not applied to each. Five
    configurations still move as many files at a time as the slider says rather than five times
    as many, because the limit describes your disk and your network link.
  - Transfer Status and Uploads stay one list across everything, with a new Configuration column
    saying which one each row belongs to.
  - Your existing setup becomes the first configuration on update, named after the folder it
    watches, still signed in, still running. Nothing needs re-entering.
  - Configurations can be named on the Local Monitoring tab. Leave the name empty and the folder
    being watched is used instead.

- `pbctl watch` can watch several folders at once. Pass `--also <dir>` for each extra one; they
  share the concurrency limit rather than each taking it, so three folders still move three files
  at a time and not nine.
- `pbctl watch --no-upload` walks the folders and reports what that cost without contacting a
  server, so it needs no credential. `--for MINUTES` stops the run and reports on its own instead
  of waiting to be interrupted. Together they are how the cost of monitoring is measured on a
  machine that has no API key set.

## Bug Fixes

- A transfer that failed for one configuration marked the same file as failed for every other
  configuration sending it, including ones whose copy was already on the server and verified.
  Those files were then shown as failed and sent again. A failure now reaches only the
  destination it happened to, unless the file is one nothing could transfer -- a folder, or a
  name no server will accept -- in which case it still applies to all of them.
- Renaming a file so that only its case changed left a second entry for it in the Uploads table.
  Nothing was ever transferred twice; the extra row was a record of the destination the file used
  to have. It is now cleared when the file is next recorded.

## Performance

## Breaking Changes
