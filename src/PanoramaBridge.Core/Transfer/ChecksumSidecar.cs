using System.Globalization;
using System.Text;
using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// The small checksum file written beside each upload.
/// </summary>
/// <remarks>
/// <para>
/// The hashes are in the local ledger already, but that ledger lives on one instrument computer
/// and does not travel with the data. A sidecar does: anyone who has the files years later can
/// check them without this application, without its database, and without asking Panorama to
/// recompute anything.
/// </para>
/// <para>
/// The first line is exactly what <c>md5sum</c> writes, so <c>md5sum -c run.raw.md5</c> works
/// unmodified. Everything else is a comment line, which that format ignores. The acquisition
/// time is in there because it is the one thing the server cannot keep: Panorama stamps an
/// uploaded file with the time it arrived, and refuses <c>PROPPATCH</c> outright, so the date
/// the instrument wrote the file survives only if it is written into content.
/// </para>
/// </remarks>
public static class ChecksumSidecar
{
    /// <summary>Appended to the file's own name, as <c>md5sum</c> conventions expect.</summary>
    public const string Extension = ".md5";

    /// <summary>Where the sidecar for an uploaded file goes.</summary>
    public static RemotePath PathFor(RemotePath uploaded)
    {
        ArgumentNullException.ThrowIfNull(uploaded);
        return uploaded.Parent.Append(uploaded.Name + Extension);
    }

    /// <summary>True when a name is one of these, so a listing can tell them apart from data.</summary>
    public static bool IsSidecar(string name) =>
        name is not null && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the sidecar for a file that has just been uploaded and verified.
    /// </summary>
    /// <param name="fileName">Name of the file as stored on the server.</param>
    /// <param name="hashes">What the upload's own pass over the file computed.</param>
    /// <param name="length">Bytes stored.</param>
    /// <param name="acquiredUtc">
    /// When the instrument last wrote the file. The date worth keeping, and the one the server
    /// discards.
    /// </param>
    /// <param name="uploadedUtc">When it reached the server.</param>
    /// <param name="producer">What wrote this, so an odd-looking file can be traced back.</param>
    public static string Render(
        string fileName,
        ContentHashes hashes,
        long length,
        DateTimeOffset acquiredUtc,
        DateTimeOffset uploadedUtc,
        string producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var text = new StringBuilder();

        // Line one, and the only line that has to be machine-readable: two spaces between the
        // hash and the name is what md5sum writes and what md5sum -c expects.
        text.Append(hashes.Md5).Append("  ").Append(fileName).Append('\n');

        text.Append("# file      ").Append(fileName).Append('\n');
        text.Append("# bytes     ").Append(length.ToString("D", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("# md5       ").Append(hashes.Md5).Append('\n');

        if (!string.IsNullOrEmpty(hashes.Sha256))
        {
            text.Append("# sha256    ").Append(hashes.Sha256).Append('\n');
        }

        text.Append("# acquired  ").Append(Stamp(acquiredUtc)).Append('\n');
        text.Append("# uploaded  ").Append(Stamp(uploadedUtc)).Append('\n');
        text.Append("# by        ").Append(producer).Append('\n');

        return text.ToString();
    }

    /// <summary>ISO 8601 in UTC, which is unambiguous wherever it is read.</summary>
    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The instant an instrument last wrote a file, from its ledger stamp.</summary>
    public static DateTimeOffset AcquiredFrom(LocalFileStamp stamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds(stamp.LastWriteUnixMs);
}
