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
/// Owns the transfer engines on behalf of the UI.
/// </summary>
/// <remarks>
/// <para>
/// The view models talk to this and never to the WebDAV client or the ledger directly, so all
/// the awkward lifetime questions -- when a client is rebuilt, when a run can be canceled,
/// which credential is in force -- live in one place.
/// </para>
/// <para>
/// One <see cref="ConfigurationRunner"/> per enabled configuration, each with its own connection,
/// engine and monitor, because configurations may watch different folders and address different
/// servers as different people. What they share is the concurrency limit, which describes the
/// disk and the link rather than any one pairing -- see <see cref="TransferBudget"/>.
/// </para>
/// </remarks>
public sealed class TransferService : IAsyncDisposable, IDisposable
{
    private readonly IStateStore _store;
    private readonly ICredentialStore _credentials;
    private readonly ResourceGovernor _governor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TransferService> _log;

    /// <summary>
    /// The running configurations.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale rather than mutated, so a reader on any thread sees a complete set --
    /// the status properties below are read from the UI thread and from timer callbacks while
    /// monitoring is being started or stopped.
    /// </remarks>
    private volatile ConfigurationRunner[] _runners = [];

    private TransferBudget? _budget;
    private CancellationTokenSource? _monitoring;
    private CancellationTokenSource? _run;
    private SweepResult? _lastSweep;

    /// <summary>
    /// The connection the settings screen tested, kept for the remote folder browser.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the runners' connections. The browser shows the server the
    /// person is editing, which is not necessarily one that is running -- and with configurations
    /// on different servers, "the connected client" is otherwise an ambiguous thing to ask for.
    /// </remarks>
    private HttpClient? _browseHttp;
    private WebDavClient? _browseClient;
    private string? _browseConnectedTo;

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

    /// <summary>
    /// True while any configuration is still being watched.
    /// </summary>
    /// <remarks>
    /// Asks the runners rather than only the token, because a runner whose monitor died cancels
    /// its own linked token and that does not cancel this one. Reporting true when every runner
    /// has stopped would leave the window saying it was monitoring folders nobody was looking
    /// at, with the button still offering to stop something that had already stopped.
    /// </remarks>
    public bool IsMonitoring =>
        _monitoring is { IsCancellationRequested: false } && MonitoredConfigurations > 0;

    /// <summary>How many configurations are currently being watched.</summary>
    /// <remarks>Counted rather than taken from the length, so one that has given up drops out.</remarks>
    public int MonitoredConfigurations
    {
        get
        {
            var running = 0;

            foreach (var runner in _runners)
            {
                if (runner.IsRunning)
                {
                    running++;
                }
            }

            return running;
        }
    }

    /// <summary>
    /// True while any file has bytes moving, however that transfer was started.
    /// </summary>
    /// <remarks>
    /// <see cref="IsRunning"/> is not this. It tracks <c>_run</c>, which only a manual scan
    /// creates; a file uploaded by monitoring leaves it null, so asking <see cref="IsRunning"/>
    /// "is a transfer in progress" answers no for the ordinary case -- an unattended machine
    /// uploading an acquisition. Every path reports into <see cref="Progress"/>, which is why
    /// this asks that instead.
    ///
    /// Only <see cref="TransferState.Uploading"/> counts. Uploaded-but-unverified is deliberately
    /// excluded: its bytes are already on the server and the next sweep will confirm them, and
    /// with verification turned off a file can rest in that state, which would leave this stuck
    /// true forever.
    /// </remarks>
    public bool HasTransferInFlight =>
        Progress.Snapshot().Any(p => p.State == TransferState.Uploading);

    /// <summary>
    /// What monitoring is doing across every running configuration, or null when none is.
    /// </summary>
    /// <remarks>
    /// <see cref="MonitorStatus.WatchingForChanges"/> is true only when it is true of all of them.
    /// Change notifications are a convenience and the periodic sweep is the safety net, so the
    /// honest summary of "some of these folders are getting notifications" is that this machine
    /// is relying on its sweeps.
    /// </remarks>
    public MonitorStatus? Monitor
    {
        get
        {
            var runners = _runners;

            if (runners.Length == 0)
            {
                return null;
            }

            var settling = 0;
            var watching = true;

            foreach (var runner in runners)
            {
                var status = runner.Status;
                settling += status.Settling;
                watching &= status.WatchingForChanges;
            }

            return new MonitorStatus(watching, settling, _lastSweep);
        }
    }

    /// <summary>Raised when a run starts or finishes, so commands can re-evaluate.</summary>
    public event Action? RunStateChanged;

    /// <summary>Raised after each walk of a monitored folder, naming the configuration.</summary>
    public event Action<ConfigurationSweep>? Swept;

    /// <summary>Raised whenever a file is examined and found not ready to read.</summary>
    public event Action<GateReport>? Waiting;

    /// <summary>Raised when a configuration stops for a reason nobody asked for.</summary>
    public event Action<string>? MonitoringFailed;

    /// <summary>
    /// Builds the client for the given settings and confirms the server accepts it.
    /// </summary>
    /// <remarks>
    /// Reports whether the chosen destination is writable, rather than letting the user discover
    /// a permissions problem hours into a transfer.
    /// </remarks>
    /// <param name="edited">
    /// The configuration the settings tabs are showing, which is the one to test and the one the
    /// typed secret belongs to. Null falls back to the first, which is what a caller with only
    /// one configuration means.
    /// </param>
    public async Task<ConnectionCheck> TestConnectionAsync(
        AppSettings settings,
        string? secret,
        MonitoringConfiguration? edited = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            return new ConnectionCheck(false, problems[0]);
        }

        var configuration = edited ?? EditedConfiguration(settings) ?? new MonitoringConfiguration();

        try
        {
            var credential = ResolveCredential(configuration, secret);
            if (credential is null)
            {
                return new ConnectionCheck(
                    false,
                    configuration.AuthMode == AuthMode.ApiKey
                        ? "Enter an API key, or generate one from Panorama's External Tool Access page."
                        : "Enter your Panorama password.");
            }

            var client = ConnectForBrowsing(settings, configuration, credential);

            var destination = RemotePath.Parse(configuration.RemotePath);
            var capabilities = await client
                .GetCapabilitiesAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            // Listing the parent tells us the permissions on the destination itself.
            var siblings = await client
                .ListAsync(destination.Parent, cancellationToken)
                .ConfigureAwait(false);

            var folder = siblings.FirstOrDefault(r =>
                r.IsCollection && string.Equals(r.Name, destination.Name, StringComparison.Ordinal));

            var writable = folder?.Permissions.CanUpload ?? capabilities.Allows("PUT");

            var detail = folder is null
                ? $"{configuration.RemotePath} does not exist yet; it will be created on the first upload."
                : writable
                    ? $"You can upload to {configuration.RemotePath}."
                    : $"{configuration.RemotePath} is read-only for this account. A Panorama "
                      + "administrator needs to grant write access.";

            return new ConnectionCheck(
                true,
                $"Connected to {capabilities.ServerName ?? configuration.ServerUrl}.",
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
            return new ConnectionCheck(
                false, $"Could not reach {configuration.ServerUrl}: {ex.Message}");
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
    /// Walks every enabled configuration's directory once and transfers what needs transferring.
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
    /// <para>
    /// Configurations are scanned at the same time rather than one after another. The transfers
    /// they produce are gated by one shared budget either way, so taking them in turn would not
    /// reduce the load on the disk -- it would only mean the second folder waited out the first
    /// one's two-minute patience for a file still being written before it was looked at at all.
    /// </para>
    /// </remarks>
    /// <param name="edited">
    /// The configuration the settings tabs are showing, so the typed secret reaches the
    /// credential slot it was typed for. See <see cref="SecretFor"/>.
    /// </param>
    public async Task<TransferSummary> ScanAndUploadAsync(
        AppSettings settings,
        string? secret,
        MonitoringConfiguration? edited = null,
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
                "The folders are being monitored; ask for a check rather than starting a second scan.");
        }

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(problems[0]);
        }

        var configurations = settings.EnabledConfigurations.ToArray();

        // One budget for the whole scan, so pressing Upload now with five configurations moves as
        // many files at once as the slider says rather than five times as many.
        using var budget = new TransferBudget(settings.MaxConcurrentTransfers);

        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RunStateChanged?.Invoke();

        try
        {
            var scans = configurations
                .Select(c => ScanOneAsync(
                    settings, c, SecretFor(settings, c, secret, edited), budget, _run.Token))
                .ToArray();

            var summaries = await Task.WhenAll(scans).ConfigureAwait(false);

            return summaries.Aggregate(
                new TransferSummary(0, 0, 0, 0, 0, TimeSpan.Zero),
                (total, one) => new TransferSummary(
                    total.Uploaded + one.Uploaded,
                    total.Skipped + one.Skipped,
                    total.Conflicts + one.Conflicts,
                    total.Failed + one.Failed,
                    total.BytesUploaded + one.BytesUploaded,

                    // The longest, not the sum. They ran at the same time, so adding them would
                    // report a wall-clock time that never elapsed.
                    total.Elapsed > one.Elapsed ? total.Elapsed : one.Elapsed));
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

    private async Task<TransferSummary> ScanOneAsync(
        AppSettings settings,
        MonitoringConfiguration configuration,
        string? secret,
        TransferBudget budget,
        CancellationToken cancellationToken)
    {
        var credential = ResolveCredential(configuration, secret)
            ?? throw new InvalidOperationException(
                $"{configuration.DisplayName}: no credential is available for "
                + $"{configuration.ServerUrl}.");

        await using var runner = new ConfigurationRunner(
            configuration, settings, credential, _store, budget, _loggerFactory);

        runner.Progress += Progress.Report;
        runner.Waiting += OnWaiting;

        try
        {
            return await runner
                .ScanOnceAsync(ManualScanPatience, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            runner.Progress -= Progress.Report;
            runner.Waiting -= OnWaiting;
        }
    }

    /// <summary>Stops the current manual run. In-flight uploads are abandoned, not corrupted.</summary>
    public void Cancel() => _run?.Cancel();

    /// <summary>
    /// Starts watching every enabled configuration, transferring files as they finish being
    /// written.
    /// </summary>
    /// <remarks>
    /// Each engine is started once and left running, so its workers block on an empty queue
    /// rather than being torn down and rebuilt around every file.
    /// <para>
    /// A configuration that will not start takes the whole attempt down with it, and the ones
    /// already started are stopped again. Starting four of five and reporting success would leave
    /// the window saying it was monitoring while one instrument quietly filled its disk -- and
    /// several configurations is precisely the situation in which nobody can see at a glance that
    /// one folder is uncovered.
    /// </para>
    /// </remarks>
    /// <param name="edited">
    /// The configuration the settings tabs are showing, so the typed secret reaches the
    /// credential slot it was typed for. See <see cref="SecretFor"/>.
    /// </param>
    public async Task StartMonitoringAsync(
        AppSettings settings,
        string? secret,
        MonitoringConfiguration? edited = null,
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

        // IsMonitoring is false once every runner has given up, but the runners themselves are
        // still here holding a connection, an engine and a monitor each. Starting again without
        // winding them down would simply drop them, leaking an HttpClient and a set of worker
        // tasks per configuration, every time somebody pressed the button after a failure.
        if (_monitoring is not null)
        {
            await StopMonitoringAsync().ConfigureAwait(false);
        }

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(problems[0]);
        }

        var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budget = new TransferBudget(settings.MaxConcurrentTransfers);
        var started = new List<ConfigurationRunner>();

        try
        {
            foreach (var configuration in settings.EnabledConfigurations)
            {
                var credential = ResolveCredential(
                        configuration, SecretFor(settings, configuration, secret, edited))
                    ?? throw new InvalidOperationException(
                        $"{configuration.DisplayName}: no credential is available for "
                        + $"{configuration.ServerUrl}.");

                var runner = new ConfigurationRunner(
                    configuration, settings, credential, _store, budget, _loggerFactory);

                runner.Progress += Progress.Report;
                runner.Swept += OnSwept;
                runner.Waiting += OnWaiting;
                runner.Failed += OnRunnerFailed;

                started.Add(runner);

                await runner.StartAsync(monitoring.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var runner in started)
            {
                Detach(runner);
                await runner.DisposeAsync().ConfigureAwait(false);
            }

            monitoring.Dispose();
            budget.Dispose();
            throw;
        }

        _budget = budget;
        _monitoring = monitoring;
        _runners = [.. started];

        _log.LogInformation(
            "Monitoring {Count} configuration(s), {Concurrency} transfer(s) at once across all of them.",
            started.Count,
            budget.Capacity);

        RunStateChanged?.Invoke();
    }

    /// <summary>Stops every configuration and waits for the engines to wind down.</summary>
    public async Task StopMonitoringAsync()
    {
        var monitoring = _monitoring;

        if (monitoring is null)
        {
            return;
        }

        await monitoring.CancelAsync().ConfigureAwait(false);

        var runners = _runners;
        _runners = [];

        foreach (var runner in runners)
        {
            Detach(runner);
            await runner.DisposeAsync().ConfigureAwait(false);
        }

        _monitoring = null;
        monitoring.Dispose();

        _budget?.Dispose();
        _budget = null;

        _log.LogInformation("Monitoring stopped.");

        RunStateChanged?.Invoke();
        _governor.ReleaseIdleMemory();
    }

    /// <summary>
    /// Asks every monitored configuration to walk its folder now rather than at its next turn.
    /// </summary>
    /// <returns>False when nothing is being monitored, so the caller can scan instead.</returns>
    public bool RequestSweep(string reason)
    {
        var runners = _runners;

        if (runners.Length == 0)
        {
            return false;
        }

        foreach (var runner in runners)
        {
            runner.RequestSweep(reason);
        }

        return true;
    }

    /// <summary>The connection the settings screen tested, for the remote folder browser.</summary>
    public IWebDavClient? Client => _browseClient;

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
        _run = null;

        await StopMonitoringAsync().ConfigureAwait(false);

        _browseHttp?.Dispose();
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

        foreach (var runner in _runners)
        {
            Detach(runner);
            runner.Abandon();
        }

        _runners = [];

        // Deliberately not disposed here, unlike in StopMonitoringAsync. Abandoning cancels
        // without waiting, so a worker may still be inside AcquireAsync -- and disposing the
        // semaphore underneath it turns an orderly cancellation into an ObjectDisposedException
        // logged as a transfer failure on the way out of the process. Nothing is leaked by
        // leaving it: the budget holds no handle, and the process is exiting.
        _budget = null;

        _browseHttp?.Dispose();
    }

    /// <summary>
    /// The configuration the settings tabs are showing.
    /// </summary>
    /// <remarks>
    /// Only a fallback for a caller that did not say. The tabs can be showing any of them, and
    /// <c>SettingsViewModel.Edited</c> is what actually knows which -- so every caller that has a
    /// view model passes it, and this covers the one-configuration case and the tests.
    /// Deliberately not "the first enabled one": testing the connection has to test what the
    /// person is looking at, and the tabs go on showing a configuration after it is switched off.
    /// </remarks>
    private static MonitoringConfiguration? EditedConfiguration(AppSettings settings) =>
        settings.Configurations.FirstOrDefault();

    /// <summary>
    /// The secret from the password box, for the configurations it is actually the credential for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is one password box and it belongs to the configuration the tabs are showing. Handing
    /// what was typed there to every configuration would sign one in to a different server with
    /// another's key -- and, worse, make it ignore the credential it has of its own, because a
    /// secret typed this session deliberately takes precedence over a stored one. Those fall back
    /// to Windows Credential Manager, which is where their own lives.
    /// </para>
    /// <para>
    /// It does reach any configuration that would read the same credential slot, which is the
    /// same server as the same account. Two folders on one instrument going to two projects on
    /// one Panorama is the ordinary reason to want a second configuration at all, and those two
    /// share a credential -- refusing the second the key that is demonstrably its own, until
    /// somebody had saved it, would be a rule with nothing behind it.
    /// </para>
    /// <para>
    /// The slot is asked for rather than worked out here, so this cannot drift from the rule that
    /// actually decides which credential gets read.
    /// </para>
    /// </remarks>
    private static string? SecretFor(
        AppSettings settings,
        MonitoringConfiguration configuration,
        string? secret,
        MonitoringConfiguration? editing)
    {
        if ((editing ?? EditedConfiguration(settings)) is not { } edited)
        {
            return null;
        }

        var slot = WindowsCredentialStore.TargetFor(configuration.ServerUrl, configuration.Account);
        var typedFor = WindowsCredentialStore.TargetFor(edited.ServerUrl, edited.Account);

        return string.Equals(slot, typedFor, StringComparison.OrdinalIgnoreCase) ? secret : null;
    }

    private void Detach(ConfigurationRunner runner)
    {
        runner.Progress -= Progress.Report;
        runner.Swept -= OnSwept;
        runner.Waiting -= OnWaiting;
        runner.Failed -= OnRunnerFailed;
    }

    private void OnSwept(ConfigurationSweep sweep)
    {
        _lastSweep = sweep.Result;
        Swept?.Invoke(sweep);
    }

    /// <summary>
    /// Reports a configuration that stopped watching for a reason nobody asked for.
    /// </summary>
    /// <remarks>
    /// Reported as a failed sweep as well as through <see cref="MonitoringFailed"/>. The window
    /// composes its status line from the last sweep of each configuration, so without this the
    /// message would be on screen only until the next healthy configuration swept and replaced
    /// it -- a folder that had stopped being watched, announced once and then gone.
    /// </remarks>
    private void OnRunnerFailed(ConfigurationRunner runner, string problem)
    {
        OnSwept(new ConfigurationSweep(
            runner.Name,
            new SweepResult(0, 0, 0, TimeSpan.Zero, problem)));

        MonitoringFailed?.Invoke($"{runner.Name}: {problem}");

        // So the button and the count re-read: this runner has stopped, and if it was the last
        // one then IsMonitoring is now false.
        RunStateChanged?.Invoke();
    }

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

    private PanoramaCredential? ResolveCredential(
        MonitoringConfiguration configuration,
        string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret))
        {
            return configuration.AuthMode == AuthMode.ApiKey
                ? PanoramaCredential.ApiKey(secret)
                : PanoramaCredential.UserNameAndPassword(configuration.UserName, secret);
        }

        // Nothing typed this session, so fall back to what was saved. Read under this
        // configuration's account, so two of them on one server do not read each other's.
        var stored = _credentials.Read(configuration.ServerUrl, configuration.Account);
        if (stored is null)
        {
            return null;
        }

        return configuration.AuthMode == AuthMode.ApiKey
            ? PanoramaCredential.ApiKey(stored.Value.Secret)
            : PanoramaCredential.UserNameAndPassword(stored.Value.UserName, stored.Value.Secret);
    }

    /// <summary>
    /// Rebuilds the browsing client when the server or credential changes, and reuses it
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// Not rebuilt per operation, so repeated trips through the folder browser do not repeat the
    /// TLS handshake. The identity string is compared rather than the credential itself, so a
    /// secret is never held longer than needed.
    /// </remarks>
    private WebDavClient ConnectForBrowsing(
        AppSettings settings,
        MonitoringConfiguration configuration,
        PanoramaCredential credential)
    {
        var identity =
            $"{configuration.ServerUrl}|{credential.UserName}|{credential.Secret.GetHashCode()}";

        if (_browseClient is not null && _browseConnectedTo == identity)
        {
            return _browseClient;
        }

        _browseHttp?.Dispose();

        var options = new WebDavClientOptions
        {
            BaseAddress = new Uri(configuration.ServerUrl, UriKind.Absolute),
            Credential = credential,
            MaxConcurrentTransfers = settings.MaxConcurrentTransfers,
            TrustedRootCertificatePath = settings.TrustedRootCertificatePath,
            RecordSha256 = settings.RecordSha256,
        };

        _browseHttp = options.CreateHttpClient();
        _browseClient = new WebDavClient(
            _browseHttp, options, _loggerFactory.CreateLogger<WebDavClient>());
        _browseConnectedTo = identity;

        _log.LogInformation(
            "Using {Server} as {Credential}.", configuration.ServerUrl, credential.ToString());

        return _browseClient;
    }
}
