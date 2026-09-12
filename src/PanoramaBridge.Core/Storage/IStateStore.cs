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
    /// <summary>Returns the ledger row for a file at a destination, or null when unknown.</summary>
    /// <remarks>
    /// Keyed by both halves since a file may have been sent to more than one destination by more
    /// than one configuration. Asking by path alone cannot say which of those rows is meant.
    /// </remarks>
    Task<UploadRecord?> GetAsync(LedgerKey key, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a ledger row.</summary>
    Task SaveAsync(UploadRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a state transition without rewriting the whole row.
    /// </summary>
    /// <remarks>
    /// Called before the side effect it describes, so a crash leaves the ledger describing an
    /// attempt that may or may not have happened rather than one that definitely did not.
    /// Throws when <paramref name="localPath"/> has no row; callers must save the initial row
    /// before recording a transition.
    /// </remarks>
    /// <summary>Records why a row is waiting, touching nothing else about it.</summary>
    /// <remarks>
    /// One column, deliberately. Saving the whole record instead re-stamps state and attempts
    /// from whatever snapshot the caller is holding, which on the recovery path is read before
    /// the workers start — so a worker's write could be undone by it.
    /// </remarks>
    Task SetErrorAsync(
        LedgerKey key, string? error, CancellationToken cancellationToken = default);

    Task SetStateAsync(
        LedgerKey key,
        TransferState state,
        string? lastError = null,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an existing row verified, recording how it was checked.</summary>
    Task MarkVerifiedAsync(
        LedgerKey key,
        VerifyMethod method,
        DateTimeOffset verifiedUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ledger rows for a batch of local paths, keyed by path.
    /// </summary>
    /// <returns>
    /// Every row for each path, which may be more than one: a file sent to two destinations by
    /// two configurations has a row for each. The caller picks the row for the destination it is
    /// asking about — or, when a path has no destination because it cannot be placed on the
    /// server at all, considers them together.
    /// </returns>
    /// <remarks>
    /// One statement per batch rather than one per file, because the reconciliation sweep asks
    /// this about every file in the monitored tree. At a hundred thousand files the difference
    /// between a batched read and a per-file read is the difference between a sweep that costs a
    /// second and one that costs minutes of disk work on the volume an instrument is writing to.
    /// Paths absent from the ledger are simply absent from the result.
    /// </remarks>
    Task<IReadOnlyDictionary<string, IReadOnlyList<UploadRecord>>> GetManyAsync(
        IReadOnlyCollection<string> localPaths,
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
