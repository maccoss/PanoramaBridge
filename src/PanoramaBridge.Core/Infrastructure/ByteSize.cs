namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Byte counts written the way somebody watching a transfer reads them.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, because there were three: the file progress line, the overall progress
/// line and <c>pbctl</c> each carried a private copy of the same loop. Three copies of a display
/// rule is three chances for the same number to be written two ways in one window.
/// </para>
/// <para>
/// Binary units, matching what Windows shows in Explorer for the same file. A scientist comparing
/// this window against a folder listing should see the same number, and 1 KB here is 1,024 bytes
/// for that reason rather than as a stance on SI prefixes.
/// </para>
/// </remarks>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Renders a byte count, for example <c>6.8 GB</c>.
    /// </summary>
    /// <remarks>
    /// Whole bytes below a kilobyte and one decimal above it. The decimal is what makes a growing
    /// file legible: without it a 2 GB acquisition reads the same before and after a 40 MB write,
    /// which is the opposite of what a progress message is for.
    /// <para>
    /// A negative count is rendered with a sign rather than rejected. Sizes are never negative,
    /// but differences between them are, and a caller describing one should not have to
    /// special-case the direction before it can call this.
    /// </para>
    /// </remarks>
    public static string Describe(double bytes)
    {
        var magnitude = Math.Abs(bytes);
        var unit = 0;

        while (magnitude >= 1024 && unit < Units.Length - 1)
        {
            magnitude /= 1024;
            unit++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;

        return unit == 0
            ? $"{sign}{magnitude:F0} B"
            : $"{sign}{magnitude:F1} {Units[unit]}";
    }

    /// <summary>Renders a byte count.</summary>
    public static string Describe(long bytes) => Describe((double)bytes);
}
