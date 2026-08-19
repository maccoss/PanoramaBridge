using System.Net;

namespace PanoramaBridge.Tests.TestDoubles;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> for unit tests.
/// </summary>
/// <remarks>
/// Deliberately tiny and dependency-free. It covers status-code matrices, headers, malformed
/// bodies and thrown transport exceptions, which is everything the client-level tests need.
/// Engine-level tests that need streaming, custom verbs or a mid-transfer abort use the
/// in-process fake WebDAV server instead.
/// </remarks>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond ?? throw new ArgumentNullException(nameof(respond));
    }

    /// <summary>Every request this handler has seen, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Always answers with the given status and body.</summary>
    public static StubHttpMessageHandler Returning(HttpStatusCode status, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        }));

    /// <summary>Always throws, simulating an offline machine or a DNS failure.</summary>
    public static StubHttpMessageHandler Throwing(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Wraps this handler in a ready-to-use client.</summary>
    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _respond(request, cancellationToken);
    }
}
