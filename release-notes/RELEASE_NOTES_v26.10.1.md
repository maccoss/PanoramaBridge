# PanoramaBridge v26.10.1 Release Notes

A single fix, for instruments whose destination folder on Panorama has grown large.

## Bug Fixes

- **Small files stopped transferring into folders holding a lot of data.** Sequence files and
  other small companions beside a season of acquisitions would fail over and over, while the
  `.raw` files beside them uploaded perfectly normally.

  To decide whether a file already on the server still matches the local one, PanoramaBridge asked
  Panorama for a checksum -- but for the whole destination folder rather than for the one file.
  Panorama computes those on demand and does not cache them, at roughly 600 MB/s, so a folder
  holding 19 GB answers in about 30 seconds and one holding 180 GB cannot answer within the five
  minutes allowed. Past that size, deciding about a 73 KB sequence file beside those acquisitions
  became impossible, permanently, and it got worse with every run.

  It now asks for that file's checksum alone. A 73 KB file costs 73 KB of hashing instead of
  180 GB, and the cost no longer grows as the folder fills.

  The `.raw` files were never affected: a file that is not on the server yet skips the question
  entirely, which is why acquisitions kept arriving while their sequence files did not.

  If you saw repeated "Transfer of ... failed" entries for `.sld` files in the log overnight, this
  is what they were. Nothing was lost -- those files simply never sent, and they will now.
