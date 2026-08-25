# PanoramaBridge v26.4.1 Release Notes

A patch release. Four faults in the conflict handling v26.4.0 introduced, one of which could
replace a good copy on the server with a damaged one, and a long-standing inefficiency in how
folder acquisitions were re-checked.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **Choosing to replace a file could send a damaged one.** If PanoramaBridge held a Thermo `.raw`
  because reading it showed the file ends before its data does, and you then chose **Replace what
  is on the server**, the short file was sent — over a good copy — and recorded as verified
  against its own truncated contents.

  This was most likely to happen to files held by an earlier version, because those carry no
  record of *why* they were held and so were offered every choice.

  The file is now read again at the moment of sending, whatever was decided. A decision says which
  copy you want; it is not a licence to send one that is broken.

- **Replacing a file that had been sent alongside replaced the wrong one.** If a file had already
  gone up under a new name — `run (2).raw` — and later conflicted again, **Replace what is on the
  server** overwrote the original `run.raw` instead: precisely the copy you chose to preserve when
  you sent yours alongside it. It now replaces its own copy.

- **Keeping or replacing a file forgot that it had been renamed.** After either choice, a file that
  had been sent alongside lost the record of where it lives, so the next time it changed it was
  sent to its original name instead.

- **Files settled from the Uploads tab still counted as needing attention.** The count in the
  status bar did not come down until PanoramaBridge was restarted, so it disagreed with the tab you
  had just used to clear them.

- **A decision that could not be applied now says so.** If a transfer picked a file up while the
  Uploads tab was open, the decision was correctly refused but silently, leaving the impression
  that everything had been settled.

- **Deciding about "all of them" now reaches all of them.** The buttons read the whole record
  rather than the rows on screen, so conflicts older than the most recent few thousand entries —
  or hidden by a search — are included, and the banner offering the buttons no longer hides in
  exactly that case. A file ticked before narrowing the list also stays ticked.

## Performance

- **Completed folder acquisitions are no longer re-examined on every check.** A Bruker, Waters or
  Agilent acquisition reaches Panorama as one `.zip`, and PanoramaBridge was comparing that against
  the folder's own name, deciding they were different, and re-measuring the whole folder on every
  pass — for every acquisition it had ever completed. Nothing was re-uploaded, but on an
  instrument with a few hundred of them it was real disk work, every few minutes, for ever. Present
  since folder acquisitions arrived in v26.3.0.
