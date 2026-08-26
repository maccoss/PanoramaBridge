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

- **A file renamed only by letter case is no longer sent again on every folder check, forever.**
  Windows treats `run.raw` and `RUN.raw` as the same file; Panorama does not. PanoramaBridge now
  works out where a file belongs from the name it currently has on disk, and the ledger's record
  of that name is corrected too, so the file settles instead of being re-offered every time.

- **Recovering many interrupted uploads with verification turned off no longer blocks
  PanoramaBridge from starting.** A crash that left a large number of files mid-transfer could
  previously stall startup indefinitely while recovery tried to queue them all before any upload
  worker began draining the queue.

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

- **Directory acquisition archives have been removed.** Bruker and Agilent `.d` directories and
  Waters `.raw` directories are no longer packed into one archive. PanoramaBridge walks those
  directories like any other and transfers only matching files inside them. The setting has been
  removed because the completion decision could not be validated against an instrument writing
  one, and sending a partial acquisition is worse than not sending it.
