using PanoramaBridge.Core.Hashing;

namespace PanoramaBridge.Core.WebDav;

/// <summary>Outcome of an upload.</summary>
/// <param name="Path">Where the file ended up.</param>
/// <param name="BytesUploaded">How many bytes were sent.</param>
/// <param name="Hashes">Hashes computed from the same pass that streamed the file.</param>
/// <param name="Elapsed">Wall-clock time for the transfer.</param>
public readonly record struct UploadResult(
    RemotePath Path,
    long BytesUploaded,
    ContentHashes Hashes,
    TimeSpan Elapsed)
{
    /// <summary>Average throughput in bytes per second, or zero if it was instantaneous.</summary>
    public double BytesPerSecond =>
        Elapsed.TotalSeconds > 0 ? BytesUploaded / Elapsed.TotalSeconds : 0;
}

/// <summary>What the server supports at a given location.</summary>
/// <param name="ServerName">The <c>Server</c> header, e.g. <c>LabKey/26.7-SNAPSHOT</c>.</param>
/// <param name="DavCompliance">Reported DAV compliance classes, e.g. <c>1,2</c>.</param>
/// <param name="AllowedMethods">Verbs from the <c>Allow</c> header.</param>
public readonly record struct ServerCapabilities(
    string? ServerName,
    string? DavCompliance,
    IReadOnlyList<string> AllowedMethods)
{
    /// <summary>True when the server will accept the named verb.</summary>
    public bool Allows(string method) =>
        AllowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a file can be uploaded under a temporary name and renamed into place, so a
    /// partial upload is never visible under the final name.
    /// </summary>
    public bool SupportsAtomicPublish => Allows("PUT") && Allows("MOVE");
}

/// <summary>
/// The WebDAV operations PanoramaBridge needs.
/// </summary>
/// <remarks>
/// Every method takes a cancellation token, and every failure that is worth telling the user
/// about arrives as a <see cref="WebDavException"/> carrying the method, path, status and the
/// server's own response body -- never as a bare boolean.
/// </remarks>
public interface IWebDavClient
{
    /// <summary>Reports what the server allows at <paramref name="path"/>.</summary>
    Task<ServerCapabilities> GetCapabilitiesAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a collection's immediate children.</summary>
    Task<IReadOnlyList<WebDavResource>> ListAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a single resource, or null when it does not exist.</summary>
    Task<WebDavResource?> GetResourceAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the server-computed MD5 of a file, or null when the file does not exist.
    /// </summary>
    Task<string?> GetFileHashAsync(
        RemotePath file,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns server-computed MD5s for every file directly inside a collection, keyed by name.
    /// </summary>
    /// <remarks>
    /// One request covers the whole collection, which is what turns verification from a
    /// per-file round trip into a per-directory one. It does not recurse: subdirectories are
    /// omitted, so a nested tree needs one call per directory.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> GetCollectionHashesAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a collection and any missing ancestors.
    /// </summary>
    /// <remarks>
    /// Necessary because the server's MKCOL is single-level: creating <c>a/b/c</c> in one call
    /// returns 409 when <c>a/b</c> does not yet exist. Already-created collections are
    /// remembered for the lifetime of the client, so a batch of uploads into one folder does
    /// not re-issue the same request repeatedly.
    /// </remarks>
    Task EnsureCollectionAsync(
        RemotePath collection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file, hashing it in the same pass.
    /// </summary>
    /// <param name="localFilePath">The file to send.</param>
    /// <param name="destination">Where it should land.</param>
    /// <param name="progress">Reports cumulative bytes handed to the socket.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <param name="lastModified">
    /// The time to stamp the stored file with, normally when the instrument wrote it. Null lets
    /// the server stamp it with the time it arrived, which loses the acquisition date.
    /// </param>
    Task<UploadResult> UploadAsync(
        string localFilePath,
        RemotePath destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null);

    /// <summary>
    /// Uploads a small piece of text, such as a checksum sidecar.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UploadAsync"/> because none of what makes that method careful --
    /// streaming from a file handle shared with an instrument, hashing in the same pass, a stall
    /// watchdog -- applies to a few hundred bytes held in memory.
    /// </remarks>
    /// <param name="content">What to write.</param>
    /// <param name="destination">Where it should land.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="lastModified">The time to stamp the stored file with.</param>
    Task UploadTextAsync(
        string content,
        RemotePath destination,
        CancellationToken cancellationToken = default,
        DateTimeOffset? lastModified = null);

    /// <summary>Renames or moves a resource.</summary>
    Task MoveAsync(
        RemotePath source,
        RemotePath destination,
        bool overwrite = true,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a resource. Succeeds silently when it is already absent.</summary>
    Task DeleteAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);
}
