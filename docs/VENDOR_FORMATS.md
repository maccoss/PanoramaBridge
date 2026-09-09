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
early has no business staying merely opt-in.

Those directories are walked as ordinary folders. PanoramaBridge transfers only files inside that
match the configured extensions; it does not create or upload a `.zip` archive for the folder.

A file is now accepted if removing trailing extensions one at a time reaches an extension that was
asked for, so `.wiff` brings `.wiff.scan`, `.wiff.dia` and `.wiff.dia.quant` with it. The rule is
about the shape of the name rather than a list of vendor suffixes, because the vendor that invents
the next suffix will not tell us.

Three things are always excluded from that walk, and are not a user's to remove: SQLite's
`-journal`, `-wal` and `-shm` working files; the `.md5` sidecar PanoramaBridge writes itself —
which would otherwise reach `run.raw` from `run.raw.md5` and upload our own bookkeeping as data;
and any name ending `.tmp`, because a `run.raw.tmp` is by definition still being written. These
are matched against the end of the whole name rather than segment by segment, so `QC.tmp.mzML` —
a finished mzML whose stem merely reads like a temporary file — is unaffected.

### Derived output that sits beside an acquisition

**The consequence to be aware of** was that the rule brings *every* `.wiff.*` or `.raw.*` sibling,
including files another program derived from the acquisition. It turned out to matter on the
instrument computer after all, and not only in an analysis folder: AutoQC commonly runs alongside
the acquisition software, imports each run into Skyline as it appears, and leaves the chromatogram
cache next to it as `run.raw.skyd`. The walk reached `.raw` and took it, so caches that can run to
gigabytes — and are rebuilt on every re-import — were transferred as though they were
acquisitions.

No rule about the shape of a name can fix that. `run.raw.skyd` is built exactly the way
`run.wiff.scan` is, and one is derived output while the other is where the spectra live, so
telling them apart needs knowledge rather than logic.

**Never transfer as a companion** on the Local Monitoring tab is that knowledge, and it is the
user's to extend rather than a hardcoded list of which companions count as data. The walk stops at
an extension in it instead of looking past it — so `run.raw.skyd.gz` is refused too, which
checking only the last extension would have missed. It defaults to `.skyd`, Skyline's chromatogram
cache, written beside the acquisition by AutoQC.

Nothing whose absence would be a safety failure belongs in that box, because a user can empty it.
`.tmp` was briefly in it and is now one of the always-on rules above for exactly that reason:
clearing the box to get a companion back would otherwise have re-armed uploading a half-written
acquisition.

An extension in the transfer list always wins, so putting one in both boxes still sends it: the
exclusions can only narrow the companion walk, never veto something somebody typed. That is why
the label says *as a companion*. Clearing the box restores the earlier behaviour of taking every
companion — and settings validation reports the one case where that goes badly wrong, excluding
`.scan` while `.wiff` is being transferred, which would upload the metadata without the spectra.

On the Sciex dataset above, `.wiff.dia` and `.wiff.dia.quant` are still taken — another 5.5 GB per
acquisition. Those were written months after the run by processing rather than by the instrument,
so a folder an instrument writes into would not normally hold them; anyone pointing PanoramaBridge
at an analysis folder can add them.

`pbctl watch --exclude .skyd` is the same setting, and `--exclude ""` reproduces the
behaviour before it existed, which is how the difference is checked against a real folder.
