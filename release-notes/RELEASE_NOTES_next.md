# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

## Performance

## Breaking Changes

- **Deciding about a held file one at a time has been removed.** When something different already
  occupies a file's destination, PanoramaBridge holds the file and shows it under **Needs
  attention**, as it did before v26.4.0. The per-file **Replace / Send alongside / Keep** buttons
  are gone.

  What to do instead: set **When a different file is already on the server** on the Local
  Monitoring tab — *Ask me* holds the file, *Leave the copy on the server alone* keeps what is
  there, *Replace the copy on the server* sends yours. Or rename or move the local file, which has
  always worked.

  The feature was withdrawn because it was not reliable. Nine reviews of it found faults faster
  than they could be fixed, several of them worse than the problem they replaced, and a held file
  is a safe thing whereas a wrong decision carried out on your behalf is not.

- **The "send mine alongside it, under a new name" conflict setting has been removed.** It needed
  a per-file record of where each renamed file went, which went with the change above. The three
  remaining choices behave as before.

- **Sending acquisition folders as one archive is now off by default.** If you monitor a folder
  that your instrument writes Bruker or Agilent `.d` directories into, or Waters `.raw`
  directories, turn on **Send acquisition folders as a single archive** on the Local Monitoring
  tab.

  It is opt-in because it cannot be checked here: the MacCoss Lab runs Thermo instruments only, so
  no real directory acquisition has ever been written by an instrument this application was
  watching. With it off, such a folder is treated as an ordinary folder and the files inside are
  sent individually — which is what happened before v26.3.0.
