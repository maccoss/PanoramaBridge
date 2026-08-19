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
/// <param name="Eta">Estimated time remaining, when it can be estimated.</param>
/// <param name="Verification">How the remote copy has been checked, if at all.</param>
/// <param name="Message">Extra detail, such as why something was skipped or failed.</param>
public sealed record TransferProgress(
    string LocalPath,
    string RemotePath,
    TransferState State,
    string Phase,
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond = 0,
    TimeSpan? Eta = null,
    VerifyMethod Verification = VerifyMethod.None,
    string? Message = null)
{
    /// <summary>Completion as a fraction, or null when the size is unknown.</summary>
    public double? Fraction => TotalBytes > 0
        ? Math.Clamp((double)BytesTransferred / TotalBytes, 0, 1)
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
            var rate = BytesPerSecond > 0 ? $" - {Format(BytesPerSecond)}/s" : string.Empty;
            var eta = Eta is { } remaining ? $" - {DescribeEta(remaining)} left" : string.Empty;
            return $"{Phase} {Fraction:P0} of {Format(TotalBytes)}{rate}{eta}";
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

    private static string DescribeEta(TimeSpan eta) => eta.TotalHours >= 1
        ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
        : eta.TotalMinutes >= 1
            ? $"{(int)eta.TotalMinutes}m"
            : $"{Math.Max(1, (int)eta.TotalSeconds)}s";

    private static string Format(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:F0} B" : $"{bytes:F1} {units[unit]}";
    }
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
