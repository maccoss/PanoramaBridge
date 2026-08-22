namespace PanoramaBridge.ThermoRaw.Tests;

/// <summary>
/// Counts what a stream is actually asked to do.
/// </summary>
/// <remarks>
/// Seeks matter as much as bytes here. Over SMB a seek can cost a round trip, and an instrument's
/// output folder is very often a share.
/// </remarks>
internal sealed class CountingStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data, writable: false);

    public int Reads { get; private set; }

    public long BytesRead { get; private set; }

    public int Seeks { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Reads++;
        var read = _inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        Reads++;
        var read = _inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override long Position
    {
        get => _inner.Position;
        set
        {
            if (value != _inner.Position)
            {
                Seeks++;
            }

            _inner.Position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Seeks++;
        return _inner.Seek(offset, origin);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override void Flush() => _inner.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// What checking a file costs.
/// </summary>
/// <remarks>
/// This runs on an instrument computer beside acquisition software, and it runs on every file the
/// monitor is waiting on, every sweep. The property that matters is not that it is fast on a small
/// file but that it costs the <em>same</em> on a large one: an acquisition can be forty gigabytes,
/// and anything proportional to that would be paid over and over.
/// </remarks>
public sealed class ValidationCostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static (ThermoRawResult Result, CountingStream Counts) Measure(byte[] bytes)
    {
        var stream = new CountingStream(bytes);
        var result = ThermoRawValidator.Validate(stream, bytes.Length, "cost.raw");
        return (result, stream);
    }

    [Fact]
    public void Checking_a_file_does_not_read_the_file()
    {
        var (result, counts) = Measure(SyntheticRawFile.Valid());

        result.Verdict.ShouldBe(ThermoRawVerdict.NoTruncationDetected);

        // The header is 1,356 bytes of it. Everything after is a handful of integers.
        counts.BytesRead.ShouldBeLessThan(4096);
    }

    [Fact]
    public void A_file_a_thousand_times_larger_costs_the_same()
    {
        // The assertion the design exists for. Reads must be bounded by the format, not the
        // acquisition: a sweep re-examines every file it is still waiting on.
        var small = new SyntheticRawFile { TrailingBytes = 4 * 1024 }.Build();
        var large = new SyntheticRawFile { TrailingBytes = 4 * 1024 * 1024 }.Build();

        large.Length.ShouldBeGreaterThan(small.Length * 100);

        var (smallResult, smallCounts) = Measure(small);
        var (largeResult, largeCounts) = Measure(large);

        smallResult.Verdict.ShouldBe(ThermoRawVerdict.NoTruncationDetected);
        largeResult.Verdict.ShouldBe(ThermoRawVerdict.NoTruncationDetected);

        largeCounts.BytesRead.ShouldBe(smallCounts.BytesRead);
        largeCounts.Reads.ShouldBe(smallCounts.Reads);
        largeCounts.Seeks.ShouldBe(smallCounts.Seeks);
    }

    [Fact]
    public void The_number_of_round_trips_is_bounded()
    {
        // Named for the network case rather than the local one. Reads and seeks against a share
        // are round trips, and a check that took hundreds of them would be noticeable on a folder
        // being swept every fifteen minutes.
        var (_, counts) = Measure(SyntheticRawFile.Valid());

        counts.Reads.ShouldBeLessThan(120);
        counts.Seeks.ShouldBeLessThan(120);
    }

    [Fact]
    public void Rejecting_something_that_is_not_a_raw_file_costs_one_read()
    {
        // The common case by volume: every non-Thermo file the monitor looks at. It must not cost
        // more than looking at the first bytes.
        var bytes = new byte[64 * 1024];
        Random.Shared.NextBytes(bytes);
        bytes[0] = 0;
        bytes[1] = 0;

        var (result, counts) = Measure(bytes);

        result.Verdict.ShouldBe(ThermoRawVerdict.NotThermoRaw);
        counts.BytesRead.ShouldBe(ThermoRawHeader.Size);
        counts.Reads.ShouldBe(1);
    }

    /// <summary>Records what it actually costs, so the number is in the record rather than in a
    /// commit message.</summary>
    [Fact]
    public void What_a_check_costs_is_recorded()
    {
        var bytes = new SyntheticRawFile { TrailingBytes = 8 * 1024 * 1024 }.Build();

        var stream = new CountingStream(bytes);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = ThermoRawValidator.Validate(stream, bytes.Length, "cost.raw");
        started.Stop();

        output.WriteLine($"file size     {bytes.Length:N0} bytes");
        output.WriteLine($"bytes read    {stream.BytesRead:N0}");
        output.WriteLine($"read calls    {stream.Reads}");
        output.WriteLine($"seeks         {stream.Seeks}");
        output.WriteLine($"elapsed       {started.Elapsed.TotalMilliseconds:F3} ms");
        output.WriteLine($"verdict       {result.Verdict}");

        result.Verdict.ShouldBe(ThermoRawVerdict.NoTruncationDetected);
    }
}
