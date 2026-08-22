# Thermo RAW validation

How PanoramaBridge decides that a `.raw` file is not missing bytes, what that check can and cannot
establish, and where it came from.

For downloading and running the standalone tool, see
[`src/PanoramaBridge.ThermoRaw/README.md`](../src/PanoramaBridge.ThermoRaw/README.md). This page is
the reasoning behind it.

---

## Credit, and what is original here

**Essentially none of the file-format knowledge below is ours.** It is a port of
[**thermo-raw-file-validator**](https://github.com/mriffle/thermo-raw-file-validator) by **Michael
Riffle** (Apache-2.0), who established the layout empirically against a corpus of real
acquisitions and published the findings, the offsets, and the mutation analysis behind them.

That distinction matters, because the offsets and skip lengths in `RawStructure` are not derivable
from any published specification. Thermo does not document the RAW format. Every constant in that
file — that the fixed header is 1,356 bytes, that the format version sits at `0x24`, that the
acquisition-end FILETIME sits at `0x98`, that the preamble holds fourteen length-prefixed strings
before revision 47 adds two more, that a scan-index entry is 72 bytes then 80 then 88 — is
somebody's finding from reading real files, not a fact anyone could look up. They are reproduced
here with attribution rather than rediscovered.

What is ours is narrow:

- the C# implementation, and its bounds-checked reader
- the decision to port **only the truncation half**, and why
- the verdict vocabulary, in particular refusing to have a `Complete` verdict at all
- the rule that an inconclusive answer never blocks a transfer
- the integration: where in the pipeline the check runs, and what happens to a file that fails it

If you need a **positive** completeness verdict, embedded-checksum validation, or scan-packet
integrity, use Riffle's tool. It does considerably more than this does, and it is the reference
this is measured against.

---

## The problem this solves

PanoramaBridge's central rule is that it must never upload a half-written file. A partial copy is
worse than no copy: it looks complete, and it verifies against its own truncated content.

Before a file is transferred it must pass two independent signals, both in
`Core/Monitoring/FileStabilityTracker`:

1. **Nothing else holds it.** The file is opened with `FileShare.None`; if any other handle
   exists, the open fails. A file being acquired into is frequently still *readable*, so a plain
   read open would succeed and prove nothing — only exclusivity answers the question.
2. **Its size has stopped changing**, measured from an open handle rather than the directory
   entry, which Windows leaves stale while a write handle is open.

Both of those are statements about the **absence of change**. Neither can distinguish a finished
file from an abandoned one:

| Situation | Handle held? | Size stable? | Ready by those two signals? | Actually complete? |
|---|---|---|---|---|
| Instrument acquiring | yes | no | no | no |
| Acquisition finished | no | yes | **yes** | yes |
| Acquisition aborted | no | yes | **yes** | **no** |
| Copy died part-way over a share | no | yes | **yes** | **no** |

The last two rows are the gap. A copy whose tool crashed is unlocked, perfectly stable, and short.
This check exists to read what the file says about itself, which is a different question from what
the file system says about it.

---

## It never writes to the file

Stated first because it is the property people most need to be sure of.

Every open is `FileMode.Open` with `FileAccess.Read`. There is no `Create`, no `Append`, no
`Write`, anywhere in `PanoramaBridge.ThermoRaw`, in `thermoraw-check`, or in the coordinator's use
of them. An acquisition is not ours to modify, and a validator that could alter what it validates
would be worse than no validator. A test reads a `.raw` file before and after a transfer run and
asserts that neither a single byte nor the modification time changed.

The share mode is worth a sentence too. The coordinator opens with **`FileShare.Read`**, not
`FileShare.None`. Both detect the case that matters identically — Windows refuses either open while
another handle holds the file for **writing**, which is exactly how "the instrument is still
acquiring" is detected. But `None` additionally locks *every other reader* out for as long as the
handle is held, which is the wrong thing to do on an instrument computer: it would make
PanoramaBridge the reason somebody else's read failed. `Read` also avoids mistaking a concurrent
reader — a backup, a virus scanner — for a writer.

If the open is refused, the check simply has no opinion and the next sweep asks again. A file being
written can therefore never reach the parser: the open fails first. That is a structural guarantee
rather than a timing assumption, and a test holds a `.raw` open for writing and asserts no verdict
is recorded.

---

## What the check does, step by step

All of it lives in `src/PanoramaBridge.ThermoRaw/`. Reads are bounded — nothing walks the body —
so a 40 GB acquisition costs the same as a small one.

### 1. The fixed header — `ThermoRawHeader`

Reads the first 1,356 bytes and requires:

| Offset | Field | Check |
|---|---|---|
| `0x00` | magic | must be `0xA101` |
| `0x02` | signature | UTF-16LE `Finnigan` |
| `0x24` | format version | recorded; drives everything below |
| `0x28` | acquisition start | Windows FILETIME |
| `0x98` | acquisition end | FILETIME; **zero means the run never finished** |

Failing magic or signature ends it: the answer is `NotThermoRaw`, which is an ordinary outcome
rather than an error, because most files in a monitored folder are not RAW files.

### 2. Is the revision one we understand?

Structural layout is **confirmed** for revisions **47, 57, 60, 62, 63, 64, 66**. Revision 8 is
recognised but unconfirmed. Anything else stops here with `Unknown`, carrying a reason.

### 3. The preamble — `RawStructure.LocateRunHeaders`

Steps from the end of the fixed header to the controller address table. The preamble is a run of
length-prefixed UTF-16 strings whose **count changes between revisions**, which is why this cannot
seek to a constant: fourteen strings, then two more and four bytes from revision 47, then fifteen
more from revision 60, then a fixed run of padding and one further string.

Revision 64 introduced 64-bit addressing; before it, addresses are 32-bit and cannot describe a
file above 4 GB, which most acquisitions now exceed.

### 4. The run header — `RawStructure.ParseRunHeader`

Reads the scan range, the trailer and segment counts, and seven addresses: scan index, scan data,
instrument log, error log, scan trailer, scan parameters, and the header's own address. Where a
file has several controllers, the mass-spectrometry one is selected by reporting trailers or by
matching the file's data address.

### 5. Do the pointers fit?

The actual test. Every pointer must address a byte the file contains, and the scan index must have
room for the scans it claims — `scan count x entry size`, where the entry is 72 bytes below
revision 64, 80 from 64, and 88 from 66.

**Two kinds of problem, kept strictly apart:**

- **Proof of truncation** — a structure needs bytes beyond the end of the file. Only this yields
  `Truncated`.
- **An anomaly** — a pointer of zero, a scan range describing no scans, a run header whose
  self-address disagrees. These mean the layout was misread, not that bytes are missing, and they
  yield `Unknown`.

Collapsing those two was a real defect during development. It is a mistake with a direction: in a
reporting tool a false `Truncated` is a wrong line of output, but here it holds back a file that is
perfectly whole.

---

## The verdicts

| Verdict | Meaning | Effect on a transfer |
|---|---|---|
| `NoTruncationDetected` | Pointers fit, acquisition finalised | Uploads |
| `Truncated` | **Proven** short | **Held** |
| `NotFinalised` | Sound, but the acquisition-end timestamp is absent | Uploads, recorded |
| `Unknown` | Nothing could be established. Carries a reason | Uploads, recorded |
| `NotThermoRaw` | Not a RAW file | Uploads, nothing recorded |
| `Error` | Could not be read | Uploads |

Two of these are deliberate and worth defending.

**There is no `Complete` verdict.** The best available answer is `NoTruncationDetected`, because
bytes can be missing from the end of a region whose pointer still lands inside the file. A verdict
that sounds like proof gets read as proof — the same reason the Uploads tab distinguishes
*Verified (server MD5)* from *Uploaded*. A test asserts the enum has no member called `Complete`.

**`Unknown` never blocks a transfer.** Thermo ships new format revisions. A checker that refused an
unfamiliar one would turn a firmware update into an instrument that has silently stopped uploading
— far worse than transferring a file whose structure was not understood. A test walks every verdict
asserting that only `Truncated` holds anything back.

`NotFinalised` also does not block: an aborted run is a real file that someone may well want kept.
It is recorded so the decision is theirs.

---

## How PanoramaBridge uses it

The check runs in `TransferCoordinator`, immediately before the upload decision, and **not** in the
readiness gate. Both matter:

- **Why not earlier.** The gate's job is "has this stopped changing"; the coordinator's is "should
  this go". Running it at the coordinator means a refused file already has a ledger row, so it is
  visible in the Uploads tab rather than being invisibly skipped.
- **Why the file cannot be mid-write.** The coordinator opens it `FileShare.None`. An instrument
  holds its output open for the length of a run, so an exclusive open failing *is* the "still
  acquiring" signal. A file being written can never reach the parser: the open fails first, and
  the check simply returns no opinion. That is a structural guarantee, not a timing assumption.
- **The length comes from the open handle**, never the directory entry — a stale length is exactly
  what would make a growing file look truncated.

A file proven truncated is saved with state `Conflict`, shown as **Incomplete file**, with the
verdict in its detail. It is held, not failed and not silently dropped: uploading it is the one
thing that must not happen, and hiding it is the second.

Every verdict is written to the `raw_check` column of the ledger and shown in the Uploads tab's
**File check** column, with a **Not checked** filter for the inconclusive ones. That filter is a
work list: each entry names a revision or a layout the checker does not yet understand.

### Cost

Measured against an 8.4 MB file: **1,656 bytes read, 65 read calls, 28 seeks, 0.013 ms.** A test
asserts a file a thousand times larger produces byte-for-byte identical counts. Rejecting a
non-Thermo file costs one read of 1,356 bytes, which is the common case in a monitored folder.

The read count is requests to the stream, not disk operations — a real file goes through a 4 KB
buffer, so small sequential reads coalesce and only distant seeks cost a round trip. On a network
share that is the number that matters, and it is under thirty.

---

## Limitations

Read these before trusting a green result.

1. **It cannot prove a file is complete.** It proves the absence of one specific kind of damage.
   Bytes missing from the end of a region whose pointer still lands inside the file are invisible
   to it.

2. **The automated tests use synthetic files; real ones were checked by hand.** The 28 tests build
   RAW files byte by byte and show the reader walks its layout consistently and reacts correctly
   when pointers do not fit. They cannot show the layout matches what an instrument writes, because
   the fixtures are generated from the same understanding the reader uses — they are not
   independent.

   Before 26.2.0 shipped, the tool was run over **47 real MacCoss Lab acquisitions totalling
   313 GB** — spanning 2020 to 2026, from a Q Exactive HF through to current instruments, the
   largest 9.9 GB. **Every one returned `NoTruncationDetected`: no false positives.** All 47 were
   format revision **66**.

   Two things follow. The revision-66 path is exercised against real files and not just fixtures,
   which is the case that matters most because it is what these instruments write. And **no other
   revision has been confirmed the same way** — 47, 57, 60, 62, 63 and 64 are inherited from the
   reference and remain unverified here. A file of one of those revisions is checked with rules
   nobody in this lab has tested. The failure would be a false `Truncated`, which holds a good
   file; it is recorded and visible, not lost.

3. **A new format revision silently stops checking.** By design: it returns `Unknown` and the file
   transfers. The consequence is that validation can lapse without anything failing, which is what
   the **Not checked** filter exists to make visible.

4. **The embedded checksum is not used.** Thermo RAW files carry an Adler-32, but in tested
   revisions 57–66 it covers only the **first 10 MiB**, so on a modern instrument it certifies a
   small prefix of a multi-gigabyte file and nothing after. Riffle documents this and recommends an
   external digest instead — which is what PanoramaBridge's server-MD5 verification already is, over
   every byte the server stored. Adding the Adler-32 would be a weaker guarantee than one we have.

5. **Thermo only.** Waters also writes `.raw`, as a *directory*; those are recognised and skipped.
   Bruker `.d`, Sciex `.wiff` and everything else are untouched.

6. **A file could in principle be reopened after the check.** The window between the exclusive open
   closing and the upload starting is not zero. Nothing in normal acquisition behaviour does this,
   and the existing size-and-hash checks during upload would catch the result.

7. **Anomalies and truncation can be confused by a misread layout.** If the preamble walk goes
   wrong on a file whose revision we believe we understand, the pointers read afterwards are
   meaningless. The `Unknown` path exists for exactly this, but a sufficiently unlucky misread
   could in principle produce a plausible-looking pointer past EOF and a false `Truncated`. This is
   the failure mode to watch for, and the reason limitation 2 matters.

---

## Extending it

To add a format revision:

1. Get real files of that revision and run the current build against them.
2. Add the number to `ThermoRawHeader.RecognisedVersions`, and to `ConfirmedVersions` only once it
   has been checked against real acquisitions.
3. Adjust `RawStructure.LocateRunHeaders` and `ParseRunHeader` if the preamble or run header moved,
   and `RunHeader.ScanIndexEntrySize` if the entry grew.
4. Check what Riffle's tool says about the same files. Disagreement is worth understanding before
   either is trusted.

The **Not checked** filter in the Uploads tab is where the candidates come from.
