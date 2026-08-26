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
        // Deliberately tolerant. An unset monitored folder is a settings problem, and
        // AppSettings.Validate already says so in a sentence written for a scientist. Throwing
        // here would turn it into a parameter-name exception out of a constructor, before any
        // progress reporting exists to carry it.
        _localBaseDirectory = localBaseDirectory ?? string.Empty;
        _destinationRoot = destinationRoot ?? throw new ArgumentNullException(nameof(destinationRoot));
    }

    /// <summary>
    /// Where a file goes, given what is on disk now and what the ledger remembers about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path comes from the caller rather than from the row, because the row records whatever
    /// case the ledger was written with and the ledger is <c>NOCASE</c>. Resolving from the stored
    /// path meant renaming a file's case on disk left every later upload going to the old-cased
    /// remote name, which the listing and the checksum sidecar then disagree with.
    /// </para>
    /// <para>
    /// Whether this is a directory acquisition comes from the caller too, for the same reason: it
    /// is a fact about what is on disk at this moment. <c>IsDataset</c> is never cleared once set,
    /// so a <c>.d</c> folder later replaced by a plain <c>.d</c> file would otherwise be sent to
    /// the archive name and land on top of the acquisition.
    /// </para>
    /// <para>
    /// Only the rename comes from the row, because only the row knows it.
    /// </para>
    /// </remarks>
    /// <param name="localPath">The path as it is on disk.</param>
    /// <param name="isDataset">Whether that path is a directory acquisition, right now.</param>
    /// <param name="record">The row, when there is one.</param>
    public RemotePath For(string localPath, bool isDataset) =>
        Resolve(localPath, isDataset ? DatasetFolder.ArchiveNameFor(localPath) : null);

    /// <summary>
    /// Where a row's copy is, according to the row alone.
    /// </summary>
    /// <remarks>
    /// For the sweep, which reasons about rows rather than about what it is holding. The engine
    /// uses the overload that takes the path, because it has the file in front of it.
    /// </remarks>
    public RemotePath For(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return For(record.LocalPath, record.IsDataset);
    }

    /// <summary>
    /// The leaf a file occupies: its rename if it has one, its archive name if it is an
    /// acquisition folder, otherwise its own name.
    /// </summary>
    /// <remarks>
    /// Blank counts as absent, not as a name. <see cref="PathSafety.ResolveDestination"/> treats
    /// whitespace as "no leaf given", so testing only for null here meant an empty rename
    /// short-circuited the acquisition rule and then got ignored downstream -- a <c>.d</c>
    /// resolving to its folder name. Two notions of "no leaf" inside the one type built so that
    /// callers cannot hold two notions of anything.
    /// </remarks>
    private static string? Leaf(string? renameTo, string localPath, bool isDataset) =>
        string.IsNullOrWhiteSpace(renameTo)
            ? isDataset ? DatasetFolder.ArchiveNameFor(localPath) : null
            : renameTo;

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
