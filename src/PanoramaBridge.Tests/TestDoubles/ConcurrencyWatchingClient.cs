using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.TestDoubles;

/// <summary>
/// Wraps a fake server and records how many uploads overlapped.
/// </summary>
/// <remarks>
/// <para>
/// The high-water mark is the whole point. Counting uploads would say only that they happened;
/// this says how many were happening at once, which is what the concurrency setting controls and
/// what got multiplied when one engine became one engine per configuration.
/// </para>
/// <para>
/// A wrapper rather than a subclass, because <see cref="FakeWebDavClient"/> is deliberately
/// sealed: a fake that can be partially overridden is a fake nobody can trust to be faithful.
/// Every member here routes to the inner client and nothing is answered locally, so a test using
/// this gets the same server behavior it would get without it.
/// </para>
/// </remarks>
internal sealed class ConcurrencyWatchingClient : IWebDavClient
{
    private readonly FakeWebDavClient _inner;
    private int _inFlight;
    private int _highWaterMark;

    public ConcurrencyWatchingClient(FakeWebDavClient? inner = null) =>
        _inner = inner ?? new FakeWebDavClient();

    /// <summary>The fake being wrapped, for seeding content and reading its call counts.</summary>
    public FakeWebDavClient Inner => _inner;

    /// <summary>
    /// How long each upload is held, so overlap is observable rather than a race.
    /// </summary>
    /// <remarks>
    /// Without a hold, uploads of a small file finish so fast that they may genuinely never
    /// overlap, and the test would pass whatever the limit was -- proving nothing.
    /// </remarks>
    public TimeSpan HoldEachUpload { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>The most uploads that were ever in flight at the same time.</summary>
    public int HighWaterMark => Volatile.Read(ref _highWaterMark);

    /// <inheritdoc />
    public async Task<UploadResult> UploadAsync(
        string localFilePath,
        RemotePath destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null)
    {
        var now = Interlocked.Increment(ref _inFlight);

        // Compare-and-swap rather than a plain assignment: two uploads starting together would
        // otherwise each read the old mark, and the higher of the two could be lost.
        int seen;
        while (now > (seen = Volatile.Read(ref _highWaterMark)))
        {
            Interlocked.CompareExchange(ref _highWaterMark, now, seen);
        }

        try
        {
            if (HoldEachUpload > TimeSpan.Zero)
            {
                await Task.Delay(HoldEachUpload, cancellationToken).ConfigureAwait(false);
            }

            return await _inner
                .UploadAsync(localFilePath, destination, progress, cancellationToken, lastModified)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <inheritdoc />
    public Task<ServerCapabilities> GetCapabilitiesAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.GetCapabilitiesAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<WebDavResource>> ListAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<WebDavResource?> GetResourceAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.GetResourceAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetFileHashAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.GetFileHashAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetCollectionHashesAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.GetCollectionHashesAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task EnsureCollectionAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.EnsureCollectionAsync(path, cancellationToken);

    /// <inheritdoc />
    public Task UploadTextAsync(
        string content,
        RemotePath destination,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null) =>
        _inner.UploadTextAsync(content, destination, cancellationToken, lastModified);

    /// <inheritdoc />
    public Task MoveAsync(
        RemotePath source,
        RemotePath destination,
        bool overwrite = true,
        CancellationToken cancellationToken = default) =>
        _inner.MoveAsync(source, destination, overwrite, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(RemotePath path, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(path, cancellationToken);
}
