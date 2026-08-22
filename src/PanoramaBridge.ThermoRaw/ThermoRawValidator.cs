namespace PanoramaBridge.ThermoRaw;

/// <summary>
/// Answers one question about a Thermo RAW file: is it short?
/// </summary>
/// <remarks>
/// <para>
/// It exists because the two signals normally used to decide a file is finished -- nothing holds
/// a handle to it, and its size has stopped changing -- are both statements about the
/// <em>absence</em> of change, and neither can tell a finished file from an abandoned one. A copy
/// that died part-way over a network share is unlocked, stable, and short, and looks perfectly
/// ready. This reads what the file says about itself instead.
/// </para>
/// <para>
/// Reads are bounded: the fixed header, a walk of the preamble, and one run header. Nothing scans
/// the body, so the cost does not grow with a 40 GB acquisition.
/// </para>
/// <para>
/// The file layout is a port of
/// <see href="https://github.com/mriffle/thermo-raw-file-validator">thermo-raw-file-validator</see>
/// by Michael Riffle (Apache-2.0). The offsets there were established against real files and are
/// reproduced here rather than rediscovered.
/// </para>
/// </remarks>
public static class ThermoRawValidator
{
    /// <summary>
    /// Whether a name is one this can examine.
    /// </summary>
    /// <remarks>
    /// Extension only, and deliberately cheap: it touches no disk, because the caller uses it to
    /// decide whether opening the file is worth it at all. It therefore says yes to a
    /// <em>directory</em> named <c>something.raw</c>, which is how Waters writes an acquisition.
    /// Sorting that out belongs to <see cref="Validate(string)"/>, which has to look anyway.
    /// </remarks>
    public static bool IsCandidate(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).Equals(".raw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks one file by path.</summary>
    /// <remarks>
    /// Opened for reading with the widest possible sharing, so examining a file never interferes
    /// with an instrument writing it, and never becomes the reason a transfer fails.
    /// </remarks>
    public static ThermoRawResult Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var info = new FileInfo(path);

            if (Directory.Exists(path))
            {
                // Waters writes .raw as a directory. Saying "the file is not there" about one
                // sends somebody looking for a missing file that was never missing.
                return Failed(
                    path, 0, ThermoRawVerdict.NotThermoRaw,
                    "this is a directory, not a file; Waters writes .raw as a folder");
            }

            if (!info.Exists)
            {
                return Failed(path, 0, ThermoRawVerdict.Error, "the file is not there");
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            // The handle's length, not FileInfo's. Windows leaves the directory entry stale
            // while a write handle is open, and a stale length is exactly what makes a growing
            // file look truncated.
            return Validate(stream, stream.Length, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(path, 0, ThermoRawVerdict.Error, ex.Message);
        }
    }

    /// <summary>Checks a file already open.</summary>
    /// <param name="stream">Seekable, positioned anywhere.</param>
    /// <param name="size">The length to treat as authoritative.</param>
    /// <param name="path">Reported back in the result; not opened.</param>
    /// <remarks>
    /// <paramref name="size"/> is taken from the caller rather than from the stream because the
    /// caller usually has a length read from an open handle, which is the only length worth
    /// trusting while a file may still be growing.
    /// </remarks>
    public static ThermoRawResult Validate(Stream stream, long size, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var evidence = new List<string>();

        try
        {
            if (size < ThermoRawHeader.Size)
            {
                // Too short to hold a header. Only a RAW file if something says it is, and
                // nothing can, so this is not a truncation claim.
                return new ThermoRawResult(
                    path, ThermoRawVerdict.NotThermoRaw, ThermoRawUnknownReason.None,
                    null, size, null, null,
                    [$"the file is {size} bytes, shorter than a {ThermoRawHeader.Size}-byte header"]);
            }

            var buffer = new byte[ThermoRawHeader.Size];
            stream.Position = 0;
            stream.ReadExactly(buffer);

            var header = ThermoRawHeader.TryRead(buffer);

            if (header is null)
            {
                return new ThermoRawResult(
                    path, ThermoRawVerdict.NotThermoRaw, ThermoRawUnknownReason.None,
                    null, size, null, null,
                    ["the header does not carry the Thermo magic number and Finnigan signature"]);
            }

            evidence.Add($"header is a valid Thermo RAW revision {header.FormatVersion}");
            evidence.Add(header.AcquisitionFinished
                ? "the acquisition-end timestamp is populated"
                : "the acquisition-end timestamp is absent");

            if (!ThermoRawHeader.RecognisedVersions.Contains(header.FormatVersion))
            {
                return Unchecked(
                    path, header, size, evidence,
                    ThermoRawUnknownReason.UnrecognisedFormatVersion,
                    "this revision is not one whose layout is known here");
            }

            if (!ThermoRawHeader.ConfirmedVersions.Contains(header.FormatVersion))
            {
                return Unchecked(
                    path, header, size, evidence,
                    ThermoRawUnknownReason.UnconfirmedFormatVersion,
                    "this revision is known but its layout has not been confirmed against a real file");
            }

            RawStructure.Result structure;

            try
            {
                structure = RawStructure.Inspect(stream, size, header.FormatVersion);
            }
            catch (RawStructureException ex) when (ex.ProvesTruncation)
            {
                evidence.Add(ex.Message);
                return new ThermoRawResult(
                    path, ThermoRawVerdict.Truncated, ThermoRawUnknownReason.None,
                    header.FormatVersion, size, null, header.AcquisitionFinished, evidence);
            }
            catch (RawStructureException ex)
            {
                evidence.Add(ex.Message);
                return Unchecked(
                    path, header, size, evidence,
                    ThermoRawUnknownReason.LayoutNotUnderstood,
                    "the layout did not parse, and truncation is not the proven explanation");
            }

            evidence.AddRange(structure.TruncationProof);
            evidence.AddRange(structure.Anomalies);

            // Order matters, and so does the separation. Only a structure that needs bytes the
            // file does not have proves it is short; a field that is merely not what was expected
            // means the layout was misread, and reporting that as truncation would hold back a
            // file that is perfectly whole.
            if (structure.TruncationProof.Count > 0)
            {
                return new ThermoRawResult(
                    path, ThermoRawVerdict.Truncated, ThermoRawUnknownReason.None,
                    header.FormatVersion, size, structure.RequiredBytes,
                    header.AcquisitionFinished, evidence);
            }

            if (structure.Anomalies.Count > 0)
            {
                return Unchecked(
                    path, header, size, evidence,
                    ThermoRawUnknownReason.LayoutNotUnderstood,
                    "the run header is not as expected, and nothing is proven missing");
            }

            evidence.Add("every pointer in the run header lands inside the file");

            if (!header.AcquisitionFinished)
            {
                return new ThermoRawResult(
                    path, ThermoRawVerdict.NotFinalised, ThermoRawUnknownReason.None,
                    header.FormatVersion, size, structure.RequiredBytes, false, evidence);
            }

            return new ThermoRawResult(
                path, ThermoRawVerdict.NoTruncationDetected, ThermoRawUnknownReason.None,
                header.FormatVersion, size, structure.RequiredBytes, true, evidence);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or EndOfStreamException or NotSupportedException)
        {
            evidence.Add(ex.Message);
            return new ThermoRawResult(
                path, ThermoRawVerdict.Error, ThermoRawUnknownReason.None,
                null, size, null, null, evidence);
        }
    }

    private static ThermoRawResult Unchecked(
        string path,
        ThermoRawHeader header,
        long size,
        List<string> evidence,
        ThermoRawUnknownReason reason,
        string note)
    {
        evidence.Add(note);
        return new ThermoRawResult(
            path, ThermoRawVerdict.Unknown, reason,
            header.FormatVersion, size, null, header.AcquisitionFinished, evidence);
    }

    private static ThermoRawResult Failed(
        string path, long size, ThermoRawVerdict verdict, string why) =>
        new(path, verdict, ThermoRawUnknownReason.None, null, size, null, null, [why]);
}
