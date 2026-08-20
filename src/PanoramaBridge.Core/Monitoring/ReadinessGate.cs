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
/// </remarks>
public sealed class ReadinessGate
{
    private readonly FileStabilityTracker _tracker;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<ReadinessGate> _log;

    /// <summary>
    /// Slowest cadence the gate backs off to while nothing is changing.
    /// </summary>
    /// <remarks>
    /// Thirty seconds against an acquisition that runs for half an hour is two checks a minute
    /// rather than sixty, and costs at most thirty seconds of latency once it finally finishes.
    /// </remarks>
    private static readonly TimeSpan MaximumPollInterval = TimeSpan.FromSeconds(30);

    public ReadinessGate(
        FileStabilityTracker tracker,
        TimeSpan? pollInterval = null,
        ILogger<ReadinessGate>? log = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _log = log ?? NullLogger<ReadinessGate>.Instance;
    }

    /// <summary>Doubles the wait, up to the ceiling.</summary>
    private static TimeSpan Slower(TimeSpan current)
    {
        var doubled = current * 2;
        return doubled > MaximumPollInterval ? MaximumPollInterval : doubled;
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
            delay = Slower(delay);
        }

        return new GateOutcome(released, abandoned, waiting);
    }
}
