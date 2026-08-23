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
/// <param name="Hashes">
/// Server-computed MD5 by name, for whatever has been learned so far -- recorded by an upload,
/// or folded in by <see cref="RemoteSnapshotCache.HashOfAsync"/>. Empty on a fresh listing, and
/// deliberately so: asking the server for a folder's hashes is expensive and is not done until
/// something reads one. Use <see cref="RemoteSnapshotCache.HashOfAsync"/> rather than this.
/// </param>
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

    /// <summary>
    /// The server's hash for a file, if one is already known here.
    /// </summary>
    /// <remarks>
    /// Only what has been learned: recorded by an upload, or merged in after a fetch. A fresh
    /// listing carries none, so this returning null does not mean the server withheld a hash --
    /// it usually means nobody has asked yet. <see cref="RemoteSnapshotCache.HashOfAsync"/> is
    /// the one that asks, and is what callers deciding about a file should use.
    /// </remarks>
    public string? HashOf(string name) => Hashes.TryGetValue(name, out var hash) ? hash : null;
}

/// <summary>
/// Fetches and caches destination-folder snapshots.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns "is this file already on the server?" from a per-file question into a
/// per-folder one. One <c>?method=json</c> listing answers it for every file in a folder, so a
/// batch of two hundred acquisitions into one directory costs one round trip instead of two
/// hundred. The matching <c>?method=md5sum</c> is fetched only if something actually compares
/// content, and then also once for the whole folder.
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

    /// <summary>
    /// Folder hashes, fetched at most once per folder and only when one is read.
    /// </summary>
    /// <remarks>
    /// Dropped whenever the folder's listing is refetched, so a hash can never outlive the
    /// listing it belongs to. Without that these would have no expiry at all: a colleague
    /// replacing a file on the server, same size and different content, would be answered from
    /// an hours-old hash, the local copy would match it, and the file would be recorded
    /// "Verified (server MD5)" against something the server does not hold.
    /// </remarks>
    private readonly
        ConcurrentDictionary<RemotePath, Lazy<Task<IReadOnlyDictionary<string, string>>>>
        _hashes = new();

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
    /// into the same folder refetches its listing, and discards any hashes fetched for it -- so
    /// the next comparison pays for a collection hash again, which the server computes on demand
    /// over every byte in the folder. Measured against panoramaweb.org, a folder holding 19 GB
    /// takes half a minute. Doing that once per uploaded file makes a batch quadratic in the size
    /// of the destination.
    /// </remarks>
    public void Invalidate(RemotePath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var key = folder.AsCollection();

        // Both, or the caller who asked for the folder to be re-read gets a fresh listing
        // stamped with hashes from before it.
        _cache.TryRemove(key, out _);
        _hashes.TryRemove(key, out _);
    }

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

        // The fetched set is deliberately left alone. It predates this upload and so does not
        // mention the new file -- but the snapshot now carries that file's own hash, and
        // HashOfAsync reads the snapshot first, so the gap can never be reached. Dropping it
        // would instead make a mixed batch re-hash the whole folder after every single upload,
        // which is the quadratic cost this class exists to prevent.
    }

    /// <summary>
    /// The server's hash for one file in a folder, fetching the folder's hashes if needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="GetAsync"/>, and lazy, because the two questions cost wildly
    /// different amounts. A listing is one cheap request. A collection hash makes Panorama read
    /// <em>every byte in the folder</em> -- it computes them on demand, at roughly 600 MB/s -- so
    /// a destination holding 300 GB of previous acquisitions is minutes of server time.
    /// </para>
    /// <para>
    /// Fetching it with the listing meant paying that on the ordinary case: a new acquisition
    /// going into a populated folder. The listing already establishes the file is not there and
    /// must be uploaded; the hash is read only when a name matches, which for new work is never.
    /// Users saw "Checking server" sit for minutes before the first file moved, for a number
    /// nothing looked at.
    /// </para>
    /// <para>
    /// Still one request per folder rather than per file: the whole folder's hashes arrive
    /// together, so a batch re-offered into a populated destination pays it once.
    /// </para>
    /// <para>
    /// Takes the snapshot the caller already holds rather than fetching its own. Looking it up
    /// again could cross the cache lifetime and answer from a different listing, so the entry a
    /// caller decided about and the hash it compared could come from two different views of the
    /// folder -- and a file deleted in between would produce a conflict prompt about a file that
    /// is simply gone.
    /// </para>
    /// </remarks>
    public async Task<string?> HashOfAsync(
        RemoteFolderSnapshot snapshot,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var key = snapshot.Folder.AsCollection();
        var folder = snapshot.Folder;

        // Already known: recorded by an upload this session, or by an earlier fetch.
        if (snapshot.Hashes.TryGetValue(name, out var known))
        {
            return known;
        }

        // Nothing to ask about. Avoids a fetch for a folder the server does not have yet.
        if (snapshot.Entries.Count == 0)
        {
            return null;
        }

        // Nothing to hash: the folder holds only subdirectories, and md5sum would return an
        // empty set at the cost of a request.
        if (!snapshot.Entries.Values.Any(e => !e.IsCollection))
        {
            return null;
        }

        // Lazy, so the factory runs once however many workers arrive together. GetOrAdd's own
        // factory is not atomic, and a duplicate here is not a wasted round trip but minutes of
        // server-side hashing -- the exact cost this whole path exists to avoid.
        var pending = _hashes.GetOrAdd(
            key,
            k => new Lazy<Task<IReadOnlyDictionary<string, string>>>(
                () => _client.GetCollectionHashesAsync(k, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        IReadOnlyDictionary<string, string> hashes;
        try
        {
            hashes = await pending.Value.ConfigureAwait(false);
        }
        catch
        {
            // A failed fetch must not be cached, or one blip poisons the folder for the rest of
            // the session -- every later file whose name is already on the server would fail,
            // exhaust its attempts and be abandoned. The listing cache learned this already.
            _hashes.TryRemove(
                new KeyValuePair<RemotePath, Lazy<Task<IReadOnlyDictionary<string, string>>>>(
                    key, pending));
            throw;
        }

        Merge(key, hashes);

        return hashes.TryGetValue(name, out var hash) ? hash : null;
    }

    /// <summary>
    /// Folds fetched hashes into the cached snapshot, so the folder is asked once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anything already on the snapshot wins. Those entries can only have come from
    /// <see cref="Record"/> -- a refetch of the listing clears the fetched hashes with it -- so
    /// they describe an upload this session made, which is newer than any fetch.
    /// </para>
    /// <para>
    /// An earlier version claimed the same rule while merging in the other direction, and could
    /// not have implemented it: after one merge the snapshot holds that whole fetch, which is
    /// indistinguishable from a recorded entry, so a second merge overwrote fresh server hashes
    /// with stale ones.
    /// </para>
    /// <para>
    /// Skipped entirely when the snapshot already has every fetched name, which is the common
    /// case on repeat lookups. Copying a 35,000-entry map per file, on the transfer path, is not
    /// something to do for nothing.
    /// </para>
    /// </remarks>
    private void Merge(RemotePath key, IReadOnlyDictionary<string, string> hashes)
    {
        if (!_cache.TryGetValue(key, out var pending) || !pending.IsCompletedSuccessfully)
        {
            return;
        }

        var snapshot = pending.Result;

        if (hashes.Count == 0 || hashes.Keys.All(snapshot.Hashes.ContainsKey))
        {
            return;
        }

        var merged = new Dictionary<string, string>(hashes, StringComparer.Ordinal);

        foreach (var (name, hash) in snapshot.Hashes)
        {
            merged[name] = hash;
        }

        _cache.TryUpdate(key, Task.FromResult(snapshot with { Hashes = merged }), pending);
    }

    /// <summary>Drops everything. Used when the destination or credentials change.</summary>
    public void Clear()
    {
        _cache.Clear();
        _hashes.Clear();
    }

    private async Task<RemoteFolderSnapshot> FetchAsync(
        RemotePath folder,
        CancellationToken cancellationToken)
    {
        // This listing supersedes whatever was known about the folder, so anything hashed
        // against the previous one goes with it. This is what gives the hashes an expiry: they
        // inherit the snapshot's, rather than living forever in a dictionary of their own.
        _hashes.TryRemove(folder.AsCollection(), out _);

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

        // The listing only. Hashes are fetched by HashOfAsync, and only when something actually
        // reads one -- see the remark there for why that matters so much.
        return new RemoteFolderSnapshot(
            folder,
            takenUtc,
            entries.ToDictionary(e => e.Name, e => e, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
