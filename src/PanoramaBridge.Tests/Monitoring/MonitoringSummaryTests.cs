using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// What the status line says when several folders have different news.
/// </summary>
/// <remarks>
/// The line is the only thing an unattended instrument computer shows about whether transfers are
/// happening. With one configuration it could simply report the last sweep; with several, the
/// last sweep is whichever happened to finish most recently, and reporting that means a folder
/// nobody can read is announced and then cleared a second later by a folder that is fine.
/// </remarks>
public sealed class MonitoringSummaryTests
{
    private static SweepResult Fine(int examined, int offered = 0) =>
        new(examined, offered, examined - offered, TimeSpan.FromSeconds(1));

    private static SweepResult Broken(string problem) =>
        new(0, 0, 0, TimeSpan.Zero, problem);

    [Fact]
    public void One_configuration_reads_exactly_as_it_always_has()
    {
        // No prefix and no count. Nothing about the single-configuration case changed, and the
        // line somebody has been reading for a year should not start growing decoration.
        MonitoringSummary
            .Describe(new Dictionary<string, SweepResult> { ["QE-HF"] = Fine(12) })
            .Line
            .ShouldBe("Monitoring - 12 file(s) checked, all up to date.");

        MonitoringSummary
            .Describe(new Dictionary<string, SweepResult> { ["QE-HF"] = Fine(12, offered: 3) })
            .Line
            .ShouldBe("Monitoring - 3 file(s) to transfer.");
    }

    [Fact]
    public void A_single_broken_configuration_is_not_named_when_it_is_the_only_one()
    {
        var summary = MonitoringSummary.Describe(new Dictionary<string, SweepResult>
        {
            ["QE-HF"] = Broken("The monitored directory does not exist: D:\\Data"),
        });

        summary.Line.ShouldBe("The monitored directory does not exist: D:\\Data");
        summary.Failed.ShouldBeTrue();
    }

    [Fact]
    public void Several_healthy_configurations_are_counted_together()
    {
        var summary = MonitoringSummary.Describe(new Dictionary<string, SweepResult>
        {
            ["Lumos"] = Fine(10, offered: 2),
            ["Exploris"] = Fine(5, offered: 1),
            ["QE-HF"] = Fine(7),
        });

        summary.Line.ShouldBe("Monitoring - 3 file(s) to transfer across 3 configurations.");
        summary.Failed.ShouldBeFalse();
    }

    [Fact]
    public void A_broken_configuration_is_named_and_survives_a_healthy_one_reporting_after_it()
    {
        // The bug this exists to prevent. Exploris cannot be read; Lumos sweeps a moment later
        // and is fine. Reporting the most recent sweep would clear the failure off the window and
        // the share would stay broken with nothing on screen to say so.
        var sweeps = new Dictionary<string, SweepResult>
        {
            ["Exploris"] = Broken("The folder could not be read"),
        };

        MonitoringSummary.Describe(sweeps).Failed.ShouldBeTrue();

        sweeps["Lumos"] = Fine(40);

        var summary = MonitoringSummary.Describe(sweeps);

        summary.Failed.ShouldBeTrue("a healthy folder does not make a broken one healthy");
        summary.Line.ShouldBe("Exploris: The folder could not be read");
    }

    [Fact]
    public void More_than_one_broken_configuration_says_how_many()
    {
        // Naming one and silently dropping the rest would have somebody fix that folder, see the
        // line change to the next failure, and reasonably conclude the fix caused it.
        var summary = MonitoringSummary.Describe(new Dictionary<string, SweepResult>
        {
            ["Lumos"] = Fine(3),
            ["Exploris"] = Broken("The folder could not be read"),
            ["QE-HF"] = Broken("Access to the path is denied"),
        });

        summary.Failed.ShouldBeTrue();
        summary.Line.ShouldContain("2 configurations cannot be checked");
    }

    [Fact]
    public void The_named_failure_does_not_change_with_the_order_they_reported_in()
    {
        // Two dictionaries holding the same answers in a different order have to produce the same
        // line, or the window rotates between failures on every sweep and reads as flapping.
        var first = MonitoringSummary.Describe(new Dictionary<string, SweepResult>
        {
            ["Exploris"] = Broken("cannot be read"),
            ["Astral"] = Broken("cannot be read"),
        });

        var second = MonitoringSummary.Describe(new Dictionary<string, SweepResult>
        {
            ["Astral"] = Broken("cannot be read"),
            ["Exploris"] = Broken("cannot be read"),
        });

        first.Line.ShouldBe(second.Line);
        first.Line.ShouldStartWith("Astral: ");
    }

    [Fact]
    public void A_failure_with_nothing_to_say_still_says_something()
    {
        // SweepResult.Failed is Problem being non-null, so this cannot happen from the scanner --
        // but the line is what an instrument computer shows all day, and an empty one would read
        // as the application having lost its place.
        var summary = MonitoringSummary.Describe(new Dictionary<string, SweepResult>
        {
            ["Lumos"] = new(0, 0, 0, TimeSpan.Zero, string.Empty),
        });

        summary.Line.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Nothing_swept_yet_is_not_a_failure()
    {
        // The gap between pressing Start monitoring and the first sweep finishing.
        var summary = MonitoringSummary.Describe(new Dictionary<string, SweepResult>());

        summary.Failed.ShouldBeFalse();
        summary.Line.ShouldNotBeNullOrWhiteSpace();
    }
}
