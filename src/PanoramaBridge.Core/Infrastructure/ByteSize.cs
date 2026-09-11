using System.Globalization;

namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Byte counts written the way somebody watching a transfer reads them.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, because there were five. Four rendered a size — the file progress line,
/// the overall progress line, the uploads table and <c>pbctl</c> — and a fifth rendered a rate on
/// a transfer row. Copies of a display rule are chances for the same number to be written two
/// ways in one window, and they had already begun to drift: the rate's copy stopped at GB where
/// the others went to TB, and wrote a sub-kilobyte value as <c>512.0 B</c> where the others wrote
/// <c>512 B</c>.
/// </para>
/// <para>
/// The count in this paragraph was wrong when it was first written — it said three, because a
/// search for the duplicates found only the ones that were easy to find. If another appears,
/// route it here rather than adding a sixth.
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
        // Every caller that computes a rate already turns "no measurable elapsed time" into zero,
        // so zero is this application's established value for "no rate". Mapping a non-finite one
        // to the same thing keeps a division somebody forgets to guard from putting "NaN B/s" on
        // screen, where nothing would fail and nobody would report it.
        if (!double.IsFinite(bytes))
        {
            bytes = 0;
        }

        var magnitude = Math.Abs(bytes);
        var unit = 0;

        // The comparison is against what will be PRINTED, not what is held. 1,048,575 bytes is
        // 1023.999 KB, which prints as "1024.0 KB" — a figure no unit scale should ever show, and
        // what this loop produced while it compared the unrounded value. Stepping on the rounded
        // one is what keeps the printed number below 1024.
        while (unit < Units.Length - 1 && Rounded(magnitude, unit) >= 1024)
        {
            magnitude /= 1024;
            unit++;
        }

        var rounded = Rounded(magnitude, unit);

        // Only sign a number that is not zero. A small negative difference would otherwise read
        // "-0 B", which says less than "0 B" does.
        var sign = bytes < 0 && rounded != 0 ? "-" : string.Empty;

        return unit == 0
            ? $"{sign}{magnitude.ToString("F0", Culture)} B"
            : $"{sign}{magnitude.ToString("F1", Culture)} {Units[unit]}";
    }

    /// <summary>Renders a byte count.</summary>
    public static string Describe(long bytes) => Describe((double)bytes);

    /// <summary>
    /// Invariant, deliberately, rather than the machine's locale.
    /// </summary>
    /// <remarks>
    /// Every string around these numbers is English and none of it is localized, so a German
    /// machine would otherwise render "Still being written (1,5 KB" — one comma decimal inside an
    /// English sentence. It also keeps the figure stable between the window, the console and the
    /// log, which matters when somebody pastes a line of it into a support request.
    /// <para>
    /// Dates are the opposite case and are formatted with the current culture, because date order
    /// genuinely differs by country and is read rather than compared.
    /// </para>
    /// </remarks>
    private static CultureInfo Culture => CultureInfo.InvariantCulture;

    /// <summary>The value as it will be printed at this unit.</summary>
    private static double Rounded(double magnitude, int unit) =>
        Math.Round(magnitude, unit == 0 ? 0 : 1, MidpointRounding.AwayFromZero);
}
