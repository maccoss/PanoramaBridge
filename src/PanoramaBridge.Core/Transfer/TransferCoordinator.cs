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

    /// <summary>The only thing here that decides where a file goes.</summary>
    private readonly DestinationMap _destinations;
    private readonly TransferEngineOptions _options;
    private readonly ILogger<TransferCoordinator> _log;

    private readonly Channel<string> _queue;
    private readonly object _workersLock = new();
    private Task[]? _workers;

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

        _destinations = new DestinationMap(_options.LocalBaseDirectory, _options.DestinationRoot);
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
        // Recovery writes into the same bounded queue as ordinary discovery. Starting workers
        // here makes the operation safe on its own: callers cannot deadlock startup by recovering
        // more rows than the channel holds before they remember to call RunAsync.
        EnsureWorkersStarted(cancellationToken);

        var interrupted = await _store.GetInterruptedAsync(cancellationToken).ConfigureAwait(false);

        if (interrupted.Count == 0)
        {
            return 0;
        }

        // Asked per folder, and remembered. This runs at startup, which on a monitored network
        // share is routinely before the share is mounted — and an unreachable path answers
        // exactly as a deleted one does. Writing a row off then claims data no longer exists
        // while it sits untouched on the share.
        //
        // Per folder rather than once for the monitored root, because the ledger outlives the
        // setting: rows recorded while a different folder was watched are still returned here,
        // and a check of today's root says nothing about whether yesterday's is reachable. Per
        // folder rather than per row, because probing a dead host once per row stacks SMB
        // timeouts before the window is usable.
        //
        // Not being able to look is not evidence that nothing is there, which is the rule the
        // remote side already follows.
        var reachable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var requeued = 0;
        var unreachable = 0;

        foreach (var record in interrupted)
        {
            var folder = Path.GetDirectoryName(record.LocalPath);

            if (!string.IsNullOrEmpty(folder))
            {
                if (!reachable.TryGetValue(folder, out var canSee))
                {
                    canSee = Path.Exists(folder);
                    reachable[folder] = canSee;
                }

                if (!canSee)
                {
                    unreachable++;
                    continue;
                }
            }

            if (Directory.Exists(record.LocalPath))
            {
                // A folder acquisition from a version that sent folders as one archive. That has
                // been withdrawn, so the upload cannot be resumed — and requeueing it would be
                // silently dropped by the worker, leaving the row interrupted for ever.
                await _store
                    .SetStateAsync(
                        record.LocalPath,
                        TransferState.Failed,
                        "Sending a folder as a single archive has been removed, so this "
                        + "interrupted folder upload cannot be resumed. The folder itself is "
                        + "untouched.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!File.Exists(record.LocalPath))
            {
                // The monitored folder is reachable and the file is not in it: genuinely gone,
                // so there is nothing left to do about it either way.
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

        if (unreachable > 0)
        {
            _log.LogInformation(
                "{Count} interrupted transfer(s) are in folders that cannot be seen, so they "
                + "are left as they are until those folders are reachable again.",
                unreachable);
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

        var workers = EnsureWorkersStarted(cancellationToken);

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

    /// <summary>Starts the queue readers exactly once for this coordinator.</summary>
    private Task[] EnsureWorkersStarted(CancellationToken cancellationToken)
    {
        lock (_workersLock)
        {
            return _workers ??= Enumerable
                .Range(0, Math.Max(1, _options.MaxConcurrentTransfers))
                .Select(_ => Task.Run(() => WorkerLoopAsync(cancellationToken), CancellationToken.None))
                .ToArray();
        }
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
        if (Directory.Exists(localPath))
        {
            _log.LogDebug("{Path} is a directory and is not a transferable file.", localPath);
            return;
        }

        if (!File.Exists(localPath))
        {
            _log.LogDebug("{Path} disappeared before it could be transferred.", localPath);
            return;
        }

        var stamp = LocalFileStamp.FromFile(localPath);

        var destination = _destinations.For(localPath);

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

        // The same gate the sweep applies, applied again here because this is where every
        // route arrives. A file reaches this method from the folder watcher and from pbctl sync
        // as well, and neither consults the sweep — so a guard that lived only there could be
        // walked straight past, and the ladder would then re-save the row as an ordinary
        // occupied-destination conflict, losing the very marker that was protecting it.
        //
        // Before the ladder, so a held file costs no listing and no hashing while it waits.
        if (stamp.Matches(record.Length, record.LastWriteUnixMs)
            && record.IsHeldRegardlessOf(_options.ConflictPolicy))
        {
            Report(localPath, encoded, TransferState.Conflict, "Held",
                0, stamp.Length, message: record.LastError);
            return;
        }

        // What the file says about itself, which is a different question from what the file
        // system says about it. The readiness gate has already established that nothing holds it
        // and its size has stopped changing, and neither of those can tell a finished
        // acquisition from an abandoned one.
        var rawCheck = InspectRawFile(localPath, stamp.Length);

        record = record with
        {
            // SQLite keys the ledger NOCASE, so its stored spelling can predate a case-only
            // rename. The worker must read the path it was offered, not that older spelling.
            LocalPath = localPath,
            RemotePath = encoded,
            Length = stamp.Length,
            LastWriteUnixMs = stamp.LastWriteUnixMs,
            RawCheck = rawCheck?.Summary ?? record.RawCheck,
        };

        if (rawCheck is { IsProvenTruncated: true })
        {
            Interlocked.Increment(ref _conflicts);

            await _store
                .SaveAsync(
                    record with
                    {
                        State = TransferState.Conflict,
                        LastError = rawCheck.Summary,

                        // Recorded so the sweep knows the conflict policy is not an answer to
                        // this row. Skip would bury a broken acquisition and Overwrite would push
                        // it over a good remote copy, so it stays held until the file changes.
                        ConflictKind = ConflictKind.LocalFileDamaged,
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
                Interlocked.Increment(ref _conflicts);

                await _store
                    .SaveAsync(
                        record with
                        {
                            State = TransferState.Conflict,
                            LastError = decision.Reason,
                            // Plainly reassigned, and deliberately so. The gate above already
                            // turns back every held row whose file is unchanged, so the only way
                            // to arrive here carrying a withdrawn decision is with a file that
                            // has since changed — which is a new question about new bytes, and
                            // an ordinary conflict is the honest answer to it. Preserving the old
                            // marker here was tried and it held a file the person had just
                            // replaced, which is the opposite of the escape hatch every one of
                            // these holds is documented to have.
                            ConflictKind = ConflictKind.DestinationOccupied,
                            Md5 = decision.Hashes?.Md5 ?? record.Md5,
                            Sha256 = decision.Hashes?.Sha256 ?? record.Sha256,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                Report(localPath, encoded, TransferState.Conflict, "Held",
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
    private static string Explain(string step) => step switch
    {
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

    private async Task UploadAsync(
        UploadRecord record,
        LocalFileStamp stamp,
        RemotePath destination,
        CancellationToken cancellationToken)
    {
        var localPath = record.LocalPath;
        var encoded = record.RemotePath;

        await _store
            .SetStateAsync(localPath, TransferState.Uploading, null, cancellationToken)
            .ConfigureAwait(false);

        var sending = stamp.Length;

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
            .UploadAsync(localPath, destination, progress, cancellationToken, acquired)
            .ConfigureAwait(false);

        stopwatch.Stop();
        Interlocked.Add(ref _bytesUploaded, result.BytesUploaded);

        // The upload's own pass produced the hashes, so cache them rather than ever reading
        // the file again.
        await _store
            .SaveCachedHashesAsync(stamp, result.Hashes, cancellationToken)
            .ConfigureAwait(false);

        // The marker is cleared because the question it recorded has just been answered. Left
        // set, it would hold the row on every later sweep — and with verification turned off a
        // sent row never satisfies IsSettled, so nothing else would ever release it.
        var uploaded = record.WithHashes(result.Hashes) with
        {
            State = TransferState.Uploaded,
            ConflictKind = ConflictKind.Unknown,
        };
        await _store.SaveAsync(uploaded, cancellationToken).ConfigureAwait(false);

        // Something that grew while it was being sent means the remote copy is already stale.
        // Nothing in the Python version noticed this.
        var after = LocalFileStamp.FromFile(localPath);

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
                            state, VerifyMethod.None, null, 0, error),
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
    /// <remarks>
    /// Awaits any workers <see cref="RecoverInterruptedAsync"/> already started, so a caller
    /// that never reaches <see cref="RunAsync"/> -- because recovery itself threw -- does not
    /// leave them running unobserved against a channel nobody will read from again.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        CompleteAdding();

        Task[]? workers;
        lock (_workersLock)
        {
            workers = _workers;
        }

        if (workers is not null)
        {
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch
            {
                // Each worker already recorded and reported its own failure; Dispose must not
                // raise a second one on the way out.
            }
        }
    }
}
