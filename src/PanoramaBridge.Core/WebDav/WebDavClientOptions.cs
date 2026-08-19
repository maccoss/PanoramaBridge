using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// Settings for the WebDAV transport.
/// </summary>
/// <remarks>
/// Only the values a user could reasonably need are exposed. Buffer sizes, the retry policy and
/// connection lifetimes are deliberately fixed: every knob is a support conversation, and none
/// of those has a right answer a scientist should have to find.
/// </remarks>
public sealed class WebDavClientOptions
{
    /// <summary>The Panorama server, for example <c>https://panoramaweb.org</c>.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>How to authenticate. An API key is preferred over an account password.</summary>
    public required PanoramaCredential Credential { get; init; }

    /// <summary>
    /// How many files may be uploaded at once. Three or four HTTP/1.1 connections saturate a
    /// gigabit link to a single Tomcat without monopolizing its connector threads.
    /// </summary>
    /// <remarks>
    /// Lower this to one or two when the monitored volume is a spinning disk: concurrent
    /// sequential reads turn into seeking, and the disk becomes the bottleneck instead of the
    /// network.
    /// </remarks>
    public int MaxConcurrentTransfers { get; init; } = 3;

    /// <summary>Budget for listings, stat calls, MKCOL, MOVE and DELETE.</summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Budget for a single file's server-side hash.</summary>
    public TimeSpan FileHashTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Budget for hashing a whole collection.</summary>
    public TimeSpan CollectionHashTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an upload may make no progress before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Deliberately a <em>progress</em> timeout rather than a wall-clock one. A 7 GB acquisition
    /// over a VPN can legitimately take hours, so any total deadline would eventually kill a
    /// healthy transfer; a connection that has moved no bytes for two minutes, on the other
    /// hand, is not coming back.
    /// </remarks>
    public TimeSpan UploadStallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Total attempts for a retryable failure, including the first.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Ceiling on the backoff between attempts.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// An additional trusted root, for a site behind a TLS-inspecting proxy.
    /// </summary>
    /// <remarks>
    /// Additive: the certificate is trusted <em>in addition to</em> the system store, and the
    /// chain is still validated. There is deliberately no option to skip validation altogether.
    /// </remarks>
    public string? TrustedRootCertificatePath { get; init; }

    /// <summary>
    /// Builds the shared <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// One instance serves the whole process, so TLS handshakes are not repeated per file.
    /// <para>
    /// Two choices here are load-bearing. <see cref="HttpClient.Timeout"/> is left infinite
    /// because it applies to the <em>whole</em> request including the body, and any finite
    /// value would abort a legitimate multi-hour upload; per-operation deadlines are applied
    /// with linked cancellation tokens instead. And the credential is written straight onto the
    /// default headers rather than handed to the handler, because .NET's built-in credential
    /// support waits to be challenged -- which would mean discovering a rejected key only after
    /// streaming several gigabytes.
    /// </para>
    /// </remarks>
    public HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = MaxConcurrentTransfers + 4,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,

            // A redirect to a sign-in page must surface as a failure, not be followed and
            // mistaken for success.
            AllowAutoRedirect = false,

            // Stateless Basic on every request. Without a session cookie there is no CSRF
            // requirement on PUT, MKCOL, MOVE or DELETE, which keeps the transport simple.
            UseCookies = false,
        };

        if (!string.IsNullOrWhiteSpace(TrustedRootCertificatePath))
        {
            handler.SslOptions = BuildAdditiveTrust(TrustedRootCertificatePath);
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        client.DefaultRequestHeaders.Authorization = Credential.ToAuthenticationHeader();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        client.DefaultRequestHeaders.ExpectContinue = true;

        return client;
    }

    private static SslClientAuthenticationOptions BuildAdditiveTrust(string certificatePath)
    {
        // X509CertificateLoader is .NET 9+; this is the .NET 8 equivalent.
#pragma warning disable SYSLIB0057
        var extraRoot = new X509Certificate2(certificatePath);
#pragma warning restore SYSLIB0057

        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        policy.CustomTrustStore.Add(extraRoot);

        // Keep the machine's own roots as well, so adding a corporate certificate does not
        // break every other server the client might talk to.
        using var systemRoots = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        systemRoots.Open(OpenFlags.ReadOnly);
        policy.CustomTrustStore.AddRange(systemRoots.Certificates);

        return new SslClientAuthenticationOptions { CertificateChainPolicy = policy };
    }
}
