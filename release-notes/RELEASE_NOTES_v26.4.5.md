# PanoramaBridge v26.4.5 Release Notes

A patch release. Folder acquisitions never asked whether anything already occupied their
destination, so one could silently replace an archive another computer had put there. That is the
important one; the rest are smaller faults in where a renamed file is sent.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **A folder acquisition could silently replace an archive it did not put there.** Single files
  have always been checked against what is on the server before anything is sent; a Bruker,
  Waters or Agilent folder was not. It was packed and written straight over whatever occupied its
  destination, with no conflict raised and the **When a different file is already on the server**
  setting never consulted.

  The way in is ordinary: install PanoramaBridge on a second instrument computer and point it at a
  folder another machine has already sent. With nothing in its own records, every acquisition was
  repacked — a lot of disk work on the acquisition machine — and written over the copy already
  there. If the two differed at all, that copy was gone, with nothing recorded and nothing to see.

  A folder is now checked before it is packed, and held for a decision if the destination holds
  something this computer did not put there. Your own earlier copy of the same acquisition is
  still updated without asking. Present since folder acquisitions arrived in v26.3.0.

- **A renamed acquisition folder was sent to the wrong archive.** If a folder had been sent
  alongside under a new name, the next upload went back to the original archive name instead of
  the one it had been sent under.

- **Changing a file's capitalisation on disk sent it to the old name.** The destination was worked
  out from the name as previously recorded rather than the name on disk, so renaming `Run.RAW` to
  `run.raw` kept every later upload going to `Run.RAW`.

## Performance

- **One fewer database read per file on every check.** The upload record was read twice for each
  file examined.
