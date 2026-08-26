namespace PanoramaBridge.Core.WebDav;

/// <summary>Why a name cannot be used as a remote path segment.</summary>
public enum PathRejectionReason
{
    /// <summary>The name is usable.</summary>
    None,

    /// <summary>Empty, or entirely whitespace.</summary>
    Empty,

    /// <summary>A relative-path segment that would escape the destination.</summary>
    Traversal,

    /// <summary>The file is not underneath the folder being monitored.</summary>
    OutsideMonitoredFolder,

    /// <summary>
    /// Contains a semicolon, which the server silently truncates the name at.
    /// </summary>
    SemicolonTruncatesOnServer,

    /// <summary>Contains a character that cannot appear in a path segment at all.</summary>
    IllegalCharacter,

    /// <summary>Longer than the server will accept.</summary>
    TooLong,
}

/// <summary>
/// A file that cannot be given a place on the server, and why.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="ArgumentException"/> because that is what these throw sites raised
/// before, and callers and tests catching that keep working.
/// </para>
/// <para>
/// <see cref="UserMessage"/> exists because <see cref="ArgumentException.Message"/> appends
/// <c>(Parameter 'localFilePath')</c>, and the coordinator puts the message it catches straight
/// into the row a person then reads on the Transfers tab. A scientist looking at a stalled
/// transfer was being shown a parameter name.
/// </para>
/// <para>
/// <see cref="Reason"/> exists because the alternative is telling the rejections apart by their
/// wording. A handler added for one of them caught all of them and replaced a message that says
/// exactly what to do — rename the file, the server truncates at the semicolon — with one
/// claiming the file was outside the monitored folder, which for that file was simply untrue.
/// </para>
/// </remarks>
public sealed class PathNotPlaceableException : ArgumentException
{
    public PathNotPlaceableException(
        PathRejectionReason reason, string userMessage, string paramName)
        : base(userMessage, paramName)
    {
        Reason = reason;
        UserMessage = userMessage;
    }

    /// <summary>Which rejection this is, without parsing the message.</summary>
    public PathRejectionReason Reason { get; }

    /// <summary>The message alone, fit to show to somebody.</summary>
    public string UserMessage { get; }
}

/// <summary>The outcome of validating one name.</summary>
/// <param name="Reason">Why the name was rejected, or <see cref="PathRejectionReason.None"/>.</param>
/// <param name="Message">An explanation written for the person who has to fix it.</param>
public readonly record struct PathValidation(PathRejectionReason Reason, string? Message)
{
    /// <summary>True when the name can be used.</summary>
    public bool IsValid => Reason == PathRejectionReason.None;

    /// <summary>A passing result.</summary>
    public static PathValidation Ok => new(PathRejectionReason.None, null);
}

/// <summary>
/// Validates names before they are used to build a remote destination.
/// </summary>
/// <remarks>
/// This runs on the name a local file <em>would be given</em> on the server, not on paths read
/// back from the server -- an existing remote name has to be representable whatever it contains.
/// </remarks>
public static class PathSafety
{
    /// <summary>
    /// Longest single segment accepted. Well under any server limit, and long enough for the
    /// most verbose instrument naming conventions.
    /// </summary>
    public const int MaxSegmentLength = 255;

    /// <summary>
    /// Characters that cannot survive a path segment. Windows cannot produce most of these in
    /// a filename, so in practice they only appear in a hand-edited destination path.
    /// </summary>
    private static readonly char[] IllegalCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Checks one path segment -- a file or folder name -- for anything that would make the
    /// remote copy wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The semicolon rule is the one that matters, and it is not theoretical. Verified against
    /// panoramaweb.org on 2026-08-19: the servlet container strips path parameters
    /// <em>after</em> percent-decoding, so a segment is silently truncated at the first
    /// semicolon.
    /// </para>
    /// <para>
    /// Uploading <c>run;rep1.raw</c> and <c>run;rep2.raw</c> stores <em>both</em> as a single
    /// object named <c>run</c>: the second overwrites the first, and both requests return
    /// 201 Created. Nothing surfaces the loss, because a later GET of the original URL is
    /// truncated the same way and succeeds. No client-side encoding avoids it -- <c>%3B</c> is
    /// decoded and then truncated, while <c>%253B</c> produces a literal, differently-wrong
    /// name. Refusing the upload and asking for a rename is the only option that cannot lose
    /// an acquisition.
    /// </para>
    /// </remarks>
    public static PathValidation ValidateSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return new PathValidation(PathRejectionReason.Empty, "The name is empty.");
        }

        if (segment is "." or "..")
        {
            return new PathValidation(
                PathRejectionReason.Traversal,
                $"'{segment}' is a relative path segment and cannot be used as a name.");
        }

        if (segment.Contains(';', StringComparison.Ordinal))
        {
            return new PathValidation(
                PathRejectionReason.SemicolonTruncatesOnServer,
                $"'{segment}' contains a semicolon. Panorama truncates the name at the first "
                + $"semicolon, so this would be stored as '{segment[..segment.IndexOf(';', StringComparison.Ordinal)]}' "
                + "and could silently overwrite another file. Rename it before uploading.");
        }

        if (segment.Length > MaxSegmentLength)
        {
            return new PathValidation(
                PathRejectionReason.TooLong,
                $"The name is {segment.Length} characters; the limit is {MaxSegmentLength}.");
        }

        var illegal = segment.IndexOfAny(IllegalCharacters);
        if (illegal >= 0)
        {
            return new PathValidation(
                PathRejectionReason.IllegalCharacter,
                $"'{segment}' contains '{segment[illegal]}', which cannot be used in a remote name.");
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c))
            {
                return new PathValidation(
                    PathRejectionReason.IllegalCharacter,
                    "The name contains a control character.");
            }
        }

        return PathValidation.Ok;
    }

    /// <summary>
    /// Validates every segment of a relative path, returning the first problem found.
    /// </summary>
    public static PathValidation ValidateRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return new PathValidation(PathRejectionReason.Empty, "The path is empty.");
        }

        foreach (var segment in segments)
        {
            var result = ValidateSegment(segment);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return PathValidation.Ok;
    }

    /// <summary>
    /// Maps a local file to its destination under <paramref name="destinationRoot"/>, preserving
    /// the directory structure below <paramref name="localBaseDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Refuses to produce a path that escapes the destination, whatever the inputs. The
    /// containment check is a belt-and-braces assertion on the result rather than trust in the
    /// validation above.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The file lies outside the base directory, or a name cannot be used remotely.
    /// </exception>
    /// <param name="localBaseDirectory">The monitored folder.</param>
    /// <param name="localFilePath">What is being transferred.</param>
    /// <param name="destinationRoot">Where the monitored folder maps to.</param>
    /// <param name="remoteName">
    /// Overrides the last segment. Used for a directory acquisition, which is a <c>.d</c> folder
    /// locally and a <c>.d.zip</c> file remotely: everything above it keeps the same shape, only
    /// the leaf changes.
    /// </param>
    public static RemotePath ResolveDestination(
        string localBaseDirectory,
        string localFilePath,
        RemotePath destinationRoot,
        string? remoteName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
        ArgumentNullException.ThrowIfNull(destinationRoot);

        var relative = Path.GetRelativePath(localBaseDirectory, localFilePath);

        if (!string.IsNullOrWhiteSpace(remoteName))
        {
            // Validated as part of the whole relative path below, so a name containing a
            // separator or anything else unusable is refused exactly as any other would be.
            var parent = Path.GetDirectoryName(relative);
            relative = string.IsNullOrEmpty(parent) ? remoteName : Path.Combine(parent, remoteName);
        }

        // Two dots followed by a separator, or nothing else at all — not merely a name that
        // begins with two dots. "..2026-Levitt-AHA" is a legal Windows folder, and treating it as
        // an escape told somebody their file was outside the folder being monitored when it was
        // sitting inside it, along with advice that could not work.
        var escapes = relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(relative, "..", StringComparison.Ordinal);

        if (Path.IsPathRooted(relative) || escapes)
        {
            throw new PathNotPlaceableException(
                PathRejectionReason.OutsideMonitoredFolder,
                $"This file is not inside the folder being monitored "
                + $"({localBaseDirectory}), so there is nowhere on the server it belongs. "
                + "Nothing has been sent and the file has not been touched.",
                nameof(localFilePath));
        }

        var validation = ValidateRelativePath(relative);
        if (!validation.IsValid)
        {
            // Message is non-null whenever Reason is not None, which is the branch this is in.
            throw new PathNotPlaceableException(
                validation.Reason,
                validation.Message ?? "This file's name cannot be used on the server.",
                nameof(localFilePath));
        }

        var root = destinationRoot.AsCollection();
        var resolved = root.Append(RemotePath.Parse(relative));

        if (!resolved.IsUnder(root))
        {
            throw new PathNotPlaceableException(
                PathRejectionReason.Traversal,
                $"This file would be placed outside the destination folder on the server "
                + $"('{resolved}'), so it has not been sent. Its name or the folders above it "
                + "would have to change.",
                nameof(localFilePath));
        }

        return resolved;
    }
}
