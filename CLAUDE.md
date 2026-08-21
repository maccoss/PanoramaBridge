# PanoramaBridge — guide for AI-assisted development

PanoramaBridge watches directories on mass-spectrometer computers and transfers acquired data to a
Panorama (LabKey) server over WebDAV. It is a Windows desktop application written in C# on
.NET 8 with WPF.

> **Start here:** [`docs/DOTNET_PORT_HANDOFF.md`](docs/DOTNET_PORT_HANDOFF.md) is the current
> state of the work — what is done, what is next, decisions already settled, verified server
> behaviour, and the traps that cost real time to learn. Read it before touching `src/`.

A Python/PyQt6 implementation preceded this one. It was never put into production and is not a
reference: **do not benchmark against it, and do not treat its behaviour as a specification.**
Agreement with it proves nothing and disagreement is not evidence of a regression. It was
removed from the repository after v26.1.0 shipped and survives only in git history, under the
`v0.1.9rc4` tag. Do not restore any part of it.

---

## Technologies

| Technology | Purpose |
|---|---|
| .NET 8, C# 12 | Core language |
| WPF, MVVM (`CommunityToolkit.Mvvm`) | Desktop UI, Windows only |
| `Microsoft.Data.Sqlite` | Upload ledger and hash cache |
| Serilog | Rolling logs under `%LOCALAPPDATA%` |
| Velopack (`vpk`) | Installer, automatic updates, delta packages |
| xUnit, Shouldly | Tests |

Do **not** add FluentAssertions 8 or later — its licence changed to paid for commercial use.

---

## Layout

```
Directory.Build.props        single <Version> (CalVer), warnings as errors
version-policy.json          minimum-supported-version floor
release-notes/               one file per version; becomes the GitHub Release body
src/PanoramaBridge.Core/     all logic, no UI dependency          net8.0
src/PanoramaBridge.App/      WPF shell                            net8.0-windows
src/PanoramaBridge.Cli/      pbctl, the headless harness           net8.0
src/PanoramaBridge.Tests/    xUnit, net8.0-windows so the WPF shell can be tested
```

`Core` must stay free of UI types. That is why the progress aggregator and the readiness gate live
there and not in view models: their behaviour has to be testable without a dispatcher.

---

## Building and testing

```bash
dotnet build PanoramaBridge.sln -c Release
dotnet test  PanoramaBridge.sln -c Release      # no network required
src/PanoramaBridge.App/bin/Release/net8.0-windows/PanoramaBridge.exe
```

Warnings are errors. CI fails on a failing test — unlike the workflow it replaced, which ran
tests with `continue-on-error` and shipped regardless.

### Opt-in suites

These skip cleanly unless their environment variables are set, and clean up after themselves.

| Suite | Variables |
|---|---|
| SMB share monitoring | `PANORAMABRIDGE_SMB_PATH` |

`PANORAMABRIDGE_IT_URL`, `PANORAMABRIDGE_IT_APIKEY` and `PANORAMABRIDGE_IT_PATH` are read by
`pbctl`, not by any test. A live-server suite is still to be written -- see the handoff's open
items for why it matters.

**Never commit a secret.** Credentials come from the environment or Windows Credential Manager,
never from a file in the repository or a command-line argument.

### `pbctl`

`caps`, `ls`, `mkdir`, `md5`, `put`, `sync`, `watch`, `status`, `rm`. Exercises the transport,
the engine and continuous monitoring against a real server without any XAML.

`watch` is also how idle cost is measured — it reports the processor time monitoring used. Give
it a filter matching nothing and it walks the folder without contacting the server at all, so no
credential is involved. See §7 of the handoff for the numbers.

> From Git Bash, set `MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'` first, or MSYS rewrites
> `/_webdav/...` into a local path and every request 404s. It looks exactly like a server fault.

---

## Releasing

CalVer, `YY.feature.patch` — `26.1.0` is the first feature release of 2026. Matches the
convention used by `skyline-prism`.

1. Finalise `release-notes/RELEASE_NOTES_next.md`, rename it to `RELEASE_NOTES_v{version}.md`,
   update its heading, and create a fresh empty draft.
2. Bump `<Version>` in `Directory.Build.props`. It is the single source of truth.
3. Commit, merge to `main`.
4. `git tag v{version} && git push origin v{version}`.

**Pushing the tag builds the artifacts and creates the GitHub Release.** Do not hand-create it.
`release.yml` refuses to publish if the tag and `Directory.Build.props` disagree, and fails with
an explicit message if the notes file is missing — so the rename in step 1 must happen before
tagging. The notes file is published verbatim as the Release body; write it for the people reading
the Releases page.

A tag containing `alpha`, `beta` or `rc` is flagged a GitHub prerelease. A tag containing `beta`
additionally goes to the `win-beta` update channel so it can be piloted on one instrument.
Everything else goes to stable `win`.

---

## House style

### No emojis

Not in code, log messages, documentation, commit messages, test output, or scripts. Use plain
text: `[OK]`, `PASS`, `Verified`, `[FAIL]`, `Error:`, `Warning:`.

### Comments explain why

Especially why an obvious-looking alternative was rejected. This codebase is full of decisions
that look wrong until you know what was measured — `PROCESS_MODE_BACKGROUND_BEGIN` making idle CPU
a hundred times worse, `Progress<T>` posting rather than invoking, WPF omitting `System.IO` from
implicit usings. Each is recorded at the point it matters. Keep doing that; the alternative is
someone helpfully "simplifying" it back.

### Errors are written for a scientist

Someone looking at a stalled transfer, not a stack trace. `WebDavException.ToUserMessage()` is the
pattern: say what happened, and what would fix it.

### Never overstate verification

Nothing reports "verified" unless the server's own hash was compared against the bytes sent.
`VerifyMethod` exists precisely so the UI can distinguish *Verified (server MD5)* from
*Uploaded — size only* from *not verified*. A tick that means less than it appears to is how
upload tracking loses trust, and regaining it is expensive.

### Never upload a partial file

The single most important property. Uploading a half-written acquisition is worse than not
uploading it, because the copy looks complete and verifies against its own truncated content.
Two independent signals are required — an exclusive-open probe and size stability read from an
open handle — and neither is sufficient alone. See §6 of the handoff before changing anything in
`Core/Monitoring`.

### The instrument comes first

This runs alongside vendor acquisition software. Idle cost must be near zero: the application
spends nearly all its life with nothing to do. A recurring timer that fires forever to check
whether anything needs doing is exactly what to avoid — wake on an event instead. Measure before
and after; assumptions here have been wrong by two orders of magnitude.

### Test the cost, not just the result

That the fast path makes **zero** requests and does **no** hashing is what stops upload tracking
regressing. Assertions about absence need a strict fake to be meaningful, and a fake has to be
faithful: one that answers without reading the request body leaves streaming and hashing
untested, and one holding state in a plain `Dictionary` cannot test concurrency.

---

## Common tasks

### Adding a feature

1. Read the relevant part of the handoff — the constraint you are about to hit is probably
   already written down.
2. Put logic in `Core`, keep the WPF layer thin.
3. Add tests, including cost assertions where a fast path matters.
4. `dotnet test` must be green with no warnings.
5. Note anything surprising in the handoff's traps section.

### Fixing a bug

1. Write the failing test first, at the level the bug actually lives — several bugs here only
   reproduced under concurrency, or on CI, or against a real file handle.
2. Fix it, and record *why* the fix is what it is if the cause was non-obvious.
3. Run the full suite; several of these components interact.

### Touching the transport

`Core/WebDav/RemotePath` is the only thing permitted to build a URL. Bypassing it is how the
previous implementation silently broke upload verification for anyone whose server URL had a path
segment.
