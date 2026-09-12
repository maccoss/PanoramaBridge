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
        options.ExcludedExtensions.ShouldContain(".skyd", "so does the exclusion list");
        options.AlsoWatch.ShouldBeEmpty("one folder unless another is asked for");
        options.NoUpload.ShouldBeFalse("watch transfers unless told not to");
        options.ForMinutes.ShouldBe(0, "zero means watch until interrupted");
    }

    [Fact]
    public void Several_folders_can_be_watched_at_once()
    {
        // How the cost of several configurations is measured: each folder gets its own watcher
        // and its own sweep timer, which is what the application does and what multiplies.
        var options = Parse("--also", @"E:\Data", "--also", @"F:\Data");

        options.AlsoWatch.ShouldBe([@"E:\Data", @"F:\Data"]);
    }

    [Fact]
    public void An_extra_folder_is_not_confused_with_the_remote_path()
    {
        // watch takes an optional remote directory second, so extra locals have to be named
        // rather than positional -- otherwise pbctl watch A B means two different things
        // depending on whether B happens to look like a remote path.
        var options = Parse("/_webdav/MacCoss/maccoss/@files/", "--also", @"E:\Data");

        options.Paths.ShouldBe(["/_webdav/MacCoss/maccoss/@files/"]);
        options.AlsoWatch.ShouldBe([@"E:\Data"]);
    }

    [Fact]
    public void A_folder_switch_with_no_folder_is_refused()
    {
        Reject("--also").ShouldContain("needs a directory");
    }

    [Fact]
    public void A_folder_switch_does_not_swallow_the_next_switch()
    {
        // The same trap --exclude guards. Left unchecked this watches a directory named
        // "--no-upload" and silently does not apply the switch, so the run contacts a server
        // somebody had just said not to contact.
        Reject("--also", "--no-upload").ShouldContain("--no-upload");
    }

    [Fact]
    public void Watching_several_folders_is_still_only_parsing()
    {
        // The parser accepts the pair; whether --also is allowed without --no-upload is the
        // command's decision, not this one's, because it depends on there being one destination.
        var options = Parse("--also", @"E:\Data");

        options.AlsoWatch.ShouldHaveSingleItem();
        options.NoUpload.ShouldBeFalse();
    }

    [Fact]
    public void Walking_without_uploading_is_asked_for_by_name()
    {
        Parse("--no-upload").NoUpload.ShouldBeTrue();
    }

    [Fact]
    public void A_run_can_stop_itself()
    {
        // So the idle-cost measurement is repeatable rather than something a person has to sit
        // and interrupt at the right moment.
        Parse("--for", "5").ForMinutes.ShouldBe(5);
        Reject("--for").ShouldContain("needs a number");
    }

    [Fact]
    public void Exclusions_can_be_replaced_or_turned_off_altogether()
    {
        Parse("--exclude", ".skyd,.blib").ExcludedExtensions.ShouldBe([".skyd", ".blib"]);

        // An empty argument is how watch reproduces the behavior before .skyd was excluded,
        // which is what makes the fix checkable against a real folder rather than only in tests.
        Parse("--exclude", "").ExcludedExtensions.ShouldBeEmpty();
    }

    [Fact]
    public void An_exclusion_switch_with_nothing_after_it_says_so()
    {
        Reject("--exclude").ShouldContain("--exclude");
    }

    [Theory]
    [InlineData("--exclude", "--no-verify")]
    [InlineData("--ext", "--no-verify")]
    [InlineData("--exclude", "--concurrency")]
    public void A_list_switch_will_not_swallow_the_next_option(string switchName, string next)
    {
        // There IS a next argument, so a bare arity check passes and the option is taken as the
        // value: the run would exclude nothing useful AND verify after being told not to, with
        // nothing on screen about either.
        Reject(switchName, next).ShouldContain(next);
    }

    [Fact]
    public void An_empty_list_is_still_a_value_and_not_a_mistake()
    {
        // The escape hatch has to survive the check above -- "" is not option-shaped.
        Parse("--exclude", "").ExcludedExtensions.ShouldBeEmpty();
        Parse("--ext", "").Extensions.ShouldBeEmpty();
    }

    [Fact]
    public void Whether_a_filter_was_asked_for_is_recorded()
    {
        // sync mirrors the directory whole and builds no filter, so it refuses these rather than
        // accepting them, ignoring them, and exiting zero.
        Parse().FiltersGiven.ShouldBeFalse();
        Parse("--concurrency", "4").FiltersGiven.ShouldBeFalse();
        Parse("--ext", ".raw").FiltersGiven.ShouldBeTrue();
        Parse("--exclude", ".skyd").FiltersGiven.ShouldBeTrue();

        // Including the empty form, which is a deliberate instruction and not an absence.
        Parse("--exclude", "").FiltersGiven.ShouldBeTrue();
    }

    [Fact]
    public void Every_switch_is_understood()
    {
        var options = Parse(
            "--concurrency", "6",
            "--every", "45",
            "--stable", "90",
            "--ext", ".wiff,.d",
            "--exclude", ".skyd",
            "--no-verify");

        options.Concurrency.ShouldBe(6);
        options.ReconcileMinutes.ShouldBe(45);
        options.StableSeconds.ShouldBe(90);
        options.Extensions.ShouldBe([".wiff", ".d"]);
        options.ExcludedExtensions.ShouldBe([".skyd"]);
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
    public void Extensions_are_normalized_the_way_the_settings_screen_normalizes_them()
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
