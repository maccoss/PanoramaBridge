using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Core.Storage;

/// <summary>
/// Where a file stands in the transfer pipeline.
/// </summary>
/// <remarks>
/// Explicit states, persisted on every transition, replace the Python version's single
/// "Completed" label -- which meant three different things depending on how it was reached and
/// left the UI and the on-disk history free to disagree.
/// </remarks>
public enum TransferState
{
    /// <summary>Seen on disk but not yet considered stable enough to touch.</summary>
    Discovered = 0,

    /// <summary>Stable and accepted into the queue.</summary>
    Queued = 1,

    /// <summary>Bytes are moving.</summary>
    Uploading = 2,

    /// <summary>The server accepted the PUT but the copy has not been verified yet.</summary>
    Uploaded = 3,

    /// <summary>The remote copy has been confirmed. The terminal success state.</summary>
    Verified = 4,

    /// <summary>An identical copy was already on the server, so nothing was sent.</summary>
    Skipped = 5,

    /// <summary>A different file already occupies the destination; a decision is needed.</summary>
    Conflict = 6,

    /// <summary>Locked by another process, typically an instrument still writing to it.</summary>
    LockedRetrying = 7,

    /// <summary>The local file changed while it was being uploaded, so the result is void.</summary>
    Superseded = 8,

    /// <summary>Gave up. <see cref="UploadRecord.LastError"/> says why.</summary>
    Failed = 9,
}

/// <summary>
/// How thoroughly a remote copy was checked.
/// </summary>
/// <remarks>
/// Recorded and surfaced in the UI so a green tick never overstates what was actually proven.
/// The Python version could report "verified" after doing nothing more than downloading the
/// first 8 KB and finding it readable.
/// </remarks>
public enum VerifyMethod
{
    /// <summary>Not checked at all.</summary>
    None = 0,

    /// <summary>Only the byte count was compared. Catches truncation, nothing else.</summary>
    SizeOnly = 1,

    /// <summary>
    /// The server's own MD5, computed over the bytes it stored, matched the local hash. The
    /// only genuinely end-to-end result.
    /// </summary>
    ServerMd5 = 2,
}

/// <summary>
/// Identity of a local file at a moment in time.
/// </summary>
/// <remarks>
/// Size plus modification time is the cheap test for "has this changed since we last looked",
/// and it is what makes the fast path fast: answering it needs one stat call, no hashing and no
/// network.
/// </remarks>
/// <param name="Path">Full local path.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="LastWriteUnixMs">Modification time, UTC, in Unix milliseconds.</param>
public readonly record struct LocalFileStamp(string Path, long Length, long LastWriteUnixMs)
{
    /// <summary>Stats a file on disk.</summary>
    public static LocalFileStamp FromFile(string path)
    {
        var info = new FileInfo(path);
        return FromFileInfo(info);
    }

    /// <summary>Builds a stamp from an already-populated <see cref="FileInfo"/>.</summary>
    public static LocalFileStamp FromFileInfo(FileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new LocalFileStamp(
            info.FullName,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    /// <summary>True when size and modification time both still match.</summary>
    public bool Matches(long length, long lastWriteUnixMs) =>
        Length == length && LastWriteUnixMs == lastWriteUnixMs;
}

/// <summary>Why a row is held at <see cref="TransferState.Conflict"/>.</summary>
/// <remarks>
/// <para>
/// Restored after being withdrawn with the per-file decision feature, because one part of it was
/// never about that feature: the sweep releases held files when the conflict policy is an answer,
/// and two kinds of held row must not be released that way. The numeric values match what
/// v26.4.1—v26.4.6 wrote into the <c>conflict_kind</c> column, so rows from those builds are
/// read correctly without a migration.
/// </para>
/// <para>
/// The lesson this enum keeps encoding: telling these apart by comparing message strings was
/// tried, and broke the moment a message was reworded. The reason a row is held has to be stored,
/// not inferred.
/// </para>
/// </remarks>
public enum ConflictKind
{
    /// <summary>Not recorded — a row written before v26.4.1, or by v26.5.0.</summary>
    Unknown = 0,

    /// <summary>Something different occupies the destination.</summary>
    DestinationOccupied = 1,

    /// <summary>
    /// The local file is damaged: reading it proved it ends before its data does.
    /// </summary>
    /// <remarks>
    /// The conflict policy is not an answer to this. Skip would bury a broken acquisition, and
    /// Overwrite would push it over a good remote copy — the outcome the truncation check
    /// exists to prevent. The row is held until the local file changes, whatever the policy.
    /// </remarks>
    LocalFileDamaged = 2,

}

/// <summary>
/// One row of the upload ledger: what is known about a local file and its remote copy.
/// </summary>
/// <param name="LocalPath">Full local path. The primary key.</param>
/// <param name="RemotePath">Encoded destination path.</param>
/// <param name="Length">Local size at the point recorded.</param>
/// <param name="LastWriteUnixMs">Local modification time at the point recorded.</param>
/// <param name="Md5">Lower-case hex MD5, once known.</param>
/// <param name="Sha256">Lower-case hex SHA-256, kept as the provenance record.</param>
/// <param name="State">Where it stands.</param>
/// <param name="VerifyMethod">How the remote copy was checked.</param>
/// <param name="VerifiedUtc">When verification last succeeded.</param>
/// <param name="Attempts">Upload attempts so far.</param>
/// <param name="LastError">Why the last attempt failed.</param>
public sealed record UploadRecord(
    string LocalPath,
    string RemotePath,
    long Length,
    long LastWriteUnixMs,
    string? Md5,
    string? Sha256,
    TransferState State,
    VerifyMethod VerifyMethod,
    DateTimeOffset? VerifiedUtc,
    int Attempts,
    string? LastError,
    string? RawCheck = null,
    ConflictKind ConflictKind = ConflictKind.Unknown)
{
    /// <summary>
    /// True when this file is known to be safely on the server, unchanged since.
    /// </summary>
    /// <remarks>
    /// Requires the hash to have actually been checked. A row that reached
    /// <see cref="TransferState.Uploaded"/> but never got past
    /// <see cref="VerifyMethod.SizeOnly"/> is not treated as settled, which is what stops a
    /// truncated or mis-stored copy from being mistaken for a good one.
    /// </remarks>
    public bool IsSettled(LocalFileStamp stamp) =>
        (State is TransferState.Verified or TransferState.Skipped)
        && VerifyMethod == VerifyMethod.ServerMd5
        && stamp.Matches(Length, LastWriteUnixMs);

    /// <summary>
    /// True when this file is settled <em>and</em> settled at the destination it would be sent
    /// to now.
    /// </summary>
    /// <remarks>
    /// The destination has to be part of the question. A verified row proves the bytes reached
    /// somewhere; it does not prove they reached the folder currently configured. Point the
    /// application at a different remote path and every file needs sending again, however
    /// thoroughly the old copy was checked.
    /// <para>
    /// Shared by the decision ladder's first tier and by the reconciliation sweep so the two can
    /// not drift: the sweep deciding a file is accounted for when the ladder would not agree
    /// means the file is silently never offered.
    /// </para>
    /// </remarks>
    public bool IsSettledAt(LocalFileStamp stamp, string encodedDestination) =>
        IsSettled(stamp)
        && string.Equals(RemotePath, encodedDestination, StringComparison.Ordinal);

    /// <summary>
    /// Whether this row is held for a reason the conflict policy is not an answer to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One reason, currently: the local file is damaged. A second, for decisions carried over
    /// from the withdrawn per-file feature, was written and then removed — it protected no rows
    /// that exist. The ledger on the machine that ran every one of those builds, through the whole
    /// life of the feature, holds 806 rows and not one of them was kept by choice or sent under a
    /// different name. What it did produce was a value v26.4.x reads but does not define, which
    /// that build's bulk actions then swept up, and a documented Skip route that sent the file
    /// whenever its original name happened to be free.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// One predicate, two callers — the sweep, which uses it to avoid queueing the file at all,
    /// and the coordinator, which uses it as the actual gate. It lives here because putting it
    /// only in the sweep is what made it bypassable: a file reaches the coordinator from the
    /// folder watcher and from <c>pbctl sync</c> as well, and neither goes past the sweep.
    /// Duplicating this decision rather than sharing it is the mistake that cost the most in this
    /// area already.
    /// </para>
    /// <para>
    /// Deliberately independent of <see cref="State"/>. Tying it to <see cref="TransferState.Conflict"/>
    /// left a two-step way round: retire the row under Skip, which saves it
    /// <see cref="TransferState.Skipped"/> with the kind intact, then change to Overwrite — and
    /// a check that only looked at Conflict rows no longer applied. The reason a row is held
    /// outlives the state it was recorded in.
    /// </para>
    /// <para>
    /// Callers must test the stamp first. A file that changed is a new question, and both of
    /// these holds are meant to be reopened by fixing or replacing the file.
    /// </para>
    /// </remarks>
    public bool IsHeldRegardlessOfPolicy =>
        ConflictKind switch
        {
            // The policy answers "something else is at the destination". It is not an answer to
            // "this file is broken": Skip would bury a damaged acquisition, and Overwrite would
            // push it over a good remote copy.
            ConflictKind.LocalFileDamaged => true,

            _ => false,
        };

    /// <summary>A new row for a file that has just been discovered.</summary>
    public static UploadRecord ForNewFile(LocalFileStamp stamp, string remotePath) =>
        new(
            LocalPath: stamp.Path,
            RemotePath: remotePath,
            Length: stamp.Length,
            LastWriteUnixMs: stamp.LastWriteUnixMs,
            Md5: null,
            Sha256: null,
            State: TransferState.Discovered,
            VerifyMethod: VerifyMethod.None,
            VerifiedUtc: null,
            Attempts: 0,
            LastError: null);

    /// <summary>Returns this row with hashes attached.</summary>
    public UploadRecord WithHashes(ContentHashes hashes) =>
        this with { Md5 = hashes.Md5, Sha256 = hashes.Sha256 };
}
