using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>What one sweep of the monitored tree found.</summary>
/// <param name="Examined">Files that matched the filter.</param>
/// <param name="Offered">Files handed on because the ledger does not account for them.</param>
/// <param name="AlreadyAccountedFor">Files the ledger settles without any further work.</param>
/// <param name="Elapsed">How long the sweep took.</param>
/// <param name="Problem">Why the sweep could not run, phrased for the user. Null when it ran.</param>
public readonly record struct SweepResult(
    int Examined,
    int Offered,
    int AlreadyAccountedFor,
    TimeSpan Elapsed,
    string? Problem = null)
{
    /// <summary>True when the monitored folder could not be read at all.</summary>
    public bool Failed => Problem is not null;
}

/// <summary>What the sweep walks, and what it compares against.</summary>
public sealed record ReconciliationOptions
{
    /// <summary>Directory walked. Also the base the remote structure mirrors.</summary>
    public required string Root { get; init; }

    /// <summary>Remote folder the structure is mirrored into.</summary>
    public required RemotePath DestinationRoot { get; init; }

    /// <summary>Which files count as data worth transferring.</summary>
    public required CandidateFilter Filter { get; init; }

    /// <summary>Whether to walk subdirectories.</summary>
    public bool IncludeSubdirectories { get; init; } = true;

    /// <summary>
    /// What to do when the destination is occupied, which decides whether a held file is offered
    /// again.
    /// </summary>
    /// <remarks>
    /// The sweep needs this because holding is now the only thing that happens to a conflict: the
    /// per-file buttons were withdrawn. A row held under <c>Ask</c> stays held, which is the point
    /// of asking. Under any other policy it has to be offered again, or changing the setting —
    /// the one remedy the release notes give — does nothing at all: the policy is consulted by
    /// the decision ladder, and the ladder is only reached for files the sweep offers.
    /// </remarks>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    /// <summary>
    /// How many times an upload that failed is retried before the sweep stops offering it.
    /// </summary>
    /// <remarks>
    /// Bounds the damage a permanently failing transfer can do: without it, a seven-gigabyte file
    /// the server keeps refusing would be sent again on every sweep, for ever. It stays in the
    /// ledger as failed, remains visible in the uploads view, and is offered again the moment the
    /// file changes or the user asks for it explicitly.
    /// </remarks>
    public int MaxUploadAttempts { get; init; } = 5;
}

/// <summary>
/// Walks the monitored tree and offers whatever the ledger does not already account for.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism continuous monitoring rests on. Change notifications are an accelerator
/// that is allowed to fail silently -- they are dropped when the kernel buffer overflows, and
/// whether they arrive at all over SMB depends on the server -- so anything that must not be
/// missed has to be found by walking the folder.
/// </para>
/// <para>
/// The filtering here is what keeps that affordable. A file the ledger settles is dropped before
/// it reaches the readiness gate, which is what stops a sweep of an already-transferred directory
/// from opening every file in it every quarter of an hour, on the same disk an instrument is
/// writing to.
/// </para>
/// </remarks>
public sealed class ReconciliationScanner
{
    /// <summary>
    /// Files asked about in one ledger lookup.
    /// </summary>
    /// <remarks>
    /// Bounds what a sweep holds at once. A tree of two hundred thousand files is walked in
    /// batches of this size, so peak memory is a few hundred paths rather than the whole tree.
    /// </remarks>
    private const int BatchSize = 500;

    private readonly IStateStore _store;
    private readonly ReconciliationOptions _options;
    private readonly ILogger<ReconciliationScanner> _log;

    /// <summary>
    /// Where a row's copy belongs, answered the same way the engine answers it.
    /// </summary>
    /// <remarks>
    /// The sweep and the engine disagreeing about this is what made renamed files and directory
    /// acquisitions be offered on every pass for ever. They cannot disagree now: it is the same
    /// type, given the same two values.
    /// </remarks>
    private readonly DestinationMap _destinations;

    public ReconciliationScanner(
        IStateStore store,
        ReconciliationOptions options,
        ILogger<ReconciliationScanner>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullLogger<ReconciliationScanner>.Instance;
        _destinations = new DestinationMap(_options.Root, _options.DestinationRoot);
    }

    /// <summary>
    /// Walks the tree once.
    /// </summary>
    /// <param name="offer">
    /// Called for each file that needs looking at. It receives the stamp read during the walk, so
    /// nothing has to stat the file a second time.
    /// </param>
    /// <param name="cancellationToken">Stops the sweep.</param>
    public async Task<SweepResult> SweepAsync(
        Func<string, LocalFileStamp, Task> offer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var started = DateTimeOffset.UtcNow;
        var examined = 0;
        var offered = 0;
        var accounted = 0;

        var batch = new List<LocalFileStamp>(BatchSize);

        IEnumerator<LocalFileStamp> walk;

        try
        {
            walk = Enumerate().GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreachable(ex, started);
        }

        using (walk)
        {
            while (true)
            {
                LocalFileStamp stamp;

                try
                {
                    if (!walk.MoveNext())
                    {
                        break;
                    }

                    stamp = walk.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A share that went away mid-walk, or a folder whose permissions changed
                    // underneath us. Report what was found rather than losing the whole sweep.
                    _log.LogWarning(ex, "The sweep of {Root} stopped early.", _options.Root);

                    return new SweepResult(
                        examined,
                        offered,
                        accounted,
                        DateTimeOffset.UtcNow - started,
                        Describe(ex));
                }

                cancellationToken.ThrowIfCancellationRequested();

                examined++;
                batch.Add(stamp);

                if (batch.Count < BatchSize)
                {
                    continue;
                }

                var full = await ProcessAsync(batch, offer, cancellationToken).ConfigureAwait(false);
                offered += full.Offered;
                accounted += full.Accounted;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            var last = await ProcessAsync(batch, offer, cancellationToken).ConfigureAwait(false);
            offered += last.Offered;
            accounted += last.Accounted;
        }

        var elapsed = DateTimeOffset.UtcNow - started;

        _log.LogDebug(
            "Swept {Root}: {Examined} file(s) examined, {Offered} offered, {Accounted} already settled, in {Ms} ms.",
            _options.Root,
            examined,
            offered,
            accounted,
            (int)elapsed.TotalMilliseconds);

        return new SweepResult(examined, offered, accounted, elapsed);
    }

    /// <summary>
    /// Walks the tree, yielding each transferable thing already measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hand-rolled walk rather than <c>EnumerateFiles</c> with recursion, because it needs to
    /// decide whether each directory should be descended into before listing its contents.
    /// </para>
    /// <para>
    /// Files are stamped from the entry the walk already read, so size and modification time are
    /// free; going through <c>LocalFileStamp.FromFile</c> instead would stat every file a second
    /// time, and over SMB that is a second round trip each.
    /// </para>
    /// </remarks>
    private IEnumerable<LocalFileStamp> Enumerate()
    {
        var options = new EnumerationOptions
        {
            // One level at a time: the recursion is here so it can be pruned.
            RecurseSubdirectories = false,

            // A folder this account cannot read must not abort the walk: on a shared instrument
            // volume there is usually at least one.
            IgnoreInaccessible = true,

            // Reparse points are skipped so a junction cannot send the walk round in a loop.
            AttributesToSkip =
                FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var pending = new Stack<string>();
        pending.Push(_options.Root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var isRoot = string.Equals(directory, _options.Root, StringComparison.OrdinalIgnoreCase);

            // Materialised here so a failure surfaces now rather than part-way through the
            // foreach, where it could not be told apart from a folder going away mid-walk.
            List<FileSystemInfo> entries;

            try
            {
                entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A subfolder that cannot be read is skipped: on a shared instrument volume
                // there is usually at least one, and losing the sweep over it would be worse.
                //
                // The root is different. If the monitored folder itself is unreachable -- a
                // share that is down, a drive not mounted yet -- the sweep must say so, so
                // monitoring can report it and wait rather than quietly finding nothing and
                // looking healthy.
                if (isRoot)
                {
                    throw;
                }

                _log.LogDebug(ex, "Skipped {Directory} while sweeping.", directory);
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (_options.IncludeSubdirectories)
                    {
                        pending.Push(child.FullName);
                    }

                    continue;
                }

                if (entry is FileInfo file && _options.Filter.Accepts(file.FullName))
                {
                    yield return LocalFileStamp.FromFileInfo(file);
                }
            }
        }
    }

    private async Task<(int Offered, int Accounted)> ProcessAsync(
        List<LocalFileStamp> batch,
        Func<string, LocalFileStamp, Task> offer,
        CancellationToken cancellationToken)
    {
        var known = await _store
            .GetManyAsync(batch.Select(stamp => stamp.Path).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        var offered = 0;
        var accounted = 0;

        foreach (var stamp in batch)
        {
            if (IsAccountedFor(stamp, known.GetValueOrDefault(stamp.Path)))
            {
                accounted++;
                continue;
            }

            offered++;
            await offer(stamp.Path, stamp).ConfigureAwait(false);
        }

        return (offered, accounted);
    }

    /// <summary>
    /// Whether the ledger already answers for this file, so the sweep can leave it alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately conservative: anything not clearly finished is offered, and the decision
    /// ladder -- which is allowed to ask the server -- makes the real call. The only judgements
    /// made here are the ones the ledger can settle on its own, with no network and without
    /// touching the file.
    /// </para>
    /// <para>
    /// A conflicted file is left alone because it needs a person rather than a retry, and
    /// re-running the ladder over it would spend a request per sweep on an answer that has not
    /// changed. It comes back the moment the local file does.
    /// </para>
    /// <para>
    /// A failed file is retried until it has used up its attempts. Attempts count uploads that
    /// actually started, so a failure that happened before then -- an unreachable server while
    /// the ladder was still deciding, say -- keeps being retried. That is deliberate: those cost
    /// nothing beyond a ledger read, and an unattended monitor has to recover on its own from a
    /// network that was down overnight.
    /// </para>
    /// </remarks>
    private bool IsAccountedFor(LocalFileStamp stamp, UploadRecord? record)
    {
        if (record is null)
        {
            return false;
        }

        string destination;

        try
        {
            // The ledger uses a case-insensitive key, while Panorama does not. The path yielded
            // by this sweep is therefore the only spelling that can answer where this candidate
            // would go now; resolving from the ledger spelling would offer a case-only rename
            // on every pass after it had already been uploaded under the new name.
            destination = _destinations.For(stamp.Path).ToEncodedString();
        }
        catch (ArgumentException)
        {
            // A name the server would mangle, or a path outside the monitored tree. Offer it, so
            // the failure is recorded against the file and shown, rather than being swallowed by
            // the component whose job is to find things.
            return false;
        }

        if (record.IsSettledAt(stamp, destination))
        {
            return true;
        }

        if (!stamp.Matches(record.Length, record.LastWriteUnixMs))
        {
            // Changed since the ledger last saw it, whatever state that row is in.
            return false;
        }

        // Held for a reason the policy cannot answer, whatever state the row is in. Checked
        // before the switch because the reason outlives the state: a row retired under Skip is
        // saved Skipped with its kind intact, and arms that matched only Conflict stopped
        // applying to it — which let a change to Overwrite send a file that was being protected.
        if (record.IsHeldRegardlessOfPolicy)
        {
            return true;
        }

        return record.State switch
        {
            // Held, and staying held only while the answer is still "ask me". Any other policy
            // is an answer, and the file has to reach the ladder for it to be applied.
            TransferState.Conflict => _options.ConflictPolicy is ConflictPolicy.Ask
                or ConflictPolicy.Rename,

            // A conflict the ladder resolved as Skip is saved Skipped but never server-verified,
            // so IsSettledAt above never matches it. Without this arm the file re-runs the whole
            // ladder -- another listing, maybe another hash -- on every sweep for as long as the
            // policy stays Skip.
            TransferState.Skipped when record.VerifyMethod != VerifyMethod.ServerMd5 =>
                _options.ConflictPolicy == ConflictPolicy.Skip
                && string.Equals(record.RemotePath, destination, StringComparison.Ordinal),

            TransferState.Failed => record.Attempts >= _options.MaxUploadAttempts,
            _ => false,
        };
    }

    private SweepResult Unreachable(Exception ex, DateTimeOffset started)
    {
        _log.LogWarning(ex, "Could not read the monitored folder {Root}.", _options.Root);
        return new SweepResult(0, 0, 0, DateTimeOffset.UtcNow - started, Describe(ex));
    }

    private string Describe(Exception ex) =>
        $"Could not read the monitored folder {_options.Root}: {ex.Message} "
        + "Transfers resume by themselves once it is reachable again.";
}
