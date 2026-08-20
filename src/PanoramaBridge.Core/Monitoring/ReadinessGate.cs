using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>What happened to a candidate file while waiting for it to settle.</summary>
/// <param name="Released">Files that became ready and were handed on.</param>
/// <param name="Abandoned">Files that went away or could not be read, with the reason.</param>
/// <param name="StillWaiting">Files still not ready when the gate stopped.</param>
public sealed record GateOutcome(
    IReadOnlyList<string> Released,
    IReadOnlyDictionary<string, FileReadiness> Abandoned,
    IReadOnlyDictionary<string, FileReadiness> StillWaiting);

/// <summary>
/// What one look at a file found.
/// </summary>
/// <param name="Path">Full local path.</param>
/// <param name="Readiness">What the look found, with a sentence fit to show the user.</param>
/// <param name="StillWatching">
/// Whether the gate is still following this file. False means it has been resolved one way or
/// another -- released, gone, unreadable, or handed back to the periodic sweep -- so a caller
/// keeping a list of files it is waiting on knows to drop it.
/// </param>
public readonly record struct GateReport(
    string Path,
    FileReadiness Readiness,
    bool StillWatching);

/// <summary>
/// How persistently to keep looking at a file another process is holding open.
/// </summary>
/// <remarks>
/// <para>
/// An instrument holds its output open for an entire run, which can be hours, so the gate settles
/// into a slow cadence rather than asking every second. <paramref name="RecheckInterval"/> is
/// that cadence, and it is the ceiling the gate's backoff climbs to.
/// </para>
/// <para>
/// <b>Not looking at all for a long stretch was tried, and it was wrong.</b> Deferring a locked
/// file for half an hour looks like an obvious saving; what it actually costs is latency, because
/// there is no way to learn that a file has been released except by looking. A file finishing
/// during the wait then sits there until the periodic folder check comes round. The saving being
/// bought is two file opens per thirty seconds, which is nothing. See §7 of the handoff for the
/// run that demonstrated this.
/// </para>
/// <para>
/// Giving up is not permanent, and must not be. After <paramref name="MaxChecks"/> looks that all
/// find the file in use, it stops being watched closely and goes back to the periodic sweep,
/// which offers it again on its next pass. Nothing is ever dropped for being busy too long.
/// </para>
/// </remarks>
/// <param name="RecheckInterval">Slowest cadence a file in use is re-examined at.</param>
/// <param name="MaxChecks">Looks before the file is handed back to the sweep.</param>
public sealed record LockedFilePolicy(TimeSpan RecheckInterval, int MaxChecks)
{
    /// <summary>Matches the defaults on the Local Monitoring tab.</summary>
    public static LockedFilePolicy Default { get; } = new(TimeSpan.FromSeconds(30), 20);

    /// <summary>Keeps looking for as long as the caller does, for a scan that has its own bound.</summary>
    public static LockedFilePolicy None { get; } =
        new(TimeSpan.FromSeconds(30), int.MaxValue);
}

/// <summary>
/// Holds files back until they are safe to read, then releases them.
/// </summary>
/// <remarks>
/// <para>
/// Sits between discovering a file and queueing it. Nothing reaches the transfer engine without
/// passing through here, which is what makes "we never upload a partial file" a property of the
/// system rather than of whichever code path happened to find the file.
/// </para>
/// <para>
/// It has to be a loop rather than a single test. A file is never ready on first sight -- one
/// observation cannot distinguish a finished file from one between writes -- so a scan that
/// checked once and moved on would either upload partial files or transfer nothing at all. The
/// gate keeps re-examining its candidates and releases each one the moment it settles, so an
/// acquisition still running simply arrives later rather than being missed.
/// </para>
/// <para>
/// Two ways in. <see cref="PumpAsync"/> takes a fixed set and finishes, which is what a manual
/// scan wants. <see cref="WatchAsync"/> takes a stream that stays open, which is what continuous
/// monitoring wants and is the only one that ever hands a file back.
/// </para>
/// </remarks>
public sealed class ReadinessGate
{
    private readonly FileStabilityTracker _tracker;
    private readonly TimeSpan _pollInterval;
    private readonly LockedFilePolicy _lockedFiles;
    private readonly ILogger<ReadinessGate> _log;

    /// <summary>
    /// Slowest cadence the gate backs off to while nothing is changing.
    /// </summary>
    /// <remarks>
    /// Thirty seconds against an acquisition that runs for half an hour is two checks a minute
    /// rather than sixty, and costs at most thirty seconds of latency once it finally finishes.
    /// <see cref="WatchAsync"/> takes it from <see cref="LockedFilePolicy.RecheckInterval"/>
    /// instead, which is the same number by default and the one the user can change.
    /// </remarks>
    private static readonly TimeSpan MaximumPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Candidates taken into the pending set in one pass of <see cref="WatchAsync"/>.
    /// </summary>
    /// <remarks>
    /// Keeps the set being actively re-examined to a working size. Without it, a first sweep of
    /// a large directory that has never been transferred would put every file in the tree into
    /// one pass, and each pass opens every file it holds.
    /// </remarks>
    private const int DrainLimit = 512;

    /// <param name="tracker">Decides when a file has stopped changing.</param>
    /// <param name="pollInterval">Cadence to start from after anything moves.</param>
    /// <param name="lockedFiles">
    /// How persistently to keep looking at a file another process holds open. Used by
    /// <see cref="WatchAsync"/> only: a manual scan has its own deadline, so
    /// <see cref="PumpAsync"/> keeps looking at its normal cadence until that expires.
    /// </param>
    /// <param name="log">Where to record what was given up on.</param>
    public ReadinessGate(
        FileStabilityTracker tracker,
        TimeSpan? pollInterval = null,
        LockedFilePolicy? lockedFiles = null,
        ILogger<ReadinessGate>? log = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _lockedFiles = lockedFiles ?? LockedFilePolicy.Default;
        _log = log ?? NullLogger<ReadinessGate>.Instance;
    }

    /// <summary>Doubles the wait, up to the ceiling.</summary>
    private static TimeSpan Slower(TimeSpan current, TimeSpan ceiling)
    {
        var doubled = current * 2;
        return doubled > ceiling ? ceiling : doubled;
    }

    /// <summary>
    /// Watches <paramref name="candidates"/> and calls <paramref name="onReady"/> for each one
    /// as it becomes safe to read.
    /// </summary>
    /// <param name="candidates">Files to consider.</param>
    /// <param name="onReady">Invoked once per file, when it settles.</param>
    /// <param name="onWaiting">
    /// Invoked each time a file is examined and found not ready, so the UI can explain the delay
    /// rather than appearing to have stalled.
    /// </param>
    /// <param name="giveUpAfter">
    /// How long to keep waiting overall. Null waits indefinitely, which is what continuous
    /// monitoring wants; a manual scan passes a bound so the run finishes.
    /// </param>
    /// <param name="cancellationToken">Stops the gate.</param>
    public async Task<GateOutcome> PumpAsync(
        IEnumerable<string> candidates,
        Func<string, Task> onReady,
        Action<string, FileReadiness>? onWaiting = null,
        TimeSpan? giveUpAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(onReady);

        var pending = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var released = new List<string>();
        var abandoned = new Dictionary<string, FileReadiness>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Dictionary<string, FileReadiness>(StringComparer.OrdinalIgnoreCase);

        var deadline = giveUpAfter is { } limit
            ? DateTimeOffset.UtcNow + limit
            : DateTimeOffset.MaxValue;

        var delay = _pollInterval;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var path in pending.ToArray())
            {
                var readiness = _tracker.Check(path);

                if (readiness.IsReady)
                {
                    pending.Remove(path);
                    waiting.Remove(path);
                    released.Add(path);

                    // Something moved, so go back to checking briskly: files usually finish in
                    // batches, and the next one is probably close behind.
                    delay = _pollInterval;

                    await onReady(path).ConfigureAwait(false);
                    continue;
                }

                if (!readiness.IsWorthRetrying)
                {
                    // Missing or unreadable: asking again will not change the answer.
                    pending.Remove(path);
                    waiting.Remove(path);
                    abandoned[path] = readiness;

                    _log.LogDebug("Giving up on {Path}: {Reason}", path, readiness.Detail);
                    onWaiting?.Invoke(path, readiness);
                    continue;
                }

                waiting[path] = readiness;
                onWaiting?.Invoke(path, readiness);
            }

            if (pending.Count == 0)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _log.LogInformation(
                    "Stopped waiting with {Count} file(s) still in use or being written.",
                    pending.Count);
                break;
            }

            // Back off while nothing is changing. An instrument holds its output open for an
            // entire run, so polling every second for half an hour means thousands of pointless
            // file opens on the disk the instrument is writing to. Backing off to a slower
            // cadence costs a few seconds of latency on a transfer that has already waited
            // minutes, and spares the machine the churn.
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Slower(delay, MaximumPollInterval);
        }

        return new GateOutcome(released, abandoned, waiting);
    }

    /// <summary>
    /// Watches a stream of candidates that stays open, releasing each file as it settles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs until <paramref name="candidates"/> is completed and everything accepted from it has
    /// been resolved, or until cancellation.
    /// </para>
    /// <para>
    /// With nothing pending this blocks on the channel rather than polling it, so an idle
    /// monitor performs no work at all -- no timer tick, no file access, nothing to wake the
    /// processor. That is the whole point on an instrument computer, where the application spends
    /// almost all of its life with nothing to do.
    /// </para>
    /// </remarks>
    /// <param name="candidates">Files to consider, as they are discovered.</param>
    /// <param name="onReady">Invoked once per file, when it settles.</param>
    /// <param name="onReport">
    /// Invoked every time a file is examined and found not ready, and once more when it stops
    /// being followed, so the UI can explain a delay rather than appearing to have stalled.
    /// </param>
    /// <param name="cancellationToken">Stops the gate.</param>
    public async Task WatchAsync(
        ChannelReader<string> candidates,
        Func<string, Task> onReady,
        Action<GateReport>? onReport = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(onReady);

        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Consecutive looks that found each file in use. Reset the moment it does anything, so a
        // file that is visibly progressing is never given up on however long it takes.
        var inUseFor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var delay = _pollInterval;
        var moreCandidatesComing = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var drained = 0;
                drained < DrainLimit && candidates.TryRead(out var candidate);
                drained++)
            {
                pending.Add(candidate);
            }

            if (pending.Count == 0)
            {
                if (!moreCandidatesComing)
                {
                    return;
                }

                // Nothing to watch. Block until something is offered; this is where an idle
                // monitor sits, costing nothing.
                if (!await candidates.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            foreach (var path in pending.ToArray())
            {
                var readiness = _tracker.Check(path);

                if (readiness.IsReady)
                {
                    pending.Remove(path);
                    inUseFor.Remove(path);

                    // Something moved, so go back to checking briskly: files usually finish in
                    // batches, and the next one is probably close behind.
                    delay = _pollInterval;

                    onReport?.Invoke(new GateReport(path, readiness, StillWatching: false));
                    await onReady(path).ConfigureAwait(false);
                    continue;
                }

                if (!readiness.IsWorthRetrying)
                {
                    pending.Remove(path);
                    inUseFor.Remove(path);

                    _log.LogDebug("Giving up on {Path}: {Reason}", path, readiness.Detail);
                    onReport?.Invoke(new GateReport(path, readiness, StillWatching: false));
                    continue;
                }

                if (readiness.Reason != ReadinessReason.Locked)
                {
                    // Growing or settling. The file is doing something, so patience starts over.
                    inUseFor.Remove(path);
                    onReport?.Invoke(new GateReport(path, readiness, StillWatching: true));
                    continue;
                }

                var checks = inUseFor.GetValueOrDefault(path) + 1;

                if (checks > _lockedFiles.MaxChecks)
                {
                    pending.Remove(path);
                    inUseFor.Remove(path);
                    _tracker.Forget(path);

                    _log.LogInformation(
                        "{Path} has been in use for {Checks} checks; handing it back to the "
                        + "periodic folder check.",
                        path,
                        checks - 1);

                    onReport?.Invoke(new GateReport(
                        path,
                        FileReadiness.StillInUse(readiness.Length, path, checks - 1),
                        StillWatching: false));
                    continue;
                }

                inUseFor[path] = checks;
                onReport?.Invoke(new GateReport(path, readiness, StillWatching: true));
            }

            if (pending.Count == 0)
            {
                continue;
            }

            moreCandidatesComing = await WaitAsync(
                candidates,
                delay,
                moreCandidatesComing,
                cancellationToken).ConfigureAwait(false);

            delay = Slower(delay, _lockedFiles.RecheckInterval);
        }
    }

    /// <summary>
    /// Waits for the polling interval, or for a new candidate, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Waking on arrival matters when the wait is long: a file deferred for half an hour has to
    /// come back the moment the watcher says it changed, not thirty minutes later.
    /// </remarks>
    /// <returns>False once the candidate stream is finished, so it is not waited on again.</returns>
    private static async Task<bool> WaitAsync(
        ChannelReader<string> candidates,
        TimeSpan wait,
        bool moreCandidatesComing,
        CancellationToken cancellationToken)
    {
        if (!moreCandidatesComing)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            return false;
        }

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var arrival = candidates.WaitToReadAsync(wake.Token).AsTask();
        var elapsed = Task.Delay(wait, wake.Token);

        var first = await Task.WhenAny(arrival, elapsed).ConfigureAwait(false);

        // Cancel whichever lost, then observe both so the loser's cancellation is not left as an
        // unobserved task fault.
        await wake.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(arrival, elapsed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: one of the two was cancelled on purpose.
        }

        cancellationToken.ThrowIfCancellationRequested();

        // False only when the channel completed, which is the one result that means no further
        // candidate will ever arrive.
        return first != arrival || arrival.Result;
    }
}
