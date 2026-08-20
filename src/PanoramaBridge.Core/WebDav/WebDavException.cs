using System.Net;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// A WebDAV request that failed in a way worth reporting to the user.
/// </summary>
/// <remarks>
/// Carries the method, path, status and the server's own response body. The Python version
/// returned a bare <c>bool</c> from its directory-creation call, which left the remote browser
/// dialog with no way to explain a failure -- it resorted to reading the application's own log
/// file back off disk and string-matching for "Permission denied". A typed failure removes the
/// need for that entirely.
/// </remarks>
public sealed class WebDavException : Exception
{
    public WebDavException(
        string method,
        RemotePath path,
        HttpStatusCode statusCode,
        string? reasonPhrase = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(BuildMessage(method, path, statusCode, reasonPhrase), innerException)
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        ResponseBody = responseBody;
    }

    /// <summary>The HTTP method that failed.</summary>
    public string Method { get; }

    /// <summary>The resource it was aimed at.</summary>
    public RemotePath Path { get; }

    /// <summary>The status the server returned.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The server's reason phrase, when it sent one.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>The response body, truncated. Useful when LabKey returns an HTML error page.</summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// True when retrying the identical request could plausibly succeed.
    /// </summary>
    public bool IsTransient => StatusCode is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// A sentence suitable for showing in the transfer table, rather than a status code.
    /// </summary>
    public string ToUserMessage() => StatusCode switch
    {
        HttpStatusCode.Unauthorized =>
            "Panorama rejected the credentials. Check the API key or password on the Remote Settings tab.",
        HttpStatusCode.Forbidden =>
            $"No permission to {Method.ToLowerInvariant()} '{Path.Name}'. A Panorama administrator "
            + "needs to grant write access to this folder.",
        HttpStatusCode.NotFound =>
            $"'{Path.Name}' was not found on the server.",
        HttpStatusCode.Conflict =>
            $"The parent folder of '{Path.Name}' does not exist on the server.",
        HttpStatusCode.RequestEntityTooLarge =>
            $"The server refused '{Path.Name}' as too large.",
        HttpStatusCode.InsufficientStorage =>
            "The server is out of storage space.",
        _ when IsTransient =>
            $"Panorama is temporarily unavailable ({(int)StatusCode}). This will be retried.",
        _ =>
            $"Panorama returned {(int)StatusCode} {ReasonPhrase} for {Method} '{Path.Name}'.",
    };

    private static string BuildMessage(
        string method,
        RemotePath path,
        HttpStatusCode statusCode,
        string? reasonPhrase) =>
        $"{method} {path} failed: {(int)statusCode} {reasonPhrase}".TrimEnd();
}
