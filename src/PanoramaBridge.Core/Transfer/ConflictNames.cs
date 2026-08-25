namespace PanoramaBridge.Core.Transfer;

/// <summary>
/// Picks a free name when an acquisition has to be sent alongside something already there.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from anything that talks to a server, because the names it produces end up
/// in front of a person before they agree to them. A rename that surprises somebody is worse than
/// one that is merely ugly.
/// </para>
/// <para>
/// The suffix goes before the extension -- <c>run (2).raw</c>, not <c>run.raw (2)</c> -- so the
/// result is still a <c>.raw</c> to Skyline, to Panorama, and to whoever sorts the folder later.
/// Appending after the extension would produce a file that no tool downstream recognises, which
/// is a strange way to resolve a conflict about data nobody wants to lose.
/// </para>
/// <para>
/// Companion extensions are handled by taking only the last one. A Sciex <c>run.wiff.scan</c>
/// becomes <c>run.wiff (2).scan</c>, which keeps the <c>.scan</c> where readers look for it.
/// That is not perfect -- the companion no longer shares a stem with its <c>.wiff</c> -- but the
/// alternative is a name whose extension has moved, and a Sciex acquisition renamed piecemeal is
/// already a situation somebody needs to look at rather than one to paper over.
/// </para>
/// </remarks>
public static class ConflictNames
{
    /// <summary>
    /// The first free name of the form <c>name (n).ext</c>, starting at 2.
    /// </summary>
    /// <remarks>
    /// Starts at 2 because the file already there is, in the only sense that matters to the
    /// person reading the folder, the first one. Comparison is case-insensitive: the servers this
    /// talks to treat <c>Run.raw</c> and <c>run.raw</c> as the same name, and producing a
    /// "free" name that collides on arrival would turn a conflict into a failed upload.
    /// </remarks>
    /// <param name="desired">The name that is already taken.</param>
    /// <param name="taken">Names occupying the destination folder.</param>
    /// <exception cref="ArgumentException"><paramref name="desired"/> is blank.</exception>
    public static string NextFree(string desired, IEnumerable<string> taken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desired);
        ArgumentNullException.ThrowIfNull(taken);

        var occupied = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!occupied.Contains(desired))
        {
            return desired;
        }

        var extension = Path.GetExtension(desired);
        var stem = desired[..^extension.Length];

        // Unbounded rather than capped at some round number. A cap would have to fail somehow,
        // and "could not find a free name" is a worse answer than a slightly long one; a folder
        // holding thousands of collisions for a single name is a situation the count is not the
        // problem in.
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";

            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
