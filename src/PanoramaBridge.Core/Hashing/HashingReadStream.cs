using System.Security.Cryptography;

namespace PanoramaBridge.Core.Hashing;

/// <summary>The hashes computed over a file's bytes.</summary>
/// <param name="Md5">Lower-case hex MD5. Compared against the server's own hash.</param>
/// <param name="Sha256">
/// Lower-case hex SHA-256, or null when it was not requested. Optional because MD5 is the hash
/// the server reports, so SHA-256 is a provenance record rather than something anything verifies.
/// </param>
public readonly record struct ContentHashes(string Md5, string? Sha256);

/// <summary>
/// A read-only pass-through stream that hashes every byte that flows through it.
/// </summary>
/// <remarks>
/// <para>
/// This exists so a file is read from disk exactly once. The Python version read every file
/// twice -- once to compute a SHA-256 and again to stream it into the request -- which on a
/// 7 GB acquisition wastes minutes of disk time per file and leaves the network idle while it
/// happens. Wrapping the upload stream folds the hash into the transfer for free.
/// </para>
/// <para>
/// MD5 is always computed, because it is what Panorama reports back and therefore the only hash
/// that can be compared against the bytes the server actually stored. SHA-256 is optional and off
/// by default: it is a provenance record rather than something anything verifies, and on an
/// instrument computer a second digest per transfer costs processor time that the acquisition
/// wants. Neither is a security boundary here -- they detect corruption, not tampering.
/// </para>
/// </remarks>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly IncrementalHash? _sha256;

    private ContentHashes? _finished;

    /// <param name="inner">The stream to read through.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="inner"/> open on dispose.</param>
    /// <param name="alsoSha256">
    /// Whether to compute SHA-256 as well. Off by default: MD5 is the hash Panorama reports, so
    /// it is the only one that can be compared against what the server actually stored, and
    /// computing a second digest doubles the processor cost of every transfer. On an instrument
    /// computer that cost competes with the acquisition, which is a poor trade for a value
    /// nothing checks. Turn it on where a stronger provenance record is wanted.
    /// </param>
    public HashingReadStream(Stream inner, bool leaveOpen = false, bool alsoSha256 = false)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (!inner.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(inner));
        }

        _inner = inner;
        _leaveOpen = leaveOpen;

        if (alsoSha256)
        {
            _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }
    }

    /// <summary>Bytes read so far.</summary>
    public long BytesRead { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Finalizes and returns the hashes.
    /// </summary>
    /// <remarks>
    /// Call once, after the stream has been read to the end. The result is cached, so asking
    /// twice is harmless, but hashing cannot continue afterwards.
    /// </remarks>
    public ContentHashes GetHashes()
    {
        return _finished ??= new ContentHashes(
            Convert.ToHexString(_md5.GetHashAndReset()).ToLowerInvariant(),
            _sha256 is null
                ? null
                : Convert.ToHexString(_sha256.GetHashAndReset()).ToLowerInvariant());
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Absorb(buffer[..read]);
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Absorb(buffer.Span[..read]);
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Absorb(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        if (_finished is not null)
        {
            throw new InvalidOperationException(
                "The hashes have already been finalized; this stream cannot be read further.");
        }

        _md5.AppendData(chunk);
        _sha256?.AppendData(chunk);
        BytesRead += chunk.Length;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _md5.Dispose();
            _sha256?.Dispose();

            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
