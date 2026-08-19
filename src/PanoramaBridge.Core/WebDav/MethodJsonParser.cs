using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// Reads the response of LabKey's <c>?method=json</c> directory listing.
/// </summary>
/// <remarks>
/// Preferred over PROPFIND for every listing: it is a smaller payload, needs no XML namespace
/// handling, and carries the per-resource permission flags and allowed-verb string that PROPFIND
/// does not. Those are what let the remote folder browser grey out a folder the user cannot
/// upload to, instead of letting them choose it and discover a 403 hours later.
/// </remarks>
public static class MethodJsonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Parses a listing body into resources.
    /// </summary>
    /// <param name="responseBody">The raw JSON.</param>
    /// <param name="parent">
    /// The collection that was listed, used to build each entry's full path. The server's own
    /// <c>href</c> is deliberately not trusted for this: it arrives percent-encoded and in a
    /// form that varies, and re-deriving the path keeps every path in the application flowing
    /// through one construction route.
    /// </param>
    /// <exception cref="FormatException">The body was not a listing.</exception>
    public static IReadOnlyList<WebDavResource> Parse(string responseBody, RemotePath parent)
    {
        ArgumentNullException.ThrowIfNull(responseBody);
        ArgumentNullException.ThrowIfNull(parent);

        ListingDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ListingDocument>(responseBody, Options);
        }
        catch (JsonException ex)
        {
            // A LabKey session timeout answers with an HTML login page rather than JSON.
            throw new FormatException(
                "The server did not return a JSON directory listing. This usually means the "
                + "request was not authenticated.",
                ex);
        }

        if (document?.Files is null)
        {
            throw new FormatException("The directory listing contained no 'files' array.");
        }

        var collection = parent.AsCollection();
        var resources = new List<WebDavResource>(document.Files.Count);

        foreach (var entry in document.Files)
        {
            if (string.IsNullOrEmpty(entry.Text))
            {
                continue;
            }

            var isCollection = entry.Collection ?? !(entry.Leaf ?? true);

            resources.Add(new WebDavResource(
                Name: entry.Text,
                Path: collection.Append(entry.Text, isCollection),
                IsCollection: isCollection,
                Length: entry.ContentLength ?? entry.Size ?? 0,
                LastModifiedUtc: ParseTimestamp(entry.LastModified) ?? ParseTimestamp(entry.CreationDate),
                ETag: entry.ETag,
                ContentType: entry.ContentType,
                CreatedBy: entry.CreatedBy,
                Permissions: new ResourcePermissions(
                    CanRead: entry.CanRead ?? false,
                    CanUpload: entry.CanUpload ?? false,
                    CanEdit: entry.CanEdit ?? false,
                    CanDelete: entry.CanDelete ?? false,
                    CanRename: entry.CanRename ?? false,
                    AllowedMethods: SplitMethods(entry.Options))));
        }

        return resources;
    }

    /// <summary>
    /// Splits the server's <c>options</c> string, for example
    /// <c>"OPTIONS, GET, HEAD, COPY, DELETE, MOVE, LOCK, UNLOCK, PROPFIND, POST, PUT, MKCOL"</c>.
    /// </summary>
    internal static IReadOnlyList<string> SplitMethods(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return [];
        }

        return options
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant())
            .ToArray();
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Observed form is "2013-02-01T05:07:39-00:00", which round-trips as ISO 8601.
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed class ListingDocument
    {
        [JsonPropertyName("files")]
        public List<ListingEntry>? Files { get; set; }
    }

    private sealed class ListingEntry
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("collection")]
        public bool? Collection { get; set; }

        [JsonPropertyName("leaf")]
        public bool? Leaf { get; set; }

        [JsonPropertyName("contentlength")]
        public long? ContentLength { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("contenttype")]
        public string? ContentType { get; set; }

        [JsonPropertyName("etag")]
        public string? ETag { get; set; }

        [JsonPropertyName("lastmodified")]
        public string? LastModified { get; set; }

        [JsonPropertyName("creationdate")]
        public string? CreationDate { get; set; }

        [JsonPropertyName("createdby")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("options")]
        public string? Options { get; set; }

        [JsonPropertyName("canRead")]
        public bool? CanRead { get; set; }

        [JsonPropertyName("canUpload")]
        public bool? CanUpload { get; set; }

        [JsonPropertyName("canEdit")]
        public bool? CanEdit { get; set; }

        [JsonPropertyName("canDelete")]
        public bool? CanDelete { get; set; }

        [JsonPropertyName("canRename")]
        public bool? CanRename { get; set; }
    }
}
