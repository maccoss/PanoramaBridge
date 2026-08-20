using System.Collections.Concurrent;
using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.TestDoubles;

/// <summary>
/// An in-memory WebDAV server good enough for engine-level tests.
/// </summary>
/// <remarks>
/// Holds a tree of files with real content, so hashes are genuine rather than stubbed, and
/// counts every call. The counters are what let a test assert that the fast path costs nothing
/// -- an assertion about absence, which is otherwise very easy to get wrong.
/// </remarks>
public sealed class FakeWebDavClient : IWebDavClient
{
    // Concurrent: several upload workers mutate these at once, and a plain Dictionary
    // silently loses entries under that load -- which then shows up as a verification failure
    // rather than as the data race it actually is.
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _collections =
        new(new[] { new KeyValuePair<string, byte>("/", 0) }, StringComparer.Ordinal);

    private int _listCalls;
    private int _collectionHashCalls;
    private int _fileHashCalls;
    private int _uploadCalls;
    private int _mkcolCalls;
    private int _textUploadCalls;

    /// <summary>Calls to each operation, for asserting on cost.</summary>
    public int ListCalls => Volatile.Read(ref _listCalls);

    public int CollectionHashCalls => Volatile.Read(ref _collectionHashCalls);

    public int FileHashCalls => Volatile.Read(ref _fileHashCalls);

    public int UploadCalls => Volatile.Read(ref _uploadCalls);

    public int MkcolCalls => Volatile.Read(ref _mkcolCalls);

    /// <summary>Small text writes, such as checksum sidecars.</summary>
    public int TextUploadCalls => Volatile.Read(ref _textUploadCalls);

    /// <summary>Total requests of any kind.</summary>
    public int TotalCalls =>
        ListCalls + CollectionHashCalls + FileHashCalls + UploadCalls + MkcolCalls + TextUploadCalls;

    /// <summary>Forces the next upload to fail this many times before succeeding.</summary>
    public int FailUploadsBeforeSucceeding { get; set; }

    /// <summary>When set, the server reports this instead of the true hash. Simulates corruption.</summary>
    public string? OverrideReportedHash { get; set; }

    /// <summary>When true, small text writes fail, as a read-only or full server would.</summary>
    public bool FailTextUploads { get; set; }

    /// <summary>
    /// Collections the account may read but not write to, by encoded path.
    /// </summary>
    /// <remarks>
    /// The permission flags come back with the listing on a real server, which is what lets the
    /// folder browser refuse a read-only destination before a transfer rather than after one.
    /// </remarks>
    public HashSet<string> ReadOnlyPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>Makes the next listing fail once, as a transient problem would.</summary>
    public bool FailListingsOnce { get; set; }

    /// <summary>When true, collection hash requests report nothing, as a locked-down server might.</summary>
    public bool WithholdHashes { get; set; }

    /// <summary>Mirrors the client option, so a test can opt into SHA-256 as the app would.</summary>
    public bool RecordSha256 { get; set; }

    public void Reset()
    {
        Volatile.Write(ref _listCalls, 0);
        Volatile.Write(ref _collectionHashCalls, 0);
        Volatile.Write(ref _fileHashCalls, 0);
        Volatile.Write(ref _uploadCalls, 0);
        Volatile.Write(ref _mkcolCalls, 0);
        Volatile.Write(ref _textUploadCalls, 0);
    }

    /// <summary>Seeds a file with real content.</summary>
    public void Seed(RemotePath path, byte[] content)
    {
        _files[path.AsCollection(false).ToEncodedString()] = content;

        for (var folder = path.Parent; !folder.IsRoot; folder = folder.Parent)
        {
            _collections.TryAdd(folder.AsCollection().ToEncodedString(), 0);
        }
    }

    /// <summary>
    /// What each stored file was stamped with, as LabKey's X-LABKEY-Last-Modified would.
    /// </summary>
    /// <remarks>
    /// Recorded so a test can assert that an acquisition keeps the date the instrument wrote it.
    /// A real server stamps an upload with its arrival time unless it is told otherwise, and the
    /// whole point is that it is told otherwise.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _stamps = new(StringComparer.Ordinal);

    /// <summary>The modification time the server would report for a path, if one was supplied.</summary>
    public DateTimeOffset? StampOf(RemotePath path) =>
        _stamps.TryGetValue(path.AsCollection(false).ToEncodedString(), out var stamp) ? stamp : null;

    private void Stamp(RemotePath path, DateTimeOffset? lastModified)
    {
        if (lastModified is { } moment)
        {
            _stamps[path.AsCollection(false).ToEncodedString()] = moment;
        }
    }

    /// <summary>The stored text at a path, or null.</summary>
    public string? Text(RemotePath path) =>
        Content(path) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    /// <summary>The stored bytes at a path, or null.</summary>
    public byte[]? Content(RemotePath path) =>
        _files.TryGetValue(path.AsCollection(false).ToEncodedString(), out var bytes) ? bytes : null;

    public Task<ServerCapabilities> GetCapabilitiesAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ServerCapabilities(
            "FakeLabKey/1.0",
            "1,2",
            ["OPTIONS", "GET", "HEAD", "PUT", "MKCOL", "MOVE", "DELETE", "PROPFIND"]));

    public Task<IReadOnlyList<WebDavResource>> ListAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listCalls);

        if (FailListingsOnce)
        {
            FailListingsOnce = false;
            throw new WebDavException(
                "GET(json)",
                collection,
                System.Net.HttpStatusCode.ServiceUnavailable);
        }

        var prefix = collection.AsCollection().ToEncodedString();
        if (!_collections.ContainsKey(prefix))
        {
            throw new WebDavException("GET(json)", collection, System.Net.HttpStatusCode.NotFound);
        }

        var results = new List<WebDavResource>();

        foreach (var (path, content) in _files)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)
                && !path[prefix.Length..].Contains('/', StringComparison.Ordinal))
            {
                var name = path[prefix.Length..];
                results.Add(new WebDavResource(
                    name,
                    collection.AsCollection().Append(name),
                    IsCollection: false,
                    Length: content.Length,
                    LastModifiedUtc: null,
                    ETag: null,
                    ContentType: null,
                    CreatedBy: null,
                    Permissions: Writable));
            }
        }

        foreach (var folder in _collections.Keys)
        {
            if (folder != prefix
                && folder.StartsWith(prefix, StringComparison.Ordinal)
                && folder[prefix.Length..].TrimEnd('/').Length > 0
                && !folder[prefix.Length..].TrimEnd('/').Contains('/', StringComparison.Ordinal))
            {
                var name = folder[prefix.Length..].TrimEnd('/');
                results.Add(new WebDavResource(
                    name,
                    collection.AsCollection().Append(name, isCollection: true),
                    IsCollection: true,
                    Length: 0,
                    LastModifiedUtc: null,
                    ETag: null,
                    ContentType: null,
                    CreatedBy: null,
                    Permissions: ReadOnlyPaths.Contains(folder) ? ReadOnly : Writable));
            }
        }

        return Task.FromResult<IReadOnlyList<WebDavResource>>(results);
    }

    public Task<WebDavResource?> GetResourceAsync(
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        ListAsync(path.Parent, cancellationToken)
            .ContinueWith(
                t => t.Result.FirstOrDefault(r => r.Name == path.Name),
                cancellationToken,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);

    public Task<string?> GetFileHashAsync(
        RemotePath file,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fileHashCalls);

        var content = Content(file);
        if (content is null || WithholdHashes)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(OverrideReportedHash ?? Md5Of(content));
    }

    public Task<IReadOnlyDictionary<string, string>> GetCollectionHashesAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _collectionHashCalls);

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (WithholdHashes)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(hashes);
        }

        var prefix = collection.AsCollection().ToEncodedString();
        foreach (var (path, content) in _files)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)
                && !path[prefix.Length..].Contains('/', StringComparison.Ordinal))
            {
                hashes[path[prefix.Length..]] = OverrideReportedHash ?? Md5Of(content);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(hashes);
    }

    public Task EnsureCollectionAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _mkcolCalls);

        for (var folder = collection.AsCollection(); !folder.IsRoot; folder = folder.Parent)
        {
            _collections.TryAdd(folder.AsCollection().ToEncodedString(), 0);
        }

        return Task.CompletedTask;
    }

    public async Task<UploadResult> UploadAsync(
        string localFilePath,
        RemotePath destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null)
    {
        Interlocked.Increment(ref _uploadCalls);
        Stamp(destination, lastModified);

        if (FailUploadsBeforeSucceeding > 0)
        {
            FailUploadsBeforeSucceeding--;
            throw new HttpRequestException("simulated transport failure");
        }

        await EnsureCollectionAsync(destination.Parent, cancellationToken).ConfigureAwait(false);

        // Read the real file so the hashes and stored bytes are genuine.
        var content = await File.ReadAllBytesAsync(localFilePath, cancellationToken).ConfigureAwait(false);
        _files[destination.AsCollection(false).ToEncodedString()] = content;

        progress?.Report(content.Length);

        return new UploadResult(
            destination,
            content.Length,
            // Matches the real client's default: SHA-256 is opt-in.
            new ContentHashes(Md5Of(content), RecordSha256 ? Sha256Of(content) : null),
            TimeSpan.FromMilliseconds(1));
    }

    public Task UploadTextAsync(
        string content,
        RemotePath destination,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null)
    {
        Interlocked.Increment(ref _textUploadCalls);

        if (FailTextUploads)
        {
            throw new WebDavException(
                "PUT",
                destination,
                System.Net.HttpStatusCode.Forbidden,
                "The server refused it.");
        }

        _files[destination.AsCollection(false).ToEncodedString()] =
            System.Text.Encoding.UTF8.GetBytes(content);

        Stamp(destination, lastModified);

        for (var folder = destination.Parent; !folder.IsRoot; folder = folder.Parent)
        {
            _collections.TryAdd(folder.AsCollection().ToEncodedString(), 0);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(
        RemotePath source,
        RemotePath destination,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        var from = source.AsCollection(false).ToEncodedString();
        if (_files.Remove(from, out var content))
        {
            _files[destination.AsCollection(false).ToEncodedString()] = content;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(RemotePath path, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(path.AsCollection(false).ToEncodedString(), out _);
        _collections.TryRemove(path.AsCollection().ToEncodedString(), out _);
        return Task.CompletedTask;
    }

    /// <summary>What a folder the account may look at but not write to reports.</summary>
    private static ResourcePermissions ReadOnly => new(
        CanRead: true,
        CanUpload: false,
        CanEdit: false,
        CanDelete: false,
        CanRename: false,
        AllowedMethods: ["GET"]);

    private static ResourcePermissions Writable => new(
        CanRead: true,
        CanUpload: true,
        CanEdit: true,
        CanDelete: true,
        CanRename: true,
        AllowedMethods: ["GET", "PUT", "MKCOL", "MOVE", "DELETE"]);

    internal static string Md5Of(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant();

    internal static string Sha256Of(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>An <see cref="IFileHasher"/> that counts how often it was asked to work.</summary>
public sealed class CountingFileHasher : IFileHasher
{
    private readonly IFileHasher _inner = new FileHasher();

    /// <summary>How many files have been hashed.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public Task<ContentHashes> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        Calls++;
        return _inner.ComputeAsync(path, cancellationToken);
    }
}
