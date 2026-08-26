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

    /// <summary>
    /// Withdrawn. Retained only so a settings file that names it can still be read.
    /// </summary>
    /// <remarks>
    /// Sending a file alongside under a free name needed a per-file record of where it actually
    /// went. Without that record the sweep resolves the file to its original name, decides the row
    /// describes somewhere else, and offers it again on every pass for ever — a loop that was
    /// found and fixed twice before the feature was withdrawn.
    /// <para>
    /// The member stays because removing it is not free: settings are stored as JSON with a string
    /// enum converter, so a file saying <c>"ConflictPolicy": "Rename"</c> throws while parsing, and
    /// the store's response to an unreadable file is to move it aside and start from defaults.
    /// Deleting this would have cost anyone who chose it their server, their monitored folder and
    /// every other setting, on the first launch after updating.
    /// </para>
    /// <para>
    /// Treated as <see cref="Ask"/> wherever a policy is acted on, which is the safe reading: hold
    /// the file and show it rather than guess.
    /// </para>
    /// </remarks>
    Rename = 3,
}

// Rename was a fourth choice: send it alongside under a free name. It is gone with the per-file
// conflict machinery, because recording where a renamed file actually went needs a column on the
// row -- and without that record the sweep resolves the file to its original name, decides the row
// describes somewhere else, and offers it again on every pass for ever. That loop was found and
// fixed twice. Three choices that work beat four where one needs a subsystem to be correct.

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
