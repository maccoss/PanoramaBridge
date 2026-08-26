using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>
/// The one answer to "where does this file go on the server".
/// </summary>
/// <remarks>
/// <para>
/// Every path uses this type instead of joining remote segments itself. The source path is the
/// authority: the ledger is case-insensitive, while the server is not, so a file renamed only by
/// case must resolve from the path seen on disk rather than an earlier ledger spelling.
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
    /// Where a file on disk goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path comes from the caller rather than from the row, because the row records whatever
    /// case the ledger was written with and the ledger is <c>NOCASE</c>. Resolving from the stored
    /// path meant renaming a file's case on disk left every later upload going to the old-cased
    /// remote name, which the listing and the checksum sidecar then disagree with.
    /// </para>
    /// </remarks>
    /// <param name="localPath">The path as it is on disk.</param>
    public RemotePath For(string localPath) => Resolve(localPath, leaf: null);

    /// <summary>
    /// Where a row's local path would go.
    /// </summary>
    /// <remarks>
    /// Retained for callers that only have a row. The scanner resolves candidates from the path it
    /// just enumerated, so it never uses this older spelling to decide whether a file is settled.
    /// </remarks>
    public RemotePath For(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return For(record.LocalPath);
    }

    private RemotePath Resolve(string localPath, string? leaf) =>
        PathSafety.ResolveDestination(_localBaseDirectory, localPath, _destinationRoot, leaf);
}
