using System.Buffers.Binary;
using System.Text;

namespace PanoramaBridge.ThermoRaw;

/// <summary>
/// The fixed header every Thermo RAW file opens with.
/// </summary>
/// <param name="FormatVersion">The RAW revision.</param>
/// <param name="AcquisitionStartFileTime">Windows FILETIME the run began, or null.</param>
/// <param name="AcquisitionEndFileTime">Windows FILETIME the run was closed out, or null.</param>
/// <remarks>
/// A clean-room reading of the first 1,356 bytes: no Thermo library, no .NET framework
/// dependency, no interop. The layout follows the one established by
/// <see href="https://github.com/mriffle/thermo-raw-file-validator">thermo-raw-file-validator</see>
/// (Apache-2.0), which derived it from real files.
/// </remarks>
public sealed record ThermoRawHeader(
    int FormatVersion,
    ulong? AcquisitionStartFileTime,
    ulong? AcquisitionEndFileTime)
{
    /// <summary>Bytes in the fixed header.</summary>
    public const int Size = 1356;

    private const ushort Magic = 0xA101;
    private const string Signature = "Finnigan";

    private const int VersionOffset = 0x24;
    private const int AcquisitionStartOffset = 0x28;
    private const int AcquisitionEndOffset = 0x98;

    /// <summary>
    /// Revisions whose layout this understands well enough to walk.
    /// </summary>
    /// <remarks>
    /// Anything outside this set yields Unknown rather than a guess. Thermo ships new revisions,
    /// and a validator that assumed an unfamiliar one behaved like a familiar one would report
    /// truncation on a perfectly good file -- which, wired to a transfer gate, stops an
    /// instrument uploading.
    /// </remarks>
    public static readonly IReadOnlySet<int> RecognisedVersions =
        new HashSet<int> { 8, 47, 57, 60, 62, 63, 64, 66 };

    /// <summary>
    /// Revisions whose structural layout has been confirmed against real files.
    /// </summary>
    /// <remarks>
    /// Version 8 is recognised but not confirmed: it is old enough that no current instrument
    /// writes it, so nothing here has been checked against one.
    /// </remarks>
    public static readonly IReadOnlySet<int> ConfirmedVersions =
        new HashSet<int> { 47, 57, 60, 62, 63, 64, 66 };

    /// <summary>
    /// Whether the run was closed out.
    /// </summary>
    /// <remarks>
    /// The acquisition software writes this when it finalises the file, so zero means the run
    /// never finished. It sits in the header at the <em>front</em> of the file, which is why it
    /// says nothing about truncation: a file copied halfway still carries a populated one.
    /// </remarks>
    public bool AcquisitionFinished => AcquisitionEndFileTime is > 0;

    /// <summary>
    /// Reads the header, or returns null when this is not a Thermo RAW file.
    /// </summary>
    /// <remarks>
    /// Never throws for content reasons. A file that is not a RAW file is an ordinary answer
    /// here, because the caller is usually asking exactly that.
    /// </remarks>
    public static ThermoRawHeader? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(data) != Magic)
        {
            return null;
        }

        // UTF-16LE, 9 characters including the terminator.
        var signature = Encoding.Unicode.GetString(data.Slice(2, 18)).Split('\0')[0];
        if (!string.Equals(signature, Signature, StringComparison.Ordinal))
        {
            return null;
        }

        var version = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[VersionOffset..]);
        var start = BinaryPrimitives.ReadUInt64LittleEndian(data[AcquisitionStartOffset..]);
        var end = BinaryPrimitives.ReadUInt64LittleEndian(data[AcquisitionEndOffset..]);

        return new ThermoRawHeader(
            version,
            start == 0 ? null : start,
            end == 0 ? null : end);
    }

    /// <summary>Converts a Windows FILETIME to UTC, or null when absent or nonsensical.</summary>
    public static DateTimeOffset? ToTimestamp(ulong? fileTime)
    {
        if (fileTime is not > 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime((long)fileTime.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Garbage in that field is not worth failing a check over.
            return null;
        }
    }
}
