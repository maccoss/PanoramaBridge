using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.ThermoRaw;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Transfer;

/// <summary>Settings for a transfer run.</summary>
public sealed class TransferEngineOptions
{
    /// <summary>Local root; the structure below it is mirrored remotely.</summary>
    public required string LocalBaseDirectory { get; init; }

    /// <summary>Remote folder the structure is mirrored into.</summary>
    public required RemotePath DestinationRoot { get; init; }

    /// <summary>What to do when a different file already occupies a destination.</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    /// <summary>How many files move at once.</summary>
    public int MaxConcurrentTransfers { get; init; } = 3;

    /// <summary>
    /// Queue depth. Bounded so pointing the app at a directory of two hundred thousand files
    /// applies backpressure instead of exhausting memory.
    /// </summary>
    public int QueueCapacity { get; init; } = 5000;

    /// <summary>
    /// Whether to confirm each upload against the server's own hash.
    /// </summary>
    /// <remarks>
    /// Costs one request per file and is the only thing that makes a "verified" claim mean
    /// anything, so it is on by default.
    /// </remarks>
    public bool VerifyUploads { get; init; } = true;

    /// <summary>
    /// Whether to write a <c>.md5</c> file beside each upload.
    /// </summary>
    /// <remarks>
    /// The hashes are in the ledger regardless, but the ledger lives on one instrument computer
    /// and does not travel with the data. A sidecar does, and it is the only place the date the
    /// instrument wrote the file can survive -- the server stamps an upload with its arrival
    /// time and will not accept anything else.
    /// </remarks>
    public bool WriteChecksumSidecars { get; init; } = true;
}

/// <summary>
/// Owns the transfer queue and its workers.
/// </summary>
/// <remarks>
/// <para>
/// All mutable state lives here, in concurrent collections. The Python version spread the same
/// state across six plain sets and dictionaries mutated from the file-watcher thread, the upload
/// thread and the GUI thread with no synchronisation at all -- safe only because the interpreter
/// lock plus a single worker made real concurrency impossible. Any attempt to parallelise it
/// would have corrupted that state immediately.
/// </para>
/// <para>
/// Nothing here touches a UI type. Progress leaves through <see cref="Progress"/>, which the
/// caller marshals onto whatever thread it needs.
/// </para>
/// </remarks>
public sealed class TransferCoordinator : IAsyncDisposable
{
    private readonly IWebDavClient _client;
    private readonly IStateStore _store;
    private readonly UploadDecisionService _decisions;
    private readonly RemoteSnapshotCache _snapshots;
    private readonly TransferEngineOptions _options;
    private readonly ILogger<TransferCoordinator> _log;

    private readonly Channel<string> _queue;

    /// <summary>
    /// Paths accepted into this run. Gate against enqueueing the same file twice, whether from
    /// a duplicate watcher event, a reconciliation sweep or a manual request.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _accepted =
        new(StringComparer.OrdinalIgnoreCase);

    private int _uploaded;
    private int _skipped;
    private int _conflicts;
    private int _failed;
    private long _bytesUploaded;

    public TransferCoordinator(
        IWebDavClient client,
        IStateStore store,
        TransferEngineOptions options,
        RemoteSnapshotCache? snapshots = null,
        UploadDecisionService? decisions = null,
        ILogger<TransferCoordinator>? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullLogger<TransferCoordinator>.Instance;

        _snapshots = snapshots ?? new RemoteSnapshotCache(client);
        _decisions = decisions ?? new UploadDecisionService(store, _snapshots);

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Raised as transfers progress. Fires on worker threads.</summary>
    public event Action<TransferProgress>? Progress;

    /// <summary>Files accepted into this run and not yet finished.</summary>
    public int Pending => _accepted.Count;

    /// <summary>
    /// Offers a file to the queue.
    /// </summary>
    /// <returns>False when it was already accepted, so nothing was enqueued.</returns>
    public async Task<bool> EnqueueAsync(string localPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var full = Path.GetFullPath(localPath);

        // TryAdd is the dedup gate: a file already in flight is never queued a second time.
        if (!_accepted.TryAdd(full, 0))
        {
            return false;
        }

        await _queue.Writer.WriteAsync(full, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Signals that no more files will be offered.</summary>
    public void CompleteAdding() => _queue.Writer.TryComplete();

    /// <summary>
    /// Requeues anything a previous run left mid-flight.
    /// </summary>
    /// <remarks>
    /// Every state change is written before the action it describes, so a row still marked
    /// Uploading means the process died at some point during that upload. Whether the bytes
    /// arrived is unknown, which is exactly what the decision ladder is for: it will find an
    /// identical remote copy and skip, or a partial one and re-send.
    /// </remarks>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await _store.GetInterruptedAsync(cancellationToken).ConfigureAwait(false);
        var requeued = 0;

        foreach (var record in interrupted)
        {
            if (!File.Exists(record.LocalPath))
            {
                // The source is gone, so there is nothing left to do about it either way.
                await _store
                    .SetStateAsync(
                        record.LocalPath,
                        TransferState.Failed,
                        "The local file no longer exists.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (await EnqueueAsync(record.LocalPath, cancellationToken).ConfigureAwait(false))
            {
                requeued++;
            }
        }

        if (requeued > 0)
        {
            _log.LogInformation("Requeued {Count} transfers interrupted by a previous run.", requeued);
        }

        return requeued;
    }

    /// <summary>
    /// Runs the workers until the queue is completed and drained, or cancellation is requested.
    /// </summary>
    public async Task<TransferSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var workers = Enumerable
            .Range(0, Math.Max(1, _options.MaxConcurrentTransfers))
            .Select(_ => Task.Run(() => WorkerLoopAsync(cancellationToken), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        stopwatch.Stop();

        return new TransferSummary(
            _uploaded,
            _skipped,
            _conflicts,
            _failed,
            Interlocked.Read(ref _bytesUploaded),
            stopwatch.Elapsed);
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var localPath in _queue.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(localPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Say that it stopped. Without this the file's last report stays "Uploading",
                // and the aggregator keeps the latest report per file for the life of the
                // service -- so "is a transfer in flight?" answers yes for the rest of the
                // session. That silently disabled the updater's Restart now and the tray's Exit,
                // which both refuse while something is in flight: the buttons looked dead, on
                // whichever machine had once had a transfer interrupted.
                //
                // Queued rather than Failed: nothing failed. The file is still wanted and the
                // next sweep offers it again.
                Report(
                    localPath,
                    "?",
                    TransferState.Queued,
                    "Interrupted",
                    0,
                    0,
                    message: "Stopped before it finished. It will be offered again.");

                throw;
            }
            catch (Exception ex)
            {
                // One bad file must never take the run down with it.
                Interlocked.Increment(ref _failed);
                _log.LogError(ex, "Transfer of {Path} failed.", localPath);

                await SafeSetStateAsync(localPath, TransferState.Failed, ex.Message).ConfigureAwait(false);
                Report(localPath, "?", TransferState.Failed, "Failed", 0, 0, message: ex.Message);
            }
            finally
            {
                _accepted.TryRemove(localPath, out _);
            }
        }
    }

    private async Task ProcessAsync(string localPath, CancellationToken cancellationToken)
    {
        // A directory here is a Bruker .d, offered whole by the sweep. It is packed into the
        // single .d.zip Panorama stores, and from the moment it is packed the rest of this class
        // treats it as the ordinary file it has become.
        if (Directory.Exists(localPath))
        {
            await ProcessDatasetAsync(localPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(localPath))
        {
            _log.LogDebug("{Path} disappeared before it could be transferred.", localPath);
            return;
        }

        var stamp = LocalFileStamp.FromFile(localPath);

        // Read before deciding. A person may have resolved a conflict since this file was last
        // offered, and their answer replaces the question rather than informing it -- asking the
        // policy again would return the same conflict and discard the decision.
        var decided = await _store.GetAsync(localPath, cancellationToken).ConfigureAwait(false);

        if (decided is { HasPendingResolution: true })
        {
            await ApplyResolutionAsync(decided, stamp, cancellationToken).ConfigureAwait(false);
            return;
        }

        var destination = PathSafety.ResolveDestination(
            _options.LocalBaseDirectory,
            localPath,
            _options.DestinationRoot);

        var encoded = destination.ToEncodedString();

        // Decide BEFORE touching the row. Writing a Queued state first would erase the
        // verified standing that the ledger tier reads, so every file would fall through to
        // the network and the fast path would never fire.
        var decision = await _decisions
            .DecideAsync(
                stamp,
                destination,
                _options.ConflictPolicy,
                cancellationToken,
                onStep: step => Report(
                    localPath,
                    encoded,
                    TransferState.Queued,
                    step,
                    0,
                    stamp.Length,
                    message: Explain(step)))
            .ConfigureAwait(false);

        var record = await _store.GetAsync(localPath, cancellationToken).ConfigureAwait(false)
            ?? UploadRecord.ForNewFile(stamp, encoded);

        // What the file says about itself, which is a different question from what the file
        // system says about it. The readiness gate has already established that nothing holds it
        // and its size has stopped changing, and neither of those can tell a finished
        // acquisition from an abandoned one.
        var rawCheck = InspectRawFile(localPath, stamp.Length);

        record = record with
        {
            RemotePath = encoded,
            Length = stamp.Length,
            LastWriteUnixMs = stamp.LastWriteUnixMs,
            RawCheck = rawCheck?.Summary ?? record.RawCheck,
        };

        if (rawCheck is { IsProvenTruncated: true })
        {
            Interlocked.Increment(ref _conflicts);

            // Held rather than failed, and saved rather than updated, for the same reason a
            // conflict is: a file refused with no row is a file nobody can see was refused.
            // Uploading it is the one thing that must not happen -- a short copy on the server
            // looks complete and verifies against its own truncated content.
            await _store
                .SaveAsync(
                    record with
                    {
                        State = TransferState.Conflict,
                        LastError = rawCheck.Summary,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _log.LogWarning(
                "{Path}: not uploaded. {Summary}. {Evidence}",
                localPath,
                rawCheck.Summary,
                string.Join("; ", rawCheck.Evidence));

            Report(localPath, encoded, TransferState.Conflict, "Incomplete file",
                0, stamp.Length, message: rawCheck.Summary);
            return;
        }

        _log.LogDebug(
            "{Path}: {Action} (decided at tier {Tier}) - {Reason}",
            localPath, decision.Action, decision.Tier, decision.Reason);

        switch (decision.Action)
        {
            case UploadAction.Skip:
                await CompleteSkipAsync(record, decision, cancellationToken).ConfigureAwait(false);
                return;

            case UploadAction.Conflict:

                // Rename is a standing instruction rather than a question, so it is carried out
                // here instead of being held. The setting has existed since the first release and
                // did nothing: the decision ladder returned a conflict saying "a new name is
                // needed" and no caller ever picked one, so choosing Rename behaved exactly like
                // Ask and files piled up waiting for a decision nobody knew to make.
                if (_options.ConflictPolicy == ConflictPolicy.Rename)
                {
                    var renamed = await RenameAroundAsync(
                        record, stamp, destination, cancellationToken).ConfigureAwait(false);

                    if (renamed)
                    {
                        return;
                    }

                    // Could not find out what is in the folder, so there is no name to trust.
                    // Falls through and is held, which is the safe end of that failure.
                }

                Interlocked.Increment(ref _conflicts);

                // Saved rather than updated: a file seen for the first time has no row yet, and
                // an UPDATE would silently do nothing -- leaving a refused file invisible in the
                // audit view, which is exactly the tracking gap this design exists to close.
                await _store
                    .SaveAsync(
                        record with
                        {
                            State = TransferState.Conflict,
                            LastError = decision.Reason,
                            Md5 = decision.Hashes?.Md5 ?? record.Md5,
                            Sha256 = decision.Hashes?.Sha256 ?? record.Sha256,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                Report(localPath, encoded, TransferState.Conflict, "Needs a decision",
                    0, stamp.Length, message: decision.Reason);
                return;

            case UploadAction.Upload:
            default:
                // Make sure the row exists before the upload path starts issuing state updates.
                await _store
                    .SaveAsync(record with { State = TransferState.Queued }, cancellationToken)
                    .ConfigureAwait(false);

                await UploadAsync(record, stamp, destination, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Says why a check is taking a moment, in terms of what is actually happening.
    /// </summary>
    /// <remarks>
    /// Both of these were reported as the application "sitting there doing nothing". Neither is
    /// idle; both are waiting on work whose cost is proportional to something the user can see,
    /// so saying which one it is turns a stall into a wait.
    /// </remarks>
    private static string Explain(string step) => step switch
    {
        // Two steps, not one, because they now cost wildly different amounts. Listing a folder
        // is a single cheap request; hashing one makes Panorama read every byte in it. While
        // both were reported as "Checking server", the message had to describe the expensive
        // case, so it explained a delay that most files never incur.
        "Checking server" =>
            "Asking the server what is already in the destination folder.",

        "Hashing the destination" =>
            "A file of this name is already on the server, so Panorama is being asked for its "
            + "checksum. It works that out over everything in the folder, which takes a while "
            + "for a folder holding a lot of data. Paid once per folder.",

        "Checking file" =>
            "Reading this file to compare it with the copy already on the server.",

        _ => step,
    };

    private async Task CompleteSkipAsync(
        UploadRecord record,
        UploadDecision decision,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _skipped);

        var verification = decision.ImpliedVerification;

        var updated = record with
        {
            State = TransferState.Skipped,
            VerifyMethod = verification,
            VerifiedUtc = verification == VerifyMethod.ServerMd5 ? DateTimeOffset.UtcNow : null,
            Md5 = decision.Hashes?.Md5 ?? decision.RemoteHash ?? record.Md5,
            Sha256 = decision.Hashes?.Sha256 ?? record.Sha256,
            LastError = null,
        };

        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        Report(
            record.LocalPath,
            record.RemotePath,
            TransferState.Skipped,
            "Already on the server",
            record.Length,
            record.Length,
            verification: verification,
            message: decision.Reason);
    }

    /// <summary>
    /// Packs a directory acquisition and transfers it as one object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The archive is the transfer item. That is what makes this safe without any atomicity
    /// machinery: one object either arrives and verifies against the server's own checksum or it
    /// does not, so there is no state in which half an acquisition sits on the server looking
    /// complete. Verification, the checksum sidecar, conflict handling and the ledger all work on
    /// it unchanged, because by then it is simply a file.
    /// </para>
    /// <para>
    /// The ledger row is keyed on the folder, not the archive, so what is recorded is the thing
    /// the user has and can point at. Its length and modification time are the folder's, which is
    /// what decides whether it needs sending again.
    /// </para>
    /// </remarks>
    private async Task ProcessDatasetAsync(string folder, CancellationToken cancellationToken)
    {
        var measured = DatasetFolder.Measure(folder);

        if (measured is not { } stampedFolder || stampedFolder.IsEmpty)
        {
            _log.LogDebug("{Path} is gone or empty; nothing to transfer.", folder);
            return;
        }

        var stamp = new LocalFileStamp(
            folder, stampedFolder.TotalBytes, stampedFolder.NewestWriteUnixMs);

        var destination = PathSafety.ResolveDestination(
            _options.LocalBaseDirectory,
            folder,
            _options.DestinationRoot,
            DatasetFolder.ArchiveNameFor(folder));

        var encoded = destination.ToEncodedString();

        var record = await _store.GetAsync(folder, cancellationToken).ConfigureAwait(false)
            ?? UploadRecord.ForNewFile(stamp, encoded);

        // Asked of the row as it was stored, before it is brought up to date. Updating it first
        // and then comparing compares the new measurement with itself, which is always equal --
        // so every acquisition would look unchanged and nothing would ever be sent twice.
        //
        // Checked before packing rather than after, because packing six gigabytes to discover it
        // was already there is the most expensive way possible to answer the question.
        var settled = record.IsSettledAt(stamp, encoded);

        record = record with
        {
            RemotePath = encoded,
            Length = stampedFolder.TotalBytes,
            LastWriteUnixMs = stampedFolder.NewestWriteUnixMs,
            IsDataset = true,
        };

        if (settled)
        {
            Interlocked.Increment(ref _skipped);

            Report(folder, encoded, TransferState.Skipped, "Already on the server",
                stampedFolder.TotalBytes, stampedFolder.TotalBytes,
                verification: record.VerifyMethod,
                message: "Unchanged since it was uploaded.");

            return;
        }

        var archivePath = DatasetArchive.StagingPathFor(folder);

        // Logged at Information rather than Debug, and with the numbers rather than a summary.
        // No instrument in this lab writes a directory acquisition, so every real one runs
        // somewhere nobody here can reproduce -- and a report that says "it did not work" is
        // worth very little next to one carrying what the folder actually measured.
        _log.LogInformation(
            "Packing {Path}: {Files} file(s), {Bytes:N0} bytes, newest write {Newest:u}.",
            folder,
            stampedFolder.FileCount,
            stampedFolder.TotalBytes,
            stampedFolder.NewestWriteUtc);

        Report(folder, encoded, TransferState.Uploading, "Packing",
            0, stampedFolder.TotalBytes,
            message: $"Packing {stampedFolder} into one archive before sending it.");

        var packed = await DatasetArchive
            .CreateAsync(
                folder,
                archivePath,
                stampedFolder.TotalBytes,
                new InlineProgress<long>(read => Report(
                    folder, encoded, TransferState.Uploading, "Packing",
                    read, stampedFolder.TotalBytes)),
                cancellationToken)
            .ConfigureAwait(false);

        if (!packed.Succeeded)
        {
            Interlocked.Increment(ref _failed);
            _log.LogError(
                "Could not pack {Path} ({Files} file(s), {Bytes:N0} bytes): {Reason} - {Detail}",
                folder,
                stampedFolder.FileCount,
                stampedFolder.TotalBytes,
                packed.Failure,
                packed.Detail);

            await _store
                .SaveAsync(
                    record with { State = TransferState.Failed, LastError = packed.Detail },
                    cancellationToken)
                .ConfigureAwait(false);

            Report(folder, encoded, TransferState.Failed, "Could not be packed",
                0, stampedFolder.TotalBytes, message: packed.Detail);

            return;
        }

        try
        {
            // From here it is a file, and everything that already exists for files applies. The
            // ledger row keeps the folder's identity; only the bytes come from the archive.
            await UploadAsync(
                    record with { State = TransferState.Uploading },
                    stamp,
                    destination,
                    cancellationToken,
                    source: packed.Path)
                .ConfigureAwait(false);
        }
        finally
        {
            // Always. A six-gigabyte temporary left beside an acquisition is its own failure,
            // and it is left inside the folder the sweep walks.
            DatasetArchive.Discard(packed.Path);
        }
    }

    /// <summary>
    /// Sends a file alongside the one occupying its name, under the first free one.
    /// </summary>
    /// <remarks>
    /// The names come from the folder snapshot the decision ladder has just taken, so this costs
    /// nothing beyond what has already been paid for. Returns false when the folder could not be
    /// read, in which case the caller holds the file: a name chosen without knowing what is there
    /// is a name that might overwrite something.
    /// <para>
    /// That failure branch is untested, and close to unreachable: the ladder has just listed this
    /// folder successfully, so the call below is served from the cache. It is kept because the
    /// alternative to a cache miss here is an exception escaping into the worker loop, and said
    /// to be untested rather than left to look covered.
    /// </para>
    /// </remarks>
    private async Task<bool> RenameAroundAsync(
        UploadRecord record,
        LocalFileStamp stamp,
        RemotePath destination,
        CancellationToken cancellationToken)
    {
        RemoteFolderSnapshot snapshot;

        try
        {
            snapshot = await _snapshots
                .GetAsync(destination.Parent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WebDavException ex)
        {
            _log.LogWarning(
                ex,
                "Could not list {Folder} to find a free name for {Path}; holding it instead.",
                destination.Parent,
                record.LocalPath);

            return false;
        }

        var free = ConflictNames.NextFree(destination.Name, snapshot.Entries.Keys);

        var renamed = PathSafety.ResolveDestination(
            _options.LocalBaseDirectory,
            record.LocalPath,
            _options.DestinationRoot,
            free);

        _log.LogInformation(
            "{Path}: '{Taken}' is occupied, sending it as '{Free}' by policy.",
            record.LocalPath,
            destination.Name,
            free);

        var queued = record with
        {
            RemotePath = renamed.ToEncodedString(),
            State = TransferState.Queued,
        };

        await _store.SaveAsync(queued, cancellationToken).ConfigureAwait(false);

        await UploadAsync(queued, stamp, renamed, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Carries out what a person decided about a conflict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision is consumed here rather than left on the row: it answers the conflict that
    /// was raised, not every conflict this file will ever have. Leaving it set would silently
    /// turn a one-off "overwrite this" into a standing policy for one file, which is the kind of
    /// thing nobody remembers agreeing to.
    /// </para>
    /// <para>
    /// Neither branch consults <see cref="TransferOptions.ConflictPolicy"/>. That is the whole
    /// point: the policy is what produced the conflict, and asking it again would produce the
    /// same one.
    /// </para>
    /// </remarks>
    private async Task ApplyResolutionAsync(
        UploadRecord record,
        LocalFileStamp stamp,
        CancellationToken cancellationToken)
    {
        var localPath = record.LocalPath;

        // A rename keeps the tree's shape and changes only the leaf, exactly as a packed
        // acquisition does.
        var destination = PathSafety.ResolveDestination(
            _options.LocalBaseDirectory,
            localPath,
            _options.DestinationRoot,
            record.Resolution == ConflictResolution.Rename ? record.RenameTo : null);

        var encoded = destination.ToEncodedString();

        _log.LogInformation(
            "{Path}: sending after a conflict was resolved as {Resolution}{Named}.",
            localPath,
            record.Resolution,
            record.Resolution == ConflictResolution.Rename ? $" ({record.RenameTo})" : string.Empty);

        // A rename is checked against its new destination before anything is sent. The name was
        // free when it was offered to the user, which may have been minutes or a reboot ago, and
        // "send it alongside" must never turn into "replace whatever arrived there since". An
        // overwrite needs no such check: replacing what is there is the decision.
        if (record.Resolution == ConflictResolution.Rename)
        {
            var check = await _decisions
                .DecideAsync(stamp, destination, ConflictPolicy.Ask, cancellationToken)
                .ConfigureAwait(false);

            if (check.Action != UploadAction.Upload)
            {
                Interlocked.Increment(ref _conflicts);

                var reason = check.Action == UploadAction.Skip
                    ? $"'{destination.Name}' on the server is already identical to this file."
                    : $"'{destination.Name}' is occupied as well. {check.Reason}";

                await _store
                    .SaveAsync(
                        record with
                        {
                            RemotePath = encoded,
                            State = TransferState.Conflict,
                            LastError = reason,
                            Resolution = ConflictResolution.None,
                            RenameTo = null,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                Report(localPath, encoded, TransferState.Conflict, "Needs a decision",
                    0, stamp.Length, message: reason);
                return;
            }
        }

        var queued = record with
        {
            RemotePath = encoded,
            Length = stamp.Length,
            LastWriteUnixMs = stamp.LastWriteUnixMs,
            State = TransferState.Queued,
            LastError = null,
            Resolution = ConflictResolution.None,
            RenameTo = null,
        };

        await _store.SaveAsync(queued, cancellationToken).ConfigureAwait(false);

        await UploadAsync(queued, stamp, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="source">
    /// The bytes to send, when they are not at <c>record.LocalPath</c>. A directory acquisition
    /// is packed into an archive beside it, and the row goes on identifying the folder -- which
    /// is the thing the user has -- while the upload reads the archive.
    /// </param>
    private async Task UploadAsync(
        UploadRecord record,
        LocalFileStamp stamp,
        RemotePath destination,
        CancellationToken cancellationToken,
        string? source = null)
    {
        var localPath = record.LocalPath;
        var readFrom = source ?? localPath;
        var encoded = record.RemotePath;

        await _store
            .SetStateAsync(localPath, TransferState.Uploading, null, cancellationToken)
            .ConfigureAwait(false);

        // The archive's size, not the folder's: progress has to be against what is going over
        // the wire, or a packed acquisition would appear to overshoot or stall short. It is
        // the total for every report this method makes, not only the ones during the upload:
        // the bytes counted against it are always the archive's, so the folder's size would
        // show a stored acquisition finishing at slightly over a hundred percent.
        var sending = source is null ? stamp.Length : new FileInfo(source).Length;

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        // Inline, not Progress<long>: the framework type POSTS each report, so one could be
        // delivered after the "verified" state that follows it and flip a finished row back to
        // in-progress. The aggregator is latest-wins, so ordering is load-bearing.
        var progress = new InlineProgress<long>(sent =>
        {
            // Throttled here as well as in the UI: a 1 MiB granularity on a 7 GB file is seven
            // thousand events, and the consumer should not have to defend against that.
            if (stopwatch.Elapsed - lastReport < TimeSpan.FromMilliseconds(250) && sent < sending)
            {
                return;
            }

            lastReport = stopwatch.Elapsed;

            var rate = stopwatch.Elapsed.TotalSeconds > 0 ? sent / stopwatch.Elapsed.TotalSeconds : 0;

            Report(localPath, encoded, TransferState.Uploading, "Uploading",
                sent, sending, rate);
        });

        // Stamped with the time the instrument wrote the file, not the time it was transferred.
        // Moving data is not the same as collecting it, and the collection date is the one that
        // means something a year later.
        var acquired = ChecksumSidecar.AcquiredFrom(stamp);

        var result = await _client
            .UploadAsync(readFrom, destination, progress, cancellationToken, acquired)
            .ConfigureAwait(false);

        stopwatch.Stop();
        Interlocked.Add(ref _bytesUploaded, result.BytesUploaded);

        // The upload's own pass produced the hashes, so cache them rather than ever reading
        // the file again.
        await _store
            .SaveCachedHashesAsync(stamp, result.Hashes, cancellationToken)
            .ConfigureAwait(false);

        var uploaded = record.WithHashes(result.Hashes) with { State = TransferState.Uploaded };
        await _store.SaveAsync(uploaded, cancellationToken).ConfigureAwait(false);

        // Something that grew while it was being sent means the remote copy is already stale.
        // Nothing in the Python version noticed this.
        //
        // For an acquisition the question is asked of the folder, not of the archive: the
        // archive is a snapshot taken before the upload and cannot change, while the folder can,
        // and it is the folder that decides whether what is now on the server is still current.
        var after = source is null
            ? LocalFileStamp.FromFile(localPath)
            : DatasetFolder.Measure(localPath) is { } now
                ? new LocalFileStamp(localPath, now.TotalBytes, now.NewestWriteUnixMs)
                : default;

        if (!after.Matches(stamp.Length, stamp.LastWriteUnixMs))
        {
            await _store
                .SetStateAsync(
                    localPath,
                    TransferState.Superseded,
                    "The file changed while it was being uploaded.",
                    cancellationToken)
                .ConfigureAwait(false);

            // What the server now holds is what was just sent, whatever the local file has
            // since become, so the cache can be told rather than emptied.
            _snapshots.Record(destination, result.BytesUploaded, result.Hashes.Md5, acquired);

            Report(localPath, encoded, TransferState.Superseded, "Changed during upload",
                result.BytesUploaded, sending,
                message: "The file changed while it was being uploaded; it will be sent again.");

            // Re-offer it. The dedup gate was released by the worker loop's finally, so this
            // is accepted rather than silently dropped.
            await EnqueueAsync(localPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Recorded, not invalidated. Dropping the snapshot here made every file in a batch pay
        // for a fresh collection hash, which the server computes on demand over the whole folder
        // -- so a hundred files into one destination cost a hundred hashes of an ever-growing
        // directory. Everything the cache needs is already in hand.
        _snapshots.Record(destination, result.BytesUploaded, result.Hashes.Md5, acquired);

        if (!_options.VerifyUploads)
        {
            Interlocked.Increment(ref _uploaded);
            Report(localPath, encoded, TransferState.Uploaded, "Uploaded",
                result.BytesUploaded, sending, result.BytesPerSecond,
                verification: VerifyMethod.None);
            return;
        }

        Report(localPath, encoded, TransferState.Uploaded, "Verifying",
            result.BytesUploaded, sending, result.BytesPerSecond);

        var remoteHash = await _client
            .GetFileHashAsync(destination, cancellationToken)
            .ConfigureAwait(false);

        if (remoteHash is null)
        {
            Interlocked.Increment(ref _failed);
            const string Message =
                "The server did not report a hash for the uploaded file, so it could not be verified.";

            await _store
                .SetStateAsync(localPath, TransferState.Failed, Message, cancellationToken)
                .ConfigureAwait(false);

            Report(localPath, encoded, TransferState.Failed, "Not verified",
                result.BytesUploaded, sending, message: Message);
            return;
        }

        if (!string.Equals(remoteHash, result.Hashes.Md5, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _failed);
            var message =
                $"The server stored different content than was sent (server {remoteHash}, "
                + $"local {result.Hashes.Md5}).";

            await _store
                .SetStateAsync(localPath, TransferState.Failed, message, cancellationToken)
                .ConfigureAwait(false);

            _log.LogError("Verification of {Path} failed: {Message}", localPath, message);

            Report(localPath, encoded, TransferState.Failed, "Verification failed",
                result.BytesUploaded, sending, message: message);
            return;
        }

        Interlocked.Increment(ref _uploaded);

        await _store
            .MarkVerifiedAsync(localPath, VerifyMethod.ServerMd5, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        await WriteSidecarAsync(destination, stamp, result, acquired, cancellationToken)
            .ConfigureAwait(false);

        Report(localPath, encoded, TransferState.Verified, "Verified",
            result.BytesUploaded, sending, result.BytesPerSecond,
            verification: VerifyMethod.ServerMd5);
    }

    /// <summary>
    /// Writes the checksum sidecar next to a file that has just been verified.
    /// </summary>
    /// <remarks>
    /// Deliberately after verification, and deliberately unable to fail the transfer. The file
    /// itself is on the server and proven; a sidecar that could not be written is a lesser
    /// problem than a transfer reported as failed when the data arrived intact.
    /// </remarks>
    private async Task WriteSidecarAsync(
        RemotePath destination,
        LocalFileStamp stamp,
        UploadResult result,
        DateTimeOffset acquired,
        CancellationToken cancellationToken)
    {
        if (!_options.WriteChecksumSidecars)
        {
            return;
        }

        var sidecar = ChecksumSidecar.PathFor(destination);

        try
        {
            var text = ChecksumSidecar.Render(
                destination.Name,
                result.Hashes,
                result.BytesUploaded,
                acquired,
                DateTimeOffset.UtcNow,
                Infrastructure.AppInfo.UserAgent);

            // Same date as the file it describes, so the two stay together however a listing is
            // sorted.
            await _client
                .UploadTextAsync(text, sidecar, cancellationToken, acquired)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "{Path} was uploaded and verified, but its checksum file could not be written.",
                sidecar);
        }
    }

    /// <summary>
    /// Asks a Thermo RAW file whether it is short, on a handle nothing else holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here writes to the file.</b> <see cref="FileMode.Open"/> and
    /// <see cref="FileAccess.Read"/>, never Create, Append or Write. An acquisition is not ours
    /// to modify, and a validator that could alter what it validates would be worse than no
    /// validator.
    /// </para>
    /// <para>
    /// Shared as <see cref="FileShare.Read"/> rather than <see cref="FileShare.None"/>. Both
    /// detect the case that matters identically -- Windows refuses either open while another
    /// handle holds the file for writing, which is how "the instrument is still acquiring" is
    /// detected -- but None additionally locks every other reader out for as long as we hold it.
    /// That is the wrong thing to do on an instrument computer: it would make this the reason
    /// somebody else's read failed. Read also stops a concurrent reader, a backup or a virus
    /// scanner, from being mistaken for a writer.
    /// </para>
    /// <para>
    /// If the open fails the answer is simply no opinion, and the next sweep asks again.
    /// </para>
    /// <para>
    /// The length comes from the open handle rather than the directory entry, which Windows
    /// leaves stale while a write handle is open -- a stale length is exactly what would make a
    /// growing file look truncated.
    /// </para>
    /// </remarks>
    private ThermoRawResult? InspectRawFile(string localPath, long length)
    {
        if (!ThermoRawValidator.IsCandidate(localPath))
        {
            return null;
        }

        try
        {
            using var reading = new FileStream(
                localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var result = ThermoRawValidator.Validate(reading, reading.Length, localPath);

            if (result.Verdict is ThermoRawVerdict.Unknown or ThermoRawVerdict.NotFinalised)
            {
                // Recorded rather than acted on. These are the files that say the checker needs
                // to learn something, and they are worth nothing if they are invisible.
                _log.LogInformation("{Path}: {Summary}", localPath, result.Summary);
            }

            return result;
        }
        catch (IOException)
        {
            // Something grabbed it between the gate and here. No opinion; the sweep will return.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task SafeSetStateAsync(string localPath, TransferState state, string? error)
    {
        try
        {
            var existing = await _store.GetAsync(localPath, CancellationToken.None)
                .ConfigureAwait(false);

            if (existing is null)
            {
                // Nothing was ever recorded for this file -- it failed before a destination
                // could even be resolved, which is what happens to a name the server would
                // mangle. Record it anyway so the failure is visible rather than silent.
                var length = 0L;
                var mtime = 0L;
                try
                {
                    var stamp = LocalFileStamp.FromFile(localPath);
                    length = stamp.Length;
                    mtime = stamp.LastWriteUnixMs;
                }
                catch (IOException)
                {
                    // The file may already be gone; the row is still worth writing.
                }

                await _store
                    .SaveAsync(
                        new UploadRecord(
                            localPath, string.Empty, length, mtime, null, null,
                            state, VerifyMethod.None, null, 0, error, false),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            await _store.SetStateAsync(localPath, state, error, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Already handling a failure; do not replace it with a worse one.
            _log.LogWarning(ex, "Could not record the state of {Path}.", localPath);
        }
    }

    private void Report(
        string localPath,
        string remotePath,
        TransferState state,
        string phase,
        long transferred,
        long total,
        double rate = 0,
        VerifyMethod verification = VerifyMethod.None,
        string? message = null) =>
        Progress?.Invoke(new TransferProgress(
            localPath, remotePath, state, phase, transferred, total, rate, verification, message));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        CompleteAdding();
        return ValueTask.CompletedTask;
    }
}
