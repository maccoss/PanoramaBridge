# Multiple configurations

Running several local-folder-to-Panorama pairings at once, each startable and stoppable on its
own, the way AutoQC Loader does it.

Asked for by the lab in the group meeting of 2026-09-11. This page is the design, the decisions
already taken, and the order the work has to happen in. It is written before the code because two
of the steps migrate data that cannot be regenerated if they go wrong.

---

## What a configuration is

A **name**, plus everything on the Local Monitoring and Remote Settings tabs:

| | |
|---|---|
| Local | monitored folder, subdirectories, file types, never-transfer list, readiness timings, conflict policy |
| Remote | server, sign-in, destination path, verification, checksum sidecars |

Each one can be enabled or disabled independently and reports its own status. Several run at once.

**Not** part of a configuration: anything about the application itself — tray behavior, verbose
logging, and the transfer concurrency limit, which is a property of the disk and the network
rather than of any one pairing. Those stay global.

**Transfer Status and Uploads stay as they are** — one view across everything, per the request.
They gain a column saying which configuration a row belongs to; they do not split into tabs.

---

## Decisions already made — please do not re-litigate

Both were put to the lab and answered on 2026-09-11.

### Overlapping local folders are allowed

Two enabled configurations may watch the same folder and send it to different destinations. The
motivating case is one acquisition folder going to two Panorama projects.

This is the expensive answer. The alternative — refusing to enable a configuration whose local
tree intersects an enabled one — needs no migration at all, and was rejected because it forbids
something a lab plausibly wants in order to save work here.

### Configurations may use different servers and accounts

Each carries its own server URL, auth mode and credential. The motivating cases are a second
LabKey instance, and two projects owned by different accounts on `panoramaweb.org`.

---

## Why this is not a UI change

Everything below `TransferService` already takes an `AppSettings` and is therefore already
per-configuration: `MonitorOptions.FromSettings`, `NewCoordinator`, `ContinuousMonitor`. The
single-configuration assumption lives in three places, and two of them hold data.

> Written before any of the work. The names have moved since -- `FromSettings` is
> `FromConfiguration`, `NewCoordinator` is `ConfigurationRunner` -- and the phase sections below
> record what each step actually did. This part is left as it was because it is the reasoning
> that set the order, and that reasoning held.

### 1. The upload ledger is keyed by local path alone

```sql
CREATE TABLE uploads (local_path TEXT PRIMARY KEY COLLATE NOCASE, ...)
```

One row per local file. `UploadRecord.IsSettledAt(stamp, destination)` deliberately returns false
when the row's recorded destination is not the one being asked about — that is what makes
re-pointing the remote path re-send everything, and it is correct.

Together with a single-column key, two configurations sharing a folder produce this:

1. A uploads `run.raw`, writes the row with destination A
2. B's sweep reads the row, sees the wrong destination, re-uploads, **overwrites** it with B
3. A's next sweep sees destination B, re-uploads, overwrites
4. …every sweep, forever, to both destinations

Silent, unbounded, and precisely the cost the ledger exists to prevent. **The key must widen to
`(local_path, remote_path)`.**

### 2. Credentials are keyed by server URL alone

`WindowsCredentialStore.TargetFor(serverUrl)` — so two configurations on one server with different
accounts overwrite each other's stored secret. The key must include the account.

### 3. `TransferService` holds one of everything

One `_monitor`, one `_monitorEngine`, one `_http`/`_client`/`_connectedTo`. It becomes a
coordinator over a set of per-configuration runners, each owning its own connection because
configurations may address different servers.

---

## Order of work

Data first, because the migrations are the only irreversible part and everything else depends on
the shapes they settle.

### Phase 1 — widen the ledger key  **[done: cbb9044]**

`(local_path, remote_path)` as the primary key. `IStateStore` gains the destination wherever it
currently takes only a path.

**SQLite cannot alter a primary key.** This is not `ALTER TABLE ADD COLUMN` like every migration
before it — the table has to be rebuilt: create the new shape, copy, drop, rename, inside one
transaction. The database being rebuilt is the record of what has already been uploaded. If it is
lost, every instrument re-uploads everything; if it is silently half-migrated, some files are
reported verified that are not.

Required before this ships:

- the whole rebuild in one transaction, so a crash mid-migration leaves the old table intact
- a test that kills the process mid-migration and reopens
- a test that a ledger written by 26.8.1 opens, migrates, and still answers "settled" for rows
  that were settled before — no re-upload after an update
- a test that two destinations for one local path now coexist

### Phase 2 — key credentials by server *and* account  **[done]**

Smaller, and it turned out to need no migration at all.

The obvious discriminator is the user name, and it does not work: with an API key -- the
recommended mode -- there is no user name, only the key, so two API-key configurations on one
server would still overwrite each other. The key is therefore an **account**, which phase 3 fills
with the configuration's identity.

An empty account resolves to *exactly* the target name used before accounts existed. That is the
whole migration: every credential already stored is found where it has always been, a rolled-back
build still finds it because nothing moved, and configuration one can keep the empty account and
inherit the credential the machine already has.

### Carried forward from phase 1

Two things the ledger work surfaced that later phases have to deal with:

- **A failure recorded before a destination is known uses an empty destination.** That is a real
  key, not an argument error, and `SafeSetStateAsync` marks every row a file has -- all three
  routes there are files nothing can transfer, so the failure is true for every destination.
- **A case-only rename left a stale row.** Local paths collapse case-insensitively but remote
  paths compare exactly, so `run.raw` to `RUN.raw` produced two rows. Nothing ever re-uploaded;
  the sweep finds the row for the destination it would use now. The Uploads table would have
  shown one file twice, so it is cleared on save -- see phase 5.

### Phase 3 — split settings into application and configurations  **[done]**

`AppSettings` is now application-level settings plus an ordered list of `MonitoringConfiguration`.
A configuration gained a name, an enabled flag, a created timestamp and an account; it kept every
per-pairing field, with the reasoning on each one moved across intact.

`$version` went to 2. A version 1 file — which is every file in the field — is read through a
private `LegacySettings` view of the old flat shape and becomes configuration one: named for the
folder it watches, enabled, with an empty account so it inherits the credential the machine
already has. It is rewritten once, on load.

Nothing here can lose data the way phase 1 could. The old file is rewritten only once the new
shape exists, through a temporary file, so a crash leaves a version 1 file that the build which
wrote it still reads. The risk is silence rather than loss: the properties that moved are simply
gone from `AppSettings`, so a version 1 file read as the current shape does not fail — it loads
with the monitored folder, the destination and the sign-in missing, and the first anyone knows of
it is that an instrument stopped transferring. The tests therefore assert the upgraded settings
property by property, from a settings file written out as text, rather than round-tripping a
record against itself.

Two things this settled that the plan did not say:

- **An absent property and a `null` one are different answers.** Absent means the file predates
  the setting, so the defaults apply; `null` is a mistake in a hand-edited file that the settings
  screen is meant to report. Deserializing gives `null` for both, so presence is read from the
  text instead. Conflating them would have refilled an emptied never-transfer list with the
  defaults and re-armed collecting the `.skyd` caches somebody had deliberately allowed.
- **A file written by a newer build is left alone.** This build cannot represent what it does not
  know about, so stamping its own `$version` onto the part it understood would hand the newer
  build back a file quietly missing settings. A rollback now reads what it can and writes nothing.

**Which settings went where.** The division is by what a value describes. A watched folder, a
destination and a sign-in describe one pairing. The concurrency limit, the yield to instrument
software, the SHA-256 record, the extra root certificate, the recent-destinations list and the
tray behavior describe this computer — a TLS-inspecting proxy intercepts everything leaving the
machine, and the disk is as slow for one configuration as for five — so those stayed on
`AppSettings`.

`TrustedRootCertificatePath` is the one this page implied differently. Its control sits on the
per-configuration Remote Settings tab today, so phase 5 has to move it when that tab splits up.

**What still assumes a single configuration.** Named rather than hidden, because removing them is
the work of phase 4: `TransferService.ActiveConfiguration`, `MainViewModel.ActiveConfiguration`,
and the configuration index `SettingsViewModel` takes as a constructor parameter. Each takes the
first enabled configuration and says in its remarks which phase replaces it.

### Phase 4 — `TransferService` runs a set  **[done]**

`ConfigurationRunner` is one enabled configuration's connection, engine and monitor.
`TransferService` owns a set of them and is otherwise unchanged from the outside: the same
`IsMonitoring`, the same events, the same buttons. Each runner has its own WebDAV client, because
a client carries exactly one credential and configurations may sign in to one server as different
people.

Both of the things the plan called implicit are now explicit, and one more turned up.

**Concurrency is shared.** `TransferBudget` is a permit taken as each file starts and given back
when it finishes, held in common by every engine. Shared rather than divided: three
configurations against a limit of three would otherwise get one slot each, so the one with a
hundred files waiting would use a third of the link while the other two sat idle. The test is a
cost assertion — two engines, twelve files each, a budget of two, and the high-water mark of
overlapping uploads has to be exactly two. Bypassing the budget makes it fail, which was checked
rather than assumed.

**Idle cost does not multiply.** Measured at one, three and eight configurations; the numbers and
what they mean are in §7 of [the handoff](DOTNET_PORT_HANDOFF.md). The short version: cost tracks
how many files are swept, not how many configurations there are, per-folder cost is flat at about
0.3% of one core at a one-minute interval, and at the default fifteen minutes eight
configurations watching four thousand files come to 0.17%. Memory grows by roughly 1.7 MB per
configuration. The feature clears the bar it was given.

**One password box, several servers.** Not in the plan and nearly a real defect. The typed secret
was being handed to every configuration, and because a secret typed this session deliberately
takes precedence over a stored one, a configuration on another server would have ignored the
credential it had of its own and signed in with somebody else's key. It now reaches only
configurations that resolve to the same credential slot — the same server as the same account —
which still covers the ordinary case of two folders on one instrument going to two projects on
one Panorama, since those genuinely share a credential.

Two smaller decisions worth recording:

- **Starting is all or nothing.** A configuration that will not start takes the attempt down and
  the ones already started are stopped again. Starting four of five and reporting success would
  leave the window saying it was monitoring while one instrument quietly filled its disk, and
  with several configurations nobody can see at a glance that one folder is uncovered.
- **The status line is composed, not overwritten.** `MonitoringSummary` in `Core` turns the last
  sweep from each configuration into one line. Setting it from whichever swept most recently
  meant a folder that could not be read was announced and then cleared a second later by a folder
  that was fine — a broken share flickering past and gone.

`pbctl watch` grew what the measurement needed: `--also <dir>` to watch another folder alongside,
`--no-upload` to walk and report without contacting a server (and so without a credential), and
`--for MINUTES` to stop and report on its own. CLAUDE.md had promised the no-credential run was
available through a filter matching nothing; it was not, and that paragraph now describes what
actually works.

### Phase 5 — the Configurations tab  **[done]**

The list, with the columns AutoQC Loader shows: name, user, created, status, a tick to enable,
and Add, Copy and Remove. Selecting a row points the Local Monitoring and Remote Settings tabs at
that configuration, so those two are the editor rather than a second place configurations live —
which is why the tab layout grew by two rather than by one.

**The Application tab is the other new one, and it is not decoration.** Local Monitoring and
Remote Settings carried settings that describe this computer: the concurrency slider, the tray
and verbose-logging options, the additional root certificate. Once those tabs edit *a*
configuration, a machine-wide setting shown beside them reads as belonging to it — somebody
turning off verbose logging for one instrument would reasonably believe the others were
untouched. A screen that says that is a screen that lies, so they moved. `SettingsBindingTests`
now states the rule rather than where things sit: no property of `AppSettings` may be bound on a
per-configuration tab. The single exception is `RecentRemotePaths`, and it is an exception
because it is not edited anywhere — it is the drop-down beside a configuration's own destination.

**The configuration column is derived, not stored.** `ConfigurationLookup` matches a row's local
path and destination against the configurations. Adding a column to the ledger would mean a
schema migration to store something already derivable, and it would go stale the moment somebody
renamed a configuration. Both halves have to match, because two configurations may watch one
folder. Transfer Status does not use the lookup: `ConfigurationRunner` tags each progress report
as it leaves, because it knows, and a lookup there would be guessing at what it already knew.

Blank in that column is ordinary rather than a failure: the ledger outlives the configuration
that wrote a row, and a file transferred by one since deleted is still a true record of an
upload.

**Switching configuration saves first.** Discarding half-typed edits on a click elsewhere is the
worst of the options — the boxes are simply different when you come back and nothing says why.

**A copy gets a new credential slot**, unlike the configuration it came from. Two configurations
sharing one slot is fine and common, but a copy is a new pairing, and its own slot is what lets
it be signed in as somebody else later without signing the original out.

**Status is whether it would run, not whether it is running.** The Transfer Status tab is where
what is happening now belongs. Saying "Running" here for a configuration whose share had just
gone would be the kind of tick that means less than it appears to.

`StartupTests` builds the real service container and resolves the window. Every registration
compiles whatever it resolves to, so a missing one or a cycle between two view models is a clean
build and an application that will not open — and adding a tab is exactly the change that invites
that.

**The stale row carried forward from phase 1 is cleared on save.** The condition is deliberately
narrow: the same local file, a destination that differs from the one being written exactly yet
matches it ignoring case. A genuinely different destination fails that second test and is kept,
which is what makes two configurations sharing a folder possible at all. `CaseOnlyRenameTests`
pins both halves.

---

## What could make this not worth doing

Written down so it is a decision rather than a discovery:

- If the idle cost measured in phase 4 is not near zero per configuration, the feature conflicts
  with the property that matters most on an instrument computer, and the answer is fewer
  configurations sharing one watcher rather than shipping it anyway.
- The phase 1 migration is the only step that can lose data. If it cannot be made crash-safe with
  a test that proves it, the overlapping-folders decision should be revisited rather than the
  migration shipped on hope.
