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

# Vendor formats: supported inputs

The MacCoss Lab runs **Thermo instruments only**, and support is limited to input shapes that can
be exercised with evidence. PanoramaBridge never writes an acquisition; it first proves that a
candidate file is no longer held open and its length has remained stable for the configured quiet
period.

| Vendor | Shape | Status |
|---|---|---|
| **Thermo** `.raw` | One file | **Verified against 47 real acquisitions, 313 GB**, 2020–2026, up to 9.9 GB, all format revision 66. Includes the truncation check. |
| **Sciex** `.wiff` | Files with companions | **Verified against a real ZenoTOF 8600 dataset**. Companion files stay separate, as Skyline expects. |

## Directory acquisitions

Bruker and Agilent `.d` directories and Waters `.raw` directories are **not supported**. The
previous archive implementation was withdrawn because no local instrument could exercise the
decision that a directory acquisition had finished writing. Packing downloaded data established
neither that decision nor the crash and recovery paths, and a feature that can send an acquisition
documentation.

Those directories are walked as ordinary folders. PanoramaBridge transfers only files inside that
match the configured extensions; it does not create or upload a `.zip` archive for the folder.

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
