# thermoraw-check

Reports whether a Thermo RAW file has been **truncated**, without Thermo libraries, .NET Framework,
Wine, or an instrument.

Download `thermoraw-check-win-x64.exe` or `thermoraw-check-linux-x64` from the
[latest release](https://github.com/maccoss/PanoramaBridge/releases/latest). One file, nothing to
install.

```console
$ thermoraw-check /data/instrument
QE_20260822_01.raw: No truncation detected
QE_20260822_02.raw: Truncated - needs 4,214,880 bytes, file is 1,048,576
    header is a valid Thermo RAW revision 66
    the acquisition-end timestamp is populated
    the scan index needs 4,214,880 bytes for 52,000 scans, and the file is 1,048,576
```

Exit codes: `0` nothing wrong, `1` at least one file is short, `2` bad arguments, `3` a file could
not be read. `--json` for machine output, `--strict` to fail on anything less than a clean answer.

Nothing here writes to a RAW file: every open is `FileMode.Open` with `FileAccess.Read`, and a
test asserts a file is byte-for-byte unchanged after a check. See
[the validation notes](../../docs/THERMO_RAW_VALIDATION.md) for the full reasoning, the limitations,
and the provenance.

## Why it exists

PanoramaBridge transfers acquisitions off instrument computers, and its central rule is that it
must never upload a half-written file — a partial copy is worse than no copy, because it looks
complete and verifies against its own truncated content.

The two signals normally used to decide a file is finished are that nothing holds a handle to it
and that its size has stopped changing. **Both are statements about the absence of change**, and
neither distinguishes a finished file from an abandoned one. A copy that died part-way across a
network share is unlocked, perfectly stable, and short.

This reads what the file says about itself instead.

## What it checks, and what it does not

It parses the fixed 1,356-byte header, walks the preamble to the controller table, reads the run
header, and checks that every pointer in it — scan index, scan data, instrument log, error log,
trailer, parameters — addresses bytes the file actually contains. Reads are bounded, so a 40 GB
acquisition costs the same as a small one.

**It does not prove a file is complete.** Bytes can be missing from the end of a region whose
pointer still lands inside the file. The verdict is deliberately named `NoTruncationDetected` and
never `Complete`, because a name that sounds like proof gets read as proof.

For a positive completeness verdict, and for embedded-checksum validation, use
[thermo-raw-file-validator](https://github.com/mriffle/thermo-raw-file-validator), which does
considerably more.

### On the embedded checksum

Thermo RAW files carry an Adler-32. It is worth knowing that in tested revisions 57–66 it covers
only the **first 10 MiB** of the file, so on any modern instrument it certifies a small prefix of a
multi-gigabyte acquisition and nothing after it. This tool does not check it; PanoramaBridge
verifies uploads against an MD5 the server computes over every byte it stored, which is strictly
stronger.

## What it costs

Measured against an 8.4 MB file, counting what the reader asks the stream for:

| | |
|---|---|
| Bytes read | 1,656 |
| Read calls | 65 |
| Seeks | 28 |
| Elapsed | 0.013 ms |

Every one of those is fixed by the format, not by the file. A test asserts that a file a thousand
times larger produces byte-for-byte identical counts, because an acquisition can be forty
gigabytes and anything proportional to that would be paid on every sweep.

A file that is not a Thermo RAW file costs exactly one read of 1,356 bytes, which matters because
that is most of what a monitored folder contains.

Those 65 reads are requests to the stream, not disk operations: reading from a real file goes
through a 4 KB buffer, so the small sequential reads coalesce and only the seeks to distant
offsets cost a round trip. On a network share that is the number to watch, and it is under thirty.

## Reading a file that is still being written

Don't. The checker deliberately does not defend against it, and inside PanoramaBridge it does not
have to.

`Validate(string path)` opens the file **shared**, so running it can never interfere with an
instrument writing. That is right for a command-line tool and wrong for a transfer gate: it will
happily read a file mid-acquisition and report nonsense about it.

`Validate(Stream, long size, string path)` exists for the other case. PanoramaBridge's readiness
gate already opens each candidate with `FileShare.None` — an exclusive open that fails outright if
anything else holds a handle, which is how it detects an instrument still acquiring. Running the
check **on that handle** means a file being written cannot reach it: the open would have failed
first. The ordering is a structural guarantee rather than a timing assumption.

The size passed in should be the one read from the open handle, not from the directory entry.
Windows does not keep the recorded size current while a write handle is open, and a stale size is
exactly what would make a growing file look truncated.

## Verdicts

| Verdict | Meaning |
|---|---|
| `NoTruncationDetected` | Every pointer fits and the acquisition is finalised. Not a completeness proof. |
| `Truncated` | Proven short: a pointer or the scan index addresses bytes that are not there. |
| `NotFinalised` | Structurally sound, but the acquisition-end timestamp is absent — the run never finished. |
| `Unknown` | It is a RAW file and nothing useful could be established. Carries a reason. |
| `NotThermoRaw` | Not a Thermo RAW file. |
| `Error` | Could not be read. |

**`Unknown` never means "do not use this file."** Thermo ships new format revisions, and a checker
that refused an unfamiliar one would turn a firmware update into an instrument that has silently
stopped uploading. Unknown verdicts carry a reason — `UnrecognisedFormatVersion`,
`UnconfirmedFormatVersion`, `LayoutNotUnderstood` — and PanoramaBridge records them against the
upload so the gaps are findable rather than invisible.

Structural layout is confirmed for revisions 47, 57, 60, 62, 63, 64 and 66.

## Credit

The file layout is a port of
[thermo-raw-file-validator](https://github.com/mriffle/thermo-raw-file-validator) by Michael
Riffle, Apache-2.0. The offsets and skip lengths are not derivable from anything published; they
are that project's findings, reproduced here rather than rediscovered. Only the truncation half is
ported.

## Building

```bash
dotnet test    src/PanoramaBridge.ThermoRaw.Tests/PanoramaBridge.ThermoRaw.Tests.csproj
dotnet publish src/PanoramaBridge.ThermoRawCheck/PanoramaBridge.ThermoRawCheck.csproj \
  -c Release -r linux-x64 -o out
```

Targets `net8.0` with no package references, and CI builds and tests it on Ubuntu as well as
Windows.

The tests use synthetic files, which show the reader walks its layout consistently but cannot show
the layout matches what an instrument writes. For that, it was run over **47 real acquisitions
totalling 313 GB**, spanning 2020 to 2026 and up to 9.9 GB each: every one returned
`NoTruncationDetected`, and all were format revision 66. Other revisions are inherited from the
reference and have not been confirmed against real files here.
