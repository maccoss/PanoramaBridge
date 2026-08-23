using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// Decides whether a file needs uploading, spending as little as possible to find out.
/// </summary>
/// <remarks>
/// <para>
/// A three-tier ladder, cheapest first:
/// </para>
/// <list type="number">
/// <item>
/// <b>Ledger.</b> One indexed lookup keyed on the local path. If the recorded size and
/// modification time still match and the row was verified against the server's own hash, the
/// file is settled. No hashing, no network. This is the overwhelmingly common case and it has
/// to stay free.
/// </item>
/// <item>
/// <b>Remote snapshot.</b> One <c>?method=json</c> plus one <c>?method=md5sum</c> per
/// destination folder, cached, answers the question for every file in that folder.
/// </item>
/// <item>
/// <b>Local hash.</b> Only when the first two are inconclusive, and only after checking the
/// hash cache. When the answer turns out to be "upload", the hash is not computed here at all:
/// it comes free from the upload's own single pass over the file.
/// </item>
/// </list>
/// <para>
/// The Python version inverted this: it hashed a multi-gigabyte file and made two or three
/// round trips <em>before</em> deciding whether the upload was needed, on the UI thread.
/// </para>
/// </remarks>
public sealed class UploadDecisionService
{
    private readonly IStateStore _store;
    private readonly RemoteSnapshotCache _snapshots;
    private readonly IFileHasher _hasher;
    private readonly ILogger<UploadDecisionService> _log;

    public UploadDecisionService(
        IStateStore store,
        RemoteSnapshotCache snapshots,
        IFileHasher? hasher = null,
        ILogger<UploadDecisionService>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _hasher = hasher ?? new FileHasher();
        _log = log ?? NullLogger<UploadDecisionService>.Instance;
    }

    /// <summary>
    /// Works out what to do with <paramref name="stamp"/> given its intended
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="stamp">The local file being decided about.</param>
    /// <param name="destination">Where it would go.</param>
    /// <param name="policy">What to do about a clash.</param>
    /// <param name="cancellationToken">Stops the decision.</param>
    /// <param name="onStep">
    /// Told what the ladder is about to spend time on, so a file is never shown sitting idle
    /// while something slow happens on its behalf. Both of the expensive tiers can take a
    /// noticeable while: the server computes a folder's hashes on demand over every byte in it,
    /// and hashing a seven-gigabyte acquisition locally is not instant either.
    /// </param>
    public async Task<UploadDecision> DecideAsync(
        LocalFileStamp stamp,
        RemotePath destination,
        ConflictPolicy policy,
        CancellationToken cancellationToken = default,
        Action<string>? onStep = null)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var encodedDestination = destination.ToEncodedString();

        // -- Tier 0: the ledger already knows -------------------------------------------------
        var existing = await _store.GetAsync(stamp.Path, cancellationToken).ConfigureAwait(false);

        if (existing is not null && existing.IsSettledAt(stamp, encodedDestination))
        {
            return new UploadDecision(
                UploadAction.Skip,
                DecisionTier.Ledger,
                "Already uploaded and verified; unchanged since.",
                RemoteHash: existing.Md5);
        }

        // -- Tier 1: what is actually in the destination folder -------------------------------
        onStep?.Invoke("Checking server");

        var snapshot = await _snapshots
            .GetAsync(destination.Parent, cancellationToken)
            .ConfigureAwait(false);

        var remote = snapshot.Find(destination.Name);

        if (remote is null)
        {
            // Nothing there. The hash is deliberately not computed: the upload will produce it
            // in the same pass that sends the bytes.
            return new UploadDecision(
                UploadAction.Upload,
                DecisionTier.RemoteSnapshot,
                "Not present on the server.");
        }

        if (remote.IsCollection)
        {
            return new UploadDecision(
                UploadAction.Conflict,
                DecisionTier.RemoteSnapshot,
                $"A folder named '{destination.Name}' already occupies the destination.");
        }

        // Only now, with a name that matches, is a hash worth what the server pays to compute it.
        var remoteHash = await _snapshots
            .HashOfAsync(destination.Parent, destination.Name, cancellationToken)
            .ConfigureAwait(false);

        // Is the remote copy the one this application put there? If the ledger's recorded hash
        // for this destination still matches what the server holds, then nobody else has
        // touched it and a local change is simply a newer version to send -- not a clash
        // needing a human decision. Only a remote copy we cannot account for is a conflict.
        var remoteIsOurLastUpload =
            existing is not null
            && string.Equals(existing.RemotePath, encodedDestination, StringComparison.Ordinal)
            && existing.Md5 is not null
            && remoteHash is not null
            && string.Equals(existing.Md5, remoteHash, StringComparison.OrdinalIgnoreCase);

        if (remote.Length != stamp.Length)
        {
            // Different size is conclusive without hashing either side.
            if (remoteIsOurLastUpload)
            {
                return new UploadDecision(
                    UploadAction.Upload,
                    DecisionTier.RemoteSnapshot,
                    "The local file has changed since it was uploaded.");
            }

            return Resolve(
                policy,
                DecisionTier.RemoteSnapshot,
                $"A different file is already there ({remote.Length:N0} bytes on the server, "
                + $"{stamp.Length:N0} locally).");
        }

        if (remoteHash is null)
        {
            // Same size but the server would not tell us its hash. Size alone is too weak to
            // call it identical, so treat it as a conflict rather than assume.
            return Resolve(
                policy,
                DecisionTier.RemoteSnapshot,
                $"A file of the same size is already there, but the server did not report a "
                + $"hash for it, so it cannot be confirmed identical.");
        }

        // -- Tier 2: hash the local file, cache first -----------------------------------------
        onStep?.Invoke("Checking file");

        var hashes = await GetHashesAsync(stamp, cancellationToken).ConfigureAwait(false);

        if (string.Equals(hashes.Md5, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            return new UploadDecision(
                UploadAction.Skip,
                DecisionTier.LocalHash,
                "An identical copy is already on the server.",
                hashes,
                remoteHash);
        }

        if (remoteIsOurLastUpload)
        {
            return new UploadDecision(
                UploadAction.Upload,
                DecisionTier.LocalHash,
                "The local file has changed since it was uploaded.",
                hashes);
        }

        // Same name, same size, different content, and the remote copy is not one we can
        // account for. Surfacing that a matching copy exists elsewhere helps explain what
        // happened.
        var elsewhere = await _store
            .FindByContentAsync(stamp.Length, hashes.Md5, cancellationToken)
            .ConfigureAwait(false);

        return Resolve(
            policy,
            DecisionTier.LocalHash,
            "A file with the same name and size but different content is already on the server.",
            hashes,
            remoteHash,
            elsewhere);
    }

    /// <summary>
    /// Returns hashes for a file, using the cache when the file has not changed.
    /// </summary>
    public async Task<ContentHashes> GetHashesAsync(
        LocalFileStamp stamp,
        CancellationToken cancellationToken = default)
    {
        var cached = await _store.GetCachedHashesAsync(stamp, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.Value;
        }

        _log.LogDebug("Hashing {Path} ({Bytes:N0} bytes).", stamp.Path, stamp.Length);

        var hashes = await _hasher.ComputeAsync(stamp.Path, cancellationToken).ConfigureAwait(false);
        await _store.SaveCachedHashesAsync(stamp, hashes, cancellationToken).ConfigureAwait(false);

        return hashes;
    }

    private static UploadDecision Resolve(
        ConflictPolicy policy,
        DecisionTier tier,
        string reason,
        ContentHashes? hashes = null,
        string? remoteHash = null,
        UploadRecord? elsewhere = null) => policy switch
        {
            ConflictPolicy.Skip => new UploadDecision(
                UploadAction.Skip, tier, reason + " Skipped by policy.", hashes, null, elsewhere),

            // Deliberately does not carry the remote hash forward: the copy about to be made
            // has not been verified yet, and claiming otherwise would overstate the result.
            ConflictPolicy.Overwrite => new UploadDecision(
                UploadAction.Upload, tier, reason + " Overwriting by policy.", hashes, null, elsewhere),

            // Renaming is the caller's job, since it has to pick a name that is still free.
            ConflictPolicy.Rename => new UploadDecision(
                UploadAction.Conflict, tier, reason + " A new name is needed.", hashes, remoteHash, elsewhere),

            _ => new UploadDecision(
                UploadAction.Conflict, tier, reason, hashes, remoteHash, elsewhere),
        };
}

/// <summary>Computes content hashes for a local file.</summary>
public interface IFileHasher
{
    /// <summary>Hashes a file in one pass.</summary>
    Task<ContentHashes> ComputeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Hashes a file by streaming it once through <see cref="HashingReadStream"/>.</summary>
public sealed class FileHasher : IFileHasher
{
    /// <inheritdoc />
    public async Task<ContentHashes> ComputeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            // Never block an instrument that is writing alongside us.
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var hashing = new HashingReadStream(file, leaveOpen: true);
        await hashing.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

        return hashing.GetHashes();
    }
}
