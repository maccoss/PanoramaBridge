using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>
/// The one answer to "where does this file go on the server".
/// </summary>
/// <remarks>
/// <para>
/// Six call sites used to work this out for themselves, each passing
/// <see cref="PathSafety.ResolveDestination"/> a leaf name it had derived from whatever it
/// happened to know. Every one of them had to remember the same three rules -- a plain file keeps
/// its own name, a renamed one keeps the name it was sent under, a directory acquisition arrives
/// as one archive -- and the bugs came from the ones that remembered a rule differently, or not at
/// all:
/// </para>
/// <list type="bullet">
/// <item>
/// The sweep resolved a renamed file to its original name while the ledger held the renamed one,
/// decided the row described somewhere else, and offered the file again. The engine then renamed
/// it again, on every pass, for ever.
/// </item>
/// <item>
/// The same mismatch for every directory acquisition, which reaches the server as
/// <c>.d.zip</c> while the sweep resolved it to <c>.d</c>: a full re-measure of every finished
/// acquisition, every pass, since directory acquisitions shipped.
/// </item>
/// <item>
/// Resolving a conflict passed a null leaf for anything but a fresh rename, so replacing a file
/// that lived at <c>run (2).raw</c> overwrote <c>run.raw</c> -- destroying the copy somebody had
/// chosen to preserve.
/// </item>
/// <item>
/// A rename that could not be carried out cleared the leaf, putting the row back to its original
/// name and setting up the same destruction on the next decision.
/// </item>
/// </list>
/// <para>
/// Those are one bug, found four times. The rules live here now, so a caller cannot hold a
/// different opinion about where a file belongs -- it has nowhere to express one.
/// </para>
/// <para>
/// In <c>Monitoring</c> rather than <c>Transfer</c> because <see cref="DatasetFolder"/> owns the
/// archive-naming rule and lives here, and because Transfer already depends on Monitoring while
/// the reverse would close a cycle.
/// </para>
/// </remarks>
public sealed class DestinationMap
{
    private readonly string _localBaseDirectory;
    private readonly RemotePath _destinationRoot;

    /// <param name="localBaseDirectory">The monitored folder, which destinations are relative to.</param>
    /// <param name="destinationRoot">Where that folder maps to on the server.</param>
    public DestinationMap(string localBaseDirectory, RemotePath destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBaseDirectory);

        _localBaseDirectory = localBaseDirectory;
        _destinationRoot = destinationRoot ?? throw new ArgumentNullException(nameof(destinationRoot));
    }

    /// <summary>
    /// The leaf name a row's copy occupies, or null when it occupies its own name.
    /// </summary>
    /// <remarks>
    /// A rename wins over the acquisition rule: a <c>.d</c> sent alongside as
    /// <c>250314 (2).d.zip</c> lives there, not at the name its folder would otherwise produce.
    /// </remarks>
    public static string? LeafFor(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.RenameTo ?? LeafFor(record.LocalPath, record.IsDataset);
    }

    /// <summary>The leaf name a file with no row yet would occupy.</summary>
    public static string? LeafFor(string localPath, bool isDataset) =>
        isDataset ? DatasetFolder.ArchiveNameFor(localPath) : null;

    /// <summary>Where an existing row's copy is, or would go.</summary>
    public RemotePath For(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Resolve(record.LocalPath, LeafFor(record));
    }

    /// <summary>Where a file with no row yet would go.</summary>
    public RemotePath For(string localPath, bool isDataset = false) =>
        Resolve(localPath, LeafFor(localPath, isDataset));

    /// <summary>
    /// Where a file would go under a name chosen for it.
    /// </summary>
    /// <remarks>
    /// For the moment a rename is decided, before the row records it. Everything afterwards asks
    /// <see cref="For(UploadRecord)"/>, because by then the row knows.
    /// </remarks>
    public RemotePath Under(string localPath, string leaf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaf);

        return Resolve(localPath, leaf);
    }

    private RemotePath Resolve(string localPath, string? leaf) =>
        PathSafety.ResolveDestination(_localBaseDirectory, localPath, _destinationRoot, leaf);
}
