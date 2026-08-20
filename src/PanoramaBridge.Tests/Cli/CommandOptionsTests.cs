using PanoramaBridge.Cli;

namespace PanoramaBridge.Tests.Cli;

/// <summary>
/// The switches <c>pbctl sync</c> and <c>pbctl watch</c> accept.
/// </summary>
/// <remarks>
/// Argument parsing is the part of a command-line tool that fails in front of the user, and it
/// fails quietly: a mistyped switch that is silently treated as a path means the tool runs
/// happily and does the wrong thing. So the tests are as much about what is rejected as about
/// what is understood.
/// </remarks>
public sealed class CommandOptionsTests
{
    private static CommandOptions Parse(params string[] args)
    {
        CommandOptions.TryParse(args, out var options, out var problem).ShouldBeTrue(problem);
        return options;
    }

    private static string Reject(params string[] args)
    {
        CommandOptions.TryParse(args, out _, out var problem).ShouldBeFalse();
        return problem.ShouldNotBeNull();
    }

    [Fact]
    public void Nothing_given_means_the_documented_defaults()
    {
        var options = Parse();

        options.Concurrency.ShouldBe(3);
        options.Verify.ShouldBeTrue();
        options.ReconcileMinutes.ShouldBe(15);
        options.StableSeconds.ShouldBe(10);
        options.Paths.ShouldBeEmpty();
        options.Extensions.ShouldContain(".raw", "the defaults are the ones the settings screen uses");
    }

    [Fact]
    public void Every_switch_is_understood()
    {
        var options = Parse(
            "--concurrency", "6",
            "--every", "45",
            "--stable", "90",
            "--ext", ".wiff,.d",
            "--no-verify");

        options.Concurrency.ShouldBe(6);
        options.ReconcileMinutes.ShouldBe(45);
        options.StableSeconds.ShouldBe(90);
        options.Extensions.ShouldBe([".wiff", ".d"]);
        options.Verify.ShouldBeFalse();
    }

    [Fact]
    public void Anything_that_is_not_a_switch_is_a_path_whatever_order_it_arrives_in()
    {
        // The remote path can come before or after the switches, and taking a switch's value as
        // a path is exactly the mistake this ordering has to avoid.
        var options = Parse("--concurrency", "2", "/_webdav/MacCoss/@files/", "--no-verify");

        options.Paths.ShouldBe(["/_webdav/MacCoss/@files/"]);
        options.Concurrency.ShouldBe(2);
        options.Verify.ShouldBeFalse();
    }

    [Fact]
    public void Paths_keep_the_order_they_were_given_in()
    {
        Parse("first", "second").Paths.ShouldBe(["first", "second"]);
    }

    [Theory]
    [InlineData("--concurrency")]
    [InlineData("--every")]
    [InlineData("--stable")]
    [InlineData("--ext")]
    public void A_switch_missing_its_value_is_reported(string option)
    {
        // Previously a trailing switch with no value was silently ignored, because the parser
        // only matched it "when i + 1 < length". The user got the default and no explanation.
        Reject(option).ShouldContain(option);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("-1")]
    [InlineData("3.5")]
    public void A_number_that_is_not_one_is_reported_rather_than_thrown(string value)
    {
        // int.Parse would surface as an unhandled FormatException, which names neither the
        // switch nor the value.
        var problem = Reject("--concurrency", value);

        problem.ShouldContain("--concurrency");
        problem.ShouldContain(value);
    }

    [Fact]
    public void An_unknown_switch_is_refused_rather_than_taken_for_a_path()
    {
        // The dangerous case: --no-verfiy accepted as a remote path would upload everything to a
        // folder of that name and report success.
        Reject("--no-verfiy").ShouldContain("--no-verfiy");
    }

    [Fact]
    public void Extensions_are_normalised_the_way_the_settings_screen_normalises_them()
    {
        Parse("--ext", "RAW; mzML  .d").Extensions.ShouldBe([".raw", ".mzml", ".d"]);
    }

    [Fact]
    public void Zero_is_allowed_because_it_means_something_for_some_switches()
    {
        // --stable 0 is a legitimate "do not wait", used when a folder is known to be quiet.
        Parse("--stable", "0").StableSeconds.ShouldBe(0);
    }
}
