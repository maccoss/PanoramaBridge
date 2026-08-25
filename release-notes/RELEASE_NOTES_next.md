# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

- **Held files can now be decided about.** When something different already occupies a file's
  destination on Panorama, PanoramaBridge holds the file rather than guessing — but until now
  there was no way to say what should happen, so it stayed held indefinitely.

  The **Uploads** tab now offers three answers, on the **Needs attention** filter: **Replace what
  is on the server**, **Send alongside, under a new name**, or **Keep what is on the server**.
  Tick the files you want to decide about, or leave everything unticked to decide about all of
  them at once — a plate that produced five hundred conflicts is three clicks rather than five
  hundred.

  Nothing interrupts a transfer to ask. Conflicts wait until somebody is at the machine, because a
  dialog appearing on an instrument computer overnight would block every transfer behind it until
  someone clicked it.

  Your decision is remembered across a restart, so it is safe to decide and walk away.

  **Send alongside** picks the first free name — `run.raw` becomes `run (2).raw`, keeping the
  extension where Skyline and Panorama look for it. The name is checked against the server again
  before anything is sent, so a file that arrived at that name in the meantime is never replaced.

  One case is deliberately narrower: a file held because **its own contents are damaged** — a
  Thermo `.raw` that ends before its data does — offers only **Keep**. Replacing a good copy on
  the server with a short one is the outcome that check exists to prevent.

- **A new choice for occupied destinations: send mine alongside it.** Under **When a different
  file is already on the server**, alongside *Ask me*, *Leave the copy on the server alone* and
  *Replace the copy on the server*, there is now **Send mine alongside it, under a new name**. The
  file is sent as `run (2).raw` without asking, and the copy already there is left as it is.

  The behaviour existed in the settings file but had no button and never worked, so nothing
  changes for anyone who has not just gone looking for it.

## Bug Fixes

## Performance

## Breaking Changes
