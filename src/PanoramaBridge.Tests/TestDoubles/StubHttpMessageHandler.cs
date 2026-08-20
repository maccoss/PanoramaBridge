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

    /// <summary>Total request-body bytes this handler has consumed.</summary>
    public long BytesReceived { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        // Drain the request body before answering. A real transport serializes the content,
        // and skipping that here would leave any streaming or hashing wrapper in the request
        // pipeline untouched -- an upload test would then "pass" while having sent nothing and
        // hashed nothing.
        if (request.Content is not null)
        {
            var counter = new CountingStream();
            await request.Content.CopyToAsync(counter, cancellationToken).ConfigureAwait(false);
            BytesReceived += counter.BytesWritten;
        }

        return await _respond(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Discards what it is given but remembers how much there was.</summary>
    private sealed class CountingStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;

        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            BytesWritten += count;
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
