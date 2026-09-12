using System.Net.Http;
using Microsoft.Extensions.Logging;
using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.App.Services;

/// <summary>What one configuration's sweep found, and which configuration that was.</summary>
/// <remarks>
/// The name travels with the result because several configurations sweep independently. Without
/// it the status line is set by whichever swept last, so a folder that cannot be read is cleared
/// from the window a moment later by a different folder that can.
/// </remarks>
/// <param name="Configuration">The configuration's display name.</param>
/// <param name="Result">What its walk of the folder found.</param>
public readonly record struct ConfigurationSweep(string Configuration, SweepResult Result);

/// <summary>
/// Everything one configuration needs in order to run: its connection, its engine and its
/// monitor.
/// </summary>
/// <remarks>
/// <para>
/// One of these per running configuration. The connection is borrowed rather than owned: a client
/// carries exactly one credential, so <see cref="WebDavClientCache"/> keys them by server and
/// sign-in and hands the same one to every configuration that matches. Two folders going to two
/// projects on one Panorama share a connection pool; the same server as a different account does
/// not. Nothing here disposes it, because the next configuration along is very likely using it.
/// </para>
/// <para>
/// What it deliberately does not own is the concurrency limit. That is a
/// <see cref="TransferBudget"/> handed in from outside and shared with every other runner,
/// because the limit describes the disk and the link rather than any one pairing.
/// </para>
/// </remarks>
public sealed class ConfigurationRunner : IAsyncDisposable
{
    private readonly ILogger<ConfigurationRunner> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IStateStore _store;

    private readonly IWebDavClient _client;
    private readonly TransferCoordinator _engine;
    private readonly ContinuousMonitor _monitor;

    private CancellationTokenSource? _running;
    private Task? _loop;
    private Task? _transfers;
    private bool _disposed;

    /// <param name="configuration">The pairing this runner serves.</param>
    /// <param name="settings">
    /// Application-level settings. Read for the things that describe this computer rather than
    /// the pairing: the extra root certificate, the SHA-256 record, the connection pool size.
    /// </param>
    /// <param name="client">
    /// The connection to this configuration's server, from <see cref="WebDavClientCache"/>.
    /// Borrowed rather than owned: it is shared with any other configuration signing in to the
    /// same server as the same account, and it outlives this runner. A runner that built its own
    /// meant a fresh connection pool, and a fresh TLS handshake per file, for every scan.
    /// </param>
    /// <param name="budget">The concurrency limit shared with every other runner.</param>
    public ConfigurationRunner(
        MonitoringConfiguration configuration,
        AppSettings settings,
        IWebDavClient client,
        IStateStore store,
        TransferBudget budget,
        ILoggerFactory loggerFactory)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(budget);

        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<ConfigurationRunner>();

        _engine = new TransferCoordinator(
            _client,
            store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = configuration.LocalDirectory,
                DestinationRoot = RemotePath.Parse(configuration.RemotePath),
                ConflictPolicy = configuration.ConflictPolicy,
                VerifyUploads = configuration.VerifyUploads,
                WriteChecksumSidecars = configuration.WriteChecksumSidecars,

                // Workers, not the limit. How many files may actually move at once is the
                // budget's business, and it is shared with every other runner.
                MaxConcurrentTransfers = budget.Capacity,
                Budget = budget,
            },
            log: loggerFactory.CreateLogger<TransferCoordinator>());

        _monitor = new ContinuousMonitor(
            store,
            MonitorOptions.FromConfiguration(configuration),
            loggerFactory);
    }

    /// <summary>The pairing this runner serves.</summary>
    public MonitoringConfiguration Configuration { get; }

    /// <summary>What to call this configuration in a message.</summary>
    public string Name => Configuration.DisplayName;

    /// <summary>True between a successful start and a stop.</summary>
    public bool IsRunning => _running is { IsCancellationRequested: false };

    /// <summary>What this configuration's monitor is doing.</summary>
    public MonitorStatus Status => _monitor.Status;

    /// <summary>The connection this configuration is using. Not owned by this runner.</summary>
    public IWebDavClient Client => _client;

    /// <summary>Raised as this configuration's transfers progress. Fires on worker threads.</summary>
    public event Action<TransferProgress>? Progress;

    /// <summary>Raised after each walk of this configuration's folder.</summary>
    public event Action<ConfigurationSweep>? Swept;

    /// <summary>Raised whenever a file is examined and found not ready to read.</summary>
    public event Action<GateReport>? Waiting;

    /// <summary>Raised when this configuration stops for a reason nobody asked for.</summary>
    /// <remarks>
    /// Carries the runner as well as the reason. The name is needed because with several running
    /// "Monitoring stopped" does not say which folder is no longer being watched, and the runner
    /// itself is needed because whoever is holding the set has to stop counting this one -- the
    /// others keep sweeping and would otherwise keep the window looking healthy.
    /// </remarks>
    public event Action<ConfigurationRunner, string>? Failed;

    /// <summary>
    /// Recovers anything interrupted, then starts watching.
    /// </summary>
    /// <remarks>
    /// Recovery can start worker tasks and then fail partway through the ledger. Left unhandled
    /// that leaves workers blocked reading a queue nothing will ever complete, so a failure here
    /// tears down what it started before rethrowing.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return;
        }

        var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await _engine.RecoverInterruptedAsync(running.Token).ConfigureAwait(false);
        }
        catch
        {
            running.Dispose();
            throw;
        }

        _engine.Progress += OnProgress;
        _monitor.Swept += OnSwept;
        _monitor.Waiting += OnWaiting;

        _running = running;
        _transfers = _engine.RunAsync(running.Token);
        _loop = WatchAsync(running);

        _log.LogInformation(
            "{Configuration}: monitoring {Root} into {Destination} on {Server}, re-checking every "
            + "{Minutes} minute(s).",
            Name,
            Configuration.LocalDirectory,
            Configuration.RemotePath,
            Configuration.ServerUrl,
            Configuration.ReconcileMinutes);
    }

    /// <summary>Stops this configuration and waits for its engine to wind down.</summary>
    public async Task StopAsync()
    {
        var running = _running;

        if (running is null)
        {
            return;
        }

        await running.CancelAsync().ConfigureAwait(false);
        await AwaitQuietlyAsync(_loop).ConfigureAwait(false);

        _engine.CompleteAdding();
        await AwaitQuietlyAsync(_transfers).ConfigureAwait(false);

        _engine.Progress -= OnProgress;
        _monitor.Swept -= OnSwept;
        _monitor.Waiting -= OnWaiting;

        _running = null;
        _loop = null;
        _transfers = null;
        running.Dispose();

        _log.LogInformation("{Configuration}: monitoring stopped.", Name);
    }

    /// <summary>Asks this configuration to walk its folder now rather than at its next turn.</summary>
    public void RequestSweep(string reason) => _monitor.RequestSweep(reason);

    /// <summary>
    /// Walks this configuration's folder once and transfers what needs transferring.
    /// </summary>
    /// <remarks>
    /// What the Upload now button does, for one configuration. Everything found still goes
    /// through the readiness gate: pressing a button must not be a way round the rule that a
    /// partially written file is never uploaded, and during an acquisition is exactly when
    /// somebody would press it.
    /// </remarks>
    /// <param name="patience">
    /// How long to wait for a file something else is still writing. A bound is needed because the
    /// user is standing there watching; continuous monitoring, which nobody is waiting on, keeps
    /// looking indefinitely instead.
    /// </param>
    public async Task<TransferSummary> ScanOnceAsync(
        TimeSpan patience,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _engine.Progress += OnProgress;

        try
        {
            await _engine.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);

            // Started before anything is offered, so a file that settles early begins moving
            // while the rest of the folder is still being walked.
            var transfers = _engine.RunAsync(cancellationToken);

            var options = MonitorOptions.FromConfiguration(Configuration);

            var scanner = new ReconciliationScanner(
                _store,
                new ReconciliationOptions
                {
                    Root = options.Root,
                    DestinationRoot = options.DestinationRoot,
                    Filter = options.Filter,
                    IncludeSubdirectories = options.IncludeSubdirectories,
                    ConflictPolicy = options.ConflictPolicy,
                    MaxUploadAttempts = options.MaxUploadAttempts,
                },
                _loggerFactory.CreateLogger<ReconciliationScanner>());

            var candidates = new List<string>();

            var sweep = await scanner
                .SweepAsync(
                    (path, _) =>
                    {
                        candidates.Add(path);
                        return Task.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (sweep.Failed)
            {
                // Named, because with several configurations scanning at once "the folder could
                // not be read" does not say which folder.
                throw new InvalidOperationException($"{Name}: {sweep.Problem}");
            }

            var gate = new ReadinessGate(
                new FileStabilityTracker(options.StabilityPeriod),
                lockedFiles: LockedFilePolicy.None,
                log: _loggerFactory.CreateLogger<ReadinessGate>());

            var outcome = await gate
                .PumpAsync(
                    candidates,
                    path => _engine.EnqueueAsync(path, cancellationToken),
                    onWaiting: (path, readiness) =>
                        Waiting?.Invoke(new GateReport(path, readiness, readiness.IsWorthRetrying)),
                    giveUpAfter: patience,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _engine.CompleteAdding();

            _log.LogInformation(
                "{Configuration}: offered {Count} of {Examined} file(s); {Settled} were already on "
                + "the server and {Waiting} were still in use.",
                Name,
                outcome.Released.Count,
                sweep.Examined,
                sweep.AlreadyAccountedFor,
                outcome.StillWaiting.Count);

            var summary = await transfers.ConfigureAwait(false);

            // Files the ledger settled never reached the engine, so they are not in its counts.
            // They were still skipped, and saying so is what keeps "nothing needed transferring"
            // distinguishable from "there was nothing there".
            return summary with { Skipped = summary.Skipped + sweep.AlreadyAccountedFor };
        }
        finally
        {
            _engine.Progress -= OnProgress;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        await _monitor.DisposeAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);

        // The client is not disposed here. It belongs to the cache and is very likely shared with
        // another configuration on the same server; closing its pool when one runner stops would
        // take the others' connections with it.
    }

    /// <summary>
    /// Cancels without waiting, for a process on its way out.
    /// </summary>
    /// <remarks>
    /// An abandoned upload is a case the design already covers: every state change is written to
    /// the ledger before the action it describes, so the next run finds the row still marked
    /// Uploading and offers the file again. Waiting for a multi-gigabyte upload to notice would
    /// only hold the window open.
    /// </remarks>
    public void Abandon()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _running?.Cancel();
        _running?.Dispose();
        _running = null;

        _monitor.Dispose();
    }

    /// <summary>
    /// Runs the monitor, and stands down visibly if it ever stops for a reason nobody asked for.
    /// </summary>
    /// <remarks>
    /// Monitoring that has quietly died is worse than monitoring that never started: the window
    /// would go on saying the folder was being watched, and an instrument would fill it up
    /// unnoticed. With several configurations this matters more rather than less -- the others
    /// keep sweeping and keep the window looking healthy.
    /// </remarks>
    private async Task WatchAsync(CancellationTokenSource running)
    {
        try
        {
            await _monitor
                .RunAsync(path => _engine.EnqueueAsync(path, running.Token), running.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Asked to stop.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Configuration}: monitoring stopped unexpectedly.", Name);

            // Cancelled before the event, so IsRunning is already false by the time anybody asks.
            // This is a linked child of the token the service holds, and cancelling a child does
            // not cancel its parent -- which is exactly why the service has to be told rather
            // than left to notice.
            await running.CancelAsync().ConfigureAwait(false);

            Failed?.Invoke(
                this,
                $"Monitoring stopped. {ex.Message} Start it again once the cause is dealt with.");
        }
    }

    private async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // How a canceled run is supposed to end.
        }
        catch (Exception ex)
        {
            // Already stopping. Leaving this half torn down would be worse than the failure
            // itself, so record it and carry on.
            _log.LogWarning(ex, "{Configuration}: failed while stopping.", Name);
        }
    }

    /// <summary>
    /// Passes a progress report up, saying which configuration it came from.
    /// </summary>
    /// <remarks>
    /// Tagged here because this is the last place that knows. Once several configurations report
    /// into one transfer table, a row without this is a file moving with nothing to say which
    /// instrument it came from or where it is going -- and with overlapping folders allowed, the
    /// path alone cannot answer that.
    /// </remarks>
    private void OnProgress(TransferProgress progress) =>
        Progress?.Invoke(progress with { Configuration = Name });

    private void OnSwept(SweepResult result) => Swept?.Invoke(new ConfigurationSweep(Name, result));

    private void OnWaiting(GateReport report) => Waiting?.Invoke(report);
}
