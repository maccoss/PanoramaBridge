using System.Buffers.Binary;
using System.Text;

namespace PanoramaBridge.ThermoRaw.Tests;

/// <summary>
/// Builds a Thermo RAW file byte by byte, so the checks can be exercised without one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot prove.</b> A synthetic file shows that the reader walks the layout
/// it believes in, consistently, and that it reacts correctly when pointers do not fit. It cannot
/// show that the layout matches what a Thermo instrument actually writes -- only a real
/// acquisition can, and none is committed here. Treat a green suite as evidence about the code,
/// not about the format.
/// </para>
/// <para>
/// The layout follows thermo-raw-file-validator (Apache-2.0), the same source the reader is
/// ported from, which is the reason the two agree: they are not independent.
/// </para>
/// </remarks>
internal sealed class SyntheticRawFile
{
    private readonly List<byte> _bytes = [];

    /// <summary>The RAW revision to write. 66 exercises the 64-bit address path.</summary>
    public int FormatVersion { get; init; } = 66;

    /// <summary>Whether to write an acquisition-end timestamp.</summary>
    public bool Finalised { get; init; } = true;

    /// <summary>Scans the run header claims, which sets how long the scan index must be.</summary>
    public uint ScanCount { get; init; } = 10;

    /// <summary>Overrides the scan-index address, for pointing it somewhere impossible.</summary>
    public long? ScanIndexAddressOverride { get; init; }

    /// <summary>Bytes of padding after the structures, standing in for scan data.</summary>
    public int TrailingBytes { get; init; } = 4096;

    public static byte[] Valid() => new SyntheticRawFile().Build();

    /// <summary>Writes the file and returns it.</summary>
    public byte[] Build()
    {
        WriteFixedHeader();
        var controllerAddressSlot = WritePreamble();

        // The run header goes after the preamble, and its address is patched back in.
        var runHeaderAddress = _bytes.Count;
        PatchU64(controllerAddressSlot, (ulong)runHeaderAddress);

        WriteRunHeader(runHeaderAddress);

        // Everything the pointers refer to has to be inside the file, so pad past the highest.
        Pad(TrailingBytes);
        return [.. _bytes];
    }

    // -- the fixed header ----------------------------------------------------------------------

    private void WriteFixedHeader()
    {
        var header = new byte[ThermoRawHeader.Size];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0xA101);
        Encoding.Unicode.GetBytes("Finnigan").CopyTo(header, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), (uint)FormatVersion);

        // A plausible FILETIME; only zero versus non-zero is read.
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x28), 133_000_000_000_000_000UL);

        if (Finalised)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x98), 133_000_000_100_000_000UL);
        }

        _bytes.AddRange(header);
    }

    // -- the variable preamble, ending at the controller address table ---------------------------

    /// <summary>Writes the preamble and returns the offset of the first controller address.</summary>
    private int WritePreamble()
    {
        Pad(64);

        for (var i = 0; i < 14; i++)
        {
            EmptyString();
        }

        if (FormatVersion >= 47)
        {
            EmptyString();
            EmptyString();
            Pad(4);
        }

        if (FormatVersion >= 60)
        {
            for (var i = 0; i < 15; i++)
            {
                EmptyString();
            }
        }

        Pad(24);
        EmptyString();
        Pad(20);

        if (FormatVersion >= 64)
        {
            U32(0);
            U32(0);
            U32(1);          // one controller
            U32(0);
            U32(0);
            U32(0);
            U32(0);
            Pad(760);

            var dataAddressSlot = _bytes.Count;
            U64(0);          // patched to the run header's data address below
            Pad(8);

            // The reader walks max(count, 2) entries even for a single controller.
            var first = _bytes.Count;
            U64(0);
            Pad(8);
            U64(0);
            Pad(8);

            // The MS run header is selected when its data address matches this one, or when it
            // reports trailers. The fixture reports trailers, so this can stay zero.
            PatchU64(dataAddressSlot, 0);
            return first;
        }

        U32(0);
        U32(0);          // legacy data address
        U32(1);          // one controller
        U32(0);
        U32(0);
        U32(0);
        var legacyFirst = _bytes.Count;
        U32(0);
        U32(0);
        U32(0);
        U32(0);
        return legacyFirst;
    }

    // -- the run header --------------------------------------------------------------------------

    private void WriteRunHeader(long address)
    {
        // Laid out after it, so the pointers land somewhere real.
        var afterHeader = address + RunHeaderLength();
        var scanIndex = ScanIndexAddressOverride ?? afterHeader;
        var data = afterHeader + 8;
        var instrumentLog = afterHeader + 16;
        var errorLog = afterHeader + 24;
        var scanTrailer = afterHeader + 32;
        var scanParameters = afterHeader + 40;

        Pad(8);
        U32(1);                       // first scan
        U32(ScanCount);               // last scan
        U32(0);                       // instrument log length
        U32(0);                       // error log length
        Pad(4);

        // The 32-bit addresses, superseded by the 64-bit set below on revision 64 and later.
        U32(FormatVersion >= 64 ? 0 : (uint)scanIndex);
        U32(FormatVersion >= 64 ? 0 : (uint)data);
        U32(FormatVersion >= 64 ? 0 : (uint)instrumentLog);
        U32(FormatVersion >= 64 ? 0 : (uint)errorLog);

        Pad(4 + (5 * 8) + 56 + 88 + 40 + 320);

        for (var i = 0; i < 13; i++)
        {
            if (i == 6)
            {
                Pad(16);
            }

            Pad(520);
        }

        U32(FormatVersion >= 64 ? 0 : (uint)scanTrailer);
        U32(FormatVersion >= 64 ? 0 : (uint)scanParameters);
        U32(1);                       // trailer count, which marks this as the MS controller
        U32(0);                       // parameter count
        U32(1);                       // segment count
        Pad(8);
        U32(FormatVersion >= 64 ? 0 : (uint)address);
        Pad(8);

        if (FormatVersion >= 64)
        {
            U64((ulong)scanIndex);
            U64((ulong)data);
            U64((ulong)instrumentLog);
            U64((ulong)errorLog);
            U64(0);
            U64((ulong)scanTrailer);
            U64((ulong)scanParameters);
            Pad(8);
            U64((ulong)address);
        }
    }

    /// <summary>How long the run header is, so the pointers after it can be worked out.</summary>
    private long RunHeaderLength()
    {
        long length = 8 + (4 * 4) + 4 + (4 * 4);
        length += 4 + (5 * 8) + 56 + 88 + 40 + 320;
        length += (13 * 520) + 16;
        length += (5 * 4) + 8 + 4 + 8;

        if (FormatVersion >= 64)
        {
            length += (7 * 8) + 8 + 8;
        }

        return length;
    }

    // -- primitives --------------------------------------------------------------------------------

    private void Pad(int count) => _bytes.AddRange(new byte[count]);

    private void EmptyString() => U32(0);

    private void U32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
    }

    private void U64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
    }

    private void PatchU64(int offset, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);

        for (var i = 0; i < 8; i++)
        {
            _bytes[offset + i] = buffer[i];
        }
    }
}
