using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Hashing;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// WebDAV client for Panorama.
/// </summary>
/// <remarks>
/// Written against verified server behaviour rather than the WebDAV specification, because the
/// two differ in ways that matter: MKCOL is single-level, PUT answers 201 for a replacement as
/// well as a creation, and <c>Content-Range</c> on PUT is not implemented at all, so there is no
/// partial or resumable upload to fall back on. Uploads are therefore one streaming PUT per
/// file, at any size, guarded by a stall watchdog and a retry policy.
/// </remarks>
public sealed class WebDavClient : IWebDavClient
{
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private readonly HttpClient _http;
    private readonly WebDavClientOptions _options;
    private readonly ILogger<WebDavClient> _log;

    /// <summary>
    /// Collections known to exist. Avoids re-issuing MKCOL for every file in a batch that all
    /// land in the same folder.
    /// </summary>
    private readonly ConcurrentDictionary<RemotePath, byte> _knownCollections = new();

    public WebDavClient(
        HttpClient httpClient,
        WebDavClientOptions options,
        ILogger<WebDavClient>? log = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullLogger<WebDavClient>.Instance;
    }

    /// <inheritdoc />
    public async Task<ServerCapabilities> GetCapabilitiesAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Options, Url(path)),
            "OPTIONS",
            path,
            _options.MetadataTimeout,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "OPTIONS", path, cancellationToken).ConfigureAwait(false);

        var allow = response.Content.Headers.Allow.Count > 0
            ? response.Content.Headers.Allow
            : (IEnumerable<string>)(response.Headers.TryGetValues("Allow", out var v) ? v : []);

        return new ServerCapabilities(
            ServerName: response.Headers.Server.ToString() is { Length: > 0 } s ? s : null,
            DavCompliance: response.Headers.TryGetValues("DAV", out var dav)
                ? string.Join(",", dav)
                : null,
            AllowedMethods: allow
                .SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(m => m.ToUpperInvariant())
                .Distinct()
                .ToArray());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebDavResource>> ListAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default)
    {
        var target = collection.AsCollection();

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url(target, "method=json")),
            "GET(json)",
            target,
            _options.MetadataTimeout,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "GET(json)", target, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return MethodJsonParser.Parse(body, target);
    }

    /// <inheritdoc />
    public async Task<WebDavResource?> GetResourceAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        if (path.IsRoot)
        {
            return null;
        }

        // Listing the parent and picking the entry out costs the same single request as asking
        // about the file directly, and it returns the permission flags too.
        IReadOnlyList<WebDavResource> siblings;
        try
        {
            siblings = await ListAsync(path.Parent, cancellationToken).ConfigureAwait(false);
        }
        catch (WebDavException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return siblings.FirstOrDefault(r => string.Equals(r.Name, path.Name, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<string?> GetFileHashAsync(
        RemotePath file,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url(file, "method=md5sum")),
            "GET(md5sum)",
            file,
            _options.FileHashTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "GET(md5sum)", file, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Md5SumParser.ParseSingle(body);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetCollectionHashesAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default)
    {
        var target = collection.AsCollection();

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url(target, "method=md5sum")),
            "GET(md5sum)",
            target,
            _options.CollectionHashTimeout,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await EnsureSuccessAsync(response, "GET(md5sum)", target, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Md5SumParser.Parse(body);
    }

    /// <inheritdoc />
    public async Task EnsureCollectionAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default)
    {
        var target = collection.AsCollection();

        if (target.IsRoot || _knownCollections.ContainsKey(target))
        {
            return;
        }

        var status = await TryCreateCollectionAsync(target, cancellationToken).ConfigureAwait(false);

        if (status == HttpStatusCode.Conflict)
        {
            // The server's MKCOL is single-level: a 409 means an ancestor is missing. Create
            // the parent chain, then try this level once more.
            await EnsureCollectionAsync(target.Parent, cancellationToken).ConfigureAwait(false);
            status = await TryCreateCollectionAsync(target, cancellationToken).ConfigureAwait(false);
        }

        switch (status)
        {
            // 405 means it is already there, which is exactly what was wanted.
            case HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.MethodNotAllowed:
                _knownCollections.TryAdd(target, 0);
                return;

            default:
                throw new WebDavException("MKCOL", target, status);
        }
    }

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(
        string localFilePath,
        RemotePath destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
        ArgumentNullException.ThrowIfNull(destination);

        var validation = PathSafety.ValidateSegment(destination.Name);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Message, nameof(destination));
        }

        await EnsureCollectionAsync(destination.Parent, cancellationToken).ConfigureAwait(false);

        var length = new FileInfo(localFilePath).Length;
        var stopwatch = Stopwatch.StartNew();

        var attempt = 0;
        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            // Both the request and the file handle have to be rebuilt for every attempt:
            // HttpContent is single-use, and the stream has already been consumed.
            long uploadedThisAttempt = 0;

            await using var file = new FileStream(
                localFilePath,
                FileMode.Open,
                FileAccess.Read,
                // Never block the instrument that is writing alongside us.
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var hashing = new HashingReadStream(
                file, leaveOpen: true, alsoSha256: _options.RecordSha256);

            var content = new StreamingFileContent(
                hashing,
                length,
                onBytesWritten: written =>
                {
                    uploadedThisAttempt += written;
                    progress?.Report(uploadedThisAttempt);
                });

            try
            {
                using var response = await SendWithStallWatchdogAsync(
                    content,
                    destination,
                    lastModified,
                    cancellationToken).ConfigureAwait(false);

                await EnsureSuccessAsync(response, "PUT", destination, cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                var hashes = hashing.GetHashes();
                _log.LogInformation(
                    "Uploaded {Path} ({Bytes} bytes) in {Seconds:F1}s",
                    destination,
                    length,
                    stopwatch.Elapsed.TotalSeconds);

                return new UploadResult(destination, length, hashes, stopwatch.Elapsed);
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt) && !cancellationToken.IsCancellationRequested)
            {
                var delay = RetryDelay(attempt, ex);

                _log.LogWarning(
                    ex,
                    "Upload of {Path} failed on attempt {Attempt} of {Max}; retrying in {Delay}.",
                    destination,
                    attempt,
                    _options.MaxAttempts,
                    delay);

                // There is no resume: the server has no partial-upload support, so a retry
                // restarts from byte zero. Progress is reset so the UI does not appear to
                // jump backwards without explanation.
                progress?.Report(0);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task MoveAsync(
        RemotePath source,
        RemotePath destination,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(Move, Url(source));
                request.Headers.Add("Destination", Url(destination).AbsoluteUri);
                request.Headers.Add("Overwrite", overwrite ? "T" : "F");
                return request;
            },
            "MOVE",
            source,
            _options.MetadataTimeout,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "MOVE", source, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <summary>
    /// LabKey's own way of letting a client keep a file's modification time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is epoch milliseconds, and it is read from either a header or a query parameter
    /// by <c>DavController.getLastModifiedHeader()</c>, on both the PUT path and the multipart
    /// POST the file browser web part uses. It is what makes an acquisition keep the date the
    /// instrument wrote it rather than the date it happened to be transferred.
    /// </para>
    /// <para>
    /// Nothing standard works here. <c>PROPPATCH</c> is implemented but gated on the user agent
    /// looking like Windows Explorer, so it answers 405 for anybody else; a <c>Last-Modified</c>
    /// request header and the ownCloud <c>X-OC-Mtime</c> convention are both accepted with 201
    /// and silently ignored. All four were measured against panoramaweb.org.
    /// </para>
    /// </remarks>
    private const string LastModifiedHeader = "X-LABKEY-Last-Modified";

    private static void StampWith(HttpRequestMessage request, DateTimeOffset? lastModified)
    {
        if (lastModified is not { } moment)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            LastModifiedHeader,
            moment.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task UploadTextAsync(
        string content,
        RemotePath destination,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destination);

        var validation = PathSafety.ValidateSegment(destination.Name);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Message, nameof(destination));
        }

        await EnsureCollectionAsync(destination.Parent, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, Url(destination))
                {
                    Content = new StringContent(content, Encoding.UTF8, "text/plain"),
                };

                StampWith(request, lastModified);
                return request;
            },
            "PUT",
            destination,
            _options.MetadataTimeout,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "PUT", destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(RemotePath path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, Url(path)),
            "DELETE",
            path,
            _options.MetadataTimeout,
            cancellationToken).ConfigureAwait(false);

        // Deleting something that is already gone is the desired end state, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, "DELETE", path, cancellationToken).ConfigureAwait(false);

        _knownCollections.TryRemove(path.AsCollection(), out _);
    }

    private async Task<HttpStatusCode> TryCreateCollectionAsync(
        RemotePath collection,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(Mkcol, Url(collection)),
            "MKCOL",
            collection,
            _options.MetadataTimeout,
            cancellationToken).ConfigureAwait(false);

        return response.StatusCode;
    }

    /// <summary>
    /// Sends a request, retrying transient failures with jittered backoff.
    /// </summary>
    /// <remarks>
    /// The request is built by a factory rather than passed in, because a
    /// <see cref="HttpRequestMessage"/> cannot be sent twice.
    /// </remarks>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        string method,
        RemotePath path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            try
            {
                var response = await _http
                    .SendAsync(createRequest(), HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);

                if (IsRetryableStatus(response.StatusCode) && attempt < _options.MaxAttempts)
                {
                    var delay = RetryDelay(attempt, response);
                    _log.LogDebug(
                        "{Method} {Path} returned {Status}; retrying in {Delay}.",
                        method, path, (int)response.StatusCode, delay);

                    response.Dispose();
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt) && !cancellationToken.IsCancellationRequested)
            {
                var delay = RetryDelay(attempt, ex);
                _log.LogDebug(
                    ex, "{Method} {Path} failed on attempt {Attempt}; retrying in {Delay}.",
                    method, path, attempt, delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends the upload, abandoning it if no bytes move for the configured stall window.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithStallWatchdogAsync(
        StreamingFileContent content,
        RemotePath destination,
        DateTimeOffset? lastModified,
        CancellationToken cancellationToken)
    {
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Put, Url(destination)) { Content = content };
        StampWith(request, lastModified);
        var send = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);

        var stalled = false;

        using var watchdog = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(
            async () =>
            {
                try
                {
                    while (await watchdog.WaitForNextTickAsync(abort.Token).ConfigureAwait(false))
                    {
                        if (DateTimeOffset.UtcNow - content.LastProgressUtc <= _options.UploadStallTimeout)
                        {
                            continue;
                        }

                        _log.LogWarning(
                            "Upload of {Path} has made no progress for {Timeout}; abandoning it.",
                            destination,
                            _options.UploadStallTimeout);

                        stalled = true;
                        await abort.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // The upload finished and the watchdog was told to stand down. Swallowing
                    // this is essential: letting it escape would make every successful upload
                    // fault when the monitor is awaited below.
                }
            },
            CancellationToken.None);

        try
        {
            return await send.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stalled && !cancellationToken.IsCancellationRequested)
        {
            // Surface a stall as a transport failure so the retry policy sees it, rather than
            // as a cancellation, which callers treat as the user's decision.
            throw new HttpRequestException(
                $"The upload of '{destination}' made no progress for {_options.UploadStallTimeout}.");
        }
        finally
        {
            if (!abort.IsCancellationRequested)
            {
                await abort.CancelAsync().ConfigureAwait(false);
            }

            await monitor.ConfigureAwait(false);
            request.Dispose();
        }
    }

    private Uri Url(RemotePath path, string? query = null)
    {
        var uri = path.ToUri(_options.BaseAddress);
        return query is null ? uri : new Uri(uri.AbsoluteUri + "?" + query, UriKind.Absolute);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string method,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            body = text.Length > 2000 ? text[..2000] : text;
        }
        catch (Exception)
        {
            // The body is a diagnostic nicety; never let reading it mask the real failure.
        }

        throw new WebDavException(method, path, response.StatusCode, response.ReasonPhrase, body);
    }

    private static bool IsRetryableStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private bool ShouldRetry(Exception exception, int attempt)
    {
        if (attempt >= _options.MaxAttempts)
        {
            return false;
        }

        return exception switch
        {
            // A stalled or timed-out attempt surfaces as cancellation. The caller's own
            // cancellation is filtered out by the call sites before this runs.
            OperationCanceledException => true,
            HttpRequestException => true,
            IOException => true,
            WebDavException dav => dav.IsTransient,
            _ => false,
        };
    }

    private TimeSpan RetryDelay(int attempt, object? context)
    {
        // Honour an explicit Retry-After over any computed backoff.
        if (context is HttpResponseMessage { Headers.RetryAfter: { } retryAfter })
        {
            if (retryAfter.Delta is { } delta)
            {
                return Min(delta, _options.MaxRetryDelay);
            }

            if (retryAfter.Date is { } date)
            {
                return Min(date - DateTimeOffset.UtcNow, _options.MaxRetryDelay);
            }
        }

        // Full jitter. Without it, a batch of concurrent uploads that all hit the same blip
        // would retry in lockstep and hammer the server in waves.
        var ceiling = Math.Min(
            _options.MaxRetryDelay.TotalMilliseconds,
            TimeSpan.FromSeconds(2).TotalMilliseconds * Math.Pow(2, attempt - 1));

        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) =>
        a < b ? (a > TimeSpan.Zero ? a : TimeSpan.Zero) : b;
}
