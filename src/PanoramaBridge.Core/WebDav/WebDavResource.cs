namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// One entry from a directory listing.
/// </summary>
/// <remarks>
/// Populated from LabKey's <c>?method=json</c>, which carries more than PROPFIND does: the
/// per-resource permission flags let the remote browser tell someone a folder is read-only
/// <em>before</em> they start a six-hour transfer into it, rather than surfacing it as a 403
/// at the end.
/// </remarks>
/// <param name="Name">The decoded entry name, as the server reports it.</param>
/// <param name="Path">Full path to the entry.</param>
/// <param name="IsCollection">True for a folder.</param>
/// <param name="Length">Size in bytes; zero for a collection.</param>
/// <param name="LastModifiedUtc">Server-side modification time, when reported.</param>
/// <param name="ETag">Opaque server tag; recorded but not used for verification.</param>
/// <param name="ContentType">MIME type the server assigned.</param>
/// <param name="CreatedBy">Display name of whoever put it there.</param>
/// <param name="Permissions">What the authenticated user may do with it.</param>
public sealed record WebDavResource(
    string Name,
    RemotePath Path,
    bool IsCollection,
    long Length,
    DateTimeOffset? LastModifiedUtc,
    string? ETag,
    string? ContentType,
    string? CreatedBy,
    ResourcePermissions Permissions);

/// <summary>
/// What the authenticated user may do with a resource, as reported by the server.
/// </summary>
/// <param name="CanRead">Read the contents.</param>
/// <param name="CanUpload">Create new files inside it. Meaningful for collections.</param>
/// <param name="CanEdit">Replace existing content.</param>
/// <param name="CanDelete">Remove it.</param>
/// <param name="CanRename">Rename or move it. Gates the temp-name-then-MOVE publish strategy.</param>
/// <param name="AllowedMethods">HTTP verbs the server says it will accept here.</param>
public readonly record struct ResourcePermissions(
    bool CanRead,
    bool CanUpload,
    bool CanEdit,
    bool CanDelete,
    bool CanRename,
    IReadOnlyList<string> AllowedMethods)
{
    /// <summary>Nothing known. Used when a listing omits the flags.</summary>
    public static ResourcePermissions Unknown { get; } =
        new(false, false, false, false, false, []);

    /// <summary>True when the server will accept the named verb here.</summary>
    public bool Allows(string method) =>
        AllowedMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a file can be uploaded here and then renamed into place, which is what the
    /// atomic publish strategy needs.
    /// </summary>
    public bool SupportsAtomicPublish => CanUpload && Allows("MOVE");
}
