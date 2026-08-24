# Vendor formats: what is verified, and what is assumed

The MacCoss Lab runs **Thermo instruments only**. Every other vendor's format has been exercised
against real acquisitions downloaded from Panorama Public — Bruker, Waters and Agilent folders,
and a Sciex dataset with its companions — rather than against an instrument writing one. The data
is real; the acquisition in progress is not.

That distinction is the whole of this page. A downloaded folder is already finished, so it
establishes everything about recognising, packing, naming and transferring an acquisition, and
nothing at all about deciding when one has stopped being written. The first set is settled. The
second is not, and cannot be until somebody with the instrument watches it happen — which is why
a report has somewhere to land at the bottom of this page.

---

## Where each vendor stands

| Vendor | Shape | Status |
|---|---|---|
| **Thermo** `.raw` | One file | **Verified against 47 real acquisitions, 313 GB**, 2020–2026, up to 9.9 GB, all format revision 66. Includes the truncation check. |
| **Bruker** `.d` | Directory → one `.d.zip` | **Packing and naming verified** against real acquisitions from Panorama Public. Completion detection untested against a live instrument. |
| **Waters** `.raw` | Directory → one `.raw.zip` | **Packing and naming verified** (`new_LG_6679.raw.zip`). Completion detection untested against a live instrument. |
| **Agilent** `.d` | Directory → one `.d.zip` | **Packing and naming verified** (`LPK15_11260-S10-R1.d.zip`, 547 of them). Completion detection untested against a live instrument. |
| **Sciex** `.wiff` | Files, with companions | **Verified against a real ZenoTOF 8600 dataset**, companions included. Not archived: the files stay separate, as Skyline expects. |

## What is settled, and what is not

For a directory acquisition, three things were once reasoned rather than observed. Two are now
settled; the third is the one that matters most, and it is the one a downloaded folder cannot
settle.

1. **That one archive is what Panorama wants.** Settled. Panorama Public stores all three
   directory formats as `<folder name>.zip`, which is exactly what PanoramaBridge produces:

   | Vendor | Example on Panorama Public |
   |---|---|
   | Bruker | `250314_HeLa_100ng_90min_DIA_01_S2-A1_1_507.d.zip` |
   | Waters | `new_LG_6679.raw.zip` |
   | Agilent | `LPK15_11260-S10-R1.d.zip` |
2. **That the archive is one Panorama and Skyline will read.** Settled for what was tested. Real
   Bruker, Waters and Agilent acquisitions were packed and transferred by this code, and the
   Sciex dataset went with its companions.
3. **That the folder has finished being written.** **Still assumed**, and untouched by either of
   the above: a folder downloaded from Panorama Public is complete before PanoramaBridge ever
   sees it, so nothing about it exercises the decision to wait. Three signals must settle
   together — nothing inside open for writing, and size, file count and newest timestamp all
   unchanged for the quiet period. This is modelled on how Bruker is described as finishing the
   files in a `.d` at different moments. If some instrument writes in a way that satisfies all
   three part-way through, an acquisition could be sent early.

Storing rather than compressing is a judgement rather than an assumption: Bruker's binary data is
already compressed, so deflate would cost processor time on an instrument computer for very
little. If a format turns out to compress well, this is worth revisiting — with a measurement.

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
a filter of `.wiff` used to match the 38 MB metadata file and nothing else: **0.3% of the
acquisition, recorded as verified**, because the one file that was sent did arrive intact. Nothing
would have revealed it until somebody opened the result and found no spectra.

No Sciex data had been transferred with PanoramaBridge before this, so there is nothing on a
server to repair — it was caught by looking at a real dataset rather than at the format's
documentation.

A file is now accepted if removing trailing extensions one at a time reaches an extension that was
asked for, so `.wiff` brings `.wiff.scan`, `.wiff.dia` and `.wiff.dia.quant` with it. The rule is
about the shape of the name rather than a list of vendor suffixes, because the vendor that invents
the next suffix will not tell us.

Two things are excluded from that walk: SQLite's `-journal`, `-wal` and `-shm` working files, and
the `.md5` sidecar PanoramaBridge writes itself — which would otherwise reach `run.raw` from
`run.raw.md5` and upload our own bookkeeping as data.

**One consequence to be aware of:** the rule brings *every* `.wiff.*` sibling, including derived
files. On the dataset above that means `.wiff.dia` and `.wiff.dia.quant`, another 5.5 GB per
acquisition — but note those were written months after the run, by processing rather than by the
instrument. A folder an instrument writes into would not normally contain them; the dataset
examined here is an analysis folder, which is why they are in it.

So this is unlikely to matter when monitoring an instrument, and would matter when pointing
PanoramaBridge at a folder that also holds processed output. If that turns out to be a real
nuisance, the fix is a way to exclude an extension rather than a hardcoded list of which
companions count as data — the latter needs someone who works with the format, and would be wrong
the moment a vendor adds a suffix.
