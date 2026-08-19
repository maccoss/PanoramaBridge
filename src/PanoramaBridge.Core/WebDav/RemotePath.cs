using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// An absolute path on a WebDAV server, stored as decoded segments.
/// </summary>
/// <remarks>
/// <para>
/// Every URL the application builds goes through this type. That is the whole point of it
/// existing. In the Python implementation four call sites each joined URLs their own way, and
/// two of them disagreed: checksums were written through one code path and read through
/// another, so on any server whose base URL carried a path segment the sidecar was written to
/// one place and read from another, and upload verification silently degraded forever without
/// a single error being logged.
/// </para>
/// <para>
/// Segments are held <em>decoded</em>. Encoding happens once, at the boundary, in
/// <see cref="ToUri"/>. A path is therefore never double-encoded, and a name containing a
/// percent sign survives a round trip.
/// </para>
/// </remarks>
public sealed class RemotePath : IEquatable<RemotePath>
{
    /// <summary>The server root.</summary>
    public static readonly RemotePath Root = new([], isCollection: true);

    private readonly string[] _segments;

    private RemotePath(string[] segments, bool isCollection)
    {
        _segments = segments;
        IsCollection = isCollection;
    }

    /// <summary>
    /// True when this path denotes a collection. Collections are rendered with a trailing
    /// slash, which some WebDAV servers require in order to resolve them at all.
    /// </summary>
    public bool IsCollection { get; }

    /// <summary>Decoded path segments, outermost first. Empty for the root.</summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>The final segment, decoded. Empty for the root.</summary>
    public string Name => _segments.Length == 0 ? string.Empty : _segments[^1];

    /// <summary>True for the server root, which has no parent.</summary>
    public bool IsRoot => _segments.Length == 0;

    /// <summary>
    /// The containing collection. The root is its own parent, so walking upwards always
    /// terminates.
    /// </summary>
    public RemotePath Parent =>
        _segments.Length == 0 ? Root : new RemotePath(_segments[..^1], isCollection: true);

    /// <summary>
    /// Parses a path written by a human or held in configuration, for example
    /// <c>/_webdav/MacCoss/maccoss/@files/</c>. Input may be percent-encoded or not; any
    /// escape sequence present is decoded, so both forms converge on the same value.
    /// </summary>
    /// <remarks>
    /// A trailing slash marks the path as a collection. Empty and <c>.</c> segments are
    /// dropped, and <c>..</c> is rejected rather than resolved -- silently collapsing it would
    /// let a crafted relative path escape the configured destination.
    /// </remarks>
    public static RemotePath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Replace('\\', '/');
        var isCollection = normalized.Length == 0 || normalized.EndsWith('/');

        var segments = new List<string>();
        foreach (var raw in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = Uri.UnescapeDataString(raw);

            if (segment is "." or "")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new ArgumentException(
                    $"Path segment '..' is not allowed in a remote path: '{path}'.",
                    nameof(path));
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? Root : new RemotePath([.. segments], isCollection);
    }

    /// <summary>
    /// Builds a path from already-decoded segments, bypassing any parsing. Use this when the
    /// segments come from somewhere structured, such as a local relative path that has already
    /// been split and validated.
    /// </summary>
    public static RemotePath FromSegments(IEnumerable<string> segments, bool isCollection = false)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var list = new List<string>();
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
            {
                continue;
            }

            if (segment == ".." || segment.Contains('/') || segment.Contains('\\'))
            {
                throw new ArgumentException(
                    $"Invalid remote path segment: '{segment}'.",
                    nameof(segments));
            }

            list.Add(segment);
        }

        return list.Count == 0 ? Root : new RemotePath([.. list], isCollection);
    }

    /// <summary>Appends one decoded segment, yielding a file path.</summary>
    public RemotePath Append(string segment, bool isCollection = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);

        if (segment is ".." or "." || segment.Contains('/') || segment.Contains('\\'))
        {
            throw new ArgumentException($"Invalid remote path segment: '{segment}'.", nameof(segment));
        }

        return new RemotePath([.. _segments, segment], isCollection);
    }

    /// <summary>Appends a relative path, preserving its collection flag.</summary>
    public RemotePath Append(RemotePath relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        return relative.IsRoot
            ? this
            : new RemotePath([.. _segments, .. relative._segments], relative.IsCollection);
    }

    /// <summary>Returns this path reinterpreted as a collection or as a file.</summary>
    public RemotePath AsCollection(bool isCollection = true) =>
        IsCollection == isCollection ? this : new RemotePath(_segments, isCollection);

    /// <summary>
    /// True when <paramref name="other"/> is this path or an ancestor of it.
    /// </summary>
    /// <remarks>
    /// Compared segment by segment rather than by string prefix, so <c>/data/run10</c> is
    /// correctly reported as <em>not</em> being under <c>/data/run1</c>. This is the guard that
    /// keeps a computed destination inside the configured upload folder.
    /// </remarks>
    public bool IsUnder(RemotePath other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other._segments.Length > _segments.Length)
        {
            return false;
        }

        for (var i = 0; i < other._segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The percent-encoded absolute path, for example <c>/_webdav/MacCoss/a%20b.raw</c>.
    /// Collections carry a trailing slash.
    /// </summary>
    public string ToEncodedString()
    {
        if (_segments.Length == 0)
        {
            return "/";
        }

        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            builder.Append('/').Append(EncodeSegment(segment));
        }

        if (IsCollection)
        {
            builder.Append('/');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Resolves this path against a server base address.
    /// </summary>
    /// <remarks>
    /// The base URL's own path is preserved, so a LabKey server deployed under a prefix such as
    /// <c>https://host/labkey</c> works. Note this is deliberately not
    /// <c>new Uri(baseUri, relative)</c>: that overload <em>discards</em> the base path whenever
    /// the relative part begins with a slash, which is exactly the trap the Python version fell
    /// into.
    /// </remarks>
    public Uri ToUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address must be an absolute URI.", nameof(baseUri));
        }

        var prefix = baseUri.GetLeftPart(UriPartial.Authority);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        return new Uri(prefix + basePath + ToEncodedString(), UriKind.Absolute);
    }

    /// <summary>
    /// Percent-encodes one path segment.
    /// </summary>
    /// <remarks>
    /// Only RFC 3986 unreserved characters and <c>@</c> are left alone; everything else is
    /// escaped. That is stricter than the grammar strictly requires -- <c>$ &amp; ' ( ) * + , ; =</c>
    /// are all legal in a path segment -- but a file-transfer tool gains nothing from emitting
    /// them raw and has a great deal to lose from a proxy or server that mishandles one.
    /// <para>
    /// <c>@</c> is the deliberate exception. LabKey's file roots are named <c>@files</c> and
    /// <c>@pipeline</c>, and the server emits them unencoded in its own hrefs; both forms
    /// resolve, and matching what the server itself produces keeps request logs readable.
    /// Note <see cref="Uri.EscapeDataString"/> would encode it, which is why the encoding is
    /// done here by hand.
    /// </para>
    /// </remarks>
    public static string EncodeSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var builder = new StringBuilder(segment.Length);
        foreach (var b in Encoding.UTF8.GetBytes(segment))
        {
            if (IsUnreserved((char)b))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char c) =>
        c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-' or '.' or '_' or '~'
            or '@';

    /// <inheritdoc />
    public override string ToString() => ToEncodedString();

    /// <summary>
    /// Ordinal comparison of the decoded segments. Case-sensitive: WebDAV paths are, even
    /// though the Windows file system the files came from is not.
    /// </summary>
    public bool Equals([NotNullWhen(true)] RemotePath? other)
    {
        if (other is null || other._segments.Length != _segments.Length || other.IsCollection != IsCollection)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RemotePath);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsCollection);
        foreach (var segment in _segments)
        {
            hash.Add(segment, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(RemotePath? left, RemotePath? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(RemotePath? left, RemotePath? right) => !(left == right);
}
