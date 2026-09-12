# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## Bug Fixes

- **Delete on the Configurations tab did not ask before removing a configuration.** It was meant
  to, and had been since the button was added: the question was written, and nothing ever put it
  on screen. Removing a configuration now asks first, and so does dismissing a row on the Uploads
  tab.
