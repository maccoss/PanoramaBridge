using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Which files the monitor considers data.
/// </summary>
/// <remarks>
/// One filter serves both the sweep and the change watcher. If they could disagree, a file would
/// arrive or not depending on whether Windows happened to deliver a notification -- a difference
/// nobody could reproduce from a bug report.
/// </remarks>
public sealed class CandidateFilterTests
{
    private static readonly CandidateFilter Instrument = new([".raw", ".wiff"]);

    [Theory]
    [InlineData(@"C:\data\run1.raw")]
    [InlineData(@"C:\data\RUN1.RAW")]
    [InlineData(@"C:\data\sample.wiff")]
    public void Files_with_a_listed_extension_are_accepted(string path) =>
        Instrument.Accepts(path).ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\data\notes.txt")]
    [InlineData(@"C:\data\archive.notraw")]
    public void Anything_else_is_left_alone(string path) =>
        Instrument.Accepts(path).ShouldBeFalse();

    [Fact]
    public void A_suffix_match_is_not_good_enough()
    {
        // Path.GetExtension rather than EndsWith, so a filter of .raw does not also drag in a
        // file someone named archive.notraw.
        Instrument.Accepts(@"C:\data\archive.notraw").ShouldBeFalse();
        Instrument.Accepts(@"C:\data\archive.not.raw").ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\data\.hidden.raw")]
    [InlineData(@"C:\data\~partial.raw")]
    public void Working_files_are_never_data(string path)
    {
        // Instrument software and Windows both leave these behind, and a copy in progress is
        // frequently one of them. Uploading a half-written working file is exactly what this
        // application must not do.
        Instrument.Accepts(path).ShouldBeFalse();
        CandidateFilter.Everything.Accepts(path).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_extension_list_means_everything()
    {
        // What the settings screen means by leaving the box empty.
        CandidateFilter.Everything.Accepts(@"C:\data\notes.txt").ShouldBeTrue();
        CandidateFilter.Everything.Accepts(@"C:\data\run1.raw").ShouldBeTrue();
    }
}
