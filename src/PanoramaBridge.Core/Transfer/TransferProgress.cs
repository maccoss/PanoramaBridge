using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// A snapshot of one transfer, for display.
/// </summary>
/// <remarks>
/// Carries a <see cref="Phase"/> string as well as a byte count. The Python version reported
/// only a percentage, and because that percentage counted bytes read from disk rather than sent,
/// it had to be clamped below 100% in three separate places to avoid claiming completion too
/// early. Naming the phase means the gap between the last byte written and the server's
/// acknowledgement can simply be stated instead of hidden.
/// </remarks>
/// <param name="LocalPath">The file being transferred.</param>
/// <param name="RemotePath">Where it is going.</param>
/// <param name="State">Ledger state.</param>
/// <param name="Phase">What is happening right now, in words.</param>
/// <param name="BytesTransferred">Bytes handed to the socket so far.</param>
/// <param name="TotalBytes">Size of the file.</param>
/// <param name="BytesPerSecond">Recent throughput.</param>
/// <param name="Verification">How the remote copy has been checked, if at all.</param>
/// <param name="Message">Extra detail, such as why something was skipped or failed.</param>
/// <param name="Configuration">
/// Which configuration is moving it, for the column the transfer table shows. Empty when nothing
/// tagged it, which is what a one-off scan started before configurations existed looks like.
/// Attached as the report leaves the runner rather than worked out from the paths, because the
/// runner knows and a lookup would only be guessing at what it already knew.
/// </param>
public sealed record TransferProgress(
    string LocalPath,
    string RemotePath,
    TransferState State,
    string Phase,
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond = 0,
    VerifyMethod Verification = VerifyMethod.None,
    string? Message = null,
    string Configuration = "")
{
    /// <summary>Completion as a fraction, or null when the size is unknown.</summary>
    public double? Fraction => TotalBytes > 0
        ? Math.Clamp((double)BytesTransferred / TotalBytes, 0, 1)
        : null;

    /// <summary>
    /// Estimated time remaining, or null when there is nothing to base it on.
    /// </summary>
    /// <remarks>
    /// Derived rather than passed in. As a constructor parameter it was possible to supply a
    /// throughput figure and forget the estimate, leaving the UI showing a rate with no ETA
    /// beside it for no reason the user could discern.
    /// </remarks>
    public TimeSpan? Eta => BytesPerSecond > 0 && TotalBytes > BytesTransferred
        ? TimeSpan.FromSeconds((TotalBytes - BytesTransferred) / BytesPerSecond)
        : null;

    /// <summary>The file name alone, for a narrow column.</summary>
    public string FileName => Path.GetFileName(LocalPath);

    /// <summary>
    /// A one-line description combining phase, progress and rate. What the transfer table shows.
    /// </summary>
    public string Describe()
    {
        if (State is TransferState.Uploading && TotalBytes > 0)
        {
            var rate = BytesPerSecond > 0 ? $" - {ByteSize.Describe(BytesPerSecond)}/s" : string.Empty;
            var eta = Eta is { } remaining ? $" - {Duration.Describe(remaining)} left" : string.Empty;
            return $"{Phase} {Fraction:P0} of {ByteSize.Describe(TotalBytes)}{rate}{eta}";
        }

        return Message is { Length: > 0 } ? $"{Phase} - {Message}" : Phase;
    }

    /// <summary>Verification standing, phrased so it never overstates what was proven.</summary>
    public string DescribeVerification() => Verification switch
    {
        VerifyMethod.ServerMd5 => "Verified (server MD5)",
        VerifyMethod.SizeOnly => "Uploaded - size only",
        _ => "Uploaded - not verified",
    };
}

/// <summary>Totals across a run.</summary>
/// <param name="Uploaded">Files actually sent and verified.</param>
/// <param name="Skipped">Files already present and identical.</param>
/// <param name="Conflicts">Files needing a decision.</param>
/// <param name="Failed">Files that gave up.</param>
/// <param name="BytesUploaded">Bytes sent.</param>
/// <param name="Elapsed">Wall-clock time.</param>
public readonly record struct TransferSummary(
    int Uploaded,
    int Skipped,
    int Conflicts,
    int Failed,
    long BytesUploaded,
    TimeSpan Elapsed)
{
    /// <summary>Files considered, whatever the outcome.</summary>
    public int Total => Uploaded + Skipped + Conflicts + Failed;

    /// <summary>Average upload throughput across the run.</summary>
    public double BytesPerSecond =>
        Elapsed.TotalSeconds > 0 ? BytesUploaded / Elapsed.TotalSeconds : 0;
}
