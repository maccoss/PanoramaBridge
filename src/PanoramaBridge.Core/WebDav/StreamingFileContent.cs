using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace PanoramaBridge.Core.WebDav;

/// <summary>
/// Request body that streams a file from disk, reporting progress as it goes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryComputeLength"/> returns the real length, so the request carries a
/// <c>Content-Length</c> rather than being chunked. That matters: it lets the server reject an
/// oversized or unauthorized upload up front, and it is what makes
/// <c>Expect: 100-continue</c> useful.
/// </para>
/// <para>
/// Progress is reported as bytes are handed to the socket. That is not the same as bytes
/// acknowledged by the server -- <c>WriteAsync</c> completes once the data reaches the kernel
/// and TLS send buffers, so the count runs ahead by a few hundred kilobytes. On a multi-gigabyte
/// acquisition that is under 0.05%, and it is far closer to the truth than the Python version's
/// progress, which counted bytes read <em>from disk</em> into a buffering HTTP library. The
/// remaining gap is covered honestly by showing a distinct "Verifying" phase between the last
/// byte written and the server's response, rather than by clamping the bar at 99% in three
/// separate places as the old code did.
/// </para>
/// </remarks>
public sealed class StreamingFileContent : HttpContent
{
    /// <summary>
    /// Copy buffer size. Large enough to keep a fast disk and a fat network pipe busy without
    /// making progress reporting coarse.
    /// </summary>
    public const int BufferSize = 1024 * 1024;

    private readonly Stream _source;
    private readonly long _length;
    private readonly Action<long>? _onBytesWritten;

    public StreamingFileContent(
        Stream source,
        long length,
        Action<long>? onBytesWritten = null,
        string contentType = "application/octet-stream")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _source = source;
        _length = length;
        _onBytesWritten = onBytesWritten;

        Headers.ContentType = new MediaTypeHeaderValue(contentType);
        Headers.ContentLength = length;
    }

    /// <summary>Bytes handed to the socket so far.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>
    /// When the last byte was written. The upload watchdog uses this to distinguish a slow
    /// transfer, which is fine, from a stalled one, which is not.
    /// </summary>
    public DateTimeOffset LastProgressUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await _source
                    .ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await stream
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                BytesWritten += read;
                LastProgressUtc = DateTimeOffset.UtcNow;
                _onBytesWritten?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
