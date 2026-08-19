namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// Reads the response of LabKey's <c>?method=md5sum</c>.
/// </summary>
/// <remarks>
/// <para>
/// The response is <c>text/plain</c>, one line per file, in the GNU <c>md5sum</c> layout:
/// thirty-two hex digits, a space, an asterisk, then the file name.
/// </para>
/// <code>
/// 558fb082796f8ff111b6e2f2f3c3356c *ASMS_2019_PanoramaPublic.pdf
/// 89c1acd7fca191af78e4b1824c3fc3ed *Panorama (Webinar 2014-08-19).pdf
/// </code>
/// <para>
/// This matters more than a listing does, because the hash is computed by the server over the
/// bytes it actually stored. That makes it a true end-to-end integrity check, unlike the
/// sidecar file the Python version wrote, which only ever proved that the client could write a
/// second file containing a number it had made up itself.
/// </para>
/// <para>
/// Verified against panoramaweb.org: names are emitted verbatim. Spaces, <c>@</c>, <c>#</c>,
/// parentheses and non-ASCII characters appear raw, with no percent-encoding and no
/// GNU-style backslash escaping, so everything after the separator is the name.
/// </para>
/// <para>
/// On a collection the response covers only the files directly inside it. Subdirectories are
/// omitted entirely -- not even listed -- so verifying a nested dataset needs one request per
/// directory. That is still one request per <em>directory</em> rather than per file.
/// </para>
/// </remarks>
public static class Md5SumParser
{
    private const int HashLength = 32;

    /// <summary>
    /// Parses the response body into a name-to-hash map.
    /// </summary>
    /// <remarks>
    /// Names are compared ordinally: the server is case-sensitive even though the Windows file
    /// system the files came from is not, and quietly folding case here would let two distinct
    /// remote files collide during verification.
    /// </remarks>
    /// <exception cref="FormatException">A non-blank line was not in the expected layout.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in EnumerateLines(responseBody))
        {
            if (!TryParseLine(line, out var name, out var hash))
            {
                throw new FormatException(
                    $"Unexpected line in an md5sum response: '{Truncate(line)}'.");
            }

            // A duplicate name cannot happen on a real listing, but a later entry winning is
            // the safer reading if it ever did.
            result[name] = hash;
        }

        return result;
    }

    /// <summary>
    /// Parses the response for a single file and returns its hash, or null when the response
    /// was empty.
    /// </summary>
    /// <remarks>
    /// Requesting md5sum on a file yields exactly one line. The name is not checked against the
    /// requested path: the server reports the stored name, and comparing it here would reject
    /// legitimate cases such as a name that differs only by the server's own normalization.
    /// Name-set comparison belongs at the directory level, where it is meaningful.
    /// </remarks>
    public static string? ParseSingle(string responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        foreach (var line in EnumerateLines(responseBody))
        {
            if (TryParseLine(line, out _, out var hash))
            {
                return hash;
            }

            throw new FormatException(
                $"Unexpected line in an md5sum response: '{Truncate(line)}'.");
        }

        return null;
    }

    private static bool TryParseLine(string line, out string name, out string hash)
    {
        name = string.Empty;
        hash = string.Empty;

        // Thirty-two hex digits, a separator, then at least one character of name.
        if (line.Length < HashLength + 3)
        {
            return false;
        }

        for (var i = 0; i < HashLength; i++)
        {
            if (!Uri.IsHexDigit(line[i]))
            {
                return false;
            }
        }

        // Binary mode is " *"; the text-mode variant is two spaces. Accept either, because
        // which one appears is an implementation detail of the server.
        if (line[HashLength] != ' ' || line[HashLength + 1] is not ('*' or ' '))
        {
            return false;
        }

        hash = line[..HashLength].ToLowerInvariant();

        // Everything after the separator is the name, verbatim. It may contain spaces,
        // asterisks or anything else the file system allowed.
        name = line[(HashLength + 2)..];

        return name.Length > 0;
    }

    /// <summary>
    /// Splits on either line ending and drops blank lines, so a missing or doubled trailing
    /// newline is not an error.
    /// </summary>
    private static IEnumerable<string> EnumerateLines(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 80 ? value : value[..80] + "...";
}
