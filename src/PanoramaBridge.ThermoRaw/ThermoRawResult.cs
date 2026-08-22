namespace PanoramaBridge.ThermoRaw;

/// <summary>
/// What could be established about a Thermo RAW file.
/// </summary>
/// <remarks>
/// Deliberately has no "complete" member. Proving a RAW file whole needs terminal-record
/// analysis that this does not attempt, and a verdict that sounds like proof would be read as
/// proof. <see cref="NoTruncationDetected"/> says exactly as much as was checked.
/// </remarks>
public enum ThermoRawVerdict
{
    /// <summary>Nothing was checked, or the check has not run.</summary>
    NotChecked = 0,

    /// <summary>
    /// Every internal pointer lands inside the file and the acquisition is finalised.
    /// </summary>
    /// <remarks>
    /// Not a completeness proof. Bytes can be missing from the end of a region whose pointer
    /// still lands inside the file, and nothing here rules that out.
    /// </remarks>
    NoTruncationDetected = 1,

    /// <summary>
    /// Proven short: a pointer or the scan index addresses bytes the file does not contain.
    /// </summary>
    /// <remarks>The only verdict that should ever stop a file being uploaded.</remarks>
    Truncated = 2,

    /// <summary>
    /// Structurally sound, but the acquisition-end timestamp is absent.
    /// </summary>
    /// <remarks>
    /// The run never finished: aborted, or the instrument stopped. The file may be perfectly
    /// readable as far as it goes, which is why this is not <see cref="Truncated"/>.
    /// </remarks>
    NotFinalised = 3,

    /// <summary>The file is not a Thermo RAW file at all.</summary>
    NotThermoRaw = 4,

    /// <summary>
    /// It is a Thermo RAW file, and this could not say anything useful about it.
    /// </summary>
    /// <remarks>
    /// Never a reason to hold a file back. A Thermo firmware update shipping an unrecognised
    /// revision would otherwise stop an instrument uploading, which is a far worse outcome than
    /// transferring a file whose structure was not understood. <see cref="ThermoRawResult.Reason"/>
    /// says why, so the gap can be closed rather than guessed at.
    /// </remarks>
    Unknown = 5,

    /// <summary>The file could not be read.</summary>
    Error = 6,
}

/// <summary>Why a verdict is <see cref="ThermoRawVerdict.Unknown"/>.</summary>
/// <remarks>
/// Recorded against the upload so that the files this cannot check are findable later. A
/// validator improves by being shown what it failed on; "unknown" with no reason is a dead end.
/// </remarks>
public enum ThermoRawUnknownReason
{
    /// <summary>Not applicable: the verdict is not Unknown.</summary>
    None = 0,

    /// <summary>The header parsed, but this revision of the format is not recognised.</summary>
    UnrecognisedFormatVersion = 1,

    /// <summary>
    /// A recognised revision whose layout has not been confirmed against real files.
    /// </summary>
    UnconfirmedFormatVersion = 2,

    /// <summary>The layout did not parse, and truncation is not the proven explanation.</summary>
    LayoutNotUnderstood = 3,
}

/// <summary>The outcome of checking one file.</summary>
/// <param name="Path">The file examined.</param>
/// <param name="Verdict">What was established.</param>
/// <param name="Reason">Why, when the verdict is Unknown.</param>
/// <param name="FormatVersion">The RAW revision, when the header parsed.</param>
/// <param name="FileSize">Physical size in bytes.</param>
/// <param name="RequiredBytes">
/// The highest byte the file's own structures refer to, when that could be worked out. Greater
/// than <paramref name="FileSize"/> is what proves truncation.
/// </param>
/// <param name="AcquisitionFinished">
/// Whether the acquisition-end timestamp is populated, when the header parsed.
/// </param>
/// <param name="Evidence">What was observed, in order, for a human reading a report.</param>
public sealed record ThermoRawResult(
    string Path,
    ThermoRawVerdict Verdict,
    ThermoRawUnknownReason Reason,
    int? FormatVersion,
    long FileSize,
    long? RequiredBytes,
    bool? AcquisitionFinished,
    IReadOnlyList<string> Evidence)
{
    /// <summary>True only when this file is positively known to be short.</summary>
    /// <remarks>
    /// The question the transfer gate asks. Everything that is not proven short is allowed
    /// through, including everything this did not understand.
    /// </remarks>
    public bool IsProvenTruncated => Verdict == ThermoRawVerdict.Truncated;

    /// <summary>
    /// Whether a human should be shown this, because the checker could not do its job.
    /// </summary>
    public bool NeedsAttention =>
        Verdict is ThermoRawVerdict.Truncated
            or ThermoRawVerdict.NotFinalised
            or ThermoRawVerdict.Unknown;

    /// <summary>One line, for a table cell or a log.</summary>
    public string Summary => Verdict switch
    {
        ThermoRawVerdict.NotChecked => "Not checked",
        ThermoRawVerdict.NoTruncationDetected => "No truncation detected",
        ThermoRawVerdict.Truncated => RequiredBytes is { } needed
            ? $"Truncated - needs {needed:N0} bytes, file is {FileSize:N0}"
            : "Truncated",
        ThermoRawVerdict.NotFinalised => "Acquisition never finished",
        ThermoRawVerdict.NotThermoRaw => "Not a Thermo RAW file",
        ThermoRawVerdict.Error => "Could not be read",
        _ => Reason switch
        {
            ThermoRawUnknownReason.UnrecognisedFormatVersion =>
                $"Unchecked - RAW revision {FormatVersion} is not recognised",
            ThermoRawUnknownReason.UnconfirmedFormatVersion =>
                $"Unchecked - RAW revision {FormatVersion} is not confirmed",
            ThermoRawUnknownReason.LayoutNotUnderstood => "Unchecked - layout not understood",
            _ => "Unchecked",
        },
    };
}
