# PanoramaBridge vNEXT Release Notes

Working draft for the next release. Rename this file to `RELEASE_NOTES_v{version}.md` at release
time and update the heading; see `README.md` in this directory for the process.

## New Features

## Bug Fixes

- **Files held by an earlier version are carried over rather than lost.** Two kinds of record
  written by v26.3.0 to v26.4.6 mean something this version cannot act on: a file you chose to
  keep the server's copy of, and a file that had been sent under a new name. Both are now shown
  under **Needs attention** with an explanation, instead of disappearing from the list or being
  sent again to the wrong name.

- **Your settings survive the update even if you had chosen "send mine alongside it".** That
  choice no longer exists, and a settings file naming it would otherwise have been unreadable —
  which PanoramaBridge responds to by starting from defaults, losing the server, the monitored
  folder and everything else. It is read and treated as *Ask me*.

## Performance

## Breaking Changes

- **Deciding about a held file one at a time has been removed.** When something different already
  occupies a file's destination, PanoramaBridge holds the file and shows it under **Needs
  attention**, as it did before v26.4.0. The per-file **Replace / Send alongside / Keep** buttons
  are gone.

  What to do instead: set **When a different file is already on the server** on the Local
  Monitoring tab. *Ask me* holds the file and shows it. Change it to *Leave the copy on the server
  alone* or *Replace the copy on the server* and the files already being held are acted on at the
  next folder check — so the setting is how a backlog is cleared, not only how the next conflict
  is handled. Renaming or moving the local file works too, as it always has.

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
  watching.

  With it off, the folder is walked into like any other and only files matching your file types
  are sent — which for a Bruker or Waters acquisition is usually none of them, because the files
  inside do not carry the folder's extension. That is what happened before v26.3.0. If you rely on
  these formats, turn the setting on.
