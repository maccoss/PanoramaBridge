using PanoramaBridge.Core.Hashing;

namespace PanoramaBridge.Core.Storage;

/// <summary>
/// The upload ledger and the local hash cache.
/// </summary>
/// <remarks>
/// <para>
/// Replaces two separate Python stores that both had structural problems: a pickle rewritten in
/// full on <em>every</em> successful upload, and a hash cache embedded inside the settings JSON
/// and therefore capped at a thousand entries with a crude eviction that threw away work.
/// </para>
/// <para>
/// Every method is safe to call from several upload workers at once.
/// </para>
/// </remarks>
public interface IStateStore
{
    /// <summary>Returns the ledger row for a local path, or null when it is unknown.</summary>
    Task<UploadRecord?> GetAsync(string localPath, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a ledger row.</summary>
    Task SaveAsync(UploadRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a state transition without rewriting the whole row.
    /// </summary>
    /// <remarks>
    /// Called before the side effect it describes, so a crash leaves the ledger describing an
    /// attempt that may or may not have happened rather than one that definitely did not.
    /// </remarks>
    Task SetStateAsync(
        string localPath,
        TransferState state,
        string? lastError = null,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a row verified, recording how it was checked.</summary>
    Task MarkVerifiedAsync(
        string localPath,
        VerifyMethod method,
        DateTimeOffset verifiedUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a settled row with the same content, whatever its path.
    /// </summary>
    /// <remarks>
    /// This is what stops a rename or a moved base directory from re-sending gigabytes: if the
    /// same bytes are already verified at the same destination, there is nothing to do.
    /// </remarks>
    Task<UploadRecord?> FindByContentAsync(
        long length,
        string md5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rows left mid-flight by a crash, which must be requeued at startup.
    /// </summary>
    Task<IReadOnlyList<UploadRecord>> GetInterruptedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Rows in the given states, most recent first.</summary>
    Task<IReadOnlyList<UploadRecord>> GetByStateAsync(
        IReadOnlyCollection<TransferState> states,
        int limit = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>How many rows are in each state. Drives the status summary.</summary>
    Task<IReadOnlyDictionary<TransferState, int>> CountByStateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns cached hashes for a file, or null when it has not been hashed at this exact
    /// size and modification time.
    /// </summary>
    Task<ContentHashes?> GetCachedHashesAsync(
        LocalFileStamp stamp,
        CancellationToken cancellationToken = default);

    /// <summary>Caches hashes against a file's size and modification time.</summary>
    Task SaveCachedHashesAsync(
        LocalFileStamp stamp,
        ContentHashes hashes,
        CancellationToken cancellationToken = default);
}
