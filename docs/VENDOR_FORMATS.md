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
| **Bruker** `.d` | Directory → one `.d.zip` | **Naming confirmed** against real data on Panorama Public; the completion logic is still untested against a real instrument. |
| **Waters** `.raw` | Directory → one `.raw.zip` | **Naming confirmed** (`new_LG_6679.raw.zip`); completion logic untested. |
| **Agilent** `.d` | Directory → one `.d.zip` | **Naming confirmed** (`LPK15_11260-S10-R1.d.zip`, 547 of them); completion logic untested. |
| **Sciex** `.wiff` | Files, with companions | **Companion handling verified** against a real ZenoTOF 8600 dataset. Not archived: the files stay separate, as Skyline expects. |

## What "assumed" actually means

For a directory acquisition, three things are reasoned rather than observed.

1. **That one archive is what Panorama wants.** This is now settled rather than assumed. Panorama
   Public stores all three directory formats as `<folder name>.zip`, which is exactly what
   PanoramaBridge produces:

   | Vendor | Example on Panorama Public |
   |---|---|
   | Bruker | `250314_HeLa_100ng_90min_DIA_01_S2-A1_1_507.d.zip` |
   | Waters | `new_LG_6679.raw.zip` |
   | Agilent | `LPK15_11260-S10-R1.d.zip` |
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

## Sciex: companions, not archives

A ZenoTOF 8600 acquisition is a set of sibling files rather than a folder:

| File | Size | What it is |
|---|---|---|
| `run.wiff` | 38 MB | Metadata and the sample list |
| `run.wiff.scan` | **8,197 MB** | The spectra |
| `run.wiff.dia` | 5,459 MB | Derived, written later |
| `run.wiff.dia.quant` | 30 MB | Derived, written months later |
| `run.wiff2` | 74 MB | A second-generation container |
| `run.wiff2-journal` | 0 MB | SQLite's working file |
| `run.timeseries.data` | 24 KB | Instrument telemetry |

These are **not** archived. Skyline opens a `.wiff` by reading the `.wiff.scan` beside it, so both
have to arrive as separate files, which is what PanoramaBridge does.

The trap is in the naming. `Path.GetExtension("run.wiff.scan")` is `.scan`, not `.wiff.scan` — so
a filter of `.wiff` used to match the 38 MB metadata file and nothing else. **A Sciex user
transferred 0.3% of their acquisition and it was recorded as verified**, because the one file that
was sent did arrive intact. Nothing revealed it until somebody tried to open the result.

A file is now accepted if removing trailing extensions one at a time reaches an extension that was
asked for, so `.wiff` brings `.wiff.scan`, `.wiff.dia` and `.wiff.dia.quant` with it. The rule is
about the shape of the name rather than a list of vendor suffixes, because the vendor that invents
the next suffix will not tell us.

Two things are excluded from that walk: SQLite's `-journal`, `-wal` and `-shm` working files, and
the `.md5` sidecar PanoramaBridge writes itself — which would otherwise reach `run.raw` from
`run.raw.md5` and upload our own bookkeeping as data.

**One consequence to be aware of:** asking for `.wiff` now also brings the derived `.wiff.dia` and
`.wiff.dia.quant`, which on this dataset is another 5.5 GB per acquisition. If that is not wanted,
say so — the alternative is a list of exactly which companions are data, which needs someone who
works with the format rather than a guess.
