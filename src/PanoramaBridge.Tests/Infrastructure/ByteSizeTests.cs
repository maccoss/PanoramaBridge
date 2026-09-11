using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Tests.Infrastructure;

/// <summary>
/// How a byte count is written for somebody watching a transfer.
/// </summary>
/// <remarks>
/// There were three copies of this loop before it was one. The tests matter less for the
/// arithmetic, which is obvious, than for pinning the boundaries: the copies agreed only because
/// nobody had changed one of them yet.
/// </remarks>
public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(7_323_298_011, "6.8 GB")]
    public void Sizes_are_written_the_way_a_person_reads_them(long bytes, string expected) =>
        ByteSize.Describe(bytes).ShouldBe(expected);

    [Fact]
    public void Bytes_are_whole_and_everything_above_carries_one_decimal()
    {
        // The decimal is what makes a growing file legible. Without it a large acquisition reads
        // identically before and after a write, which is the opposite of what progress is for.
        ByteSize.Describe(1023L).ShouldBe("1023 B");
        ByteSize.Describe(1024L).ShouldBe("1.0 KB");
        ByteSize.Describe(1024L * 1024 * 1024 * 1024).ShouldBe("1.0 TB");
    }

    [Fact]
    public void The_largest_unit_keeps_counting_rather_than_running_out()
    {
        // Nothing here produces a petabyte, but a unit table that silently wraps would report a
        // wrong number rather than a big one.
        ByteSize.Describe(1024L * 1024 * 1024 * 1024 * 1024).ShouldBe("1024.0 TB");
    }

    [Fact]
    public void A_difference_can_be_negative_and_says_so()
    {
        // Sizes are never negative; differences between them are. A caller describing one should
        // not have to work out the direction before it can call this.
        ByteSize.Describe(-2048L).ShouldBe("-2.0 KB");
        ByteSize.Describe(-512L).ShouldBe("-512 B");
    }
}
