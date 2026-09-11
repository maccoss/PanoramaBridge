using PanoramaBridge.Core.Infrastructure;

namespace PanoramaBridge.Tests.Infrastructure;

/// <summary>
/// How a remaining time is written, and that every surface writes it the same way.
/// </summary>
/// <remarks>
/// There were three copies, and the third had drifted: a per-file row said "3m 20s left" while
/// the totals line above it said "3m" about the same transfer, at the same moment, in the same
/// window. That is the failure these guard -- not the arithmetic.
/// </remarks>
public sealed class DurationTests
{
    [Theory]
    [InlineData(0, 0, 45, "45s")]
    [InlineData(0, 3, 20, "3m 20s")]
    [InlineData(0, 3, 0, "3m 0s")]
    [InlineData(2, 15, 0, "2h 15m")]
    [InlineData(26, 4, 0, "26h 4m")]
    public void A_remaining_time_reads_in_two_units_where_it_has_them(
        int hours, int minutes, int seconds, string expected) =>
        Duration.Describe(new TimeSpan(hours, minutes, seconds)).ShouldBe(expected);

    [Fact]
    public void An_almost_finished_transfer_never_says_zero()
    {
        // A remaining time that has rounded to nothing is still a transfer that has not
        // finished, and "0s" invites the reader to wonder why it is still going.
        Duration.Describe(TimeSpan.Zero).ShouldBe("1s");
        Duration.Describe(TimeSpan.FromMilliseconds(200)).ShouldBe("1s");
    }
}
