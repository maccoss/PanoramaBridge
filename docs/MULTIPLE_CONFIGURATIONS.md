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
- **A case-only rename now leaves a stale row.** Local paths still collapse case-insensitively
  but remote paths compare exactly, so `run.raw` to `RUN.raw` produces two rows. Nothing
  re-uploads; the sweep finds the row for the destination it would use now. The Uploads table
  would show two entries for one file, so this wants clearing on save before phase 5 puts that
  table in front of anyone.

### Phase 3 — split settings into application and configurations

`AppSettings` becomes application-level settings plus an ordered list of named configurations.
The settings file already carries a `$version`, so the migration has somewhere to hook: today's
single configuration becomes the first entry, named for the folder it watches.

A configuration needs, beyond today's fields: a name, an enabled flag, and a created timestamp —
the columns AutoQC shows.

### Phase 4 — `TransferService` runs a set

One runner per enabled configuration. Two things that are currently implicit become explicit:

- **Concurrency is global.** `MaxConcurrentTransfers` is per-run today; N configurations would
  multiply it. On a spinning disk more parallelism is slower — the application says so itself in
  `ConcurrencyAdvice` — so the limit has to be shared across configurations, not per one.
- **Idle cost multiplies.** N configurations means N watchers and N sweep timers. CLAUDE.md is
  explicit that assumptions here have been wrong by two orders of magnitude. Measure with
  `pbctl watch` at one, three and eight configurations before this is called done.

### Phase 5 — the Configurations tab

The list: name, user, created, status, with add, edit, copy, delete and a checkbox to enable.
Transfer Status and Uploads gain a configuration column.

---

## What could make this not worth doing

Written down so it is a decision rather than a discovery:

- If the idle cost measured in phase 4 is not near zero per configuration, the feature conflicts
  with the property that matters most on an instrument computer, and the answer is fewer
  configurations sharing one watcher rather than shipping it anyway.
- The phase 1 migration is the only step that can lose data. If it cannot be made crash-safe with
  a test that proves it, the overlapping-folders decision should be revisited rather than the
  migration shipped on hope.
