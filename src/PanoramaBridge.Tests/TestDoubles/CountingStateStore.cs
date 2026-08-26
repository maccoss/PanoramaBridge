using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.TestDoubles;

/// <summary>
/// A real ledger with a counter on every call.
/// </summary>
/// <remarks>
/// A decorator rather than a stand-in, because the assertions that matter here are about cost,
/// and a cost assertion against a fake that behaves differently from the real store proves
/// nothing. The store underneath is the actual SQLite implementation, so batching, collation and
/// concurrency all behave as they will in the field.
/// </remarks>
public sealed class CountingStateStore : IStateStore, IAsyncDisposable
{
    private readonly SqliteStateStore _inner;

    private int _gets;
    private int _batchedGets;
    private int _pathsLookedUp;
    private int _saves;

    public CountingStateStore(SqliteStateStore? inner = null) =>
        _inner = inner ?? SqliteStateStore.InMemory();

    /// <summary>Single-row lookups.</summary>
    public int Gets => Volatile.Read(ref _gets);

    /// <summary>Batched lookups, which is one statement each however many paths it carries.</summary>
    public int BatchedGets => Volatile.Read(ref _batchedGets);

    /// <summary>Paths asked about across all batched lookups.</summary>
    public int PathsLookedUp => Volatile.Read(ref _pathsLookedUp);

    /// <summary>Row writes.</summary>
    public int Saves => Volatile.Read(ref _saves);

    public void Reset()
    {
        Volatile.Write(ref _gets, 0);
        Volatile.Write(ref _batchedGets, 0);
        Volatile.Write(ref _pathsLookedUp, 0);
        Volatile.Write(ref _saves, 0);
    }

    /// <inheritdoc />
    public Task<UploadRecord?> GetAsync(string localPath, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _gets);
        return _inner.GetAsync(localPath, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, UploadRecord>> GetManyAsync(
        IReadOnlyCollection<string> localPaths,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _batchedGets);
        Interlocked.Add(ref _pathsLookedUp, localPaths.Count);
        return _inner.GetManyAsync(localPaths, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveAsync(UploadRecord record, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _saves);
        return _inner.SaveAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetStateAsync(
        string localPath,
        TransferState state,
        string? lastError = null,
        CancellationToken cancellationToken = default) =>
        _inner.SetStateAsync(localPath, state, lastError, cancellationToken);

    /// <inheritdoc />
    public Task MarkVerifiedAsync(
        string localPath,
        VerifyMethod method,
        DateTimeOffset verifiedUtc,
        CancellationToken cancellationToken = default) =>
        _inner.MarkVerifiedAsync(localPath, method, verifiedUtc, cancellationToken);

    /// <inheritdoc />
    public Task<UploadRecord?> FindByContentAsync(
        long length,
        string md5,
        CancellationToken cancellationToken = default) =>
        _inner.FindByContentAsync(length, md5, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadRecord>> GetInterruptedAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetInterruptedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadRecord>> GetByStateAsync(
        IReadOnlyCollection<TransferState> states,
        int limit = 1000,
        CancellationToken cancellationToken = default) =>
        _inner.GetByStateAsync(states, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<TransferState, int>> CountByStateAsync(
        CancellationToken cancellationToken = default) =>
        _inner.CountByStateAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ContentHashes?> GetCachedHashesAsync(
        LocalFileStamp stamp,
        CancellationToken cancellationToken = default) =>
        _inner.GetCachedHashesAsync(stamp, cancellationToken);

    /// <inheritdoc />
    public Task SaveCachedHashesAsync(
        LocalFileStamp stamp,
        ContentHashes hashes,
        CancellationToken cancellationToken = default) =>
        _inner.SaveCachedHashesAsync(stamp, hashes, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
