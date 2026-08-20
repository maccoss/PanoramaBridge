# PanoramaBridge .NET port — handoff

State of the port as of 2026-08-20. Read this before touching `src/`.

Everything lives on branch **`dotnet-port`**, open as
**[PR #2](https://github.com/maccoss/PanoramaBridge/pull/2)**. `main` is untouched and still holds
the Python application.

---

## 1. Where things stand

| Phase | State |
|---|---|
| 0 — Skeleton and update rail | **Done.** Installer, auto-update, delta packages, CI, release workflow. |
| 1 — WebDAV transport | **Done.** Verified against panoramaweb.org. |
| 2 — Transfer engine and upload ledger | **Done.** Measured against panoramaweb.org. |
| 3 — WPF shell | **Done.** Four tabs, driven end to end via UI Automation. |
| Stability gate | **Done, pulled forward from Phase 4** — it is the highest-risk correctness property. |
| Resource behaviour on an instrument PC | **Done, measured.** |
| SMB share monitoring | **Verified** against a live file server. |
| 4 — Continuous monitoring | **Not started.** See §7. |
| 5 — Datasets, conflict dialog, polish | Not started. |
| 6 — Code signing, ship | Not started. |

281 tests passing, 7 skipped (opt-in SMB), CI green, warnings as errors.

**What works today:** configure the two settings tabs, press **Upload now**, and files are
transferred and verified. There is no automatic watching yet — that is Phase 4.

---

## 2. Running and verifying it

```bash
dotnet build PanoramaBridge.sln -c Release
dotnet test  PanoramaBridge.sln -c Release          # 281 tests, no network needed
src/PanoramaBridge.App/bin/Release/net8.0-windows/PanoramaBridge.exe
```

### `pbctl`, the headless harness

Exists so the transport and engine can be exercised without any XAML. Also the future
unattended mode.

```bash
export PANORAMABRIDGE_IT_URL=https://panoramaweb.org
export PANORAMABRIDGE_IT_APIKEY=<a LabKey API key>
export PANORAMABRIDGE_IT_PATH=/_webdav/MacCoss/maccoss/@files/scratch/

pbctl caps      # server, DAV class, allowed verbs
pbctl ls        # listing with per-entry write permission
pbctl mkdir     # recursive collection creation
pbctl md5       # server-computed hash; trailing slash hashes a whole collection
pbctl put       # upload, then verify against the server's own hash
pbctl sync      # mirror a directory, then report what it cost
pbctl status    # what the ledger holds
pbctl rm
```

### Opt-in test suites

| Suite | Gate | Notes |
|---|---|---|
| SMB monitoring | `PANORAMABRIDGE_SMB_PATH` | A writable folder on a share. Creates and removes its own scratch subfolder. |

> **Running `pbctl` from Git Bash:** set `MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'` first, or
> MSYS rewrites `/_webdav/...` into `C:/Program Files/Git/_webdav/...` and every request 404s.
> This looks exactly like a server problem and is not one.

### Screenshotting the app

A plain screen capture returns a **blank white client area** while the window is perfectly
healthy — a desktop-compositor artefact in this environment. Use `PrintWindow` with
`PW_RENDERFULLCONTENT` (2), which asks the window to draw itself. UI Automation reports the full
element tree either way, so use that to confirm the UI exists before believing a blank image.

---

## 3. Layout

```
Directory.Build.props        single <Version>, CalVer, warnings as errors
version-policy.json          minimum-supported-version floor (see §5)
release-notes/               one file per version; becomes the GitHub Release body
src/PanoramaBridge.Core/     all logic. No UI dependency. net8.0
src/PanoramaBridge.App/      WPF shell. net8.0-windows
src/PanoramaBridge.Cli/      pbctl
src/PanoramaBridge.Tests/    xUnit
```

`Core` is deliberately UI-free, which is why the progress aggregator and the readiness gate live
there rather than in the view models: their behaviour is testable without a dispatcher.

### The parts that carry the design

| Type | Why it matters |
|---|---|
| `WebDav/RemotePath` | **The only thing allowed to build a URL.** In the Python version four call sites each joined URLs their own way and two disagreed, which silently broke verification. |
| `WebDav/PathSafety` | Refuses names the server would mangle; resolves a local file to its destination and asserts containment. |
| `WebDav/WebDavClient` | Recursive MKCOL, `?method=json`, `?method=md5sum`, streaming PUT with a stall watchdog, retry with jitter. |
| `Monitoring/FileStabilityTracker` | Decides when a file has stopped being written. **The highest-risk correctness property in the application.** |
| `Monitoring/ReadinessGate` | Puts that decision in the path. Nothing reaches the engine without passing through it. |
| `Transfer/UploadDecisionService` | The three-tier "does this need uploading?" ladder. |
| `Transfer/TransferCoordinator` | Owns all mutable transfer state, a bounded `Channel`, and N workers. |
| `Transfer/TransferProgressAggregator` | Keeps the UI off the engine's back, and lets the UI timer stay stopped while idle. |
| `Storage/SqliteStateStore` | The upload ledger and hash cache. |
| `Infrastructure/ResourceGovernor` | Keeps the process out of the instrument's way. |

---

## 4. Decisions already made — please do not re-litigate

- **WPF**, `net8.0-windows`, MVVM, Windows only.
- **The Python version was never in production.** The user's words: brittle, not trustworthy. So
  there is no installed base, nothing it wrote matters, and **do not benchmark against it**.
  Migration of its settings is a convenience, not a requirement.
- **API keys preferred** over the account password. Both supported.
- **Velopack** for install and update, from GitHub Releases.
- **CalVer `YY.feature.patch`**, matching `skyline-prism`. Version lives only in
  `Directory.Build.props`; `release.yml` refuses to publish if the tag disagrees.
- **Folder acquisitions** (`.d`, Waters `.raw` directories) will become atomic transfer items.
- **Verification means the server's own MD5.** Nothing weaker may be reported as verified.

---

## 5. Verified server facts — do not re-discover these

All confirmed with real HTTP against panoramaweb.org (LabKey 26.7).

| Fact | Consequence |
|---|---|
| `?method=md5sum` returns a server-computed MD5, per file **or per whole collection** (flat, subdirectories omitted) | The basis of verification. One request per folder. |
| `?method=json` carries `canRead/canUpload/canEdit/canDelete/canRename` and an `options` verb list | The folder browser can refuse a read-only destination up front. |
| **`Content-Range` on PUT is not implemented** | No partial or resumable upload. One streaming PUT per file, any size. A retry restarts from zero. |
| MKCOL is **single-level**: 409 on a nested path, 405 when it already exists | Recursive creation, treating 405 as success. |
| PUT answers **201 for a replacement too**, not 204 | 201 must not be read as "created new". |
| `MOVE`, `DELETE`, `LOCK`/`UNLOCK` all allowed; `DAV: 1,2` | Atomic publish via a temporary name is possible. |
| `Expect: 100-continue` is honoured | A rejected credential surfaces before gigabytes are sent. |
| Basic only; **Digest never offered** | API key is `Basic base64("apikey:" + key)`. |
| No CSRF requirement with stateless Basic and `UseCookies = false` | Keeps the transport simple. |
| Both `@files` and `%40files` resolve | `@` is sent literally, matching LabKey's own hrefs. |
| Lab destination is `/_webdav/MacCoss/maccoss/@files/…` | `@files` hangs off the **`maccoss` container**, not the project root. |

### The semicolon problem

The servlet container strips path parameters **after** percent-decoding, so a remote name is
truncated at its first `;`:

```
PUT run;rep1.raw  (content A)  -> 201 Created, stored as "run"
PUT run;rep2.raw  (content B)  -> 201 Created, stored as "run", A destroyed
```

Both succeed. A later GET of the original URL truncates identically and returns 200, so nothing
surfaces the loss. Directory names behave the same. No client-side encoding avoids it: `%3B` is
decoded then truncated, `%253B` yields a literally different name. `PathSafety` refuses such a
name outright.

The existing MacCoss data was audited for this and is **clean**.

---

## 6. Traps, and things that cost real time to learn

Each of these is recorded in the code at the point it matters. Listed here so a new session does
not rediscover them the hard way.

### Correctness

- **A file with an open write handle reports a stale size.** Windows does not keep the directory
  entry current, so `FileInfo.Length` can be unchanged across samples while a file is actively
  being written — it looks perfectly settled. `FileStabilityTracker` reads the length from an
  **opened handle** instead. Over SMB the client's 10-second metadata cache makes this worse.
- **An exclusive-open probe alone is not enough either.** A writer that opens, appends and closes
  per block leaves gaps where nothing holds the file. Both signals are required.
- **A file is never released on first sighting.** One observation cannot distinguish a finished
  file from one between writes.
- **`Progress<T>` posts, it does not invoke.** A progress report can be delivered *after* the
  code that followed it. Since the aggregator is latest-wins, a late "uploading 100%" flipped a
  finished row back to in-progress and left it there. Use `InlineProgress<T>`. CI caught this;
  locally the ordering happened to work.
- **`SetStateAsync` is an UPDATE.** A conflict or failure on a never-before-seen file left no
  ledger row at all — the exact tracking gap the design exists to close. Always `SaveAsync` a row
  first.
- **Editing a file we uploaded is a new version, not a conflict.** If the ledger's recorded hash
  still matches what the server holds, nobody else touched it. Only an unaccountable remote copy
  is a real conflict. Tested both ways.

### Resource use on an instrument computer

- **`PROCESS_MODE_BACKGROUND_BEGIN` is actively harmful here.** It looks like exactly the right
  tool. Measured: idle CPU went from 0.31% of a core to **41%** — lowest memory priority makes
  Windows trim the working set aggressively (135 MB → 32 MB) and the process then spends its life
  faulting pages back in. Suits a short-lived indexer, not a process that sits quietly for weeks.
  Removed. Do not reintroduce it.
- **Trimming the working set once, without that mode, is the opposite:** settles at ~14–21 MB and
  stays there. Done via a one-shot delay, never a recurring timer.
- **A recurring timer that fires forever to check whether anything needs doing is the cost being
  eliminated.** The transfer grid's 5 Hz dispatcher timer ran unconditionally; it now starts only
  when the aggregator raises `WorkAppeared`.

Measured before → after: idle **0.31% → 0.026%** of one core, **135 MB → 21 MB**, 24 → 14 threads.
Transferring 128 MB costs 6.9% of one core. Those numbers are from a **32-core** machine; a
4-core instrument PC will show roughly 8× the whole-machine percentage.

### Build and tooling

- **WPF omits `System.IO` from implicit usings on purpose**, because `System.IO.Path` collides
  with `System.Windows.Shapes.Path`. Import it per file; adding it project-wide reintroduces the
  ambiguity.
- **`DISABLE_XAML_GENERATED_MAIN` does not work on its own.** The temporary markup-compile project
  WPF generates does not inherit `DefineConstants`, so the generated entry point returns and
  collides. `App.xaml` is demoted from `ApplicationDefinition` to `Page` instead.
- **`InvariantGlobalization` breaks WPF data binding** (`Cannot find non-neutral culture`).
- **`PublishSingleFile` would defeat delta updates**, and `PublishTrimmed` is unsupported for WPF.
  Publish to a folder; Velopack packs it.
- **CI must fetch the previous release before `vpk pack`**, or no delta is ever produced — a
  runner starts empty. Locally it works, which is why it went unnoticed. A delta is ~121 KB
  against a ~67 MB full package.
- **`GithubSource(prerelease: false)` silently gates delivery.** Channel decides *who* receives an
  update; the GitHub prerelease flag is presentation only. Left false, a release candidate on the
  stable channel is never offered.
- **A record with list members gets reference equality** from the compiler. `AppSettings` needed
  explicit structural equality or every "settings changed?" check reported a false difference.
- **ADO.NET rejects a CLR `null`**; use `DBNull.Value`. Making SHA-256 optional broke every upload
  at the point it recorded hashes.
- **Mapped drive letters belong to a single Windows sign-in.** `Y:` and `U:` were invisible from a
  process in another logon session. A monitored folder must be given as a **UNC path** or it will
  not resolve under a service or scheduled task.

### Test-infrastructure fidelity

Two of these mattered as much as product bugs:

- **A stub `HttpMessageHandler` that answers without reading the request body** leaves streaming
  and hashing wrappers untouched — an upload test passes having sent and hashed nothing. Drain the
  body like a real transport does.
- **A fake server holding files in a plain `Dictionary`** loses entries under four concurrent
  workers, which surfaces as a verification failure rather than the data race it is. A fake that
  cannot survive concurrency cannot test concurrency.

---

## 7. Next: Phase 4, continuous monitoring

The stability gate it depends on already exists and is tested. What is missing is the thing that
feeds it.

**Build it sweep-first.** The periodic directory sweep is the mechanism; `FileSystemWatcher` is a
pure accelerator that is allowed to fail silently. This is not defensive over-engineering — it is
what the SMB measurements showed: notifications are server-dependent, and the server tested
delivered **three events per new file**, so debouncing is required even when they do work.

Concretely:

1. `DirectoryMonitor` — `FileSystemWatcher` with `InternalBufferSize = 65536`, **subscribing to
   `Error`**: on `InternalBufferOverflowException`, log, recreate, and trigger a full sweep. The
   Python version had no `Error` handler at all and lost events silently under load.
2. `ReconciliationScanner` — every `ReconcileMinutes` (default 15) and at startup, enumerate with
   `RecurseSubdirectories`, `IgnoreInaccessible`, and `AttributesToSkip` including `ReparsePoint`
   (symlink loops), diffed against the ledger by `(path, size, mtime)`.
3. Feed both into `ReadinessGate`, then the coordinator. The gate already backs off to 30 s while
   nothing changes and resets the moment something moves.
4. Locked-file retry policy wired to the engine, using the settings that already exist.
5. **Re-measure idle CPU with monitoring running, and on a share.** Enumerating a network folder
   every 15 minutes is a different cost profile from a local disk, and idle cost is the one thing
   the user has been explicit about. Do not assume it carried over.

After that: dataset folders (`AcquisitionDetector`, atomic `.d` upload, collection-level
verification), the conflict dialog, `LegacyConfigImporter`, then signing and ship.

---

## 8. Open items and pending decisions

| Item | Notes |
|---|---|
| Default concurrency on a 4-core instrument PC | 3 today. If a transfer's ~1.7% of a 4-core machine is too much, drop to 1–2. Needs real hardware. |
| Code signing | Azure Trusted Signing (~$10/mo) preferred, then SignPath Foundation (free for OSS). Needs a decision and possibly UW paperwork. |
| Is `?method=md5sum` computed on demand or cached? | Determines whether verifying a 100 GB folder is seconds or minutes. Untested at scale. |
| Vendor completion sentinels | Bruker `analysis.tdf`, Agilent `AcqData\`, Waters `_HEADER.TXT` — validate against real acquisitions. |
| Concurrency ceiling panoramaweb tolerates | Start at 3; nothing has been pushed hard enough to find a limit. |
| Python removal | Nothing deleted yet. `publish-pypi.yml` is retriggered to manual only so a .NET release cannot publish the Python package. |
| SQLite connection-per-operation | Fine at current scale; a warm run of 38 files took ~1.4 s of fixed overhead. Worth revisiting before the reconciliation sweep touches 200k files. |

---

## 9. This machine

- **Settings** already point at `C:\Users\macco\Documents\test-panoramabridge-local` →
  `/_webdav/MacCoss/maccoss/@files/test-panoramabridge/`.
- **An API key is stored in Windows Credential Manager** under
  `PanoramaBridge:https://panoramaweb.org`, so **Test connection** and **Upload now** work without
  typing anything. Note that folder holds 0.9–7.3 GB `.raw` files already present on the server,
  so a run will hash them locally to compare.
- **A live SMB server** is reachable at `\\192.168.1.199\DataAnalysis` (mapped as `Y:` in the
  user's interactive session only — use the UNC path).
- Secrets are **never** committed. Integration and SMB suites read environment variables.

---

## 10. House style

Worth matching, because the existing code is consistent about it:

- Comments explain **why**, and especially why an obvious-looking alternative was rejected. The
  traps in §6 are all recorded at the point they matter.
- Failures carry a message written for a scientist looking at a stalled transfer, not a stack
  trace. `WebDavException.ToUserMessage()` is the pattern.
- Nothing reports "verified" unless the server's own hash was compared. `VerifyMethod` exists so
  the UI can say what was actually checked.
- Tests assert the **cost** of a decision, not only its result — that the fast path makes zero
  requests and does no hashing is what stops the tracking regressing.
- No emojis anywhere, per `CLAUDE.md`.
