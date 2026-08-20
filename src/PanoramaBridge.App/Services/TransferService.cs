using Microsoft.Extensions.Logging;
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
public sealed class TransferService : IAsyncDisposable
{
    private readonly IStateStore _store;
    private readonly ICredentialStore _credentials;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TransferService> _log;

    private HttpClient? _http;
    private WebDavClient? _client;
    private string? _connectedTo;
    private CancellationTokenSource? _run;

    public TransferService(
        IStateStore store,
        ICredentialStore credentials,
        ILoggerFactory loggerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<TransferService>();
    }

    /// <summary>Collects progress for the UI to drain on its own schedule.</summary>
    public TransferProgressAggregator Progress { get; } = new();

    /// <summary>True while a scan or transfer run is in flight.</summary>
    public bool IsRunning => _run is { IsCancellationRequested: false };

    /// <summary>Raised when a run starts or finishes, so commands can re-evaluate.</summary>
    public event Action? RunStateChanged;

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
    /// Walks the monitored directory and transfers whatever needs transferring.
    /// </summary>
    /// <remarks>
    /// The scan runs on a background task. The equivalent in the Python version ran on the UI
    /// thread and hashed every file it found, so pointing it at a populated directory froze the
    /// window for minutes.
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

        var credential = ResolveCredential(settings, secret)
            ?? throw new InvalidOperationException("No credential is available for this server.");

        Connect(settings, credential);

        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RunStateChanged?.Invoke();

        try
        {
            var options = new TransferEngineOptions
            {
                LocalBaseDirectory = settings.LocalDirectory,
                DestinationRoot = RemotePath.Parse(settings.RemotePath),
                MaxConcurrentTransfers = settings.MaxConcurrentTransfers,
                ConflictPolicy = settings.ConflictPolicy,
                VerifyUploads = settings.VerifyUploads,
            };

            await using var coordinator = new TransferCoordinator(
                _client!,
                _store,
                options,
                log: _loggerFactory.CreateLogger<TransferCoordinator>());

            coordinator.Progress += Progress.Report;

            await coordinator.RecoverInterruptedAsync(_run.Token).ConfigureAwait(false);

            var offered = 0;
            foreach (var file in EnumerateCandidates(settings))
            {
                if (await coordinator.EnqueueAsync(file, _run.Token).ConfigureAwait(false))
                {
                    offered++;
                }
            }

            coordinator.CompleteAdding();

            _log.LogInformation("Offered {Count} files for transfer.", offered);

            return await coordinator.RunAsync(_run.Token).ConfigureAwait(false);
        }
        finally
        {
            _run.Dispose();
            _run = null;
            RunStateChanged?.Invoke();
        }
    }

    /// <summary>Stops the current run. In-flight uploads are abandoned, not corrupted.</summary>
    public void Cancel() => _run?.Cancel();

    /// <summary>
    /// Files in the monitored tree whose extension is of interest.
    /// </summary>
    /// <remarks>
    /// Extension matching uses <see cref="Path.GetExtension(string)"/> rather than a suffix
    /// comparison, so a filter of <c>.raw</c> does not also match <c>archive.notraw</c>.
    /// </remarks>
    private static IEnumerable<string> EnumerateCandidates(AppSettings settings)
    {
        var wanted = settings.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.IncludeSubdirectories,
            IgnoreInaccessible = true,
            // Reparse points are skipped so a junction cannot send the scan round in a loop.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(settings.LocalDirectory, "*", options))
        {
            var name = Path.GetFileName(file);

            // Instrument software and Windows both leave dot- and tilde-prefixed working files
            // behind; they are never acquisition data.
            if (name.StartsWith('.') || name.StartsWith('~'))
            {
                continue;
            }

            if (wanted.Count == 0 || wanted.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
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
    public ValueTask DisposeAsync()
    {
        _run?.Cancel();
        _run?.Dispose();
        _http?.Dispose();
        return ValueTask.CompletedTask;
    }
}
