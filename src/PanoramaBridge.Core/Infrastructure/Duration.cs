namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// How long is left, written the way somebody watching a transfer reads it.
/// </summary>
/// <remarks>
/// <para>
/// The same story as <see cref="ByteSize"/>, one file over. There were three copies: the file
/// progress line, the overall progress line and a transfer row. The first two were identical and
/// the third had already drifted — it wrote the sub-hour case as <c>3m 20s</c> where the others
/// wrote <c>3m</c>, so a per-file row and the totals line above it disagreed about the time
/// remaining on the same transfer, in the same window, at the same moment.
/// </para>
/// <para>
/// The drifted form is the one kept. It loses nothing, it matches the hour case — which always
/// showed two units — and taking it means the rows, which are numerous, read as they already did;
/// only the single totals line changes.
/// </para>
/// </remarks>
public static class Duration
{
    /// <summary>
    /// Renders a remaining time, for example <c>2h 15m</c>, <c>3m 20s</c> or <c>45s</c>.
    /// </summary>
    /// <remarks>
    /// Never "0s". A remaining time that has rounded to nothing is still a transfer that has not
    /// finished, and saying zero invites the reader to wonder why it is still going.
    /// </remarks>
    public static string Describe(TimeSpan remaining) => remaining.TotalHours >= 1
        ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
        : remaining.TotalMinutes >= 1
            ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
            : $"{Math.Max(1, (int)remaining.TotalSeconds)}s";
}
