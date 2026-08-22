using System.Buffers.Binary;

namespace PanoramaBridge.ThermoRaw;

/// <summary>
/// Raised when a structure is malformed or reaches past the end of the file.
/// </summary>
/// <param name="message">What was being read, and where it ran out.</param>
/// <param name="provesTruncation">
/// True when the file is positively short rather than merely not understood.
/// </param>
public sealed class RawStructureException(string message, bool provesTruncation)
    : Exception(message)
{
    /// <summary>
    /// Whether this proves the file is short.
    /// </summary>
    /// <remarks>
    /// The distinction the whole design turns on. Running off the end of the file proves
    /// truncation; a field holding an implausible value does not -- it may simply be a layout
    /// this does not understand, and reporting that as truncation would block good files.
    /// </remarks>
    public bool ProvesTruncation { get; } = provesTruncation;
}

/// <summary>
/// A bounds-checked little-endian reader over a seekable stream.
/// </summary>
/// <remarks>
/// Every seek and read is checked against the known length rather than against whatever the
/// stream reports, because the file may be growing underneath. Reading past the end is the
/// signal being looked for, so it is raised rather than tolerated.
/// </remarks>
internal sealed class RawReader(Stream stream, long size)
{
    private readonly byte[] _scratch = new byte[8];

    public long Size { get; } = size;

    public long Position => stream.Position;

    public void Seek(long offset)
    {
        if (offset < 0 || offset > Size)
        {
            throw new RawStructureException(
                $"offset {offset} is outside the file, which is {Size} bytes",
                provesTruncation: offset > Size);
        }

        stream.Position = offset;
    }

    public void Skip(long length) => Seek(Position + length);

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Read(2));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Read(8));

    /// <summary>Skips a length-prefixed UTF-16 string without decoding it.</summary>
    /// <remarks>
    /// The values are never used, only stepped over to reach the structures after them. An
    /// implausible length means the layout is not what was assumed, which is not truncation.
    /// </remarks>
    public void SkipPascalString()
    {
        var characters = U32();

        // A quarter of a million characters is far beyond any real instrument or method name,
        // and is what a wrong offset looks like.
        if (characters > 262_144)
        {
            throw new RawStructureException(
                $"implausible string length {characters}; the layout is not as expected",
                provesTruncation: false);
        }

        Skip(characters * 2L);
    }

    private ReadOnlySpan<byte> Read(int length)
    {
        var at = Position;

        if (at + length > Size)
        {
            throw new RawStructureException(
                $"a {length}-byte field at {at} reaches past the end of the file, which is {Size} bytes",
                provesTruncation: true);
        }

        var span = _scratch.AsSpan(0, length);
        stream.ReadExactly(span);
        return span;
    }
}
