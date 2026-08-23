# Vendor formats: what is verified, and what is assumed

The MacCoss Lab runs **Thermo instruments only**. Everything PanoramaBridge does for any other
vendor is reasoned from documentation, from what is already stored on Panorama, and from the
shape of the format — not from watching that instrument write a file.

This page says which is which, so nobody has to guess how much to trust a given path, and so a
report from someone with the instrument we lack has somewhere to land.

---

## Where each vendor stands

| Vendor | Shape | Status |
|---|---|---|
| **Thermo** `.raw` | One file | **Verified against 47 real acquisitions, 313 GB**, 2020–2026, up to 9.9 GB, all format revision 66. Includes the truncation check. |
| **Bruker** `.d` | Directory → one `.d.zip` | **Assumed.** Built and tested against folders shaped like a `.d`. No real acquisition has ever been through it. |
| **Waters** `.raw` | Directory → one `.raw.zip` | **Assumed**, and less exercised than Bruker: the same code path, with no fixture modelled on a real one. |
| **Sciex** `.wiff` | Files, with `.wiff.scan` companions | **Not handled as a set.** Each file transfers on its own, which is probably wrong — see below. |
| **Agilent** `.d` | Directory → one `.d.zip` | **Assumed.** Shares the Bruker path by extension; nothing distinguishes them here. |

## What "assumed" actually means

For a directory acquisition, three things are reasoned rather than observed.

1. **That one archive is what Panorama wants.** This comes from evidence rather than nothing: the
   lab's Bruker data on Panorama Public is stored as `.d.zip`, one per acquisition, around 6 GB
   each. That is a strong signal for Bruker and a weaker one for the other vendors.
2. **That the folder has finished being written.** Three signals must settle together — nothing
   inside open for writing, and size, file count and newest timestamp all unchanged for the quiet
   period. This is modelled on how Bruker is described as finishing the files in a `.d` at
   different moments. If some instrument writes in a way that satisfies all three part-way
   through, an acquisition could be sent early.
3. **That storing rather than compressing is right.** Bruker's binary data is already compressed,
   so deflate would cost processor time on an instrument computer for very little. If a format
   turns out to compress well, this is worth revisiting — with a measurement.

## How it can be wrong, and how bad each is

Nothing here modifies or deletes an acquisition, and a folder is only ever sent whole. So the
failure modes are bounded:

| Failure | Consequence | How you would notice |
|---|---|---|
| Sent too early | An incomplete acquisition on Panorama, looking complete | The `.d.zip` is smaller than expected, or does not open |
| Never sent | Nothing transfers; the folder sits there | It stays out of the Uploads tab |
| Refused | Marked failed with a reason | *Could not be packed* in the Uploads tab |
| Packed wrongly | An archive Panorama or Skyline will not read | Downstream tooling rejects it |

The first is the one that matters, and the only one that is quiet.

## If you have one of these instruments

Please try it, and please say what happened either way — a report that it simply worked is worth
as much as one that it did not, because it is the only way any of these rows move from *assumed*
to *verified*.

What is useful in a report:

- The vendor and instrument, and one example acquisition folder name.
- Whether it transferred at all, and whether the `.d.zip` on Panorama opens and is the size you
  expect.
- **The log.** PanoramaBridge records what each folder measured before packing it — file count,
  total bytes, newest timestamp — which is exactly what is needed to work out whether it decided
  correctly. **Help → Application logs**, or `%LOCALAPPDATA%\PanoramaBridge\logs`. Credentials are
  scrubbed, so a log is safe to attach.
- If it went early: whether the instrument was still running when it transferred.

[Open an issue](https://github.com/maccoss/PanoramaBridge/issues) with any of that.

## Sciex, which is not handled

A `.wiff` arrives with `.wiff.scan`, and sometimes `.wiff2`, `.wiff.dia` and others alongside it.
PanoramaBridge currently treats each as an unrelated file: add the extensions and they all
transfer, but nothing knows they belong together, so they can arrive at different times and one
can arrive without the others.

That is very likely wrong, and it is deliberately not guessed at. Whether they should be zipped
as a set, transferred with the companions first, or left exactly as they are is a question for
somebody who works with the data. If that is you, say so.
