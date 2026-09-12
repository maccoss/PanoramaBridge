namespace PanoramaBridge.Core.Storage;

/// <summary>
/// What identifies one row of the upload ledger: a local file and where it was sent.
/// </summary>
/// <param name="LocalPath">Full local path. Compared without regard to case, as Windows does.</param>
/// <param name="RemotePath">Encoded destination path. Compared exactly, as the server does.</param>
/// <remarks>
/// <para>
/// A type rather than two parameters, because the destination is the half that is easy to forget
/// and expensive to omit. Until multiple configurations existed the ledger was keyed by local path
/// alone; a second configuration sending the same folder somewhere else would then overwrite the
/// first one's row, and each would re-upload every shared file on every sweep, to both
/// destinations, for ever. Making the pair a value the compiler demands is what stops that
/// returning by accident.
/// </para>
/// <para>
/// The two halves compare differently on purpose, matching the systems they name. Windows treats
/// <c>D:\Data\Run.raw</c> and <c>d:\data\run.raw</c> as one file, so the local half is
/// case-insensitive and the column carries <c>COLLATE NOCASE</c>. A WebDAV server does not, so
/// the remote half is exact — the same rule <see cref="UploadRecord.IsSettledAt"/> already
/// applied when it compared destinations with <see cref="StringComparison.Ordinal"/>.
/// </para>
/// </remarks>
public readonly record struct LedgerKey(string LocalPath, string RemotePath)
{
    /// <summary>Equality matching the database's collations rather than .NET's defaults.</summary>
    public bool Equals(LedgerKey other) =>
        string.Equals(LocalPath, other.LocalPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RemotePath, other.RemotePath, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        LocalPath is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(LocalPath),
        RemotePath is null ? 0 : StringComparer.Ordinal.GetHashCode(RemotePath));

    /// <inheritdoc />
    public override string ToString() => $"{LocalPath} -> {RemotePath}";
}
