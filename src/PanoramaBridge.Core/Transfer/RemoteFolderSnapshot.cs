using System.Collections.Concurrent;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// What a destination folder contained at a point in time: names, sizes and server-computed
/// hashes.
/// </summary>
/// <param name="Folder">The collection this describes.</param>
/// <param name="TakenUtc">When it was fetched.</param>
/// <param name="Entries">Immediate children by name.</param>
/// <param name="Hashes">Server-computed MD5 by name, for the files in it.</param>
public sealed record RemoteFolderSnapshot(
    RemotePath Folder,
    DateTimeOffset TakenUtc,
    IReadOnlyDictionary<string, WebDavResource> Entries,
    IReadOnlyDictionary<string, string> Hashes)
{
    /// <summary>A folder that does not exist yet.</summary>
    public static RemoteFolderSnapshot Empty(RemotePath folder, DateTimeOffset takenUtc) =>
        new(
            folder,
            takenUtc,
            new Dictionary<string, WebDavResource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Looks up an entry by exact name.</summary>
    public WebDavResource? Find(string name) =>
        Entries.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>The server's hash for a file, if it reported one.</summary>
    public string? HashOf(string name) => Hashes.TryGetValue(name, out var hash) ? hash : null;
}

/// <summary>
/// Fetches and caches destination-folder snapshots.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns "is this file already on the server?" from a per-file question into a
/// per-folder one. Two requests -- one <c>?method=json</c> and one <c>?method=md5sum</c> --
/// answer it for every file in a folder, so a batch of two hundred acquisitions into one
/// directory costs two round trips instead of four hundred.
/// </para>
/// <para>
/// The Python version asked per file, and paid a full local SHA-256 as well, which is why
/// pointing it at a populated directory froze the window for minutes.
/// </para>
/// </remarks>
public sealed class RemoteSnapshotCache
{
    private readonly IWebDavClient _client;
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<RemotePath, Task<RemoteFolderSnapshot>> _cache = new();

    public RemoteSnapshotCache(
        IWebDavClient client,
        TimeSpan? lifetime = null,
        Func<DateTimeOffset>? clock = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _lifetime = lifetime ?? TimeSpan.FromMinutes(5);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Returns a snapshot of <paramref name="folder"/>, fetching it if there is no fresh one.
    /// </summary>
    /// <remarks>
    /// The in-flight task is cached rather than its result, so several workers arriving at the
    /// same folder at once share one pair of requests instead of racing to issue their own.
    /// </remarks>
    public async Task<RemoteFolderSnapshot> GetAsync(
        RemotePath folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var key = folder.AsCollection();

        while (true)
        {
            var pending = _cache.GetOrAdd(key, k => FetchAsync(k, cancellationToken));

            RemoteFolderSnapshot snapshot;
            try
            {
                snapshot = await pending.ConfigureAwait(false);
            }
            catch
            {
                // A failed fetch must not be cached, or one blip would poison the folder for
                // the rest of the session.
                _cache.TryRemove(new KeyValuePair<RemotePath, Task<RemoteFolderSnapshot>>(key, pending));
                throw;
            }

            if (_clock() - snapshot.TakenUtc <= _lifetime)
            {
                return snapshot;
            }

            _cache.TryRemove(new KeyValuePair<RemotePath, Task<RemoteFolderSnapshot>>(key, pending));
        }
    }

    /// <summary>
    /// Drops the cached snapshot for a folder.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="Record"/> after an upload. Dropping the snapshot means the next file
    /// into the same folder refetches it, and a refetch includes a collection hash, which the
    /// server computes on demand over every byte in the folder. Doing that once per uploaded
    /// file makes a batch quadratic in the size of the destination: measured against
    /// panoramaweb.org, a folder holding 19 GB takes half a minute to hash.
    /// </remarks>
    public void Invalidate(RemotePath folder) =>
        _cache.TryRemove(folder.AsCollection(), out _);

    /// <summary>
    /// Folds a file that has just been uploaded into the cached snapshot of its folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative -- dropping the snapshot so the next question refetches it -- is what this
    /// replaces, and it did not scale. Everything needed is already known: the destination, the
    /// number of bytes sent, and the hash computed during the upload's own pass over the file,
    /// which the server confirmed. So the cache can simply be told, instead of asking again.
    /// </para>
    /// <para>
    /// Only the length, the collection flag and the hash are read by the decision ladder. The
    /// rest of the entry is filled in as accurately as it can be from here and is not relied on.
    /// </para>
    /// </remarks>
    /// <param name="file">Where the file was written.</param>
    /// <param name="length">Bytes stored.</param>
    /// <param name="md5">The hash the upload produced and the server agreed with.</param>
    /// <param name="lastModified">What the server will now report for it.</param>
    public void Record(RemotePath file, long length, string md5, DateTimeOffset? lastModified = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        var key = file.Parent.AsCollection();

        if (!_cache.TryGetValue(key, out var pending))
        {
            return;
        }

        if (!pending.IsCompletedSuccessfully)
        {
            // A fetch is in flight. It may have been issued before this upload, in which case
            // its answer will not mention the new file, so the honest thing is to drop it.
            _cache.TryRemove(new KeyValuePair<RemotePath, Task<RemoteFolderSnapshot>>(key, pending));
            return;
        }

        var snapshot = pending.Result;

        var entries = new Dictionary<string, WebDavResource>(snapshot.Entries, StringComparer.Ordinal)
        {
            [file.Name] = new WebDavResource(
                file.Name,
                file,
                IsCollection: false,
                Length: length,
                LastModifiedUtc: lastModified ?? _clock(),
                ETag: null,
                ContentType: null,
                CreatedBy: null,
                Permissions: ResourcePermissions.Unknown),
        };

        var hashes = new Dictionary<string, string>(snapshot.Hashes, StringComparer.Ordinal)
        {
            [file.Name] = md5,
        };

        // Keeps the original TakenUtc, so recording does not extend the snapshot's life. Anything
        // another client changed in the folder still shows up when the lifetime expires.
        var updated = Task.FromResult(snapshot with { Entries = entries, Hashes = hashes });

        _cache.TryUpdate(key, updated, pending);
    }

    /// <summary>Drops everything. Used when the destination or credentials change.</summary>
    public void Clear() => _cache.Clear();

    private async Task<RemoteFolderSnapshot> FetchAsync(
        RemotePath folder,
        CancellationToken cancellationToken)
    {
        var takenUtc = _clock();

        IReadOnlyList<WebDavResource> entries;
        try
        {
            entries = await _client.ListAsync(folder, cancellationToken).ConfigureAwait(false);
        }
        catch (WebDavException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The destination folder has not been created yet, which is a normal starting
            // state rather than a failure.
            return RemoteFolderSnapshot.Empty(folder, takenUtc);
        }

        var hashes = entries.Any(e => !e.IsCollection)
            ? await _client.GetCollectionHashesAsync(folder, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new RemoteFolderSnapshot(
            folder,
            takenUtc,
            entries.ToDictionary(e => e.Name, e => e, StringComparer.Ordinal),
            hashes);
    }
}
