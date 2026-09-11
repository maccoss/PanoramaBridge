using System.Globalization;
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

    [Theory]
    [InlineData(1_048_575, "1.0 MB")]
    [InlineData(1_073_741_823, "1.0 GB")]
    [InlineData(1_099_511_627_775, "1.0 TB")]
    public void A_value_just_under_a_boundary_steps_up_rather_than_printing_1024(
        long bytes, string expected)
    {
        // 1,048,575 bytes is 1023.999 KB, which prints as "1024.0 KB" if the loop compares the
        // value it holds instead of the one it is about to show. No unit scale should ever
        // display 1024 of anything. The original theory stepped 1536 -> 1048576, straight over
        // every boundary, so it passed with this wrong.
        ByteSize.Describe(bytes).ShouldBe(expected);
    }

    [Fact]
    public void A_rate_just_under_a_kilobyte_does_not_print_1024_bytes()
    {
        // The same defect one unit down, reachable only from the double overload: a throughput
        // of 1023.6 B/s rounds to "1024 B" at whole-byte precision.
        ByteSize.Describe(1023.6).ShouldBe("1.0 KB");
    }

    [Fact]
    public void Output_does_not_depend_on_the_machines_locale()
    {
        // Every string around these numbers is English and none of it is localized, so a comma
        // decimal would land inside an English sentence. It also keeps the window, the console
        // and the log showing one figure, which matters when a line is pasted into a support
        // request -- and stops this suite passing on CI while failing in Munich.
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            ByteSize.Describe(1536L).ShouldBe("1.5 KB");
            ByteSize.Describe(7_323_298_011).ShouldBe("6.8 GB");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_rate_that_was_never_measured_reads_as_nothing_rather_than_NaN()
    {
        // Both rate sources guard their division, so this is defensive. It matters because the
        // double overload is now the single entry point for every byte display: a future caller
        // dividing by an unguarded elapsed time would put "NaN B/s" on screen, where nothing
        // fails and nobody reports it.
        ByteSize.Describe(double.NaN).ShouldBe("0 B");
        ByteSize.Describe(double.PositiveInfinity).ShouldBe("0 B");
        ByteSize.Describe(double.NegativeInfinity).ShouldBe("0 B");
    }

    [Fact]
    public void A_difference_can_be_negative_and_says_so()
    {
        // Sizes are never negative; differences between them are. A caller describing one should
        // not have to work out the direction before it can call this.
        ByteSize.Describe(-2048L).ShouldBe("-2.0 KB");
        ByteSize.Describe(-512L).ShouldBe("-512 B");

        // But not a signed zero: "-0 B" says less than "0 B" does.
        ByteSize.Describe(-0.4).ShouldBe("0 B");
    }
}
