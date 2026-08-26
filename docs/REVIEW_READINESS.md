# Review Readiness

Use this before requesting an independent review. The goal is not a claim that no reviewer can
find a defect. The goal is that no known reachable issue remains in supported behavior, and that
every safety claim has executable evidence.

## Supported surface

- [ ] State which user-visible behaviors are supported by this change.
- [ ] Remove unsupported behavior from code, settings, UI, and current documentation. A hidden or
      opt-in path is still supported code and is still reviewable.
- [ ] List any persisted values written by older releases and say how they load, normalize, or
      retire without losing unrelated settings or ledger history.
- [ ] Add a release-note entry for user-visible behavior changes.

## Transfer invariants

- [ ] Every route to `TransferCoordinator` passes through `ReadinessGate`; no manual or recovery
      route can send a file that is still being written.
- [ ] Every persisted state transition has an existing ledger row. Test the unknown-row case so an
      update cannot silently disappear.
- [ ] Interrupted work is requeued without filling a bounded channel before workers run.
- [ ] A destination is derived in one place from the current local path. Test case-only renames if
      the ledger compares paths case-insensitively.
- [ ] A claimed verification result is backed by the server's MD5, never a successful PUT alone.

## Test evidence

- [ ] Add a focused test for each new invariant and failure path.
- [ ] Use strict fakes that count or reject unexpected network, hash, and ledger operations.
- [ ] Assert cost on settled fast paths: no upload, no hash, and no server request when the ledger
      already answers the question.
- [ ] Inject cancellation, missing source, remote listing or hash failure, and crash recovery where
      the change touches those paths.
- [ ] Temporarily revert each safety fix and confirm its new test fails before relying on it.
- [ ] Run `dotnet build PanoramaBridge.sln -c Release` and
      `dotnet test PanoramaBridge.sln -c Release` with no warnings.

## External contracts

- [ ] Re-run the opt-in SMB suite after changing monitoring or readiness behavior.
- [ ] Re-run the live WebDAV contract checks before release whenever transport behavior changes.
- [ ] Record measured server or instrument facts in the handoff only after the corresponding check
      is reproducible.
