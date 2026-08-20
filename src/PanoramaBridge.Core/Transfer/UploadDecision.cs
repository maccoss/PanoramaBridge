using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Core.Transfer;

/// <summary>What to do when the destination is already occupied by something different.</summary>
public enum ConflictPolicy
{
    /// <summary>Stop and ask. The default, because the alternative risks losing data.</summary>
    Ask = 0,

    /// <summary>Leave the remote copy alone.</summary>
    Skip = 1,

    /// <summary>Replace the remote copy.</summary>
    Overwrite = 2,

    /// <summary>Upload alongside it under a new name.</summary>
    Rename = 3,
}

/// <summary>What should happen to a file.</summary>
public enum UploadAction
{
    /// <summary>Send it.</summary>
    Upload,

    /// <summary>Nothing to do: an identical copy is already there.</summary>
    Skip,

    /// <summary>Something different occupies the destination and a decision is needed.</summary>
    Conflict,
}

/// <summary>
/// Which tier of the decision ladder produced the answer.
/// </summary>
/// <remarks>
/// Recorded so the cost of a decision is visible. The whole design goal is that the common case
/// is answered by <see cref="Ledger"/> -- one indexed lookup, no hashing, no network.
/// </remarks>
public enum DecisionTier
{
    /// <summary>The local ledger already knew. One indexed lookup.</summary>
    Ledger = 0,

    /// <summary>A cached per-folder snapshot of the destination answered it.</summary>
    RemoteSnapshot = 1,

    /// <summary>The file had to be hashed.</summary>
    LocalHash = 2,
}

/// <summary>The outcome of asking whether a file needs uploading.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Tier">Which tier answered, and therefore what it cost.</param>
/// <param name="Reason">A sentence fit to show the user.</param>
/// <param name="Hashes">Local hashes, when the decision required computing them.</param>
/// <param name="RemoteHash">The server's hash for the destination, when one was known.</param>
/// <param name="AlreadyUploadedAs">
/// A different local file with identical content that is already verified at this destination.
/// </param>
public sealed record UploadDecision(
    UploadAction Action,
    DecisionTier Tier,
    string Reason,
    ContentHashes? Hashes = null,
    string? RemoteHash = null,
    UploadRecord? AlreadyUploadedAs = null)
{
    /// <summary>True when nothing needs to be sent.</summary>
    public bool IsSkip => Action == UploadAction.Skip;

    /// <summary>
    /// The verification standing implied by this decision. A skip justified by comparing the
    /// server's own hash is as strong as a fresh verified upload; anything weaker is not.
    /// </summary>
    public VerifyMethod ImpliedVerification =>
        Action == UploadAction.Skip && RemoteHash is not null
            ? VerifyMethod.ServerMd5
            : VerifyMethod.None;
}
