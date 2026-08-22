namespace PanoramaBridge.ThermoRaw;

/// <summary>
/// Walks a Thermo RAW file far enough to find its run header and check the pointers in it.
/// </summary>
/// <remarks>
/// <para>
/// The layout below is a port of the one in
/// <see href="https://github.com/mriffle/thermo-raw-file-validator">thermo-raw-file-validator</see>
/// by Michael Riffle (Apache-2.0), which established it empirically against a corpus of real
/// files. The offsets and skip lengths are not derivable from anything published; they are that
/// work's findings, and they are reproduced rather than rediscovered.
/// </para>
/// <para>
/// Only the truncation half is ported. Proving a file <em>whole</em> additionally needs terminal
/// record analysis, which is the larger and more version-sensitive part; proving it <em>short</em>
/// needs only the pointers, and short is the question a transfer gate has to answer.
/// </para>
/// </remarks>
internal static class RawStructure
{
    /// <summary>What the walk found.</summary>
    /// <param name="Run">The run header, when one was located.</param>
    /// <param name="Problems">Pointers that do not fit inside the file.</param>
    /// <param name="RequiredBytes">The highest byte any structure refers to.</param>
    internal sealed record Result(
        RunHeader? Run,
        IReadOnlyList<string> Problems,
        long? RequiredBytes);

    /// <summary>
    /// Finds the run header for the MS controller and checks that its pointers fit.
    /// </summary>
    /// <exception cref="RawStructureException">
    /// The layout is not as expected, or a read reached past the end of the file.
    /// </exception>
    public static Result Inspect(Stream stream, long size, int version)
    {
        var reader = new RawReader(stream, size);
        var (dataAddress, controllers) = LocateRunHeaders(reader, version);

        var run = SelectMsRunHeader(reader, version, dataAddress, controllers);
        var problems = new List<string>();
        long required = 0;

        void Require(string what, long pointer, bool mayBeZero = false)
        {
            if (pointer == 0 && mayBeZero)
            {
                return;
            }

            if (pointer <= 0 || pointer >= size)
            {
                problems.Add($"the {what} pointer is {pointer}, outside a file of {size} bytes");
                return;
            }

            required = Math.Max(required, pointer);
        }

        Require("run header", run.Address);
        Require("scan index", run.ScanIndexAddress);
        Require("scan data", run.DataAddress);
        Require("instrument log", run.InstrumentLogAddress);
        Require("error log", run.ErrorLogAddress);
        Require("scan trailer", run.ScanTrailerAddress, mayBeZero: true);
        Require("scan parameters", run.ScanParametersAddress, mayBeZero: true);

        if (run.ScanCount <= 0)
        {
            problems.Add("the scan range does not describe any scans");
        }
        else
        {
            var indexEnd = run.ScanIndexEnd(version);
            required = Math.Max(required, indexEnd);

            if (indexEnd > size)
            {
                problems.Add(
                    $"the scan index needs {indexEnd:N0} bytes for {run.ScanCount:N0} scans, "
                    + $"and the file is {size:N0}");
            }
        }

        if (run.SelfAddress != 0 && run.SelfAddress != run.Address)
        {
            // Not a truncation signal. It means the run header was not where it was looked for,
            // so everything read out of it is suspect.
            throw new RawStructureException(
                $"the run header at {run.Address} records its own address as {run.SelfAddress}",
                provesTruncation: false);
        }

        return new Result(run, problems, required == 0 ? null : required);
    }

    /// <summary>
    /// Steps past the variable-length preamble to the controller address table.
    /// </summary>
    /// <remarks>
    /// The preamble is a run of length-prefixed strings whose count changes between revisions,
    /// which is why this cannot simply seek to a constant.
    /// </remarks>
    private static (long DataAddress, IReadOnlyList<long> Controllers) LocateRunHeaders(
        RawReader reader,
        int version)
    {
        reader.Seek(ThermoRawHeader.Size);
        reader.Skip(64);

        for (var i = 0; i < 14; i++)
        {
            reader.SkipPascalString();
        }

        if (version >= 47)
        {
            reader.SkipPascalString();
            reader.SkipPascalString();
            reader.Skip(4);
        }

        if (version >= 60)
        {
            for (var i = 0; i < 15; i++)
            {
                reader.SkipPascalString();
            }
        }

        reader.Skip(24);
        reader.SkipPascalString();
        reader.Skip(20);

        if (version >= 64)
        {
            reader.U32();
            reader.U32();
            var count = reader.U32();
            reader.U32();
            reader.U32();
            reader.U32();
            reader.U32();

            if (count is < 1 or > 64)
            {
                throw new RawStructureException(
                    $"implausible controller count {count}", provesTruncation: false);
            }

            reader.Skip(760);
            var dataAddress = (long)reader.U64();
            reader.Skip(8);

            var addresses = new List<long>();
            for (var i = 0; i < Math.Max(count, 2); i++)
            {
                addresses.Add((long)reader.U64());
                reader.Skip(8);
            }

            return (dataAddress, addresses.Take((int)count).ToList());
        }

        reader.U32();
        var legacyData = reader.U32();
        var legacyCount = reader.U32();
        reader.U32();
        reader.U32();
        reader.U32();
        var first = reader.U32();
        reader.U32();
        reader.U32();
        var second = reader.U32();

        if (legacyCount is < 1 or > 2)
        {
            throw new RawStructureException(
                $"implausible controller count {legacyCount}", provesTruncation: false);
        }

        return (legacyData, legacyCount == 1 ? [first] : [first, second]);
    }

    /// <summary>Reads each controller's run header and picks the mass-spectrometry one.</summary>
    private static RunHeader SelectMsRunHeader(
        RawReader reader,
        int version,
        long dataAddress,
        IReadOnlyList<long> controllers)
    {
        RunHeader? firstParsed = null;

        foreach (var address in controllers)
        {
            if (address == 0)
            {
                continue;
            }

            var run = ParseRunHeader(reader, address, version);
            firstParsed ??= run;

            if (version >= 64 && (run.TrailerCount > 0 || run.DataAddress == dataAddress))
            {
                return run;
            }

            if (version < 64 && run.ScanCount > 0 && run.SegmentCount > 0)
            {
                return run;
            }
        }

        return firstParsed
            ?? throw new RawStructureException(
                "no controller has a run header address", provesTruncation: false);
    }

    /// <summary>Reads the fields of one run header.</summary>
    /// <remarks>
    /// Revisions 64 and later repeat the addresses as 64-bit values after the 32-bit ones, and
    /// the later set is the one that counts: the 32-bit fields cannot address a file above 4 GB,
    /// which most acquisitions now exceed.
    /// </remarks>
    private static RunHeader ParseRunHeader(RawReader reader, long address, int version)
    {
        reader.Seek(address);
        reader.Skip(8);

        var firstScan = reader.U32();
        var lastScan = reader.U32();
        reader.U32();               // instrument log length
        reader.U32();               // error log length
        reader.Skip(4);

        long scanIndex = reader.U32();
        long data = reader.U32();
        long instrumentLog = reader.U32();
        long errorLog = reader.U32();

        reader.Skip(4 + (5 * 8) + 56 + 88 + 40 + 320);

        for (var i = 0; i < 13; i++)
        {
            if (i == 6)
            {
                reader.Skip(16);
            }

            reader.Skip(520);
        }

        long scanTrailer = reader.U32();
        long scanParameters = reader.U32();
        var trailerCount = reader.U32();
        reader.U32();               // parameter count
        var segmentCount = reader.U32();
        reader.Skip(8);
        long selfAddress = reader.U32();
        reader.Skip(8);

        if (version >= 64)
        {
            scanIndex = (long)reader.U64();
            data = (long)reader.U64();
            instrumentLog = (long)reader.U64();
            errorLog = (long)reader.U64();
            reader.U64();
            scanTrailer = (long)reader.U64();
            scanParameters = (long)reader.U64();
            reader.Skip(8);
            selfAddress = (long)reader.U64();
        }

        return new RunHeader(
            address,
            firstScan,
            lastScan,
            trailerCount,
            segmentCount,
            scanIndex,
            data,
            instrumentLog,
            errorLog,
            scanTrailer,
            scanParameters,
            selfAddress);
    }
}
