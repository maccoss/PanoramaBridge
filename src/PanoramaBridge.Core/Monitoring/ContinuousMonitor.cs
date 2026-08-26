using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>Everything continuous monitoring needs to know.</summary>
public sealed record MonitorOptions
{
    /// <summary>Directory watched, and the base the remote structure mirrors.</summary>
    public required string Root { get; init; }

    /// <summary>Remote folder the structure is mirrored into.</summary>
    public required RemotePath DestinationRoot { get; init; }

    /// <summary>Which files count as data worth transferring.</summary>
    public required CandidateFilter Filter { get; init; }

    /// <summary>Whether to watch the tree below the root.</summary>
    public bool IncludeSubdirectories { get; init; } = true;

    /// <summary>What to do when the destination is occupied.</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    /// <summary>How long a file must be unchanged before it is considered finished.</summary>
    public TimeSpan StabilityPeriod { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often to walk the whole tree.</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How persistently to keep looking at a file another process holds open.</summary>
    public LockedFilePolicy LockedFiles { get; init; } = LockedFilePolicy.Default;

    /// <summary>How many failed attempts before the sweep stops offering a file.</summary>
    public int MaxUploadAttempts { get; init; } = 5;

    /// <summary>Reads the settings the user actually edits.</summary>
    public static MonitorOptions FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new MonitorOptions
        {
            Root = settings.LocalDirectory,
            DestinationRoot = RemotePath.Parse(settings.RemotePath),
            Filter = new CandidateFilter(settings.Extensions),
            IncludeSubdirectories = settings.IncludeSubdirectories,
            ConflictPolicy = settings.ConflictPolicy,
            StabilityPeriod = TimeSpan.FromSeconds(Math.Max(0, settings.StabilitySeconds)),

            // A zero or negative interval would turn the safety net into a busy loop. One minute
            // is the floor, which is still far more often than anything here needs.
            ReconcileInterval = TimeSpan.FromMinutes(Math.Max(1, settings.ReconcileMinutes)),

            LockedFiles = new LockedFilePolicy(
                TimeSpan.FromSeconds(Math.Max(1, settings.LockedFileRetryIntervalSeconds)),
                Math.Max(1, settings.LockedFileMaxRetries)),
        };
    }
}

/// <summary>What the monitor is doing, for the status line.</summary>
/// <param name="WatchingForChanges">
/// Whether file system notifications are being delivered. False is not a failure: the sweep
/// finds everything on its own, just less promptly.
/// </param>
/// <param name="Settling">Files being watched because they are not safe to read yet.</param>
/// <param name="LastSweep">What the most recent walk of the tree found.</param>
public readonly record struct MonitorStatus(
    bool WatchingForChanges,
    int Settling,
    SweepResult? LastSweep);

/// <summary>
/// Watches a directory and feeds settled files to the transfer engine.
/// </summary>
/// <remarks>
/// <para>
/// Two ways in, deliberately unequal. The periodic sweep is the mechanism and is always on;
/// change notifications are an accelerator that is allowed to fail silently. Building it the
/// other way round -- notifications as the mechanism, a sweep as an option someone has to know to
/// enable -- is how the previous implementation lost files under load with nothing in the log to
/// say so.
/// </para>
/// <para>
/// Both feed <see cref="ReadinessGate"/> rather than the transfer engine, so the rule that
/// nothing partial is ever uploaded holds regardless of which path found the file.
/// </para>
/// <para>
/// While idle this costs one timer at the reconciliation interval, and nothing else: the gate
/// blocks on an empty channel instead of polling, and duplicate suppression in the watcher is a
/// comparison rather than a window that has to be waited out.
/// </para>
/// </remarks>
public sealed class ContinuousMonitor : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Candidates held between the finders and the gate.
    /// </summary>
    /// <remarks>
    /// Bounded, so pointing the application at a directory of two hundred thousand files applies
    /// backpressure to the sweep instead of building the whole list in memory. A notification
    /// that cannot be written is dropped rather than waited on -- notifications are expendable by
    /// design, and blocking a file system callback would be far worse than losing one.
    /// </remarks>
    private const int QueueCapacity = 2000;

    private readonly MonitorOptions _options;
    private readonly ReconciliationScanner _scanner;
    private readonly DirectoryMonitor _watcher;
    private readonly ReadinessGate _gate;
    private readonly ILogger<ContinuousMonitor> _log;

    private readonly Channel<string> _candidates =
        Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Signals a sweep that should not wait for its turn. Coalesces repeats.</summary>
    private readonly SemaphoreSlim _sweepNow = new(0, 1);

    private readonly ConcurrentDictionary<string, FileReadiness> _settling =
        new(StringComparer.OrdinalIgnoreCase);

    private SweepResult? _lastSweep;
    private bool _disposed;

    public ContinuousMonitor(
        IStateStore store,
        MonitorOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _options = options ?? throw new ArgumentNullException(nameof(options));

        var loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _log = loggers.CreateLogger<ContinuousMonitor>();

        _scanner = new ReconciliationScanner(
            store,
            new ReconciliationOptions
            {
                Root = options.Root,
                DestinationRoot = options.DestinationRoot,
                Filter = options.Filter,
                IncludeSubdirectories = options.IncludeSubdirectories,
                ConflictPolicy = options.ConflictPolicy,
                MaxUploadAttempts = options.MaxUploadAttempts,
            },
            loggers.CreateLogger<ReconciliationScanner>());

        _watcher = new DirectoryMonitor(
            options.Root,
            options.IncludeSubdirectories,
            options.Filter,
            log: loggers.CreateLogger<DirectoryMonitor>());

        _gate = new ReadinessGate(
            new FileStabilityTracker(options.StabilityPeriod),
            pollInterval: TimeSpan.FromSeconds(1),
            lockedFiles: options.LockedFiles,
            log: loggers.CreateLogger<ReadinessGate>());

        _watcher.Changed += OnFileChanged;
        _watcher.SweepRequested += OnSweepRequested;
    }

    /// <summary>Raised after each walk of the tree.</summary>
    public event Action<SweepResult>? Swept;

    /// <summary>Raised whenever a file is examined and is not ready to be read.</summary>
    public event Action<GateReport>? Waiting;

    /// <summary>What the monitor is doing now.</summary>
    public MonitorStatus Status =>
        new(_watcher.IsWatching, _settling.Count, _lastSweep);

    /// <summary>Files not ready to be read yet, with the reason for each.</summary>
    public IReadOnlyDictionary<string, FileReadiness> Settling => _settling;

    /// <summary>
    /// Asks for a walk of the tree without waiting for the next scheduled one.
    /// </summary>
    /// <remarks>
    /// What the Upload now button does while monitoring is running: a full sweep is exactly the
    /// work it would otherwise start, and running two scans over the same folder at once is not
    /// something the user gains anything from.
    /// </remarks>
    public void RequestSweep(string reason)
    {
        _log.LogDebug("A folder check was requested: {Reason}", reason);

        try
        {
            _sweepNow.Release();
        }
        catch (SemaphoreFullException)
        {
            // One is already pending, and two sweeps back to back would find the same thing.
        }
    }

    /// <summary>
    /// Runs until cancelled, handing each file to <paramref name="onReady"/> once it is safe to
    /// read.
    /// </summary>
    /// <remarks>
    /// The sweep and the gate run alongside each other rather than in turn, so a file that
    /// settles is queued immediately instead of waiting for the walk it was found by to finish.
    /// </remarks>
    public async Task RunAsync(Func<string, Task> onReady, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onReady);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _watcher.Start();

        var gate = Task.Run(
            () => _gate.WatchAsync(_candidates.Reader, Release(onReady), OnReport, cancellationToken),
            CancellationToken.None);

        try
        {
            await SweepLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Asked to stop.
        }
        finally
        {
            _watcher.Stop();
            _candidates.Writer.TryComplete();

            try
            {
                await gate.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Asked to stop.
            }
        }
    }

    /// <summary>Wraps the caller's handler so a released file stops being counted as settling.</summary>
    private Func<string, Task> Release(Func<string, Task> onReady) =>
        path =>
        {
            _settling.TryRemove(path, out _);
            _log.LogDebug("{Path} has settled; handing it to the transfer engine.", path);
            return onReady(path);
        };

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Re-arming here rather than only at startup is what recovers from a share that was
            // unreachable when monitoring began. It costs one call against a live watch.
            if (!_watcher.IsWatching)
            {
                _watcher.Start();
            }

            var result = await _scanner
                .SweepAsync(OfferSwept, cancellationToken)
                .ConfigureAwait(false);

            _lastSweep = result;

            if (result.Failed)
            {
                _log.LogWarning("The folder check could not run: {Problem}", result.Problem);
            }

            Raise(result);

            // The only recurring wait in the whole monitor. Everything else wakes on an event.
            await _sweepNow.WaitAsync(_options.ReconcileInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hands a swept file to the gate, waiting if the queue is full.
    /// </summary>
    /// <remarks>
    /// Waiting is the point: it is what makes a first sweep of a directory of two hundred
    /// thousand files apply backpressure rather than build the whole list in memory. The stamp
    /// the walk read is not needed here -- the gate reads the length from an open handle, which
    /// is the only reading it is allowed to trust.
    /// </remarks>
    private Task OfferSwept(string path, LocalFileStamp stamp) =>
        _candidates.Writer.WriteAsync(path, CancellationToken.None).AsTask();

    private void OnFileChanged(string path)
    {
        // TryWrite, not WriteAsync: this runs on the file system watcher's callback, and blocking
        // there is what makes the kernel buffer overflow and lose events for every other file.
        // A notification dropped because the queue is full costs nothing, because the sweep finds
        // the file anyway.
        if (!_candidates.Writer.TryWrite(path))
        {
            _log.LogDebug(
                "The queue was full, so the change to {Path} waits for the next folder check.",
                path);
        }
    }

    private void OnSweepRequested(SweepRequest request) => RequestSweep(request.Reason);

    private void OnReport(GateReport report)
    {
        if (report.StillWatching)
        {
            _settling[report.Path] = report.Readiness;
        }
        else
        {
            _settling.TryRemove(report.Path, out _);
        }

        try
        {
            Waiting?.Invoke(report);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A readiness report could not be delivered.");
        }
    }

    private void Raise(SweepResult result)
    {
        try
        {
            Swept?.Invoke(result);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A sweep result could not be delivered.");
        }
    }

    /// <summary>
    /// Releases the watch and the queue.
    /// </summary>
    /// <remarks>
    /// Nothing here is asynchronous, so both interfaces are offered and both do the same work.
    /// A container disposed synchronously refuses a service that only implements
    /// <see cref="IAsyncDisposable"/>, which is a startling way to find out about it.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _watcher.Changed -= OnFileChanged;
        _watcher.SweepRequested -= OnSweepRequested;
        _watcher.Dispose();

        _candidates.Writer.TryComplete();
        _sweepNow.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
