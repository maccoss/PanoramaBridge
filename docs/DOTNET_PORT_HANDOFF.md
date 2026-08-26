# PanoramaBridge .NET port — handoff

State of the work as of 2026-08-24. Read this before touching `src/`.

The port is finished and shipped. **`main` is the .NET application**, released through
**v26.3.0**; the Python package was removed after v26.1.0 and survives only in git history under
the `v0.1.9rc4` tag. The `dotnet-port` branch and PR #2 are closed history, not somewhere to
look. This page is no longer a port plan — it is the standing record of what is built, what was
decided, and what cost real time to learn.

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
| 5 — Datasets | **Done, shipped in v26.3.0.** Directory acquisitions and Sciex companions. See §7a. |
| 5 — Conflict dialog, polish | Not started. See §9. |
| 6 — Code signing, ship | **Shipped, through v26.3.0.** Code signing still outstanding; see §9. |

534 tests in the main suite, 9 of them skipped unless the opt-in SMB suite is enabled, plus 32 in
`ThermoRaw.Tests`. CI green, warnings as errors.

> Coverage was **Core 87%, App 49%, pbctl 17%** when it was last measured, at v26.1.0. Three
> feature releases have landed since and nothing has re-run it, so treat those as historical
> rather than current.

**What works today:** configure the two settings tabs and press **Start monitoring**. The folder
is watched, and each acquisition is transferred and verified once it has finished being written.
**Upload now** still does a single pass for anyone who would rather drive it by hand.

That now covers three shapes of acquisition: a single file (Thermo `.raw`), a directory packed
into one archive (Bruker and Agilent `.d`, Waters `.raw` directories), and a file that travels
with companions (Sciex `.wiff` with its `.wiff.scan`). See §7a.

---

## 2. Running and verifying it

```bash
dotnet build PanoramaBridge.sln -c Release
dotnet test  PanoramaBridge.sln -c Release          # 566 tests, no network needed
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
src/PanoramaBridge.ThermoRaw/       Thermo RAW truncation check. net8.0, no dependencies
src/PanoramaBridge.ThermoRawCheck/  thermoraw-check, standalone and trimmed
src/PanoramaBridge.ThermoRaw.Tests/ net8.0, so CI runs it on Linux too
```

`ThermoRaw` sits outside `Core` deliberately: it is useful without PanoramaBridge, it must build
on Linux, and it ships as its own binary with every release. Keep it dependency-free, `Core`
included.

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
| `Monitoring/CandidateFilter` | Which files count as data. One filter, so the sweep and the watcher cannot disagree. Also walks trailing extensions, so `.wiff` brings `.wiff.scan`. |
| `Monitoring/DatasetFolder` | Recognises a directory acquisition, measures it, and names its archive. |
| `Monitoring/DatasetStabilityTracker` | The folder counterpart of the stability tracker. Three signals must settle together, and an empty folder is never ready. |
| `Transfer/DatasetArchive` | Packs a directory acquisition into one stored-not-compressed archive, beside the acquisition, checking free space first. |
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
- **No settings importer, and none is planned.** This followed from the line above and took a
  while to be said out loud. `LegacyConfigImporter` sat in the next-steps list from the start of
  the port; nobody ever ran the Python application in earnest, so there is no `config.json` on any
  instrument to read, and an importer for a file that does not exist anywhere is work with no
  user at the end of it. Anyone who did try the Python version retypes a server URL and a folder
  once. Removed from the plan rather than left to look pending.
- **API keys preferred** over the account password. Both supported.
- **Velopack** for install and update, from GitHub Releases.
- **CalVer `YY.feature.patch`**, matching `skyline-prism`. Version lives only in
  `Directory.Build.props`; `release.yml` refuses to publish if the tag disagrees.
- **Folder acquisitions** (`.d`, Waters `.raw` directories) are atomic transfer items: one
  directory becomes one archive, uploaded as one object. Done in v26.3.0 — see §7a.
- **Verification means the server's own MD5.** Nothing weaker may be reported as verified.

### The Python package is retired, and how

Done, not pending. All four PyPI releases -- `0.1.7rc1`, `0.1.9rc1`, `0.1.9rc3`, `0.1.9rc4` -- are
**yanked**, with the reason `Retired. Replaced by the .NET application:
https://github.com/maccoss/PanoramaBridge/releases`, and the project is **archived**.

Yanked as well as archived because archiving is a banner on a web page and `pip install` never
shows it. Yanking is what makes `pip install panoramabridge` fail rather than quietly install a
retired application that uploads data, while an exact pin still resolves for anyone who has one.
Both are reversible from the project's PyPI pages.

Two things to know if this ever comes up again:

- **Every release on PyPI was a pre-release.** There was no stable version, despite a `v0.1.8`
  git tag -- it was never published. With no stable release available pip falls back to the
  newest pre-release, so `pip install panoramabridge` did resolve, to `0.1.9rc4`.
- **No final deprecation release was published**, though an earlier plan called for one. Its
  purpose was to tell strangers where the project went, and there were none: the only two users
  were the project owner and one colleague. Publishing one would have meant restoring the
  packaging files from the tag and building, and archiving blocks uploads afterwards, so it would
  have had to come first.

The source itself remains fetchable from `v0.1.9rc4`.

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
  quadratic in the size of the destination, because refetching discards the folder's hashes too,
  and the next comparison makes the server recompute them over every byte in the folder. A hundred files into a folder that is filling up
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
  an extension filter, and `DirectoryMonitor` still checks `File.Exists` before reporting
  anything, because a watcher event carries no way to tell which it was.

  What changed in v26.3.0 is that a directory is no longer always wrong. The **sweep** offers one
  deliberately when its extension was asked for, and `FileStabilityTracker.Check` routes anything
  that is a directory to the dataset tracker. So the rule is not "directories are a mistake" but
  "a directory is an acquisition only when the sweep says so"; the watcher path still has no way
  to know, and still declines.

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

- **`UseWindowsForms` on a WPF project is the same trap from the other side.** Adding it for the
  tray icon puts `System.Windows.Forms` into the implicit usings, and `Application`,
  `UserControl` and `MessageBox` immediately become ambiguous: five errors in five files, none of
  which want WinForms. The fix is `<Using Remove="System.Windows.Forms" />` (and `System.Drawing`
  alongside it, whose `Point`, `Color` and `Size` collide the same way), then naming the two
  WinForms types in full in the one file that uses them. Reach for `Shell_NotifyIcon` interop
  instead only if you are prepared to handle the `TaskbarCreated` message yourself -- Explorer
  restarting destroys every tray icon, and NotifyIcon is what puts ours back.

- **An XML comment cannot contain `--`.** Writing the note above into the `.csproj` failed the
  build with `MSB4025` before it failed anything else. House style uses `--` as a dash in prose,
  which makes this easy to hit in exactly the files where a comment is most needed.
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

## 7a. Directory acquisitions and companions, as built

Shipped in v26.3.0. Two problems, one release.

**A directory acquisition is one object.** A `.d` is a directory locally and a single `.d.zip`
remotely, which is an observation rather than a preference: Panorama Public stores every Bruker,
Waters and Agilent acquisition as `<folder name>.zip`. One object is what makes the transfer
atomic without any machinery for atomicity — it either arrives and verifies against the server's
own MD5, or it does not — so verification, the sidecar, conflict handling and the ledger all
apply unchanged.

- `DatasetFolder` recognises one by the same extension list that governs files, and being a
  directory is what separates a Waters `.raw` folder from a Thermo `.raw` file.
- `DatasetStabilityTracker` decides it has finished: nothing inside open for writing, **and**
  size, file count and newest timestamp all unchanged. Three numbers rather than one, because
  Bruker closes the files in a `.d` at different moments, so a total can hold still while a file
  is added. An empty `.d` is never ready.
- `DatasetArchive` packs it, stored not compressed, beside the acquisition under a `~` name that
  `CandidateFilter` already rejects — otherwise every pack would hand the sweep a six-gigabyte
  candidate. Free space is checked with headroom first, and a failed or cancelled pack removes
  its partial file.
- **The sweep no longer descends** into an acquisition. That was the actual mechanism behind
  "a folder still being written can transfer partially": a recursive walk offered the files
  inside individually.

**Companions travel with the acquisition.** `Path.GetExtension("run.wiff.scan")` is `.scan`, so a
filter of `.wiff` matched 38 MB of metadata and left 8.2 GB of spectra behind — and recorded it
verified, correctly as far as it went. `CandidateFilter` now strips trailing extensions one at a
time, so `.wiff` reaches `.wiff.scan`. Excluded from that walk: SQLite's `-journal`, `-wal` and
`-shm`, and our own `.md5` sidecar, which would otherwise reach `run.raw` from `run.raw.md5`.

What is **not** established: the completion decision, for any vendor. Real acquisitions were
downloaded from Panorama Public and run through, which settles recognising, packing, naming and
verifying — but a downloaded folder arrives finished, so nothing about it exercises the decision
to wait. See [`VENDOR_FORMATS.md`](VENDOR_FORMATS.md), which draws that line per vendor and names
sending-early as the one quiet failure mode.

---

## 8. Next

1. **The conflict dialog.** The ledger already records `Conflict`, and the sweep deliberately
   leaves such a file alone until a person or a local change resolves it. Nothing yet asks.
2. **Code signing**, still outstanding since v26.1.0. See §9.
3. **Nothing outstanding in the dataset path.** The eight defects a review turned up after
   v26.3.0 are all fixed: the two that mattered -- a walk failure taking monitoring down with it,
   and a leaked tracker sample that could call a folder ready on one look -- and six smaller ones.
   They are in the git history rather than here, now that none of them is pending.

---

## 9a. Known defects in conflict handling and directory acquisitions

Found by nine review passes over v26.4.0—v26.4.6 and verified against the code, but **not
fixed**. A branch that attempted them was abandoned: each round of fixes introduced roughly as many
defects as it removed, several worse than the originals, so the work was stopped rather than
continued. Read this before changing anything in `ProcessDatasetAsync`, `ApplyResolutionAsync` or
`UploadsViewModel`.

None of these loses data silently — the paths that did were closed in v26.4.5 and v26.4.6. What
remains is invisibility, needless work, and decisions that go missing.

| Defect | Mechanism |
|---|---|
| A first-time acquisition has no ledger row while it transfers | `ProcessDatasetAsync` packs and uploads without ever calling `SaveAsync`, and `SetStateAsync` is `UPDATE`-only so its `Uploading` write matches nothing. A new `.d` is absent from the Uploads tab for the whole of a multi-hour transfer, `Attempts` never increments, and `RecoverInterruptedAsync` cannot recover it because `GetInterruptedAsync` finds no row. The file path saves a `Queued` row first; this one does not. |
| An interrupted acquisition folder is written off | `RecoverInterruptedAsync` asks `File.Exists`, false for a directory, and marks the row failed with a message about a local file. Only reachable once a row exists, so it compounds the entry above. |
| Startup can block when verification is off | `GetInterruptedAsync` returns `Uploading`, `Uploaded` and `Queued` unbounded. With `VerifyUploads` off a row stays `Uploaded` for ever, and recovery enqueues into a bounded channel (5000, wait-when-full) before any worker runs. Past that many rows `StartMonitoringAsync` never returns; below it, every file ever uploaded is re-offered on each launch. |
| `ConflictPolicy.Rename` does nothing for acquisition folders | The dataset conflict switch has no `Rename` arm, so folders are held instead. The same defect was fixed for files in v26.4.0 and never fixed here. |
| An acquisition skipped by policy is not recorded | The skip reports progress but writes no row, so the folder is invisible in the audit view and re-examined every sweep — another listing and another collection hash, which Panorama computes by reading every byte in the destination. |
| A rename decision on a folder is applied without re-checking the name | The occupied check is skipped entirely while a decision is pending, so a name that was free when offered can be taken by the time the bytes move. The file path re-checks; this one does not. |
| The sweep and the engine disagree after a case-only rename | The ledger is `NOCASE`, so a row keeps its original spelling. The sweep resolves from the row's stored path and the engine from the path on disk, so after a case change the two never match and the file is offered on every pass for ever. |
| `IsDataset` is never cleared | Nothing writes it back to false, so a `.d` folder later replaced by a plain `.d` file still resolves to the archive name. |
| A decision made while the ladder is running is discarded | The row is read before `DecideAsync`, which can spend a listing and a collection hash, and every save writes the whole row including the resolution column — so a decision made in that window is overwritten with `None` and the person is re-prompted. |
| A failed attempt spends the decision | The resolution is cleared before the attempt, so a full disk while packing or a timeout while uploading loses it. **Restoring it naively makes things worse**: pack failures never increment `Attempts` (that happens only on the `Uploading` transition, which this path never writes), so a restored decision turns a loop that self-terminated at `Conflict` into an unbounded repack of the whole acquisition on every sweep. A correct fix needs an attempt bound *and* a re-check before the restored decision is acted on. Tried, reverted. |
| Open containing folder does nothing for an acquisition | `TransferStatusViewModel.OpenContainingFolder` asks `File.Exists`, false for a directory, and returns silently. `Path.Exists` is the one-call form both this and recovery want. |

### Why this is written down rather than fixed

Nine review rounds produced about seventy-five findings. From the second round on, each round found
that the previous round's fixes had introduced new defects — four times the new one was worse than
the original, including a UI that reported a refused acquisition as "Already on the server", and a
decision-restore that replaced a terminating loop with an unbounded one.

Several fixes shipped with comments asserting the opposite of what the code did, and at least eight
tests were written that could not fail, one of them validating a release note that was therefore
untrue. The defence that worked was reverting each fix and watching its test go red; anything less
passed things that did not work.

The useful advice is not about any single row above. Two roots account for most of the list, and
both are worth checking for before editing here: **a decision duplicated rather than reused** (the
destination was derived at six call sites, then the ladder was re-implemented for folders), and
**"I cannot look" treated as "there is nothing there"** (fixed on the server side in v26.4.6, still
present on the local side).

---

## 9. Open items and pending decisions

| Item | Notes |
|---|---|
| Default concurrency on a 4-core instrument PC | 3 today. If a transfer's ~1.7% of a 4-core machine is too much, drop to 1–2. Needs real hardware. |
| Code signing | Azure Trusted Signing (~$10/mo) preferred, then SignPath Foundation (free for OSS). Needs a decision and possibly UW paperwork. |
| Vendor completion sentinels | **Not the approach taken.** Completion is decided by three signals settling together (§7a) rather than by looking for a per-vendor sentinel file, because a sentinel list is wrong the moment a vendor changes one and there is nobody here to notice. Still worth validating against a live instrument write, which is the one thing the real data could not settle. |
| Concurrency ceiling panoramaweb tolerates | Start at 3; nothing has been pushed hard enough to find a limit. |
| SQLite connection-per-operation | Fine at current scale; a warm run of 38 files took ~1.4 s of fixed overhead. The sweep no longer reads per file — `GetManyAsync` batches five hundred paths per statement — so the 200k case is much less alarming than it was, but it has still never been run. |
| Retrying a failed upload | The sweep re-offers a failed file until `MaxUploadAttempts`, five, and then leaves it until the file changes or someone asks. Deliberately not a user setting yet: nobody has hit the case in anger, and one more box on that tab needs to earn its place. |
| A sweep of a very large share | 35,000 files takes tens of seconds (§7). Nothing adapts the interval to how long the last sweep took, and perhaps it should. |
| The first transfer into a big destination folder stalls | **Fixed and released in v26.2.1.** The collection hash was fetched alongside the listing, for every folder, before anything knew whether it would be read -- and it is read only when a destination name matches, which for new work is never. So a new acquisition into a populated folder made Panorama hash every byte in it, at roughly 600 MB/s, to answer a question the listing had already answered: 300 GB was minutes of "Checking server" before the first file moved. It is now fetched on demand, still once per folder so a batch that genuinely needs hashes pays one request. |
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
