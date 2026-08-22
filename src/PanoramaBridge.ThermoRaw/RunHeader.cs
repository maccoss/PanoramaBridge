namespace PanoramaBridge.ThermoRaw;

/// <summary>
/// The part of a Thermo run header needed to tell whether the file is short.
/// </summary>
/// <remarks>
/// Only the addresses and counts. The run header carries a great deal more that is irrelevant to
/// the one question here: does this file contain the bytes its own index says it contains.
/// </remarks>
internal sealed record RunHeader(
    long Address,
    uint FirstScan,
    uint LastScan,
    uint TrailerCount,
    uint SegmentCount,
    long ScanIndexAddress,
    long DataAddress,
    long InstrumentLogAddress,
    long ErrorLogAddress,
    long ScanTrailerAddress,
    long ScanParametersAddress,
    long SelfAddress)
{
    /// <summary>Scans described, or zero for a range that does not make sense.</summary>
    public long ScanCount => LastScan >= FirstScan ? LastScan - FirstScan + 1L : 0;

    /// <summary>
    /// Bytes one scan-index entry occupies, which grew with the format.
    /// </summary>
    public static int ScanIndexEntrySize(int version) =>
        version >= 66 ? 88 : version >= 64 ? 80 : 72;

    /// <summary>
    /// The last byte the scan index refers to.
    /// </summary>
    /// <remarks>
    /// The most useful single number here. An index describing more scans than the file has room
    /// for is the clearest possible statement that bytes are missing.
    /// </remarks>
    public long ScanIndexEnd(int version) =>
        ScanIndexAddress + (ScanCount * ScanIndexEntrySize(version));
}
