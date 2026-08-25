# PanoramaBridge v26.4.6 Release Notes

A single fix. The check that stops a folder acquisition replacing an archive it did not put there
treated a server error as "nothing is there", so a momentary fault could allow the very overwrite
the check exists to prevent.

Installed copies update themselves. Nothing needs reinstalling, and no setting changes.

## Bug Fixes

- **A server error while checking a folder's destination could allow a silent overwrite.** Before
  packing a Bruker, Waters or Agilent folder, PanoramaBridge asks whether anything already occupies
  its place on the server. If that question failed — a busy server, a timeout, a momentary 500 —
  the answer was taken to be "nothing is there", and the folder was packed and written over
  whatever was.

  Not being able to look is not evidence that nothing is there. The folder is now left alone and
  tried again on the next check, which is what happens for single files in the same situation.

  This affects v26.4.5 only, and only folder acquisitions.
