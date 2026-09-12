using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
/// The secret reaches the key only as a SHA-256 digest, so a key can be logged without leaking
/// it. Deliberately not <c>string.GetHashCode</c>, which the code this replaced used: that is
/// thirty-two bits and not collision-resistant, and a collision here does not degrade a cache --
/// it hands one configuration a client carrying another account's Authorization header, and
/// uploads its files as that account.
/// </para>
/// <para>
/// Everything else the client is built from is in the key too. An entry made before somebody
/// changed the extra root certificate, the pool size or the SHA-256 setting would otherwise be
/// handed back afterwards, and the new setting would be silently ignored for the rest of the
/// session.
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

        var key = string.Join(
            '|',
            configuration.ServerUrl,
            credential.UserName,
            Fingerprint(credential.Secret),
            settings.MaxConcurrentTransfers,
            settings.TrustedRootCertificatePath ?? string.Empty,
            settings.RecordSha256);

        lock (_gate)
        {
            // Inside the lock, not before it. Checked outside, a Dispose could run between the
            // check and the lock, and this would then put a client into a dictionary that has
            // already been emptied -- never disposed, and still handed out after teardown.
            ObjectDisposedException.ThrowIf(_disposed, this);

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

    /// <summary>A digest of the secret, so identity is exact and the key is still safe to log.</summary>
    private static string Fingerprint(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private sealed record Entry(HttpClient Http, WebDavClient Client);
}
