using PanoramaBridge.Cli;

namespace PanoramaBridge.Tests.Cli;

/// <summary>
/// The harness's path resolution and its formatting.
/// </summary>
/// <remarks>
/// <see cref="Program.Target"/> reads an environment variable, so these tests set and restore it
/// rather than assuming anything about the machine. They run in one class so xUnit does not
/// parallelise them against each other over the same variable.
/// </remarks>
[Collection(nameof(ProgramTests))]
public sealed class ProgramTests : IDisposable
{
    private const string PathVariable = "PANORAMABRIDGE_IT_PATH";

    private readonly string? _original = Environment.GetEnvironmentVariable(PathVariable);

    private static void SetPath(string? value) =>
        Environment.SetEnvironmentVariable(PathVariable, value);

    [Fact]
    public void An_explicit_path_wins()
    {
        SetPath("/_webdav/MacCoss/maccoss/@files/from-the-environment/");

        Program.Target(["/_webdav/MacCoss/maccoss/@files/explicit/"], 0)
            .ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/explicit/");
    }

    [Fact]
    public void The_environment_supplies_a_default()
    {
        // What makes the documented workflow work: export the path once, then run several
        // commands without repeating it.
        SetPath("/_webdav/MacCoss/maccoss/@files/scratch/");

        Program.Target([], 0)
            .ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/scratch/");
    }

    [Fact]
    public void A_blank_argument_falls_through_to_the_environment()
    {
        SetPath("/_webdav/MacCoss/maccoss/@files/scratch/");

        Program.Target(["   "], 0)
            .ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/scratch/");
    }

    [Fact]
    public void With_neither_it_says_which_variable_to_set()
    {
        SetPath(null);

        Should.Throw<InvalidOperationException>(() => Program.Target([], 0))
            .Message
            .ShouldContain(PathVariable);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(7_323_298_011, "6.8 GB")]
    public void Sizes_are_reported_in_units_a_person_reads(long bytes, string expected) =>
        Program.FormatBytes(bytes).ShouldBe(expected);

    /// <inheritdoc />
    public void Dispose() => SetPath(_original);
}
