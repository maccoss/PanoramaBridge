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
| 4 — Continuous monitoring | **Done, measured.** See §7 for what it is and §8 for what was left. |
| 5 — Datasets, conflict dialog, polish | Not started. See §9. |
| 6 — Code signing, ship | **Shipping as v26.1.0.** Code signing still outstanding; see §9. |

432 tests passing, 9 skipped unless the opt-in SMB suite is enabled, CI green, warnings as errors.
Coverage: **Core 87%, App 49%, pbctl 17%**, 69% over everything hand-written.

**What works today:** configure the two settings tabs and press **Start monitoring**. The folder
is watched, and each acquisition is transferred and verified once it has finished being written.
**Upload now** still does a single pass for anyone who would rather drive it by hand.

---

## 2. Running and verifying it

```bash
dotnet build PanoramaBridge.sln -c Release
dotnet test  PanoramaBridge.sln -c Release          # 432 tests, no network needed
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
pbctl watch     # monitor a directory until interrupted, then report what it cost the machine
pbctl status    # what the ledger holds
pbctl rm
```

`watch` is also the measuring instrument. It runs the same monitor the window runs with no XAML
in the way, so its processor time is monitoring's processor time and nothing else's, and it
prints that on the way out. To measure the idle cost of the mechanism alone, give it a filter
that matches nothing: the folder is still walked in full on every sweep, but nothing is offered
and the server is never contacted, so no credential is needed either.

```bash
pbctl watch <dir> /_webdav/unused/ --ext .no-such-extension --every 1
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
src/PanoramaBridge.Tests/    xUnit. net8.0-windows: see below
```

`Core` is deliberately UI-free, which is why the progress aggregator and the readiness gate live
there rather than in the view models: their behaviour is testable without a dispatcher.

The test project targets **`net8.0-windows` with `UseWPF`**, so it can reference the shell and
the harness and test them directly. That makes the suite Windows-only, which it already was in
practice -- `WindowsCredentialStore` and `NetworkPaths` both P/Invoke -- and CI runs
`windows-latest`. Note the consequence: `UseWPF` drops `System.IO` and `System.Net.Http` from the
implicit usings, so the test project puts them back explicitly. See §6.

### The parts that carry the design

| Type | Why it matters |
|---|---|
| `WebDav/RemotePath` | **The only thing allowed to build a URL.** In the Python version four call sites each joined URLs their own way and two disagreed, which silently broke verification. |
| `WebDav/PathSafety` | Refuses names the server would mangle; resolves a local file to its destination and asserts containment. |
| `WebDav/WebDavClient` | Recursive MKCOL, `?method=json`, `?method=md5sum`, streaming PUT with a stall watchdog, retry with jitter. |
| `Monitoring/FileStabilityTracker` | Decides when a file has stopped being written. **The highest-risk correctness property in the application.** |
| `Monitoring/ReadinessGate` | Puts that decision in the path. Nothing reaches the engine without passing through it — including **Upload now**, which used to go round it. |
| `Monitoring/ReconciliationScanner` | The periodic walk. The mechanism monitoring rests on, and the only thing that guarantees a file is found. |
| `Monitoring/DirectoryMonitor` | `FileSystemWatcher`, wrapped so it is allowed to fail. An accelerator, never the mechanism. |
| `Monitoring/ContinuousMonitor` | Puts those two together and feeds the gate. |
| `Monitoring/CandidateFilter` | Which files count as data. One filter, so the sweep and the watcher cannot disagree. |
| `Transfer/UploadDecisionService` | The three-tier "does this need uploading?" ladder. |
| `Transfer/ChecksumSidecar` | The `.md5` written beside every upload. The only record of a file's hash, and of the date it was acquired, that travels with the data rather than living in a database on one instrument PC. `md5sum -c` reads it unmodified. |
| `Transfer/TransferCoordinator` | Owns all mutable transfer state, a bounded `Channel`, and N workers. |
| `Transfer/TransferProgressAggregator` | Keeps the UI off the engine's back, and lets the UI timer stay stopped while idle. |
| `Storage/SqliteStateStore` | The upload ledger and hash cache. |
| `Infrastructure/ResourceGovernor` | Keeps the process out of the instrument's way. |
| `Infrastructure/NetworkPaths` | Turns a mapped drive letter back into the share it stands for, because the folder picker returns whatever was clicked and a drive letter belongs to one sign-in. |

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
| A collection hash is **computed on demand, not cached**: the folder holding this lab's test `.raw` files, about 19 GB, took **30 s** | Roughly 600 MB/s of server-side hashing. The first transfer into a folder full of large files therefore waits half a minute before anything moves, once per folder per session. A 100 GB folder would be two to three minutes. |
| `?method=json` carries `canRead/canUpload/canEdit/canDelete/canRename` and an `options` verb list | The folder browser can refuse a read-only destination up front. |
| **`Content-Range` on PUT is not implemented** | No partial or resumable upload. One streaming PUT per file, any size. A retry restarts from zero. |
| MKCOL is **single-level**: 409 on a nested path, 405 when it already exists | Recursive creation, treating 405 as success. |
| PUT answers **201 for a replacement too**, not 204 | 201 must not be read as "created new". |
| `MOVE`, `DELETE`, `LOCK`/`UNLOCK` all allowed; `DAV: 1,2` | Atomic publish via a temporary name is possible. |
| `Expect: 100-continue` is honoured | A rejected credential surfaces before gigabytes are sent. |
| Basic only; **Digest never offered** | API key is `Basic base64("apikey:" + key)`. |
| No CSRF requirement with stateless Basic and `UseCookies = false` | Keeps the transport simple. |
| **`X-LABKEY-Last-Modified` sets the stored file's modification time.** Epoch milliseconds, as a request header or a query parameter, honoured on PUT and on the multipart POST the file browser uses | This is how an acquisition keeps the date the instrument wrote it. Sent on every upload. Works on a replacement too, and does not disturb the hash. |
| Nothing standard does that job: `PROPPATCH` answers **405** unless the user agent looks like Windows Explorer, and `X-OC-Mtime` and a `Last-Modified` request header are both accepted with 201 and ignored | Measuring only the standard mechanisms produces a confident and wrong conclusion that the date cannot be preserved. It was reached here, and the evidence against it was a file in the Panorama UI showing its real acquisition date. When the server appears not to support something the web interface plainly does, read `DavController.java` -- LabKey Server is open source. |
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

- **A gate only guarantees anything if every path goes through it.** `ScanAndUploadAsync`
  enumerated the folder and queued what it found, so **Upload now** could send a partially
  written file — and during an acquisition is exactly when someone presses it. The gate had
  existed and been tested for weeks; nothing in it was wrong. Fixed by routing the manual scan
  through `PumpAsync` as well. Whenever a new way of discovering files is added, this is the
  question to ask about it first.

- **Not looking at a locked file for a long stretch is a latency bug, not a saving.** The design
  originally deferred a file another process held open for thirty minutes before looking again,
  which is what the setting on the Local Monitoring tab said to do. It was built, tested, and
  then failed the first time the real window ran: a file copied into the watched folder was put
  down mid-write and nothing brought it back, because **nothing announces that a handle has been
  closed**. There is no notification for it and the file does not change, so the only way to find
  out is to look. What the long wait bought was two file opens per thirty seconds.

  The setting was removed rather than reinterpreted — a setting that does nothing is exactly what
  this codebase criticises the Python version for. A file in use is now re-examined on the gate's
  ordinary backoff, up to `LockedFileRetryIntervalSeconds`.

  The general lesson is worth more than the specific fix: **a component that stops observing
  cannot be woken by something that produces no event.** Anything that skips work here has to be
  checked against what would restart it.

- **Giving up on a locked file must not mean forgetting it.** After `LockedFileMaxRetries`
  consecutive checks that all find the file in use, it stops being watched closely and goes back
  to the periodic sweep, which offers it again on its next pass. Read as "stop asking so often",
  never as "abandon" — plenty of acquisitions run longer than any close-watching budget.

- **A `Dispose` that is not idempotent takes the application down on the way out.** The shell is
  disposed twice on a normal exit -- once by the window as it closes, once by the service
  container, which owns it too -- and the second `CancellationTokenSource.Cancel()` threw. The
  exception escaped `Main`, so closing the application produced a dialog reading **"PanoramaBridge
  could not start."** It was reported from a real install; a `Stop-Process` in a test script never
  runs `OnClosed` and never reaches container disposal, so no amount of UI Automation had found
  it. `Dispose` is required to tolerate being called twice. Both this and `TransferService` now do.

  Also worth knowing: **a service that implements only `IAsyncDisposable` makes a synchronously
  disposed container throw** rather than skip it. `Main` returning disposes the container
  synchronously, so anything in the container needs `IDisposable` too.

- **Invalidating the destination snapshot after every upload does not scale.** It looks obviously
  right -- the folder changed, so drop what we knew about it -- and it made a batch of uploads
  quadratic in the size of the destination, because refetching includes a collection hash the
  server computes over every byte in the folder. A hundred files into a folder that is filling up
  meant a hundred passes over an ever-larger directory. Nothing about it is visible in a test
  against an empty destination, which is why the cost test seeds one first.
  `RemoteSnapshotCache.Record` folds the upload into what is already cached instead: the name,
  the length and the hash are all in hand by then, and the server has just confirmed the hash.

- **"The server does not support it" is a conclusion worth double-checking against the server's
  own web interface.** Four mechanisms for preserving a file's modification time were measured
  against panoramaweb.org, all four failed, and the conclusion drawn -- that Panorama cannot do
  it -- was wrong. LabKey has its own header, `X-LABKEY-Last-Modified`, which is what its file
  browser sends. The measurements were each individually correct; the inference from them was
  not, because a negative result over standard mechanisms says nothing about a proprietary one.
  The cheap check that would have caught it: **does the product's own UI do the thing?** If it
  does, the capability exists and only the mechanism is unknown, and `DavController.java` in
  `LabKey/platform` is public.

- **`Skip.If` under a plain `[Fact]` is a failing test, not a skipped one.** Xunit.SkippableFact
  works by throwing, and only `[SkippableFact]` catches that and reports a skip. The mistake is
  invisible on any machine where the condition is false -- a test guarded by "skip if there is no
  mapped network drive" passes on every developer machine in this lab, because they all have
  three, and fails on every build agent, because none do. It reached CI as part of the v26.1.0
  tag. Grep for `Skip.If` and check the attribute above each one.

- **`AddLogging` filters at Information before Serilog ever sees the event.** There was a
  `LoggingLevelSwitch` wired up for the "Verbose logging" toggle, and a toggle in the UI, and
  nothing that connected them — and even once connected, the Microsoft.Extensions.Logging
  pipeline dropped everything below Information first. So debug logging was unobtainable by any
  means. This cost an afternoon on the bug above: the monitor looked completely silent when it
  was in fact working exactly as instructed. `SetMinimumLevel(LogLevel.Trace)` hands filtering to
  Serilog's switch, which is the only thing the toggle can control.

- **A directory raises the same watcher events a file does.** A folder named `dataset.raw` passes
  an extension filter, and handing it to the gate means opening it, failing, and telling the user
  their file could not be read. Checked with `File.Exists` before anything is reported.

- **Instrument and copy software rename into place.** A file written under a working name and
  renamed on completion arrives as `Renamed`, not `Created`. Subscribing only to creations means
  every such file waits for the next sweep, which looks like monitoring being slow rather than
  broken.

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

- **Monitoring adds one recurring wait, and that is the whole budget.** The reconciliation
  interval is the only timer in it. The readiness gate blocks on an empty channel rather than
  polling, and the watcher's duplicate suppression is a comparison made when an event arrives
  rather than a window that has to be waited out. Both were built that way on purpose, and both
  are asserted in tests.

- **Enumerating a network folder is not like enumerating a local one, and the difference is
  three orders of magnitude.** Measured below. This is why the sweep filters against the ledger
  before anything reaches the gate, and why `ReconciliationScanner` walks with `DirectoryInfo`
  rather than `Directory.EnumerateFiles` — see the next entry.

- **`Directory.EnumerateFiles` costs a second stat per file.** It yields paths, so anything
  wanting the size has to open a `FileInfo` and ask, which over SMB is a second round trip per
  file. `DirectoryInfo.EnumerateFiles` yields `FileInfo` objects already populated from what the
  directory walk returned, so size and modification time are free. On a 35,000-file share that is
  the difference between one walk and two.

Measured before → after: idle **0.31% → 0.026%** of one core, **135 MB → 21 MB**, 24 → 14 threads.
Transferring 128 MB costs 6.9% of one core. Those numbers are from a **32-core** machine; a
4-core instrument PC will show roughly 8× the whole-machine percentage.

### Build and tooling

- **`UseWPF` drops `System.IO` and `System.Net.Http` from the implicit usings**, because
  `System.IO.Path` collides with `System.Windows.Shapes.Path`. That bites the moment a project
  gains `UseWPF` -- retargeting the test project broke fifteen files at once, all of them
  complaining about `HttpRequestMessage` and `Stream`. In `App` the right fix is a per-file
  import; in the test project, which draws nothing, they are simply put back project-wide.

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

## 7. Continuous monitoring, as built

Sweep-first, as planned. `ReconciliationScanner` walks the tree and is the only thing that
guarantees a file is found; `DirectoryMonitor` wraps `FileSystemWatcher` and is allowed to fail
silently. `ContinuousMonitor` runs both and feeds `ReadinessGate`, which feeds the engine.

```
DirectoryMonitor  ─┐
                   ├─ Channel<GateCandidate> ─ ReadinessGate ─ TransferCoordinator ─ Panorama
ReconciliationScanner ─┘
```

What each piece does, and the parts that are not obvious:

- **`ReconciliationScanner`** walks with `RecurseSubdirectories`, `IgnoreInaccessible` and
  `AttributesToSkip` including `ReparsePoint`, and drops anything the ledger already settles
  *before* it reaches the gate. That filter is what keeps the sweep affordable: without it, every
  file in the tree would be opened every quarter of an hour, on the disk an instrument is writing
  to. A folder it cannot read is reported, not thrown — an unmounted share is ordinary here, and
  the next sweep picks the folder up when it comes back.

- **`DirectoryMonitor`** subscribes to `Error`, and on `InternalBufferOverflowException` logs,
  rebuilds the watch and asks for a full sweep. It also subscribes to `Renamed`, and suppresses
  repeats of the same path within a second — the measured share sends three notifications per
  file. The suppression is a comparison made when an event arrives, not a window that has to be
  waited out, so it costs nothing while idle.

- **`ReadinessGate.WatchAsync`** is the continuous counterpart to `PumpAsync`. It blocks on an
  empty channel rather than polling — that is where an idle monitor sits — and it is the only one
  of the two that ever hands a file back, because a manual scan has someone waiting on it and
  stops at its own deadline instead.

- **`LockedFilePolicy`** carries the two settings under "Files held open by an instrument". Both
  of the things that are load-bearing about it are in §6: it never stops looking at a file in
  use, and handing one back to the sweep is not the same as abandoning it.

`ScanAndUploadAsync` was rebuilt on the same two pieces, which closed a real defect — see §6.

### What it costs

Measured with `pbctl watch` on this 32-core machine, over five minutes, sweeping **every minute**
— fifteen times more often than the default, so these are pessimistic:

| | Local folder, 32 files | SMB share, 35,551 files |
|---|---|---|
| One sweep | 0–2 ms | 16.2, 26.3, 37.5, 29.1 s |
| Processor | 0.078 s = **0.026% of one core** | 1.109 s = **0.37% of one core** |
| Working set | 38.5 → 39.3 MB | 51.2 → 61.4 MB |
| Threads | 15 | 15 |

The local figure is the same **0.026%** the application idled at *before* monitoring existed, so
the mechanism itself costs nothing measurable.

The share figure is the one to think about. Walking 35,000 files over SMB takes **tens of
seconds**, so at a one-minute interval it is sweeping about half the time — and even then it is
0.37% of one core, because almost all of that is spent waiting on the network rather than on the
processor. Per sweep it works out at roughly a quarter-second of processor time; at the default
fifteen-minute interval that is about **0.03% of one core**, which is to say back at idle.

Two things follow. The interval matters much more on a share than on a local disk, and nobody
should point this at the root of a file server — an instrument's output folder is the intended
target, and the Local Monitoring tab now says so.

These measurements deliberately exclude the ledger lookup, because a filter matching nothing was
used so that no credential was needed. That lookup is one indexed SQLite statement per five
hundred files, asserted in `ReconciliationScannerTests`.

Against the live share, the SMB suite also reports a sweep of a 25-file folder at 21 ms cold and
8 ms warm, and confirms that this server does deliver change notifications — three per file, as
before.

### What the real window does

Driven through UI Automation, watching a folder with the reconciliation interval turned down to
one minute so several sweeps fit in a run:

```
23:01:33  Noticed a change to ...\monitored-run.raw          <- the file starts arriving
23:01:34  Noticed a change to ...\monitored-run.raw          <- the repeat, one second later
23:01:46  monitored-run.raw has settled; handing it to the transfer engine
23:01:46  monitored-run.raw: Upload (decided at tier RemoteSnapshot) - Not present on the server
23:01:47  Uploaded .../monitor-check/monitored-run.raw (786432 bytes) in 0.2s
23:03:44  Swept ...: 1 file(s) examined, 0 offered, 1 already settled, in 3 ms
23:04:44  Swept ...: 1 file(s) examined, 0 offered, 1 already settled, in 0 ms
23:05:44  Swept ...: 1 file(s) examined, 0 offered, 1 already settled, in 0 ms
```

The last three lines are the steady state: the sweep keeps running, finds the file, recognises it
from the ledger, and asks the server nothing at all.

---

## 8. Next: Phase 5

1. **Dataset folders.** `AcquisitionDetector`, atomic `.d` and Waters `.raw` directory upload,
   collection-level verification. `CandidateFilter` explicitly does not handle these: the
   question is when a whole folder is complete, not whether one file inside it matches, so they
   become transfer items of their own rather than a filter rule.
2. **The conflict dialog.** The ledger already records `Conflict`, and the sweep deliberately
   leaves such a file alone until a person or a local change resolves it. Nothing yet asks.
3. **`LegacyConfigImporter`**, then signing and ship.

---

## 9. Open items and pending decisions

| Item | Notes |
|---|---|
| Default concurrency on a 4-core instrument PC | 3 today. If a transfer's ~1.7% of a 4-core machine is too much, drop to 1–2. Needs real hardware. |
| Code signing | Azure Trusted Signing (~$10/mo) preferred, then SignPath Foundation (free for OSS). Needs a decision and possibly UW paperwork. |
| Vendor completion sentinels | Bruker `analysis.tdf`, Agilent `AcqData\`, Waters `_HEADER.TXT` — validate against real acquisitions. |
| Concurrency ceiling panoramaweb tolerates | Start at 3; nothing has been pushed hard enough to find a limit. |
| Retiring the `panoramabridge` PyPI package | The source, the tests, the build scripts and `publish-pypi.yml` were deleted after v26.1.0 shipped; `v0.1.9rc4` is the last tag containing them. The PyPI project itself still lists 0.1.9rc4 as installable and has to be marked deprecated by hand on pypi.org -- that is the one remaining step, and it needs the project owner's account. |
| SQLite connection-per-operation | Fine at current scale; a warm run of 38 files took ~1.4 s of fixed overhead. The sweep no longer reads per file — `GetManyAsync` batches five hundred paths per statement — so the 200k case is much less alarming than it was, but it has still never been run. |
| Retrying a failed upload | The sweep re-offers a failed file until `MaxUploadAttempts`, five, and then leaves it until the file changes or someone asks. Deliberately not a user setting yet: nobody has hit the case in anger, and one more box on that tab needs to earn its place. |
| A sweep of a very large share | 35,000 files takes tens of seconds (§7). Nothing adapts the interval to how long the last sweep took, and perhaps it should. |
| The first transfer into a big destination folder stalls for half a minute | Now measured and understood (§5): the server hashes the whole collection on demand. It is one request per folder per session and the answer is cached afterwards, but the user sees a file sit at "Waiting" with no explanation. Worth saying so in the UI, and worth asking whether a per-file hash is cheaper for a large destination. |
| Monitoring while a manual scan runs | Refused rather than queued. **Upload now** turns into **Check now** while monitoring, which covers the case that actually comes up. |
| A live-server test suite | `CLAUDE.md` documented one gated on `PANORAMABRIDGE_IT_*` and it never existed; those variables are read only by `pbctl`. It matters because every fact in §5 was established by a throwaway program and nothing re-checks any of them. If LabKey changes `?method=md5sum`, the semicolon behaviour, or `X-LABKEY-Last-Modified`, this document quietly becomes wrong. |
| `pbctl` command bodies | 3.7% covered. The parsing is now extracted and fully tested; everything below it needs a server, which is the suite above. |
| Watching more than one folder | One monitored directory, as before. The engine and the monitor are both per-folder objects, so a second one is not structurally hard — but the settings screen, the ledger's meaning of "the base directory", and the transfer list all assume one. |

---

## 10. This machine

- **Settings** already point at `C:\Users\macco\Documents\test-panoramabridge-local` →
  `/_webdav/MacCoss/maccoss/@files/test-panoramabridge/`.
- **An API key is stored in Windows Credential Manager** under
  `PanoramaBridge:https://panoramaweb.org`, so **Test connection**, **Upload now** and **Start
  monitoring** work without typing anything. Note that folder holds 32 files, 52 GB of them
  0.9–7.3 GB `.raw` files already present on the server, so a first run hashes them locally to
  compare. The ledger (`%LOCALAPPDATA%\PanoramaBridge\state.db`) is currently absent, so that
  first run has not happened yet.
- **A live SMB server** is reachable at `\\192.168.1.199\DataAnalysis` (mapped as `Y:` in the
  user's interactive session only — use the UNC path). It holds about 35,500 files, it does
  deliver change notifications, and it sends three per new file.
- The SMB suite runs against `\\192.168.1.199\DataAnalysis\panoramabridge-scratch`; set
  `PANORAMABRIDGE_SMB_PATH` to it. Each test creates and removes its own subfolder.
- Secrets are **never** committed. Integration and SMB suites read environment variables.

---

## 11. House style

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
