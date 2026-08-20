using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Storage;
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
        if (!File.Exists(localPath))
        {
            _log.LogDebug("{Path} disappeared before it could be transferred.", localPath);
            return;
        }

        var stamp = LocalFileStamp.FromFile(localPath);

        var destination = PathSafety.ResolveDestination(
            _options.LocalBaseDirectory,
            localPath,
            _options.DestinationRoot);

        var encoded = destination.ToEncodedString();

        // Decide BEFORE touching the row. Writing a Queued state first would erase the
        // verified standing that the ledger tier reads, so every file would fall through to
        // the network and the fast path would never fire.
        var decision = await _decisions
            .DecideAsync(stamp, destination, _options.ConflictPolicy, cancellationToken)
            .ConfigureAwait(false);

        var record = await _store.GetAsync(localPath, cancellationToken).ConfigureAwait(false)
            ?? UploadRecord.ForNewFile(stamp, encoded);

        record = record with
        {
            RemotePath = encoded,
            Length = stamp.Length,
            LastWriteUnixMs = stamp.LastWriteUnixMs,
        };

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

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        // Inline, not Progress<long>: the framework type POSTS each report, so one could be
        // delivered after the "verified" state that follows it and flip a finished row back to
        // in-progress. The aggregator is latest-wins, so ordering is load-bearing.
        var progress = new InlineProgress<long>(sent =>
        {
            // Throttled here as well as in the UI: a 1 MiB granularity on a 7 GB file is seven
            // thousand events, and the consumer should not have to defend against that.
            if (stopwatch.Elapsed - lastReport < TimeSpan.FromMilliseconds(250) && sent < stamp.Length)
            {
                return;
            }

            lastReport = stopwatch.Elapsed;

            var rate = stopwatch.Elapsed.TotalSeconds > 0 ? sent / stopwatch.Elapsed.TotalSeconds : 0;

            Report(localPath, encoded, TransferState.Uploading, "Uploading",
                sent, stamp.Length, rate);
        });

        var result = await _client
            .UploadAsync(localPath, destination, progress, cancellationToken)
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

        // A file that grew while it was being sent means the remote copy is already stale.
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

            _snapshots.Invalidate(destination.Parent);

            Report(localPath, encoded, TransferState.Superseded, "Changed during upload",
                result.BytesUploaded, stamp.Length,
                message: "The file changed while it was being uploaded; it will be sent again.");

            // Re-offer it. The dedup gate was released by the worker loop's finally, so this
            // is accepted rather than silently dropped.
            await EnqueueAsync(localPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        _snapshots.Invalidate(destination.Parent);

        if (!_options.VerifyUploads)
        {
            Interlocked.Increment(ref _uploaded);
            Report(localPath, encoded, TransferState.Uploaded, "Uploaded",
                result.BytesUploaded, stamp.Length, result.BytesPerSecond,
                verification: VerifyMethod.None);
            return;
        }

        Report(localPath, encoded, TransferState.Uploaded, "Verifying",
            result.BytesUploaded, stamp.Length, result.BytesPerSecond);

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
                result.BytesUploaded, stamp.Length, message: Message);
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
                result.BytesUploaded, stamp.Length, message: message);
            return;
        }

        Interlocked.Increment(ref _uploaded);

        await _store
            .MarkVerifiedAsync(localPath, VerifyMethod.ServerMd5, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        Report(localPath, encoded, TransferState.Verified, "Verified",
            result.BytesUploaded, stamp.Length, result.BytesPerSecond,
            verification: VerifyMethod.ServerMd5);
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
