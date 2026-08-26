using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Security;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.App.Services;

/// <summary>Result of testing a connection, phrased for display.</summary>
/// <param name="Succeeded">Whether the server answered and accepted the credential.</param>
/// <param name="Summary">One line describing the outcome.</param>
/// <param name="Detail">Extra information, such as the destination's permissions.</param>
/// <param name="CanUploadToDestination">
/// Whether the configured destination accepts uploads. Knowing this before a six-hour transfer
/// starts is the whole reason the check exists.
/// </param>
public readonly record struct ConnectionCheck(
    bool Succeeded,
    string Summary,
    string? Detail = null,
    bool CanUploadToDestination = false);

/// <summary>
/// Owns the transfer engine on behalf of the UI.
/// </summary>
/// <remarks>
/// The view models talk to this and never to the WebDAV client or the ledger directly, so all
/// the awkward lifetime questions -- when the HTTP client is rebuilt, when a run can be
/// cancelled, which credential is in force -- live in one place.
/// </remarks>
public sealed class TransferService : IAsyncDisposable, IDisposable
{
    private readonly IStateStore _store;
    private readonly ICredentialStore _credentials;
    private readonly ResourceGovernor _governor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TransferService> _log;

    private HttpClient? _http;
    private WebDavClient? _client;
    private string? _connectedTo;
    private CancellationTokenSource? _run;

    private ContinuousMonitor? _monitor;
    private TransferCoordinator? _monitorEngine;
    private CancellationTokenSource? _monitoring;
    private Task? _monitorLoop;
    private Task? _monitorTransfers;
    private bool _disposed;

    public TransferService(
        IStateStore store,
        ICredentialStore credentials,
        ResourceGovernor governor,
        ILoggerFactory loggerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<TransferService>();
    }

    /// <summary>Collects progress for the UI to drain on its own schedule.</summary>
    public TransferProgressAggregator Progress { get; } = new();

    /// <summary>True while a scan or transfer run is in flight.</summary>
    public bool IsRunning => _run is { IsCancellationRequested: false };

    /// <summary>True while the monitored folder is being watched.</summary>
    public bool IsMonitoring => _monitoring is { IsCancellationRequested: false };

    /// <summary>
    /// True while any file has bytes moving, however that transfer was started.
    /// </summary>
    /// <remarks>
    /// <see cref="IsRunning"/> is not this. It tracks <c>_run</c>, which only a manual scan
    /// creates; a file uploaded by monitoring leaves it null, so asking <see cref="IsRunning"/>
    /// "is a transfer in progress" answers no for the ordinary case -- an unattended machine
    /// uploading an acquisition. Both paths report into <see cref="Progress"/>, which is why
    /// this asks that instead.
    ///
    /// Only <see cref="TransferState.Uploading"/> counts. Uploaded-but-unverified is deliberately
    /// excluded: its bytes are already on the server and the next sweep will confirm them, and
    /// with verification turned off a file can rest in that state, which would leave this stuck
    /// true forever.
    /// </remarks>
    public bool HasTransferInFlight =>
        Progress.Snapshot().Any(p => p.State == TransferState.Uploading);

    /// <summary>What monitoring is doing, or null when it is not running.</summary>
    public MonitorStatus? Monitor => _monitor?.Status;

    /// <summary>Raised when a run starts or finishes, so commands can re-evaluate.</summary>
    public event Action? RunStateChanged;

    /// <summary>Raised after each walk of the monitored folder.</summary>
    public event Action<SweepResult>? Swept;

    /// <summary>Raised whenever a file is examined and found not ready to read.</summary>
    public event Action<GateReport>? Waiting;

    /// <summary>Raised when monitoring stops for a reason nobody asked for.</summary>
    public event Action<string>? MonitoringFailed;

    /// <summary>
    /// Builds the client for the given settings and confirms the server accepts it.
    /// </summary>
    /// <remarks>
    /// Reports whether the chosen destination is writable, rather than letting the user discover
    /// a permissions problem hours into a transfer.
    /// </remarks>
    public async Task<ConnectionCheck> TestConnectionAsync(
        AppSettings settings,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            return new ConnectionCheck(false, problems[0]);
        }

        try
        {
            var credential = ResolveCredential(settings, secret);
            if (credential is null)
            {
                return new ConnectionCheck(
                    false,
                    settings.AuthMode == AuthMode.ApiKey
                        ? "Enter an API key, or generate one from Panorama's External Tool Access page."
                        : "Enter your Panorama password.");
            }

            Connect(settings, credential);

            var destination = RemotePath.Parse(settings.RemotePath);
            var capabilities = await _client!
                .GetCapabilitiesAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            // Listing the parent tells us the permissions on the destination itself.
            var siblings = await _client
                .ListAsync(destination.Parent, cancellationToken)
                .ConfigureAwait(false);

            var folder = siblings.FirstOrDefault(r =>
                r.IsCollection && string.Equals(r.Name, destination.Name, StringComparison.Ordinal));

            var writable = folder?.Permissions.CanUpload ?? capabilities.Allows("PUT");

            var detail = folder is null
                ? $"{settings.RemotePath} does not exist yet; it will be created on the first upload."
                : writable
                    ? $"You can upload to {settings.RemotePath}."
                    : $"{settings.RemotePath} is read-only for this account. A Panorama "
                      + "administrator needs to grant write access.";

            return new ConnectionCheck(
                true,
                $"Connected to {capabilities.ServerName ?? settings.ServerUrl}.",
                detail,
                writable);
        }
        catch (WebDavException ex)
        {
            _log.LogWarning(ex, "Connection test failed.");
            return new ConnectionCheck(false, ex.ToUserMessage());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Connection test failed.");
            return new ConnectionCheck(false, $"Could not reach {settings.ServerUrl}: {ex.Message}");
        }
    }

    /// <summary>
    /// How long a manual scan waits for a file something else is still writing.
    /// </summary>
    /// <remarks>
    /// A bound is needed because the user is standing there watching. Anything still in use when
    /// it expires is reported and left; continuous monitoring, which nobody is waiting on, keeps
    /// looking indefinitely instead.
    /// </remarks>
    private static readonly TimeSpan ManualScanPatience = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Walks the monitored directory once and transfers whatever needs transferring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan runs on a background task. The equivalent in the Python version ran on the UI
    /// thread and hashed every file it found, so pointing it at a populated directory froze the
    /// window for minutes.
    /// </para>
    /// <para>
    /// Everything found goes through the readiness gate, exactly as it does under continuous
    /// monitoring. Pressing a button must not be a way round the rule that a partially written
    /// file is never uploaded -- and during an acquisition is precisely when someone would press
    /// it.
    /// </para>
    /// </remarks>
    public async Task<TransferSummary> ScanAndUploadAsync(
        AppSettings settings,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (IsRunning)
        {
            throw new InvalidOperationException("A transfer run is already in progress.");
        }

        if (IsMonitoring)
        {
            throw new InvalidOperationException(
                "The folder is being monitored; ask for a check rather than starting a second scan.");
        }

        var credential = ResolveCredential(settings, secret)
            ?? throw new InvalidOperationException("No credential is available for this server.");

        Connect(settings, credential);

        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RunStateChanged?.Invoke();

        try
        {
            await using var coordinator = NewCoordinator(settings);

            await coordinator.RecoverInterruptedAsync(_run.Token).ConfigureAwait(false);

            // Started before anything is offered, so a file that settles early begins moving
            // while the rest of the folder is still being walked.
            var transfers = coordinator.RunAsync(_run.Token);

            var monitorOptions = MonitorOptions.FromSettings(settings);

            var scanner = new ReconciliationScanner(
                _store,
                new ReconciliationOptions
                {
                    Root = monitorOptions.Root,
                    DestinationRoot = monitorOptions.DestinationRoot,
                    Filter = monitorOptions.Filter,
                    IncludeSubdirectories = monitorOptions.IncludeSubdirectories,
                    ConflictPolicy = monitorOptions.ConflictPolicy,
                    MaxUploadAttempts = monitorOptions.MaxUploadAttempts,
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
                    _run.Token)
                .ConfigureAwait(false);

            if (sweep.Failed)
            {
                throw new InvalidOperationException(sweep.Problem);
            }

            var gate = new ReadinessGate(
                new FileStabilityTracker(monitorOptions.StabilityPeriod),
                lockedFiles: LockedFilePolicy.None,
                log: _loggerFactory.CreateLogger<ReadinessGate>());

            var outcome = await gate
                .PumpAsync(
                    candidates,
                    path => coordinator.EnqueueAsync(path, _run.Token),
                    onWaiting: (path, readiness) =>
                        Waiting?.Invoke(new GateReport(path, readiness, readiness.IsWorthRetrying)),
                    giveUpAfter: ManualScanPatience,
                    cancellationToken: _run.Token)
                .ConfigureAwait(false);

            coordinator.CompleteAdding();

            _log.LogInformation(
                "Offered {Count} of {Examined} file(s); {Settled} were already on the server and "
                + "{Waiting} were still in use.",
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
            _run.Dispose();
            _run = null;
            RunStateChanged?.Invoke();

            // Hand back what the transfer needed. An idle monitor on an instrument computer
            // should not sit on memory the acquisition software may want.
            _governor.ReleaseIdleMemory();
        }
    }

    /// <summary>Stops the current manual run. In-flight uploads are abandoned, not corrupted.</summary>
    public void Cancel() => _run?.Cancel();

    /// <summary>
    /// Starts watching the monitored folder, transferring files as they finish being written.
    /// </summary>
    /// <remarks>
    /// The engine is started once and left running, so its workers block on an empty queue rather
    /// than being torn down and rebuilt around every file.
    /// </remarks>
    public async Task StartMonitoringAsync(
        AppSettings settings,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (IsMonitoring)
        {
            return;
        }

        if (IsRunning)
        {
            throw new InvalidOperationException(
                "A scan is already running. Wait for it to finish before starting monitoring.");
        }

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(problems[0]);
        }

        var credential = ResolveCredential(settings, secret)
            ?? throw new InvalidOperationException("No credential is available for this server.");

        Connect(settings, credential);

        var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var engine = NewCoordinator(settings);

        await engine.RecoverInterruptedAsync(monitoring.Token).ConfigureAwait(false);

        var monitor = new ContinuousMonitor(
            _store,
            MonitorOptions.FromSettings(settings),
            _loggerFactory);

        monitor.Swept += OnSwept;
        monitor.Waiting += OnWaiting;

        _monitoring = monitoring;
        _monitorEngine = engine;
        _monitor = monitor;

        _monitorTransfers = engine.RunAsync(monitoring.Token);
        _monitorLoop = WatchAsync(monitor, engine, monitoring);

        _log.LogInformation(
            "Monitoring {Root} into {Destination}, re-checking every {Minutes} minute(s).",
            settings.LocalDirectory,
            settings.RemotePath,
            settings.ReconcileMinutes);

        RunStateChanged?.Invoke();
    }

    /// <summary>Stops monitoring and waits for the engine to wind down.</summary>
    public async Task StopMonitoringAsync()
    {
        var monitoring = _monitoring;

        if (monitoring is null)
        {
            return;
        }

        await monitoring.CancelAsync().ConfigureAwait(false);
        await AwaitQuietlyAsync(_monitorLoop).ConfigureAwait(false);

        _monitorEngine?.CompleteAdding();
        await AwaitQuietlyAsync(_monitorTransfers).ConfigureAwait(false);

        if (_monitor is not null)
        {
            _monitor.Swept -= OnSwept;
            _monitor.Waiting -= OnWaiting;
            await _monitor.DisposeAsync().ConfigureAwait(false);
        }

        if (_monitorEngine is not null)
        {
            await _monitorEngine.DisposeAsync().ConfigureAwait(false);
        }

        _monitor = null;
        _monitorEngine = null;
        _monitorLoop = null;
        _monitorTransfers = null;
        _monitoring = null;
        monitoring.Dispose();

        _log.LogInformation("Monitoring stopped.");

        RunStateChanged?.Invoke();
        _governor.ReleaseIdleMemory();
    }

    /// <summary>
    /// Asks monitoring to walk the folder now rather than at its next turn.
    /// </summary>
    /// <returns>False when nothing is being monitored, so the caller can scan instead.</returns>
    public bool RequestSweep(string reason)
    {
        if (_monitor is null)
        {
            return false;
        }

        _monitor.RequestSweep(reason);
        return true;
    }

    /// <summary>
    /// Runs the monitor, and stands down visibly if it ever stops for a reason nobody asked for.
    /// </summary>
    /// <remarks>
    /// Monitoring that has quietly died is worse than monitoring that never started: the window
    /// would go on saying it was watching the folder, and an instrument would fill it up
    /// unnoticed. Any unexpected failure cancels the run so the button goes back to "Start
    /// monitoring" and the status line says what happened.
    /// </remarks>
    private async Task WatchAsync(
        ContinuousMonitor monitor,
        TransferCoordinator engine,
        CancellationTokenSource monitoring)
    {
        try
        {
            await monitor
                .RunAsync(path => engine.EnqueueAsync(path, monitoring.Token), monitoring.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Asked to stop.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Monitoring stopped unexpectedly.");

            await monitoring.CancelAsync().ConfigureAwait(false);

            MonitoringFailed?.Invoke(
                $"Monitoring stopped: {ex.Message} Start it again once the cause is dealt with.");

            RunStateChanged?.Invoke();
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
            // How a cancelled run is supposed to end.
        }
        catch (Exception ex)
        {
            // Already stopping. Leaving the service half torn down would be worse than the
            // failure itself, so record it and carry on shutting down.
            _log.LogWarning(ex, "Something failed while monitoring was being stopped.");
        }
    }

    private void OnSwept(SweepResult result) => Swept?.Invoke(result);

    /// <summary>
    /// Puts a file that is not ready yet into the transfer list, with the reason.
    /// </summary>
    /// <remarks>
    /// Without this the window shows nothing at all while an acquisition is being written, which
    /// on a run lasting an hour is indistinguishable from monitoring having stopped working. The
    /// aggregator already counts <see cref="TransferState.Discovered"/> and
    /// <see cref="TransferState.LockedRetrying"/> as queued, so these rows need no special
    /// handling further up.
    /// <para>
    /// A file that has simply gone is not reported. It is usually a working name that was
    /// renamed into place a moment later, and a row saying so would be noise rather than news.
    /// A file that cannot be read is reported, because that is a permissions problem somebody has
    /// to fix.
    /// </para>
    /// </remarks>
    private void OnWaiting(GateReport report)
    {
        var readiness = report.Readiness;

        var state = readiness.Reason switch
        {
            ReadinessReason.Locked => TransferState.LockedRetrying,
            ReadinessReason.Unreadable => TransferState.Failed,
            _ => TransferState.Discovered,
        };

        // A file that has gone is not worth a row in the transfer list. It is usually a working
        // name that was renamed into place a moment later.
        if (readiness.Reason != ReadinessReason.Missing)
        {
            Progress.Report(new TransferProgress(
                report.Path,
                string.Empty,
                state,
                state == TransferState.Failed ? "Cannot read" : "Waiting",
                0,
                readiness.Length,
                Message: readiness.Detail));
        }

        Waiting?.Invoke(report);
    }

    private TransferCoordinator NewCoordinator(AppSettings settings)
    {
        var coordinator = new TransferCoordinator(
            _client!,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = settings.LocalDirectory,
                DestinationRoot = RemotePath.Parse(settings.RemotePath),
                MaxConcurrentTransfers = settings.MaxConcurrentTransfers,
                ConflictPolicy = settings.ConflictPolicy,
                VerifyUploads = settings.VerifyUploads,
                WriteChecksumSidecars = settings.WriteChecksumSidecars,
            },
            log: _loggerFactory.CreateLogger<TransferCoordinator>());

        coordinator.Progress += Progress.Report;
        return coordinator;
    }

    private PanoramaCredential? ResolveCredential(AppSettings settings, string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret))
        {
            return settings.AuthMode == AuthMode.ApiKey
                ? PanoramaCredential.ApiKey(secret)
                : PanoramaCredential.UserNameAndPassword(settings.UserName, secret);
        }

        // Nothing typed this session, so fall back to what was saved.
        var stored = _credentials.Read(settings.ServerUrl);
        if (stored is null)
        {
            return null;
        }

        return settings.AuthMode == AuthMode.ApiKey
            ? PanoramaCredential.ApiKey(stored.Value.Secret)
            : PanoramaCredential.UserNameAndPassword(stored.Value.UserName, stored.Value.Secret);
    }

    /// <summary>
    /// Rebuilds the HTTP client when the server or credential changes, and reuses it otherwise.
    /// </summary>
    /// <remarks>
    /// One client for the process is what keeps TLS handshakes from being repeated per file, so
    /// it is deliberately not rebuilt per operation. The identity string is compared rather than
    /// the credential itself so a secret is never held longer than needed.
    /// </remarks>
    private void Connect(AppSettings settings, PanoramaCredential credential)
    {
        var identity = $"{settings.ServerUrl}|{credential.UserName}|{credential.Secret.GetHashCode()}";

        if (_client is not null && _connectedTo == identity)
        {
            return;
        }

        _http?.Dispose();

        var options = new WebDavClientOptions
        {
            BaseAddress = new Uri(settings.ServerUrl, UriKind.Absolute),
            Credential = credential,
            MaxConcurrentTransfers = settings.MaxConcurrentTransfers,
            TrustedRootCertificatePath = settings.TrustedRootCertificatePath,
            RecordSha256 = settings.RecordSha256,
        };

        _http = options.CreateHttpClient();
        _client = new WebDavClient(_http, options, _loggerFactory.CreateLogger<WebDavClient>());
        _connectedTo = identity;

        _log.LogInformation(
            "Using {Server} as {Credential}.", settings.ServerUrl, credential.ToString());
    }

    /// <summary>The connected client, for the remote folder browser.</summary>
    public IWebDavClient? Client => _client;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _run?.Cancel();
        _run?.Dispose();

        await StopMonitoringAsync().ConfigureAwait(false);

        _http?.Dispose();
    }

    /// <summary>
    /// Synchronous teardown, for the service container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IAsyncDisposable"/> alone is not enough: a container disposed synchronously --
    /// which is what happens when <c>Main</c> returns -- refuses to dispose a service that only
    /// implements the async interface, and throws rather than skipping it. So both are here.
    /// </para>
    /// <para>
    /// This one cancels and does not wait. The process is on its way out, and waiting for a
    /// multi-gigabyte upload to notice would only hold the window open. An abandoned upload is
    /// already a case the design covers: every state change is written to the ledger before the
    /// action it describes, so the next run finds the row still marked Uploading and re-offers
    /// it. Use <see cref="StopMonitoringAsync"/> when a graceful stop is actually wanted.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _run?.Cancel();
        _run?.Dispose();
        _run = null;

        _monitoring?.Cancel();
        _monitoring?.Dispose();
        _monitoring = null;

        _monitor?.Dispose();
        _monitor = null;
        _monitorEngine = null;

        _http?.Dispose();
    }
}
