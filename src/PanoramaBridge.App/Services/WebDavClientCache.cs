using System.Net.Http;
using Microsoft.Extensions.Logging;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.App.Services;

/// <summary>
/// One WebDAV client per server and sign-in, kept for as long as the service lives.
/// </summary>
/// <remarks>
/// <para>
/// A client is expensive to make and cheap to keep: its handler holds the connection pool, so
/// building one per operation means a fresh TLS handshake for every file and a socket left in
/// TIME_WAIT afterwards. Before configurations there was one client and a remark saying it was
/// deliberately not rebuilt per operation; one runner per configuration quietly turned that into
/// one per configuration per scan, so pressing Upload now with eight of them created and
/// destroyed eight pools.
/// </para>
/// <para>
/// Keyed by server and credential together, because that pair is what a connection actually is.
/// Two configurations on one server as one account share a client and its pool; the same server as
/// a different account does not, since a client carries exactly one credential.
/// </para>
/// <para>
/// The secret is not part of the key in any readable form -- only its hash code contributes, the
/// same way <c>TransferService</c> has always compared connection identity -- so a cache key can
/// be logged without leaking anything.
/// </para>
/// </remarks>
public sealed class WebDavClientCache : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    public WebDavClientCache(ILoggerFactory loggerFactory) =>
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    /// <summary>How many distinct connections are currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>
    /// The client for this configuration's server and credential, building one only if needed.
    /// </summary>
    /// <param name="settings">
    /// Read for the things that describe this computer rather than the pairing: the extra root
    /// certificate, the SHA-256 record, and how large the connection pool should be.
    /// </param>
    public IWebDavClient For(
        AppSettings settings,
        MonitoringConfiguration configuration,
        PanoramaCredential credential)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credential);

        ObjectDisposedException.ThrowIf(_disposed, this);

        var key =
            $"{configuration.ServerUrl}|{credential.UserName}|{credential.Secret.GetHashCode()}";

        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                return existing.Client;
            }

            var options = new WebDavClientOptions
            {
                BaseAddress = new Uri(configuration.ServerUrl, UriKind.Absolute),
                Credential = credential,

                // The whole limit, not a share of it. At most this many transfers are in flight
                // anywhere, and they may all be going through this one client.
                MaxConcurrentTransfers = Math.Max(1, settings.MaxConcurrentTransfers),

                TrustedRootCertificatePath = settings.TrustedRootCertificatePath,
                RecordSha256 = settings.RecordSha256,
            };

            var http = options.CreateHttpClient();

            var client = new WebDavClient(
                http, options, _loggerFactory.CreateLogger<WebDavClient>());

            _clients[key] = new Entry(http, client);

            _loggerFactory.CreateLogger<WebDavClientCache>().LogInformation(
                "Using {Server} as {Credential}.",
                configuration.ServerUrl,
                credential.ToString());

            return client;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            foreach (var entry in _clients.Values)
            {
                entry.Http.Dispose();
            }

            _clients.Clear();
        }
    }

    private sealed record Entry(HttpClient Http, WebDavClient Client);
}
