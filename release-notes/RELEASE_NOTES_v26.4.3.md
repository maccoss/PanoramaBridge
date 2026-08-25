# PanoramaBridge v26.4.3 Release Notes

A patch release, fixing faults in the conflict handling that v26.4.2 introduced. The important one
could decide about every held file on the machine when you had aimed at one.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **A decision could apply to every held file instead of the one you picked.** If you ticked a
  file and a transfer picked it up before you pressed the button, the tick was discarded and the
  decision widened to every conflict on the machine — with no confirmation, and nothing said
  afterwards.

  Aiming at files that are no longer held now does nothing at all, and says so. A tick is also
  used up by the decision it was made for, rather than lingering into the next one.

- **Settled files stayed in the transfer list saying they needed a decision.** Choosing **Replace**
  or **Send alongside** removed the file from the counts but left its row on screen, so the list
  and the status bar disagreed — and because nothing was being transferred at the time, nothing
  redrew it either, which made the button look as though it had done nothing.

- **A file left out of a rename because its contents are damaged is now reported.** It was silently
  skipped, so you could believe every picked file had been renamed while one was still held.

- **A refused rename no longer shows a path the file has never occupied.** When the new name turned
  out to be taken as well, the transfer list showed that name — somebody else's file — rather
  than where your copy actually is.
